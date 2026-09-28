using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace LiteBro;

/// <summary>settings.ini: key = value lines, # comments. Read once at startup.</summary>
sealed class Settings
{
    static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly string OldDir = Path.Combine(LocalAppData, Associations.OldAppName);
    /// <summary>The user's data: %LOCALAPPDATA%\LiteBro, or LiteBrowser's folder while it cannot be moved yet.</summary>
    public static string Dir { get; private set; } = Path.Combine(LocalAppData, Associations.AppName);
    public static string IniPath => Path.Combine(Dir, "settings.ini");
    public static string LogPath => Path.Combine(Dir, "harness.log");
    static string WindowPath => Path.Combine(Dir, "window.txt");

    /// <summary>
    /// LiteBrowser, as this browser was called up to 1.9.6, kept the tiles, icons, logs and the WebView2 profile
    /// with the sign-ins in a folder of that name. The first start as LiteBro moves it over whole, which on the same
    /// disk is a rename. Should something still hold its files, this start uses it where it is and a later one
    /// tries again. Called first thing, before anything reads Dir.
    /// </summary>
    public static void MoveOldData()
    {
        if (Directory.Exists(Dir) || !Directory.Exists(OldDir)) return;
        try
        {
            // A running LiteBro works in the old folder: this launch only hands it an address
            if (!SingleInstance.PrimaryRunning())
            {
                // The installer has just closed LiteBrowser: its WebView2 processes may take a few seconds to let go
                for (int i = 0; i < 20; i++)
                {
                    try
                    {
                        Directory.Move(OldDir, Dir);
                        return;
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { Thread.Sleep(250); }
                }
            }
        }
        catch (Exception) { }
        // Never an empty new folder instead: once it exists, the old one would not be moved any more
        Dir = OldDir;
    }

    public string SearchUrl = "https://www.google.com/search?q=";
    public string SearchCountry = "";
    public string Theme = "auto";
    public bool Gpu = true;
    public string ExtraBrowserArgs = "";
    public string LocalHosts = "";
    public string OtherBrowser = "";
    /// <summary>«Только localhost»: requests to the internet are blocked (see NetGuard).</summary>
    public bool LocalOnly;
    /// <summary>Journal the requests to the internet while LocalOnly is off too.</summary>
    public bool NetJournal;
    /// <summary>Hosts let through in LocalOnly mode (CDN, API), through spaces.</summary>
    public string AllowHosts = "";
    /// <summary>With LocalOnly: the engine starts with --disable-web-security (no CORS, no same-origin checks).</summary>
    public bool IgnoreCors;

    static readonly string[] Keys =
    {
        "searchurl", "searchcountry", "theme", "gpu", "extrabrowserargs", "localhosts", "otherbrowser", "localonly", "netjournal", "allowhosts", "ignorecors",
    };

    public static Settings Load()
    {
        Directory.CreateDirectory(Dir);
        var s = new Settings();
        if (!File.Exists(IniPath))
        {
            s.Save();
            return s;
        }
        var seen = new HashSet<string>();
        foreach (var raw in File.ReadAllLines(IniPath))
        {
            var line = raw.Trim();
            int eq = line.IndexOf('=');
            if (line.StartsWith("#") || eq <= 0) continue;
            var value = line.Substring(eq + 1).Trim();
            var key = line.Substring(0, eq).Trim().ToLowerInvariant();
            seen.Add(key);
            switch (key)
            {
                case "searchurl": s.SearchUrl = value; break;
                case "searchcountry": s.SearchCountry = value.ToUpperInvariant(); break;
                case "theme":
                    var t = value.ToLowerInvariant();
                    s.Theme = t is "dark" or "light" ? t : "auto";
                    break;
                case "gpu": s.Gpu = IsTrue(value); break;
                case "extrabrowserargs": s.ExtraBrowserArgs = value; break;
                case "localhosts": s.LocalHosts = value; break;
                case "otherbrowser": s.OtherBrowser = value; break;
                case "localonly": s.LocalOnly = IsTrue(value); break;
                case "netjournal": s.NetJournal = IsTrue(value); break;
                case "allowhosts": s.AllowHosts = value; break;
                case "ignorecors": s.IgnoreCors = IsTrue(value); break;
            }
        }
        // A file from an older version lacks the newer keys or has ones since dropped: rewrite it, keeping the values
        if (!seen.All(Keys.Contains))
        {
            // Dropped keys go with the rewrite: the old file stays beside it
            try { File.Copy(IniPath, IniPath + ".bak", true); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }
        if (!Keys.All(seen.Contains) || !seen.All(Keys.Contains)) s.Save();
        return s;
    }

    /// <summary>Remembers the country of the search button: the file is written anew with the values read at startup.</summary>
    public void SaveSearchCountry(string code)
    {
        SearchCountry = code;
        Save();
    }

    /// <summary>Remembers the network switches and the allowed hosts (from the start page and /net).</summary>
    public void SaveNet(bool localOnly, bool journal, string allowHosts, bool ignoreCors)
    {
        LocalOnly = localOnly;
        IgnoreCors = ignoreCors;
        NetJournal = journal;
        AllowHosts = allowHosts;
        Save();
    }

    void Save()
    {
        try { File.WriteAllText(IniPath, DefaultText(), Encoding.UTF8); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
    }

    static bool IsTrue(string v) =>
        v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase);

    static string Bool(bool b) => b ? "true" : "false";

    string DefaultText() => string.Join(Environment.NewLine,
        "# LiteBro. Файл читается при запуске: правь, когда все окна браузера закрыты.",
        "",
        "# Проекты стартовой страницы (кнопка ⌂ и Alt+Home) хранятся рядом, в projects.json",
        "",
        "# Куда уходит текст из адресной строки, если это не адрес",
        "SearchUrl = " + SearchUrl,
        "# Страна поиска Google: код из кнопки справа в адресной строке (DE, US…), добавляет к поиску gl и hl.",
        "# Пусто = как обычно. Кнопка сама меняет эту строку.",
        "SearchCountry = " + SearchCountry,
        "",
        "# Оформление: auto = как «Режим приложения» в Windows, dark = тёмное, light = светлое",
        "Theme = " + Theme,
        "",
        "# --- Движок (Edge WebView2) ---",
        "# false = рисовать без видеокарты; нужно только при проблемах с драйвером",
        "Gpu = " + Bool(Gpu),
        "# Дополнительные флаги Chromium через пробел, к встроенным флагам экономии памяти",
        "ExtraBrowserArgs = " + ExtraBrowserArgs,
        "",
        "# --- Ссылки из других программ ---",
        "# Здесь открываются: localhost, *.localhost, 127.x.x.x, [::1] и имена, которые файл hosts",
        "# направляет на 127.0.0.1. Адрес 0.0.0.0 открывается как 127.0.0.1.",
        "# Свои имена через пробел, можно со звёздочкой, например: *.test 192.168.1.*",
        "LocalHosts = " + LocalHosts,
        "# Куда сразу уходит всё остальное. Пусто = браузер, который открывает файлы .html",
        "# Пример: \"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" --single-argument %1",
        "OtherBrowser = " + OtherBrowser,
        "",
        "# --- Сеть ---",
        "# Режим «только localhost»: страницы не ходят в интернет, только на этот компьютер и в локальную сеть.",
        "# Переключатель справа на стартовой странице меняет эту строку.",
        "LocalOnly = " + Bool(LocalOnly),
        "# Журнал запросов в интернет (logs\\network.log) и при выключенном режиме; при включённом пишется всегда",
        "NetJournal = " + Bool(NetJournal),
        "# Что пропускать в режиме «только localhost», через пробел, например: fonts.googleapis.com fonts.gstatic.com",
        "AllowHosts = " + AllowHosts,
        "# Только вместе с LocalOnly: браузер не проверяет CORS и same-origin (флаг --disable-web-security)",
        "IgnoreCors = " + Bool(IgnoreCors),
        "");

    public static (Rectangle Bounds, bool Maximized, double Zoom)? LoadWindow()
    {
        try
        {
            var p = File.ReadAllText(WindowPath).Split(' ');
            var n = p.Take(4).Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            return (new Rectangle(n[0], n[1], n[2], n[3]), p[4] == "1", double.Parse(p[5], CultureInfo.InvariantCulture));
        }
        catch (Exception) { return null; }
    }

    public static void SaveWindow(Rectangle b, bool maximized, double zoom)
    {
        try
        {
            File.WriteAllText(WindowPath, FormattableString.Invariant(
                $"{b.X} {b.Y} {b.Width} {b.Height} {(maximized ? 1 : 0)} {zoom}"));
        }
        catch (IOException) { }
    }
}
