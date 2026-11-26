using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace LiteBro;

/// <summary>
/// The user's scripts: *.js in the scripts folder beside settings.ini, run on every load of the sites they match
/// (Settings.UserScripts, off by default). A header like a userscript's: // @match https://example.com/* (several,
/// none = every site), // @run-at document-start|document-end|document-idle, // @all-frames. One loader built from all
/// of them goes on each WebView (AddScriptToExecuteOnDocumentCreated); a change to the folder or the switch puts the new
/// one on: it runs from the next page load. Never on the browser's own pages.
/// </summary>
static class UserScripts
{
    public static string Dir => Path.Combine(Settings.Dir, "scripts");
    static string? loader;
    static FileSystemWatcher? watcher;
    static Timer? settle;
    /// <summary>Raised on the thread the watcher calls from: the scripts changed.</summary>
    public static event Action? Changed;

    /// <summary>The script to put on every new document; null while switched off or with no scripts.</summary>
    public static string? Loader
    {
        get
        {
            if (!App.Current.S.UserScripts) return null;
            Watch();
            loader ??= Build();
            return loader.Length == 0 ? null : loader;
        }
    }

    static void Watch()
    {
        if (watcher != null) return;
        try
        {
            Directory.CreateDirectory(Dir);
            watcher = new FileSystemWatcher(Dir, "*.js") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            FileSystemEventHandler on = (_, _) => Soon();
            watcher.Changed += on;
            watcher.Created += on;
            watcher.Deleted += on;
            watcher.Renamed += (_, _) => Soon();
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception) { } // a folder that cannot be watched: changes count from the next start
    }

    // An editor writes a file in several steps: once it has been quiet for a moment
    static void Soon()
    {
        settle ??= new Timer(_ => Invalidate());
        settle.Change(400, Timeout.Infinite);
    }

    public static void Invalidate()
    {
        loader = null;
        Changed?.Invoke();
    }

    sealed class Script
    {
        public string Name = "", Code = "", RunAt = "document-end";
        public List<string> Match = new();
        public bool AllFrames;
    }

    static readonly Regex Header = new(@"^\s*//\s*@([\w-]+)\s+(.*?)\s*$", RegexOptions.Multiline);

    static Script Parse(string name, string code)
    {
        var s = new Script { Name = name, Code = code };
        foreach (Match m in Header.Matches(code))
            switch (m.Groups[1].Value.ToLowerInvariant())
            {
                case "match": case "include": s.Match.Add(m.Groups[2].Value); break;
                case "run-at": s.RunAt = m.Groups[2].Value.ToLowerInvariant(); break;
                case "all-frames": s.AllFrames = m.Groups[2].Value.Trim() != "false"; break;
            }
        // "// @all-frames" alone: the regex wants a value, so look for the bare word too
        if (Regex.IsMatch(code, @"^\s*//\s*@all-frames\s*$", RegexOptions.Multiline)) s.AllFrames = true;
        return s;
    }

    /// <summary>A @match pattern as a regex over the whole address: * is anything.</summary>
    static string PatternOf(string glob) => "^" + string.Join(".*", glob.Split('*').Select(Regex.Escape)) + "$";

