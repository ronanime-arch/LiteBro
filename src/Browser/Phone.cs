using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
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
    static string? Fingerprint()
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
    public static async Task<bool> SetSsh(bool on)
    {
        const string enable =
            "$ErrorActionPreference = 'Stop'\n" +
            "if (-not (Get-Service sshd -ErrorAction SilentlyContinue)) { Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0 | Out-Null }\n" +
            "New-Item -Path 'HKLM:\\SOFTWARE\\OpenSSH' -Force | Out-Null\n" +
            "New-ItemProperty -Path 'HKLM:\\SOFTWARE\\OpenSSH' -Name DefaultShell -Value \"$env:WINDIR\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -PropertyType String -Force | Out-Null\n" +
            "Set-Service sshd -StartupType Automatic\n" +
            "Start-Service sshd\n" +
            "$rule = Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue\n" +
            "if ($rule) { $rule | Set-NetFirewallRule -Profile Private -Enabled True }\n" +
            "else { New-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -DisplayName 'OpenSSH Server (sshd)' -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 -Profile Private | Out-Null }\n";
        const string disable =
            "Stop-Service sshd -ErrorAction SilentlyContinue\n" +
            "Set-Service sshd -StartupType Disabled -ErrorAction SilentlyContinue\n" +
            "Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue | Set-NetFirewallRule -Enabled False\n";
        var script = on ? enable : disable;
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
}
