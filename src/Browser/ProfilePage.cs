using System;
using System.IO;

namespace LiteBro;

/// <summary>
/// «Настройка профиля»: a project's own settings for its pages (SiteSettings), at https://start.litebro/profile/&lt;id&gt;,
/// opened from the project's console. A browser page, so it may post web messages; it reads the project from the
/// tiles every start page gets.
/// </summary>
static class ProfilePage
{
    const string Prefix = "/profile/";

    public static string Url(Project p) => Home.Url + "profile/" + Uri.EscapeDataString(p.Id);

    /// <summary>The project of a /profile/ address; null for another address or a project that is gone.</summary>
    public static Project? Parse(string path) =>
        path.StartsWith(Prefix) ? ProjectStore.Find(Uri.UnescapeDataString(path.Substring(Prefix.Length))) : null;

    public static bool Is(string? uri) => Home.Is(uri) && new Uri(uri!).AbsolutePath.StartsWith(Prefix);

    public static Stream Html() => L.Stream("profile.html");
}
