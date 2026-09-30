using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace LiteBro;

/// <summary>
/// The console of a project: its program's output, live from logs\&lt;id&gt;.log, a line to type into the program,
/// and commands run by cmd in the project's folder (their output goes to the same log).
/// Served on the start page's host under /log/&lt;id&gt;, so it may post web messages; the page fetches what the log gained.
/// </summary>
static class ProgramLog
{
    const string Prefix = "/log/";
    // A long log opens with its end; each poll brings at most this much more
    const int FirstBytes = 256 << 10, MaxBytes = 512 << 10;
    // Like the start page, and the page may fetch from its own host
    public const string Headers = "Content-Type: text/html; charset=utf-8\r\nContent-Security-Policy: default-src 'none'; " +
        "script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'\r\n" + Home.Isolation;
    public const string JsonHeaders = "Content-Type: application/json; charset=utf-8\r\nCache-Control: no-store\r\n" + Home.Isolation;

    public static string Url(Project p) => Home.Url + "log/" + p.Id;

    /// <summary>The console of no project: PowerShell in the user's folder, opened from the start page's button.</summary>
    public static readonly Project Shell = new() { Id = "shell", Name = "PowerShell" };
    public static bool IsShell(Project p) => p.Id == Shell.Id;

    /// <summary>A project's console, or the one of no project.</summary>
    public static Project? Find(string? id) => id == Shell.Id ? Shell : ProjectStore.Find(id);

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
        return Find(rest);
    }

    /// <summary>
    /// What the log gained since a byte offset (-1 for the first time), up to its last whole line, so a letter of
    /// several bytes is never cut. A log shorter than the offset was started anew with the program.
    /// </summary>
    public static string Chunk(Project p, long from, bool running, Launcher launcher)
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
            ["command"] = launcher.CommandRunning,
            ["dir"] = launcher.CommandDir,
        });
    }

    public static Stream Bytes(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    static string H(string s) => WebUtility.HtmlEncode(s);
    static string Js(string s) => ProjectStore.Json.Serialize(s).Replace("<", "\\u003c");

    public static string Page(Project p) =>
        L.T("<!doctype html><html><head><meta charset=utf-8><title>Консоль: ") + H(p.Name) + "</title><style>" +
        ":root{color-scheme:light dark;--muted:rgba(127,127,127,.9);--line:rgba(127,127,127,.3);--accent:#4d6bfe}" +
        "html,body{margin:0;height:100%;background:Canvas;color:CanvasText;font:14px 'Segoe UI',sans-serif}" +
        "body{display:flex;flex-direction:column}" +
        "header{display:flex;align-items:center;gap:12px;padding:10px 14px;border-bottom:1px solid var(--line)}" +
        "h1{font-size:16px;font-weight:600;margin:0;white-space:nowrap}" +
        "#state{color:var(--muted);white-space:nowrap}#state.on{color:#37c46a}" +
        "#path{color:var(--muted);font:12px Consolas,monospace;margin-left:auto;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}" +
        "button{font:13px 'Segoe UI',sans-serif;padding:4px 12px;border:1px solid var(--line);border-radius:6px;background:transparent;color:inherit;cursor:pointer;white-space:nowrap}" +
        "button:hover{background:rgba(127,127,127,.15)}" +
        "#out{flex:1;margin:0;padding:10px 14px;overflow:auto;white-space:pre-wrap;word-break:break-word;font:13px/1.45 Consolas,monospace}" +
        "form{display:flex;gap:8px;align-items:center;padding:8px 14px;border-top:1px solid var(--line)}" +
        "#mode{padding:4px 10px}#mode.cmd{border-color:var(--accent);color:var(--accent)}" +
        "#prompt{font:13px Consolas,monospace;color:var(--muted);field-sizing:content;min-width:6ch;max-width:45%;padding:5px 6px;border:1px solid transparent;border-radius:6px;background:transparent;outline:none}" +
        "#prompt:hover{border-color:var(--line)}#prompt:focus{border-color:var(--accent);color:inherit}#prompt:disabled{opacity:.6}#prompt[hidden]{display:none}" +
        "#gt{font:13px Consolas,monospace;color:var(--muted);margin-left:-6px}" +
        "#line{flex:1;font:13px Consolas,monospace;padding:6px 8px;border:1px solid var(--line);border-radius:6px;background:transparent;color:inherit;outline:none}" +
        "#line:focus{border-color:var(--accent)}#line:disabled{opacity:.5}" +
        "</style></head><body>" +
        "<header><h1>" + H(p.Name) + "</h1><span id=state></span>" +
        L.T("<button id=stop hidden>Остановить программу</button><button id=halt hidden>Прервать команду</button><button id=clear>Очистить экран</button><button id=term title='PowerShell в папке команд: работают claude, vim и другие программы с экраном. Ctrl+клик или колёсико — в новой вкладке'>Терминал</button>") +
        "<span id=path title='" + H(LogPath(p.Id)) + "'>" + H(LogPath(p.Id)) + "</span></header>" +
        "<pre id=out></pre>" +
        "<form id=send><button type=button id=mode></button><input id=prompt autocomplete=off spellcheck=false><span id=gt>&gt;</span>" +
        "<input id=line autocomplete=off spellcheck=false></form>" +
        "<script>" +
        "const id=" + Js(p.Id) + ",shell=" + (IsShell(p) ? "true" : "false") + ",$=s=>document.getElementById(s),out=$('out'),line=$('line'),state=$('state'),stopBtn=$('stop'),halt=$('halt'),mode=$('mode'),promptEl=$('prompt');" +
        "let from=-1,running=false,busy=false,cmd=true,command=false,dir='',typed=[],back=0;" +
        "const post=m=>window.chrome.webview.postMessage(m);" +
        // Two ways to type: a command for cmd in the console's folder, or a line to the running program
        L.T("function modeShow(){mode.textContent=cmd?'Команда':'Программе';mode.className=cmd?'cmd':'';") +
        L.T("mode.title=cmd?'Строка выполняется в cmd в папке ниже; cd меняет папку. Нажмите, чтобы писать программе проекта':'Строка уходит на ввод программе проекта. Нажмите, чтобы выполнять команды';") +
        "promptEl.hidden=!cmd;if(document.activeElement!==promptEl)promptEl.value=dir;promptEl.disabled=command&&!shell;" +
        L.T("promptEl.title=promptEl.disabled?'Папку можно сменить, когда команда закончится':'Папка команд: исправьте и нажмите Enter, Esc — отменить';") +
        L.T("line.placeholder=cmd?(command?'Команда выполняется: строка уйдёт ей на ввод':'Команда, например: git status, npm install, dir'):(running?'Строка для программы, Enter — отправить':'Программа не запущена');") +
        L.T("line.disabled=!cmd&&!running;mode.hidden=shell;if(shell)line.placeholder=command?'Команда выполняется: следующая встанет в очередь':'Команда PowerShell, например: Get-ChildItem, git status, winget list';}") +
        "mode.addEventListener('click',()=>{cmd=!cmd;modeShow();line.focus();});" +
        // The folder is edited in place: Enter goes there, Esc or leaving the field puts the current one back
        "promptEl.addEventListener('keydown',e=>{if(e.key==='Enter'){e.preventDefault();const v=promptEl.value.trim();" +
        "if(v&&v!==dir){post({type:'cd',id,dir:v});setTimeout(poll,150);}line.focus();}" +
        "else if(e.key==='Escape'){e.preventDefault();promptEl.value=dir;line.focus();}});" +
        "promptEl.addEventListener('blur',()=>{promptEl.value=dir;});" +
        L.T("function show(d){running=d.running;command=d.command;dir=d.dir;state.textContent=shell?(command?'● команда выполняется':''):running?'● программа работает':'программа остановлена';state.className=running?'on':'';") +
        "stopBtn.hidden=!running;halt.hidden=!command;modeShow();}" +
        "async function poll(){if(busy)return;busy=true;try{" +
        "const r=await fetch(location.pathname+'/text?from='+from,{cache:'no-store'});const d=await r.json();" +
        "const end=out.scrollHeight-out.scrollTop-out.clientHeight<24;" +
        "if(d.reset)out.textContent='';if(d.text)out.append(d.text);from=d.size;show(d);" +
        // Only a long log is cut: the page keeps the last lines, as a terminal does
        "if(out.textContent.length>2000000)out.textContent=out.textContent.slice(-1000000);" +
        "if(end||d.reset)out.scrollTop=out.scrollHeight;" +
        "}catch(e){}finally{busy=false;}}" +
        "$('send').addEventListener('submit',e=>{e.preventDefault();const t=line.value;" +
        "if(cmd){if(!t.trim())return;post({type:'command',id,text:t});if(!command&&typed[typed.length-1]!==t)typed.push(t);back=typed.length;}" +
        "else{if(!running)return;post({type:'input',id,text:t});}" +
        "line.value='';out.scrollTop=out.scrollHeight;setTimeout(poll,150);});" +
        // Up and down go through the commands typed before, as in cmd
        "line.addEventListener('keydown',e=>{if(!cmd||(e.key!=='ArrowUp'&&e.key!=='ArrowDown'))return;e.preventDefault();" +
        "back=Math.max(0,Math.min(typed.length,back+(e.key==='ArrowUp'?-1:1)));line.value=typed[back]||'';});" +
        "stopBtn.addEventListener('click',()=>post({type:'stop',id}));" +
        "halt.addEventListener('click',()=>post({type:'stopCommand',id}));" +
        "$('clear').addEventListener('click',()=>{out.textContent='';});" +
        "$('term').addEventListener('click',e=>post({type:'terminal',id,newTab:e.ctrlKey}));" +
        "$('term').addEventListener('mousedown',e=>{if(e.button===1)e.preventDefault();});" +
        "$('term').addEventListener('auxclick',e=>{if(e.button===1){e.preventDefault();post({type:'terminal',id,newTab:true});}});" +
        // The browser tells the start pages when a program starts or stops: this page listens too
        "window.chrome.webview.addEventListener('message',e=>{if(e.data&&e.data.type==='projects')poll();});" +
        "modeShow();poll();setInterval(()=>{if(!document.hidden)poll();},700);line.focus();" +
        "</script></body></html>";
}
