using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace LiteBro;

/// <summary>
/// The site permissions page, https://start.litebro/perms: what sites were allowed or refused (camera, microphone,
/// location, notifications…) in the shared profile and in each project's own, with the choice changed or forgotten.
/// </summary>
static class PermsPage
{
    public const string Path = "/perms";
    public const string Url = "https://" + Home.Host + Path;

    public static bool Is(string? uri) => Home.Is(uri) && new Uri(uri!).AbsolutePath == Path;

    public static Stream Html() => L.Stream("perms.html");

    /// <summary>The kinds by their enum names: a newer runtime's kinds keep their English name.</summary>
    static string KindName(CoreWebView2PermissionKind kind) => kind.ToString() switch
    {
        "Microphone" => L.T("Микрофон"),
        "Camera" => L.T("Камера"),
        "Geolocation" => L.T("Местоположение"),
        "Notifications" => L.T("Уведомления"),
        "OtherSensors" => L.T("Датчики движения"),
        "ClipboardRead" => L.T("Чтение буфера обмена"),
        "MultipleAutomaticDownloads" => L.T("Несколько загрузок подряд"),
        "FileReadWrite" => L.T("Чтение и запись файлов"),
        "Autoplay" => L.T("Автовоспроизведение"),
        "LocalFonts" => L.T("Шрифты компьютера"),
        "MidiSystemExclusiveMessages" => L.T("MIDI-устройства"),
        "WindowManagement" => L.T("Окна на нескольких экранах"),
        var other => other,
    };

    /// <summary>The profiles there are: the shared one ("") and each project's own, with the names the page shows.</summary>
    public static List<(string Profile, string Name)> Profiles() =>
        new[] { ("", L.T("Общий")) }.Concat(ProjectStore.All.Where(p => p.Profile.Length > 0)
            .Select(p => (p.Profile, L.T("Проект «") + p.Name + L.T("»")))).ToList();

    /// <summary>The settings of one profile that are not the default «ask», as rows for the page.</summary>
    public static async Task<List<Dictionary<string, object>>> RowsAsync(CoreWebView2Profile data, string profile, string name)
    {
        var rows = new List<Dictionary<string, object>>();
        foreach (var s in await data.GetNonDefaultPermissionSettingsAsync())
        {
            if (s.PermissionState == CoreWebView2PermissionState.Default) continue;
            rows.Add(new Dictionary<string, object>
            {
                ["profile"] = profile,
                ["profileName"] = name,
                ["origin"] = s.PermissionOrigin,
                ["kind"] = s.PermissionKind.ToString(),
                ["kindName"] = KindName(s.PermissionKind),
                ["state"] = s.PermissionState == CoreWebView2PermissionState.Allow ? "allow" : "deny",
            });
        }
        return rows;
    }

    /// <summary>The page's choice as the engine's values; false for anything it could not have sent.</summary>
    public static bool TryParse(string? kind, string? state, out CoreWebView2PermissionKind k, out CoreWebView2PermissionState s)
    {
        s = state switch
        {
            "allow" => CoreWebView2PermissionState.Allow,
            "deny" => CoreWebView2PermissionState.Deny,
            _ => CoreWebView2PermissionState.Default,
        };
        k = default;
        return state is "allow" or "deny" or "ask" && kind != null && Enum.TryParse(kind, out k)
            && Enum.IsDefined(typeof(CoreWebView2PermissionKind), k);
    }
}
