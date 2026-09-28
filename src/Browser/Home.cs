using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LiteBro;

/// <summary>
/// The start page with the project tiles. It is served from this program under an address no site can
/// have, and it talks to the browser through web messages, which only it may send (see BrowserForm).
/// </summary>
static class Home
{
    public const string Host = "start.litebro";
    public const string Url = "https://" + Host + "/";
    // Nothing but the page's own script and pictures, and no site may frame it to trick a click on a tile
    public const string Headers = "Content-Type: text/html; charset=utf-8\r\nContent-Security-Policy: default-src 'none'; " +
        "script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'";
    static byte[]? html;

    public static bool Is(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && u.Host.Equals(Host, StringComparison.OrdinalIgnoreCase);

    public static Stream Page()
    {
        if (html == null)
        {
            using var stream = typeof(Home).Assembly.GetManifestResourceStream("home.html");
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            html = copy.ToArray();
        }
        return new MemoryStream(html, writable: false);
    }

    /// <summary>What the page shows: the tiles and which of their programs run.</summary>
    public static string State(IEnumerable<string> running) => ProjectStore.Json.Serialize(new Dictionary<string, object>
    {
        ["type"] = "projects",
        ["projects"] = ProjectStore.All,
        ["running"] = running.ToArray(),
    });
}

/// <summary>Tile pictures: copies of the images the user picked, served to the start page under /icon/.</summary>
static class Icons
{
    public static string Dir => Path.Combine(Settings.Dir, "icons");
    const long MaxBytes = 2 << 20;
    static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".bmp"] = "image/bmp", [".ico"] = "image/x-icon", [".svg"] = "image/svg+xml",
    };
    public const string Filter = "Картинки|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.ico;*.svg";
    // An svg opened by itself would run its scripts with the start page's rights: none, then
    public const string Headers = "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; sandbox";

    public static string? ContentType(string file) => Types.TryGetValue(Path.GetExtension(file), out var t) ? t : null;

    /// <summary>A picture of the icons folder by its bare name; null for anything else.</summary>
    public static byte[]? Read(string name)
    {
        if (name.Length == 0 || name != Path.GetFileName(name) || ContentType(name) == null) return null;
        try { return File.ReadAllBytes(Path.Combine(Dir, name)); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return null; }
    }

    /// <summary>Checks a picked image; null when it will do, else why not.</summary>
    public static string? Problem(string path)
    {
        if (ContentType(path) == null) return "Нужна картинка: png, jpg, gif, webp, bmp, ico или svg.";
        try { return new FileInfo(path).Length > MaxBytes ? "Картинка больше 2 МБ." : null; }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return "Файл не читается."; }
    }

    /// <summary>The picture as a data: address, for the dialog to show before it is saved.</summary>
    public static string Preview(string path) =>
        "data:" + ContentType(path) + ";base64," + Convert.ToBase64String(File.ReadAllBytes(path));

    /// <summary>Copies the image in under a new name, so the page never shows an old one from its cache.</summary>
    public static string Import(string projectId, string path)
    {
        Directory.CreateDirectory(Dir);
        var name = projectId + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + Path.GetExtension(path).ToLowerInvariant();
        File.Copy(path, Path.Combine(Dir, name), true);
        return name;
    }

    /// <summary>The picture type by its first bytes: servers often say the wrong one for icons.</summary>
    public static string? Sniff(byte[] b)
    {
        bool Starts(params byte[] signature) => b.Length >= signature.Length && signature.Select((x, i) => b[i] == x).All(same => same);
        if (Starts(0x89, 0x50, 0x4E, 0x47)) return ".png";
        if (Starts(0xFF, 0xD8, 0xFF)) return ".jpg";
        if (Starts(0x47, 0x49, 0x46, 0x38)) return ".gif";
        if (Starts(0x00, 0x00, 0x01, 0x00)) return ".ico";
        if (Starts(0x42, 0x4D)) return ".bmp";
        if (Starts(0x52, 0x49, 0x46, 0x46) && b.Length >= 12 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return ".webp";
        var head = System.Text.Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 2048)).TrimStart('﻿', ' ', '\t', '\r', '\n');
        return head.StartsWith("<svg") || (head.StartsWith("<?xml") && head.Contains("<svg")) ? ".svg" : null;
    }

    /// <summary>Saves a downloaded picture under a new name; its type by its content.</summary>
    public static string Save(string projectId, byte[] bytes)
    {
        Directory.CreateDirectory(Dir);
        var name = projectId + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + Sniff(bytes);
        File.WriteAllBytes(Path.Combine(Dir, name), bytes);
        return name;
    }

    public static void Delete(string name)
    {
        if (name.Length == 0 || name != Path.GetFileName(name)) return;
        try { File.Delete(Path.Combine(Dir, name)); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
    }
}