    static string Build()
    {
        string[] files;
        try { files = Directory.GetFiles(Dir, "*.js").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch (Exception) { return ""; }
        var b = new StringBuilder();
        foreach (var file in files)
        {
            string code;
            try { code = File.ReadAllText(file, Encoding.UTF8); }
            catch (Exception) { continue; }
            var s = Parse(Path.GetFileName(file), code);
            var match = ProjectStore.Json.Serialize(s.Match.Select(PatternOf).ToList());
            var name = ProjectStore.Json.Serialize(s.Name);
            // Each script in a function of its own: its variables and errors stay its own
            b.Append("run(").Append(name).Append(',').Append(match).Append(',').Append(ProjectStore.Json.Serialize(s.RunAt))
                .Append(',').Append(s.AllFrames ? "true" : "false").Append(",function(){\n").Append(code).Append("\n});\n");
        }
        if (b.Length == 0) return "";
        return "(function(){\n" +
            "if (!/^https?:$/.test(location.protocol) || location.hostname === 'start.litebro') return;\n" +
            "const href = location.href;\n" +
            "function run(name, match, at, frames, body) {\n" +
            "  if (window.top !== window && !frames) return;\n" +
            "  if (match.length && !match.some(p => new RegExp(p).test(href))) return;\n" +
            "  const go = () => { try { body(); } catch (e) { console.error('[LiteBro script ' + name + ']', e); } };\n" +
            "  if (at === 'document-start') go();\n" +
            "  else if (at === 'document-idle') (document.readyState === 'complete' ? go() : addEventListener('load', go, { once: true }));\n" +
            "  else (document.readyState === 'loading' ? document.addEventListener('DOMContentLoaded', go, { once: true }) : go());\n" +
            "}\n" + b + "})();";
    }

    /// <summary>Puts the current loader on a WebView, in place of the one it had; gives the id to take it off by.</summary>
    public static async Task<string?> ApplyAsync(CoreWebView2 core, string? old)
    {
        if (old != null)
        {
            try { core.RemoveScriptToExecuteOnDocumentCreated(old); }
            catch (Exception) { }
        }
        if (Loader is not { } script) return null;
        try { return await core.AddScriptToExecuteOnDocumentCreatedAsync(script); }
        catch (Exception) { return null; }
    }

    // --- Claude's tool: user_scripts ---

    static bool ValidName(string? name) =>
        name != null && Regex.IsMatch(name, @"^[\w.\- ]{1,80}\.js$") && !name.Contains("..");

    public static Dictionary<string, object> Tool(Dictionary<string, object> args)
    {
        if (!App.Current.S.UserScripts)
            return Mcp.Text("User scripts are switched off in LiteBro. Ask the user to switch on " + L.T("«Свои скрипты»") +
                " on the page https://start.litebro/dev.", true);
        var action = args.TryGetValue("action", out var a) ? a as string : null;
        var name = args.TryGetValue("name", out var n) ? n as string : null;
        Directory.CreateDirectory(Dir);
        switch (action)
        {
            case "list":
                var files = Directory.GetFiles(Dir, "*.js").Select(f => Parse(Path.GetFileName(f), SafeRead(f))).ToList();
                return Mcp.Text(files.Count == 0 ? "No scripts yet. Folder: " + Dir
                    : "Folder: " + Dir + "\n" + string.Join("\n", files.Select(s => s.Name + " — " +
                        (s.Match.Count == 0 ? "every site" : string.Join(", ", s.Match)) + ", " + s.RunAt)));
            case "read" when ValidName(name):
                var path = Path.Combine(Dir, name!);
                return File.Exists(path) ? Mcp.Text(SafeRead(path)) : Mcp.Text("No script " + name, true);
            case "save" when ValidName(name):
                var code = args.TryGetValue("code", out var c) ? c as string : null;
                if (string.IsNullOrWhiteSpace(code)) return Mcp.Text("save needs code.", true);
                File.WriteAllText(Path.Combine(Dir, name!), code, new UTF8Encoding(false));
                Invalidate();
                return Mcp.Text("Saved " + name + ". It runs from the next load of the pages it matches (reload with navigate url=reload).");
            case "delete" when ValidName(name):
                var target = Path.Combine(Dir, name!);
                if (!File.Exists(target)) return Mcp.Text("No script " + name, true);
                File.Delete(target);
                Invalidate();
                return Mcp.Text("Deleted " + name + ".");
            case "read" or "save" or "delete":
                return Mcp.Text("name must be a file name ending in .js (letters, digits, dot, dash, space).", true);
        }
        return Mcp.Text("action: list, read, save or delete.", true);
    }

    static string SafeRead(string path)
    {
        try { return File.ReadAllText(path, Encoding.UTF8); }
        catch (Exception e) { return "(" + e.Message + ")"; }
    }
}
