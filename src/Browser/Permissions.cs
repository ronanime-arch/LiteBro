using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace LiteBro;

/// <summary>
/// What sites were allowed or refused (camera, microphone, location, notifications…) in the shared profile and in
/// each project's own: read and changed through the profiles' settings, shown in PermsPopup.
/// </summary>
static class SitePermissions
{
    public sealed class Row
    {
        public string Profile = "", ProfileName = "", Origin = "";
        public CoreWebView2PermissionKind Kind;
        public CoreWebView2PermissionState State;
        public string KindName => SitePermissions.KindName(Kind);
    }

    /// <summary>The kinds by their enum names: a newer runtime's kinds keep their English name.</summary>
    static string KindName(CoreWebView2PermissionKind kind) => kind.ToString() switch
    {
        "Microphone" => L.T("Микрофон"),
        "Camera" => L.T("Камера"),
        "Geolocation" => L.T("Местоположение"),
        "Notifications" => L.T("Уведомления"),
        "OtherSensors" => L.T("Датчики движения"),
        "ClipboardRead" => L.T("Чтение буфера обмена"),
        "MultipleAutomaticDownloads" => L.T("Несколько загрузок подряд"),
        "FileReadWrite" => L.T("Чтение и запись файлов"),
        "Autoplay" => L.T("Автовоспроизведение"),
        "LocalFonts" => L.T("Шрифты компьютера"),
        "MidiSystemExclusiveMessages" => L.T("MIDI-устройства"),
        "WindowManagement" => L.T("Окна на нескольких экранах"),
        var other => other,
    };

    /// <summary>The profiles there are: the shared one ("") and each project's own, with the names shown.</summary>
    public static List<(string Profile, string Name)> Profiles() =>
        new[] { ("", L.T("Общий")) }.Concat(ProjectStore.All.Where(p => p.Profile.Length > 0)
            .Select(p => (p.Profile, L.T("Проект «") + p.Name + L.T("»")))).ToList();

    /// <summary>scheme://host:port, as the engine names a permission's origin; null for what has none.</summary>
    public static string? OriginOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")
            ? u.GetLeftPart(UriPartial.Authority) : null;

    /// <summary>
    /// The settings that are not the default «ask»: of every profile, or of one site in one profile.
    /// Each profile is reached through a tab in it, else a hidden WebView (App.WithProfileAsync).
    /// </summary>
    public static async Task<List<Row>> ReadAsync(IntPtr window, string? profile = null, string? origin = null)
    {
        var rows = new List<Row>();
        foreach (var (p, name) in Profiles().Where(x => profile == null || x.Profile == profile))
        {
            var settings = await App.Current.WithProfileAsync(p, window, async data => await data.GetNonDefaultPermissionSettingsAsync());
            foreach (var s in settings)
            {
                if (s.PermissionState == CoreWebView2PermissionState.Default) continue;
                if (origin != null && OriginOf(s.PermissionOrigin) != origin) continue;
                rows.Add(new Row { Profile = p, ProfileName = name, Origin = s.PermissionOrigin, Kind = s.PermissionKind, State = s.PermissionState });
            }
        }
        return rows;
    }

    public static Task SetAsync(IntPtr window, Row row, CoreWebView2PermissionState state) =>
        App.Current.WithProfileAsync(row.Profile, window, async data =>
        {
            await data.SetPermissionStateAsync(row.Kind, row.Origin, state);
            return true;
        });
}

/// <summary>
/// A small window under the toolbar, as a browser's site information: the permissions of one site (the tab's),
/// or of all sites; each changed with its list or forgotten («Спрашивать»). Closes when it loses focus.
/// </summary>
sealed class PermsPopup : Form
{
    static readonly CoreWebView2PermissionState[] States =
        { CoreWebView2PermissionState.Allow, CoreWebView2PermissionState.Deny, CoreWebView2PermissionState.Default };
    static string StateName(CoreWebView2PermissionState s) =>
        s == CoreWebView2PermissionState.Allow ? L.T("Разрешено") : s == CoreWebView2PermissionState.Deny ? L.T("Запрещено") : L.T("Спрашивать");