/// <summary>The pages shown while a project's program starts, or after it failed to.</summary>
static class Pages
{
    public static string Starting(Project p, string address, Launcher launcher) => Page("Запускаю " + p.Name + "…", spinner: true,
        "<p>Адрес <code>" + H(address) + "</code> пока не отвечает. LiteBro запустил программу проекта и ждёт.</p>" +
        "<p class=muted>Программа: <code>" + H(p.Exe + " " + p.Args) + "</code><br>" +
        "Папка: <code>" + H(launcher.WorkDir) + "</code><br>" +
        "Лог: <code>" + H(launcher.LogPath) + "</code> · <a href=\"" + H(ProgramLog.Url(p)) + "\">вывод программы</a></p>");

    public static string Failed(Project p, string error, string logTail) => Page(p.Name + " не запустился", spinner: false,
        "<p>" + H(error) + "</p>" +
        (logTail.Length > 0 ? "<pre>" + H(logTail) + "</pre><p><a href=\"" + H(ProgramLog.Url(p)) + "\">Весь вывод программы</a></p>" : "") +
        "<p class=muted>F5 — попробовать ещё раз. Параметры запуска меняются на стартовой странице: правый клик по плитке.</p>");

    static string H(string s) => WebUtility.HtmlEncode(s);

    static string Page(string title, bool spinner, string body) =>
        "<!doctype html><html><head><meta charset=utf-8><title>" + H(title) + "</title><style>" +
        ":root{color-scheme:light dark}" +
        "body{margin:0;min-height:100vh;display:grid;place-items:center;font:15px/1.6 'Segoe UI',sans-serif;background:Canvas;color:CanvasText}" +
        "main{max-width:760px;padding:24px}" +
        "h1{font-size:20px;font-weight:600;margin:0 0 12px;display:flex;align-items:center;gap:12px}" +
        "code,pre{font:13px Consolas,monospace}" +
        "pre{white-space:pre-wrap;background:rgba(127,127,127,.12);padding:12px;border-radius:6px;max-height:50vh;overflow:auto}" +
        ".muted{opacity:.7}" +
        ".spin{width:18px;height:18px;border:3px solid rgba(127,127,127,.3);border-top-color:#4d6bfe;border-radius:50%;animation:r 1s linear infinite}" +
        "@keyframes r{to{transform:rotate(360deg)}}" +
        "</style></head><body><main><h1>" + (spinner ? "<span class=spin></span>" : "") + H(title) + "</h1>" +
        body + "</main></body></html>";
}

/// <summary>The «Обзор…» buttons of the project dialog.</summary>
static class Pickers
{
    public static string? Picture(IWin32Window owner)
    {
        using var dialog = new OpenFileDialog { Title = "Картинка для плитки", Filter = Icons.Filter };
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileName : null;
    }

    public static string? Executable(IWin32Window owner, string current)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Программа проекта",
            Filter = "Программы (*.exe; *.cmd; *.bat)|*.exe;*.cmd;*.bat|Все файлы|*.*",
        };
        var path = Environment.ExpandEnvironmentVariables(current.Trim().Trim('"'));
        try
        {
            if (File.Exists(path)) dialog.InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
        }
        catch (ArgumentException) { }
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileName : null;
    }

    /// <summary>The modern folder picker (FolderBrowserDialog in .NET Framework is the old tree).</summary>
    public static string? Folder(IWin32Window owner, string current)
    {
        var dialog = (IFileDialog)new FileOpenDialog();
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | PickFolders | ForceFileSystem);
            dialog.SetTitle("Папка, в которой запускать программу");
            var path = Environment.ExpandEnvironmentVariables(current.Trim().Trim('"'));
            if (path.Length > 0 && Directory.Exists(path)
                && SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItem).GUID, out var folder) == 0)
                dialog.SetFolder(folder);
            if (dialog.Show(owner.Handle) != 0) return null; // cancelled
            dialog.GetResult(out var item);
            item.GetDisplayName(FileSystemPath, out var result);
            return result;
        }
        finally { Marshal.ReleaseComObject(dialog); }
    }

    const uint PickFolders = 0x20, ForceFileSystem = 0x40, FileSystemPath = 0x80058000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    class FileOpenDialog { }

    // Methods in vtable order, up to the last one used
    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint count, IntPtr specs);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind, [MarshalAs(UnmanagedType.LPWStr)] out string name);
    }
}
