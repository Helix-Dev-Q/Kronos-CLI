using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using SdImage = System.Drawing.Image;

namespace KronosCLI;

internal sealed class Launcher(Options options) : IDisposable
{
    static readonly string[] ProcessName =
    [
        "FortniteClient-Win64-Shipping_EAC",
        "FortniteClient-Win64-Shipping_EAC_EOS",
        "FortniteClient-Win64-Shipping_BE",
        "FortniteClient-Win64-Shipping",
		"FortniteClient-Win32-Shipping",
		"FortniteLauncher",
    ];

    
    const string ContentSplashUrl = "https://raw.githubusercontent.com/Helix-Dev-Q/random-stuff-for-my-repos/refs/heads/main/kronoy.bmp";
    const string EacSplashUrl = "https://raw.githubusercontent.com/Helix-Dev-Q/random-stuff-for-my-repos/refs/heads/main/Kronos%20(1).png";

    readonly List<GameSession> sessions = [];
    readonly Dictionary<string, IntPtr> ProcessesFN = new(StringComparer.OrdinalIgnoreCase);
    readonly Image images = new(options.BuildPath);

    RedirectProxy? proxy = BuildVersion.Read(options) is { UsesProxy: false } ? null : RedirectProxy.Start(options);
    RedirectProxy? liveProxy;
    IslandContent? islands;
    BuildManifests? manifests;
    StreamedVideos? videos;
    readonly EpicLauncher epic = new();
    GameSession? eacSession;
    bool proxyUnavailableWarned;
    bool eacInstalled;
    bool disposed;

    public bool Reversing => liveProxy?.Capture != null;

    public bool Running
    {
        get { lock (sessions) return sessions.Any(s => !s.HasEnded); }
    }

    public event Action? StateChanged;

    public bool EpicLauncherNeedsRestart => EpicLauncher.IsRunning && !epic.IsOurs;

    public void StartReversal(bool launch)
    {
        liveProxy ??= RedirectProxy.StartLive(options);
        if (liveProxy == null)
        {
            Log.Error("[Kronos Reversal] the proxy couldn't start, so nothing can be captured");
            return;
        }

        Reversal capture;
        try
        {
            capture = new Reversal(AppContext.BaseDirectory);
        }
        catch (Exception e)
        {
            Log.Error($"[Kronos Reversal] couldn't create the output folder: {e.Message}");
            return;
        }

        liveProxy.StartCapture(capture);
        Log.Ok($"[Kronos Reversal] capturing to {capture.Directory}");
        Log.Warn("[Kronos Reversal] this captures your REAL Fortnite traffic while EAC runs - Epic may flag the account, so use a throwaway");
        Log.Warn("[Kronos Reversal] saved files can hold your own account tokens - don't share them");
        if (FortniteRunning())
            Log.Warn("[Kronos Reversal] Fortnite is already running - only Fortnite started from the Epic Games Launcher Kronos opens can be captured, so close it and start it again");

        if (epic.IsOurs)
        {
            Log.Info("[Kronos Reversal] the Epic Games Launcher is already running with the Fortnite-only proxy");
            if (launch) EpicLauncher.Open(EpicLauncher.LaunchFortniteUrl);
        }
        else
        {
            EpicLauncher.Close();
            if (!epic.Start($"http://127.0.0.1:{liveProxy.Port}", launch ? EpicLauncher.LaunchFortniteUrl : null))
            {
                liveProxy.StopCapture();
                images.RestoreAll();
                return;
            }
            Log.Ok("[Kronos Reversal] started the Epic Games Launcher with a Fortnite-only proxy - other apps aren't affected");
        }

        Log.Info(launch
            ? "[Kronos Reversal] the Epic Games Launcher will start Fortnite - log in if it asks"
            : "[Kronos Reversal] now start Fortnite from the Epic Games Launcher");
        WatchForFortnite();
        islands = new IslandContent(capture.Directory);
        manifests = new BuildManifests(capture, options.BuildRoot);
        videos = new StreamedVideos(capture.Directory);
        WatchContent(capture, islands, manifests, videos);
    }

    void WatchContent(Reversal capture, IslandContent content, BuildManifests builds, StreamedVideos clips)
    {
        new Thread(() =>
        {
            while (!disposed && liveProxy?.Capture == capture)
            {
                try
                {
                    builds.Collect(final: false);
                    clips.Collect(final: false);
                    content.Collect(final: false);
                }
                catch (Exception e)
                {
                    Log.Detail($"[Kronos Reversal] content download pass failed: {e.Message}");
                }
                for (int i = 0; i < 10 && !disposed && liveProxy?.Capture == capture; i++) Thread.Sleep(1000);
            }
        })
        { IsBackground = true, Name = "content downloads" }.Start();
    }

