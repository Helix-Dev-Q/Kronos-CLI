using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace KronosCLI;

internal sealed class Options
{
    static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KronosCLI", "config.json");

    sealed class Stored
    {
        public string? BuildPath { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? ServerUsername { get; set; }
        public string? ServerPassword { get; set; }
        public string? Backend { get; set; }
        public string? Proxy { get; set; }
        public string? RedirectDll { get; set; }
        [JsonPropertyName("ClientLobbyDll")] public string? ClientDll { get; set; }
        [JsonPropertyName("ServerLobbyDll")] public string? ServerDll { get; set; }
        public string? Caldera { get; set; }
        public bool? Headless { get; set; }
    }

    public string BuildPath { get; set; } = @"C:\Program Files\Epic Games\Fortnite";
    public string Username { get; set; } = "Player";
    public string Password { get; set; } = "Player123";
    public string ServerUsername { get; set; } = "Server";
    public string ServerPassword { get; set; } = "1234";
    public string Backend { get; set; } = "http://127.0.0.1:3551";
    public string Proxy { get; set; } = "127.0.0.1:8888";  // idk why you would change this , but it's here if you want to
    public string RedirectDll { get; set; } = "";
    public string ClientDll { get; set; } = "";
    public string ServerDll { get; set; } = "";
    public string Caldera { get; set; } = "";
    public bool Headless { get; set; } = true;

    public string? Mode { get; private set; }
    public bool NoProxy { get; private set; }
    public bool ShowHelp { get; private set; }
    public List<string> GameArgs { get; } = [];
    public List<string> ReplaceDirs { get; } = [];

    public string Win64Dir => string.IsNullOrWhiteSpace(BuildPath)
        ? string.Empty
        : File.Exists(BuildPath)
            ? Path.GetDirectoryName(Path.GetFullPath(BuildPath))!
            : File.Exists(Path.Combine(BuildPath, "FortniteClient-Win64-Shipping.exe"))
                ? BuildPath
                : Path.Combine(BuildPath, "FortniteGame", "Binaries", "Win64");

    public string GameExeFor(bool eac) => string.IsNullOrWhiteSpace(BuildPath)
        ? string.Empty
        : Path.Combine(Win64Dir,
            eac ? "FortniteClient-Win64-Shipping_EAC_EOS.exe" : "FortniteClient-Win64-Shipping.exe");

    public string BuildRoot
    {
        get
        {
            if (string.IsNullOrWhiteSpace(BuildPath)) return string.Empty;
            try { return Path.GetFullPath(Path.Combine(Win64Dir, "..", "..", "..")); }
            catch { return BuildPath; }
        }
    }

    public string EffectiveServerUsername => string.IsNullOrWhiteSpace(ServerUsername) ? "Server" : ServerUsername;
    public string EffectiveServerPassword => string.IsNullOrEmpty(ServerPassword) ? Password : ServerPassword;

    public static string SavedAt => ConfigPath;

    static string DetectBuildPath()
    {
        string[] candidates =
        [
            .. BuildManifests.InstalledFortnite() is { } installed ? [installed] : Array.Empty<string>(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Epic Games", "Fortnite"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games", "Fortnite"),
        ];
        return candidates.FirstOrDefault(dir =>
                   File.Exists(Path.Combine(dir, "FortniteGame", "Binaries", "Win64", "FortniteClient-Win64-Shipping.exe")))
               ?? candidates.FirstOrDefault(Directory.Exists)
               ?? candidates[^1];
    }

    bool LoadConfig()
    {
        bool buildSaved = false;
        try
        {
            if (!File.Exists(ConfigPath)) return false;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(ConfigPath));
            if (stored == null) return false;
            buildSaved = !string.IsNullOrWhiteSpace(stored.BuildPath);
            if (buildSaved) BuildPath = stored.BuildPath!;
            if (!string.IsNullOrWhiteSpace(stored.Username)) Username = stored.Username;
            if (!string.IsNullOrWhiteSpace(stored.Password)) Password = stored.Password;
            if (!string.IsNullOrWhiteSpace(stored.ServerUsername)) ServerUsername = stored.ServerUsername;
            if (stored.ServerPassword != null) ServerPassword = stored.ServerPassword;
            if (!string.IsNullOrWhiteSpace(stored.Backend)) Backend = stored.Backend;
            if (!string.IsNullOrWhiteSpace(stored.Proxy)) Proxy = stored.Proxy;
            if (stored.RedirectDll != null) RedirectDll = stored.RedirectDll;
            if (stored.ClientDll != null) ClientDll = stored.ClientDll;
            if (stored.ServerDll != null) ServerDll = stored.ServerDll;
            if (stored.Caldera != null) Caldera = stored.Caldera;
            if (stored.Headless != null) Headless = stored.Headless.Value;
        }
        catch { }
        return buildSaved;
    }