    readonly IntPtr owner;
    string? profile, origin;
    readonly Label title = new() { AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) };
    readonly Label note = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
    // Not docked: a docked control makes no scroll bar in its panel
    readonly TableLayoutPanel list = new() { ColumnCount = 2, Location = Point.Empty, Padding = new Padding(0) };
    readonly Panel scroll = new() { AutoScroll = true, Dock = DockStyle.Fill };
    readonly LinkLabel all = new() { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
    bool closing;

    /// <param name="profile">The tab's profile and site; null for every site of every profile.</param>
    public PermsPopup(IntPtr owner, string? profile, string? origin)
    {
        this.owner = owner;
        this.profile = profile;
        this.origin = origin;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Theme.Face;
        ForeColor = Theme.Text;
        Padding = new Padding(14, 12, 14, 12);
        note.ForeColor = Theme.Dim;
        all.LinkColor = all.ActiveLinkColor = Color.FromArgb(0x4d, 0x8b, 0xfe);
        list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        list.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        scroll.Controls.Add(list);
        var top = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Top, WrapContents = false };
        top.Controls.Add(title);
        top.Controls.Add(note);
        var bottom = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Bottom, WrapContents = false };
        bottom.Controls.Add(all);
        Controls.Add(scroll);
        Controls.Add(bottom);
        Controls.Add(top);
        all.LinkClicked += (_, _) =>
        {
            this.profile = this.origin = null;
            _ = LoadAsync();
        };
        Deactivate += (_, _) => { if (!closing) { closing = true; BeginInvoke(new Action(Close)); } };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Line);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    /// <summary>Opens under the point (screen), kept on its screen.</summary>
    public void ShowAt(IWin32Window parent, Point at)
    {
        int unit = Font.Height;
        Size = new Size(unit * 24, unit * 10);
        var area = Screen.FromPoint(at).WorkingArea;
        Location = new Point(Math.Max(area.Left, Math.Min(at.X, area.Right - Width)), Math.Min(at.Y, area.Bottom - Height));
        Show(parent);
        _ = LoadAsync();
    }

    async Task LoadAsync()
    {
        bool one = origin != null;
        title.Text = one ? L.T("Разрешения сайта") : L.T("Разрешения сайтов");
        note.Text = one ? origin : L.T("Все сайты во всех профилях");
        all.Text = L.T("Все сайты");
        all.Visible = one;
        List<SitePermissions.Row> rows;
        try { rows = await SitePermissions.ReadAsync(owner, profile, origin); }
        catch (Exception ex)
        {
            Draw(new List<SitePermissions.Row>(), ex.Message);
            return;
        }
        if (!IsDisposed) Draw(rows, null);
    }

    void Draw(List<SitePermissions.Row> rows, string? error)
    {
        list.SuspendLayout();
        foreach (Control c in list.Controls.Cast<Control>().ToList()) c.Dispose();
        list.Controls.Clear();
        list.RowStyles.Clear();
        list.RowCount = 0;
        void AddRow(Control left, Control? right)
        {
            list.RowCount++;
            list.Controls.Add(left, 0, list.RowCount - 1);
            if (right != null) list.Controls.Add(right, 1, list.RowCount - 1);
            else list.SetColumnSpan(left, 2);
        }
        if (error != null)
            AddRow(new Label { Text = error, AutoSize = true, ForeColor = Color.FromArgb(0xd9, 0x43, 0x4b), MaximumSize = new Size(Width - 40, 0) }, null);
        else if (rows.Count == 0)
            AddRow(new Label
            {
                Text = origin != null ? L.T("Этот сайт ещё ничего не спрашивал.") : L.T("Сайты пока ничего не спрашивали, или все выборы уже забыты."),
                AutoSize = true, ForeColor = Theme.Dim, MaximumSize = new Size(Width - 40, 0),
            }, null);
        string? group = null;
        foreach (var row in rows.OrderBy(r => r.Profile).ThenBy(r => r.Origin).ThenBy(r => r.KindName))
        {
            // All sites: a heading per site and profile
            var heading = origin == null ? row.Origin + (row.Profile.Length > 0 ? " · " + row.ProfileName : "") : null;
            if (heading != null && heading != group)
            {
                group = heading;
                AddRow(new Label { Text = heading, AutoSize = true, ForeColor = Theme.Dim, Margin = new Padding(0, list.RowCount > 0 ? 10 : 0, 0, 2) }, null);
            }
            var pick = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Field,
                ForeColor = row.State == CoreWebView2PermissionState.Allow ? Color.FromArgb(0x1f, 0x9d, 0x55) : Color.FromArgb(0xd9, 0x43, 0x4b),
                Width = Font.Height * 8,
                Margin = new Padding(8, 2, 0, 2),
            };
            foreach (var s in States) pick.Items.Add(StateName(s));
            pick.SelectedIndex = Array.IndexOf(States, row.State);
            pick.SelectedIndexChanged += async (_, _) =>
            {
                pick.Enabled = false;
                try { await SitePermissions.SetAsync(owner, row, States[pick.SelectedIndex]); }
                catch (Exception) { }
                if (!IsDisposed) await LoadAsync();
            };
            AddRow(new Label { Text = row.KindName, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 0) }, pick);
        }
        list.ResumeLayout();
        list.Width = scroll.ClientSize.Width - SystemInformation.VerticalScrollBarWidth;
        list.Height = list.GetPreferredSize(new Size(list.Width, 0)).Height;
        // As tall as the list, up to about half a screen
        int unit = Font.Height;
        int want = Padding.Vertical + title.Height + note.Height + note.Margin.Vertical + list.Height
            + (all.Visible ? all.Height + all.Margin.Vertical : 0) + unit;
        Height = Math.Max(unit * 8, Math.Min(want, Screen.FromControl(this).WorkingArea.Height / 2));
        Invalidate();
    }
}
