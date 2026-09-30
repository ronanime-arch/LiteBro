using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace LiteBro;

/// <summary>
/// The interface language. Texts are written in Russian; in English every Russian phrase of the program
/// (a string in the code, a text or a string of a page) is looked up in en.tsv, whole or piece by piece.
/// </summary>
static class L
{
    /// <summary>ru or en.</summary>
    public static string Code { get; private set; } = Resolve("auto");
    public static bool En => Code == "en";

    /// <summary>The Language line of settings.ini: auto (by the language of Windows), ru, en.</summary>
    public static void Set(string setting)
    {
        Code = Resolve(setting);
        auto = setting is not ("ru" or "en");
    }

    static bool auto = true;

    public static string Resolve(string setting) => setting switch
    {
        "ru" or "en" => setting,
        // Russian where Russian is read: the languages of the former USSR that Windows is set to
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "ru" or "uk" or "be" or "kk" or "ky" or "uz" or "tg" ? "ru" : "en",
    };

    /// <summary>
    /// The engine's own words (its menus, its pages) and the Accept-Language sites get: with auto the language of
    /// Windows itself (German menus on a German Windows, though this program's own words are English there).
    /// </summary>
    public static string EngineLanguage =>
        auto && CultureInfo.CurrentUICulture.Name.Length > 0 ? CultureInfo.CurrentUICulture.Name : En ? "en-US" : "ru-RU";

    static Dictionary<string, string>? english;
    static Regex? pieces;

    static Dictionary<string, string> English
    {
        get
        {
            if (english != null) return english;
            var d = new Dictionary<string, string>();
            using (var stream = typeof(L).Assembly.GetManifestResourceStream("en.tsv"))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    int tab = line.IndexOf('\t');
                    if (tab > 0 && !line.StartsWith("## ")) d[line.Substring(0, tab)] = line.Substring(tab + 1);
                }
            }
            // Longer phrases first, and none inside a word
            pieces = new Regex("(?<![А-Яа-яЁё])(?:" + string.Join("|", d.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape)) + ")(?![А-Яа-яЁё])");
            return english = d;
        }
    }

    static readonly Regex Russian = new("[А-Яа-яЁё«»]");

    /// <summary>The text in the interface language; «» become English quotes.</summary>
    public static string T(string text)
    {
        if (!En || !Russian.IsMatch(text)) return text;
        var d = English;
        if (d.TryGetValue(text, out var whole)) return whole;
        return pieces!.Replace(text, m => d[m.Value]).Replace('«', '“').Replace('»', '”');
    }

    static readonly Dictionary<string, byte[]> resources = new();

    /// <summary>A page or script of this program in the interface language.</summary>
    public static byte[] Bytes(string name, Assembly? from = null)
    {
        var key = Code + "|" + name;
        lock (resources)
        {
            if (resources.TryGetValue(key, out var cached)) return cached;
            using var stream = (from ?? typeof(L).Assembly).GetManifestResourceStream(name);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = T(reader.ReadToEnd());
            if (En) text = text.Replace("<html lang=\"ru\">", "<html lang=\"en\">");
            return resources[key] = Encoding.UTF8.GetBytes(text);
        }
    }

    public static string Text(string name) => Encoding.UTF8.GetString(Bytes(name));

    public static Stream Stream(string name) => new MemoryStream(Bytes(name), writable: false);
}
