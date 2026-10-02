using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace LiteBro;

/// <summary>
/// The mini-games of the page about a site that does not answer (games.js): the game picked last and the records,
/// kept in games.txt next to settings.ini. The page sends them in web messages (BrowserForm.OnWebMessage).
/// </summary>
static class Games
{
    static readonly string[] Names = { "runner", "snake", "flappy" };
    const int MaxScore = 10_000_000;
    static string FilePath => Path.Combine(Settings.Dir, "games.txt");
    static string pick = "runner";
    static Dictionary<string, int>? best;

    static Dictionary<string, int> Best => best ?? Load();

    static Dictionary<string, int> Load()
    {
        best = new();
        try
        {
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var pair = line.Split(new[] { '=' }, 2);
                if (pair.Length != 2) continue;
                string key = pair[0].Trim(), value = pair[1].Trim();
                if (key == "pick" && Names.Contains(value)) pick = value;
                else if (Names.Contains(key) && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n <= MaxScore)
                    best[key] = n;
            }
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { } // none yet
        return best;
    }

    static void Save()
    {
        var lines = new[] { "pick=" + pick }.Concat(Best.Select(b => b.Key + "=" + b.Value.ToString(CultureInfo.InvariantCulture)));
        try { File.WriteAllLines(FilePath, lines); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
    }

    /// <summary>The games' block of the page: its style, what was saved, and the script.</summary>
    public static string Block()
    {
        var b = Best;
        var state = ProjectStore.Json.Serialize(new Dictionary<string, object>
        {
            ["pick"] = pick,
            ["best"] = Names.ToDictionary(n => n, n => (object)(b.TryGetValue(n, out var v) ? v : 0)),
        });
        return "<style>" +
            "#gamePick{position:fixed;top:12px;right:16px;font:13px 'Segoe UI',sans-serif;padding:4px 8px;border-radius:6px;" +
            "border:1px solid rgba(127,127,127,.45);background:Canvas;color:CanvasText}" +
            "#games{margin-top:28px;width:min(712px,calc(100vw - 48px))}" +
            "#games canvas{display:block;width:100%;aspect-ratio:640/200;border-radius:10px;background:rgba(127,127,127,.08);" +
            "outline:none;touch-action:none;cursor:pointer}" +
            "#games p{margin:8px 0 0;font-size:13px}" +
            "</style><div id=games></div><script>window.__litebroGames=" + state + ";</script><script>" + L.Text("games.js") + "</script>";
    }

    /// <summary>A message of the page: a game picked, or a record (only ever higher).</summary>
    public static void Note(string json)
    {
        Dictionary<string, object>? m;
        try { m = ProjectStore.Json.Deserialize<Dictionary<string, object>>(json); }
        catch (Exception) { return; }
        if (m == null || !(m.TryGetValue("type", out var type) && type is "game")) return;
        var b = Best;
        if (m.TryGetValue("pick", out var p) && p is string name && Names.Contains(name)) pick = name;
        else if (m.TryGetValue("game", out var g) && g is string game && Names.Contains(game)
            && m.TryGetValue("best", out var s) && s is int score && score > 0 && score <= MaxScore
            && score > (b.TryGetValue(game, out var old) ? old : 0))
            b[game] = score;
        else return;
        Save();
    }
}