    public void Save()
    {
        try
        {
            var stored = new Stored
            {
                BuildPath = BuildPath,
                Username = Username,
                Password = Password,
                ServerUsername = ServerUsername,
                ServerPassword = ServerPassword,
                Backend = Backend,
                Proxy = Proxy,
                RedirectDll = RedirectDll,
                ClientDll = ClientDll,
                ServerDll = ServerDll,
                Caldera = Caldera,
                Headless = Headless,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            Log.Detail($"Config: couldn't save {ConfigPath}: {e.Message}");
        }
    }

    public static Options Parse(string[] argv)
    {
        var options = new Options();
        if (!options.LoadConfig()) options.BuildPath = DetectBuildPath();

        for (int i = 0; i < argv.Length; i++)
        {
            string Value() => i + 1 < argv.Length
                ? argv[++i]
                : throw new ArgumentException($"{argv[i]} needs a value.");

            switch (argv[i])
            {
                case "--mode": options.Mode = Value().ToLowerInvariant(); break;
                case "--path": options.BuildPath = Value(); break;
                case "--user": options.Username = Value(); break;
                case "--pass": options.Password = Value(); break;
                case "--server-user": options.ServerUsername = Value(); break;
                case "--server-pass": options.ServerPassword = Value(); break;
                case "--backend": options.Backend = Value(); break;
                case "--proxy": options.Proxy = Value(); break;
                case "--no-proxy": options.NoProxy = true; break;
                case "--redirect-dll": options.RedirectDll = Path.GetFullPath(Value()); break;
                case "--replace-dir": options.ReplaceDirs.Add(Value()); break;
                case "--client-lobby-dll": options.ClientDll = Path.GetFullPath(Value()); break;
                case "--server-lobby-dll": options.ServerDll = Path.GetFullPath(Value()); break;
                case "--caldera": options.Caldera = Value(); break;
                case "--": options.GameArgs.AddRange(argv[(i + 1)..]); i = argv.Length; break;
                default: options.GameArgs.Add(argv[i]); break;
            }
        }

        return options;
    }
}

internal sealed record BuildVersion(string Text, int Major, int Minor, string Source)
{
    public const string ProxyMinimum = "30.10";

    static readonly Regex CloudPattern = new(@"Release-((\d+)\.(\d+)[\d.]*(?:-CL-\d+)?)", RegexOptions.Compiled);

    static readonly Regex FullVersion = new(@"\d{1,3}\.\d{1,3}(?:\.\d{1,3})?-CL-\d{5,12}", RegexOptions.Compiled);
    static readonly Regex FullVersionUtf16 = new(
        @"(?:\d\0){1,3}\.\0(?:\d\0){1,3}(?:\.\0(?:\d\0){1,3})?-\0C\0L\0-\0(?:\d\0){5,12}", RegexOptions.Compiled);
    static readonly byte[] Marker = "-CL-"u8.ToArray();
    static readonly byte[] MarkerUtf16 = Encoding.Unicode.GetBytes("-CL-");
    const int ChunkSize = 16 * 1024 * 1024;
    const int Overlap = 128;

    static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KronosCLI", "versions.json");

    sealed record CachedScan(long Length, DateTime Written, string? Version);

    static readonly object ScanLock = new();
    static Dictionary<string, CachedScan>? scanned;

    public bool UsesProxy => Major > 30 || (Major == 30 && Minor >= 10);

    public bool UsesLegacyLogin => Major < 32 || (Major == 32 && Minor <= 11);

    public override string ToString() => Text;

    public static string CloudContentPath(Options options) =>
        Path.Combine(options.BuildRoot, "Cloud", "cloudcontent.json");

    const string CrashReporter = "CrashReportClient.exe";
    static readonly Regex VersionStart = new(@"^(\d{1,4})\.(\d{1,4})", RegexOptions.Compiled);

