using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LiteBro;

/// <summary>
/// «С телефона» on the start page: QR codes of the projects' sites at this computer's address in the home network,
/// and of the SSH terminal for LiteBro on iPhone. OpenSSH Server of Windows is switched on and off here,
/// with the administrator's prompt; its port is opened for private networks only.
/// </summary>
static class Phone
{
    /// <summary>This computer's IPv4 addresses in its networks: the one with a gateway (the router) first; no virtual adapters.</summary>
    public static List<string> Addresses()
    {
        var list = new List<(string Ip, int Rank)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var name = (nic.Name + " " + nic.Description).ToLowerInvariant();
                if (new[] { "vethernet", "virtual", "vmware", "hyper-v", "wsl", "docker", "tap-", "zerotier" }.Any(name.Contains)) continue;
                var props = nic.GetIPProperties();
                bool gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var a in props.UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a.Address)) continue;
                    var b = a.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue; // no DHCP answer
                    // The home network first, Tailscale (100.64/10) after it: it reaches the phone outside home too
                    int rank = (gateway ? 0 : 2) + (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1)
                        + (b[0] == 100 && b[1] >= 64 && b[1] < 128 ? 4 : 0);
                    list.Add((a.Address.ToString(), rank));
                }
            }
        }
        catch (NetworkInformationException) { }
        return list.OrderBy(x => x.Rank).Select(x => x.Ip).Distinct().ToList();
    }

    /// <summary>The address as the phone opens it: this computer's own names (localhost, 127.x, *.localhost) become its IP.</summary>
    public static string? LanUrl(string url, string ip)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) return null;
        var host = u.Host.ToLowerInvariant().Trim('[', ']');
        if (IPAddress.TryParse(host, out var given) && !IPAddress.IsLoopback(given))
            return NetGuard.IsLocalIp(given) ? u.AbsoluteUri : null; // another machine of the network: as it is
        // Names of this machine: localhost, *.localhost, its own name and the hosts file's (they point at 127.0.0.1)
        bool own = IPAddress.TryParse(host, out _) || host == "localhost" || host.EndsWith(".localhost")
            || host == Environment.MachineName.ToLowerInvariant() || NetGuard.IsLocal(u);
        if (!own) return null;
        return new UriBuilder(u) { Host = ip }.Uri.AbsoluteUri;
    }

    /// <summary>Whether something answers on the address and port: the server and the firewall both let the phone in.</summary>
    public static async Task<bool> Answers(string host, int port)
    {
        using var client = new TcpClient();
        try
        {
            var connect = client.ConnectAsync(host, port);
            return await Task.WhenAny(connect, Task.Delay(700)) == connect && client.Connected;
        }
        catch (Exception) { return false; }
    }

    /// <summary>The project tiles with a site the phone can open, and whether it answers there.</summary>
    public static async Task<List<Dictionary<string, object?>>> Sites(string? ip)
    {
        var sites = new List<Dictionary<string, object?>>();
        var checks = new List<Task>();
        foreach (var p in ProjectStore.All)
        {
            if (p.IsTerminal || ip == null || LanUrl(p.Url, ip) is not { } lan) continue;
            var u = new Uri(lan);
            var site = new Dictionary<string, object?> { ["id"] = p.Id, ["name"] = p.Name, ["url"] = p.Url, ["lan"] = lan };
            sites.Add(site);
            var local = new Uri(p.Url.Trim());
            checks.Add(Task.Run(async () =>
            {
                site["answers"] = await Answers(u.Host, u.Port);
                // Answers here but not at the network address: it listens on 127.0.0.1 only, or the firewall stops it
                site["here"] = local.Host == u.Host ? site["answers"] : await Answers(local.IsLoopback ? "127.0.0.1" : local.Host, local.Port);
            }));
        }
        await Task.WhenAll(checks);
        return sites;
    }

    // ---- SSH

    static ServiceController? Sshd()
    {
        try { return ServiceController.GetServices().FirstOrDefault(s => s.ServiceName.Equals("sshd", StringComparison.OrdinalIgnoreCase)); }
        catch (Exception) { return null; }
    }

    /// <summary>The host key's fingerprint as ssh prints it (SHA256:…): the phone checks it, so nobody can pose as this computer.</summary>
    public static string? Fingerprint()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ssh");
        foreach (var name in new[] { "ssh_host_ed25519_key.pub", "ssh_host_ecdsa_key.pub" })
        {
            try
            {
                var parts = File.ReadAllText(Path.Combine(dir, name)).Trim().Split(' ');
                if (parts.Length < 2) continue;
                using var sha = SHA256.Create();
                return "SHA256:" + Convert.ToBase64String(sha.ComputeHash(Convert.FromBase64String(parts[1]))).TrimEnd('=');
            }
            catch (Exception) { }
        }
        return null;
    }

    public static bool Busy;

    public static bool Running()
    {
        using var s = Sshd();
        try { return s?.Status == ServiceControllerStatus.Running; } catch (Exception) { return false; }
    }

    /// <summary>The terminal's state for the dialog, with the link of the QR code when the server runs.</summary>
    public static Dictionary<string, object?> Ssh(string? ip)
    {
        using var s = Sshd();
        bool installed = s != null, running = false;
        try { running = s?.Status == ServiceControllerStatus.Running; } catch (Exception) { }
        var user = Environment.UserName;
        var fp = running ? Fingerprint() : null;
        string? link = null;
        if (running && ip != null)
        {
            link = "litebro://ssh?host=" + Uri.EscapeDataString(ip) + "&port=22&user=" + Uri.EscapeDataString(user)
                + "&name=" + Uri.EscapeDataString(Environment.MachineName) + (fp != null ? "&fp=" + Uri.EscapeDataString(fp) : "");
        }
        return new Dictionary<string, object?>
        {
            ["installed"] = installed, ["running"] = running, ["busy"] = Busy, ["user"] = user, ["fp"] = fp, ["link"] = link,
        };
    }

    /// <summary>
    /// Installs and starts OpenSSH Server (PowerShell for the phone's terminal, the port for private networks only),
    /// or stops it and keeps it from starting with Windows. Asks for the administrator: false when refused or failed.
    /// </summary>
    public static Task<bool> SetSsh(bool on) => RunElevated(on ? EnableScript : DisableScript);

    /// <summary>Installs OpenSSH Server if missing, PowerShell as its shell, starts with Windows, the port for private networks.</summary>
    public const string EnableScript =
        "$ErrorActionPreference = 'Stop'\n" +
        "if (-not (Get-Service sshd -ErrorAction SilentlyContinue)) { Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0 | Out-Null }\n" +
        "New-Item -Path 'HKLM:\\SOFTWARE\\OpenSSH' -Force | Out-Null\n" +
        "New-ItemProperty -Path 'HKLM:\\SOFTWARE\\OpenSSH' -Name DefaultShell -Value \"$env:WINDIR\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -PropertyType String -Force | Out-Null\n" +
        "Set-Service sshd -StartupType Automatic\n" +
        "Start-Service sshd\n" +
        "$rule = Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue\n" +
        "if ($rule) { $rule | Set-NetFirewallRule -Profile Private -Enabled True }\n" +
        "else { New-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -DisplayName 'OpenSSH Server (sshd)' -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 -Profile Private | Out-Null }\n";

    const string DisableScript =
        "Stop-Service sshd -ErrorAction SilentlyContinue\n" +
        "Set-Service sshd -StartupType Disabled -ErrorAction SilentlyContinue\n" +
        "Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue | Set-NetFirewallRule -Enabled False\n";

    /// <summary>A PowerShell script with the administrator's prompt: false when refused or failed.</summary>
    public static async Task<bool> RunElevated(string script)
    {
        var info = new ProcessStartInfo("powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
        };
        Busy = true;
        try
        {
            // The administrator's prompt holds the calling thread until it is answered: not the window's
            return await Task.Run(() =>
            {
                try
                {
                    using var p = Process.Start(info);
                    if (p == null) return false;
                    // Installing the capability downloads it from Windows Update: minutes
                    p.WaitForExit(15 * 60 * 1000);
                    return p.HasExited && p.ExitCode == 0;
                }
                catch (System.ComponentModel.Win32Exception) { return false; } // the prompt was refused
            });
        }
        finally { Busy = false; }
    }

    // ---- The key of a phone

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

    /// <summary>
    /// Whether the user is an administrator, also behind UAC (a limited token): OpenSSH then reads
    /// ProgramData\ssh\administrators_authorized_keys instead of the user's own file.
    /// </summary>
    static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator)) return true;
        // TokenElevationType (18): 3 is the limited half of an administrator's split token
        return GetTokenInformation(id.Token, 18, out int type, sizeof(int), out _) && type == 3;
    }

    /// <summary>
    /// Lets the phone in with its key: switches the SSH sign-in on when it is off and adds the key where OpenSSH
    /// reads it. One administrator's prompt when needed (an administrator's keys, or the server off); false when refused.
    /// </summary>
    public static async Task<bool> AddPhoneKey(string key)
    {
        bool admin = IsAdmin(), running = Running();
        if (!admin)
        {
            // The user's own file: no prompt. The server, when off, needs one (it may be another account's)
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "authorized_keys");
                if (!File.Exists(file) || !File.ReadAllText(file).Contains(key)) File.AppendAllText(file, key + "\n", Encoding.ASCII);
            }
            catch (Exception) { return false; }
            return running || await RunElevated(EnableScript);
        }
        var add =
            "$k = '" + key + "'\n" +
            "$f = Join-Path $env:ProgramData 'ssh\\administrators_authorized_keys'\n" +
            "if (-not (Test-Path $f) -or -not (Select-String -Path $f -SimpleMatch $k -Quiet)) { Add-Content -Path $f -Value $k -Encoding ascii }\n" +
            "icacls $f /inheritance:r /grant '*S-1-5-32-544:F' /grant '*S-1-5-18:F' | Out-Null\n";
        return await RunElevated((running ? "$ErrorActionPreference = 'Stop'\n" : EnableScript) + add);
    }
}

