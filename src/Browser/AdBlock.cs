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
/// The engine drops an extension whose files changed, its settings with it: the copy in use is never changed while it
/// runs. A new one (a new version, or the same one trimmed anew) waits in Staged for the next start, uBOL's settings are
/// saved beforehand (ublock-state), and a profile that has lost the extension gets them back when it is added again.
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

    static bool busy;

    static string RestartNote => L.T("Изменения uBlock Origin Lite вступят в силу после перезапуска LiteBro.");

    /// <summary>Before the engine starts, while nothing holds the extension: a copy made ready last session takes its place.</summary>
    public static Task PrepareAsync() => Task.Run(() =>
    {
        if (VersionIn(Staged) == null) return;
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

    /// <summary>
    /// With the switch on: downloads the pinned version if missing, or makes a trimmed copy of the one in use if it is not
    /// as Trim leaves it. A first install comes in at once; a change of the copy in use, at the next start.
    /// </summary>
    public static async void Ensure()
    {
        if (busy) return;
        if (!App.Current.S.AdBlock)
        {
            Show("");
            return;
        }
        if (VersionIn(Staged) != null)
        {
            Show(RestartNote);
            return;
        }
        bool have = VersionIn(Folder) == Version;
        if (have && !Trim(Folder, write: false))
        {
            Show("");
            return;
        }
        if (!have && NetGuard.LocalOnly)
        {
            Show(L.T("Скачается с GitHub, когда будет выключен режим «только localhost»."));
            return;
        }
        busy = true;
        try
        {
            if (!have) Show(L.T("Скачивается с GitHub, около 12 МБ…"));
            var problem = await (have ? Task.Run(StageTrimmed) : Task.Run(DownloadAsync));
            if (problem != null)
            {
                Show((have ? L.T("Не удалось подготовить: ") : L.T("Не удалось скачать: ")) + problem
                    + L.T(". Попробует снова при следующем включении или запуске."));
                return;
            }
            if (!Directory.Exists(Folder))
            {
                try { Directory.Move(Staged, Folder); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }
            if (VersionIn(Staged) == null)
            {
                Show("");
                Changed();
                return;
            }
            await BackupAsync();
            Show(RestartNote);
        }
        finally { busy = false; }
    }

    /// <summary>The copy in use, trimmed, into Staged (without the engine's own _metadata); null when done, else what went wrong.</summary>
    static string? StageTrimmed()
    {
        var temp = Folder + ".download";
        try
        {
            Delete(temp);
            Copy(Folder, temp);
            Trim(temp, write: true);
            Delete(Staged);
            Directory.Move(temp, Staged);
            return null;
        }
        catch (Exception e) { return e.Message; }
        finally { Delete(temp); }
    }

    static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from))
            if (Path.GetFileName(dir) != "_metadata") Copy(dir, Path.Combine(to, Path.GetFileName(dir)));
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
                Trim(root, write: true);
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

    // The lists' names are English in every language. litebro.js, added to uBOL's settings page, puts Russian ones on the
    // page when the engine's language is Russian (the lists' own names stay): the files of the lists are left as they are,
    // and a change of language changes nothing in the extension. An English name uBOL changes stays as it is.
    static readonly Dictionary<string, string[]> RussianNames = new()
    {
        ["ublock-filters"] = new[] { "uBlock filters – Ads, trackers, and more", "Фильтры uBlock – реклама, трекеры и прочее" },
        ["pgl"] = new[] { "Peter Lowe – Ads, trackers, and more", "Peter Lowe – реклама, трекеры и прочее" },
        ["ublock-badware"] = new[] { "uBlock filters – Badware risks", "Фильтры uBlock – опасное ПО" },
        ["urlhaus-full"] = new[] { "Malicious URL Blocklist", "Вредоносные адреса (URLhaus)" },
        ["adguard-mobile"] = new[] { "AdGuard/uBO – Mobile Ads", "AdGuard/uBO – реклама в мобильных версиях" },
        ["block-lan"] = new[] { "Block Outsider Intrusion into LAN", "Защита локальной сети от внешних сайтов" },
        ["dpollock-0"] = new[] { "Dan Pollock’s hosts file", "Файл hosts Дэна Поллока" },
        ["adguard-spyware-url"] = new[] { "AdGuard URL Tracking Protection", "AdGuard – отслеживание через адреса" },
        ["annoyances-cookies"] = new[] { "EasyList/uBO – Cookie Notices", "EasyList/uBO – уведомления о cookie" },
        ["annoyances-overlays"] = new[] { "EasyList/uBO – Overlay Notices", "EasyList/uBO – всплывающие окна поверх страницы" },
        ["annoyances-social"] = new[] { "EasyList – Social Widgets", "EasyList – виджеты соцсетей" },
        ["annoyances-widgets"] = new[] { "EasyList – Chat Widgets", "EasyList – виджеты чатов" },
        ["annoyances-others"] = new[] { "EasyList – Other Annoyances", "EasyList – прочие раздражители" },
        ["annoyances-notifications"] = new[] { "EasyList – Notifications", "EasyList – запросы уведомлений" },
        ["ublock-experimental"] = new[] { "uBlock filters – Experimental", "Фильтры uBlock – экспериментальные" },
        ["stevenblack-hosts"] = new[] { "Steven Black’s Unified Hosts (adware + malware)", "Сводный hosts Стивена Блэка (реклама и вредоносное ПО)" },
        ["ubol-tests"] = new[] { "uBO Lite Test Filters", "Тестовые фильтры uBO Lite" },
        ["rus-1"] = new[] { "Counters", "счётчики" }, // after the flags of «RU AdList:»
    };

    const string NamesScript = @"// LiteBro: Russian names of the filter lists when the browser speaks Russian
(() => {
  if (!chrome.i18n.getUILanguage().startsWith('ru')) return;
  const names = __NAMES__;
  const lists = document.getElementById('lists');
  const apply = () => {
    for (const [id, [en, ru]] of Object.entries(names)) {
      const name = lists.querySelector(`.listEntry[data-rulesetid=""${id}""] > .detailbar .listname`);
      if (!name) continue;
      // Text only: the flags before a name are pictures
      for (const node of name.childNodes)
        if (node.nodeType === 3 && node.nodeValue.includes(en)) node.nodeValue = node.nodeValue.replace(en, ru);
    }
  };
  new MutationObserver(apply).observe(lists, { childList: true, subtree: true });
  apply();
})();
";

    static string NamesFile => NamesScript.Replace("__NAMES__", ProjectStore.Json.Serialize(RussianNames));

    /// <summary>
    /// The copy as LiteBro uses it: what does nothing in WebView2 gone or hidden, the page to send from, the lists' names.
    /// Only a copy not in use is written (write); true when there was, or would be, anything to change.
    /// </summary>
    static bool Trim(string dir, bool write)
    {
        bool changed = false;
        void Change(Action act)
        {
            changed = true;
            if (write) act();
        }
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
                Change(() => File.Delete(Path.Combine(dir, file.Replace('/', '\\'))));
            }
        }
        var css = Path.Combine(dir, "css", "settings.css");
        if (File.Exists(css) && !File.ReadAllText(css).Contains("LiteBro")) Change(() => File.AppendAllText(css, TrimCss));
        var page = Path.Combine(dir, Page);
        if (!File.Exists(page)) Change(() => File.WriteAllText(page, "<!doctype html><meta charset=\"utf-8\"><title>LiteBro</title>\n"));
        var script = Path.Combine(dir, "litebro.js");
        if (!File.Exists(script) || File.ReadAllText(script) != NamesFile)
            Change(() => File.WriteAllText(script, NamesFile, new UTF8Encoding(false)));
        var dashboard = Path.Combine(dir, "dashboard.html");
        var html = File.Exists(dashboard) ? File.ReadAllText(dashboard) : "";
        if (html.Contains("</body>") && !html.Contains("litebro.js"))
            Change(() => File.WriteAllText(dashboard, html.Replace("</body>", "<script src=\"litebro.js\"></script>\n</body>"), new UTF8Encoding(false)));
        return changed;
    }

    static bool SameFile(string path, string dir, string relative) =>
        string.Equals(Path.GetFullPath(path), Path.GetFullPath(Path.Combine(dir, relative.Replace('/', '\\'))), StringComparison.OrdinalIgnoreCase);

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
    public static void Apply(IntPtr window, CoreWebView2Profile profile, string name)
    {
        if (!applied.ContainsKey(name)) applied[name] = ApplyAsync(window, profile, name);
    }

    static async Task ApplyAsync(IntPtr window, CoreWebView2Profile profile, string name)
    {
        try { await SetAsync(window, profile, name, App.Current.S.AdBlock); }
        catch (Exception) { applied.Remove(name); } // the WebView closed meanwhile, say: the next one tries again
    }

    static async Task SetAsync(IntPtr window, CoreWebView2Profile profile, string name, bool on)
    {
        var ours = (await profile.GetBrowserExtensionsAsync()).Where(x => x.Name == Name).ToList();
        if (on && ours.Count == 0)
        {
            // Not downloaded yet: Ensure brings the profiles in line once it is
            if (VersionIn(Folder) == null) return;
            ours.Add(await profile.AddBrowserExtensionAsync(Folder));
            // Dropped by the engine when its files changed: uBOL's settings as they were saved
            await RestoreAsync(window, name);
        }
        foreach (var x in ours)
            if (x.IsEnabled != on) await x.EnableAsync(on);
    }

    static string StateDir => Path.Combine(Settings.Dir, "ublock-state");
    static string StatePath(string profile) => Path.Combine(StateDir, (profile.Length == 0 ? "shared" : profile) + ".json");

    // What uBOL keeps of the user's choices: the sites' filtering modes (the default one too), the lists switched on,
    // strict blocking and the sites let past it, developer mode, own DNR rules
    const string SaveScript = @"(async () => {
  const send = m => chrome.runtime.sendMessage(m);
  const options = await send({ what: 'getOptionsPageData' });
  const kept = await chrome.storage.local.get(['userDnrRules', 'excludedStrictBlockHostnames']);
  return {
    modes: await send({ what: 'getFilteringModeDetails' }),
    enabledRulesets: options.enabledRulesets,
    strictBlockMode: options.strictBlockMode,
    developerMode: options.developerMode,
    userDnrRules: kept.userDnrRules,
    excluded: kept.excludedStrictBlockHostnames,
  };
})()";

    // Back through the messages uBOL's settings page sends, so the rules follow as they do from there
    const string RestoreScript = @"(async b => {
  const send = m => chrome.runtime.sendMessage(m);
  if (b.modes) await send({ what: 'setFilteringModeDetails', modes: b.modes });
  if (Array.isArray(b.enabledRulesets)) await send({ what: 'applyRulesets', enabledRulesets: b.enabledRulesets });
  await send({ what: 'setStrictBlockMode', state: b.strictBlockMode !== false });
  await send({ what: 'setDeveloperMode', state: b.developerMode === true });
  if (b.excluded) await chrome.storage.local.set({ excludedStrictBlockHostnames: b.excluded });
  if (b.userDnrRules) {
    await chrome.storage.local.set({ userDnrRules: b.userDnrRules });
    await send({ what: 'updateUserDnrRules' });
  }
  return true;
})(JSON.parse(__STATE__))";

    /// <summary>uBOL's settings in the profiles used this session, saved before the copy in use changes.</summary>
    static async Task BackupAsync()
    {
        if (App.Current.Forms.FirstOrDefault() is not { } form) return;
        foreach (var profile in applied.Keys.ToList()) await SaveStateAsync(form.Handle, profile);
    }

    static async Task SaveStateAsync(IntPtr window, string profile)
    {
        var json = await AskAsync(window, profile, SaveScript);
        if (json == null || !json.StartsWith("{")) return;
        try
        {
            Directory.CreateDirectory(StateDir);
            File.WriteAllText(StatePath(profile), json);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
    }

    static async Task RestoreAsync(IntPtr window, string profile)
    {
        string json;
        try { json = File.ReadAllText(StatePath(profile)); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return; } // none saved: a first install
        if (json.StartsWith("{")) await AskAsync(window, profile, RestoreScript.Replace("__STATE__", ProjectStore.Json.Serialize(json)));
    }

    /// <summary>The switch changed (or the extension came): the profiles with a WebView follow now, the others on their next one.</summary>
    public static void Changed()
    {
        applied.Clear();
        foreach (var form in App.Current.Forms)
            foreach (var (name, core) in form.LiveProfiles())
                Apply(form.Handle, core.Profile, name);
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
        await SaveStateAsync(window, profile); // kept should the engine drop the extension one day
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
