using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace LiteBrowser;

/// <summary>
/// Whether Windows gives links to LiteBrowser, which browser is the main one, and how to launch one directly
/// from its registry command. Shared with the installer.
/// </summary>
static class Associations
{
    public const string AppName = "LiteBrowser";
    public const string ProgId = "LiteBrowserURL";
    /// <summary>Ties the ProgId, the Start menu shortcut and the running windows to one app for Windows.</summary>
    public const string AppUserModelId = "LiteBrowser";
    /// <summary>HKCU key with the browser that had https links before LiteBrowser.</summary>
    const string StateKey = @"Software\LiteBrowser";

    const int IsProtocol = 0x1000, AssocCommand = 1, AssocFriendlyAppName = 4;

    [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>
    /// Versions up to 1.3 registered LiteBrowser for one user. A leftover of that whose program is gone
    /// hides the registration for the whole computer, and links go nowhere: take it away. Runs as the user.
    /// </summary>
    public static void RemoveStalePerUserRegistration()
    {
        var cu = Registry.CurrentUser;
        using (var command = cu.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command"))
        {
            // Nothing there, or its program still exists
            if (command?.GetValue("") is not string value || Split(value, "x") != null) return;
        }
        cu.DeleteSubKeyTree($@"Software\Classes\{ProgId}", false);
        cu.DeleteSubKeyTree($@"Software\Clients\StartMenuInternet\{AppName}", false);
        cu.DeleteSubKeyTree($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppName}", false);
        cu.DeleteSubKeyTree($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{AppName}.exe", false);
        using (var apps = cu.OpenSubKey(@"Software\RegisteredApplications", writable: true)) apps?.DeleteValue(AppName, false);
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int AssocQueryString(int flags, int what, string assoc, string? extra, StringBuilder result, ref int length);

    static string? Query(int what, string assoc, bool protocol = true)
    {
        int length = 1024;
        var result = new StringBuilder(length);
        return AssocQueryString(protocol ? IsProtocol : 0, what, assoc, "open", result, ref length) == 0 ? result.ToString() : null;
    }

    /// <summary>Any LiteBrowser.exe, wherever it is installed.</summary>
    public static bool IsLiteBrowser(string exe) =>
        Path.GetFileName(exe).Equals(AppName + ".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when Windows gives links of this protocol ("http" or "https") to LiteBrowser.</summary>
    public static bool Handles(string protocol) =>
        Split(Query(AssocCommand, protocol), "x") is { } c && IsLiteBrowser(c.Exe);

    /// <summary>The main browser's open command; null when there is none but LiteBrowser.</summary>
    public static string? OtherBrowserCommand() => MainBrowser()?.Command;

    /// <summary>Name of the main browser, "Google Chrome" for one.</summary>
    public static string OtherBrowserName() => MainBrowser()?.Name is { Length: > 0 } name ? name : "основной браузер";

    /// <summary>
    /// Windows hands LiteBrowser http and https together, but the main browser keeps .html files, so it is
    /// the one that opens them. Should .html go to LiteBrowser as well, the one that had https before it.
    /// </summary>
    static (string Command, string Name)? MainBrowser()
    {
        var html = Query(AssocCommand, ".html", protocol: false);
        if (IsOtherBrowser(html)) return (html!, Query(AssocFriendlyAppName, ".html", protocol: false) ?? "");
        using var key = Registry.CurrentUser.OpenSubKey(StateKey);
        var remembered = key?.GetValue("OtherBrowser") as string;
        return IsOtherBrowser(remembered) ? (remembered!, key!.GetValue("OtherBrowserName") as string ?? "") : null;
    }

    /// <summary>Saves the browser that has https links now, before the user hands them to LiteBrowser.</summary>
    public static void RememberDefault()
    {
        var command = Query(AssocCommand, "https");
        if (!IsOtherBrowser(command)) return;
        using var key = Registry.CurrentUser.CreateSubKey(StateKey);
        key.SetValue("OtherBrowser", command!);
        key.SetValue("OtherBrowserName", Query(AssocFriendlyAppName, "https") ?? "");
    }

    /// <summary>Takes away what RememberDefault saved.</summary>
    public static void Forget() => Registry.CurrentUser.DeleteSubKeyTree(StateKey, false);

    /// <summary>A registered browser other than LiteBrowser, not an editor that happens to open .html files.</summary>
    static bool IsOtherBrowser(string? command)
    {
        if (Split(command, "x") is not { } c || IsLiteBrowser(c.Exe)) return false;
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var clients = hive.OpenSubKey(@"Software\Clients\StartMenuInternet");
            if (clients == null) continue;
            foreach (var name in clients.GetSubKeyNames())
            {
                using var open = clients.OpenSubKey(name + @"\shell\open\command");
                if (Split(open?.GetValue("") as string, "x") is { } b
                    && string.Equals(Path.GetFullPath(b.Exe), Path.GetFullPath(c.Exe), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Splits a registry command such as `"C:\…\chrome.exe" --single-argument %1` or
    /// `C:\…\firefox.exe -osint -url "%1"` and puts the url in place of %1.
    /// The url must already be escaped (Uri.AbsoluteUri): it goes into a command line.
    /// </summary>
    public static (string Exe, string Args)? Split(string? command, string url)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = Environment.ExpandEnvironmentVariables(command!.Trim());
        string exe, rest;
        if (text.StartsWith("\""))
        {
            int end = text.IndexOf('"', 1);
            if (end < 0) return null;
            exe = text.Substring(1, end - 1);
            rest = text.Substring(end + 1);
        }
        else
        {
            int end = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (end < 0) return null;
            exe = text.Substring(0, end + 4);
            rest = text.Substring(end + 4);
        }
        if (!File.Exists(exe)) return null;
        rest = rest.Replace("%*", "").Trim();
        var args = rest.Contains("%1") || rest.Contains("%L") || rest.Contains("%l")
            ? rest.Replace("%1", url).Replace("%L", url).Replace("%l", url)
            : (rest + " \"" + url + "\"").Trim();
        return (exe, args);
    }
}
