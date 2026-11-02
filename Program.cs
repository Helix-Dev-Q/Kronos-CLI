using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace KronosCLI;

internal static class Program
{
    static readonly Ui.Button Keep = new("Keep", "");

    static readonly Ui.MenuItem[] MainItems =
    [
        new("1", "Launch Client", "Start Fortnite and log in to your backend. This is the one you want to play.", "Client"),
        new("2", "Launch Server", "Start a gameserver that logs in with the server account. It's headless (no window) unless you turn that off in Settings.", "Server"),
        new("3", "Launch Both", "Start a client and a gameserver together, both without the proxy.", "Both"),
        new("4", "Stop Everything", "Close every running Fortnite process and capture, and clear that run's game and proxy logs. Setup and settings lines stay.", "Stop"),
        new("5", "Settings", "Your Fortnite build, backend address, login and DLLs.", "Settings"),
        new("6", "Advanced", "Live Reversing and the caldera token. You don't need these just to play.", "Advanced"),
        new("7", "Exit", "Close Kronos.", "Exit"),
    ];

    static readonly Ui.MenuItem[] SetupItems =
    [
        new("1", "Fortnite build", "Pick the folder that has FortniteGame inside it (or the game's exe)."),
        new("2", "Backend", "The address your backend runs on, like http://127.0.0.1:3551."),
        new("3", "Login", "The username and password your backend accepts."),
    ];

    static string BackKey => Ui.Active ? "Esc" : "Enter";

    static Launcher? active;
    static string nextStep = "";
    static bool? backendWasUp;
    static readonly object RefreshLock = new();

    static int Main(string[] argv)
    {
        Options options;
        try
        {
            options = Options.Parse(argv);
        }
        catch (ArgumentException e)
        {
            System.Console.Error.WriteLine(e.Message);
            System.Console.Error.WriteLine();
            return 1;
        }

        Log.Reset();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Ui.Stop();
            Log.Error($"Unexpected error: {e.ExceptionObject}");
        };
        SystemProxy.RestoreLeftover();
        EpicLauncher.RestoreLeftover();
        if (options.Mode != null && !HasGame(options))
        {
            Log.Error($"No Fortnite exe under {options.Win64Dir} - set the build with --path");
            return 1;
        }

        bool dashboard = options.Mode == null
            && !System.Console.IsInputRedirected && !System.Console.IsOutputRedirected
            && Console.TryEnableAnsi();
        if (dashboard) Ui.Start();

