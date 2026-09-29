using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace LiteBro;

/// <summary>A page in a window's tab strip, with a WebView2 controller of its own in the shared environment.</summary>
sealed class Tab
{
    /// <summary>null while the tab is unloaded: closed after a long time in the background, loaded again when picked.</summary>
    public CoreWebView2Controller? Ctl;
    public CoreWebView2? Core => Ctl?.CoreWebView2;
    public string Title = "";
    /// <summary>The page address, kept for when the WebView is closed.</summary>
    public string Address = "";
    /// <summary>The WebView2 profile of the tab's WebView: "" for the shared one, else a project's own (Project.Profile).</summary>
    public string Profile = "";
    public bool Loading, ShowingInternalPage, TrimmedAfterLoad, Suspended, PlayingAudio, Closed;
    /// <summary>The project whose start page (or failure page) is shown: F5 tries it again.</summary>
    public Project? LastProject;
    /// <summary>The link of LastProject being opened; null for the project's own address.</summary>
    public string? LastLink;
    /// <summary>The address that did not answer, while this program's page about it is shown.</summary>
    public string? FailedUrl;
    /// <summary>When the tab left the front; null while it is the tab in front.</summary>
    public DateTime? InactiveSince;
    /// <summary>The shell of a terminal tab, while it runs.</summary>
    public Terminal? Term;
    /// <summary>The folder a terminal tab starts its shell in; set only by the browser when it opens the tab.</summary>
    public string? TermDir;
    /// <summary>The «только localhost» request filter and the web socket script are in place on the current WebView.</summary>
    public bool NetFilter;
    /// <summary>The request filters of the mocks (MockStore) in place on the current WebView.</summary>
    public HashSet<string> MockFilters = new();
    /// <summary>The screen and network the tab emulates (the toolbar's phone button); null for its own.</summary>
    public Emulation.Device? Device;
    public Emulation.Speed? Speed;
    public string? NetScript;
    public int NetGeneration;
    /// <summary>The journal's listener to responses, only while it is on: each response it hears crosses to this process.</summary>
    public EventHandler<CoreWebView2WebResourceResponseReceivedEventArgs>? NetResponse;
    /// <summary>
    /// Pages beyond the engine's history: left behind when the WebView was replaced (a profile change, an unload)
    /// or by a step out of that history. The last one is the nearest.
    /// </summary>
    public readonly List<string> Before = new(), Ahead = new();
    /// <summary>Entries of the engine's history below this one were stepped out of: they are in Ahead or Before now.</summary>
    public int Floor;
    /// <summary>The engine's history as last read, and the entry the tab is on.</summary>
    public string[] Trail = Array.Empty<string>();
    public int TrailAt;
    /// <summary>A page from Before or Ahead is being opened: it does not drop Ahead as a new page would.</summary>
    public bool Stepping;

    /// <summary>The address the tab stands for: the project's (or its link's) while its start or failure page is shown.</summary>
    public string Site => ShowingInternalPage && FailedUrl != null ? FailedUrl
        : ShowingInternalPage && LastProject != null ? LastLink ?? LastProject.Url
        // Until the first navigation starts, the WebView is on about:blank: the address it is going to stands for it
        : Core?.Source is { } source && !(source == "about:blank" && Address.Length > 0) ? source : Address;

    public string Label => Title.Length > 0 ? Title
        : Site.Length > 0 && !Site.StartsWith("about:") ? Site : "Новая вкладка";
}

/// <summary>The row of tabs above the toolbar, drawn by hand: a click brings a tab forward, its cross or a middle click closes it.</summary>
sealed class TabStrip : Control
{
    const string GlyphClose = "", GlyphAdd = "", GlyphSound = "";
    // The tab in front has the toolbar's colour and merges with it
    static Color Face => Theme.Face;

