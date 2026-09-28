using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using LiteBro;
using Microsoft.Win32;

namespace LiteBroSetup;

/// <summary>
/// Installer and uninstaller for LiteBro, for the whole computer (it runs as administrator).
/// LiteBro takes the links other programs open (Windows hands over http and https only together):
/// local addresses open in it, the rest goes straight on to the main browser, which keeps .html files.
/// Up to 1.9.6 it was called LiteBrowser; installing takes that away (see RemoveLiteBrowser).
///   LiteBro-Setup.exe                        install with a dialog, then help the user give it the links
///   LiteBro-Setup.exe /S                     install silently (desktop shortcut, no launch)
///   Uninstall.exe /uninstall [/S] [/purge]   remove; /purge also deletes this user's settings and browser data
/// Exit codes: 0 done, 1 cancelled or failed, 2 LiteBro is running (silent mode).
/// </summary>
static class Program
{
    const string AppName = Associations.AppName;
    const string OldName = Associations.OldAppName;
    const string ExeName = AppName + ".exe";
    const string UninstallerName = "Uninstall.exe";
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName;
    const string ClientKey = @"SOFTWARE\Clients\StartMenuInternet\" + AppName;
    const string ProgIdKey = @"SOFTWARE\Classes\" + Associations.ProgId;
    const string RegisteredApps = @"SOFTWARE\RegisteredApplications";
    const string AppPathsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + ExeName;
    const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\System";
    const string PolicyValue = "DefaultAssociationsConfiguration";
    const string WebView2Client = @"Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    const string BrowserDescription =
        "Лёгкий браузер для локальных проектов: localhost и 127.0.0.1 открываются в нём, остальные ссылки — в основном браузере.";
    const string LinkDescription = "Лёгкий браузер для локальных проектов";

