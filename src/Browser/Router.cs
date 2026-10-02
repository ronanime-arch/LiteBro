using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LiteBro;

/// <summary>
/// Windows gives LiteBro every web link (it hands over http and https only together): local addresses
/// open here, anything else goes straight on to the main browser, launched directly so it cannot come back
/// (unless ExternalToMain is off: then internet addresses open here too).
/// </summary>
static class Router
{
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
    const int AnyProcess = -1;

    // Names the hosts file points at this machine (project.test and the like). 0.0.0.0 lines are
    // ad blocking, not local sites, so only real loopback addresses count.
    static readonly Lazy<HashSet<string>> HostsLoopbackNames = new(() =>
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var hosts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
            foreach (var raw in File.ReadAllLines(hosts))
            {
                var parts = raw.Split('#')[0].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1 && IPAddress.TryParse(parts[0], out var ip) && IPAddress.IsLoopback(ip))
                    foreach (var name in parts.Skip(1)) names.Add(name);
            }
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        return names;
    });

    /// <summary>The names the hosts file points at this machine.</summary>
    public static IEnumerable<string> LocalNames() => HostsLoopbackNames.Value;

    /// <summary>Local web addresses only: files, mail links and the like belong to the main browser.</summary>
    public static bool IsLocal(Uri url, Settings settings)
    {
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) return false;
        var host = url.DnsSafeHost;
        return url.IsLoopback || host == "0.0.0.0" || host == "::"
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || HostsLoopbackNames.Value.Contains(host)
            || settings.LocalHosts.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(pattern => Regex.IsMatch(host, "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                    RegexOptions.IgnoreCase));
    }

    /// <summary>
    /// Whether a link from another program opens in LiteBro: a local address, and with ExternalToMain off any
    /// web address, but not in local-only mode, where it would only be blocked.
    /// </summary>
    public static bool OpensHere(Uri url, Settings settings) =>
        IsLocal(url, settings) || !settings.ExternalToMain && !settings.LocalOnly
            && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps);

    /// <summary>Servers print 0.0.0.0 ("all interfaces"), which a browser on Windows cannot connect to.</summary>
    public static Uri Normalize(Uri url)
    {
        var host = url.DnsSafeHost;
        if (host != "0.0.0.0" && host != "::") return url;
        return new UriBuilder(url) { Host = host == "::" ? "[::1]" : "127.0.0.1" }.Uri;
    }

    /// <summary>
    /// What to hand the main browser: web addresses escaped (they go into a command line),
    /// file paths exactly as Windows gave them, so #, % and non-Latin folder names survive.
    /// </summary>
    public static string ForOtherBrowser(string argument, Uri? url) =>
        url != null && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
            ? url.AbsoluteUri
            : argument.Replace("\"", "%22");

    static readonly string[] Chromiums = { "chrome.exe", "msedge.exe", "brave.exe", "vivaldi.exe", "chromium.exe", "yandex.exe", "browser.exe" };

    /// <summary>
    /// LiteBro holds http and https so that local links stay here, so the main browser is never the default and
    /// Chromium-based ones ask to be made it each time they start. Started by LiteBro, they are told not to ask
    /// (before --single-argument, which takes all that follows as the address).
    /// </summary>
    static string NoDefaultCheck(string exe) =>
        Chromiums.Contains(Path.GetFileName(exe), StringComparer.OrdinalIgnoreCase) ? "--no-default-browser-check " : "";

    /// <summary>
    /// Every link from other programs passes through here, so this must always open it somewhere:
    /// the settings override, the main browser (see Associations), then Edge. Never LiteBro under either name.
    /// </summary>
    public static void OpenElsewhere(string address, Settings settings)
    {
        AllowSetForegroundWindow(AnyProcess); // so the main browser may come to the front
        foreach (var command in new[] { settings.OtherBrowser, Associations.OtherBrowserCommand() })
        {
            try
            {
                if (Associations.Split(command, address) is not { } target || Associations.IsOurs(target.Exe)) continue;
                Process.Start(new ProcessStartInfo(target.Exe, NoDefaultCheck(target.Exe) + target.Args) { UseShellExecute = false });
                return;
            }
            catch (Exception) { }
        }
        // Edge ships with Windows 10/11 and has a protocol of its own, so this cannot loop back here
        try { Process.Start(new ProcessStartInfo("microsoft-edge:" + address) { UseShellExecute = true }); }
        catch (Exception) { }
    }
}

/// <summary>One LiteBro process per user: later launches pass their address to it and exit.</summary>
static class SingleInstance
{
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);

    static readonly string Id = WindowsIdentity.GetCurrent().User!.Value;
    static readonly string PipeName = Associations.AppName + "-" + Id;
    static readonly Mutex Primary = new(false, @"Local\" + Associations.AppName + "-" + Id);

    public static bool TryBecomePrimary() => Take(0);

    /// <summary>Whether a LiteBro with windows is running for this user (checked without staying primary).</summary>
    public static bool PrimaryRunning()
    {
        if (!Take(0)) return true;
        Primary.ReleaseMutex();
        return false;
    }

    /// <summary>For when the running one is closing down and refused: take over once it is gone.</summary>
    public static bool WaitToBecomePrimary(int milliseconds) => Take(milliseconds);

    static bool Take(int milliseconds)
    {
        try { return Primary.WaitOne(milliseconds); }
        catch (AbandonedMutexException) { return true; } // the previous one crashed
    }

    /// <summary>Sends an address ("" = just come to the front); true once the running one accepted it.</summary>
    public static bool SendToPrimary(string message)
    {
        try
        {
            AllowSetForegroundWindow(-1);
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(3000);
            var bytes = Encoding.UTF8.GetBytes(message + "\n");
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            var reply = Task.Run(() => pipe.ReadByte());
            return reply.Wait(10_000) && reply.Result == 1;
        }
        catch (Exception) { return false; }
    }

    /// <summary>This user alone may connect: another account's process cannot pass addresses in.</summary>
    static PipeSecurity OwnerOnly()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    /// <summary>Serves later launches on a background thread; handle runs the request and says if it was taken.</summary>
    public static void Listen(Func<string, bool> handle)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.None, 0, 0, OwnerOnly());
                    pipe.WaitForConnection();
                    var line = new List<byte>();
                    for (int b; (b = pipe.ReadByte()) >= 0 && b != '\n' && line.Count < 65536;) line.Add((byte)b);
                    pipe.WriteByte(handle(Encoding.UTF8.GetString(line.ToArray())) ? (byte)1 : (byte)0);
                    pipe.Flush();
                    pipe.WaitForPipeDrain();
                }
                catch (Exception) { Thread.Sleep(200); }
            }
        })
        { IsBackground = true, Name = "LiteBro instances" };
        thread.Start();
    }
}
