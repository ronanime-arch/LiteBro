using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace LiteBro;

/// <summary>
/// uBlock Origin Lite (AdBlock in settings.ini, off by default, the switch on the «Для разработчика» page). It is not
/// in the installer: the first time it is switched on (and «только localhost» is off, the internet being what it is for),
/// the browser downloads the pinned release from GitHub, checks its files against the release's git tree and keeps them
/// in the user's data, where the engine may write its indexed filter lists into the extension's folder. Every profile
/// gets it on its first WebView of the session. uBOL filters declaratively: the engine applies the lists.
/// </summary>
static class AdBlock
{
    public const string Name = "uBlock Origin Lite";

    // The pinned release. A newer one: its tag, its commit in uBOL-home, and the git tree of its chromium folder
    // without log.txt (the release's build log, left out of the check)
    const string Version = "2025.831.1814";
    const string Tag = "uBOLite_" + Version;
    const string Commit = "c435af056fcd1f0ad2096d9a4dd870d9bf1efa94";
    const string Tree = "3749b2deb3f8189b5ce532c62e9f8e494429b6d3";
    static readonly string[] Sources =
    {
        "https://github.com/uBlockOrigin/uBOL-home/releases/download/" + Tag + "/" + Tag + ".chromium.mv3.zip",
        // The same files as the repository has them at the release's commit, should the package differ (twice the size)
        "https://codeload.github.com/uBlockOrigin/uBOL-home/zip/" + Commit,
    };
    const long MaxDownload = 64L << 20, MaxFiles = 160L << 20;

    static string Folder => Path.Combine(Settings.Dir, "ublock");
    // A checked download waiting for the next start: the engine holds the version in use
    static string Staged => Folder + ".new";