    readonly ToolTip tip = new();
    readonly Font small = new(BrowserForm.IconFont, 7.5f), plus = new(BrowserForm.IconFont, 9.5f);
    IReadOnlyList<Tab> tabs = Array.Empty<Tab>();
    Tab? active, beside, splitLeft, splitRight;
    // Index of the tab under the mouse; tabs.Count is the new tab button
    int hover = -1;
    bool overClose;
    string tipText = "";

    public event Action<Tab>? Picked, Closing;
    /// <summary>A right click on a tab, with where to show its menu.</summary>
    public event Action<Tab, Point>? Menu;
    public event Action? NewTab;

    public TabStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Dock = DockStyle.Top;
        BackColor = Theme.Strip;
        Font = new Font("Segoe UI", 9f);
    }

    public void ApplyTheme()
    {
        BackColor = Theme.Strip;
        Invalidate();
    }

    /// <param name="left">In a split, the tab on the left half; null without a split.</param>
    /// <param name="right">In a split, the tab on the right half.</param>
    public void SetTabs(IReadOnlyList<Tab> list, Tab? front, Tab? left = null, Tab? right = null)
    {
        tabs = list;
        active = front;
        splitLeft = left;
        splitRight = right;
        beside = left == null ? null : front == left ? right : left;
        var p = PointToClient(MousePosition);
        hover = ClientRectangle.Contains(p) ? Hit(p, out overClose) : -1;
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Height = Font.Height * 2 + 2;
    }

    int Unit => Font.Height;
    int TabTop => Unit / 3;

    int TabWidth
    {
        get
        {
            int room = Width - Unit - Height;
            int w = tabs.Count == 0 ? room : room / tabs.Count;
            return Math.Max(Unit * 3, Math.Min(Unit * 15, w));
        }
    }

    Rectangle TabRect(int i) => new(Unit / 2 + i * TabWidth, TabTop, TabWidth, Height - TabTop);

    Rectangle AddRect
    {
        get
        {
            int s = Height - TabTop - Unit / 2;
            return new(Unit / 2 + tabs.Count * TabWidth + Unit / 4, TabTop + (Height - TabTop - s) / 2, s, s);
        }
    }

    Rectangle CloseRect(Rectangle tab)
    {
        int s = Unit + 3;
        return new(tab.Right - s - Unit / 3, tab.Top + (tab.Height - s) / 2, s, s);
    }

    bool HasClose(int i) => tabs[i] == active || tabs[i] == beside || TabWidth >= Unit * 5;

    int Hit(Point p, out bool close)
    {
        close = false;
        if (AddRect.Contains(p)) return tabs.Count;
        for (int i = 0; i < tabs.Count; i++)
        {
            var r = TabRect(i);
            if (!r.Contains(p)) continue;
            close = HasClose(i) && CloseRect(r).Contains(p);
            return i;
        }
        return -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        const TextFormatFlags Center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        for (int i = 0; i < tabs.Count; i++)
        {
            var tab = tabs[i];
            var r = TabRect(i);
            bool front = tab == active;
            if (front || tab == beside)
            {
                // The tab beside the one in front in a split: shaped the same, a shade darker
                using var brush = new SolidBrush(front ? Face : Mix(BackColor, Face, i == hover ? .8f : .6f));
                using var path = Rounded(r, Unit / 2, allCorners: false);
                g.FillPath(brush, path);
            }
            else if (i == hover)
            {
                var shape = new Rectangle(r.X + 2, r.Y + 2, r.Width - 4, r.Height - 6);
                using var brush = new SolidBrush(Mix(BackColor, Face, .5f));
                using var path = Rounded(shape, Unit / 2, allCorners: true);
                g.FillPath(brush, path);
            }
            else if (i + 1 < tabs.Count && tabs[i + 1] != active && tabs[i + 1] != beside && i + 1 != hover)
            {
                using var pen = new Pen(Mix(BackColor, Theme.Text, .25f));
                g.DrawLine(pen, r.Right - 1, r.Top + r.Height / 4, r.Right - 1, r.Bottom - r.Height / 3);
            }

            int left = r.Left + Unit * 2 / 3;
            // Which half of a split the tab is on, in that half's colour
            if (tab == splitLeft || tab == splitRight)
            {
                bool l = tab == splitLeft;
                int h = Unit + 2, w = Unit + 2;
                var badge = new Rectangle(left, r.Top + (r.Height - h) / 2, w, h);
                using var brush = new SolidBrush(l ? Theme.LeftPane : Theme.RightPane);
                using var path = Rounded(badge, 4, allCorners: true);
                g.FillPath(brush, path);
                using var bold = new Font(Font, FontStyle.Bold);
                TextRenderer.DrawText(g, l ? "L" : "R", bold, badge, Color.White, Center);
                left += w + Unit / 3;
            }
            if (tab.Loading)
            {
                int s = Unit * 2 / 3;
                using var pen = new Pen(Color.FromArgb(0x4d, 0x6b, 0xfe), Math.Max(2, Unit / 7));
                g.DrawArc(pen, left, r.Top + (r.Height - s) / 2, s, s, -90, 270);
                left += s + Unit / 3;
            }
            else if (tab.PlayingAudio)
            {
                var box = new Rectangle(left, r.Top, Unit, r.Height);
                TextRenderer.DrawText(g, GlyphSound, small, box, Theme.Text, Center);
                left += Unit + Unit / 4;
            }

            bool withClose = HasClose(i);
            var close = CloseRect(r);
            int right = withClose ? close.Left - 2 : r.Right - Unit / 3;
            TextRenderer.DrawText(g, tab.Label, Font, Rectangle.FromLTRB(left, r.Top, right, r.Bottom),
                tab.Ctl == null ? Theme.Dim : Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            if (!withClose) continue;
            if (i == hover && overClose)
            {
                using var brush = new SolidBrush(Mix(front ? Face : BackColor, Theme.Text, .15f));
                g.FillEllipse(brush, close);
            }
            TextRenderer.DrawText(g, GlyphClose, small, close, Theme.Text, Center);
        }

        var add = AddRect;
        if (hover == tabs.Count)
        {
            using var brush = new SolidBrush(Mix(BackColor, Face, .6f));
            g.FillEllipse(brush, add);
        }
        TextRenderer.DrawText(g, GlyphAdd, plus, add, Theme.Text, Center);
    }

    static GraphicsPath Rounded(Rectangle r, int radius, bool allCorners)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        if (allCorners)
        {
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        }
        else
        {
            p.AddLine(r.Right, r.Bottom, r.Left, r.Bottom);
        }
        p.CloseFigure();
        return p;
    }

    static Color Mix(Color a, Color b, float t) => Theme.Mix(a, b, t);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = Hit(e.Location, out bool close);
        if (h == hover && close == overClose) return;
        hover = h;
        overClose = close;
        Invalidate();
        var text = h == tabs.Count ? "Новая вкладка (Ctrl+T)"
            : h < 0 ? ""
            : close ? "Закрыть вкладку (Ctrl+W)"
            : Tip(tabs[h]);
        if (text == tipText) return;
        tipText = text;
        tip.SetToolTip(this, text);
    }

    static string Tip(Tab tab)
    {
        var text = tab.Label;
        if (tab.Site.StartsWith("http") && !Home.Is(tab.Site) && tab.Site != text) text += "\n" + tab.Site;
        if (tab.Ctl == null) text += "\nВыгружена из памяти, загрузится по клику";
        return text;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hover = -1;
        overClose = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        int h = Hit(e.Location, out bool close);
        if (h == tabs.Count) NewTab?.Invoke();
        else if (h >= 0 && !close) Picked?.Invoke(tabs[h]);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int h = Hit(e.Location, out bool close);
        if (h < 0 || h >= tabs.Count) return;
        if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && close)) Closing?.Invoke(tabs[h]);
        else if (e.Button == MouseButtons.Right) Menu?.Invoke(tabs[h], e.Location);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tip.Dispose();
            small.Dispose();
            plus.Dispose();
        }
        base.Dispose(disposing);
    }
}
