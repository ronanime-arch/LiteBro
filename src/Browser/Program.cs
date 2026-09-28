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

namespace LiteBrowser;

static class Program
{
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [STAThread]
    static void Main(string[] args)
    {
        Settings settings;
        try { settings = Settings.Load(); }
        catch (Exception) { settings = new Settings(); } // a broken ini must not stop links from opening
        var argument = args.Select(a => a.Trim()).FirstOrDefault(a => a.Length > 0 && !a.StartsWith("--"));
        var url = argument != null && Uri.TryCreate(argument, UriKind.Absolute, out var parsed) ? parsed : null;
        if (argument != null && (url == null || !Router.IsLocal(url, settings)))
        {
            // An internet address, a file or a mail link: straight on to the main browser, no window here
            Router.OpenElsewhere(Router.ForOtherBrowser(argument, url), settings);
            return;
        }
        var address = url == null ? null : Router.Normalize(url).AbsoluteUri;
        // Some programs wait until the browser they started exits. So a launch with an address returns
        // at once: the page goes to the running LiteBrowser, or to a new one started on its own.
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
        SetCurrentProcessExplicitAppUserModelID(Associations.AppUserModelId); // same id as the shortcut
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
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

    public static App Current { get; private set; } = null!;
    public static readonly Icon AppIcon = LoadIcon();
    public Settings S { get; }
    public CoreWebView2Environment? Env { get; private set; }
    Task<CoreWebView2Environment>? envTask;
    readonly List<BrowserForm> forms = new();
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
        trimTimer.Tick += (_, _) => OnTrimTick();
        hiddenTrimTimer.Tick += (_, _) =>
        {
            hiddenTrimTimer.Stop();
            TrimHiddenTabs();
        };
        // Started with an address: show just that. A plain start opens the home page (and the harness).
        OpenWindow(startUrl, isMain: true, home: startUrl == null).Show();
        ui = SynchronizationContext.Current!; // installed by the first window
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
    public Task<CoreWebView2Environment> GetEnvironmentAsync() => envTask ??= CreateEnvironmentAsync();

    async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        var args = MemoryArgs;
        if (!S.Gpu) args += " --disable-gpu";
        if (S.ExtraBrowserArgs.Length > 0) args += " " + S.ExtraBrowserArgs;
        var options = new CoreWebView2EnvironmentOptions(args);
        Env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Settings.Dir, "WebView2"), options);
        return Env;
    }

    public BrowserForm OpenWindow(string? url, bool isMain = false, bool home = false, Project? project = null)
    {
        var form = new BrowserForm(url, isMain, home, project);
        forms.Add(form);
        form.FormClosed += (_, _) =>
        {
            forms.Remove(form);
            if (lastActive == form) lastActive = null;
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
        if (address.Length == 0)
        {
            Show(lastActive ?? forms.LastOrDefault());
            return true;
        }
        var window = forms.OrderByDescending(f => f == lastActive).FirstOrDefault(f => f.TryFocusTabOn(address));
        if (window == null)
        {
            window = lastActive ?? forms.LastOrDefault();
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

    /// <summary>The launcher of a project's program, kept for as long as the browser runs.</summary>
    public Launcher LauncherFor(Project p)
    {
        if (!launchers.TryGetValue(p.Id, out var launcher))
            launchers[p.Id] = launcher = new Launcher(p, () => ui.Post(_ => ProjectsChanged(), null));
        launcher.Project = p;
        return launcher;
    }

    public IEnumerable<string> RunningIds => launchers.Where(l => l.Value.Running).Select(l => l.Key);

    /// <summary>True when the address is on the site of a project whose program runs.</summary>
    public bool IsRunningSite(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var here) && !here.IsFile
        && launchers.Values.Any(l => l.Running
            && Uri.TryCreate(l.Project.Url, UriKind.Absolute, out var site) && BrowserForm.SameSite(site, here));

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
        if (!Uri.TryCreate(p.Url, UriKind.Absolute, out var url) || url.IsFile || !ClaimIcon(p.Id)) return;
        SetSiteIcon(p.Id, await Favicons.FetchAsync(url));
    }

    public void StopProject(string id)
    {
        if (launchers.TryGetValue(id, out var launcher)) launcher.Stop();
    }

    public void DeleteProject(Project p)
    {
        StopProject(p.Id);
        launchers.Remove(p.Id);
        Icons.Delete(p.Icon);
        ProjectStore.Delete(p.Id);
        ProjectsChanged();
    }

    public void OpenProjectInNewWindow(Project p) => OpenWindow(null, project: p).Show();

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
        var shown = new HashSet<uint>(forms.Select(f => f.ShownFrameId).OfType<uint>());
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

    protected override void ExitThreadCore()
    {
        exiting = true;
        foreach (var launcher in launchers.Values) launcher.Stop();
        base.ExitThreadCore();
    }
}
