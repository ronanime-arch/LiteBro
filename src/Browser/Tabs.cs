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
    public bool Loading, ShowingInternalPage, TrimmedAfterLoad, Suspended, PlayingAudio, Closed;
    /// <summary>The project whose start page (or failure page) is shown: F5 tries it again.</summary>
    public Project? LastProject;
    /// <summary>When the tab left the front; null while it is the tab in front.</summary>
    public DateTime? InactiveSince;

    /// <summary>The address the tab stands for: the project's own while its start or failure page is shown.</summary>
    public string Site => ShowingInternalPage && LastProject != null ? LastProject.Url : Core?.Source ?? Address;

    public string Label => Title.Length > 0 ? Title
        : Site.Length > 0 && !Site.StartsWith("about:") ? Site : "Новая вкладка";
}

/// <summary>The row of tabs above the toolbar, drawn by hand: a click brings a tab forward, its cross or a middle click closes it.</summary>
sealed class TabStrip : Control
{
    const string GlyphClose = "", GlyphAdd = "", GlyphSound = "";
    static readonly Color Face = SystemColors.Control; // the toolbar's colour: the tab in front merges with it

    readonly ToolTip tip = new();
    readonly Font small = new(BrowserForm.IconFont, 7.5f), plus = new(BrowserForm.IconFont, 9.5f);
    IReadOnlyList<Tab> tabs = Array.Empty<Tab>();
    Tab? active;
    // Index of the tab under the mouse; tabs.Count is the new tab button
    int hover = -1;
    bool overClose;
    string tipText = "";

    public event Action<Tab>? Picked, Closing;
    public event Action? NewTab;

    public TabStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Dock = DockStyle.Top;
        BackColor = Mix(Face, SystemColors.ControlDark, .3f);
        Font = new Font("Segoe UI", 9f);
    }

    public void SetTabs(IReadOnlyList<Tab> list, Tab? front)
    {
        tabs = list;
        active = front;
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

    bool HasClose(int i) => tabs[i] == active || TabWidth >= Unit * 5;

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
            if (front || i == hover)
            {
                var shape = front ? r : new Rectangle(r.X + 2, r.Y + 2, r.Width - 4, r.Height - 6);
                using var brush = new SolidBrush(front ? Face : Mix(BackColor, Face, .5f));
                using var path = Rounded(shape, Unit / 2, allCorners: !front);
                g.FillPath(brush, path);
            }
            else if (i + 1 < tabs.Count && tabs[i + 1] != active && i + 1 != hover)
            {
                using var pen = new Pen(Mix(BackColor, SystemColors.ControlText, .25f));
                g.DrawLine(pen, r.Right - 1, r.Top + r.Height / 4, r.Right - 1, r.Bottom - r.Height / 3);
            }

            int left = r.Left + Unit * 2 / 3;
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
                TextRenderer.DrawText(g, GlyphSound, small, box, SystemColors.ControlText, Center);
                left += Unit + Unit / 4;
            }

            bool withClose = HasClose(i);
            var close = CloseRect(r);
            int right = withClose ? close.Left - 2 : r.Right - Unit / 3;
            TextRenderer.DrawText(g, tab.Label, Font, Rectangle.FromLTRB(left, r.Top, right, r.Bottom),
                tab.Ctl == null ? SystemColors.GrayText : SystemColors.ControlText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            if (!withClose) continue;
            if (i == hover && overClose)
            {
                using var brush = new SolidBrush(Mix(front ? Face : BackColor, SystemColors.ControlText, .15f));
                g.FillEllipse(brush, close);
            }
            TextRenderer.DrawText(g, GlyphClose, small, close, SystemColors.ControlText, Center);
        }

        var add = AddRect;
        if (hover == tabs.Count)
        {
            using var brush = new SolidBrush(Mix(BackColor, Face, .6f));
            g.FillEllipse(brush, add);
        }
        TextRenderer.DrawText(g, GlyphAdd, plus, add, SystemColors.ControlText, Center);
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

    static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

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
