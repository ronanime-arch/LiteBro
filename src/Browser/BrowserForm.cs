using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace LiteBro;

/// <summary>One browser window: a strip of tabs over a toolbar, each tab a WebView2 hosted directly through its controller.</summary>
sealed class BrowserForm : Form
{
    public static readonly string IconFont =
        FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    const string GlyphBack = "", GlyphForward = "", GlyphReload = "",
        GlyphStop = "", GlyphHome = "", GlyphGlobe = "";
    // A tab in the background is paused after a while, and after a long while closed until it is picked again
    static readonly TimeSpan SuspendAfter = TimeSpan.FromMinutes(1), UnloadAfter = TimeSpan.FromMinutes(5);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, string lParam);
    const int SetCueBanner = 0x1501; // EM_SETCUEBANNER

    readonly string? startUrl;
    readonly bool isMain, goHome;
    readonly Project? startProject;
    readonly string? startLink;
    readonly TabStrip strip = new();
    readonly Panel host = new() { Dock = DockStyle.Fill };
    // Between the two tabs of a split, dragged to share the width differently
    readonly Panel divider = new() { Cursor = Cursors.VSplit, Visible = false };
    // Over each half of a split, in the colour of its L or R mark on the tab
    readonly Panel stripeLeft = new() { BackColor = Theme.LeftPane, Visible = false },
        stripeRight = new() { BackColor = Theme.RightPane, Visible = false };
    readonly TextBox address = new()
    {
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        Font = new Font("Segoe UI", 10.5f),
        Margin = new Padding(6, 0, 6, 0),
    };
    readonly Label ram = new()
    {
        AutoSize = true,
        Anchor = AnchorStyles.None,
        Cursor = Cursors.Hand,
        Margin = new Padding(2, 0, 10, 0),
    };
    readonly ToolTip tips = new();
    readonly TableLayoutPanel bar;
    readonly ToolButton back, forward, reload, reset, home, country, star, emulate;
    const string GlyphStar = "\uE734", GlyphStarFilled = "\uE735", GlyphReset = "\uE75C", GlyphTools = "\uE90F";
    readonly Font countryGlyphFont, countryCodeFont = new("Segoe UI", 9f, FontStyle.Bold);
    readonly Timer ramTimer = new() { Interval = 2000 };
    // How long a window may sit in the background before it gives memory back
    readonly Timer backgroundTimer = new() { Interval = 30_000 };
    // Often enough for the minute of SuspendAfter to be kept to within a quarter
    readonly Timer freezeTimer = new() { Interval = 15_000 };
    readonly List<Tab> tabs = new();
    Tab? active;
    // Two tabs side by side: the one in front is one of them, the other stays on screen beside it
    Tab? paneLeft, paneRight;
    float splitAt = .5f;
    // The two halves of a split scroll together
    bool syncScroll;
    int? dragFrom;
    // A page in full screen (a video, a game): the window covers its monitor and shows that page alone
    Tab? fullScreen;
    FormWindowState beforeFullState;
    Rectangle beforeFullBounds;
    double zoom = 1;
    bool minimized, inBackground;

    CoreWebView2? Core => active?.Core;
    /// <summary>Minimized, or not used for a while: its tab in front runs with the Low memory target.</summary>
    public bool InBackground => inBackground;
    /// <summary>The main frames on screen in this window: none while it is minimized.</summary>
    public IEnumerable<uint> ShownFrameIds =>
        minimized ? Enumerable.Empty<uint>() : OnScreen.Select(t => t.Core?.FrameId).OfType<uint>();

    bool Split => paneLeft != null;
    bool IsPane(Tab tab) => Split && (tab == paneLeft || tab == paneRight);
    /// <summary>The other tab on screen in a split.</summary>
    Tab? Partner => !Split ? null : active == paneLeft ? paneRight : paneLeft;
    /// <summary>The tabs on screen: the one in front and, in a split, the one beside it.</summary>
    IEnumerable<Tab> OnScreen => new[] { active, Partner }.OfType<Tab>();

    /// <param name="url">What to open in the first tab.</param>
    /// <param name="openHome">Open the start page with the project tiles.</param>
    /// <param name="project">A project to open (and start, if need be) in the first tab.</param>
    /// <param name="link">One of the project's links to open instead of its own address.</param>
    public BrowserForm(string? url, bool isMain, bool openHome, Project? project = null, string? link = null)
    {
        startUrl = url;
        this.isMain = isMain;
        goHome = openHome;
        startProject = project;
        startLink = link;
        Text = "LiteBro";
        Icon = App.AppIcon;
        Size = new Size(1100, 800);
        MinimumSize = new Size(480, 320);

        back = MakeButton(GlyphBack, () => Step(-1));
        forward = MakeButton(GlyphForward, () => Step(1));
        reload = MakeButton(GlyphReload, ReloadOrStop);
        reset = MakeButton(GlyphReset, ResetSite);
        reset.Visible = false;
        home = MakeButton(GlyphHome, () => { if (ModifierKeys == Keys.Control) OpenNewTab(null); else GoHome(); });
        home.MouseUp += (_, e) => { if (e.Button == MouseButtons.Middle) OpenNewTab(null); };
        country = MakeButton(GlyphGlobe, ShowCountryMenu);
        country.Visible = false;
        star = MakeButton(GlyphStar, ShowFavoriteMenu);
        emulate = MakeButton(GlyphTools, ShowToolsMenu);
        emulate.Visible = false;
        countryGlyphFont = country.Font;

        bar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 10,
            RowCount = 1,
            Padding = new Padding(4, 3, 0, 3),
        };
        for (int i = 0; i < 5; i++) bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++) bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var cells = new Control[] { back, forward, reload, reset, home, address, emulate, star, country, ram };
        bar.Controls.AddRange(cells);
        // Fixed cells: a hidden button (reset, emulate, star, country) leaves its column empty instead of shifting the rest along
        for (int i = 0; i < cells.Length; i++) bar.SetCellPosition(cells[i], new TableLayoutPanelCellPosition(i, 0));

        // Docking goes from the last added: the strip on top, the toolbar under it, the page in what is left
        Controls.Add(host);
        Controls.Add(bar);
        Controls.Add(strip);

        strip.Picked += SelectTab;
        strip.Closing += CloseTab;
        strip.NewTab += () => OpenNewTab(null);
        strip.Menu += ShowTabMenu;
        strip.Mute += ToggleMute;
        host.Controls.Add(divider);
        host.Controls.Add(stripeLeft);
        host.Controls.Add(stripeRight);
        divider.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) dragFrom = e.X; };
        divider.MouseMove += (_, e) =>
        {
            if (dragFrom is not { } from || host.Width == 0) return;
            splitAt = Math.Max(.15f, Math.Min(.85f, (float)(divider.Left + e.X - from + divider.Width / 2) / host.Width));
            LayoutPanes();
        };
        divider.MouseUp += (_, _) => dragFrom = null;
        SetTips();
        ram.Click += (_, _) => Core?.OpenTaskManagerWindow();
        address.KeyDown += OnAddressKeyDown;
        address.GotFocus += (_, _) => BeginInvoke(new Action(address.SelectAll));
        ramTimer.Tick += (_, _) => UpdateRam();
        backgroundTimer.Tick += (_, _) =>
        {
            backgroundTimer.Stop();
            EnterBackground();
        };
        freezeTimer.Tick += (_, _) => FreezeIdleTabs();
        reloadTimer.Tick += (_, _) => ReloadChanged();
        FormClosed += (_, _) =>
        {
            foreach (var w in watchers.Values) w.Dispose();
            watchers.Clear();
        };
        fitTimer.Tick += (_, _) =>
        {
            fitTimer.Stop();
            foreach (var tab in OnScreen)
                if (tab.Device != null && tab.Core is { } core) _ = Emulation.ApplyAsync(core, tab.Device, tab.Speed, RoomOf(tab));
        };
        host.Resize += (_, _) => LayoutPanes();
        address.HandleCreated += (_, _) => Theme.ApplyEdit(address.Handle);
        ApplyTheme();
    }

    /// <summary>The toolbar's glyphs differ in width and height: every button gets the box of the largest.</summary>
    void EvenButtons()
    {
        var buttons = new[] { back, forward, reload, reset, home, emulate, star, country };
        foreach (var b in buttons) b.MinimumSize = Size.Empty;
        var size = new Size(buttons.Max(b => b.PreferredSize.Width), buttons.Max(b => b.PreferredSize.Height));
        size.Width = size.Height = Math.Max(size.Width, size.Height);
        foreach (var b in buttons) b.MinimumSize = size;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        BeginInvoke(new Action(EvenButtons)); // after the fonts are scaled
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyFrame(Handle); // before the window first shows
    }

    /// <summary>Colours of the theme on the frame, the tabs, the toolbar and behind the pages.</summary>
    public void ApplyTheme()
    {
        BackColor = host.BackColor = Theme.PageBackground;
        divider.BackColor = Theme.Strip;
        bar.BackColor = Theme.Face;
        foreach (var b in new[] { back, forward, reload, reset, home, emulate, star, country })
        {
            b.BackColor = Theme.Face;
            b.ForeColor = Theme.Text;
            b.FlatAppearance.MouseOverBackColor = Theme.Mix(Theme.Face, Theme.Text, .12f);
            b.FlatAppearance.MouseDownBackColor = Theme.Mix(Theme.Face, Theme.Text, .2f);
        }
        if (active != null) ShowStar(active); // gold stays gold
        if (active != null) ShowEmulation(active); // and green green
        address.BackColor = Theme.Field;
        address.ForeColor = Theme.Text;
        ram.ForeColor = Theme.Dim;
        strip.ApplyTheme();
        if (IsHandleCreated)
        {
            Theme.ApplyFrame(Handle);
            Theme.ApplyEdit(address.Handle);
            address.Invalidate();
        }
        foreach (var tab in tabs)
            if (tab.Ctl is { } c) ApplyTheme(c);
    }

    static void ApplyTheme(CoreWebView2Controller c)
    {
        c.DefaultBackgroundColor = Theme.PageBackground;
        // "auto" leaves pages to Windows' own choice, as the frame
        c.CoreWebView2.Profile.PreferredColorScheme = App.Current.S.Theme switch
        {
            "dark" => CoreWebView2PreferredColorScheme.Dark,
            "light" => CoreWebView2PreferredColorScheme.Light,
            _ => CoreWebView2PreferredColorScheme.Auto,
        };
    }

    /// <summary>The toolbar's hints that do not change with the page (the others are set in ShowState).</summary>
    void SetTips()
    {
        tips.SetToolTip(back, L.T("Назад (Alt+←)"));
        tips.SetToolTip(forward, L.T("Вперёд (Alt+→)"));
        tips.SetToolTip(reload, L.T("Обновить (F5)"));
        tips.SetToolTip(reset, L.T("Сбросить Service Worker и кэш сайта, загрузить заново (Ctrl+Shift+R)"));
        tips.SetToolTip(home, L.T("Проекты (Alt+Home). Ctrl+клик или колёсико — в новой вкладке"));
        tips.SetToolTip(country, L.T("Страна поиска"));
        tips.SetToolTip(emulate, L.T("Инструменты: эмуляция устройства и сети"));
        tips.SetToolTip(ram, L.T("Память браузера, как в диспетчере задач. Клик — диспетчер процессов (Shift+Esc)"));
        if (IsHandleCreated) SendMessage(address.Handle, SetCueBanner, (IntPtr)1, L.T("Адрес или поиск"));
    }

    /// <summary>The interface language changed: the toolbar's words now, the pages when the engine loads them again.</summary>
    public void ApplyLanguage()
    {
        SetTips();
        if (active != null) ShowState(active, switched: true);
        strip.Invalidate();
    }

    ToolButton MakeButton(string glyph, Action click)
    {
        var b = new ToolButton
        {
            Text = glyph,
            Font = new Font(IconFont, 11f),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlatStyle = FlatStyle.Flat,
            Padding = new Padding(4, 1, 4, 1),
            Margin = new Padding(1, 0, 1, 0),
        };
        b.FlatAppearance.BorderSize = 0;
        b.Click += (_, _) => click();
        return b;
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        SendMessage(address.Handle, SetCueBanner, (IntPtr)1, L.T("Адрес или поиск"));
        EvenButtons();
        var saved = isMain ? Settings.LoadWindow() : null;
        if (saved is { } w && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(w.Bounds)))
        {
            Bounds = w.Bounds;
            if (w.Maximized) WindowState = FormWindowState.Maximized;
        }
        else if (isMain)
        {
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Bounds = new Rectangle(area.X + area.Width / 10, area.Y + area.Height / 12,
                area.Width * 4 / 5, area.Height * 5 / 6);
        }
        zoom = saved?.Zoom ?? 1;

        var tab = await CreateTabAsync(startProject != null ? null : goHome ? Home.Url : startUrl);
        if (tab == null)
        {
            Close();
            return;
        }
        Add(tab, front: true);
        if (startProject != null) OpenProject(tab, startProject, startLink);
        ramTimer.Start();
        UpdateRam();
        freezeTimer.Start();
        // A window that opened behind others never gets a Deactivate to start the countdown
        if (ActiveForm != this) backgroundTimer.Start();
    }

    /// <param name="url">What to open; null for a popup the opening page fills in itself.</param>
    /// <param name="profile">The WebView2 profile; by default the one of the project the address belongs to.</param>
    async Task<Tab?> CreateTabAsync(string? url, string? profile = null)
    {
        var tab = new Tab { Profile = profile ?? ProfileFor(url) };
        return await LoadAsync(tab, url) ? tab : null;
    }

    /// <summary>Gives a tab its WebView: a new tab, or one closed after a long time in the background.</summary>
    async Task<bool> LoadAsync(Tab tab, string? url)
    {
        if (url != null) tab.Address = url; // the strip and the title know the page before the WebView does
        CoreWebView2Controller c;
        try
        {
            while (true)
            {
                var env = await App.Current.GetEnvironmentAsync();
                if (IsDisposed) return false;
                c = await App.Current.CreateControllerAsync(env, host.Handle, tab.Profile);
                // The engine restarted meanwhile: this WebView belongs to the old one, which is to go
                if (env == App.Current.Env && !App.Current.Restarting) break;
                c.Close();
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                MessageBox.Show(this, L.T("Не удалось запустить движок WebView2.\n\n") + ex.Message +
                    L.T("\n\nЕсли меняли settings.ini, закройте все окна LiteBro и откройте снова."),
                    "LiteBro", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        if (IsDisposed || tab.Closed)
        {
            c.Close();
            return false;
        }
        // Loaded twice at once (picked again while loading, or after an engine restart): the first one stays
        if (tab.Ctl != null)
        {
            c.Close();
            return true;
        }
        c.IsVisible = false; // shown once it is the tab in front
        tab.Ctl = c;
        Setup(tab);
        if (url != null) c.CoreWebView2.Navigate(url);
        return true;
    }

    void Setup(Tab tab)
    {
        var c = tab.Ctl!;
        ApplyTheme(c);
        c.Bounds = BoundsOf(tab);
        // A click into the tab beside brings it forward: the toolbar follows it
        c.GotFocus += (_, _) => { if (tab == Partner) FocusPane(tab); };
        c.ZoomFactor = zoom;
        c.AcceleratorKeyPressed += OnAcceleratorKeyPressed;
        var core = c.CoreWebView2;
        // Features this browser does not use, each with a per-page cost
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        // SmartScreen sends the addresses to Microsoft: no telemetry
        try { core.Settings.IsReputationCheckingRequired = false; }
        catch (Exception) { } // an older WebView2 runtime
        // The start page is served from here and talks to the browser through web messages
        core.AddWebResourceRequestedFilter(Home.Url + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnHomeRequest;
        core.WebResourceRequested += (_, e) => OnNetRequest(tab, e);
        tab.NetFilter = false;
        tab.MockFilters = new();
        tab.NetScript = null;
        tab.NetResponse = null;
        ApplyNet(tab);
        // A new WebView (another profile, loaded again) keeps the tab's emulation
        if (tab.Device != null || tab.Speed != null) _ = Emulation.ApplyAsync(core, tab.Device, tab.Speed, RoomOf(tab));
        core.WebMessageReceived += (_, e) => OnWebMessage(tab, e);
        core.FaviconChanged += (_, _) => TakeSiteIcon(tab);

        core.DocumentTitleChanged += (_, _) =>
        {
            tab.Title = (core.DocumentTitle ?? "").Trim();
            strip.Invalidate();
            ShowState(tab);
        };
        core.SourceChanged += (_, _) =>
        {
            if (tab.Address.StartsWith(ProgramLog.Url(ProgramLog.Shell)) && !core.Source.StartsWith(ProgramLog.Url(ProgramLog.Shell)))
                App.Current.ShellMaybeUnused();
            if (tab.Term != null && !TermPage.Is(core.Source)) StopTerm(tab);
            // A tab that was a terminal is an ordinary one once it leaves: a later /term there starts nothing
            if (tab.TermDir != null && !TermPage.Is(core.Source)) tab.TermDir = tab.TermCommand = null;
            tab.Address = core.Source;
            ShowState(tab);
        };
        core.HistoryChanged += async (_, _) =>
        {
            ShowState(tab);
            // Read now: when the WebView is replaced there is no time to ask it
            if (await HistoryOf(core) is { } h && tab.Core == core) Remember(tab, h);
        };
        core.NavigationStarting += (_, e) =>
        {
            // A search typed on Google's own page keeps the picked country; history is left as it was
            if (e.NavigationKind == CoreWebView2NavigationKind.NewDocument && !e.IsRedirected &&
                SearchCountry.Redirect(e.Uri) is { } withCountry)
            {
                e.Cancel = true;
                BeginInvoke(new Action(() => core.Navigate(withCountry)));
                return;
            }
            // A project with a profile of its own is opened in it, and one without leaves another's: the tab moves over.
            // Other sites (a login page the project sends to, say) stay in the profile the tab is in.
            if (e.NavigationKind == CoreWebView2NavigationKind.NewDocument && !e.IsRedirected
                && Uri.TryCreate(e.Uri, UriKind.Absolute, out var to) && OwnerOf(tab, to) is { } owner && owner.Profile != tab.Profile)
            {
                e.Cancel = true;
                var target = e.Uri;
                BeginInvoke(new Action(async () =>
                {
                    if (await UseProfileAsync(tab, owner.Profile)) tab.Core?.Navigate(target);
                }));
                return;
            }
            // A new page drops what was ahead, as the engine drops its own forward entries; this program's pages do not
            if (e.NavigationKind == CoreWebView2NavigationKind.NewDocument && !e.IsRedirected && !tab.Stepping && !e.Uri.StartsWith("data:"))
                tab.Ahead.Clear();
            tab.Stepping = false;
            // Back on the start page nothing is to be tried again on F5
            if (Home.Is(e.Uri)) tab.LastProject = null;
            if (Home.Is(e.Uri) || !(e.Uri.StartsWith("about:") || e.Uri.StartsWith("data:")))
            {
                tab.ShowingInternalPage = false;
                tab.FailedUrl = null;
            }
            SetLoading(tab, true);
        };
        core.NavigationCompleted += (_, e) =>
        {
            SetLoading(tab, false);
            // A browser page gets its state without waiting for its "ready", which may be lost while the engine starts
            if (e.IsSuccess && Home.Is(core.Source))
            {
                SendProjects(tab);
                SendNet(tab);
            }
            if (e.IsSuccess && !tab.ShowingInternalPage && !IsInternal(core.Source) && Dev.On("json")) ShowJson(core);
            // A site that does not answer gets this program's page, not the engine's own one that names Edge
            if (!e.IsSuccess && IsUnreachable(e.WebErrorStatus) && Uri.TryCreate(core.Source, UriKind.Absolute, out var failed)
                && !Home.Is(core.Source) && (failed.Scheme == "http" || failed.Scheme == "https"))
            {
                var url = failed.AbsoluteUri;
                BeginInvoke(new Action(() =>
                {
                    if (tab.Core != core) return;
                    ShowInternalPage(tab, Pages.Unreachable(url, ProjectOf(failed)));
                    tab.FailedUrl = url;
                    ShowState(tab, switched: true);
                }));
            }
            if (syncScroll && IsPane(tab)) WatchScroll(tab, true);
            if (!tab.TrimmedAfterLoad)
            {
                tab.TrimmedAfterLoad = true;
                App.Current.TrimHostSoon();
            }
        };
        core.NewWindowRequested += OnNewWindowRequested;
        core.SaveAsUIShowing += (_, e) => OnSaveAs(tab, core, e);
        core.ContextMenuRequested += (_, e) => AddPageTools(tab, core, e);
        // Not from inside the WebView's own event: closing the tab closes that WebView
        core.WindowCloseRequested += (_, _) => BeginInvoke(new Action(() => CloseTab(tab)));
        core.IsDocumentPlayingAudioChanged += (_, _) =>
        {
            tab.PlayingAudio = core.IsDocumentPlayingAudio;
            strip.Invalidate();
        };
        if (tab.Muted) core.IsMuted = true;
        core.ContainsFullScreenElementChanged += (_, _) => BeginInvoke(new Action(() =>
        {
            if (tab.Core == core) SetFullScreen(tab, core.ContainsFullScreenElement);
        }));
        core.ServerCertificateErrorDetected += OnCertificateError;
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited) core.Reload();
        };
    }

    /// <summary>The site is not there: a server that is off, a name that does not resolve, no network.</summary>
    static bool IsUnreachable(CoreWebView2WebErrorStatus status) => status is CoreWebView2WebErrorStatus.CannotConnect
        or CoreWebView2WebErrorStatus.HostNameNotResolved or CoreWebView2WebErrorStatus.ConnectionReset
        or CoreWebView2WebErrorStatus.Disconnected or CoreWebView2WebErrorStatus.Timeout
        or CoreWebView2WebErrorStatus.ServerUnreachable;

    /// <summary>The profile an address opens in: its project's own, else the shared one.</summary>
    static string ProfileFor(string? url) =>
        url != null && Uri.TryCreate(url, UriKind.Absolute, out var u) && ProjectOf(u) is { } p ? p.Profile : "";

    /// <summary>
    /// Moves a tab to another profile: its WebView is replaced (history does not come along) and shows nothing yet.
    /// An unloaded tab just loads in the new profile when picked.
    /// </summary>
    async Task<bool> UseProfileAsync(Tab tab, string profile)
    {
        if (tab.Profile == profile) return tab.Ctl == null || tab.Core != null;
        tab.Profile = profile;
        if (tab.Ctl is not { } old) return true;
        await Task.Yield(); // maybe called from the old WebView's own event: closed after it returns
        if (tab.Ctl != old || tab.Profile != profile) return false;
        StopTerm(tab);
        KeepHistory(tab, current: true, ahead: false);
        tab.Ctl = null;
        tab.Suspended = tab.Loading = tab.PlayingAudio = false;
        old.Close();
        if (!await LoadAsync(tab, null) || tab.Closed) return false;
        if (OnScreen.Contains(tab)) await ShowPaneAsync(tab);
        else SendToBackground(tab);
        strip.Invalidate();
        return tab.Core != null;
    }

    /// <summary>The project whose site (or one of whose links) an address is on.</summary>
    static Project? ProjectOf(Uri url) => ProjectStore.All.FirstOrDefault(p => p.Addresses().Any(a =>
        Uri.TryCreate(a, UriKind.Absolute, out var site) && !site.IsFile && !Home.Is(a) && SameSite(site, url)));

    /// <summary>The project an address in a tab belongs to: the tile the tab was opened from if the address is on its sites.</summary>
    static Project? OwnerOf(Tab tab, Uri url)
    {
        if (url.IsFile) return null;
        bool On(Project p) => p.Addresses().Any(a => Uri.TryCreate(a, UriKind.Absolute, out var site) && !site.IsFile && !Home.Is(a) && SameSite(site, url));
        if (tab.LastProject is { } last && ProjectStore.Find(last.Id) is { } p && On(p)) return p;
        // Of the tiles on that site, one in the tab's own profile keeps the tab where it is
        return ProjectStore.All.FirstOrDefault(x => x.Profile == tab.Profile && On(x)) ?? ProjectOf(url);
    }

    static bool IsInternal(string uri) => uri.StartsWith("about:") || uri.StartsWith("data:") || Home.Is(uri);

    /// <summary>Same scheme, host and port.</summary>
    public static bool SameSite(Uri a, Uri b) =>
        Uri.Compare(a, b, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    static bool IsOn(Tab tab, Uri url) =>
        !url.IsFile && Uri.TryCreate(tab.Site, UriKind.Absolute, out var here) && SameSite(here, url);

    /// <summary>What the address bar shows for a tab: nothing for the start page and blank pages.</summary>
    static string AddressOf(Tab tab) => IsInternal(tab.Site) ? "" : tab.Site;

    /// <summary>The toolbar and the window title follow the tab in front.</summary>
    /// <param name="switched">The tab has just come forward: the address bar shows its address even while typed in.</param>
    void ShowState(Tab tab, bool switched = false)
    {
        if (tab != active) return;
        Text = tab.Title.Length == 0 ? "LiteBro" : tab.Title + " — LiteBro";
        reload.Text = tab.Loading ? GlyphStop : GlyphReload;
        tips.SetToolTip(reload, tab.Loading ? L.T("Остановить") : L.T("Обновить (F5)"));
        if (switched || !address.Focused) address.Text = AddressOf(tab);
        ShowCountry(tab);
        ShowStar(tab);
        reset.Visible = SiteOrigin(tab) != null && Dev.On("reset");
        // Buttons switched off on the «Для разработчика» page; their keys still work
        back.Visible = Dev.On("back");
        forward.Visible = Dev.On("forward");
        reload.Visible = Dev.On("reload");
        home.Visible = Dev.On("home");
        ram.Visible = Dev.On("ram");
        ShowEmulation(tab);
        ShowStrip();
        if (App.Current.S.AutoReload || watchers.Count > 0) WatchFolders();
    }

    /// <summary>The tab strip, hidden while the start page is all the window shows: it comes with the first site or tile.</summary>
    void SetTabs()
    {
        strip.SetTabs(tabs, active, paneLeft, paneRight);
        ShowStrip();
    }

    void ShowStrip()
    {
        bool show = fullScreen == null && (tabs.Count != 1 || !Home.IsTiles(tabs[0].Site));
        if (strip.Visible != show) strip.Visible = show;
    }

    /// <summary>The address a tab could be saved under: a site or a file, not a page of this program.</summary>
    static string? Savable(Tab tab) =>
        !tab.ShowingInternalPage && !IsInternal(tab.Site) && Openable(tab.Site) is { } u ? u.AbsoluteUri : null;

    /// <summary>The tile an address is saved in, as its own address or as one of its links.</summary>
    static (Project Project, ProjectLink? Link)? SavedIn(string url)
    {
        foreach (var p in ProjectStore.All)
        {
            if (SameAddress(p.Url, url)) return (p, null);
            if (p.Links.FirstOrDefault(l => SameAddress(l.Url, url)) is { } link) return (p, link);
        }
        return null;
    }

    static bool SameAddress(string a, string b) =>
        Uri.TryCreate(a.Trim(), UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y) && x == y;

    /// <summary>The project a tab belongs to: the tile it was opened from, else the one on the same site.</summary>
    static Project? OwnerOf(Tab tab)
    {
        if (tab.LastProject != null && ProjectStore.Find(tab.LastProject.Id) is { } opened) return opened;
        if (!Uri.TryCreate(tab.Site, UriKind.Absolute, out var here) || here.IsFile) return null;
        return ProjectStore.All.FirstOrDefault(p => p.Addresses().Any(a =>
            Uri.TryCreate(a, UriKind.Absolute, out var site) && SameSite(site, here)));
    }

    /// <summary>The star: on sites and files, filled when the address is already on a tile.</summary>
    void ShowStar(Tab tab)
    {
        if (TermTileOf(tab) is { } t)
        {
            star.Visible = Dev.On("star");
            var tile = SavedTerm(t.Dir, t.Command);
            var glyph = tile == null ? GlyphStar : GlyphStarFilled;
            if (star.Text != glyph) star.Text = glyph;
            star.ForeColor = tile == null ? Theme.Text : Color.FromArgb(0xf5, 0xb3, 0x01);
            tips.SetToolTip(star, tile != null ? L.T("В избранном: плитка «") + tile.Name + L.T("»")
                : L.T("Добавить в избранное: терминал в этой папке") + (t.Command.Length > 0 ? L.T(" с командой ") + t.Command : ""));
            return;
        }
        var url = Savable(tab);
        star.Visible = url != null && Dev.On("star");
        if (url == null) return;
        var saved = SavedIn(url);
        var text = saved == null ? GlyphStar : GlyphStarFilled;
        if (star.Text != text) star.Text = text;
        star.ForeColor = saved == null ? Theme.Text : Color.FromArgb(0xf5, 0xb3, 0x01);
        tips.SetToolTip(star, saved is { } s
            ? L.T("В избранном: ") + (s.Link == null ? L.T("плитка «") + s.Project.Name + L.T("»") : L.T("ссылка проекта «") + s.Project.Name + L.T("»"))
            : L.T("Добавить в избранное: плиткой или ссылкой проекта"));
    }

    /// <summary>
    /// The star's menu: the page as a tile of its own, with the launch settings of the project it belongs to,
    /// or as a link of a project (its backend, say).
    /// </summary>
    void ShowFavoriteMenu()
    {
        if (active is { } termTab && TermTileOf(termTab) is { } t)
        {
            ShowTermMenu(t.Dir, t.Command);
            return;
        }
        if (active is not { } tab || Savable(tab) is not { } url) return;
        var name = tab.Title.Length > 0 ? tab.Title : NameOf(new Uri(url));
        if (name.Length > 80) name = name.Substring(0, 80).TrimEnd() + "…";
        var owner = OwnerOf(tab);
        var menu = NewMenu();
        if (SavedIn(url) is { } saved)
        {
            menu.Items.Add(new ToolStripMenuItem(saved.Link == null
                ? L.T("Это адрес плитки «") + saved.Project.Name + L.T("»")
                : L.T("Ссылка «") + saved.Link.Name + L.T("» проекта «") + saved.Project.Name + L.T("»")) { Enabled = false });
            if (saved.Link is { } link)
                menu.Items.Add(new ToolStripMenuItem(L.T("Убрать ссылку из проекта"), null, (_, _) => RemoveLink(saved.Project, link)));
            menu.Items.Add(new ToolStripSeparator());
        }
        menu.Items.Add(new ToolStripMenuItem(owner != null && owner.Exe.Length > 0
                ? L.T("Сохранить плиткой (с запуском «") + owner.Name + L.T("»)") : L.T("Сохранить плиткой"),
            null, (_, _) => SaveAsTile(url, name, owner)));
        var links = new ToolStripMenuItem(L.T("Добавить ссылкой в проект"));
        // The project the page belongs to comes first
        foreach (var p in ProjectStore.All.OrderBy(p => p == owner ? 0 : 1))
        {
            var item = new ToolStripMenuItem(p.Name, null, (_, _) => AddLink(p, url, name)) { Checked = p == owner };
            if (SameAddress(p.Url, url) || p.Links.Any(l => SameAddress(l.Url, url))) item.Enabled = false;
            else if (p.Links.Count >= MaxLinks) { item.Enabled = false; item.Text += L.T(" (уже ") + MaxLinks + L.T(" ссылок)"); }
            links.DropDownItems.Add(item);
        }
        if (links.DropDownItems.Count == 0) links.Enabled = false;
        else if (Theme.Dark)
        {
            links.DropDown.Renderer = menu.Renderer;
            links.DropDown.ForeColor = Theme.Text;
        }
        menu.Items.Add(links);
        menu.Show(star, new Point(0, star.Height));
    }

    /// <summary>What the star of a terminal saves: the folder and the command running there (none at the prompt).</summary>
    static (string Dir, string Command)? TermTileOf(Tab tab)
    {
        if (tab.Term == null || tab.Core is not { } core || !TermPage.Is(core.Source)) return null;
        if (tab.TermRunning is { } running) return (tab.TermRunningDir ?? tab.TermCwd ?? tab.TermDir ?? "", running);
        return (tab.TermCwd ?? tab.TermDir ?? "", "");
    }

    static Project? SavedTerm(string dir, string command) => ProjectStore.All.FirstOrDefault(p => p.IsTerminal
        && string.Equals(p.WorkDir.Trim().TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && p.Command == command);

    void ShowTermMenu(string dir, string command)
    {
        var menu = NewMenu();
        if (SavedTerm(dir, command) is { } saved)
            menu.Items.Add(new ToolStripMenuItem(L.T("Это плитка «") + saved.Name + L.T("»")) { Enabled = false });
        else
            menu.Items.Add(new ToolStripMenuItem(command.Length > 0 ? L.T("Сохранить плиткой: ") + Short(command) + L.T(" в этой папке") : L.T("Сохранить плиткой: терминал в этой папке"),
                null, (_, _) => SaveTermTile(dir, command)));
        menu.Show(star, new Point(0, star.Height));
        static string Short(string s) => s.Length > 40 ? s.Substring(0, 40) + "…" : s;
    }

    /// <summary>A terminal tile: a click opens PowerShell in the folder and types the command in.</summary>
    void SaveTermTile(string dir, string command)
    {
        var folder = Path.GetFileName(dir.TrimEnd('\\'));
        if (folder.Length == 0) folder = dir;
        var first = command.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
        var p = new Project
        {
            Name = first.Length > 0 ? folder + " — " + first[0] : folder,
            Url = TermPage.Url,
            Exe = first.Length > 0 ? first[0] : "",
            Args = first.Length > 1 ? first[1] : "",
            WorkDir = dir,
            Letters = ">_",
            Color = "#7d35aa",
            IconSource = "none",
        };
        ProjectStore.Save(p); // the picture is named after the Id
        if (ProgramIcon(p.Exe, dir) is { } png)
        {
            try
            {
                p.Icon = Icons.Save(p.Id, png);
                p.IconSource = "file";
                ProjectStore.Save(p);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }
        App.Current.ProjectSaved(p);
        if (active != null) ShowStar(active);
    }

    /// <summary>The picture of the command's program (claude.exe, ollama.exe…); none for scripts and npm shims.</summary>
    static byte[]? ProgramIcon(string exe, string dir)
    {
        exe = Environment.ExpandEnvironmentVariables(exe.Trim().Trim('"'));
        if (exe.Length == 0) return null;
        string? path = null;
        try
        {
            var local = Path.Combine(dir, exe);
            path = File.Exists(local) ? local : File.Exists(local + ".exe") ? local + ".exe" : Launcher.ResolveExe(exe);
        }
        catch (ArgumentException) { } // a path with characters Windows does not allow
        return path != null ? Icons.FromProgram(path) : null;
    }

    /// <summary>A new tile for the address; a project's page takes the project's program, arguments and colour.</summary>
    void SaveAsTile(string url, string name, Project? owner)
    {
        var p = new Project { Name = name, Url = url };
        if (owner != null)
        {
            p.Color = owner.Color;
            p.Exe = owner.Exe;
            p.Args = owner.Args;
            p.WorkDir = owner.WorkDir;
            // The address is saved as it is, so the program's printed one is not put in its place
            p.OpenPrintedUrl = false;
        }
        ProjectStore.Save(p);
        App.Current.ProjectSaved(p);
        App.Current.FetchSiteIcon(p);
    }

    void AddLink(Project p, string url, string name)
    {
        if (p.Links.Count >= MaxLinks || p.Links.Any(l => SameAddress(l.Url, url))) return;
        p.Links.Add(new ProjectLink { Name = name, Url = url });
        ProjectStore.Save(p);
        App.Current.ProjectSaved(p);
    }

    void RemoveLink(Project p, ProjectLink link)
    {
        if (!p.Links.Remove(link)) return;
        ProjectStore.Save(p);
        App.Current.ProjectSaved(p);
    }

    /// <summary>The country button: on Google search only, with the code of the picked country or a globe.</summary>
    void ShowCountry(Tab tab)
    {
        country.Visible = !tab.ShowingInternalPage && SearchCountry.IsGoogleSearch(tab.Site) && Dev.On("country");
        var picked = SearchCountry.Current;
        var text = picked?.Code ?? GlyphGlobe;
        if (country.Text == text) return;
        country.Text = text;
        country.Font = picked == null ? countryGlyphFont : countryCodeFont;
        tips.SetToolTip(country, picked == null ? L.T("Страна поиска") : L.T("Страна поиска: ") + picked.Name);
    }

    void ShowCountryMenu()
    {
        var picked = SearchCountry.Current;
        var menu = NewMenu();
        menu.Items.Add(new ToolStripMenuItem(L.T("Как обычно (без страны)"), null, (_, _) => PickCountry(null)) { Checked = picked == null });
        menu.Items.Add(new ToolStripSeparator());
        foreach (var c in SearchCountry.All)
            menu.Items.Add(new ToolStripMenuItem(c.Name + " (" + c.Code + ")", null, (_, _) => PickCountry(c)) { Checked = c == picked });
        menu.Show(country, new Point(0, country.Height));
    }

    /// <summary>A menu in the theme's colours, gone once closed: the next one is built again with the current state.</summary>
    ContextMenuStrip NewMenu()
    {
        var menu = new ContextMenuStrip();
        if (Theme.Dark)
        {
            menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
            menu.ForeColor = Theme.Text;
        }
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        return menu;
    }

    /// <summary>A tab's right-click menu: side by side with the tab in front, back to one, close.</summary>
    void ShowTabMenu(Tab tab, Point at)
    {
        var menu = NewMenu();
        if (active != null && !IsPane(tab) && tab != active && Dev.On("split"))
            menu.Items.Add(new ToolStripMenuItem(L.T("Открыть рядом"), null, (_, _) => SplitWith(tab)));
        if (Split)
        {
            menu.Items.Add(new ToolStripMenuItem(L.T("Синхронная прокрутка"), null, (_, _) => SetSyncScroll(!syncScroll)) { Checked = syncScroll });
            menu.Items.Add(new ToolStripMenuItem(L.T("Убрать разделение"), null, (_, _) => Unsplit()));
        }
        if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(tab.Muted ? L.T("Включить звук вкладки") : L.T("Выключить звук вкладки"), null,
            (_, _) => ToggleMute(tab)));
        menu.Items.Add(new ToolStripMenuItem(L.T("Закрыть вкладку"), null, (_, _) => CloseTab(tab)) { ShortcutKeyDisplayString = tab == active ? "Ctrl+W" : "" });
        menu.Show(strip, at);
    }

    /// <summary>Puts a tab beside the one in front, on the right; in a split it takes the place of the tab beside.</summary>
    async void SplitWith(Tab tab)
    {
        if (active == null || tab == active || tab.Closed) return;
        if (Partner is { } old) SendToBackground(old);
        paneLeft = active;
        paneRight = tab;
        divider.Visible = stripeLeft.Visible = stripeRight.Visible = true;
        SetTabs();
        LayoutPanes();
        await ShowPaneAsync(tab);
    }

    /// <summary>Back to one tab on screen: the one beside goes to the background.</summary>
    void Unsplit()
    {
        var partner = Partner;
        EndSplit();
        if (partner != null) SendToBackground(partner);
        SetTabs();
        LayoutPanes();
    }

    void EndSplit()
    {
        SetSyncScroll(false);
        paneLeft = paneRight = null;
        divider.Visible = stripeLeft.Visible = stripeRight.Visible = false;
        dragFrom = null;
    }

    // Put into a page of a split while the halves scroll together: it tells where the page is scrolled to,
    // as a share of the way down, and scrolls there when told. Pages that scroll a box of their own
    // (chats, editors) are followed by the box scrolled last, or else the tallest one.
    const string ScrollScript = @"(() => {
  if (window !== top || !window.chrome || !chrome.webview) return;
  if (window.__litebroScrollTo) return;
  let quiet = 0, box = null;
  const doc = () => document.scrollingElement || document.documentElement;
  const tallest = () => {
    let best = null, most = 0;
    for (const el of document.querySelectorAll('*')) {
      const room = el.scrollHeight - el.clientHeight;
      if (room > most && el.clientHeight > innerHeight / 3 && /(auto|scroll)/.test(getComputedStyle(el).overflowY)) { best = el; most = room; }
    }
    return best;
  };
  addEventListener('scroll', e => {
    if (!window.__litebroSync || Date.now() < quiet) return;
    const el = e.target === document ? doc() : e.target;
    if (!(el instanceof Element) || el.clientHeight < innerHeight / 3) return;
    if (el !== doc()) box = el;
    const room = el.scrollHeight - el.clientHeight;
    chrome.webview.postMessage('litebro-scroll:' + (room > 0 ? el.scrollTop / room : 0));
  }, { capture: true, passive: true });
  window.__litebroScrollTo = share => {
    quiet = Date.now() + 300;
    const main = doc();
    const el = box && box.isConnected ? box : main.scrollHeight > main.clientHeight + 1 ? main : tallest() || main;
    el.scrollTop = share * (el.scrollHeight - el.clientHeight);
  };
})();";

    /// <summary>Turns on or off the halves of a split scrolling together.</summary>
    void SetSyncScroll(bool on)
    {
        if (on == syncScroll) return;
        syncScroll = on;
        foreach (var tab in new[] { paneLeft, paneRight }.OfType<Tab>()) WatchScroll(tab, on);
    }

    static async void WatchScroll(Tab tab, bool on)
    {
        if (tab.Core is not { } core) return;
        try
        {
            if (on) await core.ExecuteScriptAsync(ScrollScript);
            await core.ExecuteScriptAsync(on ? "window.__litebroSync = true" : "window.__litebroSync = false");
        }
        catch (Exception) { } // the page went away meanwhile
    }

    /// <summary>A half of a split scrolled: the other one goes to the same share of the way down.</summary>
    void OnScrolled(Tab tab, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string text;
        try { text = e.TryGetWebMessageAsString(); }
        catch (ArgumentException) { return; }
        if (!text.StartsWith("litebro-scroll:") || !double.TryParse(text.Substring(15),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var share))
            return;
        var other = tab == paneLeft ? paneRight : paneLeft;
        share = Math.Max(0, Math.Min(1, share));
        try { _ = other?.Core?.ExecuteScriptAsync("window.__litebroScrollTo && __litebroScrollTo(" +
            share.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture) + ")"); }
        catch (Exception) { }
    }

    int DividerWidth => Math.Max(4, host.DeviceDpi / 16);

    /// <summary>Where a tab's page goes: the whole of the host, or its side of a split.</summary>
    Rectangle BoundsOf(Tab tab)
    {
        var r = host.ClientRectangle;
        if (!IsPane(tab) || tab == fullScreen) return r;
        int d = DividerWidth, x = (int)(r.Width * splitAt) - d / 2, s = StripeHeight;
        return tab == paneLeft ? new Rectangle(r.X, r.Y + s, x, r.Height - s) : new Rectangle(x + d, r.Y + s, r.Width - x - d, r.Height - s);
    }

    int StripeHeight => Math.Max(3, host.DeviceDpi / 32);

    void LayoutPanes()
    {
        if (Split)
        {
            var left = BoundsOf(paneLeft!);
            var right = BoundsOf(paneRight!);
            divider.Bounds = new Rectangle(left.Right, 0, DividerWidth, host.ClientSize.Height);
            stripeLeft.Bounds = new Rectangle(left.X, 0, left.Width, StripeHeight);
            stripeRight.Bounds = new Rectangle(right.X, 0, right.Width, StripeHeight);
        }
        foreach (var tab in OnScreen)
            if (tab.Ctl is { } c) c.Bounds = BoundsOf(tab);
        // An emulated screen is fitted to the room anew once the resizing stops
        if (OnScreen.Any(t => t.Device != null))
        {
            fitTimer.Stop();
            fitTimer.Start();
        }
    }

    readonly Timer fitTimer = new() { Interval = 300 };

    /// <summary>The page's room in CSS pixels: what an emulated screen is shrunk to fit.</summary>
    Size RoomOf(Tab tab)
    {
        var b = BoundsOf(tab);
        double scale = DeviceDpi / 96.0 * (tab.Ctl?.ZoomFactor ?? 1);
        return new Size((int)(b.Width / scale), (int)(b.Height / scale));
    }

    /// <summary>A JSON document (an API's answer opened in a tab) is shown as a tree by jsonview.js.</summary>
    static async void ShowJson(CoreWebView2 core)
    {
        try
        {
            if (!(await core.ExecuteScriptAsync("document.contentType")).Contains("json")) return;
            await core.ExecuteScriptAsync(ReadResource("jsonview.js"));
        }
        catch (Exception) { } // the page went on meanwhile
    }

    static string ReadResource(string name) => L.Text(name);

    /// <summary>A PNG of the tab into a file the user picks: the whole page (up to 16384 pixels down) or what is on screen.</summary>
    // How tall the page would be if nothing scrolled: the document, or the viewport grown by what its main scrolling
    // area hides (a chat's 100vh layout grows with the viewport, so its messages then fit)
    const string MeasureScript = @"(() => {
  let extra = 0;
  for (const el of document.querySelectorAll('*')) {
    if (el.clientHeight < innerHeight * 0.3 || el.scrollHeight <= el.clientHeight + 1) continue;
    const y = getComputedStyle(el).overflowY;
    if (y === 'auto' || y === 'scroll' || y === 'overlay') extra = Math.max(extra, el.scrollHeight - el.clientHeight);
  }
  const doc = Math.max(document.documentElement.scrollHeight, document.body ? document.body.scrollHeight : 0);
  return JSON.stringify({ w: document.documentElement.clientWidth || innerWidth, h: innerHeight, doc, extra, x: scrollX, y: scrollY });
})()";

    /// <summary>
    /// The whole page: as DevTools' full size screenshot, the viewport is made as tall as the page for one shot
    /// (captureBeyondViewport alone gives the visible part only in WebView2), then set back.
    /// </summary>
    async Task<byte[]> FullShotAsync(Tab tab, CoreWebView2 core)
    {
        var measured = ProjectStore.Json.Deserialize<Dictionary<string, object>>(
            ProjectStore.Json.Deserialize<string>(await core.ExecuteScriptAsync(MeasureScript)) ?? "{}");
        double Get(string key) => measured.TryGetValue(key, out var v) && v != null ? Convert.ToDouble(v) : 0;
        int width = (int)Math.Max(1, Get("w"));
        int height = (int)Math.Min(16384, Math.Max(Get("h"), Math.Max(Get("doc"), Get("h") + Get("extra"))));
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride",
                $"{{\"width\":{width},\"height\":{height},\"deviceScaleFactor\":0,\"mobile\":{(tab.Device?.Mobile == true ? "true" : "false")}}}");
            // Layout, and pictures that load as they come into view
            await Task.Delay(400);
            await core.ExecuteScriptAsync("scrollTo(0, 0)");
            await Task.Delay(100);
            var shot = ProjectStore.Json.Deserialize<Dictionary<string, object>>(await core.CallDevToolsProtocolMethodAsync(
                "Page.captureScreenshot", "{\"format\":\"png\"}"));
            return Convert.FromBase64String((string)shot["data"]);
        }
        finally
        {
            // Back to the tab's own screen: its emulated device, or none
            if (tab.Device != null) await Emulation.ApplyAsync(core, tab.Device, tab.Speed, RoomOf(tab));
            else
                try { await core.CallDevToolsProtocolMethodAsync("Emulation.clearDeviceMetricsOverride", "{}"); }
                catch (Exception) { }
            try { await core.ExecuteScriptAsync(FormattableString.Invariant($"scrollTo({Get("x")}, {Get("y")})")); }
            catch (Exception) { }
        }
    }

    async void Snapshot(Tab tab, bool full)
    {
        if (tab.Core is not { } core) return;
        byte[] png;
        try
        {
            if (full) png = await FullShotAsync(tab, core);
            else
            {
                using var stream = new MemoryStream();
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                png = stream.ToArray();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.T("Не удалось снять страницу.\n\n") + ex.Message, "LiteBro", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        var host = Uri.TryCreate(tab.Site, UriKind.Absolute, out var u) && u.Host.Length > 0 ? u.Host : "page";
        using var dialog = new SaveFileDialog
        {
            Title = full ? L.T("Снимок страницы целиком") : L.T("Снимок видимой части"),
            FileName = host + "-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss") + ".png",
            Filter = "PNG (*.png)|*.png",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllBytes(dialog.FileName, png); }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            MessageBox.Show(this, L.T("Не удалось записать файл.\n\n") + ex.Message, "LiteBro", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>A format in the "Save as" dialog: one the engine writes (Kind), or one written here (Own).</summary>
    sealed class SaveFormat
    {
        public string Filter = "", Ext = "";
        public CoreWebView2SaveAsKind? Kind;
        public string? Own; // pdf, png, txt, csv, json

        public SaveFormat(string filter, string ext, CoreWebView2SaveAsKind kind) { Filter = filter; Ext = ext; Kind = kind; }
        public SaveFormat(string filter, string ext, string own) { Filter = filter; Ext = ext; Own = own; }
    }

    static string lastSaveFormat = "";

    static List<SaveFormat> SaveFormats(string mime, string ext)
    {
        var list = new List<SaveFormat>();
        bool html = mime.Length == 0 || mime.Contains("html");
        if (html)
        {
            list.Add(new SaveFormat(L.T("Веб-страница полностью (*.html)"), ".html", CoreWebView2SaveAsKind.Complete));
            list.Add(new SaveFormat(L.T("Веб-страница, только HTML (*.html)"), ".html", CoreWebView2SaveAsKind.HtmlOnly));
            list.Add(new SaveFormat(L.T("Веб-архив, один файл (*.mhtml)"), ".mhtml", CoreWebView2SaveAsKind.SingleFile));
        }
        else if (mime.Contains("json"))
            list.Add(new SaveFormat("JSON (*.json)", ".json", CoreWebView2SaveAsKind.Default));
        else
        {
            if (ext.Length == 0) ext = ".*";
            list.Add(new SaveFormat(L.T("Файл как есть (*") + ext + ")", ext == ".*" ? "" : ext, CoreWebView2SaveAsKind.Default));
        }
        list.Add(new SaveFormat("PDF (*.pdf)", ".pdf", "pdf"));
        list.Add(new SaveFormat(L.T("Картинка всей страницы PNG (*.png)"), ".png", "png"));
        list.Add(new SaveFormat(L.T("Текст (*.txt)"), ".txt", "txt"));
        if (html)
        {
            list.Add(new SaveFormat(L.T("Таблицы страницы CSV для Excel (*.csv)"), ".csv", "csv"));
            list.Add(new SaveFormat(L.T("Таблицы страницы JSON (*.json)"), ".json", "json"));
        }
        else if (mime.Contains("json"))
            list.Add(new SaveFormat(L.T("Список из JSON в CSV для Excel (*.csv)"), ".csv", "csv"));
        return list;
    }

    /// <summary>
    /// "Save as" (Ctrl+S, the page's menu): this program's dialog with more formats than the engine's.
    /// HTML and MHTML still go to the engine, with the path picked here; the rest is written here.
    /// </summary>
    void OnSaveAs(Tab tab, CoreWebView2 core, CoreWebView2SaveAsUIShowingEventArgs e)
    {
        var deferral = e.GetDeferral();
        var mime = (e.ContentMimeType ?? "").ToLowerInvariant();
        var suggested = e.SaveAsFilePath ?? "";
        BeginInvoke(new Action(async () =>
        {
            SaveFormat? format = null;
            string path = "";
            try
            {
                string name = "", dir = "", ext = "";
                try
                {
                    name = Path.GetFileNameWithoutExtension(suggested);
                    dir = Path.GetDirectoryName(suggested) ?? "";
                    ext = Path.GetExtension(suggested);
                }
                catch (ArgumentException) { }
                var formats = SaveFormats(mime, ext);
                int last = formats.FindIndex(f => f.Filter == lastSaveFormat);
                using var dialog = new SaveFileDialog
                {
                    Title = L.T("Сохранить как"),
                    FileName = name.Length > 0 ? name : "page",
                    Filter = string.Join("|", formats.Select(f => f.Filter + "|*" + (f.Ext.Length > 0 ? f.Ext : ".*"))),
                    FilterIndex = last >= 0 ? last + 1 : 1,
                    AddExtension = true,
                };
                if (dir.Length > 0 && Directory.Exists(dir)) dialog.InitialDirectory = dir;
                if (dialog.ShowDialog(this) == DialogResult.OK && dialog.FilterIndex >= 1 && dialog.FilterIndex <= formats.Count)
                {
                    format = formats[dialog.FilterIndex - 1];
                    path = dialog.FileName;
                    lastSaveFormat = format.Filter;
                }
            }
            finally
            {
                if (format?.Kind is { } kind)
                {
                    e.SuppressDefaultDialog = true;
                    e.SaveAsFilePath = path;
                    e.Kind = kind;
                    e.AllowReplace = true; // the dialog here asked already
                }
                else e.Cancel = true;
                deferral.Complete();
            }
            if (format?.Own is { } own) await SaveOwnAsync(tab, core, own, path);
        }));
    }

    async Task SaveOwnAsync(Tab tab, CoreWebView2 core, string format, string path)
    {
        try
        {
            switch (format)
            {
                case "pdf":
                    if (!await core.PrintToPdfAsync(path, null)) throw new IOException(L.T("Движок не смог напечатать страницу в PDF."));
                    return;
                case "png":
                    File.WriteAllBytes(path, await FullShotAsync(tab, core));
                    return;
            }
            var result = await core.ExecuteScriptAsync("(" + ReadResource("savepage.js") + ")(\"" + format + "\")");
            var text = ProjectStore.Json.Deserialize<string?>(result);
            if (text == null)
            {
                MessageBox.Show(this, format == "txt" ? L.T("На странице нет текста.") : L.T("На странице нет таблиц, сохранять нечего."),
                    "LiteBro", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            // CSV with a BOM, so Excel reads the Cyrillic right
            File.WriteAllText(path, text, new UTF8Encoding(format == "csv"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.T("Не удалось сохранить.\n\n") + ex.Message, "LiteBro", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// A site's right-click menu: this program's tools in the engine's "More tools" submenu (beside Share),
    /// whatever is switched off on the developer page (the user's choice: off there hides buttons, not these).
    /// </summary>
    void AddPageTools(Tab tab, CoreWebView2 core, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        if (e.ContextMenuTarget.Kind != CoreWebView2ContextMenuTargetKind.Page || tab.ShowingInternalPage
            || IsInternal(core.Source) || tab.Term != null || App.Current.Env is not { } env)
            return;
        CoreWebView2ContextMenuItem Item(string label, Action act, CoreWebView2ContextMenuItemKind kind = CoreWebView2ContextMenuItemKind.Command, bool on = false)
        {
            var item = env.CreateContextMenuItem(label, null, kind);
            if (kind == CoreWebView2ContextMenuItemKind.Radio) item.IsChecked = on;
            item.CustomItemSelected += (_, _) => BeginInvoke(act);
            return item;
        }
        CoreWebView2ContextMenuItem Sub(string label, IEnumerable<CoreWebView2ContextMenuItem> children)
        {
            var sub = env.CreateContextMenuItem(label, null, CoreWebView2ContextMenuItemKind.Submenu);
            foreach (var c in children) sub.Children.Add(c);
            return sub;
        }

        var tools = new List<CoreWebView2ContextMenuItem>
        {
            Item(L.T("Захватить всю страницу…") + (Dev.On("snapshot") ? " (Ctrl+Shift+S)" : ""), () => Snapshot(tab, full: true)),
            Item(L.T("Захватить видимую часть…"), () => Snapshot(tab, full: false)),
        };
        if (SiteOrigin(tab) != null)
            tools.Add(Item(L.T("Сбросить кэш сайта и обновить") + (Dev.On("reset") ? " (Ctrl+Shift+R)" : ""), () => ResetSite(tab)));
        if (StoragePage.HasSite(core))
            tools.Add(Item(L.T("Хранилище сайта"), () => OpenStorage(tab)));
        var radio = CoreWebView2ContextMenuItemKind.Radio;
        var screens = new List<CoreWebView2ContextMenuItem> { Item(L.T("Обычный экран"), () => SetEmulation(tab, null, tab.Speed), radio, tab.Device == null) };
        screens.AddRange(Emulation.Devices.Select(d => Item($"{d.Name} ({d.Width}×{d.Height})", () => SetEmulation(tab, d, tab.Speed), radio, tab.Device == d)));
        var speeds = new List<CoreWebView2ContextMenuItem> { Item(L.T("Обычная"), () => SetEmulation(tab, tab.Device, null), radio, tab.Speed == null) };
        speeds.AddRange(Emulation.Speeds.Select(sp => Item(sp.Name, () => SetEmulation(tab, tab.Device, sp), radio, tab.Speed == sp)));
        tools.Add(Sub(L.T("Устройство") + (tab.Device != null ? ": " + tab.Device.Name : ""), screens));
        tools.Add(Sub(L.T("Сеть") + (tab.Speed != null ? ": " + tab.Speed.Name : ""), speeds));
        if (tab.Device != null || tab.Speed != null)
            tools.Add(Item(L.T("Выключить эмуляцию"), () => SetEmulation(tab, null, null)));

        var into = MoreTools(e.MenuItems);
        if (into == null)
        {
            // No "More tools" in this runtime: a submenu of its own, above "Inspect"
            var own = Sub(L.T("Другие инструменты"), tools);
            int at = e.MenuItems.Select(i => i.Name).ToList().IndexOf("inspectElement");
            e.MenuItems.Insert(at >= 0 ? at : e.MenuItems.Count, own);
            return;
        }
        if (into.Count > 0) into.Add(env.CreateContextMenuItem("", null, CoreWebView2ContextMenuItemKind.Separator));
        foreach (var t in tools) into.Add(t);
    }

    /// <summary>The engine's submenu that holds "Share".</summary>
    static IList<CoreWebView2ContextMenuItem>? MoreTools(IList<CoreWebView2ContextMenuItem> items)
    {
        foreach (var i in items)
        {
            if (i.Kind != CoreWebView2ContextMenuItemKind.Submenu) continue;
            if (i.Children.Any(c => c.Name == "share")) return i.Children;
            if (MoreTools(i.Children) is { } inner) return inner;
        }
        return null;
    }

    /// <summary>The storage page of a site's tab, in a new tab in front.</summary>
    async void OpenStorage(Tab site)
    {
        var tab = await CreateTabAsync(null);
        if (tab?.Core is not { } core) return;
        tab.StorageOf = site;
        Add(tab, front: true);
        core.Navigate(StoragePage.Url);
    }

    /// <summary>A storage page's request: done in the site's page (or its cookie manager), then the storage read anew.</summary>
    async void StorageOp(Tab page, Tab site, Dictionary<string, object> m)
    {
        var reply = new Dictionary<string, object> { ["type"] = "storage" };
        string? Text(string key) => m.TryGetValue(key, out var v) ? v as string : null;
        try
        {
            if (site.Closed) throw new InvalidOperationException(L.T("Вкладка сайта закрыта."));
            if (site.Core is not { } core) throw new InvalidOperationException(L.T("Вкладка сайта выгружена: откройте её, потом нажмите «Обновить»."));
            if (!StoragePage.HasSite(core)) throw new InvalidOperationException(L.T("Во вкладке сейчас не сайт."));
            try { core.Resume(); } catch (Exception) { } // a paused page answers nothing
            site.Suspended = false;
            reply["title"] = site.Title;
            switch (Text("op"))
            {
                case "load":
                    break;
                case "cookieSet":
                    StoragePage.SetCookie(core, m);
                    break;
                case "cookieDelete":
                    StoragePage.DeleteCookie(core, Text("name") ?? "", Text("domain") ?? "", Text("path") ?? "/");
                    break;
                default:
                    await StoragePage.RunAsync(core, m);
                    break;
            }
            reply["cookies"] = await StoragePage.CookiesAsync(core);
            reply["data"] = await StoragePage.RunAsync(core, new Dictionary<string, object> { ["op"] = "load" });
        }
        catch (Exception ex) { reply["error"] = ex.Message; }
        if (page.Core is { } c && StoragePage.Is(c.Source)) c.PostWebMessageAsJson(ProjectStore.Json.Serialize(reply));
    }

    /// <summary>The tools button: on sites and files, lit while the tab emulates something.</summary>
    void ShowEmulation(Tab tab)
    {
        bool on = tab.Device != null || tab.Speed != null;
        bool any = Dev.On("emulation") || Dev.On("snapshot") || Dev.On("storage");
        emulate.Visible = on || (any && !tab.ShowingInternalPage && !IsInternal(tab.Site) && tab.Term == null);
        emulate.ForeColor = on ? Color.FromArgb(0x1f, 0x9d, 0x55) : Theme.Text;
        var what = string.Join(", ", new[] { tab.Device?.Name, tab.Speed?.Name }.OfType<string>());
        tips.SetToolTip(emulate, on ? L.T("Инструменты. Эмуляция: ") + what : L.T("Инструменты: эмуляция устройства и сети"));
    }

    /// <summary>The tools button's menu: the screen and the network the tab emulates.</summary>
    void ShowToolsMenu()
    {
        if (active is not { } tab) return;
        var menu = NewMenu();
        if (Dev.On("emulation"))
            AddEmulationItems(menu, tab);
        if (Dev.On("snapshot"))
        {
            if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem(L.T("Снимок страницы целиком…"), null, (_, _) => Snapshot(tab, full: true)) { ShortcutKeyDisplayString = "Ctrl+Shift+S" });
            menu.Items.Add(new ToolStripMenuItem(L.T("Снимок видимой части…"), null, (_, _) => Snapshot(tab, full: false)));
        }
        if (Dev.On("storage") && tab.Core is { } core && StoragePage.HasSite(core))
        {
            if (menu.Items.Count > 0 && !Dev.On("snapshot")) menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem(L.T("Хранилище сайта: cookies, localStorage, IndexedDB"), null, (_, _) => OpenStorage(tab)));
        }
        // An emulation left on is always switched off from here, the feature on or not
        if (tab.Device != null || tab.Speed != null)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem(L.T("Выключить эмуляцию"), null, (_, _) => SetEmulation(tab, null, null)));
        }
        if (menu.Items.Count == 0) return;
        menu.Show(emulate, new Point(0, emulate.Height));
    }

    void AddEmulationItems(ContextMenuStrip menu, Tab tab)
    {
        var screen = new ToolStripMenuItem(L.T("Устройство") + (tab.Device != null ? ": " + tab.Device.Name : ""));
        screen.DropDownItems.Add(new ToolStripMenuItem(L.T("Обычный экран"), null, (_, _) => SetEmulation(tab, null, tab.Speed)) { Checked = tab.Device == null });
        foreach (var d in Emulation.Devices)
            screen.DropDownItems.Add(new ToolStripMenuItem($"{d.Name} ({d.Width}×{d.Height})", null, (_, _) => SetEmulation(tab, d, tab.Speed)) { Checked = tab.Device == d });
        var net = new ToolStripMenuItem(L.T("Сеть") + (tab.Speed != null ? ": " + tab.Speed.Name : ""));
        net.DropDownItems.Add(new ToolStripMenuItem(L.T("Обычная"), null, (_, _) => SetEmulation(tab, tab.Device, null)) { Checked = tab.Speed == null });
        foreach (var sp in Emulation.Speeds)
            net.DropDownItems.Add(new ToolStripMenuItem(sp.Name, null, (_, _) => SetEmulation(tab, tab.Device, sp)) { Checked = tab.Speed == sp });
        foreach (var sub in new[] { screen, net })
            if (Theme.Dark)
            {
                sub.DropDown.Renderer = menu.Renderer;
                sub.DropDown.ForeColor = Theme.Text;
            }
        menu.Items.Add(screen);
        menu.Items.Add(net);
    }

    /// <summary>A device or network for the tab; the page reloads when the device changes, so it lays out as on it.</summary>
    async void SetEmulation(Tab tab, Emulation.Device? device, Emulation.Speed? speed)
    {
        bool reload = device != tab.Device;
        tab.Device = device;
        tab.Speed = speed;
        ShowState(tab);
        if (tab.Core is not { } core) return;
        await Emulation.ApplyAsync(core, device, speed, RoomOf(tab));
        // Pages read the user agent and touch support once, as they load
        if (reload && tab.Core == core && !tab.ShowingInternalPage)
            try { core.Reload(); } catch (Exception) { }
    }


    /// <summary>The tab beside becomes the tab in front, where it is.</summary>
    void FocusPane(Tab tab)
    {
        if (tab == active || !IsPane(tab)) return;
        active = tab;
        SetTabs();
        ShowState(tab, switched: true);
    }

    /// <summary>The tab's speaker was clicked: its sound off, or on again.</summary>
    void ToggleMute(Tab tab)
    {
        tab.Muted = !tab.Muted;
        try { if (tab.Core is { } core) core.IsMuted = tab.Muted; }
        catch (Exception) { } // the WebView is closing
        strip.Invalidate();
    }

    /// <summary>
    /// A page went into full screen (a video's button, a game) or out of it: the window covers its monitor
    /// without a frame, the tab strip, the toolbar and the other half of a split, and comes back as it was.
    /// </summary>
    void SetFullScreen(Tab tab, bool on)
    {
        if (on == (fullScreen == tab)) return;
        if (!on)
        {
            RestoreWindow();
            return;
        }
        // Only a page on screen: one in the background asked while the user went elsewhere
        if (!OnScreen.Contains(tab) || minimized)
        {
            LeavePageFullScreen(tab);
            return;
        }
        if (fullScreen != null) RestoreWindow();
        if (tab != active) FocusPane(tab);
        fullScreen = tab;
        beforeFullState = WindowState;
        beforeFullBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        var screen = Screen.FromControl(this).Bounds;
        SuspendLayout();
        // Normal before the frame goes: WinForms makes a maximized window anew when its border changes, WebViews and all
        if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
        FormBorderStyle = FormBorderStyle.None;
        Bounds = screen;
        bar.Visible = strip.Visible = false;
        divider.Visible = stripeLeft.Visible = stripeRight.Visible = false;
        if (Partner?.Ctl is { } beside) beside.IsVisible = false;
        ResumeLayout();
        LayoutPanes();
        tab.Ctl?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
    }

    /// <summary>The window as it was before the page's full screen.</summary>
    void RestoreWindow()
    {
        if (fullScreen == null) return;
        fullScreen = null;
        SuspendLayout();
        if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
        FormBorderStyle = FormBorderStyle.Sizable;
        Bounds = beforeFullBounds;
        if (beforeFullState == FormWindowState.Maximized) WindowState = FormWindowState.Maximized;
        bar.Visible = true;
        ShowStrip();
        if (Split)
        {
            divider.Visible = stripeLeft.Visible = stripeRight.Visible = true;
            if (Partner?.Ctl is { } beside) beside.IsVisible = !minimized;
        }
        ResumeLayout();
        LayoutPanes();
        if (IsHandleCreated) Theme.ApplyFrame(Handle);
    }

    /// <summary>Esc, F11, another tab: the page leaves its full screen and the window comes back.</summary>
    void ExitFullScreen()
    {
        var tab = fullScreen;
        RestoreWindow();
        if (tab != null) LeavePageFullScreen(tab);
    }

    static void LeavePageFullScreen(Tab tab)
    {
        try { _ = tab.Core?.ExecuteScriptAsync("document.fullscreenElement && document.exitFullscreen()"); }
        catch (Exception) { } // the WebView is closing
    }

    /// <summary>
    /// A server on this machine with its own certificate (vite --https, dotnet dev-certs) opens without the warning,
    /// as Chrome's --allow-insecure-localhost; any other site keeps the engine's page.
    /// </summary>
    static void OnCertificateError(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        if (Uri.TryCreate(e.RequestUri, UriKind.Absolute, out var u) && IsThisMachine(u))
            e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
    }

    /// <summary>localhost, *.localhost (the engine resolves them to loopback itself), 127.0.0.0/8 and ::1.</summary>
    static bool IsThisMachine(Uri u) =>
        u.IsLoopback || u.DnsSafeHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>Remembers the country and searches again with it.</summary>
    void PickCountry(SearchCountry.Country? picked)
    {
        App.Current.S.SaveSearchCountry(picked?.Code ?? "");
        if (active == null) return;
        ShowCountry(active);
        if (Core is { } core && SearchCountry.IsGoogleSearch(core.Source))
            core.Navigate(SearchCountry.WithCountry(core.Source, picked));
    }

    void SetLoading(Tab tab, bool value)
    {
        tab.Loading = value;
        strip.Invalidate();
        ShowState(tab);
    }

    /// <summary>Puts a tab at the end of the strip, in front or in the background.</summary>
    void Add(Tab tab, bool front)
    {
        tabs.Add(tab);
        if (front) SelectTab(tab);
        else
        {
            SendToBackground(tab);
            SetTabs();
        }
    }

    /// <summary>Brings a tab forward; one closed in the background loads its page again.</summary>
    async void SelectTab(Tab tab)
    {
        if (tab == active || tab.Closed) return;
        if (fullScreen != null) ExitFullScreen();
        if (tab == Partner)
        {
            FocusPane(tab);
            tab.Ctl?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
            return;
        }
        if (active != null)
        {
            // In a split the picked tab takes the place of the one in front
            if (paneLeft == active) paneLeft = tab;
            else if (paneRight == active) paneRight = tab;
            SendToBackground(active);
        }
        active = tab;
        SetTabs();
        ShowState(tab, switched: true);
        if (!await ShowPaneAsync(tab) || tab != active) return;
        ShowState(tab, switched: true);
        tab.Ctl!.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
    }

    /// <summary>Puts a tab on screen: one closed in the background loads its page again, a paused one resumes.</summary>
    async Task<bool> ShowPaneAsync(Tab tab)
    {
        tab.InactiveSince = null;
        if (tab.Ctl == null && (!await LoadAsync(tab, tab.Address.Length > 0 ? tab.Address : Home.Url) || !OnScreen.Contains(tab)))
            return false;
        var c = tab.Ctl!;
        if (tab.Suspended)
        {
            tab.Suspended = false;
            try { c.CoreWebView2.Resume(); }
            catch (Exception) { } // it resumes by itself once visible
        }
        c.Bounds = BoundsOf(tab);
        c.CoreWebView2.MemoryUsageTargetLevel = inBackground
            ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal;
        c.IsVisible = !minimized;
        strip.Invalidate();
        if (syncScroll && IsPane(tab)) WatchScroll(tab, true);
        SendProjects(tab); // a start page paused in the background missed the changes
        return true;
    }

    /// <summary>A tab out of sight: hidden, on the Low memory target, its renderer trimmed soon after.</summary>
    void SendToBackground(Tab tab)
    {
        tab.InactiveSince = DateTime.UtcNow;
        if (tab.Ctl is not { } c) return;
        c.IsVisible = false;
        if (!tab.Suspended) c.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
        App.Current.TrimHiddenTabsSoon();
    }

    /// <summary>Closes a tab; the last one gives way to the start page, and only the start page itself closes the window.</summary>
    void CloseTab(Tab tab)
    {
        int i = tabs.IndexOf(tab);
        if (i < 0) return;
        if (tab == fullScreen) ExitFullScreen();
        if (tabs.Count == 1)
        {
            if (Home.IsTiles(tab.Site)) Close();
            else ReplaceWithHome(tab);
            return;
        }
        // Closing either side of a split leaves the other one alone on screen
        Tab? other = null;
        if (IsPane(tab))
        {
            other = tab == paneLeft ? paneRight : paneLeft;
            EndSplit();
        }
        tab.Closed = true;
        StopTerm(tab);
        tabs.RemoveAt(i);
        App.Current.ShellMaybeUnused();
        var c = tab.Ctl;
        tab.Ctl = null;
        c?.Close();
        if (tab == active)
        {
            active = null;
            SelectTab(other ?? tabs[Math.Min(i, tabs.Count - 1)]);
        }
        else
        {
            SetTabs();
            LayoutPanes();
        }
    }

    /// <summary>A fresh start page in front (shared profile, no history), then the last tab closes behind it.</summary>
    async void ReplaceWithHome(Tab tab)
    {
        if (replacing) return;
        replacing = true;
        try
        {
            var home = await CreateTabAsync(Home.Url, "");
            if (home == null || tab.Closed) return;
            Add(home, front: true);
            CloseTab(tab);
        }
        finally { replacing = false; }
    }

    bool replacing;

    void SelectNext(int step)
    {
        if (active == null || tabs.Count < 2) return;
        SelectTab(tabs[(tabs.IndexOf(active) + step + tabs.Count) % tabs.Count]);
    }

    /// <summary>
    /// Walks the tabs in the background on: paused after SuspendAfter, closed after UnloadAfter.
    /// Never a tab that plays sound, nor one on the site of a project whose program runs:
    /// dsh keeps its agent's connection open in the page.
    /// </summary>
    void FreezeIdleTabs()
    {
        var now = DateTime.UtcNow;
        foreach (var tab in tabs)
        {
            if (tab.InactiveSince is not { } since || tab.Core is not { } core) continue;
            // Nor a terminal: what runs in it goes on printing
            if (core.IsDocumentPlayingAudio || tab.Term != null || App.Current.IsRunningSite(tab.Site)) continue;
            // Nor a site whose storage a storage page shows: it reads it from the live page
            if (tabs.Any(t => t.StorageOf == tab)) continue;
            // A page Chromium refused to pause is busy with something (a call, say): it is not closed either
            if (tab.Suspended && now - since >= UnloadAfter) Unload(tab);
            else if (!tab.Suspended && now - since >= SuspendAfter) Suspend(tab, core);
        }
    }

    async void Suspend(Tab tab, CoreWebView2 core)
    {
        bool paused;
        try { paused = await core.TrySuspendAsync(); }
        catch (Exception) { return; }
        if (!paused || tab.Core != core) return;
        if (tab != active) tab.Suspended = true;
        else
        {
            // Picked while it was being paused
            try { core.Resume(); }
            catch (Exception) { }
        }
    }

    /// <summary>Closes a tab's WebView and keeps its address: picking the tab loads the page again.</summary>
    void Unload(Tab tab)
    {
        if (tab.Ctl is not { } c) return;
        if (tab == fullScreen) ExitFullScreen();
        // A project's start or failure page comes back as the project's own address, never as a new start
        tab.Address = tab.Site;
        KeepHistory(tab, current: false, ahead: true);
        tab.Stepping = true; // loaded again when picked: what was ahead stays
        tab.Ctl = null;
        tab.Suspended = tab.Loading = tab.PlayingAudio = false;
        c.Close();
        strip.Invalidate();
    }

    /// <summary>True when a tab here is on the address, loaded or not.</summary>
    public bool HasTabOn(string url) => tabs.Any(t => t.Site.StartsWith(url));

    /// <summary>Brings forward a tab on the same site as the address and opens the address there; false if there is none.</summary>
    public bool TryFocusTabOn(string address)
    {
        var url = new Uri(address);
        var tab = active != null && IsOn(active, url) ? active : tabs.LastOrDefault(t => IsOn(t, url));
        if (tab == null) return false;
        if (tab.Ctl == null) tab.Address = address; // loaded with it
        SelectTab(tab);
        // Opened here without reloading if the tab is already on it
        if (tab.Core is { } core && core.Source != address) core.Navigate(address);
        return true;
    }

    /// <summary>Opens an address, or the start page, in a new tab in front.</summary>
    public async void OpenNewTab(string? url)
    {
        var tab = await CreateTabAsync(url ?? Home.Url);
        if (tab != null) Add(tab, front: true);
    }

    /// <summary>The network switches changed: every tab follows, and the start pages and /net show them.</summary>
    /// <summary>«Настройки по умолчанию» on the «Для разработчика» page: asked here, so the dialog can say what restarts.</summary>
    void ConfirmResetSettings()
    {
        var text = L.T("Вернуть все настройки LiteBro к исходным, как после установки?\n\n") +
            L.T("Сбросятся: тема, страна поиска, «только localhost», разрешённые сайты, журнал сети, CORS, ") +
            L.T("удаление cookies при выходе, обновление при изменении файлов, выключенное на странице «Для разработчика», ") +
            L.T("а также строки settings.ini (поиск, видеокарта, флаги движка, локальные имена, основной браузер).\n\n") +
            L.T("Останутся: плитки проектов, заглушки, cookies и входы на сайты, логи. ") +
            L.T("Прежний файл сохранится как settings.ini.bak.");
        if (App.Current.ResetRestartsEngine())
            text += L.T("\n\nДвижок браузера перезапустится: все вкладки перезагрузятся, открытые терминалы закроются. Программы проектов продолжат работать.");
        if (MessageBox.Show(this, text, L.T("Настройки по умолчанию"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.OK)
            return;
        App.Current.ResetSettings();
    }

    /// <summary>After a switch on the «Для разработчика» page: emulation switched off leaves the tabs, the toolbar follows.</summary>
    public void ApplyDev()
    {
        if (!Dev.On("emulation"))
            foreach (var tab in tabs.Where(t => t.Device != null || t.Speed != null).ToList())
                SetEmulation(tab, null, null);
        ApplyNet();
        if (active != null) ShowState(active);
    }

    public void ApplyNet()
    {
        foreach (var tab in tabs)
        {
            ApplyNet(tab);
            SendNet(tab);
        }
        WatchFolders();
    }

    // Reloading on file changes: a watcher per folder of the pages on screen, only while the switch is on
    readonly Dictionary<string, FileSystemWatcher> watchers = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> changedFolders = new(StringComparer.OrdinalIgnoreCase);
    readonly Timer reloadTimer = new() { Interval = 400 };
    static readonly string[] Noise = { "\\node_modules\\", "\\.git\\", "\\.vs\\", "\\.idea\\", "\\obj\\", "\\bin\\", "\\__pycache__\\", "\\.venv\\", "\\.cache\\", "\\.next\\" };

    /// <summary>The folder whose files make a tab's page: its project's, or a file's own.</summary>
    string? FolderOf(Tab tab)
    {
        if (tab.ShowingInternalPage || tab.Term != null || !Uri.TryCreate(tab.Site, UriKind.Absolute, out var u)) return null;
        if (u.IsFile) return Path.GetDirectoryName(u.LocalPath);
        if (IsInternal(tab.Site) || OwnerOf(tab, u) is not { } p) return null;
        var dir = App.Current.LauncherFor(p).WorkDir;
        return dir.Length > 0 ? dir : null;
    }

    void WatchFolders()
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (App.Current.S.AutoReload && !minimized)
            foreach (var tab in OnScreen)
                if (FolderOf(tab) is { } dir) wanted.Add(dir);
        foreach (var gone in watchers.Keys.Where(d => !wanted.Contains(d)).ToList())
        {
            watchers[gone].Dispose();
            watchers.Remove(gone);
        }
        foreach (var dir in wanted.Where(d => !watchers.ContainsKey(d)))
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                var w = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                FileSystemEventHandler changed = (_, e) => OnFileChanged(dir, e.FullPath);
                w.Changed += changed;
                w.Created += changed;
                w.Deleted += changed;
                w.Renamed += (_, e) => OnFileChanged(dir, e.FullPath);
                w.EnableRaisingEvents = true;
                watchers[dir] = w;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException) { }
        }
    }

    /// <summary>On a watcher's thread: a change that counts reloads the folder's pages once the saving settles.</summary>
    void OnFileChanged(string dir, string path)
    {
        var rel = path.Length > dir.Length ? path.Substring(dir.Length) : path;
        if (Noise.Any(n => (rel + "\\").IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)) return;
        var name = Path.GetFileName(path);
        if (name.EndsWith("~") || name.StartsWith(".#") || name.StartsWith("~$")
            || new[] { ".tmp", ".swp", ".swx", ".log", ".lock", ".pid", ".db-journal" }.Any(x => name.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
            return;
        try
        {
            BeginInvoke(new Action(() =>
            {
                changedFolders.Add(dir);
                reloadTimer.Stop();
                reloadTimer.Start();
            }));
        }
        catch (InvalidOperationException) { } // the window is gone
    }

    void ReloadChanged()
    {
        reloadTimer.Stop();
        foreach (var tab in OnScreen)
            if (FolderOf(tab) is { } dir && changedFolders.Contains(dir) && tab.Core is { } core)
                try { core.Reload(); } catch (Exception) { }
        changedFolders.Clear();
    }

    /// <summary>
    /// «Только localhost» on a tab: every request passes OnNetRequest (a cost per request, so only while the mode
    /// is on), and new pages get the web socket guard. Pages already open keep their sockets until reloaded.
    /// </summary>
    async void ApplyNet(Tab tab)
    {
        if (tab.Core is not { } core) return;
        const CoreWebView2WebResourceRequestSourceKinds All = CoreWebView2WebResourceRequestSourceKinds.All;
        try
        {
            if (NetGuard.Watching && tab.NetResponse == null)
            {
                tab.NetResponse = (_, e) => OnNetResponse(tab, e);
                core.WebResourceResponseReceived += tab.NetResponse;
            }
            else if (!NetGuard.Watching && tab.NetResponse != null)
            {
                core.WebResourceResponseReceived -= tab.NetResponse;
                tab.NetResponse = null;
            }
            if (NetGuard.LocalOnly && !tab.NetFilter)
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, All);
            else if (!NetGuard.LocalOnly && tab.NetFilter)
                core.RemoveWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, All);
            tab.NetFilter = NetGuard.LocalOnly;
            // Mocks see only the requests to their own addresses
            var mocks = Dev.On("mocks") ? MockStore.Filters() : new HashSet<string>();
            foreach (var gone in tab.MockFilters.Except(mocks).ToList())
            {
                core.RemoveWebResourceRequestedFilter(gone, CoreWebView2WebResourceContext.All, All);
                tab.MockFilters.Remove(gone);
            }
            foreach (var added in mocks.Except(tab.MockFilters).ToList())
            {
                core.AddWebResourceRequestedFilter(added, CoreWebView2WebResourceContext.All, All);
                tab.MockFilters.Add(added);
            }
            // The script carries the rules: put in anew on every change, and run on the page already open,
            // which takes the new rules (and closes the sockets they forbid) without a reload
            if (tab.NetScript is { } old)
            {
                tab.NetScript = null;
                core.RemoveScriptToExecuteOnDocumentCreated(old);
            }
            var script = NetGuard.PageScript();
            _ = core.ExecuteScriptAsync(script);
            if (!NetGuard.Watching) return;
            // Two quick changes overlap here: only the latest one's script stays
            int generation = ++tab.NetGeneration;
            var id = await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
            if (tab.Core == core && NetGuard.Watching && tab.NetScript == null && generation == tab.NetGeneration) tab.NetScript = id;
            else core.RemoveScriptToExecuteOnDocumentCreated(id);
        }
        catch (Exception) { } // the WebView closed meanwhile
    }

    void SendNet(Tab tab)
    {
        if (tab.Core is { } core && !tab.Suspended && (Home.Is(core.Source)))
            core.PostWebMessageAsJson(ProjectStore.Json.Serialize(new Dictionary<string, object>
            {
                ["type"] = "net",
                ["localOnly"] = NetGuard.LocalOnly,
                ["journal"] = NetGuard.Journal,
                ["allow"] = NetGuard.AllowText,
                ["cors"] = NetGuard.IgnoreCors,
                ["clearOnExit"] = App.Current.S.ClearOnExit,
                ["autoReload"] = App.Current.S.AutoReload,
                ["file"] = NetLog.FilePath,
                ["mocks"] = MockStore.Summary(),
                ["off"] = Dev.OffList(),
                ["lang"] = App.Current.S.Language,
            }));
    }

    /// <summary>The journal entries the /net page shows, into a file the user picks.</summary>
    void ExportJournal(List<long> seqs, bool csv)
    {
        using var dialog = new SaveFileDialog
        {
            Title = L.T("Выгрузить журнал сети"),
            FileName = "network-" + DateTime.Now.ToString("yyyy-MM-dd-HHmm") + (csv ? ".csv" : ".json"),
            Filter = csv ? "CSV (*.csv)|*.csv" : "JSON (*.json)|*.json",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            // CSV with a BOM, so Excel reads the Cyrillic right
            File.WriteAllText(dialog.FileName, NetLog.Export(seqs, csv), new UTF8Encoding(csv));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            MessageBox.Show(this, L.T("Не удалось записать файл.\n\n") + ex.Message, "LiteBro", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>The engine restarts with other flags: every tab lets go of its WebView and keeps its address.</summary>
    public void UnloadAll()
    {
        foreach (var tab in tabs)
        {
            StopTerm(tab);
            Unload(tab);
        }
    }

    /// <summary>After an engine restart: the tabs on screen load again.</summary>
    public async void ReloadShown()
    {
        foreach (var tab in OnScreen.ToList())
            await ShowPaneAsync(tab);
        if (active != null) ShowState(active, switched: true);
    }

    /// <summary>In «только localhost» mode a request to the internet gets a refusal instead; the journal notes it.</summary>
    void OnNetRequest(Tab tab, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var env = App.Current.ResponseEnv;
        if (env != null && TryMock(tab, e, env)) return;
        if (env == null || !NetGuard.LocalOnly || !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var url) || !NetGuard.ShouldBlock(url))
            return;
        bool page = e.ResourceContext == CoreWebView2WebResourceContext.Document;
        e.Response = env.CreateWebResourceResponse(page ? ProgramLog.Bytes(NetGuard.BlockedPage(url.AbsoluteUri)) : null,
            403, BlockedReason, page ? "Content-Type: text/html; charset=utf-8" : "");
        NetLog.Add(e.Request.Method, url, L.T("заблокировано"), blocked: true, -1, KindOf(e.ResourceContext),
            page ? "" : tab.Site);
    }

    const string BlockedReason = "Blocked by LiteBro", MockReason = "LiteBro mock";

    /// <summary>A request a mock answers gets its stub, in «только localhost» mode too; the journal notes it.</summary>
    bool TryMock(Tab tab, CoreWebView2WebResourceRequestedEventArgs e, CoreWebView2Environment env)
    {
        if (MockStore.All.Count == 0 || !Dev.On("mocks") || !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var url) || !NetGuard.IsNetwork(url)) return false;
        var method = e.Request.Method;
        string? origin = e.Request.Headers.Contains("Origin") ? e.Request.Headers.GetHeader("Origin") : null;
        // The page may read the stub from another site: allowed for it, with its cookies
        var cors = origin != null
            ? "Access-Control-Allow-Origin: " + origin + "\r\nAccess-Control-Allow-Credentials: true\r\nVary: Origin"
            : "Access-Control-Allow-Origin: *";
        var mock = MockStore.For(method, url);
        if (mock == null && method == "OPTIONS" && e.Request.Headers.Contains("Access-Control-Request-Method") && MockStore.Covers(url))
        {
            var asked = e.Request.Headers.Contains("Access-Control-Request-Headers") ? e.Request.Headers.GetHeader("Access-Control-Request-Headers") : "";
            e.Response = env.CreateWebResourceResponse(null, 204, MockReason, cors +
                "\r\nAccess-Control-Allow-Methods: GET, POST, PUT, PATCH, DELETE, OPTIONS, HEAD" +
                (asked.Length > 0 && asked.IndexOfAny(new[] { '\r', '\n' }) < 0 ? "\r\nAccess-Control-Allow-Headers: " + asked : "") +
                "\r\nAccess-Control-Max-Age: 600");
            return true;
        }
        if (mock == null) return false;
        var body = Encoding.UTF8.GetBytes(mock.Body);
        bool empty = mock.Status is 204 or 304 || method == "HEAD";
        e.Response = env.CreateWebResourceResponse(empty ? null : new MemoryStream(body), mock.Status, MockReason,
            "Content-Type: " + mock.Type + "\r\nCache-Control: no-store\r\nX-LiteBro-Mock: 1\r\n" + cors);
        if (NetGuard.Watching)
            NetLog.Add(method, url, L.T("заглушка ") + mock.Status, blocked: false, empty ? 0 : body.Length, KindOf(e.ResourceContext),
                e.ResourceContext == CoreWebView2WebResourceContext.Document ? "" : tab.Site);
        return true;
    }

    /// <summary>A journal entry turned into a new mock: the /net page opens it in its editor.</summary>
    async void MockFromEntry(Tab tab, long seq)
    {
        if (NetLog.Get(seq) is not { } entry) return;
        var mock = await MockStore.FromEntryAsync(entry);
        SendMock(tab, mock);
    }

    void SendMock(Tab tab, Mock mock)
    {
        if (tab.Core is { } core && NetPage.Is(core.Source))
            core.PostWebMessageAsJson(ProjectStore.Json.Serialize(new Dictionary<string, object>
            {
                ["type"] = "mockEdit",
                ["mock"] = MockStore.Full(mock),
            }));
    }

    static string KindOf(CoreWebView2WebResourceContext c) => c switch
    {
        CoreWebView2WebResourceContext.Document => L.T("документ"),
        CoreWebView2WebResourceContext.Stylesheet => L.T("стиль"),
        CoreWebView2WebResourceContext.Image => L.T("картинка"),
        CoreWebView2WebResourceContext.Media => L.T("медиа"),
        CoreWebView2WebResourceContext.Font => L.T("шрифт"),
        CoreWebView2WebResourceContext.Script => L.T("скрипт"),
        CoreWebView2WebResourceContext.XmlHttpRequest => "XHR",
        CoreWebView2WebResourceContext.Fetch => "fetch",
        CoreWebView2WebResourceContext.EventSource => "EventSource",
        CoreWebView2WebResourceContext.Websocket => "WebSocket",
        CoreWebView2WebResourceContext.Manifest => L.T("манифест"),
        CoreWebView2WebResourceContext.Ping => "ping/beacon",
        CoreWebView2WebResourceContext.CspViolationReport => L.T("отчёт CSP"),
        _ => L.T("другое"),
    };

    /// <summary>What a request was for, from the Sec-Fetch-Dest header the engine adds (https only), else its Accept.</summary>
    static string KindOf(CoreWebView2HttpRequestHeaders headers)
    {
        string? Get(string name) => headers.Contains(name) ? headers.GetHeader(name) : null;
        var dest = Get("Sec-Fetch-Dest");
        if (dest != null)
            return dest switch
            {
                "document" or "iframe" or "frame" => L.T("документ"),
                "script" or "worker" or "sharedworker" or "serviceworker" => L.T("скрипт"),
                "style" => L.T("стиль"),
                "image" => L.T("картинка"),
                "font" => L.T("шрифт"),
                "audio" or "video" or "track" => L.T("медиа"),
                "empty" => "fetch/XHR",
                "manifest" => L.T("манифест"),
                _ => dest,
            };
        var accept = Get("Accept") ?? "";
        return accept.StartsWith("text/html") ? L.T("документ") : accept.StartsWith("text/css") ? L.T("стиль")
            : accept.StartsWith("image/") ? L.T("картинка") : "";
    }

    /// <summary>A response from the internet: into the journal while it is on (always in «только localhost» mode).</summary>
    void OnNetResponse(Tab tab, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        if (!NetGuard.Watching || !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var url) || !NetGuard.IsOutside(url)) return;
        var r = e.Response;
        if (r.ReasonPhrase is BlockedReason or MockReason) return; // journaled when refused or stubbed
        long size = -1;
        try
        {
            if (r.Headers.Contains("Content-Length") && long.TryParse(r.Headers.GetHeader("Content-Length"), out var n)) size = n;
        }
        catch (Exception) { }
        string kind;
        try { kind = KindOf(e.Request.Headers); }
        catch (Exception) { kind = ""; }
        NetLog.Add(e.Request.Method, url, r.StatusCode + (NetGuard.LocalOnly ? L.T(" (разрешён)") : ""), blocked: false, size, kind, tab.Site);
    }

    /// <summary>
    /// The page script (netpage.js) as a string: the stack of a request to outside, a web socket it refused or opened.
    /// Any page can send such a string, so it only ever adds to the journal.
    /// </summary>
    void OnPageNet(Tab tab, CoreWebView2WebMessageReceivedEventArgs e)
    {
        const string Prefix = "litebro-net:";
        string text;
        try { text = e.TryGetWebMessageAsString(); }
        catch (ArgumentException) { return; }
        if (!text.StartsWith(Prefix) || !NetGuard.Watching || text.Length > 20000) return;
        try
        {
            var m = ProjectStore.Json.Deserialize<Dictionary<string, object>>(text.Substring(Prefix.Length));
            string Get(string key) => m.TryGetValue(key, out var v) && v is string s ? s : "";
            if (!Uri.TryCreate(Get("url"), UriKind.Absolute, out var url) || !NetGuard.IsOutside(url)) return;
            var stack = Get("stack");
            var method = Get("method") is { Length: > 0 and < 16 } verb && verb.All(char.IsLetter) ? verb.ToUpperInvariant() : "GET";
            switch (Get("event"))
            {
                case "stack":
                    NetLog.NoteStack(url.AbsoluteUri, stack);
                    break;
                case "ws-blocked" when NetGuard.LocalOnly:
                    NetLog.Add(method, url, L.T("заблокировано"), blocked: true, -1, "WebSocket", tab.Site, stack);
                    break;
                case "ws":
                    NetLog.Add(method, url, L.T("соединение") + (NetGuard.LocalOnly ? L.T(" (разрешён)") : ""), blocked: false, -1, "WebSocket", tab.Site, stack);
                    break;
            }
        }
        catch (Exception) { }
    }

    /// <summary>A terminal tab in front with PowerShell in the folder.</summary>
    async void OpenTerminal(string dir)
    {
        var tab = await CreateTabAsync(null);
        if (tab?.Core is not { } core) return;
        tab.TermDir = dir;
        Add(tab, front: true);
        core.Navigate(TermPage.Url);
    }

    /// <summary>The start page becomes the terminal, as a tile opens in its tab.</summary>
    void OpenTerminalHere(Tab tab, string dir, string? command = null)
    {
        if (tab.Core is not { } core) return;
        tab.TermDir = dir;
        tab.TermCommand = command;
        core.Navigate(TermPage.Url);
    }

    void OpenHereOrNew(Tab tab, string url, bool newTab)
    {
        if (newTab || tab.Core is not { } core) OpenNewTab(url);
        else core.Navigate(url);
    }

    // What a shell prints is gathered and posted to its page at most once per turn of the window's thread
    const int MaxTermPost = 1 << 20;

    /// <summary>Starts (or starts anew) the shell of a terminal tab, sized as its page.</summary>
    void StartTerm(Tab tab, int columns, int rows)
    {
        // A page on /term that this browser did not open as a terminal starts nothing
        if (tab.TermDir == null || tab.Core is not { } core) return;
        StopTerm(tab);
        var dir = Directory.Exists(tab.TermDir) ? tab.TermDir : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var term = Terminal.Start(TermPage.CommandLine, dir, columns > 0 ? columns : 120, rows > 0 ? rows : 30, out var error);
        if (term == null)
        {
            core.PostWebMessageAsJson(ProjectStore.Json.Serialize(new Dictionary<string, object> { ["type"] = "termError", ["text"] = error ?? "" }));
            return;
        }
        tab.Term = term;
        tab.TermCwd = dir;
        tab.TermRunning = tab.TermRunningDir = null;
        var pending = new StringBuilder();
        var carry = "";
        term.Output += text =>
        {
            bool first;
            lock (pending)
            {
                first = pending.Length == 0;
                pending.Append(text);
            }
            if (first) Post(Flush);
        };
        term.Exited += () => Post(() =>
        {
            if (tab.Term != term) return;
            Flush();
            StopTerm(tab);
            Send(new Dictionary<string, object> { ["type"] = "termExit" });
        });
        term.Begin();
        // A terminal tile's command: typed ahead, the shell reads it once its prompt is up
        if (tab.TermCommand is { Length: > 0 } command) term.Write(command + "\r");

        void Flush()
        {
            string text;
            lock (pending)
            {
                text = pending.ToString(0, Math.Min(pending.Length, MaxTermPost));
                pending.Remove(0, text.Length);
                if (pending.Length > 0) Post(Flush);
            }
            if (tab.Term != term || text.Length == 0) return;
            var running = tab.TermRunning;
            TermPage.Watch(tab, ref carry, text);
            if (running != tab.TermRunning && tab == active) ShowStar(tab);
            Send(new Dictionary<string, object> { ["type"] = "termOut", ["data"] = text });
        }
        void Send(Dictionary<string, object> message)
        {
            if (tab.Core is { } c && TermPage.Is(c.Source)) c.PostWebMessageAsJson(ProjectStore.Json.Serialize(message));
        }
        void Post(Action action)
        {
            try { BeginInvoke(action); }
            catch (InvalidOperationException) { } // the window is gone
        }
    }

    static void StopTerm(Tab tab)
    {
        var term = tab.Term;
        tab.Term = null;
        term?.Dispose();
    }

    /// <param name="link">One of the project's links; null for its own address.</param>
    async Task OpenProjectInNewTabAsync(Project p, bool front, string? link = null)
    {
        var tab = await CreateTabAsync(null, p.Profile);
        if (tab == null) return;
        tab.Title = link == null ? p.Name : p.Links.FirstOrDefault(l => l.Url == link)?.Name ?? p.Name;
        Add(tab, front);
        OpenProject(tab, p, link);
    }

    /// <summary>
    /// «Открыть всё»: the project in this tab (or in a new one), each of its links in a tab of its own behind it.
    /// The tabs wait for the program together, each for its own address.
    /// </summary>
    async void OpenAll(Tab tab, Project p, bool newTab)
    {
        if (newTab) await OpenProjectInNewTabAsync(p, front: false);
        else OpenProject(tab, p);
        foreach (var link in p.Links.ToList()) await OpenProjectInNewTabAsync(p, front: false, link.Url);
    }

    /// <summary>
    /// Back (-1) or forward (1) in the tab in front. This program's own pages (a project's start or failure page)
    /// and the address they stood for are passed over, else back from a project would land on its start page again.
    /// Past the engine's history come the pages of the WebViews the tab had before; a tab with nothing behind it
    /// goes back to the tiles.
    /// </summary>
    async void Step(int dir)
    {
        if (active is not { } tab || tab.Core is not { } core) return;
        var h = await HistoryOf(core);
        if (tab.Core != core) return;
        if (h == null)
        {
            try { if (dir < 0) core.GoBack(); else core.GoForward(); }
            catch (Exception) { }
            return;
        }
        Remember(tab, h.Value);
        var (urls, ids, at) = h.Value;
        if (at < tab.Floor) // went below it by the mouse's own back button: the rest no longer lines up
        {
            tab.Floor = 0;
            tab.Before.Clear();
            tab.Ahead.Clear();
        }
        var here = tab.Site;
        for (var i = at + dir; i >= tab.Floor && i < urls.Length; i += dir)
        {
            if (!IsPage(urls[i]) || urls[i] == here) continue;
            try { await core.CallDevToolsProtocolMethodAsync("Page.navigateToHistoryEntry", $"{{\"entryId\":{ids[i]}}}"); }
            catch (Exception) { }
            return;
        }
        var from = dir < 0 ? tab.Before : tab.Ahead;
        string? target = null;
        while (target == null && from.Count > 0)
        {
            target = from[from.Count - 1];
            from.RemoveAt(from.Count - 1);
            if (target == here) target = null;
        }
        if (target == null && dir < 0 && !Home.IsTiles(here)) target = Home.Url;
        if (target == null) return;
        // The engine's entries on the other side, and this page, move over: the new page cuts them off
        if (dir < 0)
        {
            for (var i = urls.Length - 1; i > at; i--) if (IsPage(urls[i])) tab.Ahead.Add(urls[i]);
            if (IsPage(here)) tab.Ahead.Add(here);
        }
        else
        {
            for (var i = tab.Floor; i < at; i++) if (IsPage(urls[i])) tab.Before.Add(urls[i]);
            if (IsPage(here)) tab.Before.Add(here);
        }
        tab.Floor = at + 1;
        tab.Stepping = true;
        if (Home.Is(target)) tab.LastProject = null;
        core.Navigate(target);
    }

    /// <summary>An address worth going back to: not about:blank nor this program's start or failure page.</summary>
    static bool IsPage(string url) => url.Length > 0 && !url.StartsWith("about:") && !url.StartsWith("data:");

    /// <summary>The engine's history of a WebView: addresses, their entry ids, and the entry it is on.</summary>
    static async Task<(string[] urls, int[] ids, int at)?> HistoryOf(CoreWebView2 core)
    {
        try
        {
            var h = ProjectStore.Json.Deserialize<NavHistory>(await core.CallDevToolsProtocolMethodAsync("Page.getNavigationHistory", "{}"));
            if (h?.entries == null || h.entries.Count == 0) return null;
            return (h.entries.Select(x => x.url ?? "").ToArray(), h.entries.Select(x => x.id).ToArray(), h.currentIndex);
        }
        catch (Exception) { return null; }
    }

    internal sealed class NavHistory
    {
        public int currentIndex { get; set; }
        public List<NavEntry>? entries { get; set; }
    }

    internal sealed class NavEntry
    {
        public int id { get; set; }
        public string? url { get; set; }
    }

    static void Remember(Tab tab, (string[] urls, int[] ids, int at) h)
    {
        tab.Trail = h.urls;
        tab.TrailAt = h.at;
    }

    /// <summary>The tab's WebView is about to be replaced: its history goes on in Before (and Ahead).</summary>
    static void KeepHistory(Tab tab, bool current, bool ahead)
    {
        // A step already moved it over
        if (!tab.Stepping)
        {
            var t = tab.Trail;
            for (var i = tab.Floor; i < tab.TrailAt && i < t.Length; i++) if (IsPage(t[i])) tab.Before.Add(t[i]);
            if (current && IsPage(tab.Site)) tab.Before.Add(tab.Site);
            if (ahead) for (var i = t.Length - 1; i > tab.TrailAt; i--) if (IsPage(t[i])) tab.Ahead.Add(t[i]);
        }
        // Consecutive repeats come from a page and its own start page, say
        for (var i = tab.Before.Count - 1; i > 0; i--) if (tab.Before[i] == tab.Before[i - 1]) tab.Before.RemoveAt(i);
        tab.Trail = Array.Empty<string>();
        tab.Floor = 0;
    }

    /// <summary>The start page with the project tiles.</summary>
    void GoHome()
    {
        if (active?.Core is not { } core) return;
        active.LastProject = null;
        core.Navigate(Home.Url);
    }

    /// <summary>Opens a project's address, or one of its links: at once when it answers, else after starting its program.</summary>
    /// <param name="link">One of the project's links; null for its own address.</param>
    async void OpenProject(Tab tab, Project p, string? link = null)
    {
        if (tab.Core == null || !Uri.TryCreate(link ?? p.Url, UriKind.Absolute, out var url)) return;
        if (p.IsTerminal && link == null)
        {
            // A terminal tile: PowerShell in its folder, its command typed in
            tab.LastProject = null;
            var dir = p.WorkDir.Trim().Length > 0 ? p.WorkDir.Trim() : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (await UseProfileAsync(tab, "")) OpenTerminalHere(tab, dir, p.Command);
            return;
        }
        tab.LastProject = p;
        if (!await UseProfileAsync(tab, p.Profile) || tab.LastProject != p) return;
        tab.LastLink = link;
        var launcher = App.Current.LauncherFor(p);
        if (p.Exe.Trim().Length > 0 && !url.IsFile && !await Launcher.IsUpAsync(url))
        {
            ShowInternalPage(tab, Pages.Starting(p, url.AbsoluteUri, launcher));
            // The address the program prints stands for the project's own, never for a link
            var error = await launcher.StartAndWaitAsync(url, printed: link == null);
            // Left while it started (back to the tiles, say): the tab stays where it went
            if (tab.LastProject != p || !tab.ShowingInternalPage) return;
            if (error != null)
            {
                ShowInternalPage(tab, Pages.Failed(p, error, launcher.LogTail()));
                return;
            }
        }
        tab.Core?.Navigate(link == null ? launcher.AddressToOpen(url) : url.AbsoluteUri);
    }

    /// <summary>A page of this program; the address bar shows the project's address meanwhile.</summary>
    void ShowInternalPage(Tab tab, string html)
    {
        if (tab.Core == null) return;
        tab.ShowingInternalPage = true;
        tab.FailedUrl = null;
        tab.Core.NavigateToString(html);
        ShowState(tab, switched: true);
    }

    /// <summary>Gives every start page open here the current tiles.</summary>
    public void SendProjects()
    {
        foreach (var tab in tabs) SendProjects(tab);
        if (active != null) ShowStar(active); // a tile saved or changed: the star follows
    }

    void SendProjects(Tab tab)
    {
        if (tab.Core is { } core && !tab.Suspended && Home.Is(core.Source))
            core.PostWebMessageAsJson(Home.State(App.Current.RunningIds));
    }

    void OnHomeRequest(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var env = App.Current.ResponseEnv;
        if (env == null || !Home.Is(e.Request.Uri)) return;
        // Consoles, the journal and the rest are read only by the browser's own pages: with CORS off
        // any site could fetch them otherwise (a console's output, tokens included)
        var path = new Uri(e.Request.Uri).AbsolutePath;
        // Only the pages themselves open anywhere (a frame of another site included, which frame-ancestors then refuses);
        // their data (a console's text, the journal, icons) goes to the browser's own pages alone
        bool document = path is "/" or NetPage.Path or Dev.Path or StoragePage.Path or "/term"
            || (ProgramLog.Parse(path, out bool isText) != null && !isText);
        if (!(sender is CoreWebView2 asker && Home.Is(asker.Source))
            && (e.ResourceContext != CoreWebView2WebResourceContext.Document || !document))
        {
            e.Response = env.CreateWebResourceResponse(null, 403, "Forbidden", "");
            return;
        }
        if (path == "/")
            e.Response = env.CreateWebResourceResponse(Home.Page(), 200, "OK", Home.Headers);
        else if (ProgramLog.Parse(path, out bool text) is { } p)
        {
            if (!text)
                e.Response = env.CreateWebResourceResponse(ProgramLog.Bytes(ProgramLog.Page(p)), 200, "OK", ProgramLog.Headers);
            else
            {
                var query = new Uri(e.Request.Uri).Query;
                var m = Regex.Match(query, @"[?&]from=(-?\d+)");
                long from = m.Success && long.TryParse(m.Groups[1].Value, out var n) ? n : -1;
                var json = ProgramLog.Chunk(p, from, App.Current.RunningIds.Contains(p.Id), App.Current.LauncherFor(p));
                e.Response = env.CreateWebResourceResponse(ProgramLog.Bytes(json), 200, "OK", ProgramLog.JsonHeaders);
            }
        }
        else if (path == NetPage.Path)
            e.Response = env.CreateWebResourceResponse(NetPage.Html(), 200, "OK", ProgramLog.Headers);
        else if (path == Dev.Path)
            e.Response = env.CreateWebResourceResponse(Dev.Html(), 200, "OK", ProgramLog.Headers);
        else if (path == StoragePage.Path)
            e.Response = env.CreateWebResourceResponse(StoragePage.Html(), 200, "OK", ProgramLog.Headers);
        else if (path == NetPage.Path + "/log")
        {
            var m = Regex.Match(new Uri(e.Request.Uri).Query, @"[?&]after=(\d+)");
            long after = m.Success && long.TryParse(m.Groups[1].Value, out var n) ? n : 0;
            e.Response = env.CreateWebResourceResponse(ProgramLog.Bytes(NetLog.Since(after)), 200, "OK", ProgramLog.JsonHeaders);
        }
        else if (TermPage.File(path, out var headers) is { } file)
            e.Response = env.CreateWebResourceResponse(file, 200, "OK", headers);
        else if (path.StartsWith("/icon/") && Icons.Read(Uri.UnescapeDataString(path.Substring(6))) is { } bytes)
            e.Response = env.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK",
                "Content-Type: " + Icons.ContentType(path) + "\r\n" + Icons.Headers + "\r\n" + Home.Isolation);
        else
            e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "");
    }

    /// <summary>Which of the browser's own pages a start.litebro address is.</summary>
    static string PageOf(string source)
    {
        var path = new Uri(source).AbsolutePath;
        if (path == "/") return "home";
        if (path == NetPage.Path) return "net";
        if (path == Dev.Path) return "dev";
        if (path == StoragePage.Path) return "storage";
        if (path == "/term") return "term";
        return ProgramLog.Parse(path, out bool text) != null && !text ? "console" : "";
    }

    /// <summary>The pages each request may come from; a type not listed (ready) from any of them.</summary>
    static readonly Dictionary<string, string[]> Senders = new()
    {
        ["browse"] = new[] { "home" }, ["save"] = new[] { "home" }, ["delete"] = new[] { "home" }, ["order"] = new[] { "home" },
        ["open"] = new[] { "home" }, ["openAll"] = new[] { "home" }, ["log"] = new[] { "home" },
        ["stop"] = new[] { "home", "console" }, ["terminal"] = new[] { "home", "console" },
        ["command"] = new[] { "console" }, ["cd"] = new[] { "console" }, ["input"] = new[] { "console" }, ["stopCommand"] = new[] { "console" },
        ["net"] = new[] { "home", "net" }, ["netOpen"] = new[] { "home", "dev" }, ["devOpen"] = new[] { "home", "net" },
        ["netClear"] = new[] { "net" }, ["netExport"] = new[] { "net" }, ["mockFrom"] = new[] { "net" }, ["mockOpen"] = new[] { "net" },
        ["mockSave"] = new[] { "net" }, ["mockOn"] = new[] { "net" }, ["mockDelete"] = new[] { "net" },
        ["dev"] = new[] { "dev" }, ["lang"] = new[] { "dev" }, ["devReset"] = new[] { "dev" }, ["settingsReset"] = new[] { "dev" },
        ["storage"] = new[] { "storage" },
        ["termStart"] = new[] { "term" }, ["termIn"] = new[] { "term" }, ["termSize"] = new[] { "term" },
    };

    /// <summary>
    /// Requests of the start page. Every page may post web messages, and these start programs,
    /// so only messages from the start page itself are heard.
    /// </summary>
    void OnWebMessage(Tab tab, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Any page of a split may say where it is scrolled to; that moves nothing but the other half
        if (syncScroll && IsPane(tab)) OnScrolled(tab, e);
        OnPageNet(tab, e);
        if (!Home.Is(e.Source)) return;
        Dictionary<string, object>? m;
        try { m = ProjectStore.Json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson); }
        catch (Exception) { return; }
        if (m == null) return;
        string? Text(string key) => m.TryGetValue(key, out var v) ? v as string : null;
        bool Flag(string key) => m.TryGetValue(key, out var v) && v is true;
        int Number(string key) => m.TryGetValue(key, out var v) && v is int n ? n : 0;
        var project = ProjectStore.Find(Text("id"));
        // A project's console, or PowerShell's of no project
        var console = ProgramLog.Find(Text("id"));
        // Each request only from the page that makes it: a flaw in one page cannot reach the others' powers
        var type = Text("type") ?? "";
        if (Senders.TryGetValue(type, out var senders) && !senders.Contains(PageOf(e.Source))) return;
        switch (type)
        {
            case "ready":
                SendProjects(tab);
                SendNet(tab);
                break;
            case "net" when m.TryGetValue("autoReload", out var reloadOn):
                App.Current.S.SaveAutoReload(reloadOn is true);
                App.Current.ApplyNet();
                break;
            case "net" when m.TryGetValue("clearOnExit", out var clear):
                App.Current.S.SaveClearOnExit(clear is true);
                App.Current.ApplyNet();
                break;
            case "net":
                // The start page's switch or the /net page: what is not sent stays as it is
                bool localOnly = m.TryGetValue("localOnly", out var lo) ? lo is true : NetGuard.LocalOnly,
                    journal = m.TryGetValue("journal", out var j) ? j is true : NetGuard.Journal,
                    ignoreCors = m.TryGetValue("cors", out var cors) ? cors is true : NetGuard.IgnoreCors;
                var allow = Text("allow") ?? NetGuard.AllowText;
                // Not from inside the WebView's own event: an engine restart closes this WebView too
                BeginInvoke(new Action(() => App.Current.SetNet(localOnly, journal, allow, ignoreCors)));
                break;
            case "netExport" when m.TryGetValue("seqs", out var seqs) && seqs is System.Collections.IEnumerable list:
                var numbers = list.Cast<object>().Select(o => o is int i ? i : o is long l ? l : -1L).Where(n => n > 0).ToList();
                bool csv = Text("format") == "csv";
                BeginInvoke(new Action(() => ExportJournal(numbers, csv)));
                break;
            case "mockFrom" when m.TryGetValue("seq", out var seq) && seq is int n:
                MockFromEntry(tab, n);
                break;
            case "mockOpen" when MockStore.Find(Text("id")) is { } opened:
                SendMock(tab, opened);
                break;
            case "mockSave":
                var mock = new Mock
                {
                    Id = MockStore.Find(Text("id"))?.Id ?? "",
                    On = !m.ContainsKey("on") || Flag("on"),
                    Method = Text("method") ?? "",
                    Url = (Text("url") ?? "").Trim(),
                    Status = Number("status"),
                    Type = (Text("mime") ?? "").Trim() is { Length: > 0 } mime ? mime : "text/plain; charset=utf-8",
                    Body = Text("body") ?? "",
                };
                if (MockStore.Problem(mock) is { } problem)
                {
                    BeginInvoke(new Action(() => MessageBox.Show(this, problem, L.T("Заглушка не сохранена"), MessageBoxButtons.OK, MessageBoxIcon.Warning)));
                    break;
                }
                MockStore.Save(mock);
                App.Current.ApplyNet();
                break;
            case "mockOn" when Text("id") is { } mockId:
                MockStore.SetOn(mockId, Flag("on"));
                App.Current.ApplyNet();
                break;
            case "mockDelete" when Text("id") is { } deleted:
                MockStore.Delete(deleted);
                App.Current.ApplyNet();
                break;
            case "storage" when StoragePage.Is(e.Source) && tab.StorageOf is { } site:
                StorageOp(tab, site, m);
                break;
            case "netClear":
                NetLog.Clear();
                break;
            // The browser's pages open where they are clicked, Ctrl+click in a new tab
            case "netOpen":
                OpenHereOrNew(tab, NetPage.Url, Flag("newTab"));
                break;
            case "devOpen":
                OpenHereOrNew(tab, Dev.Url, Flag("newTab"));
                break;
            case "dev" when Dev.Is(e.Source) && Text("id") is { } devId:
                if (Dev.Set(devId, Flag("on"))) BeginInvoke(new Action(App.Current.ApplyDev));
                break;
            case "lang" when Dev.Is(e.Source) && Text("value") is "auto" or "ru" or "en":
                var language = Text("value")!;
                BeginInvoke(new Action(() => App.Current.SetLanguage(language)));
                break;
            case "settingsReset" when Dev.Is(e.Source):
                BeginInvoke(new Action(ConfirmResetSettings));
                break;
            case "devReset" when Dev.Is(e.Source):
                Dev.Reset();
                BeginInvoke(new Action(App.Current.ApplyDev));
                break;
            case "open" when project != null:
                // One of the project's own links, never an address the page makes up
                var link = Text("link");
                if (link != null && !project.Links.Any(l => l.Url == link)) break;
                if (Flag("newWindow")) App.Current.OpenProjectInNewWindow(project, link);
                else if (Flag("newTab")) _ = OpenProjectInNewTabAsync(project, front: false, link);
                else OpenProject(tab, project, link);
                break;
            case "openAll" when project != null:
                OpenAll(tab, project, Flag("newTab"));
                break;
            case "log" when console != null:
                // The console of the project (its program's output), or PowerShell's: always a tab of its own
                if (Flag("newTab") || ProgramLog.IsShell(console)) OpenNewTab(ProgramLog.Url(console));
                else tab.Core?.Navigate(ProgramLog.Url(console));
                break;
            case "terminal":
                // PowerShell in a terminal: in the console's folder, or the user's from the start page;
                // in this tab, or a new one on Ctrl+click or the mouse wheel
                var termDir = console != null ? App.Current.LauncherFor(console).CommandDir
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (Flag("newTab")) OpenTerminal(termDir);
                else OpenTerminalHere(tab, termDir);
                break;
            case "termStart" when TermPage.Is(e.Source):
                StartTerm(tab, Number("cols"), Number("rows"));
                break;
            case "termIn" when Text("data") is { } keys:
                tab.Term?.Write(keys);
                break;
            case "termSize":
                tab.Term?.Resize(Number("cols"), Number("rows"));
                break;
            case "input" when project != null && Text("text") is { } typed:
                if (App.Current.RunningIds.Contains(project.Id)) App.Current.LauncherFor(project).Send(typed);
                break;
            case "command" when console != null && Text("text") is { } line:
                App.Current.LauncherFor(console).Run(line);
                break;
            case "cd" when console != null && Text("dir") is { } folder:
                App.Current.LauncherFor(console).ChangeDir(folder);
                break;
            case "stopCommand" when console != null:
                App.Current.LauncherFor(console).StopCommand();
                break;
            case "stop" when project != null:
                App.Current.StopProject(project.Id);
                break;
            case "delete" when project != null:
                App.Current.DeleteProject(project);
                break;
            case "save":
                if (m.TryGetValue("project", out var raw)) SaveProject(raw, Text("iconPath"));
                break;
            case "order":
                // The tiles dragged into a new order
                if (m.TryGetValue("ids", out var order) && order is System.Collections.IEnumerable ids
                    && ProjectStore.Reorder(ids.OfType<string>()))
                    App.Current.ProjectsChanged();
                break;
            case "browse":
                var field = Text("field");
                var current = Text("current") ?? "";
                // Not from inside the WebView's own event: the dialogs are modal
                BeginInvoke(new Action(() => Browse(tab, field, current)));
                break;
        }
    }

    static readonly Regex ColorPattern = new("^#[0-9a-fA-F]{6}$");
    const int MaxLinks = 20;

    /// <summary>An address a tile may open: http, https or a file.</summary>
    static Uri? Openable(string? address) =>
        Uri.TryCreate((address ?? "").Trim(), UriKind.Absolute, out var u) && (u.Scheme is "http" or "https" or "file") ? u : null;

    /// <summary>A name for an address given none: the site, or the file's name.</summary>
    static string NameOf(Uri url) => url.IsFile ? Path.GetFileName(url.LocalPath) : url.Authority;

    void SaveProject(object raw, string? iconPath)
    {
        Project p;
        try { p = ProjectStore.Json.ConvertToType<Project>(raw); }
        catch (Exception) { return; }
        p.Url = (p.Url ?? "").Trim();
        if (Openable(p.Url) is not { } url) return;
        p.Name = (p.Name ?? "").Trim();
        if (p.Name.Length == 0) p.Name = NameOf(url);
        // Links open like the project's own address; one left without a name is named after its site
        var links = new List<ProjectLink>();
        foreach (var l in p.Links)
        {
            if (l == null || Openable(l.Url) is not { } u || links.Count == MaxLinks) continue;
            var name = (l.Name ?? "").Trim();
            links.Add(new ProjectLink { Url = l.Url.Trim(), Name = name.Length > 0 ? name : NameOf(u) });
        }
        p.Links = links;
        p.Color = ColorPattern.IsMatch(p.Color ?? "") ? p.Color! : "";
        p.Exe = (p.Exe ?? "").Trim();
        p.Args = (p.Args ?? "").Trim();
        p.WorkDir = (p.WorkDir ?? "").Trim();
        var letters = new System.Globalization.StringInfo((p.Letters ?? "").Trim());
        p.Letters = letters.LengthInTextElements > 3 ? letters.SubstringByTextElements(0, 3) : letters.String;
        p.IconSource = p.IconSource is "file" or "none" ? p.IconSource : "site";
        var old = ProjectStore.Find(p.Id);
        if (old == null) p.Id = "";
        p.Icon = old?.Icon ?? ""; // pictures are named here, never by the page
        ProjectStore.Save(p); // a new project gets its Id

        // The picture stays while its source does; a newly picked one replaces it
        bool picked = p.IconSource == "file" && !string.IsNullOrEmpty(iconPath) && Icons.Problem(iconPath!) == null;
        if (picked || old == null || old.IconSource != p.IconSource || p.IconSource == "none")
        {
            Icons.Delete(p.Icon);
            p.Icon = "";
        }
        if (picked)
        {
            try { p.Icon = Icons.Import(p.Id, iconPath!); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }
        ProjectStore.Save(p);
        App.Current.ProjectSaved(p);
        if (p.IconSource == "site" && p.Icon.Length == 0) App.Current.FetchSiteIcon(p);
    }

    /// <summary>A project page open in a tab gives its tile the site's icon, when the tile is to have one and has none.</summary>
    async void TakeSiteIcon(Tab tab)
    {
        var core = tab.Core;
        if (core == null || !Uri.TryCreate(core.Source, UriKind.Absolute, out var here)) return;
        var project = ProjectStore.All.FirstOrDefault(p => p.IconSource == "site" && p.Icon.Length == 0
            && Uri.TryCreate(p.Url, UriKind.Absolute, out var u) && SameSite(u, here));
        if (project == null || !App.Current.ClaimIcon(project.Id)) return;
        var bytes = await Favicons.CaptureAsync(core);
        App.Current.SetSiteIcon(project.Id, bytes);
    }

    void Browse(Tab tab, string? field, string current)
    {
        var path = field switch
        {
            "Exe" => Pickers.Executable(this, current),
            "WorkDir" => Pickers.Folder(this, current),
            "Icon" => Pickers.Picture(this),
            _ => null,
        };
        if (path == null || tab.Core is not { } core || !Home.Is(core.Source)) return;
        var reply = new Dictionary<string, object> { ["type"] = "browsed", ["field"] = field!, ["path"] = path };
        if (field == "Icon")
        {
            var problem = Icons.Problem(path);
            if (problem != null) reply["error"] = problem;
            else
            {
                try { reply["preview"] = Icons.Preview(path); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { reply["error"] = L.T("Файл не читается."); }
            }
        }
        core.PostWebMessageAsJson(ProjectStore.Json.Serialize(reply));
    }

    void ReloadOrStop()
    {
        var tab = active;
        if (tab?.Core is not { } core) return;
        if (tab.Loading) core.Stop();
        else if (tab.ShowingInternalPage && tab.FailedUrl != null) core.Navigate(tab.FailedUrl);
        else if (tab.ShowingInternalPage && tab.LastProject != null) OpenProject(tab, tab.LastProject, tab.LastLink);
        else if (tab.ShowingInternalPage) GoHome();
        else core.Reload();
    }

    /// <summary>The origin of the site a tab is on (http or https); null on the browser's own pages.</summary>
    static string? SiteOrigin(Tab tab) =>
        !tab.ShowingInternalPage && !IsInternal(tab.Site) && Uri.TryCreate(tab.Site, UriKind.Absolute, out var u)
        && (u.Scheme == "http" || u.Scheme == "https") ? u.GetLeftPart(UriPartial.Authority) : null;

    /// <summary>
    /// Ctrl+Shift+R: the site's service workers and Cache Storage go (a stale worker otherwise keeps serving
    /// the old build), the HTTP cache too, and the page loads anew past any cache. Cookies and storage stay.
    /// </summary>
    void ResetSite()
    {
        if (active != null) ResetSite(active);
    }

    async void ResetSite(Tab tab)
    {
        if (tab.Core is not { } core) return;
        if (SiteOrigin(tab) is not { } origin)
        {
            ReloadOrStop();
            return;
        }
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Storage.clearDataForOrigin", ProjectStore.Json.Serialize(
                new Dictionary<string, object> { ["origin"] = origin, ["storageTypes"] = "service_workers,cache_storage" }));
            await core.CallDevToolsProtocolMethodAsync("Network.clearBrowserCache", "{}");
            if (tab.Core == core) await core.CallDevToolsProtocolMethodAsync("Page.reload", "{\"ignoreCache\":true}");
        }
        catch (Exception) { if (tab.Core == core) core.Reload(); } // the page went meanwhile, or an old runtime
    }

    void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            var url = ToUrl(address.Text);
            if (url == null) return;
            try { Core?.Navigate(url); }
            catch (ArgumentException) { Core?.Navigate(SearchCountry.SearchUrl(address.Text.Trim())); }
            active?.Ctl?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            if (active != null) address.Text = AddressOf(active);
            active?.Ctl?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        }
    }

    static string? ToUrl(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return null;
        if (t.Contains("://") || t.StartsWith("about:")) return t;
        if (!t.Contains(" "))
        {
            if (t.StartsWith("localhost") || Regex.IsMatch(t, @"^\d{1,3}(\.\d{1,3}){3}(:\d+)?(/|$)")) return "http://" + t;
            if (t.Contains(".") && !t.EndsWith(".")) return "https://" + t;
        }
        return SearchCountry.SearchUrl(t);
    }

    void FocusAddress()
    {
        address.Focus();
        address.SelectAll();
    }

    Action? Shortcut(Keys keys)
    {
        switch (keys)
        {
            case Keys.Control | Keys.L:
            case Keys.Alt | Keys.D:
            case Keys.F6:
                return FocusAddress;
            case Keys.Control | Keys.T:
                return () => OpenNewTab(null);
            case Keys.Control | Keys.N:
                return () => App.Current.OpenWindow(null, home: true).Show();
            case Keys.Control | Keys.W:
            case Keys.Control | Keys.F4:
                return () => { if (active != null) CloseTab(active); };
            case Keys.Control | Keys.Tab:
                return () => SelectNext(1);
            case Keys.Control | Keys.Shift | Keys.Tab:
                return () => SelectNext(-1);
            case Keys.Alt | Keys.Home:
                return GoHome;
            case Keys.Alt | Keys.Left:
                return () => Step(-1);
            case Keys.Alt | Keys.Right:
                return () => Step(1);
            case Keys.F5:
            case Keys.Control | Keys.R:
                return ReloadOrStop;
            // Switched off on the «Для разработчика» page: the key goes to the page, as in other browsers
            case Keys.Control | Keys.Shift | Keys.R when Dev.On("reset"):
            case Keys.Control | Keys.F5 when Dev.On("reset"):
                return ResetSite;
            case Keys.Control | Keys.Shift | Keys.S when Dev.On("snapshot"):
                return () => { if (active != null) Snapshot(active, full: true); };
            case Keys.Shift | Keys.Escape:
                return () => Core?.OpenTaskManagerWindow();
            // Out of a page's full screen, as in other browsers
            case Keys.Escape when fullScreen != null:
            case Keys.F11 when fullScreen != null:
                return ExitFullScreen;
        }
        return null;
    }

    static readonly HashSet<Keys> TermKeys = new()
    {
        Keys.Control | Keys.L, Keys.Control | Keys.R, Keys.F5, Keys.Alt | Keys.D, Keys.Alt | Keys.Left, Keys.Alt | Keys.Right,
        Keys.Control | Keys.Shift | Keys.R, Keys.Control | Keys.F5,
    };

    // Keys pressed while the page has focus arrive here instead of ProcessCmdKey
    void OnAcceleratorKeyPressed(object? sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
    {
        if (e.KeyEventKind != CoreWebView2KeyEventKind.KeyDown && e.KeyEventKind != CoreWebView2KeyEventKind.SystemKeyDown)
            return;
        var keys = (Keys)e.VirtualKey | ModifierKeys;
        // In a terminal these belong to the shell: clear screen, search typed commands, words
        if (TermKeys.Contains(keys) && tabs.FirstOrDefault(t => t.Ctl == sender) is { Term: not null })
        {
            e.IsBrowserAcceleratorKeyEnabled = false;
            return;
        }
        var action = Shortcut(keys);
        if (action == null) return;
        e.Handled = true;
        BeginInvoke(action); // not from inside the WebView's own event
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var action = Shortcut(keyData);
        if (action == null) return base.ProcessCmdKey(ref msg, keyData);
        action();
        return true;
    }

    /// <summary>A page opens a window: it becomes a tab here, in the background when the link was Ctrl+clicked.</summary>
    async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        bool front = (ModifierKeys & (Keys.Control | Keys.Shift)) != Keys.Control;
        // A site opening a page of the browser gets no hold of it (no opener, which with CORS off could script it)
        if (Home.Is(e.Uri) && !(sender is CoreWebView2 asker && Home.Is(asker.Source)))
        {
            e.Handled = true;
            var target = e.Uri;
            BeginInvoke(new Action(() => OpenNewTab(target)));
            return;
        }
        var deferral = e.GetDeferral();
        try
        {
            // A new window shares its opener's profile (the engine wants it so, and a login popup needs its cookies)
            // and its project, so that the site does not move it into another tile's profile
            var opener = tabs.FirstOrDefault(t => t.Core == sender);
            var tab = await CreateTabAsync(null, opener?.Profile ?? "");
            if (tab?.Core is not { } core) return;
            tab.LastProject = opener?.LastProject;
            try
            {
                e.NewWindow = core;
                e.Handled = true;
            }
            catch (Exception)
            {
                // The opener went meanwhile (closed, unloaded, the engine restarted)
                tab.Closed = true;
                tab.Ctl?.Close();
                tab.Ctl = null;
                return;
            }
            Add(tab, front);
        }
        finally
        {
            deferral.Complete();
        }
    }

    void UpdateRam()
    {
        var env = App.Current.Env;
        if (env == null || minimized) return;
        long bytes = Memory.PrivateWorkingSet(Memory.SelfId);
        try
        {
            foreach (var info in env.GetProcessInfos()) bytes += Memory.PrivateWorkingSet(info.ProcessId);
        }
        catch (Exception) { return; }
        ram.Text = L.T($"{bytes >> 20} МБ");
    }

    /// <summary>Asks Chromium to drop caches, garbage and graphics memory; the App trims the rest.</summary>
    void EnterBackground()
    {
        if (inBackground) return;
        inBackground = true;
        foreach (var tab in OnScreen)
            if (tab.Core is { } core) core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
        App.Current.BackgroundChanged();
    }

    void LeaveBackground()
    {
        if (!inBackground) return;
        inBackground = false;
        // The other tabs stay on Low: they are out of sight in any case
        foreach (var tab in OnScreen)
            if (tab.Core is { } core) core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
        App.Current.BackgroundChanged();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        bool now = WindowState == FormWindowState.Minimized;
        if (now == minimized) return;
        minimized = now;
        foreach (var tab in OnScreen)
            if (tab.Ctl is { } c) c.IsVisible = !now && (fullScreen == null || tab == fullScreen);
        if (now) EnterBackground();
        else LeaveBackground();
        if (App.Current.S.AutoReload) WatchFolders();
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        foreach (var tab in tabs) tab.Ctl?.NotifyParentWindowPositionChanged();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        App.Current.NoteActive(this);
        backgroundTimer.Stop();
        LeaveBackground();
        if (!address.Focused) active?.Ctl?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!minimized) backgroundTimer.Start();
    }

    bool clearing;

    /// <summary>Hidden while it clears cookies and cache on exit: it takes no more addresses.</summary>
    public bool ShuttingDown => clearing;

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel) return;
        // The last window: cookies and cache go first, while the engine still runs (a few seconds at most)
        if (!clearing && e.CloseReason != CloseReason.WindowsShutDown && e.CloseReason != CloseReason.TaskManagerClosing
            && App.Current.ClearsOnClose(this))
        {
            clearing = true;
            e.Cancel = true;
            Hide();
            await Task.WhenAny(App.Current.ClearDataAsync(Handle), Task.Delay(8000));
            Close();
            return;
        }
        ramTimer.Stop();
        backgroundTimer.Stop();
        freezeTimer.Stop();
        if (isMain)
        {
            var bounds = fullScreen != null ? beforeFullBounds : WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            bool maximized = fullScreen != null ? beforeFullState == FormWindowState.Maximized : WindowState == FormWindowState.Maximized;
            Settings.SaveWindow(bounds, maximized, active?.Ctl?.ZoomFactor ?? zoom);
        }
        foreach (var tab in tabs)
        {
            tab.Closed = true;
            StopTerm(tab);
            var c = tab.Ctl;
            tab.Ctl = null;
            c?.Close();
        }
        tabs.Clear();
        active = null;
    }
}

/// <summary>Menu colours of the dark theme.</summary>
sealed class DarkMenuColors : ProfessionalColorTable
{
    static readonly Color Hover = Theme.Mix(Theme.Face, Theme.Text, .15f), Check = Color.FromArgb(0x4d, 0x6b, 0xfe);
    public override Color ToolStripDropDownBackground => Theme.Face;
    public override Color ImageMarginGradientBegin => Theme.Face;
    public override Color ImageMarginGradientMiddle => Theme.Face;
    public override Color ImageMarginGradientEnd => Theme.Face;
    public override Color MenuBorder => Theme.Line;
    public override Color MenuItemBorder => Hover;
    public override Color MenuItemSelected => Hover;
    public override Color SeparatorDark => Theme.Line;
    public override Color SeparatorLight => Theme.Face;
    public override Color CheckBackground => Check;
    public override Color CheckSelectedBackground => Check;
    public override Color CheckPressedBackground => Check;
}

/// <summary>A toolbar button that never takes keyboard focus away from the page.</summary>
sealed class ToolButton : Button
{
    public ToolButton() => SetStyle(ControlStyles.Selectable, false);
}