    IEnumerable<string> SplashDirectories() =>
    [
        Path.Combine(options.BuildPath, "FortniteGame", "Content", "Splash"),
        Path.Combine(options.BuildPath, "FortniteGame", "Binaries", "Win64", "EasyAntiCheat"),
        .. options.ReplaceDirs.Select(rd => Path.IsPathRooted(rd) ? rd : Path.Combine(options.BuildPath, rd)),
    ];

    void SwapSplash()
    {
        try
        {
            foreach (string dir in SplashDirectories())
                images.SwapDirectory(dir, dir.EndsWith("EasyAntiCheat", StringComparison.OrdinalIgnoreCase) ? EacSplashUrl : ContentSplashUrl);
        }
        catch (Exception e)
        {
            Log.Warn($"Preparing image swaps failed: {e.Message}");
        }
    }

    public void RestoreLeftoverImages() => Image.RestoreLeftovers(SplashDirectories());

    public void StopReversal()
    {
        if (liveProxy?.Capture is not { } capture) return;
        liveProxy.StopCapture();
        images.RestoreAll();
        try
        {
            manifests?.Collect(final: true);
            videos?.Collect(final: true);
            islands?.Collect(final: true);
        }
        catch (Exception e)
        {
            Log.Warn($"[Kronos Reversal] the last content downloads failed: {e.Message}");
        }
        Log.Ok($"[Kronos Reversal] stopped - {capture.Count} file(s), {manifests?.Count ?? 0} build manifest file(s), " +
               $"{videos?.Count ?? 0} video(s) and {islands?.Count ?? 0} island(s) in {capture.Directory}");
        islands = null;
        manifests = null;
        videos = null;

        if (!epic.IsOurs) return;
        if (FortniteRunning())
            Log.Info("[Kronos Reversal] Fortnite is still running through Kronos (nothing is saved now) - the Epic Games Launcher goes back to normal when you close Kronos");
        else
            epic.Restore();
    }

    void WatchForFortnite()
    {
        new Thread(() =>
        {
            while (!disposed && liveProxy?.Capture != null)
            {
                if (FortniteRunning())
                {
                    Log.Ok("[Kronos Reversal] a Fortnite process is running - capturing whatever it sends through the proxy");
                    return;
                }
                Thread.Sleep(1000);
            }
        })
        { IsBackground = true, Name = "reversal watch" }.Start();
    }

    static bool FortniteRunning() =>
        Process.GetProcessesByName("FortniteClient-Win64-Shipping").Length > 0 ||
        Process.GetProcessesByName("FortniteClient-Win32-Shipping").Length > 0;

    public GameSession? StartClient(bool useProxy) =>
        Start("Client", options.Username, options.Password, options.ClientDll, [], useProxy);

    public GameSession? StartServer() =>
        Start("Server", options.EffectiveServerUsername, options.EffectiveServerPassword, options.ServerDll,
            ["-nosplash", "-nosound", "-nothreadtimeout", .. options.Headless ? ["-nullrhi"] : (string[])[]], false);