/// <summary>
/// LiteBro on iPhone finds this computer by itself (UDP broadcast on <see cref="Port"/>, the home network) and asks
/// to sign in: the person here answers, and the phone's key is added for SSH, so no QR code and no Windows password.
/// The installer opens the port for private networks only. Off with the «С телефона» button on /dev.
/// </summary>
static class PhoneFind
{
    public const int Port = 47321;
    static UdpClient? udp;
    static Func<string, string, Task<bool>> ask = null!;
    static Action<Action> onUi = null!;

    /// <summary>A sign-in asked by a phone: its id, what was answered (null while the person decides)</summary>
    sealed class Request { public string Id = ""; public Dictionary<string, object?>? Reply; public DateTime At = DateTime.UtcNow; }
    static Request? pending;
    static readonly List<Request> done = new();
    static readonly List<DateTime> refusals = new();

    /// <param name="ask">Asks the person (phone's name, code shown on the phone) on the UI thread</param>
    public static void Start(Func<string, string, Task<bool>> ask, Action<Action> ui)
    {
        PhoneFind.ask = ask;
        onUi = ui;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, Port));
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        }
        catch (SocketException) { return; } // another LiteBro of another user has the port
        _ = Task.Run(Loop);
    }

    static async Task Loop()
    {
        while (udp != null)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(); }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; } // ICMP unreachable of an earlier reply
            try
            {
                if (r.Buffer.Length > 2048 || !NetGuard.IsLocalIp(r.RemoteEndPoint.Address) || !Dev.On("phone")) continue;
                var m = ProjectStore.Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(r.Buffer));
                if (m == null || !(m.TryGetValue("litebro", out var v) && v is int n && n == 1)) continue;
                var reply = Handle(m);
                if (reply == null) continue;
                reply["litebro"] = 1;
                var bytes = Encoding.UTF8.GetBytes(ProjectStore.Json.Serialize(reply));
                await udp.SendAsync(bytes, bytes.Length, r.RemoteEndPoint);
            }
            catch (Exception) { } // a stray packet
        }
    }

    static string? Str(Dictionary<string, object> m, string k) => m.TryGetValue(k, out var v) ? v as string : null;

    static Dictionary<string, object?>? Handle(Dictionary<string, object> m)
    {
        switch (Str(m, "type"))
        {
            case "find":
                return new() { ["type"] = "here", ["name"] = Environment.MachineName, ["user"] = Environment.UserName };
            case "pair":
                var id = Str(m, "id") ?? "";
                var key = Str(m, "key") ?? "";
                var phone = Str(m, "phone") ?? "iPhone";
                if (id.Length is < 8 or > 64) return null;
                if (!Regex.IsMatch(key, "^ssh-ed25519 [A-Za-z0-9+/]{68}={0,2}( [A-Za-z0-9._-]{1,40})?$")) return Answer(id, "failed");
                if (phone.Length > 40) phone = phone.Substring(0, 40);
                phone = Regex.Replace(phone, @"[\p{C}]", "");
                lock (done)
                {
                    if (done.FirstOrDefault(d => d.Id == id) is { } old) return old.Reply;
                    if (pending != null && pending.Id == id) return Answer(id, "pending");
                    // One question at a time; a few refusals in a row close the door for ten minutes
                    if (pending != null && DateTime.UtcNow - pending.At < TimeSpan.FromMinutes(3)) return Answer(id, "busy");
                    refusals.RemoveAll(t => DateTime.UtcNow - t > TimeSpan.FromMinutes(10));
                    if (refusals.Count >= 3) return Answer(id, "denied");
                    pending = new Request { Id = id };
                }
                var request = pending;
                onUi(async () =>
                {
                    Dictionary<string, object?> reply;
                    if (!await ask(phone, Code(key)))
                    {
                        lock (done) refusals.Add(DateTime.UtcNow);
                        reply = Answer(id, "denied");
                    }
                    else if (!await Phone.AddPhoneKey(key)) reply = Answer(id, "failed");
                    else
                    {
                        reply = Answer(id, "ok");
                        reply["port"] = 22;
                        reply["user"] = Environment.UserName;
                        reply["name"] = Environment.MachineName;
                        reply["fp"] = Phone.Fingerprint();
                    }
                    lock (done)
                    {
                        request.Reply = reply;
                        done.Add(request);
                        if (done.Count > 20) done.RemoveAt(0);
                        if (pending == request) pending = null;
                    }
                });
                return Answer(id, "pending");
        }
        return null;
    }

    static Dictionary<string, object?> Answer(string id, string state) => new() { ["type"] = "pair", ["id"] = id, ["state"] = state };

    /// <summary>Four digits from the key, shown on both sides: the person sees it is their phone asking</summary>
    public static string Code(string key)
    {
        using var sha = SHA256.Create();
        var h = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
        return ((((uint)h[0] << 24) | ((uint)h[1] << 16) | ((uint)h[2] << 8) | h[3]) % 10000).ToString("D4");
    }
}
