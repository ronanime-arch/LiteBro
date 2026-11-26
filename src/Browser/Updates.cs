using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LiteBro;

/// <summary>
/// Updates from the GitHub releases of the repository: the latest release (iOS builds are prereleases, so never it)
/// holds LiteBro-Setup.zip with the installer. Checked a minute after the start and every 6 hours while AutoUpdate
/// is on and «только localhost» is off; a newer version shows a green button on the toolbar. Installing asks first
/// (it closes LiteBro and with it the projects' programs), downloads, checks the file's SHA-256 from the release,
/// starts the installer with /update (one UAC prompt) and closes the browser; the installer starts it again.
/// Everything here runs on the UI thread.
/// </summary>
static class Updates
{
    const string Repo = "ronanime-arch/LiteBro";
    const string Api = "https://api.github.com/repos/" + Repo + "/releases/latest";
    /// <summary>The page of the current download.</summary>
    public const string Page = "https://github.com/" + Repo + "/releases/latest";
    public const string Download = Page + "/download/LiteBro-Setup.zip";
    const string Asset = "LiteBro-Setup.zip";
    const long MaxDownload = 64L << 20;
    static readonly TimeSpan Every = TimeSpan.FromHours(6);

    static readonly HttpClient Http = new(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromMinutes(5) };
    static readonly Timer timer = new() { Interval = 60_000 };
    static string Folder => Path.Combine(Path.GetTempPath(), "LiteBro-update");

    /// <summary>This program's version: 1.9.x, as build.ps1 gives it.</summary>
    public static readonly Version Current = Trim(Assembly.GetExecutingAssembly().GetName().Version);

    /// <summary>A newer release than this one, once a check found it.</summary>
    public static Release? Available { get; private set; }

    /// <summary>What the «Для разработчика» page says beside the buttons.</summary>
    public static string State { get; private set; } = "";

    public static bool Busy { get; private set; }

    /// <summary>Raised when Available, State or Busy change.</summary>
    public static event Action? Changed;

    public sealed class Release
    {
        public Version Version = new();
        public string Url = "", Sha256 = "";
        public long Size;
    }

    public static void Start()
    {
        timer.Tick += async (_, _) =>
        {
            timer.Interval = (int)Every.TotalMilliseconds;
            if (App.Current.S.AutoUpdate && !App.Current.S.LocalOnly) await CheckAsync();
        };
        timer.Start();
    }

    static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    static void Set(string state)
    {
        State = state;
        Changed?.Invoke();
    }

    /// <summary>Asks GitHub for the latest release; manual: from the button on /dev, even in local mode.</summary>
    public static async Task CheckAsync(bool manual = false)
    {
        if (Busy) return;
        Busy = true;
        if (manual) Set(L.T("Проверяется…"));
        try
        {
            CleanUp();
            using var request = new HttpRequestMessage(HttpMethod.Get, Api);
            request.Headers.UserAgent.ParseAdd("LiteBro");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var json = ProjectStore.Json.Deserialize<Dictionary<string, object>>(await response.Content.ReadAsStringAsync());
            var release = Parse(json);
            Available = release != null && release.Version > Current ? release : null;
            Set(Available != null
                ? L.T("Доступна версия ") + Available.Version + L.T(", у вас ") + Current + "."
                : L.T("У вас последняя версия, ") + Current + ".");
        }
        catch (Exception e)
        {
            // No network or GitHub's limit: the next check tries again; only a manual check says so
            if (manual || Available == null) Set(manual ? L.T("Не удалось проверить обновления: ") + e.Message : "");
        }
        finally
        {
            Busy = false;
            Changed?.Invoke();
        }
    }

