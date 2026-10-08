using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace LiteBro;

static class Program
{
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [STAThread]
    static void Main(string[] args)
    {
        Settings.MoveOldData();
        Settings settings;
        try { settings = Settings.Load(); }
        catch (Exception) { settings = new Settings(); } // a broken ini must not stop links from opening
        var argument = args.Select(a => a.Trim()).FirstOrDefault(a => a.Length > 0 && !a.StartsWith("--"));
        var url = argument != null && Uri.TryCreate(argument, UriKind.Absolute, out var parsed) ? parsed : null;
        if (argument != null && (url == null || !Router.OpensHere(url, settings)))
        {
            // An internet address (unless the switch keeps them here), a file or a mail link: straight on to the main browser
            Router.OpenElsewhere(Router.ForOtherBrowser(argument, url), settings);
            return;
        }
        var address = url == null ? null : Router.Normalize(url).AbsoluteUri;
        // Some programs wait until the browser they started exits. So a launch with an address returns
        // at once: the page goes to the running LiteBro, or to a new one started on its own.
        if (address != null && !args.Contains("--stay"))
        {
            if (!SingleInstance.PrimaryRunning())
            {
                try
                {
                    // Through the shell, so the new process inherits none of the caller's handles and pipes
                    Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--stay \"" + address + "\"") { UseShellExecute = true });
                    return;
                }
                catch (Exception) { } // then this process shows the page itself
            }
            else if (SingleInstance.SendToPrimary(address)) return;
        }
        if (!SingleInstance.TryBecomePrimary()
            && (SingleInstance.SendToPrimary(address ?? "") || !SingleInstance.WaitToBecomePrimary(15_000)))
            return;
        try { Associations.RemoveStalePerUserRegistration(); }
        catch (Exception) { } // the browser itself still starts
        try { Associations.MoveOldState(); }
        catch (Exception) { }
        SetCurrentProcessExplicitAppUserModelID(Associations.AppUserModelId); // same id as the shortcut
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Theme.Init(settings.Theme);
        Application.Run(new App(settings, address));
    }
}

/// <summary>Keeps the process alive while any window is open and owns what the windows share.</summary>
sealed class App : ApplicationContext
{
    // Chromium switches that save memory without costing CPU time or security: no idle spare
    // renderer, no back/forward page cache, the network service inside the browser process
    // instead of a process of its own, and one renderer per site shared by all windows.
    const string MemoryArgs =
        "--disable-features=SpareRendererForSitePerProcess,BackForwardCache " +
        "--enable-features=NetworkServiceInProcess2,NetworkServiceInProcess " +
        "--process-per-site";
    // No telemetry: none of the engine's own traffic in the background (field trials, component updates,
    // reliability reports, hyperlink pings, crash uploads); SmartScreen is off per page (Setup)
    const string QuietArgs =
        " --disable-background-networking --disable-component-update --disable-domain-reliability --no-pings --disable-breakpad";

    public static App Current { get; private set; } = null!;
    public static readonly Icon AppIcon = LoadIcon();
    public Settings S { get; }
    public CoreWebView2Environment? Env { get; private set; }
    Task<CoreWebView2Environment>? envTask;
    readonly List<BrowserForm> forms = new();
    public IReadOnlyList<BrowserForm> Forms => forms;
    readonly Dictionary<string, Launcher> launchers = new();
    readonly HashSet<string> iconClaims = new();
    SynchronizationContext ui = null!;
    readonly System.Windows.Forms.Timer trimTimer = new();
    readonly System.Windows.Forms.Timer hiddenTrimTimer = new() { Interval = 5000 };
    BrowserForm? lastActive;
    int trimPass;
    bool exiting;

    public App(Settings settings, string? startUrl)
    {
        Current = this;
        S = settings;
        NetGuard.Init(settings);
        trimTimer.Tick += (_, _) => OnTrimTick();
        hiddenTrimTimer.Tick += (_, _) =>
        {
            hiddenTrimTimer.Stop();
            TrimHiddenTabs();
        };
        // Started with an address: show just that. A plain start opens the home page (and the harness).
        OpenWindow(startUrl, isMain: true, home: startUrl == null).Show();
        ui = SynchronizationContext.Current!; // installed by the first window
        // Raised on this thread: the windows follow Windows switching between light and dark
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SingleInstance.Listen(message =>
        {
            bool taken = false;
            try { ui.Send(_ => taken = Open(message), null); }
            catch (Exception) { } // the UI thread is gone
            return taken;
        });
    }

