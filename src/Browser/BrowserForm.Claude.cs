using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace LiteBro;

/// <summary>
/// Claude's tools (Mcp.cs) in this window's tabs. A tab Claude works in is brought to the front first: the user sees
/// what it does, and a paused or unloaded tab comes back. Clicks and keys go through the DevTools protocol as input
/// events, as a person's would; only sites (http, https) can be acted on, never the browser's own pages, whose
/// messages start programs.
/// </summary>
sealed partial class BrowserForm
{
    /// <summary>Runs a tool call on the UI thread, in whichever window holds the tab.</summary>
    public static async Task<Dictionary<string, object>> RunClaudeToolAsync(string name, Dictionary<string, object> args)
    {
        if (!App.Current.S.Claude)
            return Mcp.Text("Claude access is switched off in LiteBro. Ask the user to switch on " + L.T("«Доступ для Claude»") +
                " on the LiteBro page " + L.T("«Для разработчика»") + " (its button is at the right edge of the start page).", true);
        var forms = App.Current.Forms.Where(f => !f.ShuttingDown && !f.IsDisposed).ToList();
        if (name == "tabs") return Mcp.Text(ListTabs(forms));
        if (name == "open")
        {
            var window = App.Current.LastActive is { ShuttingDown: false } last ? last : forms.LastOrDefault();
            if (window == null) return Mcp.Text("No LiteBro window is open.", true);
            return await window.ClaudeOpenAsync(Str(args, "url") ?? "");
        }
        Tab? tab = null;
        BrowserForm? form = null;
        if (Num(args, "tab") is { } id)
        {
            foreach (var f in forms)
                if (f.tabs.FirstOrDefault(t => t.Id == (int)id) is { } t)
                {
                    tab = t;
                    form = f;
                }
            if (tab == null) return Mcp.Text("No tab " + id + ". List them with `tabs`.", true);
        }
        else
        {
            form = App.Current.LastActive is { ShuttingDown: false } last ? last : forms.LastOrDefault();
            tab = form?.active;
            if (tab == null) return Mcp.Text("No tab is open.", true);
        }
        try { return await form!.ClaudeToolAsync(name, tab, args); }
        catch (Exception e) { return Mcp.Text(e.Message, true); }
    }

    static string ListTabs(List<BrowserForm> forms)
    {
        var lines = new List<string>();
        for (int w = 0; w < forms.Count; w++)
            foreach (var t in forms[w].tabs)
                lines.Add("tab " + t.Id + (forms.Count > 1 ? " (window " + (w + 1) + ")" : "") +
                    (t == forms[w].active && forms[w] == App.Current.LastActive ? " [in front]" : t == forms[w].active ? " [front of its window]" : "") +
                    (t.Ctl == null ? " [unloaded]" : "") + ": " + t.Label + " — " + t.Site +
                    (SiteOrigin(t) == null ? " (browser page: read only)" : ""));
        return lines.Count == 0 ? "No tabs." : string.Join("\n", lines);
    }

    static string? Str(Dictionary<string, object> args, string key) => args.TryGetValue(key, out var v) ? v as string : null;

    static double? Num(Dictionary<string, object> args, string key) =>
        args.TryGetValue(key, out var v) && v != null && double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    static bool Flag(Dictionary<string, object> args, string key) => args.TryGetValue(key, out var v) && v is true;

    async Task<Dictionary<string, object>> ClaudeOpenAsync(string text)
    {
        var url = ClaudeUrl(text);
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        var tab = await CreateTabAsync(url);
        if (tab == null) return Mcp.Text("The tab could not be made.", true);
        Add(tab, front: true);
        await WaitLoadAsync(tab, url);
        return Mcp.Text("Opened tab " + tab.Id + ".\n" + await StateOfAsync(tab));
    }

