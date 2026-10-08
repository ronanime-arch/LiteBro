using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace LiteBro;

/// <summary>
/// A phone or tablet screen and a slow or absent network for one tab, through the DevTools protocol:
/// the same overrides as DevTools' device mode, without opening DevTools.
/// </summary>
static class Emulation
{
    public sealed class Device
    {
        string name = "";
        public string Name { get => L.T(name); set => name = value; }
        /// <summary>The untranslated name: what a project's settings keep (SiteSettings.Device).</summary>
        public string Key => name;
        public int Width, Height;
        public double Scale;
        public bool Mobile;
        public string UserAgent = "";
    }

    public sealed class Speed
    {
        string name = "";
        public string Name { get => L.T(name); set => name = value; }
        public bool Offline;
        public double Latency;
        public double Down, Up; // bytes a second
    }

    const string Iphone = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";
    const string Android = "Mozilla/5.0 (Linux; Android 14; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Mobile Safari/537.36";
    const string Ipad = "Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";

    public static readonly Device[] Devices =
    {
        new() { Name = "iPhone SE", Width = 375, Height = 667, Scale = 2, Mobile = true, UserAgent = Iphone },
        new() { Name = "iPhone 15", Width = 393, Height = 852, Scale = 3, Mobile = true, UserAgent = Iphone },
        new() { Name = "Pixel 7", Width = 412, Height = 915, Scale = 2.625, Mobile = true, UserAgent = Android },
        new() { Name = "iPad", Width = 820, Height = 1180, Scale = 2, Mobile = true, UserAgent = Ipad },
        new() { Name = "Ноутбук 1366×768", Width = 1366, Height = 768, Scale = 1 },
        new() { Name = "Full HD 1920×1080", Width = 1920, Height = 1080, Scale = 1 },
    };

    // DevTools' own presets
    public static readonly Speed[] Speeds =
    {
        new() { Name = "Быстрая 3G", Latency = 562.5, Down = 180000, Up = 84375 },
        new() { Name = "Медленная 3G", Latency = 2000, Down = 50000, Up = 50000 },
        new() { Name = "Офлайн", Offline = true },
    };

    public static Device? Find(string key) => Array.Find(Devices, d => d.Key == key);

    static string N(double d) => d.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Puts the tab's device and network on its WebView; null takes the override away. A device larger than the room
    /// the page has is shrunk to fit.
    /// </summary>
    /// <param name="noCache">The page always loads from the server (DevTools' «Disable cache»): it needs the network domain too.</param>
    public static async Task ApplyAsync(CoreWebView2 core, Device? device, Speed? speed, Size room, bool noCache = false)
    {
        try
        {
            if (device != null)
            {
                double fit = Math.Min(1, Math.Min(room.Width / (double)device.Width, room.Height / (double)device.Height));
                if (fit <= 0) fit = 1;
                await core.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride",
                    $"{{\"width\":{device.Width},\"height\":{device.Height},\"deviceScaleFactor\":{N(device.Scale)},\"mobile\":{(device.Mobile ? "true" : "false")}," +
                    $"\"screenWidth\":{device.Width},\"screenHeight\":{device.Height},\"scale\":{N(fit)}}}");
                await core.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled",
                    device.Mobile ? "{\"enabled\":true,\"maxTouchPoints\":5}" : "{\"enabled\":false}");
                await core.CallDevToolsProtocolMethodAsync("Emulation.setEmitTouchEventsForMouse",
                    device.Mobile ? "{\"enabled\":true,\"configuration\":\"mobile\"}" : "{\"enabled\":false}");
                await core.CallDevToolsProtocolMethodAsync("Emulation.setUserAgentOverride",
                    ProjectStore.Json.Serialize(new Dictionary<string, object> { ["userAgent"] = device.UserAgent }));
            }
            else
            {
                await core.CallDevToolsProtocolMethodAsync("Emulation.clearDeviceMetricsOverride", "{}");
                await core.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled", "{\"enabled\":false}");
                await core.CallDevToolsProtocolMethodAsync("Emulation.setEmitTouchEventsForMouse", "{\"enabled\":false}");
                await core.CallDevToolsProtocolMethodAsync("Emulation.setUserAgentOverride", "{\"userAgent\":\"\"}");
            }
            if (speed != null || noCache)
            {
                await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
                await core.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled", noCache ? "{\"cacheDisabled\":true}" : "{\"cacheDisabled\":false}");
            }
            if (speed != null)
            {
                await core.CallDevToolsProtocolMethodAsync("Network.emulateNetworkConditions",
                    $"{{\"offline\":{(speed.Offline ? "true" : "false")},\"latency\":{N(speed.Latency)}," +
                    $"\"downloadThroughput\":{N(speed.Offline ? 0 : speed.Down)},\"uploadThroughput\":{N(speed.Offline ? 0 : speed.Up)}}}");
            }
            else
            {
                await core.CallDevToolsProtocolMethodAsync("Network.emulateNetworkConditions",
                    "{\"offline\":false,\"latency\":0,\"downloadThroughput\":-1,\"uploadThroughput\":-1}");
                // The network domain costs a little per request: on only while the network is emulated or the cache is off
                if (!noCache) await core.CallDevToolsProtocolMethodAsync("Network.disable", "{}");
            }
        }
        catch (Exception) { } // the WebView closed meanwhile
    }
}