    static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    static readonly string InstallDir = Path.Combine(ProgramFiles, AppName);
    static readonly string DataDir = Path.Combine(LocalAppData, AppName);
    static readonly string StartMenuLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName + ".lnk");
    static readonly string DesktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), AppName + ".lnk");
    // LiteBrowser, as this browser was called up to 1.9.6
    static readonly string LiteBrowserDir = Path.Combine(ProgramFiles, OldName);
    static readonly string LiteBrowserDataDir = Path.Combine(LocalAppData, OldName);
    // Versions up to 1.3 installed for one user; Windows never offered that copy for links
    static readonly string OldInstallDir = Path.Combine(LocalAppData, "Programs", OldName);
    static readonly string[] OldLinks =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), OldName + ".lnk"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), OldName + ".lnk"),
    };
    static readonly string LogPath = Path.Combine(Path.GetTempPath(), AppName + "-setup.log");
    static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
    public static readonly Icon AppIcon = new(Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"));

    static bool silent;

    [STAThread]
    static int Main(string[] args)
    {
        silent = Has(args, "/S");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            return Has(args, "/uninstall") ? Uninstall(args) : Install(args);
        }
        catch (Exception e)
        {
            Report("Ошибка: " + e.Message, MessageBoxIcon.Error);
            return 1;
        }
    }

    static bool Has(string[] args, string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    static int Install(string[] args)
    {
        if (IsInside(Assembly.GetExecutingAssembly().Location, InstallDir))
        {
            Report("Для удаления используйте «Установленные приложения» в параметрах Windows.", MessageBoxIcon.Information);
            return 1;
        }
        // Windows lets only the user choose the program for a link type; the checkbox opens that choice
        // right after the install.
        bool desktop = true, links = false, launch = false;
        bool hasLinks = HandlesLinks();
        // Windows keeps the user's choice for links with LiteBrowser's ProgId, which goes with it. The choice
        // itself stays even once the ProgId is gone, as after an earlier run that stopped halfway.
        bool relink = !hasLinks && new[] { "http", "https" }.Any(protocol =>
            Associations.OldNameHandles(protocol) || UserChoice(protocol) == Associations.OldProgId);
        if (!silent)
        {
            var desktopBox = new CheckBox { Text = "Ярлык на рабочем столе", Checked = true, AutoSize = true };
            var linksBox = new CheckBox
            {
                Text = "Открывать в LiteBro ссылки на localhost и 127.0.0.1.\n" +
                    "Остальные ссылки он сразу передаёт в " + Associations.OtherBrowserName() + ", своё окно не открывая.",
                Checked = true,
                AutoSize = true,
            };
            var launchBox = new CheckBox { Text = "Запустить LiteBro после установки", Checked = true, AutoSize = true };
            var text = "Лёгкий браузер для локальных проектов на движке Edge WebView2.\n\n" +
                "Программа: " + InstallDir + "\nНастройки и данные: " + DataDir + " (у каждого пользователя свои)";
            if (Directory.Exists(LiteBrowserDir) || Directory.Exists(LiteBrowserDataDir))
                text += "\n\nLiteBrowser теперь называется LiteBro. Старая программа будет удалена, а проекты, значки, " +
                    "логи и входы на сайты LiteBro перенесёт к себе при первом запуске.";
            if (relink)
                text += "\n\nWindows запомнила выбор для ссылок под старым именем, поэтому их нужно один раз " +
                    "отдать LiteBro заново: после установки откроется подсказка.";
            else if (hasLinks) text += "\n\nСсылки из других программ уже идут через LiteBro.";
            if (!HasWebView2())
                text += "\n\nНе найден Microsoft Edge WebView2 Runtime, без него браузер не запустится. " +
                    "Установите его с developer.microsoft.com/microsoft-edge/webview2.";
            var options = hasLinks ? new[] { desktopBox, launchBox } : new[] { desktopBox, linksBox, launchBox };
            using var dialog = new Dialog("Установка LiteBro", "LiteBro " + Version, text, "Установить", options);
            if (dialog.ShowDialog() != DialogResult.OK) return 1;
            desktop = desktopBox.Checked;
            links = linksBox.Checked && !hasLinks;
            launch = launchBox.Checked;
        }
        if (!CloseRunning()) return 2;
        RemoveOldPerUserInstall();

        Directory.CreateDirectory(InstallDir);
        foreach (var old in Directory.EnumerateFileSystemEntries(InstallDir))
        {
            if (Directory.Exists(old)) Directory.Delete(old, true);
            else File.Delete(old);
        }
        long bytes = 0;
        using (var zip = new ZipArchive(Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")))
        {
            foreach (var entry in zip.Entries.Where(e => e.Name.Length > 0))
            {
                var target = Path.GetFullPath(Path.Combine(InstallDir, entry.FullName.Replace('/', '\\')));
                if (!IsInside(target, InstallDir)) throw new InvalidDataException("Недопустимый путь в архиве: " + entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var from = entry.Open())
                using (var to = File.Create(target))
                    from.CopyTo(to);
                bytes += entry.Length;
            }
        }
        var uninstaller = Path.Combine(InstallDir, UninstallerName);
        File.Copy(Assembly.GetExecutingAssembly().Location, uninstaller, true);
        bytes += new FileInfo(uninstaller).Length;

        var exe = Path.Combine(InstallDir, ExeName);
        Shortcut.Create(StartMenuLink, exe, LinkDescription, Associations.AppUserModelId);
        if (desktop) Shortcut.Create(DesktopLink, exe, LinkDescription, Associations.AppUserModelId);
        else DeleteLinkInto(DesktopLink, InstallDir);

        using (var key = Registry.LocalMachine.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", Version);
            key.SetValue("DisplayIcon", exe);
            key.SetValue("Publisher", Associations.Company);
            key.SetValue("InstallLocation", InstallDir);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            key.SetValue("UninstallString", $"\"{uninstaller}\" /uninstall");
            key.SetValue("QuietUninstallString", $"\"{uninstaller}\" /uninstall /S");
            key.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        Associations.MoveOldState();
        // The main browser, found even should .html go to LiteBro later. Asked while LiteBrowser still has
        // the links, if it had them: then the browser remembered before it stays.
        Associations.RememberDefault();
        RemoveLiteBrowser(exe);
        RegisterBrowser(exe); // lets Windows offer it for links
        // Earlier versions reached other programs' links in other ways
        RemoveChromeSwitch();
        RemoveBrowserVariable();
        RemoveHttpPolicy();
        foreach (var dir in new[] { DataDir, LiteBrowserDataDir }) TryDelete(Path.Combine(dir, "other-browser.txt"));

        if (links)
        {
            using var step = new LinksStep(giveBack: false);
            step.ShowDialog();
        }
        hasLinks = HandlesLinks();
        if (launch) StartAsUser(exe);
        if (!launch || silent || (links && !hasLinks))
        {
            Report(AppName + " " + Version + " установлен в " + InstallDir + "." + (hasLinks
                ? "\nЛокальные адреса открываются в нём, остальные ссылки — в " + Associations.OtherBrowserName() + "."
                : "\nЧтобы ссылки открывались в нём: Параметры → Приложения по умолчанию → LiteBro → HTTP → LiteBro."),
                MessageBoxIcon.Information);
        }
        return 0;
    }

    /// <summary>True when Windows gives LiteBro both kinds of web links.</summary>
    public static bool HandlesLinks() => Associations.Handles("http") && Associations.Handles("https");

    /// <summary>The ProgId the user chose for a protocol in Settings, read fresh each time.</summary>
    public static string? UserChoice(string protocol)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\{protocol}\UserChoice");
        return key?.GetValue("ProgId") as string;
    }

    /// <summary>
    /// Takes away LiteBrowser, as this browser was called up to 1.9.6: its program, shortcuts and registration.
    /// Its data stays for LiteBro to move over on its first start (Settings.MoveOldData). Windows keeps the user's
    /// choice for links with the ProgId that goes here, so they are given to LiteBro once more (LinksStep).
    /// </summary>
    static void RemoveLiteBrowser(string exe)
    {
        var lm = Registry.LocalMachine;
        lm.DeleteSubKeyTree(@"SOFTWARE\Classes\" + Associations.OldProgId, false);
        lm.DeleteSubKeyTree(@"SOFTWARE\Clients\StartMenuInternet\" + OldName, false);
        lm.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + OldName + ".exe", false);
        lm.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + OldName, false);
        using (var apps = lm.OpenSubKey(RegisteredApps, writable: true)) apps?.DeleteValue(OldName, false);
        foreach (var folder in new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.CommonDesktopDirectory })
            DeleteLinkInto(Path.Combine(Environment.GetFolderPath(folder), OldName + ".lnk"), LiteBrowserDir);
        // A pin on the taskbar would point at a program that is gone: it is turned to LiteBro instead
        var pins = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        if (Directory.Exists(pins))
        {
            foreach (var link in Directory.GetFiles(pins, "*.lnk"))
            {
                // A pin it cannot change is left to the user: never a reason to stop the install
                try
                {
                    if (Shortcut.Target(link) is { } target && IsInside(target, LiteBrowserDir))
                        Shortcut.Create(link, exe, LinkDescription, Associations.AppUserModelId);
                }
                catch (Exception e) when (e is COMException || e is IOException || e is UnauthorizedAccessException) { }
            }
        }
        for (int i = 0; i < 10 && Directory.Exists(LiteBrowserDir); i++)
        {
            try { Directory.Delete(LiteBrowserDir, true); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { Thread.Sleep(500); }
        }
        SHChangeNotify(AssocChanged, 0, IntPtr.Zero, IntPtr.Zero);
    }

    static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
    }

    /// <summary>Takes back BROWSER, which 1.6.0 pointed at LiteBrowser; one pointing elsewhere stays.</summary>
    static void RemoveBrowserVariable()
    {
        var current = Environment.GetEnvironmentVariable("BROWSER", EnvironmentVariableTarget.User);
        if (!string.IsNullOrEmpty(current) && Associations.IsOurs(current!))
            Environment.SetEnvironmentVariable("BROWSER", null, EnvironmentVariableTarget.User);
    }

    const string ChromePolicyKey = @"SOFTWARE\Policies\Google\Chrome";
    static readonly string[] SwitchValues =
        { "BrowserSwitcherEnabled", "AlternativeBrowserPath", "BrowserSwitcherKeepLastChromeTab", "BrowserSwitcherDelay" };

    /// <summary>Takes back the Chrome browser switcher that 1.5.0 set; any other Chrome policies stay.</summary>
    static void RemoveChromeSwitch()
    {
        using (var key = Registry.LocalMachine.OpenSubKey(ChromePolicyKey, writable: true))
        {
            if (key?.GetValue("AlternativeBrowserPath") is not string path || !InOurFolders(path)) return;
            foreach (var name in SwitchValues) key.DeleteValue(name, false);
            key.DeleteSubKeyTree("BrowserSwitcherUrlList", false);
            key.DeleteSubKeyTree("AlternativeBrowserParameters", false);
        }
        DeleteIfEmpty(ChromePolicyKey);
        DeleteIfEmpty(@"SOFTWARE\Policies\Google");
    }

    static void DeleteIfEmpty(string path)
    {
        using (var key = Registry.LocalMachine.OpenSubKey(path))
            if (key == null || key.ValueCount > 0 || key.SubKeyCount > 0) return;
        Registry.LocalMachine.DeleteSubKey(path, false);
    }

    /// <summary>
    /// Takes back the default associations policy that versions 1.4 to 1.7 set on a domain (Windows ignores it
    /// anywhere else), and only one pointing into an install folder; the file itself goes with that folder.
    /// </summary>
    static void RemoveHttpPolicy()
    {
        using var key = Registry.LocalMachine.OpenSubKey(PolicyKey, writable: true);
        if (key?.GetValue(PolicyValue) is string existing && InOurFolders(existing)) key.DeleteValue(PolicyValue, false);
    }

    /// <summary>
    /// Registers LiteBro for the whole computer the way Chrome does (one AppUserModelID for the ProgId,
    /// the shortcuts and the windows), so Windows offers it in its choice for HTTP and HTTPS.
    /// This changes no defaults: the user picks it there, and the main browser keeps the rest.
    /// </summary>
    static void RegisterBrowser(string exe)
    {
        var icon = exe + ",0";
        var lm = Registry.LocalMachine;
        lm.DeleteSubKeyTree(ProgIdKey, false);
        using (var progId = lm.CreateSubKey(ProgIdKey))
        {
            progId.SetValue("", AppName + " HTML Document");
            progId.SetValue("AppUserModelId", Associations.AppUserModelId);
            using (var k = progId.CreateSubKey("DefaultIcon")) k.SetValue("", icon);
            using (var k = progId.CreateSubKey(@"shell\open\command")) k.SetValue("", $"\"{exe}\" \"%1\"");
            using (var k = progId.CreateSubKey("Application"))
            {
                k.SetValue("AppUserModelId", Associations.AppUserModelId);
                k.SetValue("ApplicationName", AppName);
                k.SetValue("ApplicationDescription", BrowserDescription);
                k.SetValue("ApplicationIcon", icon);
                k.SetValue("ApplicationCompany", Associations.Company);
            }
        }
        lm.DeleteSubKeyTree(ClientKey, false);
        using (var client = lm.CreateSubKey(ClientKey))
        {
            client.SetValue("", AppName);
            using (var k = client.CreateSubKey("DefaultIcon")) k.SetValue("", icon);
            using (var k = client.CreateSubKey(@"shell\open\command")) k.SetValue("", $"\"{exe}\"");
            using (var k = client.CreateSubKey("InstallInfo")) k.SetValue("IconsVisible", 1, RegistryValueKind.DWord);
            using var capabilities = client.CreateSubKey("Capabilities");
            capabilities.SetValue("ApplicationName", AppName);
            capabilities.SetValue("ApplicationDescription", BrowserDescription);
            capabilities.SetValue("ApplicationIcon", icon);
            // Settings offers a browser for HTTP only when it takes https as well (checked on 26100), and picking
            // it for one hands it both. .htm and .html are listed like any browser's; the user leaves them be.
            capabilities.DeleteSubKeyTree("URLAssociations", false);
            using (var k = capabilities.CreateSubKey("URLAssociations"))
            {
                k.SetValue("http", Associations.ProgId);
                k.SetValue("https", Associations.ProgId);
            }
            capabilities.DeleteSubKeyTree("FileAssociations", false);
            using (var k = capabilities.CreateSubKey("FileAssociations"))
            {
                k.SetValue(".htm", Associations.ProgId);
                k.SetValue(".html", Associations.ProgId);
            }
            using (var k = capabilities.CreateSubKey("StartMenu")) k.SetValue("StartMenuInternet", AppName);
        }
        using (var k = lm.CreateSubKey(AppPathsKey))
        {
            k.SetValue("", exe);
            k.SetValue("Path", Path.GetDirectoryName(exe)!);
        }
        using (var apps = lm.CreateSubKey(RegisteredApps)) apps.SetValue(AppName, ClientKey + @"\Capabilities");
        SHChangeNotify(AssocChanged, 0, IntPtr.Zero, IntPtr.Zero);
    }

    static void UnregisterBrowser()
    {
        var lm = Registry.LocalMachine;
        lm.DeleteSubKeyTree(ProgIdKey, false);
        lm.DeleteSubKeyTree(ClientKey, false);
        lm.DeleteSubKeyTree(AppPathsKey, false);
        using (var apps = lm.OpenSubKey(RegisteredApps, writable: true)) apps?.DeleteValue(AppName, false);
        SHChangeNotify(AssocChanged, 0, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Takes away the per-user copy of versions up to 1.3, whose ProgId would shadow this one.</summary>
    static void RemoveOldPerUserInstall()
    {
        var cu = Registry.CurrentUser;
        cu.DeleteSubKeyTree(@"Software\Classes\" + Associations.OldProgId, false);
        cu.DeleteSubKeyTree(@"Software\Clients\StartMenuInternet\" + OldName, false);
        cu.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + OldName + ".exe", false);
        cu.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + OldName, false);
        using (var apps = cu.OpenSubKey(@"Software\RegisteredApplications", writable: true)) apps?.DeleteValue(OldName, false);
        foreach (var ext in new[] { ".htm", ".html" })
            using (var k = cu.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids", writable: true))
                k?.DeleteValue(Associations.OldProgId, false);
        foreach (var link in OldLinks) DeleteLinkInto(link, OldInstallDir);
        try { if (Directory.Exists(OldInstallDir)) Directory.Delete(OldInstallDir, true); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
    }

    const int AssocChanged = 0x08000000;
    [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    public static void OpenSettings(string page)
    {
        try { Process.Start(new ProcessStartInfo(page) { UseShellExecute = true }); }
        catch (Exception) { }
    }

    /// <summary>The installer runs as administrator; the browser must not. Explorer starts it as the user.</summary>
    static void StartAsUser(string exe)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"") { UseShellExecute = false }); }
        catch (Exception) { }
    }

    static void DeleteLinkInto(string link, string dir)
    {
        if (Shortcut.Target(link) is { } target && IsInside(target, dir)) File.Delete(link);
    }

    static int Uninstall(string[] args)
    {
        // A running exe cannot delete itself: hand the work to a copy in %TEMP% (it inherits the elevation)
        var self = Assembly.GetExecutingAssembly().Location;
        if (IsInside(self, InstallDir))
        {
            var copy = Path.Combine(Path.GetTempPath(), AppName + "-uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
            File.Copy(self, copy);
            Process.Start(new ProcessStartInfo(copy, string.Join(" ", args)) { UseShellExecute = false });
            return 0;
        }

        bool purge = Has(args, "/purge");
        // Asked before the registration goes: afterwards Windows cannot tell
        bool hadLinks = Associations.Handles("http") || Associations.Handles("https");
        if (!silent)
        {
            var purgeBox = new CheckBox
            {
                Text = "Удалить также мои настройки и данные браузера:\nпроекты, cookies, settings.ini",
                AutoSize = true,
            };
            using var dialog = new Dialog("Удаление LiteBro", "Удалить LiteBro?",
                "Будут удалены программа, ярлыки и запись в списке установленных приложений.\n" +
                "Данные в " + DataDir + " останутся, если не отметить пункт ниже." +
                (hadLinks ? "\n\nЗатем ссылки нужно будет вернуть " + Associations.OtherBrowserName() +
                    ": три клика в Параметрах, подсказка откроется сама." : ""), "Удалить", purgeBox);
            if (dialog.ShowDialog() != DialogResult.OK) return 1;
            purge = purgeBox.Checked;
        }
        if (!CloseRunning()) return 2;

        if (hadLinks && !silent)
        {
            // LiteBro's page in Settings is there only while it is registered
            using var step = new LinksStep(giveBack: true);
            step.ShowDialog();
            hadLinks = Associations.Handles("http") || Associations.Handles("https");
        }
        Associations.Forget();
        RemoveChromeSwitch();
        RemoveHttpPolicy();
        RemoveBrowserVariable();
        UnregisterBrowser();
        foreach (var link in new[] { StartMenuLink, DesktopLink }) DeleteLinkInto(link, InstallDir);
        // Retry while the Uninstall.exe that started this copy is still exiting
        for (int i = 0; i < 20 && Directory.Exists(InstallDir); i++)
        {
            try { Directory.Delete(InstallDir, true); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { Thread.Sleep(500); }
        }
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false);
        // LiteBrowser's folder too, should LiteBro not have moved it over yet
        foreach (var dir in purge ? new[] { DataDir, LiteBrowserDataDir } : new string[0])
        {
            for (int i = 0; i < 10 && Directory.Exists(dir); i++)
            {
                try { Directory.Delete(dir, true); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { Thread.Sleep(500); }
            }
        }

        if (Directory.Exists(InstallDir)) Report("Не всё удалось удалить: " + InstallDir, MessageBoxIcon.Warning);
        else Report("LiteBro удалён." + (purge ? "" : "\nНастройки и данные остались в " + DataDir), MessageBoxIcon.Information);
        if (hadLinks)
        {
            Report("Ссылки http и https всё ещё назначены LiteBro, которого больше нет. Выберите для них браузер " +
                "в Параметрах → Приложения по умолчанию: в поле поиска введите http.", MessageBoxIcon.Information);
            if (!silent) OpenSettings("ms-settings:defaultapps");
        }
        return 0;
    }

    /// <summary>
    /// Closes the browser's windows, as LiteBro or as the LiteBrowser it replaces, so their files can be
    /// replaced and the data folder moved; in silent mode only reports them.
    /// </summary>
    static bool CloseRunning()
    {
        var running = Process.GetProcessesByName(AppName).Concat(Process.GetProcessesByName(OldName)).Where(p =>
        {
            try
            {
                var file = p.MainModule!.FileName;
                return InOurFolders(file) || IsInside(file, OldInstallDir);
            }
            catch (Exception) { return false; }
        }).ToArray();
        if (running.Length == 0) return true;
        var name = running[0].ProcessName;
        if (silent)
        {
            Report(name + " запущен: закройте его и повторите.", MessageBoxIcon.Warning);
            return false;
        }
        var answer = MessageBox.Show(
            name + " запущен. Закрыть его и продолжить?\n\n" +
            "Программы проектов, которые он запустил (например, dsh), тоже остановятся, а текущая задача агента прервётся.",
            name, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (answer != DialogResult.OK) return false;
        foreach (var p in running) p.CloseMainWindow();
        if (running.Any(p => !p.WaitForExit(15000)))
        {
            Report(name + " не закрылся. Закройте его вручную и повторите.", MessageBoxIcon.Warning);
            return false;
        }
        Thread.Sleep(1000); // WebView2 processes let go of their files
        return true;
    }

    static bool HasWebView2()
    {
        foreach (var (hive, path) in new[]
        {
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\" + WebView2Client),
            (Registry.CurrentUser, @"Software\" + WebView2Client),
        })
        {
            using var key = hive.OpenSubKey(path);
            if (key?.GetValue("pv") is string version && version.Length > 0 && version != "0.0.0.0") return true;
        }
        return false;
    }

    /// <summary>False as well for what is no path at all: an empty registry value, say.</summary>
    static bool IsInside(string path, string dir)
    {
        try { return Path.GetFullPath(path).StartsWith(Path.GetFullPath(dir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException) { return false; }
    }

    /// <summary>In Program Files\LiteBro, or in the LiteBrowser folder of the versions up to 1.9.6.</summary>
    static bool InOurFolders(string path) => IsInside(path, InstallDir) || IsInside(path, LiteBrowserDir);

    static void Report(string message, MessageBoxIcon icon)
    {
        if (silent) File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        else MessageBox.Show(message, AppName, MessageBoxButtons.OK, icon);
    }
}

/// <summary>The installer's only window: icon, heading, text, options, two buttons.</summary>
sealed class Dialog : Form
{
    public Dialog(string title, string heading, string text, string okText, params CheckBox[] options)
    {
        Text = title;
        Icon = Program.AppIcon;
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(20, 18, 20, 14);

        var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        body.Controls.Add(new Label
        {
            Text = heading,
            Font = new Font("Segoe UI Semibold", 13f),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        });
        body.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(0, 0, 0, 12) });
        foreach (var option in options)
        {
            option.Margin = new Padding(0, 0, 0, 4);
            body.Controls.Add(option);
        }

        var ok = new Button { Text = okText, DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(100, 30) };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(100, 30) };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 14, 0, 0),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        var picture = new PictureBox
        {
            Image = new Icon(Program.AppIcon, 48, 48).ToBitmap(),
            SizeMode = PictureBoxSizeMode.AutoSize,
            Margin = new Padding(0, 2, 18, 0),
        };
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = 2 };
        layout.Controls.Add(picture, 0, 0);
        layout.Controls.Add(body, 1, 0);
        layout.Controls.Add(buttons, 1, 1);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}

/// <summary>
/// Windows lets only the user choose the program for a link type. This window opens LiteBro's own page
/// in Settings, names the three clicks to make there and closes itself once links go to LiteBro
/// (or, when it is being removed, back to the main browser).
/// </summary>
sealed class LinksStep : Form
{
    const string SettingsPage = "ms-settings:defaultapps?registeredAppMachine=" + Associations.AppName;
    const int TextWidth = 490;
    readonly Label status;
    readonly Button close;
    readonly System.Windows.Forms.Timer poll = new() { Interval = 500 };
    readonly bool giveBack;
    readonly string browser = Associations.OtherBrowserName();

    public LinksStep(bool giveBack)
    {
        this.giveBack = giveBack;
        Text = Associations.AppName;
        Icon = Program.AppIcon;
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        TopMost = true; // stays in sight beside Settings
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(20, 18, 20, 14);

        var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        body.Controls.Add(new Label
        {
            Text = giveBack ? "Последний шаг: вернуть ссылки" : "Последний шаг: ссылки",
            Font = new Font("Segoe UI Semibold", 13f),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        });
        body.Controls.Add(new Label
        {
            Text = giveBack
                ? "LiteBro удаляется, а ссылки http и https пока назначены ему. Выбрать для них программу " +
                    "Windows разрешает только вам. В открывшемся окне Параметров:\n\n" +
                    "1. Нажмите на плитку под заголовком HTTP (сейчас там LiteBro).\n" +
                    "2. В открывшемся окне выберите " + browser + ".\n" +
                    "3. Нажмите «Задать по умолчанию».\n\n" +
                    "Windows вернёт " + browser + " и HTTP, и HTTPS."
                : "Выбрать программу для ссылок Windows разрешает только вам. В открывшемся окне Параметров:\n\n" +
                    "1. Нажмите на плитку под заголовком HTTP (вероятно, сейчас там " + browser + ").\n" +
                    "2. В открывшемся окне выберите LiteBro.\n" +
                    "3. Нажмите «Задать по умолчанию».\n\n" +
                    "Windows отдаст LiteBro и HTTP, и HTTPS: локальные адреса он откроет сам, остальные сразу передаст " +
                    "в " + browser + ". Если " + browser + " потом предложит стать браузером по умолчанию, откажитесь: " +
                    "иначе он заберёт ссылки обратно.",
            AutoSize = true,
            MaximumSize = new Size(TextWidth, 0),
            Margin = new Padding(0, 0, 0, 10),
        });
        status = new Label
        {
            Text = "Жду вашего выбора…",
            Font = new Font("Segoe UI Semibold", 9f),
            AutoSize = true,
            MaximumSize = new Size(TextWidth, 0),
            Margin = new Padding(0),
        };
        body.Controls.Add(status);

        var reopen = new Button { Text = "Открыть Параметры", AutoSize = true, MinimumSize = new Size(100, 30) };
        reopen.Click += (_, _) => Program.OpenSettings(SettingsPage);
        close = new Button { Text = "Позже", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(100, 30) };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 14, 0, 0),
        };
        buttons.Controls.Add(close);
        buttons.Controls.Add(reopen);

        var picture = new PictureBox
        {
            Image = new Icon(Program.AppIcon, 48, 48).ToBitmap(),
            SizeMode = PictureBoxSizeMode.AutoSize,
            Margin = new Padding(0, 2, 18, 0),
        };
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = 2 };
        layout.Controls.Add(picture, 0, 0);
        layout.Controls.Add(body, 1, 0);
        layout.Controls.Add(buttons, 1, 1);
        Controls.Add(layout);
        CancelButton = close;
        poll.Tick += (_, _) => Check();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Bottom right, clear of the Settings window, which opens near the middle
        var area = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(area.Right - Width - 24, area.Bottom - Height - 24);
        Program.OpenSettings(SettingsPage);
        poll.Start();
    }

    /// <summary>The user's choice as Settings writes it, read fresh each time; the shell's answer as a fallback.</summary>
    static bool Chosen(string protocol)
    {
        if (Program.UserChoice(protocol) == Associations.ProgId) return true;
        return Associations.Handles(protocol);
    }

    void Check()
    {
        // Done: LiteBro has the protocol when installing, has it no longer when being removed
        bool http = Chosen("http") != giveBack, https = Chosen("https") != giveBack;
        if (http != https)
        {
            // Windows switched only one of them this time: the same three clicks on the other tile
            status.Text = (http ? "HTTP" : "HTTPS") + " готово. Теперь то же самое для плитки " + (http ? "HTTPS." : "HTTP.");
            return;
        }
        if (!http) return;
        poll.Stop();
        status.Text = giveBack ? "Готово: ссылки снова у " + browser + "." : "Готово: ссылки идут через LiteBro.";
        close.Text = "Готово";
        close.DialogResult = DialogResult.OK;
        var done = new System.Windows.Forms.Timer { Interval = 1500 };
        done.Tick += (_, _) =>
        {
            done.Dispose();
            DialogResult = DialogResult.OK;
        };
        done.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) poll.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>.lnk files through the shell's own IShellLink.</summary>
static class Shortcut
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, int flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    // PROPVARIANT holding a string: type tag at 0, pointer at 8, 24 bytes in all on x64
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    /// <param name="appUserModelId">When set, Windows treats the shortcut and the windows of the app as one app.</param>
    public static void Create(string link, string target, string description, string? appUserModelId = null)
    {
        var shell = (IShellLinkW)new ShellLink();
        var text = IntPtr.Zero;
        try
        {
            shell.SetPath(target);
            shell.SetWorkingDirectory(Path.GetDirectoryName(target)!);
            shell.SetDescription(description);
            shell.SetIconLocation(target, 0);
            if (appUserModelId != null)
            {
                // PKEY_AppUserModel_ID
                var key = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = 5 };
                text = Marshal.StringToCoTaskMemUni(appUserModelId);
                var value = new PropVariant { Type = 31 /* VT_LPWSTR */, Pointer = text };
                var store = (IPropertyStore)shell;
                store.SetValue(ref key, ref value);
                store.Commit();
            }
            ((IPersistFile)shell).Save(link, true);
        }
        finally
        {
            if (text != IntPtr.Zero) Marshal.FreeCoTaskMem(text);
            Marshal.ReleaseComObject(shell);
        }
    }

    public static string? Target(string link)
    {
        if (!File.Exists(link)) return null;
        var shell = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)shell).Load(link, 0);
            var path = new StringBuilder(260);
            shell.GetPath(path, path.Capacity, IntPtr.Zero, 0);
            // A shortcut to a shell item, such as the File Explorer pin on the taskbar, has no file
            return path.Length > 0 ? path.ToString() : null;
        }
        catch (COMException) { return null; }
        finally { Marshal.ReleaseComObject(shell); }
    }
}
