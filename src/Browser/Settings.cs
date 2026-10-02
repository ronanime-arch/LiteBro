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
    public string Theme = "dark";
    /// <summary>The interface language: auto (as Windows), ru, en.</summary>
    public string Language = "auto";
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
    /// <summary>Cookies and the HTTP cache are deleted when the last window closes, but for projects with KeepData.</summary>
    public bool ClearOnExit;
    /// <summary>A page on screen loads again when a file in its project's folder (or its own folder, for a file) changes.</summary>
    public bool AutoReload;
    /// <summary>Tracking prevention at its strict level (Edge's «Строгая»); else balanced, the engine's default.</summary>
    public bool StrictTracking;
    /// <summary>Ads and trackers blocked by uBlock Origin Lite (AdBlock.cs); off by default: switched on, it downloads itself.</summary>
    public bool AdBlock;
    /// <summary>A click on a tab's speaker mutes it; off by default.</summary>
    public bool TabMute;
    /// <summary>Certificate errors of servers on this machine are let through; off by default.</summary>
    public bool TrustLocalCerts;
    /// <summary>Tabs in the background are paused after SuspendAfter minutes and closed after UnloadAfter (0 = never).</summary>
    public bool FreezeTabs = true;
    public int SuspendAfter = 1, UnloadAfter = 5;
    /// <summary>Buttons and tools switched off on the «Для разработчика» page (Dev.Ids), through spaces; the developer tools by default.</summary>
    public string DevOff = Dev.DefaultOff;
    /// <summary>Which defaults the file was written with: an older file gets the values that changed since (Load).</summary>
    public int Defaults = CurrentDefaults;
    const int CurrentDefaults = 2;

    static readonly string[] Keys =
    {
        "searchurl", "searchcountry", "theme", "language", "gpu", "extrabrowserargs", "localhosts", "otherbrowser", "localonly", "netjournal", "allowhosts", "ignorecors", "clearonexit", "autoreload", "stricttracking", "adblock", "tabmute", "trustlocalcerts", "freezetabs", "suspendafter", "unloadafter", "disabled", "defaults",
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
        s.Defaults = 0; // a file without the line is older than it
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
                    s.Theme = t is "auto" or "light" ? t : "dark";
                    break;
                case "language":
                    var lang = value.ToLowerInvariant();
                    s.Language = lang is "ru" or "en" ? lang : "auto";
                    break;
                case "gpu": s.Gpu = IsTrue(value); break;
                case "extrabrowserargs": s.ExtraBrowserArgs = value; break;
                case "localhosts": s.LocalHosts = value; break;
                case "otherbrowser": s.OtherBrowser = value; break;
                case "localonly": s.LocalOnly = IsTrue(value); break;
                case "netjournal": s.NetJournal = IsTrue(value); break;
                case "allowhosts": s.AllowHosts = value; break;
                case "ignorecors": s.IgnoreCors = IsTrue(value); break;
                case "clearonexit": s.ClearOnExit = IsTrue(value); break;
                case "autoreload": s.AutoReload = IsTrue(value); break;
                case "stricttracking": s.StrictTracking = IsTrue(value); break;
                case "adblock": s.AdBlock = IsTrue(value); break;
                case "tabmute": s.TabMute = IsTrue(value); break;
                case "trustlocalcerts": s.TrustLocalCerts = IsTrue(value); break;
                case "freezetabs": s.FreezeTabs = IsTrue(value); break;
                case "suspendafter": s.SuspendAfter = Minutes(value, s.SuspendAfter, 1); break;
                case "unloadafter": s.UnloadAfter = Minutes(value, s.UnloadAfter, 0); break;
                case "disabled": s.DevOff = value; break;
                case "defaults": s.Defaults = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : 0; break;
            }
        }
        L.Set(s.Language); // before a rewrite below: its comments are in the language
        // 2: ad blocking was on by default for a while, and a Disabled line written before «Не блокировать (uBlock)»
        // existed does not name it; both are off by default now
        if (s.Defaults < 2)
        {
            s.AdBlock = false;
            if (seen.Contains("disabled") && !Dev.Parse(s.DevOff).Contains("unblock")) s.DevOff = (s.DevOff + " unblock").Trim();
        }
        bool migrated = s.Defaults < CurrentDefaults;
        s.Defaults = CurrentDefaults;
        // A file from an older version lacks the newer keys or has ones since dropped: rewrite it, keeping the values
        if (!seen.All(Keys.Contains))
        {
            // Dropped keys go with the rewrite: the old file stays beside it
            try { File.Copy(IniPath, IniPath + ".bak", true); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }
        if (migrated || !Keys.All(seen.Contains) || !seen.All(Keys.Contains)) s.Save();
        return s;
    }

    /// <summary>Remembers the country of the search button: the file is written anew with the values read at startup.</summary>
    /// <summary>Remembers the interface language (the developer page).</summary>
    public void SaveLanguage(string language)
    {
        Language = language;
        L.Set(language);
        Save();
    }

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

    /// <summary>Remembers the switch of clearing cookies and cache on exit (/net).</summary>
    public void SaveClearOnExit(bool on)
    {
        ClearOnExit = on;
        Save();
    }

    /// <summary>Remembers the switch of reloading pages when their project's files change (/net).</summary>
    public void SaveAutoReload(bool on)
    {
        AutoReload = on;
        Save();
    }

    /// <summary>Remembers what is switched off on the «Для разработчика» page.</summary>
    public void SaveDevOff(string off)
    {
        DevOff = off;
        Save();
    }

    /// <summary>
    /// Every value back to what a new install has, keeping the file as it was in settings.ini.bak.
    /// Tiles, mocks, cookies and the rest of the user's data are other files and stay.
    /// </summary>
    public void ResetDefaults()
    {
        try { if (File.Exists(IniPath)) File.Copy(IniPath, IniPath + ".bak", true); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        var d = new Settings();
        foreach (var f in typeof(Settings).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            f.SetValue(this, f.GetValue(d));
        Save();
    }

    void Save()
    {
        try { File.WriteAllText(IniPath, DefaultText(), Encoding.UTF8); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
    }

    public const int MaxMinutes = 1440;

    /// <summary>A number of minutes from min up to a day; the old value for anything else.</summary>
    public static int Minutes(string v, int old, int min) =>
        int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= min && n <= MaxMinutes ? n : old;

    /// <summary>Changes values of the «Для разработчика» page and writes the file.</summary>
    public void Change(Action<Settings> change)
    {
        change(this);
        Save();
    }

    static bool IsTrue(string v) =>
        v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase);

    static string Bool(bool b) => b ? "true" : "false";

    string DefaultText() => string.Join(Environment.NewLine,
        L.T("# LiteBro. Файл читается при запуске: правь, когда все окна браузера закрыты."),
        "",
        L.T("# Проекты стартовой страницы (кнопка ⌂ и Alt+Home) хранятся рядом, в projects.json"),
        "",
        L.T("# Куда уходит текст из адресной строки, если это не адрес"),
        "SearchUrl = " + SearchUrl,
        L.T("# Страна поиска Google: код из кнопки справа в адресной строке (DE, US…), добавляет к поиску gl и hl."),
        L.T("# Пусто = как обычно. Кнопка сама меняет эту строку."),
        "SearchCountry = " + SearchCountry,
        "",
        L.T("# Оформление: dark = тёмное, light = светлое, auto = как «Режим приложения» в Windows"),
        "Theme = " + Theme,
        "",
        L.T("# Язык: auto = как в Windows, ru = русский, en = английский. Меняется на странице «Для разработчика»."),
        "Language = " + Language,
        "",
        L.T("# --- Движок (Edge WebView2) ---"),
        L.T("# false = рисовать без видеокарты; нужно только при проблемах с драйвером"),
        "Gpu = " + Bool(Gpu),
        L.T("# Дополнительные флаги Chromium через пробел, к встроенным флагам экономии памяти"),
        "ExtraBrowserArgs = " + ExtraBrowserArgs,
        "",
        L.T("# --- Ссылки из других программ ---"),
        L.T("# Здесь открываются: localhost, *.localhost, 127.x.x.x, [::1] и имена, которые файл hosts"),
        L.T("# направляет на 127.0.0.1. Адрес 0.0.0.0 открывается как 127.0.0.1."),
        L.T("# Свои имена через пробел, можно со звёздочкой, например: *.test 192.168.1.*"),
        "LocalHosts = " + LocalHosts,
        L.T("# Куда сразу уходит всё остальное. Пусто = браузер, который открывает файлы .html"),
        L.T("# Пример: \"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" --single-argument %1"),
        "OtherBrowser = " + OtherBrowser,
        "",
        L.T("# --- Сеть ---"),
        L.T("# Режим «только localhost»: страницы не ходят в интернет, только на этот компьютер и в локальную сеть."),
        L.T("# Переключатель справа на стартовой странице меняет эту строку."),
        "LocalOnly = " + Bool(LocalOnly),
        L.T("# Журнал запросов в интернет (logs\\network.log) и при выключенном режиме; при включённом пишется всегда"),
        "NetJournal = " + Bool(NetJournal),
        L.T("# Что пропускать в режиме «только localhost», через пробел, например: fonts.googleapis.com fonts.gstatic.com"),
        "AllowHosts = " + AllowHosts,
        L.T("# Только вместе с LocalOnly: браузер не проверяет CORS и same-origin (флаг --disable-web-security)"),
        "IgnoreCors = " + Bool(IgnoreCors),
        L.T("# Удалять cookies и кэш, когда закрывается последнее окно (кроме проектов с «Не удалять при выходе»)"),
        "ClearOnExit = " + Bool(ClearOnExit),
        L.T("# Обновлять страницу на экране, когда меняется файл в папке её проекта (или в папке открытого файла)"),
        "AutoReload = " + Bool(AutoReload),
        L.T("# Строгая защита от трекеров: блокируется больше счётчиков и рекламы, изредка ломается вход через другой сайт"),
        "StrictTracking = " + Bool(StrictTracking),
        L.T("# Блокировать рекламу и трекеры uBlock Origin Lite; при первом включении скачивается с GitHub"),
        "AdBlock = " + Bool(AdBlock),
        L.T("# Доверять сертификатам серверов на этом компьютере (localhost, *.localhost, 127.x, ::1)"),
        "TrustLocalCerts = " + Bool(TrustLocalCerts),
        "",
        L.T("# --- Вкладки ---"),
        L.T("# Клик по значку звука на вкладке выключает её звук"),
        "TabMute = " + Bool(TabMute),
        L.T("# Фоновые вкладки: приостановить через SuspendAfter минут, выгрузить из памяти через UnloadAfter (0 = не выгружать)"),
        "FreezeTabs = " + Bool(FreezeTabs),
        "SuspendAfter = " + SuspendAfter.ToString(CultureInfo.InvariantCulture),
        "UnloadAfter = " + UnloadAfter.ToString(CultureInfo.InvariantCulture),
        "",
        L.T("# --- Для разработчика ---"),
        L.T("# Что выключено на странице «Для разработчика» (кнопка у правого края стартовой страницы), через пробел."),
        L.T("# После установки выключены инструменты разработчика: ") + Dev.DefaultOff + L.T(". Пусто = всё включено."),
        L.T("# Саму страницу выключить нельзя."),
        "Disabled = " + DevOff,
        L.T("# Служебная строка, не меняйте"),
        "Defaults = " + Defaults.ToString(CultureInfo.InvariantCulture),
        "");

    static string PinnedPath => Path.Combine(Dir, "pinned.txt");

    /// <summary>The pinned tabs, in order: an address, or «term» and a folder for a terminal.</summary>
    public static List<string[]> LoadPinned()
    {
        try
        {
            return File.ReadAllLines(PinnedPath, Encoding.UTF8).Where(l => l.Trim().Length > 0).Select(l => l.Split('\t')).ToList();
        }
        catch (Exception) { return new List<string[]>(); }
    }

    public static void SavePinned(IEnumerable<string[]> pinned)
    {
        try { File.WriteAllLines(PinnedPath, pinned.Select(p => string.Join("\t", p)), Encoding.UTF8); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
    }

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
