using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace LiteBro;

/// <summary>
/// Whether Windows gives links to LiteBro, which browser is the main one, and how to launch one directly
/// from its registry command. Shared with the installer.
/// </summary>
static class Associations
{
    public const string AppName = "LiteBro";
    public const string Company = "ronanime-arch";
    public const string ProgId = "LiteBroURL";
    /// <summary>Ties the ProgId, the Start menu shortcut and the running windows to one app for Windows.</summary>
    public const string AppUserModelId = Company + "." + AppName;
    /// <summary>HKCU key with the browser that had https links before LiteBro.</summary>
    const string StateKey = @"Software\" + AppName;

    // The names up to 1.9.6, when this browser was LiteBrowser
    public const string OldAppName = "LiteBrowser";
    public const string OldProgId = "LiteBrowserURL";
    const string OldStateKey = @"Software\" + OldAppName;

    const int IsProtocol = 0x1000, AssocCommand = 1, AssocFriendlyAppName = 4;

    [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>
    /// Versions up to 1.3 registered LiteBrowser for one user. A leftover of that whose program is gone
    /// hides the registration for the whole computer, and links go nowhere: take it away. Runs as the user.
    /// </summary>
    public static void RemoveStalePerUserRegistration()
    {
        var cu = Registry.CurrentUser;
        using (var command = cu.OpenSubKey($@"Software\Classes\{OldProgId}\shell\open\command"))
        {
            // Nothing there, or its program still exists
            if (command?.GetValue("") is not string value || Split(value, "x") != null) return;
        }
        cu.DeleteSubKeyTree($@"Software\Classes\{OldProgId}", false);
        cu.DeleteSubKeyTree($@"Software\Clients\StartMenuInternet\{OldAppName}", false);
        cu.DeleteSubKeyTree($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{OldAppName}", false);
        cu.DeleteSubKeyTree($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{OldAppName}.exe", false);
        using (var apps = cu.OpenSubKey(@"Software\RegisteredApplications", writable: true)) apps?.DeleteValue(OldAppName, false);
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Moves the main browser that LiteBrowser remembered under its own key over to LiteBro's.</summary>
    public static void MoveOldState()
    {
        var cu = Registry.CurrentUser;
        using (var old = cu.OpenSubKey(OldStateKey))
        {
            if (old == null) return;
            using var key = cu.CreateSubKey(StateKey);
            foreach (var name in old.GetValueNames())
                if (key.GetValue(name) == null && old.GetValue(name) is { } value) key.SetValue(name, value, old.GetValueKind(name));
        }
        cu.DeleteSubKeyTree(OldStateKey, false);
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int AssocQueryString(int flags, int what, string assoc, string? extra, StringBuilder result, ref int length);

    static string? Query(int what, string assoc, bool protocol = true)
    {
        int length = 1024;
        var result = new StringBuilder(length);
        return AssocQueryString(protocol ? IsProtocol : 0, what, assoc, "open", result, ref length) == 0 ? result.ToString() : null;
    }

    static bool IsExe(string exe, string app) => Path.GetFileName(exe).Equals(app + ".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Any LiteBro.exe or LiteBrowser.exe, wherever it is installed: never the main browser, never handed a link.</summary>
    public static bool IsOurs(string exe) => IsExe(exe, AppName) || IsExe(exe, OldAppName);

    /// <summary>True when Windows gives links of this protocol ("http" or "https") to LiteBro.</summary>
    public static bool Handles(string protocol) => HandledBy(protocol, AppName);

    /// <summary>True when they still go to LiteBrowser, as this browser was called up to 1.9.6.</summary>
    public static bool OldNameHandles(string protocol) => HandledBy(protocol, OldAppName);

    static bool HandledBy(string protocol, string app) =>
        Split(Query(AssocCommand, protocol), "x") is { } c && IsExe(c.Exe, app);

    /// <summary>The main browser's open command; null when there is none but LiteBro.</summary>
    public static string? OtherBrowserCommand() => MainBrowser()?.Command;

    /// <summary>Name of the main browser, "Google Chrome" for one.</summary>
    public static string OtherBrowserName() => MainBrowser()?.Name is { Length: > 0 } name ? name : L.T("основной браузер");

    /// <summary>
    /// Windows hands LiteBro http and https together, but the main browser keeps .html files, so it is
    /// the one that opens them. Should .html go to LiteBro as well, the one that had https before it.
    /// </summary>
    static (string Command, string Name)? MainBrowser()
    {
        var html = Query(AssocCommand, ".html", protocol: false);
        if (IsOtherBrowser(html)) return (html!, Query(AssocFriendlyAppName, ".html", protocol: false) ?? "");
        using var key = Registry.CurrentUser.OpenSubKey(StateKey);
        var remembered = key?.GetValue("OtherBrowser") as string;
        return IsOtherBrowser(remembered) ? (remembered!, key!.GetValue("OtherBrowserName") as string ?? "") : null;
    }

    /// <summary>Saves the browser that has https links now, before the user hands them to LiteBro.</summary>
    public static void RememberDefault()
    {
        var command = Query(AssocCommand, "https");
        if (!IsOtherBrowser(command)) return;
        using var key = Registry.CurrentUser.CreateSubKey(StateKey);
        key.SetValue("OtherBrowser", command!);
        key.SetValue("OtherBrowserName", Query(AssocFriendlyAppName, "https") ?? "");
    }

    /// <summary>Takes away what RememberDefault saved, under either name.</summary>
    public static void Forget()
    {
        Registry.CurrentUser.DeleteSubKeyTree(StateKey, false);
        Registry.CurrentUser.DeleteSubKeyTree(OldStateKey, false);
    }

    /// <summary>A registered browser other than this one, not an editor that happens to open .html files.</summary>
    static bool IsOtherBrowser(string? command)
    {
        if (Split(command, "x") is not { } c || IsOurs(c.Exe)) return false;
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