    GameSession? Start(string role, string login, string password, string lobbyDll, string[] roleArgs, bool useProxy)
    {
        if (useProxy && eacSession is { HasEnded: false })
        {
            Log.Warn($"[{role}] EAC only allows one game at a time and a proxy client is already running (Stop Everything, 4, closes it)");
            return null;
        }

        string gameExe = options.GameExeFor(useProxy);
        if (!File.Exists(gameExe))
        {
            Log.Error($"[{role}] Game not found: {gameExe}");
            return null;
        }

        string? redirectDll = useProxy ? null : options.RedirectDll == "" ? null : options.RedirectDll;
        if (useProxy)
        {
            proxy ??= RedirectProxy.Start(options);
            WarnIfProxyMissing();
            EnsureEacInstalled();
            EnsureEacSettings();
        }
        else if (redirectDll == null)
            Log.Warn($"[{role}] No redirect DLL is set (Settings (5) > DLLs) - traffic won't be redirected");

        string caldera = Caldera.Token(options, role);
        string[] args =
        [
            "-epicapp=Fortnite",
            "-epicenv=Prod",
            "-epiclocale=en-us",
            "-epicportal",
            "-skippatchcheck",
            "-nobe",
            .. useProxy ? ["-noeac", "-fromfl=eaceos"] : (string[])["-fromfl=eac", "-fltoken=3db3ba5dcbd2e16703f3978d"],
            $"-caldera={caldera}",
            "-AUTH_TYPE=epic",
            useProxy ? $"-httpproxy={options.Proxy}" : $"-backend={options.Backend}",
            $"-AUTH_LOGIN={login}",
            $"-AUTH_PASSWORD={password}",
            .. roleArgs,
            .. options.GameArgs,
        ];
        string argLine = string.Join(' ', args.Select(Quote));
        var startedAt = DateTime.Now;

        if (useProxy)
        {
            images.RestoreAll();
            Log.Detail($"[{role}] Splash images left as they are - EAC checks the game's files");
        }
        else
        {
            SwapSplash();
        }

        var version = BuildVersion.Read(options);
        bool? legacyLogin = version?.UsesLegacyLogin;
        bool helpersFirst = version is not { UsesProxy: true };
        if (helpersFirst) EnsureProcessesFN(useProxy);

        Process? process = redirectDll != null
            ? StartInjected(role, gameExe, argLine, redirectDll)
            : StartNormally(role, gameExe, argLine, useProxy ? options.Proxy : null);
        if (process == null)
        {
            if (helpersFirst && !Running) KillProcessesFN();
            return null;
        }

        string mode = useProxy ? "EAC + proxy" : redirectDll != null ? "no EAC + redirect DLL" : "no EAC";
        if (role == "Server") mode += options.Headless ? ", headless" : ", windowed";
        Log.Ok($"[{role}] Started as {login} ({mode}, PID {process.Id})");
        Log.Detail($"[{role}] \"{gameExe}\" " + string.Join(' ',
            args.Select(a => a.StartsWith("-AUTH_PASSWORD=") ? "-AUTH_PASSWORD=***"
                : a.StartsWith("-caldera=") ? "-caldera=<token>"
                : Quote(a))));

        Uri? redirectBackend = redirectDll != null && Uri.TryCreate(options.Backend, UriKind.Absolute, out var backend) ? backend : null;
        var session = new GameSession(role, process, login, startedAt, useProxy ? "" : lobbyDll, useProxy || redirectDll != null,
            legacyLogin, args, redirectBackend, () => Task.Run(() => StateChanged?.Invoke()));
        lock (sessions)
        {
            sessions.RemoveAll(s => s.HasEnded);
            sessions.Add(session);
        }
        if (useProxy) eacSession = session;

        if (!helpersFirst) EnsureProcessesFN(useProxy);
        return session;
    }

    Process? StartNormally(string role, string gameExe, string argLine, string? proxyEndpoint)
    {
        var process = new Process
        {
            StartInfo =
            {
                FileName = gameExe,
                Arguments = argLine,
                WorkingDirectory = options.Win64Dir,
                UseShellExecute = false,
            },
        };

        RedirectProxy.ApplyTo(process.StartInfo.Environment, proxyEndpoint != null ? $"http://{proxyEndpoint}" : null);
        process.StartInfo.Environment["OPENSSL_ia32cap"] = "~0x20000000";

        try
        {
            process.Start();
            return process;
        }
        catch (Exception e)
        {
            Log.Error($"[{role}] Could not start {gameExe}: {e.Message}");
            return null;
        }
    }

    Process? StartInjected(string role, string gameExe, string argLine, string dll)
    {
        string name = Path.GetFileName(dll);
        if (!File.Exists(dll))
        {
            Log.Error($"[{role}] Redirect DLL not found: {dll}");
            return null;
        }

        var process = StartNormally(role, gameExe, argLine, null);
        if (process == null) return null;

        WaitForInit(process);
        try
        {
            Injector.Inject(process.Handle, dll);
            Log.Ok($"[{role}] Injected redirect DLL {name}");
        }
        catch (Exception e)
        {
            Log.Error($"[{role}] Injecting redirect DLL {name} failed: {e.Message}");
        }
        return process;
    }

