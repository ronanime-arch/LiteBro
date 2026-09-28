using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LiteBrowser;

/// <summary>A tile on the start page: a local site and, optionally, the program that serves it.</summary>
sealed class Project
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>Tile colour, #rrggbb.</summary>
    public string Color { get; set; } = "";
    /// <summary>The tile's picture: a file name in the icons folder, empty for letters on the colour.</summary>
    public string Icon { get; set; } = "";
    /// <summary>Where the picture comes from: "site" (its own icon, found by itself), "file" (picked), "none".</summary>
    public string IconSource { get; set; } = "site";
    /// <summary>Started when Url does not answer; empty = just open Url.</summary>
    public string Exe { get; set; } = "";
    public string Args { get; set; } = "";
    /// <summary>Empty = the program's own folder.</summary>
    public string WorkDir { get; set; } = "";
    /// <summary>Open the address the program prints (one with a login token, say) instead of Url.</summary>
    public bool OpenPrintedUrl { get; set; }
}

/// <summary>projects.json next to settings.ini: the start page's tiles, in order.</summary>
static class ProjectStore
{
    public static readonly JavaScriptSerializer Json = new() { MaxJsonLength = int.MaxValue };
    static readonly string FilePath = Path.Combine(Settings.Dir, "projects.json");
    static List<Project>? list;

    public static List<Project> All => list ??= Load();

    static List<Project> Load()
    {
        if (!File.Exists(FilePath)) return new();
        try { return Json.Deserialize<List<Project>>(File.ReadAllText(FilePath, Encoding.UTF8)) ?? new(); }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
        {
            // Unreadable (edited by hand, say): kept aside rather than overwritten by the next save
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch (IOException) { }
            return new();
        }
        catch (IOException) { return new(); }
    }

    public static Project? Find(string? id) => id == null ? null : All.FirstOrDefault(p => p.Id == id);

    /// <summary>Replaces the project with the same Id, or adds it at the end with a new Id.</summary>
    public static void Save(Project project)
    {
        int index = project.Id.Length > 0 ? All.FindIndex(p => p.Id == project.Id) : -1;
        if (index >= 0) All[index] = project;
        else
        {
            project.Id = Guid.NewGuid().ToString("N");
            All.Add(project);
        }
        Write();
    }

    public static void Delete(string id)
    {
        if (All.RemoveAll(p => p.Id == id) > 0) Write();
    }

    static void Write()
    {
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, Json.Serialize(All), new UTF8Encoding(false));
        if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
        else File.Move(temp, FilePath);
    }
}

