using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LiteBro;

/// <summary>A stub response: requests to its address get it from the browser itself and never reach the server.</summary>
sealed class Mock
{
    public string Id { get; set; } = "";
    public bool On { get; set; } = true;
    /// <summary>GET, POST...; empty for any.</summary>
    public string Method { get; set; } = "";
    /// <summary>The address; * stands for any characters. Without a query it matches the address with any query.</summary>
    public string Url { get; set; } = "";
    public int Status { get; set; } = 200;
    public string Type { get; set; } = "application/json; charset=utf-8";
    public string Body { get; set; } = "";

    Regex? regex;
    string? regexOf;

    public bool Matches(string method, Uri url)
    {
        if (!On || (Method.Length > 0 && !Method.Equals(method, StringComparison.OrdinalIgnoreCase))) return false;
        return MatchesUrl(url);
    }

    public bool MatchesUrl(Uri url)
    {
        if (regexOf != Url)
        {
            regex = new Regex("^" + Regex.Escape(Url).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            regexOf = Url;
        }
        var address = url.AbsoluteUri;
        int hash = address.IndexOf('#');
        if (hash >= 0) address = address.Substring(0, hash);
        if (!Url.Contains("?"))
        {
            int query = address.IndexOf('?');
            if (query >= 0) address = address.Substring(0, query);
        }
        return regex!.IsMatch(address);
    }

    /// <summary>The engine's request filters that let this mock see its requests (its ? is the engine's any-one-character).</summary>
    public IEnumerable<string> Filters()
    {
        yield return Url;
        if (!Url.Contains("?")) yield return Url + "?*";
    }
}

/// <summary>mocks.json next to settings.ini: the stubs made on the /net page.</summary>
static class MockStore
{
    const int MaxMocks = 200, MaxBody = 5 << 20;
    static string FilePath => Path.Combine(Settings.Dir, "mocks.json");
    static List<Mock>? list;

    public static List<Mock> All => list ??= Load();

    static List<Mock> Load()
    {
        if (!File.Exists(FilePath)) return new();
        try { return ProjectStore.Json.Deserialize<List<Mock>>(File.ReadAllText(FilePath, Encoding.UTF8)) ?? new(); }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
        {
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch (IOException) { }
            return new();
        }
        catch (IOException) { return new(); }
    }

    public static Mock? Find(string? id) => id == null ? null : All.FirstOrDefault(m => m.Id == id);

    /// <summary>The first mock on for this request.</summary>
    public static Mock? For(string method, Uri url) => All.FirstOrDefault(m => m.Matches(method, url));

    /// <summary>A CORS preflight for an address some mock answers: the browser answers it too.</summary>
    public static bool Covers(Uri url) => All.Any(m => m.On && m.MatchesUrl(url));

    public static HashSet<string> Filters() => new(All.Where(m => m.On).SelectMany(m => m.Filters()));

    /// <summary>Why the mock cannot be saved, or null.</summary>
    public static string? Problem(Mock m)
    {
        if (!(m.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || m.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            || m.Url.Length > 2000 || m.Url.Any(char.IsWhiteSpace))
            return L.T("Адрес должен начинаться с http:// или https:// и не содержать пробелов.");
        if (m.Status < 100 || m.Status > 599) return L.T("Код ответа — число от 100 до 599.");
        if (m.Body.Length > MaxBody) return L.T("Ответ длиннее 5 МБ.");
        if (m.Method.Length > 16 || m.Method.Any(c => !char.IsLetter(c))) return L.T("Метод — слово вроде GET или POST.");
        if (m.Type.IndexOfAny(new[] { '\r', '\n' }) >= 0 || m.Type.Length > 200) return L.T("Тип содержимого — одна строка.");
        if (m.Id.Length == 0 && All.Count >= MaxMocks) return L.T("Заглушек уже ") + MaxMocks + ".";
        return null;
    }

    /// <summary>Replaces the mock with the same Id, or adds it first with a new Id: the newest wins over older ones.</summary>
    public static void Save(Mock m)
    {
        m.Method = m.Method.Trim().ToUpperInvariant();
        int index = m.Id.Length > 0 ? All.FindIndex(x => x.Id == m.Id) : -1;
        if (index >= 0) All[index] = m;
        else
        {
            m.Id = Guid.NewGuid().ToString("N");
            All.Insert(0, m);
        }
        Write();
    }

    public static void SetOn(string id, bool on)
    {
        if (Find(id) is { } m && m.On != on)
        {
            m.On = on;
            Write();
        }
    }

    public static void Delete(string id)
    {
        if (All.RemoveAll(m => m.Id == id) > 0) Write();
    }

    static void Write()
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, ProjectStore.Json.Serialize(All), new UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
            else File.Move(temp, FilePath);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
    }

    /// <summary>The list for the /net page, without the bodies.</summary>
    public static List<Dictionary<string, object>> Summary() => All.Select(m => new Dictionary<string, object>
    {
        ["id"] = m.Id,
        ["on"] = m.On,
        ["method"] = m.Method,
        ["url"] = m.Url,
        ["status"] = m.Status,
        ["type"] = m.Type,
        ["size"] = Encoding.UTF8.GetByteCount(m.Body),
    }).ToList();

    public static Dictionary<string, object> Full(Mock m) => new()
    {
        ["id"] = m.Id,
        ["on"] = m.On,
        ["method"] = m.Method,
        ["url"] = m.Url,
        ["status"] = m.Status,
        ["type"] = m.Type,
        ["body"] = m.Body,
    };

    static readonly HttpClient Http = new(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>
    /// A new mock from a journal entry. For a GET the server is asked once more for the body (without the page's cookies),
    /// unless «только localhost» would refuse the address: then, as for other methods, the body is left to be written.
    /// </summary>
    public static async Task<Mock> FromEntryAsync(NetLog.Entry e)
    {
        var m = new Mock { Method = e.Method, Url = e.Url, Type = "application/json; charset=utf-8" };
        if (!e.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) || !Uri.TryCreate(e.Url, UriKind.Absolute, out var url)
            || NetGuard.ShouldBlock(url))
            return m;
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            m.Status = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } type) m.Type = type.ToString();
            if (response.Content.Headers.ContentLength is > MaxBody) return m;
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length <= MaxBody && IsText(m.Type)) m.Body = Encoding.UTF8.GetString(bytes);
        }
        catch (Exception) { } // no answer: the body is written by hand
        return m;
    }

    static bool IsText(string type) =>
        type.StartsWith("text/") || type.Contains("json") || type.Contains("xml") || type.Contains("javascript") || type.Contains("x-www-form-urlencoded");
}