    static Release? Parse(Dictionary<string, object>? json)
    {
        if (json == null || !json.TryGetValue("tag_name", out var t) || t is not string tag) return null;
        if (json.TryGetValue("prerelease", out var pre) && pre is true) return null;
        if (!System.Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        if (!json.TryGetValue("assets", out var a) || a is not IEnumerable assets) return null;
        foreach (var item in assets.OfType<Dictionary<string, object>>())
        {
            if (!item.TryGetValue("name", out var n) || n as string != Asset) continue;
            if (!item.TryGetValue("browser_download_url", out var u) || u is not string url
                || !url.StartsWith("https://github.com/" + Repo + "/releases/download/", StringComparison.Ordinal)) return null;
            var digest = item.TryGetValue("digest", out var d) ? d as string ?? "" : "";
            return new Release
            {
                Version = Trim(version),
                Url = url,
                Sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest.Substring(7).ToLowerInvariant() : "",
                Size = item.TryGetValue("size", out var s) ? s switch { int i => i, long l => l, _ => 0 } : 0,
            };
        }
        return null;
    }

    /// <summary>The installers of earlier updates: the installer may still have been running when they were left.</summary>
    static void CleanUp()
    {
        try { if (Directory.Exists(Folder)) Directory.Delete(Folder, true); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Asks, downloads and starts the installer. True when the installer runs: the caller closes the browser.
    /// </summary>
    public static async Task<bool> InstallAsync(IWin32Window? owner, IEnumerable<string> running)
    {
        if (Busy || Available is not { } release) return false;
        var names = running.ToList();
        var text = L.T("Установить LiteBro ") + release.Version + L.T("? Сейчас у вас ") + Current + ".\n\n" +
            L.T("LiteBro закроется, а с ним остановятся программы проектов, которые он запустил (например, dsh), и текущая задача агента прервётся. Открытые терминалы тоже закроются.") +
            (names.Count > 0 ? L.T("\n\nСейчас работают: ") + string.Join(", ", names) + "." : "") +
            L.T("\n\nWindows спросит разрешение на установку. Потом LiteBro запустится снова, плитки, входы на сайты и настройки останутся.");
        if (MessageBox.Show(owner, text, "LiteBro", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return false;
        Busy = true;
        Set(L.T("Загружается LiteBro ") + release.Version + "…");
        string setup;
        try
        {
            setup = await Task.Run(() => FetchAsync(release));
        }
        catch (Exception e)
        {
            Busy = false;
            Set(L.T("Не удалось загрузить обновление: ") + e.Message);
            return false;
        }
        try
        {
            // The installer is for the whole computer: Windows asks the user for administrator rights
            Process.Start(new ProcessStartInfo(setup, "/update") { UseShellExecute = true, Verb = "runas" });
            Set(L.T("Устанавливается LiteBro ") + release.Version + "…");
            return true;
        }
        catch (Win32Exception e)
        {
            Busy = false;
            Set(e.NativeErrorCode == 1223 ? L.T("Установка отменена.") : L.T("Не удалось запустить установку: ") + e.Message);
            return false;
        }
    }

    /// <summary>The archive into the temp folder, checked against the release, and the installer out of it.</summary>
    static async Task<string> FetchAsync(Release release)
    {
        CleanUp();
        Directory.CreateDirectory(Folder);
        var zip = Path.Combine(Folder, Asset);
        using (var request = new HttpRequestMessage(HttpMethod.Get, release.Url))
        {
            request.Headers.UserAgent.ParseAdd("LiteBro");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxDownload) throw new InvalidDataException(L.T("слишком большой файл"));
            using var from = await response.Content.ReadAsStreamAsync();
            using var to = File.Create(zip);
            var buffer = new byte[81920];
            long total = 0;
            int n;
            while ((n = await from.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                if ((total += n) > MaxDownload) throw new InvalidDataException(L.T("слишком большой файл"));
                await to.WriteAsync(buffer, 0, n);
            }
            if (release.Size > 0 && total != release.Size) throw new InvalidDataException(L.T("файл загрузился не полностью"));
        }
        if (release.Sha256.Length > 0)
        {
            using var sha = SHA256.Create();
            using var file = File.OpenRead(zip);
            var hash = string.Concat(sha.ComputeHash(file).Select(b => b.ToString("x2")));
            if (hash != release.Sha256) throw new InvalidDataException(L.T("контрольная сумма не совпала"));
        }
        var setup = Path.Combine(Folder, "LiteBro-Setup.exe");
        using (var archive = ZipFile.OpenRead(zip))
        {
            var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("LiteBro-Setup.exe", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException(L.T("в архиве нет установщика"));
            entry.ExtractToFile(setup, true);
        }
        // The file must be the installer of that version, not anything else under its name
        var info = FileVersionInfo.GetVersionInfo(setup);
        if (info.ProductName != Associations.AppName || !System.Version.TryParse(info.FileVersion, out var v) || Trim(v) != release.Version)
            throw new InvalidDataException(L.T("в архиве другой файл"));
        return setup;
    }
}