    static Icon LoadIcon()
    {
        using var stream = typeof(App).Assembly.GetManifestResourceStream("app.ico");
        return new Icon(stream);
    }

    // One browser process for all windows: a new window costs a renderer, not a whole browser.
    public async Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        // While the engine restarts with other flags, new tabs wait for the new one
        while (restart is { IsCompleted: false } r) await r;
        return await (envTask ??= CreateEnvironmentAsync());
    }

    Task? restart;

    /// <summary>The engine is being restarted: a WebView made now belongs to the old one.</summary>
    public bool Restarting => restart is { IsCompleted: false };

    /// <summary>The engine to answer a request with: the current one, or during a restart the one going away.</summary>
    public CoreWebView2Environment? ResponseEnv => Env ?? oldEnv;
    CoreWebView2Environment? oldEnv;

    async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        var args = MemoryArgs + QuietArgs;
        if (!S.Gpu) args += " --disable-gpu";
        if (S.ExtraBrowserArgs.Length > 0) args += " " + S.ExtraBrowserArgs;
        engineKey = EngineKey();
        if (NetGuard.CorsOff) args += " --disable-web-security";
        // «Только localhost»: all but local traffic through the gateway, which refuses what the rules forbid
        // WebRTC's UDP would go around the proxy: none that is not proxied
        if (NetGuard.LocalOnly)
            args += " --proxy-server=http://127.0.0.1:" + Gateway.Start() + " --proxy-bypass-list=" + Gateway.BypassList() +
                " --force-webrtc-ip-handling-policy=disable_non_proxied_udp";
        // uBlock Origin Lite: extensions are always allowed, the switch turns the extension itself on and off.
        // A version downloaded last session takes its place before the engine starts, while nothing holds the old one.
        var options = new CoreWebView2EnvironmentOptions(args) { Language = L.EngineLanguage, AreBrowserExtensionsEnabled = true };
        if (S.AdBlock) await AdBlock.PrepareAsync();
        // A new WebView starts with the theme's background, not a white flash before its page paints
        var bg = Theme.PageBackground;
        Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", $"FF{bg.R:X2}{bg.G:X2}{bg.B:X2}");
        Env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Settings.Dir, "WebView2"), options);
        AdBlock.Ensure();
        return Env;
    }

    /// <summary>The profiles pages ran in this session: the ones to clear on exit.</summary>
    readonly HashSet<string> usedProfiles = new() { "" };

    /// <summary>A WebView in the shared profile ("") or in a project's own.</summary>
    public async Task<CoreWebView2Controller> CreateControllerAsync(CoreWebView2Environment env, IntPtr window, string profile, bool pages = true)
    {
        if (pages) usedProfiles.Add(profile);
        CoreWebView2Controller c;
        if (profile.Length == 0) c = await env.CreateCoreWebView2ControllerAsync(window);
        else
        {
            var options = env.CreateCoreWebView2ControllerOptions();
            options.ProfileName = profile;
            c = await env.CreateCoreWebView2ControllerAsync(window, options);
        }
        if (pages) AdBlock.Apply(window, c.CoreWebView2.Profile, profile);
        return c;
    }

    /// <summary>
    /// Runs something on a profile's settings (its permissions): through a tab already in it, else a hidden WebView
    /// made for the purpose and closed after. Such a WebView shows no page: the profile is not one to clear on exit for it.
    /// </summary>
    public Task<T> WithProfileAsync<T>(string profile, IntPtr window, Func<CoreWebView2Profile, Task<T>> use) =>
        WithCoreAsync(profile, window, core => use(core.Profile));

    /// <summary>The same with a WebView of the profile: for the DevTools protocol and the cookie manager.</summary>
    public async Task<T> WithCoreAsync<T>(string profile, IntPtr window, Func<CoreWebView2, Task<T>> use)
    {
        foreach (var form in forms)
            if (form.CoreIn(profile) is { } core) return await use(core);
        if (Env is not { } env) throw new InvalidOperationException(L.T("Движок браузера перезапускается"));
        var c = await CreateControllerAsync(env, window, profile, pages: false);
        try
        {
            c.IsVisible = false;
            return await use(c.CoreWebView2);
        }
        finally { c.Close(); }
    }

    /// <summary>A tab in a project's own profile closed: with «Не хранить кэш», once no tab is left in it, its cache goes.</summary>
    public async void ProfileMaybeUnused(string profile, IntPtr window)
    {
        if (profile.Length == 0 || !ProjectStore.All.Any(p => p.Profile == profile && p.Site.NoCache)) return;
        await Task.Yield(); // after the tab's WebView is closed
        if (forms.Any(f => f.CoreIn(profile) != null)) return;
        try { await WithProfileAsync(profile, window, async data => { await data.ClearBrowsingDataAsync(CacheKinds); return true; }); }
        catch (Exception) { } // the engine restarts or is gone
    }

    /// <summary>
    /// «Очистить сейчас» of «Настройка профиля»: cache, cookies, site storage or all of it. An own profile is cleared
    /// whole; in the shared one only the project's sites, but for the HTTP cache, which the engine keeps per profile.
    /// </summary>
    public async Task ClearSiteAsync(Project p, string what, IntPtr window)
    {
        bool cache = what is "cache" or "all", cookies = what is "cookies" or "all", storage = what is "storage" or "all";
        if (p.Profile.Length > 0)
        {
            var kinds = (CoreWebView2BrowsingDataKinds)0;
            if (cache) kinds |= CacheKinds | CoreWebView2BrowsingDataKinds.ServiceWorkers;
            if (cookies) kinds |= CoreWebView2BrowsingDataKinds.Cookies;
            if (storage) kinds |= CoreWebView2BrowsingDataKinds.AllDomStorage | CoreWebView2BrowsingDataKinds.ServiceWorkers;
            await WithProfileAsync(p.Profile, window, async data => { await data.ClearBrowsingDataAsync(kinds); return true; });
            return;
        }
        var origins = p.Addresses().Select(SitePermissions.OriginOf).OfType<string>().Distinct().ToList();
        var types = string.Join(",", new[]
        {
            cache || storage ? "cache_storage,service_workers" : null,
            storage ? "local_storage,indexeddb,websql,file_systems" : null,
        }.OfType<string>());
        await WithCoreAsync("", window, async core =>
        {
            foreach (var origin in origins)
            {
                if (types.Length > 0)
                    await core.CallDevToolsProtocolMethodAsync("Storage.clearDataForOrigin",
                        ProjectStore.Json.Serialize(new Dictionary<string, object> { ["origin"] = origin, ["storageTypes"] = types }));
                if (cookies)
                    foreach (var cookie in await core.CookieManager.GetCookiesAsync(origin))
                        core.CookieManager.DeleteCookie(cookie);
            }
            if (cache) await core.CallDevToolsProtocolMethodAsync("Network.clearBrowserCache", "{}");
            return true;
        });
    }

    /// <summary>
    /// A setting of «Настройка профиля»: saved with the tile, then every window follows. A setting of the whole profile
    /// moves the project into its own (the page asked first).
    /// </summary>
    public void SetSiteSetting(Project p, string key, object? value)
    {
        bool on = value is true;
        bool ownBefore = p.OwnProfile;
        switch (key)
        {
            case "ownProfile":
                p.OwnProfile = on;
                // Without its own profile these would apply to the shared one: they go
                if (!on) p.Site.Tracking = p.Site.PageTheme = p.Site.Autofill = "";
                break;
            case "keepData": p.KeepData = on; break;
            default:
                if (!p.Site.Set(key, value)) return;
                if (p.Site.NeedsOwnProfile) p.OwnProfile = true;
                break;
        }
        ProjectStore.Save(p);
        ProjectSaved(p);
        if (key == "noAdBlock" || (key == "ownProfile" && p.Site.NoAdBlock)) SetUnfiltered(p, p.Site.NoAdBlock);
        foreach (var form in forms.ToList()) form.SiteSettingsChanged(p, key, p.OwnProfile != ownBefore);
    }

    /// <summary>«Не блокировать рекламу»: uBlock Origin Lite leaves each of the project's sites unfiltered, in its profile.</summary>
    async void SetUnfiltered(Project p, bool off)
    {
        if (!S.AdBlock || forms.FirstOrDefault() is not { } form) return;
        var hosts = p.Addresses().Select(a => Uri.TryCreate(a, UriKind.Absolute, out var u) && (u.Scheme is "http" or "https") ? u.IdnHost : null)
            .OfType<string>().Distinct().ToList();
        foreach (var host in hosts)
            try { await AdBlock.SetUnfilteredAsync(form.Handle, p.Profile, host, off); }
            catch (Exception) { }
        // uBOL's rules apply to the pages loaded after
        foreach (var f in forms.ToList()) f.ReloadProject(p);
    }

    /// <summary>The last window is closing and cookies and cache are to go with it (ClearOnExit).</summary>
    /// A project's own profile with «Не хранить кэш» loses its cache then too.
    public bool ClearsOnClose(BrowserForm form) => (S.ClearOnExit || NoCacheProfiles().Any()) && Env != null && forms.Count == 1 && forms[0] == form;

    /// <summary>The own profiles of projects with «Не хранить кэш» that pages ran in this session.</summary>
    IEnumerable<string> NoCacheProfiles() =>
        ProjectStore.All.Where(p => p.Site.NoCache && p.Profile.Length > 0 && usedProfiles.Contains(p.Profile)).Select(p => p.Profile);

    const CoreWebView2BrowsingDataKinds CacheKinds = CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage;

    /// <summary>
    /// Deletes cookies and the HTTP cache of every profile used this session, through a hidden WebView in each.
    /// A project with KeepData keeps its own profile whole; in the shared profile it keeps the cookies of its sites
    /// (cookies do not tell ports apart: all of localhost's stay then). Storage (localStorage, IndexedDB) is left alone.
    /// </summary>
    public async Task ClearDataAsync(IntPtr window)
    {
        if (Env is not { } env) return;
        var projects = ProjectStore.All;
        var keptHosts = projects.Where(p => p.KeepData && p.Profile.Length == 0).SelectMany(p => p.Addresses())
            .Select(a => Uri.TryCreate(a, UriKind.Absolute, out var u) && !u.IsFile ? u.Host : null).OfType<string>().ToList();
        var noCache = NoCacheProfiles().ToList();
        foreach (var profile in usedProfiles.ToList())
        {
            // Kept whole (or not cleared on exit at all), but for the cache of a project with «Не хранить кэш»
            bool kept = !S.ClearOnExit || (profile.Length > 0 && projects.Any(p => p.KeepData && p.Profile == profile));
            if (kept && !noCache.Contains(profile)) continue;
            CoreWebView2Controller? c = null;
            try
            {
                c = await CreateControllerAsync(env, window, profile);
                c.IsVisible = false;
                var data = c.CoreWebView2.Profile;
                if (kept)
                {
                    await data.ClearBrowsingDataAsync(CacheKinds);
                    continue;
                }
                if (profile.Length > 0 || keptHosts.Count == 0)
                {
                    await data.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.Cookies | CoreWebView2BrowsingDataKinds.DiskCache);
                    continue;
                }
                var cookies = c.CoreWebView2.CookieManager;
                foreach (var cookie in await cookies.GetCookiesAsync(null))
                    if (!keptHosts.Any(h => CookieOf(cookie.Domain, h))) cookies.DeleteCookie(cookie);
                await data.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache);
            }
            catch (Exception) { } // the engine is gone
            finally { c?.Close(); }
        }
    }

    /// <summary>A cookie of that domain is sent to the host.</summary>
    static bool CookieOf(string domain, string host)
    {
        domain = domain.TrimStart('.');
        return host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }

    public BrowserForm OpenWindow(string? url, bool isMain = false, bool home = false, Project? project = null, string? link = null)
    {
        var form = new BrowserForm(url, isMain, home, project, link);
        forms.Add(form);
        form.FormClosed += (_, _) =>
        {
            forms.Remove(form);
            if (lastActive == form) lastActive = null;
            ShellMaybeUnused();
            if (forms.Count == 0) ExitThread();
            else BackgroundChanged();
        };
        return form;
    }

    /// <summary>
    /// An address from a later launch ("" = just show up). A tab already on that site is reused,
    /// so an app that opens its page on every start does not pile up tabs; else it opens in a new tab.
    /// </summary>
    bool Open(string address)
    {
        if (exiting) return false;
        // A window hidden while it clears data on exit is going: a new one opens instead
        var open = forms.Where(f => !f.ShuttingDown).ToList();
        var last = lastActive is { ShuttingDown: false } ? lastActive : open.LastOrDefault();
        if (address.Length == 0)
        {
            if (last == null) OpenWindow(null, home: true).Show();
            else Show(last);
            return true;
        }
        // A project's tab is taken again; an internet link gets a tab of its own, not the page open on that site
        var local = Uri.TryCreate(address, UriKind.Absolute, out var url) && Router.IsLocal(url, S);
        var window = local ? open.OrderByDescending(f => f == lastActive).FirstOrDefault(f => f.TryFocusTabOn(address)) : null;
        if (window == null)
        {
            window = last;
            if (window == null)
            {
                OpenWindow(address).Show();
                return true;
            }
            window.OpenNewTab(address);
        }
        Show(window);
        return true;
    }

    static void Show(BrowserForm? form)
    {
        if (form == null) return;
        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
        form.Activate();
    }

    public void NoteActive(BrowserForm form) => lastActive = form;

    /// <summary>A tab left the PowerShell console: once no tab shows it, its PowerShell goes, and its memory with it.</summary>
    public void ShellMaybeUnused() => ui.Post(_ =>
    {
        var url = ProgramLog.Url(ProgramLog.Shell);
        if (!forms.Any(f => f.HasTabOn(url)) && launchers.TryGetValue(ProgramLog.Shell.Id, out var launcher))
            launcher.StopShell(quiet: false);
    }, null);

    /// <summary>The PowerShell of the console of no project runs.</summary>
    public bool ShellRunning => launchers.TryGetValue(ProgramLog.Shell.Id, out var launcher) && launcher.ShellRunning;

    /// <summary>The launcher of a project's program, kept for as long as the browser runs.</summary>
    public Launcher LauncherFor(Project p)
    {
        if (!launchers.TryGetValue(p.Id, out var launcher))
            launchers[p.Id] = launcher = new Launcher(p, () => ui.Post(_ => ProjectsChanged(), null));
        launcher.Project = p;
        return launcher;
    }

    public IEnumerable<string> RunningIds => launchers.Where(l => l.Value.Running).Select(l => l.Key);

    /// <summary>True when the address is on the site of a project whose program runs, or of one of its links.</summary>
    public bool IsRunningSite(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var here) && !here.IsFile
        && launchers.Values.Any(l => l.Running && l.Project.Addresses().Any(a =>
            Uri.TryCreate(a, UriKind.Absolute, out var site) && BrowserForm.SameSite(site, here)));

    /// <summary>Every start page redraws its tiles.</summary>
    public void ProjectsChanged()
    {
        foreach (var form in forms) form.SendProjects();
    }

    public void ProjectSaved(Project p)
    {
        if (launchers.TryGetValue(p.Id, out var launcher)) launcher.Project = p;
        iconClaims.Remove(p.Id); // saved anew: the site's icon may be looked for again
        ProjectsChanged();
    }

    /// <summary>One look for a project's site icon at a time, and none again after one succeeded.</summary>
    public bool ClaimIcon(string id) => iconClaims.Add(id);

    /// <summary>Gives a tile the site's icon, if it still wants one; a failed look may be repeated later.</summary>
    public void SetSiteIcon(string id, byte[]? bytes)
    {
        var p = ProjectStore.Find(id);
        if (bytes == null || p == null || p.IconSource != "site" || p.Icon.Length > 0)
        {
            if (bytes == null) iconClaims.Remove(id);
            return;
        }
        try { p.Icon = Icons.Save(id, bytes); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return; }
        ProjectStore.Save(p);
        ProjectsChanged();
    }

    /// <summary>A site already running gives its icon at once; one behind a login gives it when opened here.</summary>
    public async void FetchSiteIcon(Project p)
    {
        if (!Uri.TryCreate(p.Url, UriKind.Absolute, out var url) || url.IsFile || Home.Is(p.Url) || NetGuard.ShouldBlock(url)
            || (p.Site.LocalOnly && NetGuard.Blocks(url)) || !ClaimIcon(p.Id)) return;
        SetSiteIcon(p.Id, await Favicons.FetchAsync(url));
    }

    public void StopProject(string id)
    {
        if (launchers.TryGetValue(id, out var launcher)) launcher.Stop();
    }

    public void DeleteProject(Project p)
    {
        StopProject(p.Id);
        if (launchers.TryGetValue(p.Id, out var launcher)) launcher.StopCommand();
        launchers.Remove(p.Id);
        Icons.Delete(p.Icon);
        ProjectStore.Delete(p.Id);
        ProjectsChanged();
    }

    /// <summary>The network switches changed (start page or /net): saved, and every tab and page follows.</summary>
    public void SetNet(bool localOnly, bool journal, string allowHosts, bool ignoreCors)
    {
        NetGuard.Set(localOnly, journal, ignoreCors);
        NetGuard.SetAllowed(allowHosts);
        S.SaveNet(localOnly, journal, NetGuard.AllowText, ignoreCors);
        foreach (var form in forms) form.ApplyNet();
        Gateway.Enforce();
        AdBlock.Ensure(); // waits for «только localhost» to be off
        if (Env != null && EngineKey() != engineKey) restart ??= RestartEngineAsync();
    }

    /// <summary>Every tab and page follows a changed switch.</summary>
    public void ApplyNet()
    {
        foreach (var form in forms) form.ApplyNet();
    }

    /// <summary>Would the engine restart if the settings went back to their defaults.</summary>
    public bool ResetRestartsEngine() =>
        NetGuard.LocalOnly || NetGuard.CorsOff || !S.Gpu || S.ExtraBrowserArgs.Length > 0;

    /// <summary>
    /// «Настройки по умолчанию»: settings.ini as after a new install, applied at once. The engine restarts
    /// only when its flags change (local mode, CORS, GPU, extra flags).
    /// </summary>
    public void ResetSettings()
    {
        S.ResetDefaults();
        L.Set(S.Language);
        foreach (var form in forms.ToList()) form.ApplyLanguage();
        Dev.Reload();
        Theme.Init(S.Theme);
        foreach (var form in forms.ToList()) form.ApplyTheme();
        // Saves the network values again (the same defaults) and restarts the engine if its flags changed
        SetNet(S.LocalOnly, S.NetJournal, S.AllowHosts, S.IgnoreCors);
        AdBlock.Changed();
        ApplyDev();
    }

    /// <summary>
    /// The interface language (the developer page): the windows follow at once; the pages and the engine's own
    /// words (its menus, Accept-Language) with a restart of the engine, which loads the tabs again.
    /// </summary>
    public void SetLanguage(string language)
    {
        S.SaveLanguage(language);
        foreach (var form in forms.ToList()) form.ApplyLanguage();
        if (Env != null && EngineKey() != engineKey) restart ??= RestartEngineAsync();
        else foreach (var form in forms.ToList()) form.ApplyNet(); // the same language: the pages get the choice back
    }

    /// <summary>
    /// A setting of the «Для разработчика» page (Защита, Вкладки, Оформление): saved, then applied everywhere.
    /// GPU is an engine flag: its change restarts the engine. Values the page could not have sent are ignored.
    /// </summary>
    public void SetSetting(string key, object value)
    {
        bool on = value is true;
        int minutes = value is int n ? n : -1;
        switch (key)
        {
            case "strictTracking": S.Change(s => s.StrictTracking = on); break;
            case "adBlock":
                S.Change(s => s.AdBlock = on);
                AdBlock.Changed();
                break;
            case "trustLocalCerts": S.Change(s => s.TrustLocalCerts = on); break;
            case "tabMute": S.Change(s => s.TabMute = on); break;
            case "externalToMain": S.Change(s => s.ExternalToMain = on); break;
            case "freezeTabs": S.Change(s => s.FreezeTabs = on); break;
            case "suspendAfter" when minutes >= 1 && minutes <= Settings.MaxMinutes: S.Change(s => s.SuspendAfter = minutes); break;
            case "unloadAfter" when minutes >= 0 && minutes <= Settings.MaxMinutes: S.Change(s => s.UnloadAfter = minutes); break;
            case "theme" when value is "dark" or "light" or "auto":
                S.Change(s => s.Theme = (string)value);
                Theme.Init(S.Theme);
                foreach (var form in forms.ToList()) form.ApplyTheme();
                break;
            case "gpu":
                S.Change(s => s.Gpu = on);
                if (Env != null && EngineKey() != engineKey) restart ??= RestartEngineAsync();
                break;
            default: return;
        }
        ApplyDev();
    }

    /// <summary>A switch of the «Для разработчика» page: every window's toolbar, menus and pages follow it.</summary>
    public void ApplyDev()
    {
        foreach (var form in forms.ToList()) form.ApplyDev();
    }

    /// <summary>The network flags the running engine was started with.</summary>
    string engineKey = "";

    /// <summary>The engine flags that can change while it runs: the language, the gateway (local mode), CORS, and GPU and extra flags on a reset.</summary>
    static string EngineKey() => L.EngineLanguage + "|" + NetGuard.LocalOnly + "|" + NetGuard.CorsOff + "|" + Current.S.Gpu + "|" + Current.S.ExtraBrowserArgs;

    /// <summary>
    /// Flags of the engine apply to its whole browser process: every tab is closed (keeping its address), the process
    /// is let go, and the tabs on screen load again in a new one. The others load when picked, as unloaded tabs do.
    /// </summary>
    async Task RestartEngineAsync()
    {
        // Out of the caller first: it may be a WebView's own event, and restart must hold this task before it ends
        await Task.Yield();
        try
        {
            do
            {
                var env = Env;
                if (env != null)
                {
                    var exited = new TaskCompletionSource<bool>();
                    env.BrowserProcessExited += (_, _) => exited.TrySetResult(true);
                    foreach (var form in forms.ToList()) form.UnloadAll();
                    await Task.WhenAny(exited.Task, Task.Delay(15000));
                }
                oldEnv = env ?? oldEnv;
                Env = null;
                envTask = null;
                await (envTask = CreateEnvironmentAsync());
            } while (EngineKey() != engineKey); // switched again meanwhile
        }
        catch (Exception) { envTask = null; }
        finally { restart = null; }
        foreach (var form in forms.ToList()) form.ReloadShown();
    }

    public void OpenProjectInNewWindow(Project p, string? link = null) => OpenWindow(null, project: p, link: link).Show();

    /// <summary>Startup touches a lot of host code the rest of the session never needs.</summary>
    public void TrimHostSoon()
    {
        var once = new System.Windows.Forms.Timer { Interval = 5000 };
        once.Tick += (_, _) =>
        {
            once.Dispose();
            Memory.Trim(Memory.SelfId);
        };
        once.Start();
    }

    /// <summary>A few seconds after a tab leaves the front, trims the renderers that show nothing on screen.</summary>
    public void TrimHiddenTabsSoon()
    {
        hiddenTrimTimer.Stop();
        hiddenTrimTimer.Start();
    }

    async void TrimHiddenTabs()
    {
        if (Env == null) return;
        var shown = new HashSet<uint>(forms.SelectMany(f => f.ShownFrameIds));
        try
        {
            foreach (var info in await Env.GetProcessExtendedInfosAsync())
            {
                // A renderer of several sites' frames is left alone if any of them is on screen
                if (info.ProcessInfo.Kind == CoreWebView2ProcessKind.Renderer
                    && !info.AssociatedFrameInfos.Any(f => shown.Contains(TopFrame(f).FrameId)))
                    Memory.Trim(info.ProcessInfo.ProcessId);
            }
        }
        catch (Exception) { } // the browser process is gone
    }

    static CoreWebView2FrameInfo TopFrame(CoreWebView2FrameInfo frame)
    {
        while (frame.ParentFrameInfo is { } parent) frame = parent;
        return frame;
    }

    /// <summary>
    /// Once every window is minimized or idle, hands the browser's pages back to Windows:
    /// after Chromium's own cleanup settles, once more, then every minute to catch what
    /// a page working in the background grows back. Touched pages fault back in cheaply.
    /// </summary>
    public void BackgroundChanged()
    {
        trimTimer.Stop();
        if (!forms.All(f => f.InBackground)) return;
        trimPass = 0;
        trimTimer.Interval = 5000;
        trimTimer.Start();
    }

    void OnTrimTick()
    {
        trimTimer.Interval = ++trimPass == 1 ? 25_000 : 60_000;
        Memory.Trim(Memory.SelfId);
        if (Env == null) return;
        try
        {
            foreach (var info in Env.GetProcessInfos()) Memory.Trim(info.ProcessId);
        }
        catch (Exception) { } // the browser process is gone
    }

    void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || !Theme.Refresh()) return;
        foreach (var form in forms) form.ApplyTheme();
    }

    protected override void ExitThreadCore()
    {
        exiting = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged; // a static event holds on to this
        foreach (var launcher in launchers.Values)
        {
            launcher.Stop();
            launcher.StopCommand();
        }
        base.ExitThreadCore();
    }
}
