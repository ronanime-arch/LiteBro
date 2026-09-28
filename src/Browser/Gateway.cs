using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace LiteBro;

/// <summary>
/// The gateway of «только localhost»: while the mode is on, the engine sends everything that is not local through
/// this proxy on 127.0.0.1 (local addresses bypass it). It only lets through or refuses, never reads HTTPS: a CONNECT
/// becomes a byte tunnel to the host, and it notes the address it really connected to. It catches what the page
/// filter cannot see (web sockets of workers, any engine request), and closes tunnels the new rules forbid.
/// </summary>
static class Gateway
{
    sealed class Tunnel
    {
        public Uri Url = null!;
        public TcpClient Client = null!, Server = null!;

        public void Close()
        {
            try { Client.Close(); } catch (Exception) { }
            try { Server.Close(); } catch (Exception) { }
        }
    }

    const int MaxHead = 64 << 10;
    static TcpListener? listener;
    static int port;
    static readonly ConcurrentDictionary<string, string> ips = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<Tunnel, byte> tunnels = new();

    /// <summary>Starts listening (once, on a free port) and gives the port.</summary>
    public static int Start()
    {
        if (listener != null) return port;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = AcceptAsync(listener);
        return port;
    }

    /// <summary>What the engine never sends here: this machine, the local network and the names it points at itself.</summary>
    public static string BypassList() => string.Join(";", new[]
    {
        "localhost", "*.localhost", "127.0.0.0/8", "[::1]", "0.0.0.0", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16",
        "169.254.0.0/16", "fc00::/7", "fe80::/10", Home.Host,
    }.Concat(Router.LocalNames()).Concat(NetGuard.Patterns(App.Current.S.LocalHosts))
        .Where(p => p.IndexOfAny(new[] { ' ', ';', '"' }) < 0).Distinct());

    /// <summary>The address a host was last really connected to through the gateway.</summary>
    public static string? IpOf(string host) => ips.TryGetValue(host, out var ip) ? ip : null;

    /// <summary>The rules changed: tunnels to hosts no longer allowed are cut now.</summary>
    public static void Enforce()
    {
        foreach (var t in tunnels.Keys)
            if (NetGuard.LocalOnly && NetGuard.ShouldBlock(t.Url))
            {
                tunnels.TryRemove(t, out _);
                t.Close();
                NetLog.Add("CONNECT", t.Url, "соединение разорвано", blocked: true, -1, "шлюз", "");
            }
    }

    static async Task AcceptAsync(TcpListener l)
    {
        while (true)
        {
            TcpClient client;
            try { client = await l.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch (Exception) { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    static async Task ServeAsync(TcpClient client)
    {
        var tunnel = new Tunnel { Client = client, Server = new TcpClient() };
        try
        {
            client.NoDelay = true;
            var fromClient = client.GetStream();
            var (head, rest) = await ReadHeadAsync(fromClient).ConfigureAwait(false);
            if (head == null) return;
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = lines[0].Split(' ');
            if (first.Length < 3) return;
            var method = first[0].ToUpperInvariant();
            bool connect = method == "CONNECT";
            // CONNECT host:port for HTTPS and web sockets; an absolute address for plain HTTP
            if (!Uri.TryCreate(connect ? "https://" + first[1] + "/" : first[1], UriKind.Absolute, out var url)
                || (!connect && url.Scheme != Uri.UriSchemeHttp))
            {
                await ReplyAsync(fromClient, "400 Bad Request").ConfigureAwait(false);
                return;
            }
            tunnel.Url = url;
            if (NetGuard.LocalOnly && NetGuard.ShouldBlock(url))
            {
                await ReplyAsync(fromClient, "403 Forbidden").ConfigureAwait(false);
                NetLog.Add(method, url, "заблокировано шлюзом", blocked: true, -1, "шлюз", "");
                return;
            }
            var server = tunnel.Server;
            server.NoDelay = true;
            var connecting = server.ConnectAsync(url.DnsSafeHost, url.Port);
            if (await Task.WhenAny(connecting, Task.Delay(15000)).ConfigureAwait(false) != connecting || connecting.IsFaulted)
            {
                await ReplyAsync(fromClient, "502 Bad Gateway").ConfigureAwait(false);
                return;
            }
            if (server.Client.RemoteEndPoint is IPEndPoint remote)
            {
                if (ips.Count > 5000) ips.Clear();
                ips[url.Host] = (remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address).ToString();
            }
            var toServer = server.GetStream();
            if (connect)
            {
                var ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
                await fromClient.WriteAsync(ok, 0, ok.Length).ConfigureAwait(false);
            }
            else
            {
                // One request per connection, so the next one (maybe for another host) is checked again
                var sb = new StringBuilder(method + " " + url.PathAndQuery + " " + first[2] + "\r\n");
                foreach (var line in lines.Skip(1))
                {
                    var name = line.Split(':')[0].Trim();
                    if (line.Length == 0 || name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase))
                        continue;
                    sb.Append(line).Append("\r\n");
                }
                sb.Append("Connection: close\r\n\r\n");
                var bytes = Encoding.ASCII.GetBytes(sb.ToString());
                await toServer.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            }
            if (rest.Length > 0) await toServer.WriteAsync(rest, 0, rest.Length).ConfigureAwait(false);
            tunnels[tunnel] = 0;
            await Task.WhenAny(CopyAsync(fromClient, toServer), CopyAsync(toServer, fromClient)).ConfigureAwait(false);
        }
        catch (Exception) { } // either side went away
        finally
        {
            tunnels.TryRemove(tunnel, out _);
            tunnel.Close();
        }
    }

    /// <summary>The request head up to the empty line, and whatever came after it in the same read.</summary>
    static async Task<(string? Head, byte[] After)> ReadHeadAsync(Stream s)
    {
        var buffer = new byte[8192];
        using var head = new MemoryStream();
        while (head.Length < MaxHead)
        {
            int n = await s.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (n == 0) return (null, Array.Empty<byte>());
            head.Write(buffer, 0, n);
            var all = head.GetBuffer();
            int len = (int)head.Length;
            for (int i = Math.Max(0, len - n - 3); i + 3 < len; i++)
                if (all[i] == '\r' && all[i + 1] == '\n' && all[i + 2] == '\r' && all[i + 3] == '\n')
                    return (Encoding.ASCII.GetString(all, 0, i), all.Skip(i + 4).Take(len - i - 4).ToArray());
        }
        return (null, Array.Empty<byte>());
    }

    static async Task ReplyAsync(Stream s, string status)
    {
        var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        try { await s.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false); }
        catch (Exception) { }
    }

    static async Task CopyAsync(Stream from, Stream to)
    {
        var buffer = new byte[16384];
        int n;
        while ((n = await from.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            await to.WriteAsync(buffer, 0, n).ConfigureAwait(false);
            await to.FlushAsync().ConfigureAwait(false);
        }
    }
}
