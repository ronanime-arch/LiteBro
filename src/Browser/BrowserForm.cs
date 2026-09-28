using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    readonly ToolButton back, forward, reload, home, country;
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
    int? dragFrom;
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

        back = MakeButton(GlyphBack, "Назад (Alt+←)", () => Core?.GoBack());
        forward = MakeButton(GlyphForward, "Вперёд (Alt+→)", () => Core?.GoForward());
        reload = MakeButton(GlyphReload, "Обновить (F5)", ReloadOrStop);
        home = MakeButton(GlyphHome, "Проекты (Alt+Home)", GoHome);
        country = MakeButton(GlyphGlobe, "Страна поиска", ShowCountryMenu);
        country.Visible = false;
        countryGlyphFont = country.Font;
        back.Enabled = forward.Enabled = false;

        bar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 7,
            RowCount = 1,
            Padding = new Padding(4, 3, 0, 3),
        };
        for (int i = 0; i < 4; i++) bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.Controls.AddRange(new Control[] { back, forward, reload, home, address, country, ram });

        // Docking goes from the last added: the strip on top, the toolbar under it, the page in what is left
        Controls.Add(host);
        Controls.Add(bar);
        Controls.Add(strip);

        strip.Picked += SelectTab;
        strip.Closing += CloseTab;
        strip.NewTab += () => OpenNewTab(null);
        strip.Menu += ShowTabMenu;
        host.Controls.Add(divider);
        divider.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) dragFrom = e.X; };
        divider.MouseMove += (_, e) =>
        {
            if (dragFrom is not { } from || host.Width == 0) return;
            splitAt = Math.Max(.15f, Math.Min(.85f, (float)(divider.Left + e.X - from + divider.Width / 2) / host.Width));
            LayoutPanes();
        };
        divider.MouseUp += (_, _) => dragFrom = null;
        tips.SetToolTip(ram, "Память браузера, как в диспетчере задач. Клик — диспетчер процессов (Shift+Esc)");
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
        host.Resize += (_, _) => LayoutPanes();
        address.HandleCreated += (_, _) => Theme.ApplyEdit(address.Handle);
        ApplyTheme();
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
        foreach (var b in new[] { back, forward, reload, home, country })
        {
            b.BackColor = Theme.Face;
            b.ForeColor = Theme.Text;
            b.FlatAppearance.MouseOverBackColor = Theme.Mix(Theme.Face, Theme.Text, .12f);
            b.FlatAppearance.MouseDownBackColor = Theme.Mix(Theme.Face, Theme.Text, .2f);
        }
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

    ToolButton MakeButton(string glyph, string tip, Action click)
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
        tips.SetToolTip(b, tip);
        return b;
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        SendMessage(address.Handle, SetCueBanner, (IntPtr)1, "Адрес или поиск");
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
    async Task<Tab?> CreateTabAsync(string? url)
    {
        var tab = new Tab();
        return await LoadAsync(tab, url) ? tab : null;
    }

    /// <summary>Gives a tab its WebView: a new tab, or one closed after a long time in the background.</summary>
    async Task<bool> LoadAsync(Tab tab, string? url)
    {
        CoreWebView2Controller c;
        try
        {
            var env = await App.Current.GetEnvironmentAsync();
            if (IsDisposed) return false;
            c = await env.CreateCoreWebView2ControllerAsync(host.Handle);
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                MessageBox.Show(this, "Не удалось запустить движок WebView2.\n\n" + ex.Message +
                    "\n\nЕсли меняли settings.ini, закройте все окна LiteBro и откройте снова.",
                    "LiteBro", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        if (IsDisposed || tab.Closed)
        {
            c.Close();
            return false;
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
        // The start page is served from here and talks to the browser through web messages
        core.AddWebResourceRequestedFilter(Home.Url + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnHomeRequest;
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
            tab.Address = core.Source;
            ShowState(tab);
        };
        core.HistoryChanged += (_, _) => ShowState(tab);
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
            // Back on the start page nothing is to be tried again on F5
            if (Home.Is(e.Uri)) tab.LastProject = null;
            if (Home.Is(e.Uri) || !(e.Uri.StartsWith("about:") || e.Uri.StartsWith("data:"))) tab.ShowingInternalPage = false;
            SetLoading(tab, true);
        };
        core.NavigationCompleted += (_, e) =>
        {
            SetLoading(tab, false);
            if (!tab.TrimmedAfterLoad)
            {
                tab.TrimmedAfterLoad = true;
                App.Current.TrimHostSoon();
            }
        };
        core.NewWindowRequested += OnNewWindowRequested;
        // Not from inside the WebView's own event: closing the tab closes that WebView
        core.WindowCloseRequested += (_, _) => BeginInvoke(new Action(() => CloseTab(tab)));
        core.IsDocumentPlayingAudioChanged += (_, _) =>
        {
            tab.PlayingAudio = core.IsDocumentPlayingAudio;
            strip.Invalidate();
        };
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited) core.Reload();
        };
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
        back.Enabled = tab.Core?.CanGoBack ?? false;
        forward.Enabled = tab.Core?.CanGoForward ?? false;
        reload.Text = tab.Loading ? GlyphStop : GlyphReload;
        tips.SetToolTip(reload, tab.Loading ? "Остановить" : "Обновить (F5)");
        if (switched || !address.Focused) address.Text = AddressOf(tab);
        ShowCountry(tab);
    }

    /// <summary>The country button: on Google search only, with the code of the picked country or a globe.</summary>
    void ShowCountry(Tab tab)
    {
        country.Visible = !tab.ShowingInternalPage && SearchCountry.IsGoogleSearch(tab.Site);
        var picked = SearchCountry.Current;
        var text = picked?.Code ?? GlyphGlobe;
        if (country.Text == text) return;
        country.Text = text;
        country.Font = picked == null ? countryGlyphFont : countryCodeFont;
        tips.SetToolTip(country, picked == null ? "Страна поиска" : "Страна поиска: " + picked.Name);
    }

    void ShowCountryMenu()
    {
        var picked = SearchCountry.Current;
        var menu = NewMenu();
        menu.Items.Add(new ToolStripMenuItem("Как обычно (без страны)", null, (_, _) => PickCountry(null)) { Checked = picked == null });
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
        if (active != null && !IsPane(tab) && tab != active)
            menu.Items.Add(new ToolStripMenuItem("Открыть рядом", null, (_, _) => SplitWith(tab)));
        if (Split)
            menu.Items.Add(new ToolStripMenuItem("Убрать разделение", null, (_, _) => Unsplit()));
        if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Закрыть вкладку", null, (_, _) => CloseTab(tab)) { ShortcutKeyDisplayString = tab == active ? "Ctrl+W" : "" });
        menu.Show(strip, at);
    }

    /// <summary>Puts a tab beside the one in front, on the right; in a split it takes the place of the tab beside.</summary>
    async void SplitWith(Tab tab)
    {
        if (active == null || tab == active || tab.Closed) return;
        if (Partner is { } old) SendToBackground(old);
        paneLeft = active;
        paneRight = tab;
        divider.Visible = true;
        strip.SetTabs(tabs, active, tab);
        LayoutPanes();
        await ShowPaneAsync(tab);
    }

    /// <summary>Back to one tab on screen: the one beside goes to the background.</summary>
    void Unsplit()
    {
        var partner = Partner;
        EndSplit();
        if (partner != null) SendToBackground(partner);
        strip.SetTabs(tabs, active, Partner);
        LayoutPanes();
    }

    void EndSplit()
    {
        paneLeft = paneRight = null;
        divider.Visible = false;
        dragFrom = null;
    }

    int DividerWidth => Math.Max(4, host.DeviceDpi / 16);

    /// <summary>Where a tab's page goes: the whole of the host, or its side of a split.</summary>
    Rectangle BoundsOf(Tab tab)
    {
        var r = host.ClientRectangle;
        if (!IsPane(tab)) return r;
        int d = DividerWidth, x = (int)(r.Width * splitAt) - d / 2;
        return tab == paneLeft ? new Rectangle(r.X, r.Y, x, r.Height) : new Rectangle(x + d, r.Y, r.Width - x - d, r.Height);
    }

    void LayoutPanes()
    {
        if (Split)
        {
            var left = BoundsOf(paneLeft!);
            divider.Bounds = new Rectangle(left.Right, 0, DividerWidth, host.ClientSize.Height);
        }
        foreach (var tab in OnScreen)
            if (tab.Ctl is { } c) c.Bounds = BoundsOf(tab);
    }

    /// <summary>The tab beside becomes the tab in front, where it is.</summary>
    void FocusPane(Tab tab)
    {
        if (tab == active || !IsPane(tab)) return;
        active = tab;
        strip.SetTabs(tabs, tab, Partner);
        ShowState(tab, switched: true);
    }

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
            strip.SetTabs(tabs, active, Partner);
        }
    }

    /// <summary>Brings a tab forward; one closed in the background loads its page again.</summary>
    async void SelectTab(Tab tab)
    {
        if (tab == active || tab.Closed) return;
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
        strip.SetTabs(tabs, tab, Partner);
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

    /// <summary>Closes a tab; closing the last one closes the window.</summary>
    void CloseTab(Tab tab)
    {
        int i = tabs.IndexOf(tab);
        if (i < 0) return;
        if (tabs.Count == 1)
        {
            Close();
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
        tabs.RemoveAt(i);
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
            strip.SetTabs(tabs, active, Partner);
            LayoutPanes();
        }
    }

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
            if (core.IsDocumentPlayingAudio || App.Current.IsRunningSite(tab.Site)) continue;
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
        // A project's start or failure page comes back as the project's own address, never as a new start
        tab.Address = tab.Site;
        tab.Ctl = null;
        tab.Suspended = tab.Loading = tab.PlayingAudio = false;
        c.Close();
        strip.Invalidate();
    }

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

    /// <param name="link">One of the project's links; null for its own address.</param>
    async Task OpenProjectInNewTabAsync(Project p, bool front, string? link = null)
    {
        var tab = await CreateTabAsync(null);
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
        tab.LastProject = p;
        tab.LastLink = link;
        var launcher = App.Current.LauncherFor(p);
        if (p.Exe.Trim().Length > 0 && !url.IsFile && !await Launcher.IsUpAsync(url))
        {
            ShowInternalPage(tab, Pages.Starting(p, url.AbsoluteUri, launcher));
            // The address the program prints stands for the project's own, never for a link
            var error = await launcher.StartAndWaitAsync(url, printed: link == null);
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
        tab.Core.NavigateToString(html);
        ShowState(tab, switched: true);
    }

    /// <summary>Gives every start page open here the current tiles.</summary>
    public void SendProjects()
    {
        foreach (var tab in tabs) SendProjects(tab);
    }

    void SendProjects(Tab tab)
    {
        if (tab.Core is { } core && !tab.Suspended && Home.Is(core.Source))
            core.PostWebMessageAsJson(Home.State(App.Current.RunningIds));
    }

    void OnHomeRequest(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var env = App.Current.Env;
        if (env == null || !Home.Is(e.Request.Uri)) return;
        var path = new Uri(e.Request.Uri).AbsolutePath;
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
                var json = ProgramLog.Chunk(p, from, App.Current.RunningIds.Contains(p.Id));
                e.Response = env.CreateWebResourceResponse(ProgramLog.Bytes(json), 200, "OK", ProgramLog.JsonHeaders);
            }
        }
        else if (path.StartsWith("/icon/") && Icons.Read(Uri.UnescapeDataString(path.Substring(6))) is { } bytes)
            e.Response = env.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK",
                "Content-Type: " + Icons.ContentType(path) + "\r\n" + Icons.Headers);
        else
            e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "");
    }

    /// <summary>
    /// Requests of the start page. Every page may post web messages, and these start programs,
    /// so only messages from the start page itself are heard.
    /// </summary>
    void OnWebMessage(Tab tab, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Home.Is(e.Source)) return;
        Dictionary<string, object>? m;
        try { m = ProjectStore.Json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson); }
        catch (Exception) { return; }
        if (m == null) return;
        string? Text(string key) => m.TryGetValue(key, out var v) ? v as string : null;
        bool Flag(string key) => m.TryGetValue(key, out var v) && v is true;
        var project = ProjectStore.Find(Text("id"));
        switch (Text("type"))
        {
            case "ready":
                SendProjects(tab);
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
            case "log" when project != null:
                // The console of the project: its program's output
                if (Flag("newTab")) OpenNewTab(ProgramLog.Url(project));
                else tab.Core?.Navigate(ProgramLog.Url(project));
                break;
            case "input" when project != null && Text("text") is { } typed:
                if (App.Current.RunningIds.Contains(project.Id)) App.Current.LauncherFor(project).Send(typed);
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
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { reply["error"] = "Файл не читается."; }
            }
        }
        core.PostWebMessageAsJson(ProjectStore.Json.Serialize(reply));
    }

    void ReloadOrStop()
    {
        var tab = active;
        if (tab?.Core is not { } core) return;
        if (tab.Loading) core.Stop();
        else if (tab.ShowingInternalPage && tab.LastProject != null) OpenProject(tab, tab.LastProject, tab.LastLink);
        else if (tab.ShowingInternalPage) GoHome();
        else core.Reload();
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
                return () => Core?.GoBack();
            case Keys.Alt | Keys.Right:
                return () => Core?.GoForward();
            case Keys.F5:
            case Keys.Control | Keys.R:
                return ReloadOrStop;
            case Keys.Shift | Keys.Escape:
                return () => Core?.OpenTaskManagerWindow();
        }
        return null;
    }

    // Keys pressed while the page has focus arrive here instead of ProcessCmdKey
    void OnAcceleratorKeyPressed(object? sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
    {
        if (e.KeyEventKind != CoreWebView2KeyEventKind.KeyDown && e.KeyEventKind != CoreWebView2KeyEventKind.SystemKeyDown)
            return;
        var action = Shortcut((Keys)e.VirtualKey | ModifierKeys);
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
        var deferral = e.GetDeferral();
        try
        {
            var tab = await CreateTabAsync(null);
            if (tab?.Core is { } core)
            {
                e.NewWindow = core;
                e.Handled = true;
                Add(tab, front);
            }
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
        ram.Text = $"{bytes >> 20} МБ";
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
            if (tab.Ctl is { } c) c.IsVisible = !now;
        if (now) EnterBackground();
        else LeaveBackground();
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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel) return;
        ramTimer.Stop();
        backgroundTimer.Stop();
        freezeTimer.Stop();
        if (isMain)
        {
            var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            Settings.SaveWindow(bounds, WindowState == FormWindowState.Maximized, active?.Ctl?.ZoomFactor ?? zoom);
        }
        foreach (var tab in tabs)
        {
            tab.Closed = true;
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