    static void WaitForInit(Process process)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            try
            {
                process.Refresh();
                if (process.HasExited) return;
                if (process.Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase)))
                {
                    Thread.Sleep(250);
                    return;
                }
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
            Thread.Sleep(50);
        }
    }

    void WarnIfProxyMissing()
    {
        if (proxyUnavailableWarned || proxy != null || ProxyListening()) return;
        Log.Warn($"Proxy chosen but nothing is listening at {options.Proxy} - the game won't be redirected");
        proxyUnavailableWarned = true;
    }

    bool ProxyListening()
    {
        int colon = options.Proxy.LastIndexOf(':');
        if (colon < 0 || !int.TryParse(options.Proxy[(colon + 1)..], out int port)) return false;
        try
        {
            using var tcp = new TcpClient();
            return tcp.ConnectAsync(options.Proxy[..colon], port).Wait(1000) && tcp.Connected;
        }
        catch { return false; }
    }

    void EnsureEacInstalled()
    {
        if (eacInstalled) return;

        string dir = Path.Combine(options.Win64Dir, "EasyAntiCheat");
        string setup = Path.Combine(dir, "EasyAntiCheat_EOS_Setup.exe");
        if (!File.Exists(setup))
        {
            Log.Warn($"EAC installer not found: {setup}");
            eacInstalled = true;
            return;
        }

        try
        {
            var process = new Process
            {
                StartInfo =
                {
                    FileName = setup,
                    Arguments = "install fn",
                    WorkingDirectory = dir,
                    UseShellExecute = true,
                    Verb = "runas",
                },
            };
            process.Start();
            process.WaitForExit();
            eacInstalled = true;

            if (process.ExitCode == 0) Log.Ok("EAC installed (EasyAntiCheat_EOS_Setup install fn)");
            else Log.Warn($"EAC installer exited with 0x{process.ExitCode:X} - EAC may not be installed");
        }
        catch (Exception e)
        {
            Log.Error($"Running the EAC installer failed: {e.Message}");
        }
    }

    void EnsureEacSettings()
    {
        string dir = Path.Combine(options.Win64Dir, "EasyAntiCheat");
        string settings = Path.Combine(dir, "Settings.json");
        if (File.Exists(settings)) return;

        try
        {
            File.Copy(Path.Combine(dir, "DefaultSettings.json"), settings);
            Log.Ok($"Created {settings} from DefaultSettings.json");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"EAC needs {settings} and it couldn't be created: {e.Message}");
        }
    }

    void EnsureProcessesFN(bool eac)
    {
        if (!eac)
        {
            string? antiCheat = new[] { "_EAC", "_EAC_EOS", "_BE" }
                .Select(suffix => Path.Combine(options.Win64Dir, $"FortniteClient-Win64-Shipping{suffix}.exe"))
                .FirstOrDefault(File.Exists);
            if (antiCheat != null) StartSuspended(antiCheat);
            else Log.Warn($"Skipping the anti-cheat: no _EAC/_EAC_EOS/_BE exe in {options.Win64Dir}");
        }
        StartSuspended(Path.Combine(options.Win64Dir, "FortniteLauncher.exe"));
    }

    void StartSuspended(string exe)
    {
        string name = Path.GetFileName(exe);
        if (ProcessesFN.ContainsKey(exe)) return;
        if (!File.Exists(exe))
        {
            Log.Warn($"Skipping {name}: not found in {Path.GetDirectoryName(exe)}");
            return;
        }

        var startup = new Native.STARTUPINFOW { cb = Marshal.SizeOf<Native.STARTUPINFOW>() };
        if (!Native.CreateProcessW(null, new StringBuilder($"\"{exe}\""), IntPtr.Zero, IntPtr.Zero, false,
                Native.CREATE_SUSPENDED, IntPtr.Zero, Path.GetDirectoryName(exe), ref startup, out var info))
        {
            Log.Error($"{name} failed to start: {new Win32Exception().Message}");
            return;
        }

        Native.CloseHandle(info.hThread);
        ProcessesFN[exe] = info.hProcess;
        Log.Detail($"{name} suspended (PID {info.dwProcessId})");
    }

    public void KillProcessesFN()
    {
        foreach (var handle in ProcessesFN.Values)
        {
            Native.TerminateProcess(handle, 0);
            Native.CloseHandle(handle);
        }
        ProcessesFN.Clear();
    }

    public void KillEverything()
    {
        List<GameSession> running;
        lock (sessions) running = [.. sessions];
        foreach (var session in running) session.MarkStopping();

        bool killedAny = ProcessesFN.Count > 0;
        KillProcessesFN();
        foreach (var name in ProcessName)
            killedAny |= KillByName(name);

        foreach (var session in running) session.WaitForExit(TimeSpan.FromSeconds(3));
        lock (sessions) sessions.Clear();
        eacSession = null;

        StopReversal();
        images.RestoreAll();

        if (killedAny) Log.Ok("Killed everything");
        else Log.Info("Nothing was running");
    }

    static bool KillByName(string name)
    {
        bool killedAny = false;
        foreach (var process in Process.GetProcessesByName(name))
        {
            try
            {
                process.Kill(true);
                process.WaitForExit(5000);
                killedAny = true;
            }
            catch { }
            finally { process.Dispose(); }
        }
        return killedAny;
    }

    static string Quote(string arg) => arg.Contains(' ') && !arg.StartsWith('"') ? $"\"{arg}\"" : arg;

    public void RestartProxy()
    {
        proxy?.Dispose();
        proxy = RedirectProxy.Start(options);
        proxyUnavailableWarned = false;
    }

    public void Dispose()
    {
        lock (sessions)
        {
            if (disposed) return;
            disposed = true;
        }
        liveProxy?.StopCapture();
        if (epic.IsOurs)
        {
            if (FortniteRunning())
                Log.Warn("[Kronos Reversal] Fortnite is still running through Kronos and will lose its connection now that Kronos is closing");
            epic.Restore();
        }
        images.RestoreAll();
        KillProcessesFN();
        proxy?.Dispose();
        liveProxy?.Dispose();
    }
}

