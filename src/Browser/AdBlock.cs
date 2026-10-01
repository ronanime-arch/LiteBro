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

    // WebView2 shows no toolbar icon and no popup of an extension: the «Поведение» options of uBOL's settings (a count of
    // blocked requests on the icon, a reload when the popup changes a site's mode) do nothing here. Check on a new version.
    const string TrimCss = "\n/* LiteBro: no toolbar icon or popup in WebView2 */\nsection[data-pane=\"settings\"] > div:first-child { display: none; }\n";

    /// <summary>Hides what does nothing in WebView2 from uBOL's settings page; once.</summary>
    static void Trim(string dir)
    {
        var css = Path.Combine(dir, "css", "settings.css");
        try
        {
            if (File.Exists(css) && !File.ReadAllText(css).Contains("LiteBro")) File.AppendAllText(css, TrimCss);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
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
