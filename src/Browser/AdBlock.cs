using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace LiteBro;

/// <summary>
/// uBlock Origin Lite, built in (AdBlock in settings.ini, on by default, the switch on the «Для разработчика» page).
/// The installer puts the extension next to the exe (build.ps1 takes a pinned release); it runs from a copy in the
/// user's data, because the engine writes its indexed filter lists into the extension's own folder, which Program Files
/// does not allow. Every profile gets it on its first WebView of the session: added if missing, then switched on or off.
/// uBOL filters declaratively: the engine applies the lists, its own code runs only to set them up.
/// </summary>
static class AdBlock
{
    public const string Name = "uBlock Origin Lite";

    static string Source => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ublock");
    static string Folder => Path.Combine(Settings.Dir, "ublock");

    static Task<string?>? prepared;

    /// <summary>The folder the extension is added from, copied over once per version; null in a build without it.</summary>
    public static Task<string?> PrepareAsync() => prepared ??= Task.Run(Prepare);

    static string? Prepare()
    {
        try
        {
            var version = VersionIn(Source);
            if (version == null) return VersionIn(Folder) != null ? Folder : null;
            if (VersionIn(Folder) == version) return Folder;
            // Copied whole first and swapped in by renames: a folder the engine still holds stays as it is
            string fresh = Folder + ".new", old = Folder + ".old";
            Delete(fresh);
            Delete(old);
            Copy(Source, fresh);
            if (Directory.Exists(Folder)) Directory.Move(Folder, old);
            Directory.Move(fresh, Folder);
            Delete(old);
            return Folder;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            // The older version stays until the next start
            return VersionIn(Folder) != null ? Folder : null;
        }
    }

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

    static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from)) Copy(dir, Path.Combine(to, Path.GetFileName(dir)));
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
            if (await PrepareAsync() is not { } folder) return;
            ours.Add(await profile.AddBrowserExtensionAsync(folder));
        }
        foreach (var x in ours)
            if (x.IsEnabled != on) await x.EnableAsync(on);
    }

    /// <summary>The switch changed: the profiles with a WebView follow now, the others on their next one.</summary>
    public static void Changed()
    {
        applied.Clear();
        foreach (var form in App.Current.Forms)
            foreach (var (name, core) in form.LiveProfiles())
                Apply(core.Profile, name);
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