internal sealed class GameSession
{
    static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved", "Logs");

    static readonly HashSet<string> ClaimedLogs = new(StringComparer.OrdinalIgnoreCase);

    static readonly TimeSpan LogTimeout = TimeSpan.FromSeconds(90);

    static readonly Regex LinePrefix = new(@"^\[[\d.:-]+\]\[\s*\d+\]", RegexOptions.Compiled);

    const string LoadMap = "LogLoad: LoadMap: ";
    const string LoginStats = "[AutoLoginStats] [Result=";
    const string ContinueLoggingIn = "[UOnlineAccountCommon::ContinueLoggingIn]";

    static readonly Regex CrashReporterStarted = new(@"Started CrashReportClient \(pid=(\d+)\)", RegexOptions.Compiled);
    static readonly Regex ConnectRefused = new(@"connect to (\S+) port (\d+) failed: Connection refused", RegexOptions.Compiled);

    readonly string role, login, lobbyDll;
    readonly Process process;
    readonly DateTime startedAt;
    readonly bool redirectExpected;
    readonly bool? legacyLogin;
    readonly HashSet<string> args;
    readonly Uri? redirectBackend;
    readonly Action? onExit;
    readonly Stopwatch uptime = Stopwatch.StartNew();
    readonly Thread watcher;

    string? logFile;
    string? loginError;
    bool loggedIn;
    bool loginShown;
    bool loginFailed;
    bool hosting;
    bool wrongBackendWarned;
    volatile bool stopping;
    volatile bool ended;

    public GameSession(string role, Process process, string login, DateTime startedAt, string lobbyDll, bool redirectExpected,
        bool? legacyLogin, IEnumerable<string> args, Uri? redirectBackend, Action? onExit = null)
    {
        this.role = role;
        this.process = process;
        this.login = login;
        this.startedAt = startedAt;
        this.lobbyDll = lobbyDll;
        this.redirectExpected = redirectExpected;
        this.legacyLogin = legacyLogin;
        this.args = new HashSet<string>(args.Select(Unquote), StringComparer.OrdinalIgnoreCase);
        this.redirectBackend = redirectBackend;
        this.onExit = onExit;

        watcher = new Thread(Watch) { IsBackground = true, Name = $"{role} log" };
        watcher.Start();
    }

    public bool HasEnded => ended || !watcher.IsAlive;

    public void MarkStopping() => stopping = true;

    public bool WaitForExit(TimeSpan? timeout = null) => watcher.Join(timeout ?? Timeout.InfiniteTimeSpan);

    void Watch()
    {
        try
        {
            logFile = WaitForLog();
            if (logFile != null)
            {
                Log.Detail($"[{role}] Following {Path.GetFileName(logFile)}");
                Follow(logFile);
            }

            process.WaitForExit();
            ReportExit();
        }
        catch (Exception e)
        {
            Error($"Log watcher failed: {e.Message}");
        }
        finally
        {
            if (logFile != null) lock (ClaimedLogs) ClaimedLogs.Remove(logFile);
            ended = true;
            onExit?.Invoke();
        }
    }

    string? WaitForLog()
    {
        while (!process.HasExited)
        {
            if (FindLog() is { } file) return file;
            if (uptime.Elapsed > LogTimeout)
            {
                Warn($"No game log for this launch showed up in {LogDir}, so only the exit will be reported");
                return null;
            }
            Thread.Sleep(500);
        }
        return null;
    }