        try
        {
            var startup = Log.Essential();
            CheckSetup(options);
            using var launcher = new Launcher(options);
            active = launcher;
            launcher.StateChanged += () => { if (Ui.Active) Refresh(options); };
            launcher.RestoreLeftoverImages();
            startup.Dispose();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { launcher.Dispose(); Ui.Stop(); };
            System.Console.CancelKeyPress += (_, _) => { launcher.Dispose(); Ui.Stop(); };
            using var windowClosed = PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => { launcher.Dispose(); Ui.Stop(); });
            using var shuttingDown = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => { launcher.Dispose(); Ui.Stop(); });
            if (dashboard) WatchSetup(options);

            return options.Mode != null ? RunOnce(launcher, options) : RunMenu(launcher, options);
        }
        catch (Exception e)
        {
            Ui.Stop();
            Log.Error($"Unexpected error: {e}");
            if (!System.Console.IsInputRedirected)
            {
                System.Console.WriteLine("Press Enter to close.");
                System.Console.ReadLine();
            }
            return 1;
        }
        finally
        {
            if (dashboard) Ui.Stop();
        }
    }

    static int RunMenu(Launcher launcher, Options options)
    {
        FirstRunSetup(options);

        bool reprint = true;
        while (true)
        {
            ShowMainMenu(options, reprint);
            reprint = false;

            switch (Log.ReadCommand())
            {
                case "1":
                    StartClient(launcher, options);
                    break;
                case "2":
                    StartServer(launcher, options);
                    break;
                case "3":
                    StartBoth(launcher, options);
                    break;
                case "4":
                    Ui.ClearSession();
                    launcher.KillEverything();
                    break;
                case "5":
                    SettingsMenu(launcher, options);
                    reprint = true;
                    break;
                case "6":
                    AdvancedMenu(launcher, options);
                    reprint = true;
                    break;
                case "7" or null:
                    return 0;
                case "":
                    break;
                case "?":
                    ShowHelp();
                    reprint = true;
                    break;
                default:
                    Log.Warn(Ui.Active ? "Pick 1-7 (or use ↑↓ and Enter) - type ? for help" : "Pick 1-7 - type ? for help");
                    break;
            }
        }
    }

    static void ShowMainMenu(Options options, bool print)
    {
        var items = MainItems
            .Select(item => item.Key == "6" && active?.Reversing == true ? item with { Label = "Advanced (capturing)" } : item)
            .ToList();

        if (Ui.Active)
        {
            Ui.SetMenu("Main menu", items);
            _ = Task.Run(() => Refresh(options));
            return;
        }
        if (!print) return;

        ShowMenu("Main menu", items);
        Refresh(options);
        Log.Raw($"""
              Next: {nextStep}
              Type a number and press Enter (? for help)

            """);
    }

    static void ShowMenu(string title, IReadOnlyList<Ui.MenuItem> items, int? select = null)
    {
        if (Ui.Active)
        {
            Ui.SetMenu(title, items, select);
            return;
        }
        Log.Raw(Environment.NewLine + title + Environment.NewLine +
                string.Join(Environment.NewLine, items.Select(item => $"  {item.Key}  {item.Label,-18} {item.Description}")) +
                Environment.NewLine);
    }

    static void ShowHelp()
    {
        Log.Raw("""
            How to use Kronos:
              1. Start your backend (the private server Fortnite logs in to).
              2. Settings (5) > Build: choose your Fortnite build folder.
              3. Settings (5) > Backend and Login: your backend's address and an account it accepts.
              4. Launch Client (1), then watch ACTIVITY until it says "Logged in".
            """);
        if (Ui.Active)
            Log.Raw("""
                  Moving around: ↑↓ then Enter, a number then Enter, or click an item. Esc goes back.
                  Scrolling: mouse wheel or PgUp/PgDn. Tab switches between ACTIVITY and PROXY.
                """);
        Log.Raw($"  Everything is also written to {Log.FilePath}");
    }

    static void FirstRunSetup(Options options)
    {
        using var essential = Log.Essential();
        bool firstRun = !File.Exists(Options.SavedAt);
        if (!firstRun && HasGame(options)) return;

        if (!firstRun)
        {
            ShowMenu("Setup", SetupItems, 0);
            if (Log.Confirm($"No Fortnite build was found at {options.BuildPath}. Pick your build folder now?", true))
                ChangeBuild(options);
            return;
        }

        ShowMenu("Setup", SetupItems, 0);
        Log.Ok("Welcome to Kronos CLI!");
        Log.Info("Before you can play, Kronos needs 3 things: your Fortnite build, your backend's address, and a login.");
        if (!Log.Confirm("Set them up now? (takes a minute - you can change them later in Settings)", true))
        {
            options.Save();
            return;
        }

        Log.Info(HasGame(options)
            ? $"Step 1 of 3 - Fortnite build: found Fortnite at {options.BuildPath}. Press Enter to use it, or Browse… for a different build."
            : "Step 1 of 3 - Fortnite build: the folder that has FortniteGame inside it. Click Browse… to pick it.");
        ChangeBuild(options);

        ShowMenu("Setup", SetupItems, 1);
        Log.Info($"Step 2 of 3 - Backend: where your backend is running. Press Enter to keep {options.Backend}.");
        ChangeBackend(options);

        ShowMenu("Setup", SetupItems, 2);
        Log.Info("Step 3 of 3 - Login: the username and password your backend accepts.");
        ChangeLogin(options, includeServer: false);

        options.Save();
        Log.Ok("Setup done! Pick Launch Client (1) to play. You can change any of this later in Settings (5).");
    }

    static void StartClient(Launcher launcher, Options options)
    {
        if (BuildVersion.Read(options) is { UsesProxy: false } version)
        {
            Log.Info($"Version {version} is older than 30.10, so the client launches without the proxy");
            launcher.StartClient(false);
            return;
        }

        launcher.StartClient(Log.Confirm(
            "Launch through EAC with the built-in proxy? Recommended on 30.10+. Answer n for a patched exe or a redirect DLL (the client DLL only works without the proxy)",
            true));
    }

    static void StartServer(Launcher launcher, Options options)
    {
        if (ProxyAllowed(options))
            Log.Info("Servers always launch without the proxy - EAC would block the server lobby DLL");
        launcher.StartServer();
    }

    static void StartBoth(Launcher launcher, Options options)
    {
        if (ProxyAllowed(options))
            Log.Info("Launching both without the proxy - EAC allows only one game at a time and blocks DLL injection. For a proxy client, use Launch Client (1)");
        launcher.StartClient(false);
        launcher.StartServer();
    }

    static void SettingsMenu(Launcher launcher, Options options)
    {
        using var essential = Log.Essential();
        while (true)
        {
            ShowMenu("Settings", SettingsItems(options));

            switch (Log.ReadCommand())
            {
                case "1":
                    ChangeBuild(options);
                    break;
                case "2":
                    ChangeDlls(options);
                    break;
                case "3":
                    ChangeLogin(options);
                    break;
                case "4":
                    ChangeBackend(options);
                    break;
                case "5":
                    ToggleHeadless(options);
                    break;
                case "6" when ProxyAllowed(options):
                    ChangeProxy(launcher, options);
                    break;
                case "7" or "" or null:
                    return;
                case "?":
                    ShowHelp();
                    break;
                default:
                    Log.Warn(ProxyAllowed(options)
                        ? $"Pick 1-7, or {BackKey} to go back"
                        : $"Pick 1-5 or 7, or {BackKey} to go back - builds older than 30.10 don't use the proxy");
                    break;
            }
        }
    }

    static List<Ui.MenuItem> SettingsItems(Options options)
    {
        var items = new List<Ui.MenuItem>
        {
            new("1", "Build", $"Your Fortnite build folder. Now: {options.BuildPath}"),
            new("2", "DLLs", $"Optional DLLs Kronos injects. Now: redirect {DllName(options.RedirectDll)}, " +
                             $"client {DllName(options.ClientDll)}, server {DllName(options.ServerDll)}"),
            new("3", "Login", $"The accounts your backend accepts. Now: {options.Username} (server: {options.EffectiveServerUsername})"),
            new("4", "Backend", $"Your backend's address. Now: {options.Backend}"),
            new("5", "Headless Server", $"On: the gameserver runs without a window, like Reboot. Off: it opens a game window. Now: {(options.Headless ? "on" : "off")}"),
        };
        if (ProxyAllowed(options))
            items.Add(new("6", "Proxy", $"Where the built-in proxy listens - you rarely need to change this. Now: {options.Proxy}"));
        items.Add(new("7", "Back", $"Back to the main menu (or press {BackKey})."));
        return items;
    }

    static void AdvancedMenu(Launcher launcher, Options options)
    {
        while (true)
        {
            ShowMenu("Advanced", AdvancedItems(launcher, options));

            switch (Log.ReadCommand())
            {
                case "1":
                    LiveReversing(launcher);
                    return;
                case "2":
                    using (Log.Essential()) ChangeCaldera(options);
                    break;
                case "3" or "" or null:
                    return;
                case "?":
                    ShowHelp();
                    break;
                default:
                    Log.Warn($"Pick 1-3, or {BackKey} to go back");
                    break;
            }
        }
    }

    static List<Ui.MenuItem> AdvancedItems(Launcher launcher, Options options) =>
    [
        launcher.Reversing
            ? new("1", "Stop Live Reversing", "Stop capturing and put the Epic Games Launcher back to normal.")
            : new("1", "Live Reversing", "For backend developers: restarts the Epic Games Launcher with a capture proxy and saves real Fortnite traffic to Reversals\\. Use a throwaway account."),
        new("2", "Caldera Token", $"Passed on every launch. Kronos has a built-in one, so set this only to use your own. Now: {Caldera.Summary(options)}"),
        new("3", "Back", $"Back to the main menu (or press {BackKey})."),
    ];

    static void LiveReversing(Launcher launcher)
    {
        if (launcher.Reversing)
        {
            launcher.StopReversal();
            return;
        }

        Log.Info("Live Reversing records what real Fortnite gets from Epic, so you can rebuild it on your backend. " +
                 "It closes and reopens the Epic Games Launcher with a capture proxy (other apps aren't affected) and saves files to Reversals\\.");
        string? pick = Log.ReadValue(
            "[L]aunch Fortnite for you, or [T]arget the one you start from the Epic Games Launcher? ",
            buttons: [new("Launch", "launch"), new("Target", "target"), new("Cancel", "back")]);

        if (pick?.ToLowerInvariant() is not ("launch" or "l" or "target" or "t")) return;

        if (launcher.EpicLauncherNeedsRestart &&
            !Log.Confirm("The Epic Games Launcher is open. Live Reversing restarts it so only Fortnite goes through Kronos - restart it now?", true))
        {
            Log.Info("[Kronos Reversal] cancelled - the Epic Games Launcher was left alone");
            return;
        }

        launcher.StartReversal(launch: pick.ToLowerInvariant() is "launch" or "l");
    }

    static string DllName(string dll) => dll == "" ? "none" : Path.GetFileName(dll);

    static void Saved(Options options)
    {
        options.Save();
        if (Ui.Active) _ = Task.Run(() => Refresh(options));
    }

    static void ChangeBuild(Options options)
    {
        string? value = Log.ReadValue($"  Fortnite build folder [{options.BuildPath}]: ", example: @"e.g. C:\FortniteBuilds\42.30-CL-58813929",
            buttons: [new("Browse…", Action: () => Dialogs.PickFolder("Select your Fortnite build folder", options.BuildPath)), Keep]);
        if (value == null) return;

        options.BuildPath = value.Trim('"');
        if (HasGame(options))
            Log.Ok($"Build set to {options.BuildPath}");
        else
            Log.Warn($"Build set to {options.BuildPath}, but no Fortnite exe is under {options.Win64Dir} - pick the folder that has FortniteGame inside it", "build");
        ReportVersion(options);
        Saved(options);
    }

    static void ChangeDlls(Options options)
    {
        Log.Info("DLLs are optional. The redirect DLL sends traffic to your backend on launches without the proxy; " +
                 "lobby DLLs are injected once the game reaches the lobby.");
        options.RedirectDll = EditDll("Redirect DLL", options.RedirectDll);
        options.ClientDll = EditDll("Client lobby DLL", options.ClientDll);
        options.ServerDll = EditDll("Server lobby DLL", options.ServerDll);
        Saved(options);
    }

    static string EditDll(string label, string current)
    {
        string? value = Log.ReadValue($"  {label} [{(current == "" ? "none" : current)}] (- to clear): ",
            example: @"e.g. C:\Hera-Redirect\x64\Release\Hera-Redirect.dll",
            buttons: [new("Browse…", Action: () => Dialogs.PickFile($"Select the {label}", "DLL files", "*.dll", current)), new("Clear", "-"), Keep]);
        if (value == null) return current;
        if (value is "-" or "none")
        {
            Log.Ok($"{label} cleared");
            return "";
        }

        string full = Path.GetFullPath(value.Trim('"'));
        if (File.Exists(full)) Log.Ok($"{label} set to {full}");
        else Log.Warn($"{label} set to {full}, but it doesn't exist yet", "dlls");
        return full;
    }

    static void ChangeLogin(Options options, bool includeServer = true)
    {
        options.Username = Log.ReadValue($"  Username [{options.Username}]: ", example: "e.g. Player (any name your backend accepts)", buttons: [Keep]) ?? options.Username;
        options.Password = Log.ReadValue($"  Password [{new string('*', options.Password.Length)}]: ", secret: true, buttons: [Keep]) ?? options.Password;
        if (includeServer)
        {
            options.ServerUsername = Log.ReadValue($"  Server username [{options.ServerUsername}]: ", example: "e.g. Server (the account the gameserver logs in with)", buttons: [Keep]) ?? options.ServerUsername;
            options.ServerPassword = Log.ReadValue($"  Server password [{new string('*', options.ServerPassword.Length)}]: ", secret: true, buttons: [Keep]) ?? options.ServerPassword;
        }
        Log.Ok(includeServer ? $"Login set to {options.Username} (server: {options.EffectiveServerUsername})" : $"Login set to {options.Username}");
        Saved(options);
    }

    static void ChangeBackend(Options options)
    {
        string? value = Log.ReadValue($"  Backend address [{options.Backend}]: ", example: "e.g. http://127.0.0.1:3551", buttons: [Keep]);
        if (value == null) return;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            Log.Warn($"{value} isn't an http:// address - backend unchanged");
            return;
        }

        options.Backend = value;
        bool up = Reachable(value);
        lock (RefreshLock) backendWasUp = up;
        if (up) Log.Ok($"Backend set to {value}");
        else Log.Warn($"Backend set to {value}, but nothing is listening there yet - start your backend", "backend");
        Saved(options);
    }

    static void ToggleHeadless(Options options)
    {
        options.Headless = !options.Headless;
        Log.Ok(options.Headless
            ? "Headless server on - the gameserver runs without a window"
            : "Headless server off - the gameserver opens a game window");
        Saved(options);
    }

    static void ChangeProxy(Launcher launcher, Options options)
    {
        string? value = Log.ReadValue($"  Proxy [{options.Proxy}]: ", example: "e.g. 127.0.0.1:8888", buttons: [Keep]);
        if (value == null || value == options.Proxy) return;
        if (!IPEndPoint.TryParse(value, out var endpoint) || endpoint.Port == 0)
        {
            Log.Warn($"{value} isn't an ip:port - proxy unchanged");
            return;
        }

        options.Proxy = value;
        launcher.RestartProxy();
        Saved(options);
    }

    static void ChangeCaldera(Options options)
    {
        Log.Info("Every launch passes a caldera token. Kronos uses the one you set here, else the newest one Live Reversing saved, " +
                 "else its built-in one. Set a captured caldera_api_v1_launcher_racp.json, or the token itself (it starts with eyJ).");
        string current = options.Caldera == "" ? "automatic" : options.Caldera.Length > 48 ? options.Caldera[..47] + "…" : options.Caldera;
        string? value = Log.ReadValue($"  Caldera token or file [{current}] (- for automatic): ",
            example: @"e.g. Reversals\2026-10-20 03-57-37\caldera_api_v1_launcher_racp.json",
            buttons: [new("Browse…", Action: () => Dialogs.PickFile("Select a captured caldera_api_v1_launcher_racp.json", "JSON files", "*.json", options.Caldera)),
                new("Automatic", "-"), Keep]);
        if (value == null) return;

        string chosen = value is "-" or "none" or "auto" or "automatic" ? "" : value.Trim('"');
        if (chosen.Length > 0 && !Caldera.IsValid(chosen))
        {
            Log.Warn($"That isn't a caldera token (they start with eyJ) or a captured caldera_api_v1_launcher_racp.json - kept: {Caldera.Summary(options)}", "caldera");
            return;
        }

        options.Caldera = File.Exists(chosen) ? Path.GetFullPath(chosen) : chosen;
        Log.Resolve("caldera");
        Log.Ok($"Caldera token: {Caldera.Summary(options)}");
        Saved(options);
    }

    static int RunOnce(Launcher launcher, Options options)
    {
        var sessions = new List<GameSession?>();
        switch (options.Mode)
        {
            case "client":
                sessions.Add(launcher.StartClient(ProxyAllowed(options) && !options.NoProxy));
                break;
            case "server":
                sessions.Add(launcher.StartServer());
                break;
            case "both":
                sessions.Add(launcher.StartClient(false));
                sessions.Add(launcher.StartServer());
                break;
            default:
                Log.Error($"Unknown mode: {options.Mode} (expected client, server or both)");
                return 1;
        }

        Log.Info("Waiting for the game to exit");
        foreach (var session in sessions.OfType<GameSession>())
            session.WaitForExit();

        return sessions.Contains(null) ? 1 : 0;
    }

    static bool HasGame(Options options) =>
        File.Exists(options.GameExeFor(true)) || File.Exists(options.GameExeFor(false));

    static bool ProxyAllowed(Options options) => BuildVersion.Read(options) is not { UsesProxy: false };

    static void ReportVersion(Options options)
    {
        if (BuildVersion.Read(options) is not { } version)
            Log.Warn($"Version: unknown - no version found in {Path.GetFileName(options.GameExeFor(false))}, so you'll be asked whether to use the proxy", "version");
        else if (version.UsesProxy)
            Log.Ok($"Version: {version} - 30.10 or newer, so clients launch through EAC with the proxy (a patched exe doesn't need it)");
        else
            Log.Ok($"Version: {version} - older than 30.10, so clients launch without the proxy");
    }

    static void CheckSetup(Options options)
    {
        Log.Info($"Build:   {options.Win64Dir}");
        if (!HasGame(options))
            Log.Warn("Build:   no Fortnite exe here - set your build in Settings (5) > Build", "build");
        ReportVersion(options);
        Log.Info($"Log:     {Log.FilePath}");

        bool backendUp = Reachable(options.Backend);
        if (backendUp) Log.Ok($"Backend: listening at {options.Backend}");
        else Log.Warn($"Backend: nothing is listening at {options.Backend} - start it first", "backend");

        foreach (var dll in Dlls(options))
            if (!File.Exists(dll)) Log.Warn($"DLL not found: {dll}", "dlls");

        Refresh(options, backendUp);
    }

    static IEnumerable<string> Dlls(Options options) =>
        new[] { options.RedirectDll, options.ClientDll, options.ServerDll }.Where(dll => dll != "");

    static void WatchSetup(Options options) =>
        new Thread(() =>
        {
            while (Ui.Active)
            {
                Thread.Sleep(3000);
                try
                {
                    if (Ui.Active) Refresh(options);
                }
                catch (Exception e)
                {
                    Log.Detail($"Status check failed: {e.Message}");
                }
            }
        })
        { IsBackground = true, Name = "setup watch" }.Start();

    static void Refresh(Options options, bool? backendUp = null)
    {
        lock (RefreshLock) RefreshLocked(options, backendUp);
    }

    static void RefreshLocked(Options options, bool? backendUp)
    {
        using var essential = Log.Essential();
        bool up = backendUp ?? Reachable(options.Backend);
        bool? was = backendWasUp;
        backendWasUp = up;
        if (was == false && up) Log.Ok($"Backend: now answering at {options.Backend}");
        else if (was == true && !up) Log.Warn($"Backend: stopped answering at {options.Backend} - is it still running?", "backend");

        var version = BuildVersion.Read(options);
        var (text, level) = NextStep(options, version, up);
        nextStep = text;
        if (!Ui.Active) return;

        bool game = HasGame(options);
        if (game) Log.Resolve("build");
        if (version != null) Log.Resolve("version");
        if (up) Log.Resolve("backend");
        if (Dlls(options).All(File.Exists)) Log.Resolve("dlls");
        if (options.Caldera.Trim() == "" || Caldera.IsValid(options.Caldera)) Log.Resolve("caldera");
        bool proxy = version is not { UsesProxy: false };
        Ui.SetStatus("build", game ? ShortBuild(options) : "not found", game ? Ui.Level.Ok : Ui.Level.Warn);
        Ui.SetStatus("version", version?.Text ?? "unknown", version == null ? Ui.Level.Warn : Ui.Level.Info);
        Ui.SetStatus("backend", up ? options.Backend : $"{options.Backend} offline", up ? Ui.Level.Ok : Ui.Level.Warn);
        Ui.SetStatus("user", options.Username, Ui.Level.Plain);
        Ui.SetStatus("proxy", proxy ? options.Proxy : "off below 30.10", proxy ? Ui.Level.Info : Ui.Level.Detail);
        if (active?.Reversing == true) Ui.SetStatus("capture", "recording", Ui.Level.Ok);
        else Ui.RemoveStatus("capture");
        Ui.SetNext(text, level);
    }

    static (string Text, Ui.Level Level) NextStep(Options options, BuildVersion? version, bool backendUp)
    {
        if (active?.Reversing == true)
            return ("Live Reversing is capturing. When you're done: Advanced (6) > Stop Live Reversing", Ui.Level.Info);
        if (active?.Running == true)
            return ("Fortnite is running - follow it in ACTIVITY. Stop Everything (4) closes it", Ui.Level.Info);
        if (!HasGame(options))
            return ("Point Kronos at your Fortnite build: Settings (5) > Build", Ui.Level.Warn);
        if (!backendUp)
            return ($"Start your backend - nothing answers at {options.Backend}. Wrong address? Settings (5) > Backend", Ui.Level.Warn);
        return ("All set - pick Launch Client (1) to play", Ui.Level.Ok);
    }

    static string ShortBuild(Options options)
    {
        string path = options.BuildPath.TrimEnd('\\', '/');
        string leaf = Path.GetFileName(path);
        return string.IsNullOrEmpty(leaf) ? path : leaf;
    }

    static bool Reachable(string url)
    {
        try
        {
            var uri = new Uri(url);
            using var tcp = new TcpClient();
            return tcp.ConnectAsync(uri.Host, uri.Port).Wait(1500) && tcp.Connected;
        }
        catch { return false; }
    }
}
