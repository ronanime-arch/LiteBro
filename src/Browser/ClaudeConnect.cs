using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LiteBro;

/// <summary>
/// «Подключить к Claude» on /dev: adds LiteBro's MCP server to Claude Desktop (claude_desktop_config.json, other entries
/// kept, a backup beside it) and to Claude Code (claude mcp add --scope user), so nothing has to be pasted by hand.
/// </summary>
static class ClaudeConnect
{
    const string Name = "litebro";
    const string FileName = "claude_desktop_config.json";
    static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string State { get; private set; } = "";
    public static bool Busy { get; private set; }

    /// <summary>The folders Claude Desktop keeps its configuration in: the classic one and those of the Store version.</summary>
    static List<string> DesktopDirs()
    {
        var dirs = new List<string>();
        void Add(string dir) { if (Directory.Exists(dir) && !dirs.Contains(dir, StringComparer.OrdinalIgnoreCase)) dirs.Add(dir); }
        try
        {
            var data = Path.Combine(Local, "Claude-Data");
            if (Directory.Exists(data)) foreach (var d in Directory.GetDirectories(data, "claude_*")) Add(d);
            var packages = Path.Combine(Local, "Packages");
            if (Directory.Exists(packages))
                foreach (var d in Directory.GetDirectories(packages, "Claude_*"))
                {
                    var roaming = Path.Combine(d, "LocalCache", "Roaming", "Claude");
                    if (File.Exists(Path.Combine(roaming, FileName))) Add(roaming);
                }
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        // The classic folder when it holds the file, or nothing else was found (the Store version leaves only a marker there)
        var classic = Path.Combine(Roaming, "Claude");
        if (File.Exists(Path.Combine(classic, FileName)) || dirs.Count == 0) Add(classic);
        return dirs;
    }

    /// <summary>Adds LiteBro to one claude_desktop_config.json, keeping everything else in it.</summary>
    static void AddToDesktop(string dir)
    {
        var file = Path.Combine(dir, FileName);
        var config = new Dictionary<string, object>();
        if (File.Exists(file))
        {
            var text = File.ReadAllText(file).Trim();
            if (text.Length > 0) config = ProjectStore.Json.Deserialize<Dictionary<string, object>>(text) ?? new();
            var backup = file + ".bak-litebro";
            if (!File.Exists(backup)) File.Copy(file, backup);
        }
        var servers = config.TryGetValue("mcpServers", out var s) && s is Dictionary<string, object> d ? d : new Dictionary<string, object>();
        servers[Name] = new Dictionary<string, object> { ["command"] = Application.ExecutablePath, ["args"] = new[] { "mcp" } };
        config["mcpServers"] = servers;
        File.WriteAllText(file, ProjectStore.Json.Serialize(config), new UTF8Encoding(false));
    }

    /// <summary>claude mcp add for every folder of the user: true when Claude Code has LiteBro now, null without Claude Code.</summary>
    static bool? AddToCode()
    {
        // An entry already there may point at an older place (LiteBrowser): it is replaced
        var (code, output) = Claude("mcp add --scope user " + Name + " -- \"" + Application.ExecutablePath + "\" mcp");
        if (code == 0) return true;
        if (code == 9009 || code == -2) return null; // cmd's "is not recognized as a command": no Claude Code
        if (output.IndexOf("already exists", StringComparison.OrdinalIgnoreCase) < 0) return false;
        Claude("mcp remove --scope user " + Name);
        return Claude("mcp add --scope user " + Name + " -- \"" + Application.ExecutablePath + "\" mcp").Code == 0;
    }

    /// <summary>Runs the claude command hidden: its exit code (-1 timed out, -2 not started) and what it printed.</summary>
    static (int Code, string Output) Claude(string arguments)
    {
        var info = new ProcessStartInfo("cmd.exe", "/d /c claude " + arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        try
        {
            using var p = Process.Start(info)!;
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60000))
            {
                try { p.Kill(); } catch (Exception) { }
                return (-1, "");
            }
            return (p.ExitCode, output.Result + error.Result);
        }
        catch (Exception) { return (-2, ""); }
    }

    public static async Task ConnectAsync(Action changed)
    {
        if (Busy) return;
        Busy = true;
        State = L.T("Подключаю…");
        changed();
        var lines = new List<string>();
        await Task.Run(() =>
        {
            int desktop = 0;
            foreach (var dir in DesktopDirs())
            {
                try { AddToDesktop(dir); desktop++; }
                catch (Exception e) { lines.Add(L.T("Claude Desktop: не удалось изменить файл ") + Path.Combine(dir, FileName) + ": " + e.Message); }
            }
            if (desktop > 0) lines.Add(L.T("Claude Desktop: подключено. Закройте Claude Desktop полностью (значок в трее, Выход) и откройте снова."));
            else if (lines.Count == 0) lines.Add(L.T("Claude Desktop не найден."));
            lines.Add(AddToCode() switch
            {
                true => L.T("Claude Code: подключено для всех папок. Начните новую сессию."),
                false => L.T("Claude Code: команда не сработала, выполните её вручную из поля ниже."),
                null => L.T("Claude Code не найден."),
            });
        });
        State = string.Join("\n", lines);
        Busy = false;
        changed();
    }
}