    string? FindLog()
    {
        if (!Directory.Exists(LogDir)) return null;

        foreach (var file in Directory.GetFiles(LogDir, "FortniteGame*.log"))
        {
            if (file.Contains("-backup-")) continue;
            lock (ClaimedLogs) if (ClaimedLogs.Contains(file)) continue;

            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                if (!IsFromThisRun(file, reader.ReadLine())) continue;

                int? crashReporter = null;
                for (int i = 0; i < 5000 && reader.ReadLine() is { } line; i++)
                {
                    if (CrashReporterStarted.Match(line) is { Success: true } started && int.TryParse(started.Groups[1].Value, out int pid))
                        crashReporter = pid;
                    if (!line.Contains("Command Line:")) continue;
                    if (!IsOurs(line, crashReporter)) break;
                    lock (ClaimedLogs)
                        if (!ClaimedLogs.Add(file)) break;
                    return file;
                }
            }
            catch (IOException) { }
        }
        return null;
    }

    bool IsOurs(string commandLine, int? crashReporter)
    {
        string[] parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part.StartsWith("-AUTH_LOGIN=", StringComparison.OrdinalIgnoreCase)))
            return parts.Contains($"-AUTH_LOGIN={login}");

        if (crashReporter is { } pid && ProcessScope.ParentOf(pid) is { } parent)
            return parent == process.Id || ProcessScope.ParentOf(parent) == process.Id;

        var shown = parts.SkipWhile(part => part != "Line:").Skip(1).Select(Unquote).ToList();
        return shown.Count > 0 && shown.All(args.Contains);
    }

    static string Unquote(string arg) => arg.Trim('"');

    bool IsFromThisRun(string file, string? firstLine)
    {
        const string stamp = "Log file open, ";
        var earliest = startedAt.AddSeconds(-2);

        if (firstLine != null && firstLine.StartsWith(stamp) &&
            DateTime.TryParseExact(firstLine[stamp.Length..].Trim(), "MM/dd/yy HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var opened))
            return opened >= earliest;

        return File.GetLastWriteTime(file) >= earliest;
    }

    void Follow(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var pending = new StringBuilder();
        var buffer = new char[64 * 1024];

        while (true)
        {
            int read = reader.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                if (process.HasExited) return;
                Thread.Sleep(250);
                continue;
            }

            pending.Append(buffer, 0, read);
            string text = pending.ToString();
            int lastNewline = text.LastIndexOf('\n');
            if (lastNewline < 0) continue;
            pending.Clear().Append(text, lastNewline + 1, text.Length - lastNewline - 1);

            foreach (var line in text[..lastNewline].Split('\n'))
                HandleLine(line.TrimEnd('\r'));
        }
    }

    void HandleLine(string line)
    {
        if (line.Contains("HttpProxyAddress = '"))
            Info($"Game is using HTTP proxy {Between(line, "HttpProxyAddress = '", "'")}");
        else if (line.Contains("bUseHttpProxy = false") && !redirectExpected)
            Warn("Game is not using an HTTP proxy, so its traffic won't be redirected");
        else if (line.Contains("ProcessUserLogin] Successfully logged in"))
            OnSignedIn(Field(line, "DisplayName"), Field(line, "UserId"));
        else if (line.Contains("HandleLoginChanged") && line.Contains("LoginStatus=[LoggedIn]"))
            OnSignedIn(login, Field(line, "LocalUserId"));
        else if (line.Contains(ContinueLoggingIn) && line.Contains("(Completed)"))
            OnLoginCompleted();
        else if (line.Contains(LoadMap))
            Info($"Loading map {line[(line.IndexOf(LoadMap, StringComparison.Ordinal) + LoadMap.Length)..].Split('?')[0]}");
        else if (line.Contains("[login_failed]"))
            loginError = Field(line, "DetailedError");
        else if (line.Contains(LoginStats))
            OnLoginStats(Field(line, "Result") ?? "unknown");
        else if (!loginFailed && line.Contains("Login request failed. Failure ErrorCode="))
            ReportLoginFailure(Between(line, "ErrorCode=", ","));
        else if (!loginFailed && line.Contains("HandleLoginRequestComplete") && line.Contains("AuthFailed"))
            ReportLoginFailure(Between(line, "[Result=", "]"));
        else if (!hosting && line.Contains("IpNetDriver listening on port "))
            OnHosting(line);
        else if (line.Contains("Join succeeded: "))
            Ok($"{After(line, "Join succeeded: ")} joined");
        else if (line.Contains("Fatal error"))
            Error(Shorten(LinePrefix.Replace(line, "")));
        else if (!wrongBackendWarned && redirectBackend != null && ConnectRefused.Match(line) is { Success: true } refused)
            OnConnectRefused(refused.Groups[1].Value, int.Parse(refused.Groups[2].Value));
    }

    void OnConnectRefused(string host, int port)
    {
        if (port == redirectBackend!.Port && host.Equals(redirectBackend.Host, StringComparison.OrdinalIgnoreCase)) return;
        wrongBackendWarned = true;
        Warn($"The game is trying to reach {host}:{port}, but your backend is at {redirectBackend.Host}:{redirectBackend.Port}. " +
             "Your redirect DLL sends traffic to its own address - use one that points at your backend, or run the backend on that port");
    }

    void ReportLoginFailure(string? code)
    {
        loginFailed = true;
        Error($"Login rejected ({code ?? "unknown"}) - the backend refused this login. Check the account/password on your backend, or the redirect DLL isn't reaching it (it may have hit real Epic).");
    }

    void OnHosting(string line)
    {
        hosting = true;
        string port = new string(After(line, "listening on port ").TakeWhile(char.IsDigit).ToArray());
        Ok($"Hosting on port {(port.Length > 0 ? port : "?")}");
    }

    static string After(string line, string marker)
    {
        int i = line.IndexOf(marker, StringComparison.Ordinal);
        return i < 0 ? "" : line[(i + marker.Length)..].Trim();
    }

    void OnSignedIn(string? name, string? id)
    {
        ReportLogin(name, id);
        if (legacyLogin != true) OnLoginReady();
    }

    void OnLoginCompleted()
    {
        if (legacyLogin == false) return;
        ReportLogin(login, null);
        OnLoginReady();
    }

    void OnLoginReady()
    {
        if (loggedIn) return;

        loggedIn = true;
        Ok($"Logged in after {Format(uptime.Elapsed)}");
        if (lobbyDll != "") InjectDll();
    }

    void OnLoginStats(string result)
    {
        if (result == "Success") return;
        Error(loginError != null ? $"Login failed ({result}): {loginError}" : $"Login failed ({result})");
    }

    void ReportLogin(string? name, string? id)
    {
        if (loginShown) return;
        loginShown = true;

        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(id) && name != id)
            Ok($"Logged in as {name} ({id})");
        else
            Ok($"Logged in as {name ?? id ?? login}");
    }

    void InjectDll()
    {
        string name = Path.GetFileName(lobbyDll);
        try
        {
            Injector.Inject(process.Handle, lobbyDll);
            Ok($"Injected lobby DLL {name}");
        }
        catch (Exception e)
        {
            Error($"Injecting lobby DLL {name} failed: {e.Message}");
        }
    }

    void ReportExit()
    {
        string after = $"after {Format(uptime.Elapsed)}";
        string code = $"code 0x{process.ExitCode:X}";

        if (stopping) Info($"Stopped {after}");
        else if (loggedIn) Info($"Exited with {code} {after}");
        else if (logFile == null) Error($"Exited with {code} {after}, before writing a log");
        else Error($"Exited with {code} {after}, before reaching the lobby");
    }

    void Info(string message) => Log.Info($"[{role}] {message}");
    void Ok(string message) => Log.Ok($"[{role}] {message}");
    void Warn(string message) => Log.Warn($"[{role}] {message}");
    void Error(string message) => Log.Error($"[{role}] {message}");

    static string? Between(string line, string open, string close)
    {
        int start = line.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return null;
        start += open.Length;
        int end = line.IndexOf(close, start, StringComparison.Ordinal);
        return end < 0 ? null : line[start..end];
    }

    static string? Field(string line, string name)
    {
        int start = line.IndexOf($"{name}=", StringComparison.Ordinal);
        if (start < 0) return null;
        start += name.Length + 1;
        if (start < line.Length && line[start] == '[') start++;

        int depth = 1;
        for (int i = start; i < line.Length; i++)
        {
            if (line[i] == '[') depth++;
            else if (line[i] == ']' && --depth == 0) return line[start..i];
        }
        return null;
    }

    static string Shorten(string text) => text.Length > 200 ? text[..200] + "..." : text;

    static string Format(TimeSpan time) =>
        time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes}m {time.Seconds}s" : $"{time.Seconds}s";
}