    static readonly HttpClient Http = new(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>What the «Для разработчика» page says under the switch: "" when ready or off.</summary>
    public static string State { get; private set; } = "";

    static Task<string?>? downloading;

    /// <summary>
    /// Before the engine starts, while nothing holds the extension: a version downloaded last session takes its place,
    /// and a copy from before the settings page was trimmed gets trimmed (the engine adds it again as changed).
    /// </summary>
    public static Task PrepareAsync() => Task.Run(() =>
    {
        Trim(Folder);
        if (VersionIn(Staged) != Version) return;
        string old = Folder + ".old";
        try
        {
            Delete(old);
            if (Directory.Exists(Folder)) Directory.Move(Folder, old);
            Directory.Move(Staged, Folder);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { } // the next start, then
        Delete(old);
    });

    /// <summary>Downloads the pinned version if it is wanted and missing; a first install is put in at once.</summary>
    public static async void Ensure()
    {
        if (downloading != null) return;
        if (!App.Current.S.AdBlock || VersionIn(Folder) == Version || VersionIn(Staged) == Version)
        {
            Show(App.Current.S.AdBlock && VersionIn(Staged) == Version ? L.T("Новая версия скачана, включится после перезапуска LiteBro.") : "");
            return;
        }
        if (NetGuard.LocalOnly)
        {
            Show(L.T("Скачается с GitHub, когда будет выключен режим «только localhost»."));
            return;
        }
        Show(L.T("Скачивается с GitHub, около 12 МБ…"));
        string? problem;
        try { problem = await (downloading = Task.Run(DownloadAsync)); }
        finally { downloading = null; }
        if (problem != null)
        {
            Show(L.T("Не удалось скачать: ") + problem + L.T(". Попробует снова при следующем включении или запуске."));
            return;
        }
        if (!Directory.Exists(Folder))
        {
            try { Directory.Move(Staged, Folder); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }
        Show(VersionIn(Folder) == Version ? "" : L.T("Новая версия скачана, включится после перезапуска LiteBro."));
        Changed();
    }

    static void Show(string state)
    {
        if (State == state) return;
        State = state;
        App.Current.ApplyNet(); // the pages show it
    }

    /// <summary>Tries each source in turn; the files go to Staged only if their git tree is the release's. Null when done, else what went wrong.</summary>
    static async Task<string?> DownloadAsync()
    {
        string temp = Folder + ".download", zip = temp + ".zip", problem = "";
        foreach (var url in Sources)
        {
            try
            {
                Delete(temp);
                await FetchAsync(url, zip);
                var root = Extract(zip, temp);
                File.Delete(Path.Combine(root, "log.txt"));
                if (TreeOf(root) != Tree)
                {
                    problem = L.T("файлы не совпали с выпуском на GitHub");
                    continue;
                }
                Trim(root);
                Delete(Staged);
                Directory.Move(root, Staged);
                return null;
            }
            catch (Exception e) { problem = e.Message; } // no network, a refused address, a full disk…
            finally
            {
                Delete(temp);
                try { File.Delete(zip); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }
        }
        return problem;
    }

    static async Task FetchAsync(string url, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("LiteBro");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxDownload) throw new InvalidDataException(L.T("слишком большой файл"));
        using var from = await response.Content.ReadAsStreamAsync();
        using var to = File.Create(path);
        var buffer = new byte[81920];
        long total = 0;
        int n;
        while ((n = await from.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            if ((total += n) > MaxDownload) throw new InvalidDataException(L.T("слишком большой файл"));
            await to.WriteAsync(buffer, 0, n);
        }
    }

    /// <summary>
    /// The extension's folder of the archive into a folder of its own: the release has it at the top (or in one folder),
    /// the repository's archive in chromium; returns where its manifest.json is.
    /// </summary>
    static string Extract(string zip, string to)
    {
        using var archive = ZipFile.OpenRead(zip);
        var manifest = archive.Entries.Where(e => e.FullName == "manifest.json" || e.FullName.EndsWith("/manifest.json"))
            .OrderBy(e => e.FullName.Count(c => c == '/')).ThenBy(e => e.FullName.Contains("/chromium/") ? 0 : 1)
            .FirstOrDefault() ?? throw new InvalidDataException(L.T("в архиве нет расширения"));
        var prefix = manifest.FullName.Substring(0, manifest.FullName.Length - "manifest.json".Length);
        var root = Path.GetFullPath(to) + "\\";
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(prefix) || entry.FullName.EndsWith("/")) continue;
            var path = Path.GetFullPath(Path.Combine(root, entry.FullName.Substring(prefix.Length).Replace('/', '\\')));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(L.T("недопустимый путь в архиве"));
            if ((total += entry.Length) > MaxFiles) throw new InvalidDataException(L.T("слишком большой файл"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path);
        }
        return to;
    }

    // WebView2 shows no toolbar icon and no popup of an extension. What only they reach goes from the copy; a file still
    // imported by a module that stays is kept, so a newer uBOL that needs one of them keeps working.
    static readonly string[] Unused =
    {
        "js/popup.js", "css/popup.css", // popup.html itself stays: the manifest names it
        "picker-ui.html", "js/picker-ui.js", "css/picker-ui.css", "js/scripting/picker.js",
        "unpicker-ui.html", "js/unpicker-ui.js", "css/unpicker-ui.css", "js/scripting/unpicker.js",
        "zapper-ui.html", "js/zapper-ui.js", "css/zapper-ui.css", "js/scripting/zapper.js",
        "js/tool-overlay-ui.js", "css/tool-overlay-ui.css", "js/scripting/tool-overlay.js",
        "report.html", "js/report.js", "css/report.css",
        "matched-rules.html", "js/matched-rules.js", "css/matched-rules.css",
    };

    // Its settings' «Поведение» block (a count on the toolbar icon, a reload when the popup changes a site's mode) does
    // nothing here: hidden, its markup stays for settings.js
    const string TrimCss = "\n/* LiteBro: no toolbar icon or popup in WebView2 */\nsection[data-pane=\"settings\"] > div:has(#showBlockedCount) { display: none; }\n";

    /// <summary>An empty page of the extension: what uBOL takes only from its own pages is sent from there (AskAsync).</summary>
    const string Page = "litebro.html";

    // The lists' names are English in every language; in a Russian interface they get Russian ones (names of lists stay)
    static readonly Dictionary<string, string> RussianNames = new()
    {
        ["ublock-filters"] = "Фильтры uBlock – реклама, трекеры и прочее",
        ["pgl"] = "Peter Lowe – реклама, трекеры и прочее",
        ["ublock-badware"] = "Фильтры uBlock – опасное ПО",
        ["urlhaus-full"] = "Вредоносные адреса (URLhaus)",
        ["adguard-mobile"] = "AdGuard/uBO – реклама в мобильных версиях",
        ["block-lan"] = "Защита локальной сети от внешних сайтов",
        ["dpollock-0"] = "Файл hosts Дэна Поллока",
        ["adguard-spyware-url"] = "AdGuard – отслеживание через адреса",
        ["annoyances-cookies"] = "EasyList/uBO – уведомления о cookie",
        ["annoyances-overlays"] = "EasyList/uBO – всплывающие окна поверх страницы",
        ["annoyances-social"] = "EasyList – виджеты соцсетей",
        ["annoyances-widgets"] = "EasyList – виджеты чатов",
        ["annoyances-others"] = "EasyList – прочие раздражители",
        ["annoyances-notifications"] = "EasyList – запросы уведомлений",
        ["ublock-experimental"] = "Фильтры uBlock – экспериментальные",
        ["stevenblack-hosts"] = "Сводный hosts Стивена Блэка (реклама и вредоносное ПО)",
        ["ubol-tests"] = "Тестовые фильтры uBO Lite",
        ["rus-1"] = "🇷🇺ru 🇺🇦ua 🇺🇿uz 🇰🇿kz: RU AdList: счётчики",
    };

    /// <summary>
    /// The copy as LiteBro uses it: what does nothing in WebView2 gone or hidden, the page to send from, Russian names
    /// of the lists. Writes only what is not so yet: a change makes the engine take the extension as a new one.
    /// </summary>
    static void Trim(string dir)
    {
        if (!File.Exists(Path.Combine(dir, "manifest.json"))) return;
        try
        {
            var gone = Unused.Where(u => File.Exists(Path.Combine(dir, u.Replace('/', '\\')))).ToList();
            if (gone.Count > 0)
            {
                // The extension's own modules (not the filter lists' scripts) that stay
                var kept = Directory.GetFiles(Path.Combine(dir, "js"), "*.js", SearchOption.AllDirectories)
                    .Where(f => !Unused.Any(u => SameFile(f, dir, u))).Select(File.ReadAllText).ToList();
                foreach (var file in gone)
                {
                    // import ... from './name' or import('./name'): still needed
                    var name = Path.GetFileName(file);
                    if (kept.Any(js => Regex.IsMatch(js, "import[^;]*?['\"][^'\"]*/" + Regex.Escape(name) + "['\"]"))) continue;
                    File.Delete(Path.Combine(dir, file.Replace('/', '\\')));
                }
            }
            var css = Path.Combine(dir, "css", "settings.css");
            if (File.Exists(css) && !File.ReadAllText(css).Contains("LiteBro")) File.AppendAllText(css, TrimCss);
            var page = Path.Combine(dir, Page);
            if (!File.Exists(page)) File.WriteAllText(page, "<!doctype html><meta charset=\"utf-8\"><title>LiteBro</title>\n");
            if (L.Code == "ru") TranslateLists(Path.Combine(dir, "rulesets", "ruleset-details.json"));
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
    }

    static bool SameFile(string path, string dir, string relative) =>
        string.Equals(Path.GetFullPath(path), Path.GetFullPath(Path.Combine(dir, relative.Replace('/', '\\'))), StringComparison.OrdinalIgnoreCase);

    /// <summary>Each list's name by its id, as the file has them ("id": "…", "name": "…"); an unknown layout stays as it is.</summary>
    static void TranslateLists(string path)
    {
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path, Encoding.UTF8);
        var changed = text;
        foreach (var pair in RussianNames)
            changed = Regex.Replace(changed, "(\"id\":\\s*\"" + Regex.Escape(pair.Key) + "\",\\s*\"name\":\\s*\")[^\"]*(\")",
                m => m.Groups[1].Value + pair.Value + m.Groups[2].Value);
        if (changed != text) File.WriteAllText(path, changed, new UTF8Encoding(false));
    }

    /// <summary>The git tree id of a folder whose files are all plain (100644), as git computes it.</summary>
    static string TreeOf(string dir)
    {
        using var sha = SHA1.Create();
        return Hex(TreeOf(dir, sha) ?? Array.Empty<byte>());
    }

    static byte[]? TreeOf(string dir, SHA1 sha)
    {
        var entries = new List<(byte[] Key, byte[] Entry)>();
        foreach (var path in Directory.GetFileSystemEntries(dir))
        {
            var name = Encoding.UTF8.GetBytes(Path.GetFileName(path));
            bool folder = Directory.Exists(path);
            var id = folder ? TreeOf(path, sha) : Object(sha, "blob", File.ReadAllBytes(path));
            if (id == null) continue; // git keeps no empty folders
            var mode = Encoding.ASCII.GetBytes(folder ? "40000 " : "100644 ");
            // git orders by name, a folder's as if it ended in a slash
            var key = folder ? name.Concat(new[] { (byte)'/' }).ToArray() : name;
            entries.Add((key, mode.Concat(name).Concat(new byte[] { 0 }).Concat(id).ToArray()));
        }
        if (entries.Count == 0) return null;
        entries.Sort((a, b) => Compare(a.Key, b.Key));
        return Object(sha, "tree", entries.SelectMany(e => e.Entry).ToArray());
    }

    static byte[] Object(SHA1 sha, string kind, byte[] body)
    {
        var head = Encoding.ASCII.GetBytes(kind + " " + body.Length + "\0");
        sha.Initialize();
        sha.TransformBlock(head, 0, head.Length, null, 0);
        sha.TransformFinalBlock(body, 0, body.Length);
        return sha.Hash;
    }

    static int Compare(byte[] a, byte[] b)
    {
        for (int i = 0; i < a.Length && i < b.Length; i++)
            if (a[i] != b[i]) return a[i] - b[i];
        return a.Length - b.Length;
    }

    static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));

    static readonly Regex VersionPattern = new("\"version\"\\s*:\\s*\"([^\"]+)\"");

    static string? VersionIn(string dir)
    {
        try
        {
            var m = VersionPattern.Match(File.ReadAllText(Path.Combine(dir, "manifest.json")));
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
    }

    static void Delete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
    }

    // The profiles brought in line this session, by name ("" for the shared one)
    static readonly Dictionary<string, Task> applied = new();

    /// <summary>The first WebView of a profile this session: the extension is added if missing and follows the switch.</summary>
    public static void Apply(CoreWebView2Profile profile, string name)
    {
        if (!applied.ContainsKey(name)) applied[name] = ApplyAsync(profile, name);
    }

    static async Task ApplyAsync(CoreWebView2Profile profile, string name)
    {
        try { await SetAsync(profile, App.Current.S.AdBlock); }
        catch (Exception) { applied.Remove(name); } // the WebView closed meanwhile, say: the next one tries again
    }

    static async Task SetAsync(CoreWebView2Profile profile, bool on)
    {
        var ours = (await profile.GetBrowserExtensionsAsync()).Where(x => x.Name == Name).ToList();
        if (on && ours.Count == 0)
        {
            // Not downloaded yet: Ensure brings the profiles in line once it is
            if (VersionIn(Folder) == null) return;
            ours.Add(await profile.AddBrowserExtensionAsync(Folder));
        }
        foreach (var x in ours)
            if (x.IsEnabled != on) await x.EnableAsync(on);
    }

    /// <summary>The switch changed (or the extension came): the profiles with a WebView follow now, the others on their next one.</summary>
    public static void Changed()
    {
        applied.Clear();
        foreach (var form in App.Current.Forms)
            foreach (var (name, core) in form.LiveProfiles())
                Apply(core.Profile, name);
        Ensure();
    }

    /// <summary>
    /// Runs a script in a page of uBOL in a hidden WebView of the profile: some messages uBOL takes only from its own pages
    /// (a site's filtering mode, as its popup sets it). The script's awaited value as JSON; null if that could not be done.
    /// </summary>
    static async Task<string?> AskAsync(IntPtr window, string profile, string script)
    {
        if (App.Current.Env is not { } env) return null;
        CoreWebView2Controller? c = null;
        try
        {
            c = await App.Current.CreateControllerAsync(env, window, profile, pages: false);
            c.IsVisible = false;
            var core = c.CoreWebView2;
            var x = (await core.Profile.GetBrowserExtensionsAsync()).FirstOrDefault(e => e.Name == Name && e.IsEnabled);
            if (x == null) return null;
            var loaded = new TaskCompletionSource<bool>();
            core.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            core.Navigate("chrome-extension://" + x.Id + "/" + Page);
            if (await Task.WhenAny(loaded.Task, Task.Delay(10000)) != loaded.Task || !loaded.Task.Result) return null;
            var call = core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", ProjectStore.Json.Serialize(new Dictionary<string, object>
            {
                ["expression"] = "(async () => JSON.stringify(await (" + script + ")))()",
                ["awaitPromise"] = true,
                ["returnByValue"] = true,
            }));
            if (await Task.WhenAny(call, Task.Delay(10000)) != call) return null;
            var answer = ProjectStore.Json.Deserialize<Dictionary<string, object>>(await call);
            return answer.TryGetValue("result", out var r) && r is Dictionary<string, object> result
                && result.TryGetValue("value", out var v) && v is string json ? json : null;
        }
        catch (Exception) { return null; } // the engine restarting, a WebView gone
        finally { c?.Close(); }
    }

    static string Send(object message) => "chrome.runtime.sendMessage(" + ProjectStore.Json.Serialize(message) + ")";

    // Sites last seen left unfiltered (or not), by profile and host: the tools menu shows them at once
    static readonly Dictionary<string, bool> unfiltered = new();

    public static bool? Unfiltered(string profile, string host) => unfiltered.TryGetValue(profile + "|" + host, out var off) ? off : null;

    /// <summary>Asks uBOL whether it leaves the site unfiltered (its «без фильтрации» mode); null if it did not say.</summary>
    public static async Task<bool?> IsUnfilteredAsync(IntPtr window, string profile, string host)
    {
        var answer = await AskAsync(window, profile, Send(new Dictionary<string, object> { ["what"] = "getFilteringMode", ["hostname"] = host }));
        if (!int.TryParse(answer, out var level)) return null;
        return unfiltered[profile + "|" + host] = level == 0;
    }

    /// <summary>The site left unfiltered, or back to the default filtering mode, as uBOL's popup does; false if it did not take.</summary>
    public static async Task<bool> SetUnfilteredAsync(IntPtr window, string profile, string host, bool off)
    {
        // Back on: the mode every site has by default
        var level = off ? "0" : "await chrome.runtime.sendMessage({ what: 'getDefaultFilteringMode' })";
        var answer = await AskAsync(window, profile, "(async () => chrome.runtime.sendMessage({ what: 'setFilteringMode', hostname: "
            + ProjectStore.Json.Serialize(host) + ", level: " + level + " }))()");
        if (!int.TryParse(answer, out var now) || (now == 0) != off) return false;
        unfiltered[profile + "|" + host] = off;
        return true;
    }

    /// <summary>uBOL's own settings page (filter lists, sites left unfiltered) in that profile; null while it is not on there.</summary>
    public static async Task<string?> DashboardAsync(CoreWebView2Profile profile)
    {
        try
        {
            var x = (await profile.GetBrowserExtensionsAsync()).FirstOrDefault(e => e.Name == Name && e.IsEnabled);
            return x == null ? null : "chrome-extension://" + x.Id + "/dashboard.html";
        }
        catch (Exception) { return null; }
    }
}
