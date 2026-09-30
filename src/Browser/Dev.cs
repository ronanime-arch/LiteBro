using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LiteBro;

/// <summary>
/// «Для разработчика»: buttons and tools the user has switched off (Disabled in settings.ini). The page itself,
/// and the buttons that lead to it, are not on the list: it can always be reached to switch things back on.
/// </summary>
static class Dev
{
    public const string Path = "/dev";
    public const string Url = "https://" + Home.Host + Path;

    /// <summary>What can be switched off. Toolbar buttons go alone (their keys still work); features take their keys and menus along.</summary>
    public static readonly string[] Ids =
    {
        // toolbar
        "back", "forward", "reload", "home", "star", "ram",
        // features
        "reset", "country", "emulation", "snapshot", "storage", "json", "mocks", "split", "perms",
        // start page
        "shell", "localonly", "netlog", "console",
    };

    /// <summary>A new install, or settings back to their defaults: the developer tools wait to be switched on.</summary>
    public const string DefaultOff = "reset emulation snapshot storage json mocks";

    static HashSet<string>? off;

    static HashSet<string> Off => off ??= Parse(App.Current.S.DevOff);

    /// <summary>The known ids in a Disabled line; the rest is dropped (a newer version's, or a typo).</summary>
    public static HashSet<string> Parse(string text) =>
        new(text.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.ToLowerInvariant()).Where(Ids.Contains));

    public static bool On(string id) => !Off.Contains(id);

    /// <summary>The ids switched off, for the pages.</summary>
    public static List<string> OffList() => Ids.Where(Off.Contains).ToList();

    /// <summary>Switches one thing on or off and remembers it; false for an id not on the list.</summary>
    public static bool Set(string id, bool on)
    {
        if (!Ids.Contains(id)) return false;
        if (on ? !Off.Remove(id) : !Off.Add(id)) return true;
        App.Current.S.SaveDevOff(string.Join(" ", OffList()));
        return true;
    }

    /// <summary>Reads the Disabled line again (after the settings were reset).</summary>
    public static void Reload() => off = null;

    /// <summary>Everything back on.</summary>
    public static void Reset()
    {
        Off.Clear();
        App.Current.S.SaveDevOff("");
    }

    public static bool Is(string? uri) => Home.Is(uri) && new Uri(uri!).AbsolutePath == Path;

    public static Stream Html() => L.Stream("dev.html");
}