internal sealed class Image
{
    readonly string buildRoot;
    readonly List<SwapRecord> records = [];
    readonly object sync = new();

    sealed class SwapRecord
    {
        public required string Target { get; init; }
        public string? Backup { get; init; }
        public required string Temp { get; init; }
    }

    public Image(string buildRoot) => this.buildRoot = buildRoot ?? string.Empty;

    public void SwapDirectory(string dir, string imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            Log.Warn("Image: no image URL given");
            return;
        }

        if (string.IsNullOrWhiteSpace(dir)) dir = buildRoot;
        else if (!Path.IsPathRooted(dir)) dir = Path.Combine(buildRoot, dir);

        if (!Directory.Exists(dir))
        {
            Log.Detail($"Image: directory not found, skipping: {dir}");
            return;
        }

        var targets = Directory.EnumerateFiles(dir)
            .Where(IsSwappable)
            .Where(f => !AlreadySwapped(f))
            .ToList();
        if (targets.Count == 0)
        {
            Log.Detail($"Image: no .bmp/.png files to replace in {dir}");
            return;
        }

        byte[] data;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            data = client.GetByteArrayAsync(imageUrl).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Log.Warn($"Image: download failed ({imageUrl}): {e.Message}");
            return;
        }

        foreach (var target in targets)
            SwapFile(target, data);
    }

    void SwapFile(string target, byte[] data)
    {
        string ext = Path.GetExtension(target).ToLowerInvariant();
        string temp = Path.Combine(Path.GetTempPath(), "fl_" + Path.GetRandomFileName() + ext);

        try
        {
            var (width, height) = OriginalSize(target);
            using var stream = new MemoryStream(data);
            using var source = new Bitmap(stream);
            Convert(source, width > 0 ? width : source.Width, height > 0 ? height : source.Height, ext, temp);
        }
        catch (Exception e)
        {
            Log.Warn($"Image: couldn't decode the downloaded image for {Path.GetFileName(target)}: {e.Message}");
            TryDelete(temp);
            return;
        }

        try
        {
            string? backup = null;
            if (File.Exists(target))
            {
                backup = target + ".flbak";
                if (File.Exists(backup)) backup = target + ".flbak." + DateTime.Now.Ticks;
                File.Move(target, backup);
            }

            File.Copy(temp, target, overwrite: true);
            lock (sync) records.Add(new SwapRecord { Target = target, Backup = backup, Temp = temp });
            Log.Detail($"Image: replaced {target}" + (backup != null ? $" (backup {Path.GetFileName(backup)})" : ""));
        }
        catch (UnauthorizedAccessException)
        {
            Log.Warn($"Image: can't write {target} - run the launcher as administrator to replace files under {Path.GetDirectoryName(target)}");
            TryDelete(temp);
        }
        catch (Exception e)
        {
            Log.Warn($"Image: couldn't replace {Path.GetFileName(target)}: {e.Message}");
            TryDelete(temp);
        }
    }

    static void Convert(Bitmap source, int width, int height, string ext, string temp)
    {
        bool png = ext == ".png";
        using var canvas = new Bitmap(width, height, png ? PixelFormat.Format32bppArgb : PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        }
        canvas.Save(temp, png ? ImageFormat.Png : ImageFormat.Bmp);
    }

    static (int Width, int Height) OriginalSize(string path)
    {
        try
        {
            if (!File.Exists(path)) return (0, 0);
            using var stream = File.OpenRead(path);
            using var image = SdImage.FromStream(stream, false, false);
            return (image.Width, image.Height);
        }
        catch { return (0, 0); }
    }

    public void RestoreAll()
    {
        lock (sync)
        {
            for (int i = records.Count - 1; i >= 0; i--)
            {
                var r = records[i];
                try
                {
                    if (r.Backup == null)
                    {
                        if (File.Exists(r.Target)) File.Delete(r.Target);
                    }
                    else if (File.Exists(r.Backup))
                    {
                        File.Move(r.Backup, r.Target, overwrite: true);
                    }
                }
                catch (Exception e)
                {
                    Log.Warn($"Image: couldn't restore {Path.GetFileName(r.Target)}: {e.Message}");
                }
                TryDelete(r.Temp);
            }
            records.Clear();
        }
    }

    public static void RestoreLeftovers(IEnumerable<string> dirs)
    {
        foreach (string dir in dirs.Where(Directory.Exists))
        {
            foreach (string backup in Directory.EnumerateFiles(dir, "*.flbak"))
            {
                string target = backup[..^".flbak".Length];
                try
                {
                    File.Move(backup, target, overwrite: true);
                    Log.Info($"Image: restored {target} from a backup an earlier run left behind");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"Image: couldn't restore {Path.GetFileName(target)} from {Path.GetFileName(backup)}: {e.Message}");
                }
            }
        }
    }

    bool AlreadySwapped(string file)
    {
        lock (sync) return records.Any(r => string.Equals(r.Target, file, StringComparison.OrdinalIgnoreCase));
    }

    static bool IsSwappable(string file) => Path.GetExtension(file).ToLowerInvariant() is ".bmp" or ".png";

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
