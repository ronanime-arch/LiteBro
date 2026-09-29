using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace LiteBro;

/// <summary>
/// Google search as seen from another country: only the free gl (country) and hl (language) parameters of the
/// search address, no proxy and no request interception.
/// </summary>
static class SearchCountry
{
    public sealed class Country
    {
        /// <summary>Two letters, upper case, as the button shows it; gl is its lower case.</summary>
        public readonly string Code;
        public readonly string Name;
        public readonly string Hl;
        public string Gl => Code.ToLowerInvariant();

        public Country(string code, string name, string hl)
        {
            Code = code;
            Name = name;
            Hl = hl;
        }
    }

    public static readonly Country[] All =
    {
        new("RU", "Россия", "ru"),
        new("UA", "Украина", "uk"),
        new("KZ", "Казахстан", "ru"),
        new("BY", "Беларусь", "ru"),
        new("GE", "Грузия", "ka"),
        new("US", "США", "en"),
        new("GB", "Великобритания", "en"),
        new("CA", "Канада", "en"),
        new("DE", "Германия", "de"),
        new("FR", "Франция", "fr"),
        new("ES", "Испания", "es"),
        new("IT", "Италия", "it"),
        new("PL", "Польша", "pl"),
        new("LT", "Литва", "lt"),
        new("CZ", "Чехия", "cs"),
        new("NL", "Нидерланды", "nl"),
        new("SE", "Швеция", "sv"),
        new("TR", "Турция", "tr"),
        new("IL", "Израиль", "iw"),
        new("IN", "Индия", "en"),
        new("CN", "Китай", "zh-CN"),
        new("JP", "Япония", "ja"),
        new("KR", "Южная Корея", "ko"),
        new("BR", "Бразилия", "pt-BR"),
        new("MX", "Мексика", "es"),
        new("AU", "Австралия", "en"),
    };

    /// <summary>The country of a settings.ini code; null for an empty or unknown one.</summary>
    public static Country? Find(string? code) =>
        All.FirstOrDefault(c => string.Equals(c.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The country picked for searches; null to search as usual, or while switched off on the «Для разработчика» page.</summary>
    public static Country? Current => Dev.On("country") ? Find(App.Current.S.SearchCountry) : null;

    // google.com, www.google.de, google.co.uk, www.google.com.ua
    static readonly Regex GoogleHost = new(@"^(www\.)?google\.(com?\.)?[a-z]{2,3}$", RegexOptions.IgnoreCase);

    public static bool IsGoogle(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http") &&
        GoogleHost.IsMatch(u.Host);

    /// <summary>A page of Google search results, where the country button is shown.</summary>
    public static bool IsGoogleSearch(string url) =>
        IsGoogle(url) && new Uri(url).AbsolutePath == "/search";

    /// <summary>
    /// The address with its gl and hl replaced by the country's, or removed for null. The other parameters stay
    /// as they are, in their order and encoding, and so does the fragment.
    /// </summary>
    public static string WithCountry(string url, Country? country)
    {
        int hash = url.IndexOf('#');
        var fragment = hash < 0 ? "" : url.Substring(hash);
        var rest = hash < 0 ? url : url.Substring(0, hash);
        int q = rest.IndexOf('?');
        var path = q < 0 ? rest : rest.Substring(0, q);

        var parts = Params(url).Where(p => !IsCountryParam(p)).ToList();
        if (country != null)
        {
            parts.Add("gl=" + country.Gl);
            parts.Add("hl=" + country.Hl);
        }
        return path + (parts.Count > 0 ? "?" + string.Join("&", parts) : "") + fragment;
    }

    /// <summary>The address already carries the country's gl and hl, once each, wherever they stand.</summary>
    public static bool HasCountry(string url, Country country)
    {
        var own = Params(url).Where(IsCountryParam).Select(p => p.ToLowerInvariant()).OrderBy(p => p).ToArray();
        return own.SequenceEqual(new[] { "gl=" + country.Gl, "hl=" + country.Hl.ToLowerInvariant() });
    }

    static string[] Params(string url)
    {
        int hash = url.IndexOf('#');
        var rest = hash < 0 ? url : url.Substring(0, hash);
        int q = rest.IndexOf('?');
        return q < 0 ? new string[0] : rest.Substring(q + 1).Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries);
    }

    static bool IsCountryParam(string part)
    {
        int eq = part.IndexOf('=');
        var key = eq < 0 ? part : part.Substring(0, eq);
        return key == "gl" || key == "hl";
    }

    /// <summary>A search address from the address bar, with the picked country when it goes to Google.</summary>
    public static string SearchUrl(string text)
    {
        var url = App.Current.S.SearchUrl + Uri.EscapeDataString(text);
        return Current is { } c && IsGoogle(url) ? WithCountry(url, c) : url;
    }

    /// <summary>
    /// Where a navigation to Google search should go instead, so that a search typed on Google's own page keeps
    /// the country; null when it can go as it is.
    /// </summary>
    public static string? Redirect(string url)
    {
        if (Current is not { } c || !IsGoogleSearch(url) || HasCountry(url, c)) return null;
        return WithCountry(url, c);
    }
}