/// <summary>
/// Runs a project's program: no window, its output in a log, and in a job so that it dies with
/// LiteBrowser, even if the browser crashes. Its output goes to a pipe of this process, so it cannot
/// outlive the browser anyway: the first line it writes after that would kill it.
/// </summary>
sealed class Launcher
{
    static readonly Regex UrlPattern = new(@"https?://[^\s""'<>]+");
    static readonly Regex AnsiPattern = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]");

    public Project Project { get; set; }
    readonly Action changed;
    readonly List<string> printed = new();
    Process? process;
    KillOnCloseJob? job;
    StreamWriter? log;
    Task<string?>? starting;

    /// <param name="changed">Called from any thread when the program starts or stops.</param>
    public Launcher(Project project, Action changed)
    {
        Project = project;
        this.changed = changed;
    }

    public string LogPath => Path.Combine(Settings.Dir, "logs", Project.Id + ".log");

    /// <summary>Where the program runs: the folder given, else the program's own.</summary>
    public string WorkDir => Project.WorkDir.Trim().Length > 0 ? Expand(Project.WorkDir)
        : ResolveExe(Expand(Project.Exe)) is { } exe ? Path.GetDirectoryName(exe)! : "";
    public bool Running => process is { HasExited: false };

    public static async Task<bool> IsUpAsync(Uri url)
    {
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(url.DnsSafeHost); }
        catch (Exception) { return false; }
        foreach (var address in addresses)
        {
            using var client = new TcpClient(address.AddressFamily);
            var connect = client.ConnectAsync(address, url.Port);
            await Task.WhenAny(connect, Task.Delay(700));
            if (connect.Status == TaskStatus.RanToCompletion) return true;
            _ = connect.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        return false;
    }

    /// <returns>null once the address answers, otherwise the reason it did not.</returns>
    public Task<string?> StartAndWaitAsync(Uri url) =>
        starting is { IsCompleted: false } ? starting : (starting = RunAsync(url));

    async Task<string?> RunAsync(Uri url)
    {
        if (!Running)
        {
            var error = Start();
            if (error != null) return error;
        }
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            if (await IsUpAsync(url))
            {
                // The line with the address and the open port come within moments of each other
                for (int i = 0; i < 25 && Project.OpenPrintedUrl && PrintedUrl(url) == null && Running; i++)
                    await Task.Delay(200);
                return null;
            }
            var p = process;
            if (p == null) return "Программа остановлена.";
            if (p.HasExited) return $"Программа завершилась с кодом {p.ExitCode}, так и не открыв порт {url.Port}.";
            await Task.Delay(400);
        }
        return $"За 2 минуты программа так и не открыла порт {url.Port}.";
    }

    /// <summary>What to open: the address the program printed for this port, if asked for, else the project's.</summary>
    public string AddressToOpen(Uri url) => (Project.OpenPrintedUrl ? PrintedUrl(url) : null) ?? url.AbsoluteUri;

    /// <summary>An address the program printed on this port; one with a login token first.</summary>
    string? PrintedUrl(Uri url)
    {
        lock (printed)
        {
            var mine = printed.Where(u => Uri.TryCreate(u, UriKind.Absolute, out var p) && p.Port == url.Port).ToList();
            return mine.FirstOrDefault(u => u.IndexOf("token=", StringComparison.OrdinalIgnoreCase) >= 0) ?? mine.FirstOrDefault();
        }
    }

    string? Start()
    {
        var exe = ResolveExe(Expand(Project.Exe));
        if (exe == null) return "Не найдена программа: " + Project.Exe;
        var dir = WorkDir;
        if (!Directory.Exists(dir)) return "Не найдена рабочая папка: " + dir;
        var args = Environment.ExpandEnvironmentVariables(Project.Args.Trim());

        var info = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Started from a desktop shortcut, PATH may lack the program's folder, and what it starts may need it
        info.Environment["PATH"] = Path.GetDirectoryName(exe) + ";" + Environment.GetEnvironmentVariable("PATH");
        info.Environment["NO_COLOR"] = "1";
        // Python holds back piped output; the address it prints must arrive at once
        info.Environment["PYTHONUNBUFFERED"] = "1";
        info.Environment["PYTHONIOENCODING"] = "utf-8";

        lock (printed) printed.Clear();
        log?.Dispose();
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        log = new StreamWriter(new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        log.WriteLine($"[{DateTime.Now:HH:mm:ss}] {exe} {args}   (папка: {dir})");

        var p = new Process { StartInfo = info, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => Write(e.Data);
        p.ErrorDataReceived += (_, e) => Write(e.Data);
        p.Exited += (_, _) => changed();
        try { p.Start(); }
        catch (Exception ex) { return "Не удалось запустить программу: " + ex.Message; }
        job?.Dispose();
        job = KillOnCloseJob.For(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        process = p;
        changed();
        return null;
    }

    static string Expand(string path) => Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));

    /// <summary>
    /// A full path, or a bare name such as npm or dsh looked up in PATH the way cmd does: with the extensions
    /// Windows runs, never as a file without one (pi-node keeps a bash script "dsh" beside dsh.cmd).
    /// </summary>
    static string? ResolveExe(string name)
    {
        if (name.Length == 0) return null;
        if (File.Exists(name)) return Path.GetFullPath(name);
        if (name.IndexOfAny(new[] { '\\', '/' }) >= 0) return null;
        var runnable = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        // python3.11 has a dot but no extension Windows runs: it is looked up as python3.11.exe and so on
        var extensions = runnable.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)
            ? new[] { "" }
            : runnable;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(folder.Trim().Trim('"'), name + extension);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { } // a malformed PATH entry
            }
        }
        return null;
    }

    void Write(string? line)
    {
        if (line == null) return;
        line = AnsiPattern.Replace(line, "");
        foreach (Match m in UrlPattern.Matches(line))
        {
            var text = m.Value.TrimEnd('.', ',', ';', ')', ']', '\'', '"');
            // Servers print 0.0.0.0 or [::] ("all interfaces"), which cannot be opened as is
            if (Uri.TryCreate(text, UriKind.Absolute, out var u)) lock (printed) printed.Add(Router.Normalize(u).AbsoluteUri);
        }
        var w = log;
        if (w == null) return;
        lock (w)
        {
            try { w.WriteLine(line); }
            catch (ObjectDisposedException) { }
        }
    }

    public string LogTail(int lines = 25)
    {
        try
        {
            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var all = new StreamReader(stream).ReadToEnd().Split('\n');
            return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines))).TrimEnd();
        }
        catch (IOException) { return ""; }
    }

    /// <summary>Stops the program with everything it started.</summary>
    public void Stop()
    {
        var p = process;
        process = null;
        if (job != null)
        {
            job.Dispose();
            job = null;
        }
        else if (p is { HasExited: false })
        {
            try { p.Kill(); } catch (Exception) { }
        }
        if (p != null)
        {
            try { p.WaitForExit(3000); } catch (Exception) { }
            p.Dispose();
        }
        var w = log;
        log = null;
        if (w != null) lock (w) w.Dispose();
        changed();
    }
}
