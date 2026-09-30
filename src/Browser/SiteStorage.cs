using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace LiteBro;

/// <summary>
/// The storage page, https://start.litebro/storage: cookies, localStorage, sessionStorage and IndexedDB of the site
/// in another tab (Tab.StorageOf, set only by the browser when it opens the page), in tables that can be edited.
/// </summary>
static class StoragePage
{
    public const string Path = "/storage";
    public const string Url = "https://" + Home.Host + Path;

    public static bool Is(string? uri) => Home.Is(uri) && new Uri(uri!).AbsolutePath == Path;

    public static Stream Html()
    {
        return L.Stream("storage.html");
    }

    /// <summary>A site's page and the address it is on, if it has a site: http or https.</summary>
    public static bool HasSite(CoreWebView2 core) =>
        Uri.TryCreate(core.Source, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https");

    /// <summary>
    /// Runs sitestorage.js with the operation in the site's page (awaiting its promise) and gives back what it
    /// returns: a JSON string, or throws with the page's error.
    /// </summary>
    public static async Task<string> RunAsync(CoreWebView2 core, Dictionary<string, object> op)
    {
        var script = L.Text("sitestorage.js");
        var expression = script.Replace("__OP__", ProjectStore.Json.Serialize(op));
        var reply = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", ProjectStore.Json.Serialize(new Dictionary<string, object>
        {
            ["expression"] = expression,
            ["awaitPromise"] = true,
            ["returnByValue"] = true,
        }));
        var r = ProjectStore.Json.Deserialize<Dictionary<string, object>>(reply);
        if (r.TryGetValue("exceptionDetails", out var ex) && ex is Dictionary<string, object> details)
        {
            var text = details.TryGetValue("exception", out var e) && e is Dictionary<string, object> err && err.TryGetValue("description", out var d)
                ? d as string : details.TryGetValue("text", out var t) ? t as string : null;
            throw new InvalidOperationException((text ?? L.T("ошибка страницы")).Split('\n')[0]);
        }
        return r.TryGetValue("result", out var res) && res is Dictionary<string, object> result && result.TryGetValue("value", out var v) && v is string s
            ? s : "{}";
    }

    /// <summary>The cookies of the page's host and its parent domains, every path.</summary>
    public static async Task<List<Dictionary<string, object>>> CookiesAsync(CoreWebView2 core)
    {
        var host = new Uri(core.Source).Host;
        var all = await core.CookieManager.GetCookiesAsync(null);
        return all.Where(c => Covers(c.Domain, host)).Select(c => new Dictionary<string, object>
        {
            ["name"] = c.Name,
            ["value"] = c.Value,
            ["domain"] = c.Domain,
            ["path"] = c.Path,
            ["expires"] = c.IsSession ? "" : c.Expires.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            ["httpOnly"] = c.IsHttpOnly,
            ["secure"] = c.IsSecure,
            ["sameSite"] = c.SameSite.ToString(),
        }).ToList();
    }

    static bool Covers(string domain, string host)
    {
        var d = domain.TrimStart('.');
        return host.Equals(d, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Adds or changes a cookie; a changed name, domain or path takes the old one away.</summary>
    public static void SetCookie(CoreWebView2 core, Dictionary<string, object> m)
    {
        string Text(string key) => m.TryGetValue(key, out var v) && v is string s ? s : "";
        bool Flag(string key) => m.TryGetValue(key, out var v) && v is true;
        var host = new Uri(core.Source).Host;
        var name = Text("name");
        if (name.Length == 0 || name.IndexOfAny(new[] { ';', '=', ',', ' ', '\t', '\r', '\n' }) >= 0)
            throw new InvalidOperationException(L.T("Имя cookie не может быть пустым и содержать ; = , или пробелы."));
        var domain = Text("domain").Trim();
        if (domain.Length == 0) domain = host;
        if (!Covers(domain, host)) throw new InvalidOperationException(L.T("Домен cookie должен быть доменом этого сайта: ") + host + ".");
        var path = Text("path").Trim();
        if (!path.StartsWith("/")) path = "/";
        if (m.ContainsKey("oldName"))
        {
            var (oldName, oldDomain, oldPath) = (Text("oldName"), Text("oldDomain"), Text("oldPath"));
            if (oldName != name || oldDomain != domain || oldPath != path) core.CookieManager.DeleteCookiesWithDomainAndPath(oldName, oldDomain, oldPath);
        }
        var cookie = core.CookieManager.CreateCookie(name, Text("value"), domain, path);
        cookie.IsHttpOnly = Flag("httpOnly");
        cookie.IsSecure = Flag("secure");
        cookie.SameSite = Text("sameSite") switch
        {
            "Strict" => CoreWebView2CookieSameSiteKind.Strict,
            "None" => CoreWebView2CookieSameSiteKind.None,
            _ => CoreWebView2CookieSameSiteKind.Lax,
        };
        // SameSite=None is taken only on a secure cookie
        if (cookie.SameSite == CoreWebView2CookieSameSiteKind.None) cookie.IsSecure = true;
        var expires = Text("expires").Trim();
        if (expires.Length > 0)
        {
            if (!DateTime.TryParse(expires, out var at)) throw new InvalidOperationException(L.T("Срок — дата вида 2027-01-31 12:00 или пусто (до закрытия браузера)."));
            cookie.Expires = at;
        }
        core.CookieManager.AddOrUpdateCookie(cookie);
    }

    /// <summary>Every cookie the storage page shows: of the page's host and its parent domains, every path.</summary>
    public static async Task ClearCookiesAsync(CoreWebView2 core)
    {
        var host = new Uri(core.Source).Host;
        foreach (var c in await core.CookieManager.GetCookiesAsync(null))
            if (Covers(c.Domain, host)) core.CookieManager.DeleteCookie(c);
    }

    public static void DeleteCookie(CoreWebView2 core, string name, string domain, string path) =>
        core.CookieManager.DeleteCookiesWithDomainAndPath(name, domain, path);
}
