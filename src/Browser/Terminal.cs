using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace LiteBro;

/// <summary>
/// A real terminal for a tab: PowerShell in a Windows pseudo console (ConPTY, Windows 10 1809 and later),
/// so programs that draw the whole screen (claude, vim, menus with arrows) work as in Windows Terminal.
/// The page draws it with xterm.js; what is typed comes here, what the console prints goes there.
/// </summary>
sealed class Terminal : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct Coord
    {
        public short X, Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    const uint ExtendedStartupInfoPresent = 0x00080000, CreateUnicodeEnvironment = 0x00000400;
    static readonly IntPtr PseudoConsoleAttribute = (IntPtr)0x00020016; // PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE
    const uint Infinite = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CreatePipe(out IntPtr read, out IntPtr write, IntPtr attributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr console);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern int ResizePseudoConsole(IntPtr console, Coord size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern void ClosePseudoConsole(IntPtr console);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size,
        IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(string? application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory,
        ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    IntPtr console, process;
    readonly FileStream input, output;
    readonly KillOnCloseJob? job;
    int disposed;

    /// <summary>Text the console printed, UTF-8 decoded; raised on a thread of its own.</summary>
    public event Action<string>? Output;
    /// <summary>The shell ended (exit typed, or it was closed); raised on a thread of its own.</summary>
    public event Action? Exited;

    Terminal(IntPtr console, IntPtr process, int processId, FileStream input, FileStream output)
    {
        this.console = console;
        this.process = process;
        this.input = input;
        this.output = output;
        try { job = KillOnCloseJob.For(Process.GetProcessById(processId)); }
        catch (Exception) { } // it ended at once
    }

    /// <summary>Starts a command line (PowerShell) in a pseudo console of the given size; null with the reason when it cannot.</summary>
    public static Terminal? Start(string commandLine, string directory, int columns, int rows, out string? error)
    {
        error = null;
        IntPtr inRead = IntPtr.Zero, inWrite = IntPtr.Zero, outRead = IntPtr.Zero, outWrite = IntPtr.Zero;
        IntPtr pc = IntPtr.Zero, list = IntPtr.Zero;
        try
        {
            if (!CreatePipe(out inRead, out inWrite, IntPtr.Zero, 0) || !CreatePipe(out outRead, out outWrite, IntPtr.Zero, 0))
            {
                error = "Не удалось создать каналы: " + Marshal.GetLastWin32Error();
                return null;
            }
            int hr = CreatePseudoConsole(Size(columns, rows), inRead, outWrite, 0, out pc);
            if (hr != 0)
            {
                // Before Windows 10 1809 there is no pseudo console
                error = "Windows не дал создать терминал (нужна Windows 10 1809 или новее), код " + hr;
                return null;
            }
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            list = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size)
                || !UpdateProcThreadAttribute(list, 0, PseudoConsoleAttribute, pc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                error = "Не удалось подключить терминал: " + Marshal.GetLastWin32Error();
                return null;
            }
            var info = new StartupInfoEx { lpAttributeList = list };
            info.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            if (!CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment, IntPtr.Zero, directory, ref info, out var pi))
            {
                error = "Не удалось запустить " + commandLine + ": " + Marshal.GetLastWin32Error();
                return null;
            }
            CloseHandle(pi.hThread);
            // The console holds its own ends of the pipes now
            CloseHandle(inRead);
            CloseHandle(outWrite);
            inRead = outWrite = IntPtr.Zero;
            var t = new Terminal(pc, pi.hProcess, pi.dwProcessId,
                new FileStream(new SafeFileHandle(inWrite, true), FileAccess.Write, 1),
                new FileStream(new SafeFileHandle(outRead, true), FileAccess.Read, 1));
            pc = inWrite = outRead = IntPtr.Zero;
            return t;
        }
        finally
        {
            if (list != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(list);
                Marshal.FreeHGlobal(list);
            }
            foreach (var h in new[] { inRead, inWrite, outRead, outWrite })
                if (h != IntPtr.Zero) CloseHandle(h);
            if (pc != IntPtr.Zero) ClosePseudoConsole(pc);
        }
    }

    static Coord Size(int columns, int rows) => new()
    {
        X = (short)Math.Max(2, Math.Min(1000, columns)),
        Y = (short)Math.Max(1, Math.Min(500, rows)),
    };

    /// <summary>Starts reading what the console prints and waiting for the shell to end; after the events are hooked up.</summary>
    public void Begin()
    {
        new Thread(Read) { IsBackground = true, Name = "Terminal output" }.Start();
        new Thread(() =>
        {
            WaitForSingleObject(process, Infinite);
            Thread.Sleep(200); // the last of its output is still on the way
            Exited?.Invoke();
        }) { IsBackground = true, Name = "Terminal process" }.Start();
    }

    void Read()
    {
        var decoder = new UTF8Encoding(false).GetDecoder(); // keeps a letter split between two reads
        var bytes = new byte[16384];
        var chars = new char[16384 + 4];
        try
        {
            int n;
            while ((n = output.Read(bytes, 0, bytes.Length)) > 0)
            {
                int c = decoder.GetChars(bytes, 0, n, chars, 0);
                if (c > 0) Output?.Invoke(new string(chars, 0, c));
            }
        }
        catch (Exception) { } // closed
    }

    /// <summary>What was typed or pasted, as the page's terminal encodes keys.</summary>
    public void Write(string text)
    {
        if (disposed != 0) return;
        try
        {
            var b = Encoding.UTF8.GetBytes(text);
            input.Write(b, 0, b.Length);
            input.Flush();
        }
        catch (Exception) { } // the shell is gone
    }

    public void Resize(int columns, int rows)
    {
        if (disposed == 0) ResizePseudoConsole(console, Size(columns, rows));
    }

    /// <summary>Ends the shell and everything it started, and frees the console.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        job?.Dispose();
        // Closing the console may wait for its output to be read: not on the window's thread
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { input.Dispose(); } catch (Exception) { }
            ClosePseudoConsole(console);
            try { output.Dispose(); } catch (Exception) { }
            CloseHandle(process);
        });
    }
}

/// <summary>The page of a terminal tab, served on the start page's host under /term, so it may post web messages.</summary>
static class TermPage
{
    public const string Url = Home.Url + "term";
    // Its own scripts only; xterm.js writes styles of its own
    const string Policy = "Content-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'";
    static readonly Dictionary<string, (string Resource, string Type)> Files = new()
    {
        ["/term"] = ("term/index.html", "text/html"),
        ["/term/term.js"] = ("term/term.js", "text/javascript"),
        ["/term/xterm.js"] = ("term/xterm.js", "text/javascript"),
        ["/term/addon-fit.js"] = ("term/addon-fit.js", "text/javascript"),
        ["/term/xterm.css"] = ("term/xterm.css", "text/css"),
    };
    static readonly Dictionary<string, byte[]> cache = new();

    public static bool Is(string? uri) =>
        Home.Is(uri) && new Uri(uri!).AbsolutePath == "/term";

    /// <summary>A file of the page by its path, with the headers to serve it; null for another path.</summary>
    public static Stream? File(string path, out string headers)
    {
        headers = "";
        if (!Files.TryGetValue(path, out var f)) return null;
        if (!cache.TryGetValue(path, out var bytes))
        {
            using var stream = typeof(TermPage).Assembly.GetManifestResourceStream(f.Resource);
            if (stream == null) return null;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            cache[path] = bytes = copy.ToArray();
        }
        headers = "Content-Type: " + f.Type + "; charset=utf-8\r\n" + Policy;
        return new MemoryStream(bytes, writable: false);
    }
}
