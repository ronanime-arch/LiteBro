using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace LiteBrowser;

/// <summary>
/// The site's own icon for a project tile: the biggest one its page declares (an apple-touch-icon is made
/// for tiles like these, an svg scales to any size), else its favicon. Fetched when the project is saved,
/// and taken from the page when LiteBrowser opens it, which works behind a login too.
/// </summary>
static class Favicons
{
    static readonly HttpClient Http = new(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) };
    static readonly Regex LinkTag = new(@"<link\b[^>]*>", RegexOptions.IgnoreCase);
    static readonly Regex Attribute = new(@"([\w-]+)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+))");
    static readonly Regex Size = new(@"(\d+)\s*x\s*\d+", RegexOptions.IgnoreCase);
    const int MaxBytes = 2 << 20;

    /// <summary>An icon a page declares: where, what for, and how big it says it is.</summary>
    sealed class Candidate
    {
        public string Href = "", Rel = "", Sizes = "";
    }

    /// <summary>Bigger first: a tile is 168 pixels wide, a favicon 16.</summary>
    static int Score(Candidate c)
    {
        if (c.Sizes.Trim().Equals("any", StringComparison.OrdinalIgnoreCase)
            || c.Href.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return 1000;
        var declared = Size.Matches(c.Sizes).Cast<Match>().Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(0).Max();
        if (declared > 0) return declared;
        return c.Rel.Contains("apple-touch-icon") ? 180 : 32;
    }

    /// <summary>The icons declared in a page's HTML, with their addresses made absolute.</summary>
    static IEnumerable<Candidate> Parse(string html, Uri page)
    {
        foreach (Match tag in LinkTag.Matches(html))
        {
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match a in Attribute.Matches(tag.Value))
            {
                var value = a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value;
                if (!attributes.ContainsKey(a.Groups[1].Value)) attributes[a.Groups[1].Value] = WebUtility.HtmlDecode(value);
            }
            if (!attributes.TryGetValue("rel", out var rel) || rel.IndexOf("icon", StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (!attributes.TryGetValue("href", out var href) || !Uri.TryCreate(page, href, out var address)) continue;
            yield return new Candidate
            {
                Href = address.AbsoluteUri,
                Rel = rel.ToLowerInvariant(),
                Sizes = attributes.TryGetValue("sizes", out var sizes) ? sizes : "",
            };
        }
    }

    /// <summary>The first candidate, biggest first, that turns out to be a picture.</summary>
    /// <param name="cookies">The browser's cookies for an address, for sites behind a login.</param>
    static async Task<byte[]?> DownloadAsync(IEnumerable<Candidate> candidates, Func<Uri, Task<string>>? cookies = null)
    {
        var ordered = candidates
            .Where(c => !c.Rel.Contains("mask-icon")) // a one-colour outline for Safari, not a picture
            .GroupBy(c => c.Href).Select(g => g.First())
            .OrderByDescending(Score);
        foreach (var c in ordered)
        {
            if (!Uri.TryCreate(c.Href, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) continue;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                if (cookies != null && await cookies(uri) is { Length: > 0 } header) request.Headers.TryAddWithoutValidation("Cookie", header);
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes) continue;
                var bytes = await response.Content.ReadAsByteArrayAsync();
                if (bytes.Length <= MaxBytes && Icons.Sniff(bytes) != null) return bytes;
            }
            catch (Exception) { } // down, refused, timed out: the next one
        }
        return null;
    }

    /// <summary>For a project just saved: what its page declares, else /favicon.ico. Without a login.</summary>
    public static async Task<byte[]?> FetchAsync(Uri site)
    {
        var candidates = new List<Candidate>();
        try
        {
            using var response = await Http.GetAsync(site, HttpCompletionOption.ResponseHeadersRead);
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (response.IsSuccessStatusCode && type.Contains("html") && !(response.Content.Headers.ContentLength > 4 * MaxBytes))
                candidates.AddRange(Parse(await response.Content.ReadAsStringAsync(), response.RequestMessage?.RequestUri ?? site));
        }
        catch (Exception) { }
        candidates.Add(new Candidate { Href = new Uri(site, "/favicon.ico").AbsoluteUri, Rel = "icon" });
        return await DownloadAsync(candidates);
    }

    /// <summary>From a page open in LiteBrowser: what it declares, the favicon Chromium chose, and at last that as drawn.</summary>
    public static async Task<byte[]?> CaptureAsync(CoreWebView2 core)
    {
        var candidates = new List<Candidate>();
        try
        {
            var result = await core.ExecuteScriptAsync(
                "JSON.stringify([...document.querySelectorAll('link[rel*=\"icon\" i]')].map(l => [l.href, l.rel, l.getAttribute('sizes') || '']))");
            // The script's result comes as JSON: a string that holds the array's JSON
            if (ProjectStore.Json.Deserialize<string>(result) is { } json)
                foreach (var row in ProjectStore.Json.Deserialize<string[][]>(json) ?? new string[0][])
                    if (row.Length == 3) candidates.Add(new Candidate { Href = row[0], Rel = row[1].ToLowerInvariant(), Sizes = row[2] });
        }
        catch (Exception) { }
        if (core.FaviconUri is { Length: > 0 } favicon) candidates.Add(new Candidate { Href = favicon, Rel = "icon" });

        var manager = core.CookieManager;
        var bytes = await DownloadAsync(candidates, async uri =>
            string.Join("; ", (await manager.GetCookiesAsync(uri.AbsoluteUri)).Select(c => c.Name + "=" + c.Value)));
        if (bytes != null) return bytes;
        try
        {
            using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (stream == null) return null;
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            return copy.Length > 0 ? copy.ToArray() : null;
        }
        catch (Exception) { return null; }
    }
}
