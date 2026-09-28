using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace LiteBro;

/// <summary>
/// The console of a project: its program's output, live from logs\&lt;id&gt;.log, and a line to type into the program.
/// Served on the start page's host under /log/&lt;id&gt;, so it may post web messages; the page fetches what the log gained.
/// </summary>
static class ProgramLog
{
    const string Prefix = "/log/";
    // A long log opens with its end; each poll brings at most this much more
    const int FirstBytes = 256 << 10, MaxBytes = 512 << 10;
    // Like the start page, and the page may fetch from its own host
    public const string Headers = "Content-Type: text/html; charset=utf-8\r\nContent-Security-Policy: default-src 'none'; " +
        "script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'";
    public const string JsonHeaders = "Content-Type: application/json; charset=utf-8\r\nCache-Control: no-store";

    public static string Url(Project p) => Home.Url + "log/" + p.Id;

    static string LogPath(string id) => Path.Combine(Settings.Dir, "logs", id + ".log");

    /// <summary>The project of a /log/ address and whether the address is for the text rather than the page.</summary>
    public static Project? Parse(string path, out bool text)
    {
        text = false;
        if (!path.StartsWith(Prefix)) return null;
        var rest = path.Substring(Prefix.Length);
        if (rest.EndsWith("/text"))
        {
            text = true;
            rest = rest.Substring(0, rest.Length - 5);
        }
        return ProjectStore.Find(rest);
    }

    /// <summary>
    /// What the log gained since a byte offset (-1 for the first time), up to its last whole line, so a letter of
    /// several bytes is never cut. A log shorter than the offset was started anew with the program.
    /// </summary>
    public static string Chunk(Project p, long from, bool running)
    {
        long size = 0;
        bool reset = false;
        string text = "";
        try
        {
            using var stream = new FileStream(LogPath(p.Id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            size = stream.Length;
            if (from < 0 || from > size)
            {
                reset = true;
                from = Math.Max(0, size - FirstBytes);
            }
            var buffer = new byte[(int)Math.Min(MaxBytes, size - from)];
            stream.Position = from;
            int read = 0;
            while (read < buffer.Length)
            {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0) break;
                read += n;
            }
            int end = read == 0 ? 0 : Array.LastIndexOf(buffer, (byte)'\n', read - 1) + 1;
            // The first chunk of a long log starts after a line break, not in the middle of a line
            int start = reset && from > 0 ? Array.IndexOf(buffer, (byte)'\n', 0, end) + 1 : 0;
            text = Encoding.UTF8.GetString(buffer, start, Math.Max(0, end - start));
            size = from + end;
        }
        catch (FileNotFoundException) { reset = from != 0; }
        catch (DirectoryNotFoundException) { reset = from != 0; }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { size = Math.Max(0, from); }
        return ProjectStore.Json.Serialize(new Dictionary<string, object>
        {
            ["size"] = size,
            ["reset"] = reset,
            ["text"] = text,
            ["running"] = running,
        });
    }

    public static Stream Bytes(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    static string H(string s) => WebUtility.HtmlEncode(s);
    static string Js(string s) => ProjectStore.Json.Serialize(s).Replace("<", "\\u003c");

    public static string Page(Project p) =>
        "<!doctype html><html><head><meta charset=utf-8><title>Вывод: " + H(p.Name) + "</title><style>" +
        ":root{color-scheme:light dark;--muted:rgba(127,127,127,.9);--line:rgba(127,127,127,.3)}" +
        "html,body{margin:0;height:100%;background:Canvas;color:CanvasText;font:14px 'Segoe UI',sans-serif}" +
        "body{display:flex;flex-direction:column}" +
        "header{display:flex;align-items:center;gap:12px;padding:10px 14px;border-bottom:1px solid var(--line)}" +
        "h1{font-size:16px;font-weight:600;margin:0}" +
        "#state{color:var(--muted)}#state.on{color:#37c46a}" +
        "#path{color:var(--muted);font:12px Consolas,monospace;margin-left:auto;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}" +
        "button{font:13px 'Segoe UI',sans-serif;padding:4px 12px;border:1px solid var(--line);border-radius:6px;background:transparent;color:inherit;cursor:pointer}" +
        "button:hover{background:rgba(127,127,127,.15)}" +
        "#out{flex:1;margin:0;padding:10px 14px;overflow:auto;white-space:pre-wrap;word-break:break-word;font:13px/1.45 Consolas,monospace}" +
        "form{display:flex;gap:8px;align-items:center;padding:8px 14px;border-top:1px solid var(--line)}" +
        "form span{font:13px Consolas,monospace;color:var(--muted)}" +
        "#line{flex:1;font:13px Consolas,monospace;padding:6px 8px;border:1px solid var(--line);border-radius:6px;background:transparent;color:inherit;outline:none}" +
        "#line:focus{border-color:#4d6bfe}#line:disabled{opacity:.5}" +
        "</style></head><body>" +
        "<header><h1>" + H(p.Name) + "</h1><span id=state></span>" +
        "<button id=stop hidden>Остановить</button><button id=clear>Очистить экран</button>" +
        "<span id=path title='" + H(LogPath(p.Id)) + "'>" + H(LogPath(p.Id)) + "</span></header>" +
        "<pre id=out></pre>" +
        "<form id=send><span>&gt;</span><input id=line autocomplete=off spellcheck=false placeholder='Строка для программы, Enter — отправить'></form>" +
        "<script>" +
        "const id=" + Js(p.Id) + ",out=document.getElementById('out'),line=document.getElementById('line'),state=document.getElementById('state'),stop=document.getElementById('stop');" +
        "let from=-1,running=false,busy=false;" +
        "const post=m=>window.chrome.webview.postMessage(m);" +
        "function show(r){running=r;state.textContent=r?'● работает':'остановлена';state.className=r?'on':'';stop.hidden=!r;line.disabled=!r;}" +
        "async function poll(){if(busy)return;busy=true;try{" +
        "const r=await fetch(location.pathname+'/text?from='+from,{cache:'no-store'});const d=await r.json();" +
        "const end=out.scrollHeight-out.scrollTop-out.clientHeight<24;" +
        "if(d.reset)out.textContent='';if(d.text)out.append(d.text);from=d.size;show(d.running);" +
        // Only a long log is cut: the page keeps the last lines, as a terminal does
        "if(out.textContent.length>2000000)out.textContent=out.textContent.slice(-1000000);" +
        "if(end||d.reset)out.scrollTop=out.scrollHeight;" +
        "}catch(e){}finally{busy=false;}}" +
        "document.getElementById('send').addEventListener('submit',e=>{e.preventDefault();if(!running)return;post({type:'input',id,text:line.value});line.value='';setTimeout(poll,150);});" +
        "stop.addEventListener('click',()=>post({type:'stop',id}));" +
        "document.getElementById('clear').addEventListener('click',()=>{out.textContent='';});" +
        // The browser tells the start pages when a program starts or stops: this page listens too
        "window.chrome.webview.addEventListener('message',e=>{if(e.data&&e.data.type==='projects')poll();});" +
        "poll();setInterval(()=>{if(!document.hidden)poll();},700);" +
        "</script></body></html>";
}
