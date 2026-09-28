using System;
using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LiteBro;

/// <summary>
/// Light or dark: from settings.ini (Theme), or by default from Windows' own choice for apps.
/// The window frame, the tab strip and the toolbar take their colours from here; pages follow through
/// prefers-color-scheme, and a WebView2 starts with the background of the theme instead of white.
/// </summary>
static class Theme
{
    const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    // DWMWA_USE_IMMERSIVE_DARK_MODE; Windows 10 before 20H1 knew it as 19
    const int DarkModeAttribute = 20, DarkModeAttributeOld = 19;

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr window, string? subAppName, string? subIdList);

    /// <summary>"auto" (as Windows), "dark" or "light".</summary>
    static string setting = "auto";

    public static bool Dark { get; private set; }

    public static Color Strip, Face, Field, Text, Dim, Line;

    /// <summary>What a WebView2 shows before its page paints.</summary>
    public static Color PageBackground => Dark ? Color.FromArgb(0x20, 0x20, 0x20) : Color.White;

    public static void Init(string value)
    {
        setting = value;
        Refresh();
    }

    /// <summary>Reads Windows' choice again; true when the theme changed.</summary>
    public static bool Refresh()
    {
        bool dark = setting switch
        {
            "dark" => true,
            "light" => false,
            _ => WindowsAppsDark(),
        };
        bool changed = dark != Dark || Text.IsEmpty;
        Dark = dark;
        if (dark)
        {
            Strip = Color.FromArgb(0x1c, 0x1c, 0x1c);
            Face = Color.FromArgb(0x2e, 0x2e, 0x2e);
            Field = Color.FromArgb(0x1f, 0x1f, 0x1f);
            Text = Color.FromArgb(0xe8, 0xe8, 0xe8);
            Dim = Color.FromArgb(0x9a, 0x9a, 0x9a);
            Line = Color.FromArgb(0x4a, 0x4a, 0x4a);
        }
        else
        {
            Face = System.Drawing.SystemColors.Control;
            Strip = Mix(Face, System.Drawing.SystemColors.ControlDark, .3f);
            Field = System.Drawing.SystemColors.Window;
            Text = System.Drawing.SystemColors.ControlText;
            Dim = System.Drawing.SystemColors.GrayText;
            Line = System.Drawing.SystemColors.ControlDark;
        }
        return changed;
    }

    /// <summary>Settings → Personalization → Colours → «Режим приложения по умолчанию».</summary>
    static bool WindowsAppsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception) { return false; }
    }

    /// <summary>The title bar and the frame of a window: dark or light as the theme.</summary>
    public static void ApplyFrame(IntPtr window)
    {
        int on = Dark ? 1 : 0;
        try
        {
            if (DwmSetWindowAttribute(window, DarkModeAttribute, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(window, DarkModeAttributeOld, ref on, sizeof(int));
        }
        catch (Exception) { } // before Windows 10 there is no dark frame
    }

    /// <summary>An edit box drawn by Windows' own dark style (the one of the file dialogs), or the usual one.</summary>
    public static void ApplyEdit(IntPtr window)
    {
        try { SetWindowTheme(window, Dark ? "DarkMode_CFD" : null, null); }
        catch (Exception) { }
    }

    public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
}
