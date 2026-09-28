using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace LiteBro;

/// <summary>
/// «Только localhost»: pages may reach this machine (and the local network), the allowed hosts and nothing else.
/// The mode and the journal of requests that leave the machine are switched on the start page and on /net.
/// </summary>
static class NetGuard
{
    public static bool LocalOnly { get; private set; }
    /// <summary>The CORS switch as set; it works only together with LocalOnly (see CorsOff).</summary>
    public static bool IgnoreCors { get; private set; }
    /// <summary>The engine is to run without CORS and same-origin checks: never outside «только localhost».</summary>
    public static bool CorsOff => LocalOnly && IgnoreCors;
    /// <summary>Journal the outside requests while the mode is off too (with it on they are always journaled).</summary>
    public static bool Journal { get; private set; }
    static Regex[] allowed = Array.Empty<Regex>();
    public static string AllowText { get; private set; } = "";

    public static bool Watching => LocalOnly || Journal;

    public static void Init(Settings s)
    {
        LocalOnly = s.LocalOnly;
        Journal = s.NetJournal;
        IgnoreCors = s.IgnoreCors;
        SetAllowed(s.AllowHosts);
    }

    public static void Set(bool localOnly, bool journal, bool ignoreCors)
    {
        LocalOnly = localOnly;
        Journal = journal;
        IgnoreCors = ignoreCors;
    }

    /// <summary>Hosts through spaces, commas or lines; *.example.com covers example.com and its subdomains.</summary>
    public static void SetAllowed(string text)
    {
        var names = Patterns(text);
        AllowText = string.Join(" ", names);
        allowed = names.Select(ToRegex).ToArray();
    }