    static readonly Dictionary<int, string> EngineBuilds = new()
    {
        [2870186] = "1.0.0", [3700114] = "1.7.2", [3724489] = "1.8.0", [3729133] = "1.8.1", [3741772] = "1.8.2",
        [3757339] = "1.9", [3775276] = "1.9.1", [3790078] = "1.10", [3807424] = "1.11", [3825894] = "2.1",
        [3841827] = "2.2", [3847564] = "2.3", [3858292] = "2.4", [3870737] = "2.4.2", [3889387] = "2.5",
        [3901517] = "3.0.0", [3915963] = "3.1", [3917250] = "3.1.1", [3935073] = "3.2", [3942182] = "3.3",
        [4008490] = "3.5", [4019403] = "3.6", [4039451] = "4.0", [4053532] = "4.1", [4072250] = "4.2",
        [4117433] = "4.4", [4127312] = "4.4.1", [4159770] = "4.5", [4204761] = "5.0", [4214610] = "5.01",
        [4240749] = "5.10", [4288479] = "5.21", [4305896] = "5.30", [4352937] = "5.40", [4363240] = "5.41",
        [4395664] = "6.0", [4424678] = "6.01", [4461277] = "6.0.2", [4464155] = "6.10", [4476098] = "6.10.1",
        [4480234] = "6.10.2", [4526925] = "6.21", [4543176] = "6.22", [4573279] = "6.31", [4629139] = "7.0",
        [4667333] = "7.10", [4727874] = "7.20", [4834550] = "7.30", [5046157] = "7.40", [5203069] = "8.00",
        [5625478] = "8.20", [5793395] = "8.30", [6005771] = "8.40", [6058028] = "8.50", [6165369] = "8.51",
        [6337466] = "9.00", [6428087] = "9.01", [6639283] = "9.10", [6922310] = "9.21", [7095426] = "9.30",
        [7315705] = "9.40", [7609292] = "9.41", [7704164] = "10.00", [7955722] = "10.10", [8456527] = "10.20",
        [8723043] = "10.31", [9380822] = "10.40", [9603448] = "11.00", [9901083] = "11.10", [10708866] = "11.30",
        [10800459] = "11.31", [11265652] = "11.50", [11556442] = "12.00", [11883027] = "12.10", [12353830] = "12.21",
        [12905909] = "12.41", [13137020] = "12.50", [13498980] = "12.61", [14113327] = "13.40", [14211474] = "14.00",
        [14456520] = "14.30", [14550713] = "14.40", [14786821] = "14.60", [14835335] = "15.00", [15014719] = "15.10",
        [15341163] = "15.30", [15526472] = "15.50", [15913292] = "16.10", [16163563] = "16.30", [16218553] = "16.40",
        [16469788] = "16.50", [16745144] = "17.10", [17004569] = "17.30", [17269705] = "17.40", [17388565] = "17.50",
        [17468642] = "18.00", [17661844] = "18.10", [17745267] = "18.20", [17811397] = "18.21", [17882303] = "18.30",
        [18163738] = "18.40", [18489740] = "19.01", [18675304] = "19.10", [19458861] = "20.00", [19598943] = "20.10",
        [19751212] = "20.20", [19950687] = "20.30", [20244966] = "20.40", [20463113] = "21.00", [20696680] = "21.10",
        [21035704] = "21.20", [21657658] = "21.50",
    };

    public static BuildVersion? Read(Options options) =>
        FromCrashReporter(options) ?? FromExe(options.GameExeFor(false)) ?? FromCloudContent(options);

    static BuildVersion? FromCrashReporter(Options options)
    {
        if (string.IsNullOrWhiteSpace(options.BuildPath)) return null;
        string? exe = new[] { Path.Combine(options.BuildRoot, "Engine", "Binaries", "Win64"), options.Win64Dir }
            .Select(dir => Path.Combine(dir, CrashReporter))
            .FirstOrDefault(File.Exists);
        if (exe == null) return null;

        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            foreach (string? text in new[] { info.ProductVersion, info.FileVersion })
                if (FromVersionResource(text) is { } version && Parse(version, exe) is { } parsed) return parsed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Detail($"Version: couldn't read {exe}: {e.Message}");
        }
        return null;
    }