    /// <summary>An address Claude may go to: http or https; text that is no address is searched for.</summary>
    static string ClaudeUrl(string text)
    {
        var url = ToUrl(text) ?? throw new ArgumentException("No address.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https") || Home.Is(url))
            throw new ArgumentException("Only http and https sites can be opened, not " + url + ".");
        return u.AbsoluteUri;
    }

    async Task<Dictionary<string, object>> ClaudeToolAsync(string name, Tab tab, Dictionary<string, object> args)
    {
        if (name == "close_tab")
        {
            MustAct(tab); // a terminal or a console would take its programs along
            CloseTab(tab);
            return Mcp.Text("Closed tab " + tab.Id + ".");
        }
        var core = await BringAsync(tab);
        switch (name)
        {
            case "navigate":
                var to = Str(args, "url") ?? "";
                if (to is "back" or "forward")
                {
                    MustAct(tab);
                    var before = core.Source;
                    Step(to == "back" ? -1 : 1);
                    await WaitLoadAsync(tab, null, before);
                }
                else if (to == "reload")
                {
                    MustAct(tab);
                    core.Reload();
                    await WaitLoadAsync(tab, null);
                }
                else
                {
                    var url = ClaudeUrl(to);
                    core.Navigate(url);
                    await WaitLoadAsync(tab, url);
                }
                return Mcp.Text(await StateOfAsync(tab));
            case "read_page":
                var what = Str(args, "what") ?? "text";
                if (what == "elements")
                {
                    MustAct(tab); // the numbers are for acting on
                    var r = await PageAsync(tab, new Dictionary<string, object> { ["op"] = "elements", ["max"] = 300 });
                    var lines = ((System.Collections.IEnumerable)r["lines"]).Cast<object>().Select(o => Convert.ToString(o)).ToList();
                    int total = Convert.ToInt32(r["total"]);
                    return Mcp.Text("Tab " + tab.Id + ": " + r["title"] + "\n" + r["url"] + "\n" + total + " interactive elements" +
                        (total > lines.Count ? ", the first " + lines.Count + " listed (on screen first)" : "") + ":\n" + string.Join("\n", lines));
                }
                else
                {
                    int offset = (int)(Num(args, "offset") ?? 0), max = (int)Math.Max(500, Math.Min(Num(args, "max_chars") ?? 20000, 100000));
                    var r = await PageAsync(tab, new Dictionary<string, object> { ["op"] = "text", ["offset"] = offset, ["max"] = max }, siteOnly: false);
                    var text = (string)r["text"];
                    int total = Convert.ToInt32(r["total"]);
                    var more = offset + text.Length < total ? "\n[… " + (total - offset - text.Length) + " more characters: offset=" + (offset + text.Length) + "]" : "";
                    return Mcp.Text("Tab " + tab.Id + ": " + r["title"] + "\n" + r["url"] + "\n\n" + text + more);
                }
            case "screenshot":
                return await ShotAsync(tab, core);
            case "click":
            {
                MustAct(tab);
                var (x, y, note) = await PointAsync(tab, args);
                var known = AllTabIds();
                await MouseAsync(core, x, y, Flag(args, "right") ? "right" : "left", Flag(args, "double") ? 2 : 1);
                await SettleAsync(tab);
                return Mcp.Text("Clicked" + note + ".\n" + await StateOfAsync(tab) + NewTabs(known));
            }
            case "type":
            {
                MustAct(tab);
                var text = Str(args, "text") ?? "";
                bool clear = Flag(args, "clear");
                if (Num(args, "element") is { } el)
                {
                    var f = await PageAsync(tab, new Dictionary<string, object> { ["op"] = "focus", ["ref"] = (int)el, ["clear"] = clear });
                    if (f["focused"] is not true)
                    {
                        // Some fields take the focus only from a click
                        var (x, y, _) = await PointAsync(tab, args);
                        await MouseAsync(core, x, y, "left", 1);
                        if (clear) await KeyAsync(core, "ctrl+a");
                    }
                }
                else if (clear) await KeyAsync(core, "ctrl+a");
                if (text.Length > 0) await Cdp(core, "Input.insertText", new Dictionary<string, object> { ["text"] = text });
                else if (clear) await KeyAsync(core, "Delete");
                var known = AllTabIds();
                if (Flag(args, "submit")) await KeyAsync(core, "Enter");
                await SettleAsync(tab);
                return Mcp.Text("Typed " + text.Length + " characters" + (Flag(args, "submit") ? " and pressed Enter" : "") + ".\n" +
                    await StateOfAsync(tab) + NewTabs(known));
            }
            case "press_key":
            {
                MustAct(tab);
                var known = AllTabIds();
                await KeyAsync(core, Str(args, "key") ?? "");
                await SettleAsync(tab);
                return Mcp.Text("Pressed " + Str(args, "key") + ".\n" + await StateOfAsync(tab) + NewTabs(known));
            }
            case "scroll":
            {
                MustAct(tab);
                if (Num(args, "element") is { } el)
                    await PageAsync(tab, new Dictionary<string, object> { ["op"] = "point", ["ref"] = (int)el });
                else
                {
                    var size = await PageAsync(tab, new Dictionary<string, object> { ["op"] = "size" });
                    double w = Convert.ToDouble(size["w"]), h = Convert.ToDouble(size["h"]);
                    double x = w / 2, y = h / 2;
                    if (Num(args, "x") is { } sx && Num(args, "y") is { } sy) (x, y) = (sx * tab.ShotScale, sy * tab.ShotScale);
                    await Cdp(core, "Input.dispatchMouseEvent", new Dictionary<string, object>
                    {
                        ["type"] = "mouseWheel", ["x"] = x, ["y"] = y, ["deltaX"] = 0, ["deltaY"] = Num(args, "dy") ?? h * .8,
                    });
                }
                await Task.Delay(400);
                var s = await PageAsync(tab, new Dictionary<string, object> { ["op"] = "size" });
                return Mcp.Text("Scrolled: the page is at " + s["y"] + " of " + s["height"] + " px (screen " + s["h"] + " px high).");
            }
            case "run_js":
            {
                MustAct(tab);
                var reply = Parse(await Cdp(core, "Runtime.evaluate", new Dictionary<string, object>
                {
                    ["expression"] = Str(args, "code") ?? "",
                    ["awaitPromise"] = true,
                    ["returnByValue"] = true,
                    ["replMode"] = true,
                    ["userGesture"] = true,
                }));
                if (ErrorOf(reply) is { } error) return Mcp.Text(error, true);
                var result = reply.TryGetValue("result", out var res) ? res as Dictionary<string, object> : null;
                var value = result == null ? "undefined"
                    : result.TryGetValue("value", out var v) ? ProjectStore.Json.Serialize(v)
                    : result.TryGetValue("description", out var d) ? Convert.ToString(d) : Convert.ToString(result.TryGetValue("type", out var ty) ? ty : "undefined");
                if (value!.Length > 50000) value = value.Substring(0, 50000) + "… [cut, " + value.Length + " characters]";
                return Mcp.Text(value);
            }
        }
        return Mcp.Text("Unknown tool: " + name, true);
    }

    /// <summary>Puts the tab in front of its window, which comes up if minimized; its WebView, loaded and running.</summary>
    async Task<CoreWebView2> BringAsync(Tab tab)
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        if (tab != active) SelectTab(tab);
        for (int i = 0; i < 150 && (tab.Core == null || tab.Suspended || tab.Ctl?.IsVisible != true); i++) await Task.Delay(100);
        if (tab.Closed) throw new InvalidOperationException("The tab was closed.");
        return tab.Core ?? throw new InvalidOperationException("The tab's page did not load.");
    }

    static void MustAct(Tab tab)
    {
        if (SiteOrigin(tab) == null)
            throw new InvalidOperationException("Tab " + tab.Id + " shows a page of the browser itself (" + tab.Site +
                "): it can be read and seen, not acted on. Open a site with `open` or `navigate`.");
    }

    /// <summary>Waits for a navigation: its start (up to 3 s, the address or the history changing), then its end (up to 30 s).</summary>
    static async Task WaitLoadAsync(Tab tab, string? url, string? from = null)
    {
        for (int i = 0; i < 30 && !tab.Loading && !tab.Closed; i++)
        {
            if (from != null && tab.Core?.Source is { } s && s != from) break;
            await Task.Delay(100);
        }
        for (int i = 0; i < 300 && tab.Loading && !tab.Closed; i++) await Task.Delay(100);
        await Task.Delay(200); // the page's own scripts fill it in
    }

    /// <summary>After a click or a key: a moment for the page, and the load if one started.</summary>
    static async Task SettleAsync(Tab tab)
    {
        await Task.Delay(350);
        for (int i = 0; i < 150 && tab.Loading && !tab.Closed; i++) await Task.Delay(100);
        if (tab.Loading) return;
        await Task.Delay(150);
    }

    async Task<string> StateOfAsync(Tab tab)
    {
        var state = "Tab " + tab.Id + ": " + tab.Label + "\n" + tab.Site;
        if (tab.Loading) state += "\n(still loading)";
        if (tab.ShowingInternalPage && tab.FailedUrl != null) state += "\nThe site does not answer (LiteBro shows its own page about it).";
        else if (tab.LocalOnly && Uri.TryCreate(tab.Site, UriKind.Absolute, out var u) && !Home.Is(tab.Site) && NetGuard.Blocks(u))
            state += "\nLiteBro is in " + L.T("«только localhost»") + " mode for this tab: internet sites are blocked. " +
                "The user can switch it off on the start page (or in the project's profile settings).";
        await Task.Yield();
        return state;
    }

    HashSet<int> AllTabIds() => new(App.Current.Forms.SelectMany(f => f.tabs).Select(t => t.Id));

    string NewTabs(HashSet<int> known)
    {
        var added = App.Current.Forms.SelectMany(f => f.tabs).Where(t => !known.Contains(t.Id)).ToList();
        return added.Count == 0 ? "" : "\nNew tab" + (added.Count > 1 ? "s" : "") + " opened: " +
            string.Join(", ", added.Select(t => t.Id + " (" + t.Site + ")"));
    }

    /// <summary>Runs claude.js with an operation in the tab's page.</summary>
    static async Task<Dictionary<string, object>> PageAsync(Tab tab, Dictionary<string, object> op, bool siteOnly = true)
    {
        if (siteOnly) MustAct(tab);
        var core = tab.Core ?? throw new InvalidOperationException("The tab's page is not loaded.");
        var reply = Parse(await Cdp(core, "Runtime.evaluate", new Dictionary<string, object>
        {
            ["expression"] = L.Text("claude.js").Replace("__OP__", ProjectStore.Json.Serialize(op)),
            ["returnByValue"] = true,
        }));
        if (ErrorOf(reply) is { } error) throw new InvalidOperationException(error);
        var value = reply.TryGetValue("result", out var res) && res is Dictionary<string, object> r && r.TryGetValue("value", out var v) ? v as string : null;
        return ProjectStore.Json.Deserialize<Dictionary<string, object>>(value ?? "{}") ?? new();
    }

    static Task<string> Cdp(CoreWebView2 core, string method, Dictionary<string, object> args) =>
        core.CallDevToolsProtocolMethodAsync(method, ProjectStore.Json.Serialize(args));

    static Dictionary<string, object> Parse(string json) =>
        ProjectStore.Json.Deserialize<Dictionary<string, object>>(json) ?? new();

    static string? ErrorOf(Dictionary<string, object> reply)
    {
        if (!reply.TryGetValue("exceptionDetails", out var ex) || ex is not Dictionary<string, object> details) return null;
        var text = details.TryGetValue("exception", out var e) && e is Dictionary<string, object> err && err.TryGetValue("description", out var d)
            ? d as string : details.TryGetValue("text", out var t) ? t as string : null;
        return (text ?? "Error in the page.").Split('\n')[0];
    }

    /// <summary>Where to click, in the page's CSS pixels: an element's middle, or a point of the latest screenshot.</summary>
    static async Task<(double X, double Y, string Note)> PointAsync(Tab tab, Dictionary<string, object> args)
    {
        if (Num(args, "element") is { } el)
        {
            var p = await PageAsync(tab, new Dictionary<string, object> { ["op"] = "point", ["ref"] = (int)el });
            var covered = p.TryGetValue("covered", out var c) ? c as string : "";
            return (Convert.ToDouble(p["x"]), Convert.ToDouble(p["y"]), " element " + el +
                (string.IsNullOrEmpty(covered) ? "" : " (another element, " + covered + ", lies over it: that one got the click)"));
        }
        if (Num(args, "x") is { } x && Num(args, "y") is { } y)
            return (x * tab.ShotScale, y * tab.ShotScale, " at " + x + ", " + y);
        throw new ArgumentException("Give element, or x and y.");
    }

    static async Task MouseAsync(CoreWebView2 core, double x, double y, string button, int clicks)
    {
        await Cdp(core, "Input.dispatchMouseEvent", new Dictionary<string, object> { ["type"] = "mouseMoved", ["x"] = x, ["y"] = y });
        for (int i = 1; i <= clicks; i++)
            foreach (var type in new[] { "mousePressed", "mouseReleased" })
                await Cdp(core, "Input.dispatchMouseEvent", new Dictionary<string, object>
                {
                    ["type"] = type, ["x"] = x, ["y"] = y, ["button"] = button, ["buttons"] = type == "mousePressed" ? (button == "left" ? 1 : 2) : 0,
                    ["clickCount"] = i,
                });
    }

    /// <summary>A key with its modifiers (ctrl+shift+a, Enter, F5), down and up, as the keyboard sends it.</summary>
    static async Task KeyAsync(CoreWebView2 core, string combo)
    {
        var parts = combo.Split('+').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (combo.EndsWith("+") && combo.Length > 1) parts.Add("+");
        if (parts.Count == 0) throw new ArgumentException("No key.");
        int modifiers = 0;
        foreach (var m in parts.Take(parts.Count - 1))
            modifiers |= m.ToLowerInvariant() switch
            {
                "alt" => 1,
                "ctrl" or "control" => 2,
                "meta" or "win" or "cmd" => 4,
                "shift" => 8,
                _ => throw new ArgumentException("Unknown modifier: " + m),
            };
        var (key, code, vk, text) = KeyOf(parts[parts.Count - 1], (modifiers & 8) != 0);
        bool typing = text != null && (modifiers & 3) == 0;
        var down = new Dictionary<string, object>
        {
            ["type"] = typing ? "keyDown" : "rawKeyDown", ["key"] = key, ["code"] = code,
            ["windowsVirtualKeyCode"] = vk, ["nativeVirtualKeyCode"] = vk, ["modifiers"] = modifiers,
        };
        if (typing) down["text"] = down["unmodifiedText"] = text!;
        await Cdp(core, "Input.dispatchKeyEvent", down);
        await Cdp(core, "Input.dispatchKeyEvent", new Dictionary<string, object>
        {
            ["type"] = "keyUp", ["key"] = key, ["code"] = code, ["windowsVirtualKeyCode"] = vk, ["nativeVirtualKeyCode"] = vk, ["modifiers"] = modifiers,
        });
    }

    static (string Key, string Code, int Vk, string? Text) KeyOf(string name, bool shift)
    {
        switch (name.ToLowerInvariant())
        {
            case "enter": case "return": return ("Enter", "Enter", 13, "\r");
            case "tab": return ("Tab", "Tab", 9, null);
            case "escape": case "esc": return ("Escape", "Escape", 27, null);
            case "backspace": return ("Backspace", "Backspace", 8, null);
            case "delete": case "del": return ("Delete", "Delete", 46, null);
            case "space": return (" ", "Space", 32, " ");
            case "arrowleft": case "left": return ("ArrowLeft", "ArrowLeft", 37, null);
            case "arrowup": case "up": return ("ArrowUp", "ArrowUp", 38, null);
            case "arrowright": case "right": return ("ArrowRight", "ArrowRight", 39, null);
            case "arrowdown": case "down": return ("ArrowDown", "ArrowDown", 40, null);
            case "home": return ("Home", "Home", 36, null);
            case "end": return ("End", "End", 35, null);
            case "pageup": return ("PageUp", "PageUp", 33, null);
            case "pagedown": return ("PageDown", "PageDown", 34, null);
            case "insert": return ("Insert", "Insert", 45, null);
        }
        if (name.Length >= 2 && (name[0] == 'F' || name[0] == 'f') && int.TryParse(name.Substring(1), out var f) && f >= 1 && f <= 12)
            return ("F" + f, "F" + f, 111 + f, null);
        if (name.Length == 1)
        {
            char c = name[0];
            if (char.IsLetter(c) && c < 128)
            {
                var k = shift ? char.ToUpperInvariant(c).ToString() : char.ToLowerInvariant(c).ToString();
                return (k, "Key" + char.ToUpperInvariant(c), char.ToUpperInvariant(c), k);
            }
            if (char.IsDigit(c)) return (name, "Digit" + c, c, name);
            return (name, "", 0, name);
        }
        throw new ArgumentException("Unknown key: " + name);
    }

    /// <summary>The visible part of the page as a JPEG, at most 1280 px wide; its scale is kept for clicks at its points.</summary>
    async Task<Dictionary<string, object>> ShotAsync(Tab tab, CoreWebView2 core)
    {
        double cssWidth = 0;
        try
        {
            var size = await PageAsync(tab, new Dictionary<string, object> { ["op"] = "size" }, siteOnly: false);
            cssWidth = Convert.ToDouble(size["w"]);
        }
        catch (Exception) { } // a page that runs no scripts: the scale is guessed from the window's DPI
        var shot = Parse(await Cdp(core, "Page.captureScreenshot", new Dictionary<string, object> { ["format"] = "png" }));
        var png = Convert.FromBase64String((string)shot["data"]);
        using var source = new Bitmap(new MemoryStream(png));
        double k = Math.Min(1, 1280.0 / source.Width);
        int w = Math.Max(1, (int)Math.Round(source.Width * k)), h = Math.Max(1, (int)Math.Round(source.Height * k));
        using var small = new Bitmap(w, h);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, 0, 0, w, h);
        }
        tab.ShotScale = cssWidth > 0 ? cssWidth / w : 96.0 / DeviceDpi / k;
        using var jpeg = new MemoryStream();
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var quality = new EncoderParameters(1);
        quality.Param[0] = new EncoderParameter(Encoder.Quality, 80L);
        small.Save(jpeg, codec, quality);
        return new Dictionary<string, object>
        {
            ["content"] = new List<object>
            {
                new Dictionary<string, object> { ["type"] = "image", ["data"] = Convert.ToBase64String(jpeg.ToArray()), ["mimeType"] = "image/jpeg" },
                new Dictionary<string, object> { ["type"] = "text", ["text"] = await StateOfAsync(tab) + "\nScreenshot " + w + "×" + h + " px; click and scroll take x, y in these pixels." },
            },
            ["isError"] = false,
        };
    }
}