    public static string[] Patterns(string text) =>
        text.Split(new[] { ' ', ',', ';', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(n => n.Trim().ToLowerInvariant()).Where(n => n.Length > 0).Distinct().Take(200).ToArray();

    static Regex ToRegex(string pattern)
    {
        var body = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
        // *.cdn.com also covers cdn.com itself
        if (pattern.StartsWith("*.")) body = "(.*\\.)?" + Regex.Escape(pattern.Substring(2)).Replace(@"\*", ".*");
        return new Regex("^" + body + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>A network address: http, https and web sockets; not files, data, blobs or this program's pages.</summary>
    public static bool IsNetwork(Uri u) =>
        (u.Scheme is "http" or "https" or "ws" or "wss") && !Home.Is(u.AbsoluteUri);

    /// <summary>This machine, the names it points at itself, the local network and the settings' LocalHosts.</summary>
    public static bool IsLocal(Uri u)
    {
        var host = u.DnsSafeHost;
        if (IPAddress.TryParse(host, out var ip)) return IsLocalIp(ip) || Router.IsLocal(AsHttp(u), App.Current.S);
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || Router.IsLocal(AsHttp(u), App.Current.S);
    }

    static Uri AsHttp(Uri u) => u.Scheme is "ws" or "wss" ? new UriBuilder(u) { Scheme = u.Scheme == "ws" ? "http" : "https", Port = u.Port }.Uri : u;

    /// <summary>Loopback, private ranges (10/8, 172.16/12, 192.168/16), link-local, and their IPv6 kin.</summary>
    public static bool IsLocalIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (b[0] & 0xfe) == 0xfc; // fc00::/7
    }

    public static bool IsAllowed(Uri u) => allowed.Any(r => r.IsMatch(u.DnsSafeHost));

    /// <summary>Leaves this machine: what the journal writes down.</summary>
    public static bool IsOutside(Uri u) => IsNetwork(u) && !IsLocal(u);

    public static bool ShouldBlock(Uri u) => LocalOnly && IsOutside(u) && !IsAllowed(u);

    static string? pageScript;

    /// <summary>
    /// The script of netpage.js with the current rules: stacks of fetch, XHR, sendBeacon and EventSource to outside
    /// hosts for the journal, and the web socket guard (sockets never reach WebResourceRequested).
    /// </summary>
    public static string PageScript()
    {
        if (pageScript == null)
        {
            using var stream = typeof(NetGuard).Assembly.GetManifestResourceStream("netpage.js");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            pageScript = reader.ReadToEnd();
        }
        var config = ProjectStore.Json.Serialize(new Dictionary<string, object>
        {
            ["names"] = Router.LocalNames().Concat(Patterns(App.Current.S.LocalHosts)).ToArray(),
            ["allow"] = Patterns(AllowText),
            ["block"] = LocalOnly,
            ["trace"] = Watching,
        });
        return pageScript.Replace("__CONFIG__", config);
    }

    /// <summary>What a blocked page shows in place of itself.</summary>
    public static string BlockedPage(string url) =>
        "<!doctype html><meta charset=utf-8><title>Заблокировано</title><style>:root{color-scheme:light dark}" +
        "body{font:15px 'Segoe UI',sans-serif;max-width:640px;margin:15vh auto;padding:0 24px}h1{font-size:22px;font-weight:600}" +
        "code{word-break:break-all}</style><h1>Заблокировано режимом «только localhost»</h1>" +
        "<p>LiteBro не пустил страницу в интернет:</p><p><code>" + WebUtility.HtmlEncode(url) + "</code></p>" +
        "<p>Режим выключается переключателем справа на стартовой странице. Там же, в журнале сети, можно добавить сайт в разрешённые.</p>";
}

/// <summary>
/// The journal of requests that leave the machine: logs\network.log (tab-separated, a new file after 5 MB, the old one
/// kept as network.1.log) and the last entries in memory for the /net page. Written on a thread of its own.
/// </summary>
static class NetLog
{
    public sealed class Entry
    {
        public long Seq { get; set; }
        public string Time { get; set; } = "";
        public string Method { get; set; } = "";
        public string Url { get; set; } = "";
        public string Host { get; set; } = "";
        public string Ip { get; set; } = "";
        /// <summary>The response's status, or why it was blocked.</summary>
        public string Result { get; set; } = "";
        public bool Blocked { get; set; }
        public long Size { get; set; } = -1;
        /// <summary>What asked for it: document, script, style, image, fetch/XHR...</summary>
        public string Kind { get; set; } = "";
        /// <summary>The page it came from.</summary>
        public string Page { get; set; } = "";
        public string Stack { get; set; } = "";
        internal DateTime Created = DateTime.UtcNow;
    }

    const int Kept = 2000;
    const long MaxFile = 5 << 20;
    static readonly BlockingCollection<Entry> queue = new(10000);
    static readonly LinkedList<Entry> recent = new();
    static readonly ConcurrentDictionary<string, string> ips = new(StringComparer.OrdinalIgnoreCase);
    static long seq;
    // Stacks the pages sent for outside addresses, waiting for the journal entry of the same address
    static readonly ConcurrentDictionary<string, (string Stack, DateTime At)> stacks = new();
    static readonly TimeSpan StackWait = TimeSpan.FromMilliseconds(400), StackKept = TimeSpan.FromSeconds(20);

    /// <summary>A page's script saw a request to this address go out, with this call stack.</summary>
    public static void NoteStack(string url, string stack)
    {
        if (stacks.Count > 2000)
            foreach (var old in stacks.Where(p => DateTime.UtcNow - p.Value.At > StackKept).Select(p => p.Key).ToList())
                stacks.TryRemove(old, out _);
        stacks[url] = (stack, DateTime.UtcNow);
    }
    static Thread? writer;

    public static string FilePath => Path.Combine(Settings.Dir, "logs", "network.log");

    public static void Add(string method, Uri url, string result, bool blocked, long size, string kind, string page, string stack = "")
    {
        var e = new Entry
        {
            Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            Method = method,
            Url = url.AbsoluteUri,
            Host = url.Host,
            Result = result,
            Blocked = blocked,
            Size = size,
            Kind = kind,
            Page = page,
            Stack = stack,
        };
        lock (queue)
        {
            if (writer == null)
            {
                writer = new Thread(Write) { IsBackground = true, Name = "Network journal", Priority = ThreadPriority.BelowNormal };
                writer.Start();
            }
        }
        queue.TryAdd(e); // a flood beyond the queue is dropped rather than slowing the pages
    }

    static void Write()
    {
        foreach (var e in queue.GetConsumingEnumerable())
        {
            e.Ip = Resolve(e.Host);
            // The page's message with the stack may come a moment after the request itself
            var wait = e.Created + StackWait - DateTime.UtcNow;
            if (e.Stack.Length == 0 && wait > TimeSpan.Zero && !stacks.ContainsKey(e.Url)) Thread.Sleep(wait);
            if (e.Stack.Length == 0 && stacks.TryRemove(e.Url, out var s) && DateTime.UtcNow - s.At < StackKept) e.Stack = s.Stack;
            lock (recent)
            {
                e.Seq = ++seq;
                recent.AddLast(e);
                if (recent.Count > Kept) recent.RemoveFirst();
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxFile)
                {
                    var old = Path.Combine(file.DirectoryName!, "network.1.log");
                    File.Delete(old);
                    File.Move(FilePath, old);
                }
                bool header = !File.Exists(FilePath);
                using var w = new StreamWriter(FilePath, append: true, new UTF8Encoding(false));
                if (header) w.WriteLine("время\tметод\tрезультат\tразмер\tинициатор\tдомен\tIP\tадрес\tстраница\tстек");
                w.WriteLine(string.Join("\t", e.Time, e.Method, e.Result, e.Size < 0 ? "" : e.Size.ToString(), e.Kind,
                    e.Host, e.Ip, e.Url, e.Page, e.Stack.Replace("\r", "").Replace("\n", " ⏎ ").Replace("\t", " ")));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The host's address as Windows resolves it (the browser does not say which one it connected to).
    /// A blocked host is looked up too: the lookup itself goes only to the DNS server.
    /// </summary>
    static string Resolve(string host)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out _)) return host.Trim('[', ']');
        // Through the gateway («только localhost») the address really connected to is known
        if (Gateway.IpOf(host) is { } real) return real;
        if (ips.TryGetValue(host, out var ip)) return ip;
        try
        {
            var task = Dns.GetHostAddressesAsync(host);
            ip = task.Wait(2000) ? string.Join(" ", task.Result.Take(2).Select(a => a.ToString())) + " (DNS)" : "";
        }
        catch (Exception) { ip = ""; }
        if (ips.Count > 5000) ips.Clear();
        ips[host] = ip;
        return ip;
    }

    /// <summary>Entries after a number, for the /net page; at most the last 500.</summary>
    public static string Since(long after)
    {
        List<Entry> list;
        long last;
        lock (recent)
        {
            list = recent.Where(e => e.Seq > after).ToList();
            last = seq;
        }
        if (list.Count > 500) list = list.Skip(list.Count - 500).ToList();
        return ProjectStore.Json.Serialize(new Dictionary<string, object>
        {
            ["last"] = last,
            ["entries"] = list,
        });
    }

    /// <summary>The entries with these numbers (those the /net page shows under its filter), as JSON or CSV for Excel.</summary>
    public static string Export(IEnumerable<long> seqs, bool csv)
    {
        var wanted = new HashSet<long>(seqs);
        List<Entry> list;
        lock (recent) list = recent.Where(e => wanted.Contains(e.Seq)).ToList();
        if (!csv)
            return ProjectStore.Json.Serialize(list.Select(e => new Dictionary<string, object>
            {
                ["time"] = e.Time,
                ["method"] = e.Method,
                ["url"] = e.Url,
                ["host"] = e.Host,
                ["ip"] = e.Ip,
                ["result"] = e.Result,
                ["blocked"] = e.Blocked,
                ["size"] = e.Size < 0 ? null! : e.Size,
                ["initiator"] = e.Kind,
                ["page"] = e.Page,
                ["stack"] = e.Stack,
            }).ToList());
        // Excel with Russian settings splits on semicolons
        var sb = new StringBuilder("время;метод;результат;заблокировано;размер;инициатор;домен;IP;адрес;страница;стек\r\n");
        foreach (var e in list)
            sb.Append(string.Join(";", new[] { e.Time, e.Method, e.Result, e.Blocked ? "да" : "нет", e.Size < 0 ? "" : e.Size.ToString(),
                e.Kind, e.Host, e.Ip, e.Url, e.Page, e.Stack }.Select(Cell))).Append("\r\n");
        return sb.ToString();
    }

    static string Cell(string s) => s.IndexOfAny(new[] { ';', '"', '\r', '\n' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";

    public static void Clear()
    {
        lock (recent) recent.Clear();
        try
        {
            File.Delete(FilePath);
            File.Delete(Path.Combine(Path.GetDirectoryName(FilePath)!, "network.1.log"));
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
    }
}

/// <summary>The network journal and the allowed hosts: https://start.litebro/net, so it may post web messages.</summary>
static class NetPage
{
    public const string Path = "/net";
    public const string Url = "https://" + Home.Host + Path;
    static byte[]? html;

    public static Stream Html()
    {
        if (html == null)
        {
            using var stream = typeof(NetPage).Assembly.GetManifestResourceStream("net.html");
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            html = copy.ToArray();
        }
        return new MemoryStream(html, writable: false);
    }
}