    internal static string? FromVersionResource(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        int header = text.IndexOf("++Fortnite", StringComparison.Ordinal);
        int dash = header < 0 ? -1 : text.IndexOf('-', header);
        if (dash < 0) return null;

        string version = text[(dash + 1)..];
        if (version is "Cert" or "Next")
        {
            string[] engine = text[..text.IndexOf('+')].Split('-');
            return engine.Length > 1 && int.TryParse(engine[1], out int build) && EngineBuilds.TryGetValue(build, out var mapped)
                ? mapped
                : null;
        }
        return VersionStart.IsMatch(version) ? version : null;
    }

    static BuildVersion? FromExe(string exe)
    {
        lock (ScanLock)
        {
            try
            {
                var info = new FileInfo(exe);
                if (!info.Exists) return null;
                scanned ??= LoadCache();
                if (scanned.TryGetValue(exe, out var cached) && cached.Length == info.Length && cached.Written == info.LastWriteTimeUtc)
                    return cached.Version == null ? null : Parse(cached.Version, exe);

                Log.Info($"Version: reading it from {Path.GetFileName(exe)} - only the first time, this takes a moment");
                var version = Pick(Scan(exe), exe);
                scanned[exe] = new CachedScan(info.Length, info.LastWriteTimeUtc, version?.Text);
                SaveCache(scanned);
                return version;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Detail($"Version: couldn't scan {exe}: {e.Message}");
                return null;
            }
        }
    }

    static BuildVersion? Pick(IReadOnlyCollection<string> found, string source)
    {
        if (found.Count > 1) Log.Detail($"Version: {Path.GetFileName(source)} names {string.Join(", ", found)}");
        return found
            .OrderByDescending(text => long.Parse(text.Split("-CL-")[1]))
            .ThenByDescending(text => text.Length)
            .Select(text => Parse(text, source))
            .OfType<BuildVersion>()
            .FirstOrDefault();
    }

    static BuildVersion? Parse(string text, string source) =>
        VersionStart.Match(text) is { Success: true } match
            ? new BuildVersion(text, int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), source)
            : null;

    static Dictionary<string, CachedScan> LoadCache()
    {
        try
        {
            if (File.Exists(CachePath) &&
                JsonSerializer.Deserialize<Dictionary<string, CachedScan>>(File.ReadAllText(CachePath)) is { } stored)
                return new Dictionary<string, CachedScan>(stored, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Detail($"Version: couldn't read {CachePath}: {e.Message}");
        }
        return new Dictionary<string, CachedScan>(StringComparer.OrdinalIgnoreCase);
    }

    static void SaveCache(Dictionary<string, CachedScan> cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Detail($"Version: couldn't save {CachePath}: {e.Message}");
        }
    }

    public static IReadOnlyCollection<string> Scan(string path)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        byte[] buffer = new byte[ChunkSize + Overlap];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);

        int kept = 0;
        bool first = true;
        while (true)
        {
            int read = stream.ReadAtLeast(buffer.AsSpan(kept, ChunkSize), ChunkSize, throwOnEndOfStream: false);
            int length = kept + read;
            bool last = read < ChunkSize;
            Collect(buffer, length, Marker, FullVersion, 1, first, last, found);
            Collect(buffer, length, MarkerUtf16, FullVersionUtf16, 2, first, last, found);
            if (last || length < Overlap) break;

            Array.Copy(buffer, length - Overlap, buffer, 0, Overlap);
            kept = Overlap;
            first = false;
        }
        return found;
    }

    static void Collect(byte[] buffer, int length, byte[] marker, Regex pattern, int charSize, bool first, bool last, HashSet<string> found)
    {
        var data = buffer.AsSpan(0, length);
        int at = 0;
        while (at < length)
        {
            int hit = data[at..].IndexOf(marker);
            if (hit < 0) return;
            hit += at;
            at = hit + marker.Length;

            int start = Math.Max(0, hit - 12 * charSize);
            int end = Math.Min(length, at + 13 * charSize);
            foreach (Match match in pattern.Matches(Encoding.Latin1.GetString(buffer, start, end - start)))
            {
                int matchStart = start + match.Index, matchEnd = matchStart + match.Length;
                if (matchStart > hit || matchEnd < at) continue;
                if ((!first && matchStart == 0) || (!last && matchEnd == length)) continue;
                found.Add((charSize == 1 ? match.Value : Encoding.Unicode.GetString(Encoding.Latin1.GetBytes(match.Value))).ToUpperInvariant());
            }
        }
    }

    static BuildVersion? FromCloudContent(Options options)
    {
        try
        {
            string path = CloudContentPath(options);
            if (!File.Exists(path)) return null;

            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (!json.RootElement.TryGetProperty("BuildVersion", out var value) || value.ValueKind != JsonValueKind.String)
                return null;

            var match = CloudPattern.Match(value.GetString()!);
            if (!match.Success) return null;

            return new BuildVersion(match.Groups[1].Value,
                int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value), path);
        }
        catch
        {
            return null;
        }
    }
}

internal static class Caldera
{
    const string CaptureName = "caldera_api_v1_launcher_racp.json";
    static readonly Regex RawToken = new(@"^eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    const string Universal =
        "eyJhbGciOiJFUzI1NiIsInR5cCI6IkpXVCJ9.eyJhY2NvdW50X2lkIjoiYmU5ZGE1YzJmYmVhNDQwN2IyZjQwZWJhYWQ4NTlhZDQiLCJnZW5lcmF0ZWQiOjE2Mzg3MTcyNzgsImNhbGRlcmFHdWlkIjoiMzgxMGI4NjMtMmE2NS00NDU3LTliNTgtNGRhYjNiNDgyYTg2IiwiYWNQcm92aWRlciI6IkVhc3lBbnRpQ2hlYXQiLCJub3RlcyI6IiIsImZhbGxiYWNrIjpmYWxzZX0.VAWQB67RTxhiWOxx7DBjnzDnXyyEnX7OljJm-j2d88G_WgwQ9wrE6lwMEHZHjBd1ISJdUO1UVUqkfLdU5nofBQ";

    public static string Token(Options options, string role)
    {
        string set = options.Caldera.Trim();
        if (set.Length > 0)
        {
            if (FromSetting(set) is { } jwt)
            {
                Log.Detail($"[{role}] Caldera token {Describe(jwt)}{(File.Exists(set) ? $" from {set}" : "")}");
                return jwt;
            }
            Log.Warn($"[{role}] The caldera setting isn't a caldera token or a captured {CaptureName}, so the built-in one is used. " +
                     "Fix it in Advanced (6) > Caldera Token", "caldera");
        }
        else if (Newest(Reversals) is { } capture && FromCapture(capture) is { } captured)
        {
            Log.Detail($"[{role}] Caldera token {Describe(captured)} from {capture}");
            return captured;
        }

        Log.Detail($"[{role}] Caldera token {Describe(Universal)} (built-in)");
        return Universal;
    }

    public static bool IsValid(string value) => FromSetting(value.Trim()) != null;

    public static string Summary(Options options)
    {
        string set = options.Caldera.Trim();
        if (set.Length > 0)
            return FromSetting(set) == null ? "invalid setting - using the built-in one"
                : File.Exists(set) ? Path.GetFileName(set) : "token pasted in";
        return Newest(Reversals) != null ? "from your latest capture" : "built-in (works for everyone)";
    }

    static string Reversals => Path.Combine(AppContext.BaseDirectory, "Reversals");

    static string? FromSetting(string value) =>
        File.Exists(value) ? FromCapture(value) : RawToken.IsMatch(value) ? value : null;

    static string? Newest(string reversals) =>
        Directory.Exists(reversals)
            ? Directory.EnumerateFiles(reversals, CaptureName, SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;

    static string? FromCapture(string path)
    {
        try
        {
            string json = string.Join('\n', File.ReadLines(path).Where(line => !line.TrimStart().StartsWith("//")));
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("jwt", out var jwt) && jwt.GetString() is { Length: > 0 } token
                ? token
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Detail($"Caldera: couldn't read {path}: {e.Message}");
            return null;
        }
    }

    static string Describe(string jwt)
    {
        try
        {
            string payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var claims = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            var root = claims.RootElement;
            string provider = root.TryGetProperty("acProvider", out var p) ? p.GetString() ?? "?" : "?";
            string generated = root.TryGetProperty("generated", out var g) && g.TryGetInt64(out long seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime.ToString("yyyy-MM-dd HH:mm")
                : "?";
            return $"({provider}, generated {generated})";
        }
        catch (Exception e) when (e is FormatException or JsonException or IndexOutOfRangeException)
        {
            return "(unreadable claims)";
        }
    }
}
