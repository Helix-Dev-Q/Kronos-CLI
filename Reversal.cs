using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace KronosCLI;

internal sealed class Reversal
{
    static readonly UTF8Encoding StrictUtf8 = new(false, true);
    static readonly UTF8Encoding Utf8NoBom = new(false);

    static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static readonly Regex HashedCloudName = new("^[0-9a-f]{32}-(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex CloudHexId = new("^[0-9a-f]{32}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly HashSet<string> VolatileQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "rvn", "t", "ts", "timestamp", "_", "cb", "nonce", "code", "token", "access_token", "refresh_token",
        "sig", "signature", "expires", "key-pair-id", "policy", "hdnts", "__token__", "sv", "se", "st", "sr", "sp", "spr",
    };

    static readonly HashSet<string> GenericTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "text/plain", "application/octet-stream", "binary/octet-stream", "application/unknown", "application/binary",
    };

    static readonly Dictionary<string, string> MimeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/vnd.apple.mpegurl"] = ".m3u8", ["application/x-mpegurl"] = ".m3u8",
        ["audio/mpegurl"] = ".m3u8", ["audio/x-mpegurl"] = ".m3u8",
        ["application/dash+xml"] = ".mpd", ["text/vtt"] = ".vtt", ["application/x-subrip"] = ".srt",
        ["video/mp4"] = ".mp4", ["video/iso.segment"] = ".m4s", ["video/mp2t"] = ".ts", ["video/webm"] = ".webm",
        ["video/quicktime"] = ".mov", ["video/x-matroska"] = ".mkv", ["video/x-msvideo"] = ".avi",
        ["audio/mpeg"] = ".mp3", ["audio/mp4"] = ".m4a", ["audio/aac"] = ".aac", ["audio/ogg"] = ".ogg",
        ["application/ogg"] = ".ogg", ["audio/opus"] = ".opus", ["audio/wav"] = ".wav", ["audio/x-wav"] = ".wav",
        ["audio/wave"] = ".wav", ["audio/flac"] = ".flac", ["audio/webm"] = ".weba",
        ["image/png"] = ".png", ["image/apng"] = ".png", ["image/jpeg"] = ".jpg", ["image/jpg"] = ".jpg",
        ["image/pjpeg"] = ".jpg", ["image/gif"] = ".gif", ["image/bmp"] = ".bmp", ["image/webp"] = ".webp",
        ["image/avif"] = ".avif", ["image/heic"] = ".heic", ["image/heif"] = ".heic", ["image/tiff"] = ".tiff",
        ["image/x-icon"] = ".ico", ["image/vnd.microsoft.icon"] = ".ico", ["image/svg+xml"] = ".svg",
        ["image/ktx2"] = ".ktx2", ["image/vnd-ms.dds"] = ".dds", ["image/x-tga"] = ".tga",
        ["font/woff"] = ".woff", ["font/woff2"] = ".woff2", ["font/ttf"] = ".ttf", ["font/otf"] = ".otf",
        ["font/collection"] = ".ttc", ["application/font-woff"] = ".woff", ["application/font-woff2"] = ".woff2",
        ["application/x-font-ttf"] = ".ttf", ["application/x-font-otf"] = ".otf", ["application/vnd.ms-fontobject"] = ".eot",
        ["text/html"] = ".html", ["application/xhtml+xml"] = ".html", ["text/css"] = ".css",
        ["application/javascript"] = ".js", ["text/javascript"] = ".js", ["application/x-javascript"] = ".js",
        ["application/ecmascript"] = ".js", ["text/ecmascript"] = ".js", ["application/wasm"] = ".wasm",
        ["application/xml"] = ".xml", ["text/xml"] = ".xml",
        ["text/csv"] = ".csv", ["text/tab-separated-values"] = ".tsv", ["text/markdown"] = ".md",
        ["application/yaml"] = ".yaml", ["application/x-yaml"] = ".yaml", ["text/yaml"] = ".yaml",
        ["text/x-yaml"] = ".yaml", ["application/toml"] = ".toml",
        ["application/pdf"] = ".pdf", ["application/zip"] = ".zip", ["application/x-zip-compressed"] = ".zip",
        ["application/gzip"] = ".gz", ["application/x-gzip"] = ".gz", ["application/x-7z-compressed"] = ".7z",
        ["application/x-tar"] = ".tar", ["application/zstd"] = ".zst",
        ["application/x-protobuf"] = ".pb", ["application/protobuf"] = ".pb", ["application/vnd.google.protobuf"] = ".pb",
        ["application/x-google-protobuf"] = ".pb", ["application/grpc"] = ".pb",
        ["application/msgpack"] = ".msgpack", ["application/x-msgpack"] = ".msgpack", ["application/cbor"] = ".cbor",
        ["application/jwt"] = ".jwt", ["application/x-www-form-urlencoded"] = ".txt",
    };

    static readonly HashSet<string> UrlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".ini", ".cfg", ".txt", ".xml", ".html", ".htm", ".css", ".js", ".mjs", ".svg",
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".heic", ".bmp", ".ico", ".tiff", ".tga", ".dds", ".ktx2",
        ".mp4", ".m4s", ".m4a", ".m4v", ".mov", ".webm", ".mkv", ".ts", ".mpd", ".m3u8", ".m3u", ".vtt", ".srt",
        ".mp3", ".ogg", ".opus", ".wav", ".flac", ".aac", ".woff", ".woff2", ".ttf", ".otf", ".eot",
        ".pdf", ".zip", ".gz", ".7z", ".zst", ".wasm", ".pb", ".csv", ".tsv", ".yaml", ".yml", ".toml", ".md",
        ".sav", ".uasset", ".uexp", ".ubulk", ".umap", ".pak", ".sig", ".utoc", ".ucas", ".bk2", ".bik",
        ".blurl", ".manifest", ".bin", ".dat",
    };

    static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".ini", ".cfg", ".txt", ".xml", ".html", ".htm", ".css", ".js", ".mjs", ".svg", ".mpd",
        ".m3u8", ".m3u", ".vtt", ".srt", ".csv", ".tsv", ".yaml", ".yml", ".toml", ".md", ".jwt",
    };

    static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".gif", ".webp", ".avif", ".heic", ".bmp", ".ico", ".tiff", ".svg", ".tga", ".dds", ".ktx2",
    };

    static readonly HashSet<string> StreamExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4s", ".m4v", ".mov", ".webm", ".mkv", ".ts", ".m3u8", ".m3u", ".mpd", ".vtt",
    };

    static readonly HashSet<string> LookupKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "accountId", "accountIds", "teamAccountIds", "ids", "userIds",
    };

    readonly object gate = new();
    readonly Dictionary<string, string> savedHosts = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, SortedDictionary<string, JsonNode?>> lookups = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, CloudFile> cloudFiles = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<string>> pendingCloud = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, bool> fullDownloads = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, string> cloudDirRequests = new(StringComparer.OrdinalIgnoreCase);

    static readonly string[] CloudDirFiles = [".manifest", ".ini", ".json"];

    sealed record CloudFile(string Name, string? Hash);

    public string Directory { get; }

    public int Count
    {
        get { lock (gate) return savedHosts.Count; }
    }

    public Reversal(string baseDir)
    {
        Directory = Path.Combine(baseDir, "Reversals", DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss"));
        System.IO.Directory.CreateDirectory(Directory);
    }

    public IReadOnlyDictionary<string, string> CloudDirRequests => cloudDirRequests;

    public bool ClaimFullDownload(Uri url) => fullDownloads.TryAdd(url.PathAndQuery, true);

    public void Requested(Uri url)
    {
        string path = Uri.UnescapeDataString(url.AbsolutePath);
        int at = path.IndexOf("/" + BuildManifests.CloudDir, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return;
        string file = path[(at + BuildManifests.CloudDir.Length + 1)..];
        if (CloudDirFiles.Any(extension => file.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            cloudDirRequests.TryAdd(file, url.ToString());
    }

    public void Save(string method, Uri url, string? contentType, string? contentEncoding, byte[] rawBody)
    {
        try
        {
            var (body, undecoded) = Decode(rawBody, contentEncoding);
            if (undecoded == null) Learn(body);

            string? cloudId = CloudId(url);
            bool access = cloudId != null &&
                          Uri.UnescapeDataString(url.AbsolutePath).Contains("/api/v1/access/", StringComparison.OrdinalIgnoreCase);
            if (cloudId != null && !access && method != "GET")
            {
                Log.Detail($"Reversal: {method} {url.AbsolutePath} is an upload, not saved");
                return;
            }
            if (body.Length == 0 && (cloudId == null || access)) return;

            string extension;
            string suffix = "";
            bool text;
            if (undecoded != null)
            {
                extension = Resolve(contentType, url, []).Extension;
                suffix = "." + undecoded;
                text = false;
                Log.Detail($"Reversal: {url.AbsolutePath} is still {undecoded}-encoded, saved byte-for-byte");
            }
            else if (Blurl(body) is { } blurl)
            {
                (body, extension, text) = blurl;
            }
            else
            {
                (extension, text) = Resolve(contentType, url, body);
            }

            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string name;
            string? verdict = null;

            lock (gate)
            {
                if (cloudId == null && undecoded == null && AddLookup(url, extension, body, stamp) is { } lookup)
                {
                    Log.Ok($"[Kronos Reversal] generated {stamp} -> {lookup.Name}  ({method} {url.AbsolutePath}, {lookup.Count} lookup(s) in one file)");
                    return;
                }

                if (cloudId != null && cloudFiles.TryGetValue(cloudId, out var file) && !access)
                {
                    name = file.Name;
                    extension = Path.GetExtension(file.Name);
                    text = TextExtensions.Contains(extension);
                    verdict = Verify(body, file.Hash);
                }
                else if (cloudId != null)
                {
                    string stem = cloudFiles.TryGetValue(cloudId, out var known) ? known.Name : "cloudstorage_" + SafeName(cloudId);
                    name = access ? stem + ".access.json" : stem + extension + suffix;
                    if (known == null) Pending(cloudId, name);
                }
                else
                {
                    name = Placed(url, extension) + suffix;
                    if (savedHosts.TryGetValue(name, out string? host) && !host.Equals(url.Host, StringComparison.OrdinalIgnoreCase))
                        name = WithHost(name, extension + suffix, url.Host);
                }

                string path = Path.Combine(Directory, name);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                Write(path, body, extension, text, url, stamp);
                savedHosts[name] = url.Host;
            }

            string line = $"[Kronos Reversal] generated {stamp} -> {name}  ({method} {url.AbsolutePath})";
            if (verdict == null) Log.Ok(line);
            else if (verdict.StartsWith("matches")) Log.Ok($"{line} - {verdict}");
            else Log.Warn($"{line} - {verdict}");
        }
        catch (Exception e)
        {
            Log.Detail($"Reversal: couldn't save {url.AbsolutePath}: {e.Message}");
        }
    }

    static string Placed(Uri url, string extension)
    {
        if (StreamExtensions.Contains(extension) ||
            url.AbsolutePath.EndsWith(".blurl", StringComparison.OrdinalIgnoreCase) ||
            url.Host.EndsWith("qstv.on.epicgames.com", StringComparison.OrdinalIgnoreCase) ||
            url.Host.Contains("-vod.", StringComparison.OrdinalIgnoreCase))
        {
            string[] segments = Uri.UnescapeDataString(url.AbsolutePath).Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(segment => segment is not ("." or ".."))
                .Select(SafeName)
                .ToArray();
            string relative = segments.Length == 0 ? "root" : Path.Combine(segments);
            return Path.Combine("Videos", relative.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? relative : relative + extension);
        }

        string name = FileName(url, extension);
        return ImageExtensions.Contains(extension) ? Path.Combine("Images", name) : name;
    }

    (string Name, int Count)? AddLookup(Uri url, string extension, byte[] body, string stamp)
    {
        if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase)) return null;
        var keys = IdentityKeys(url.Query);
        if (keys.Count == 0 || !keys.All(LookupKeys.Contains)) return null;

        JsonNode? response;
        try
        {
            response = JsonNode.Parse(body.AsSpan(BomLength(body)));
        }
        catch (JsonException)
        {
            return null;
        }

        string endpoint = url.GetLeftPart(UriPartial.Path);
        string name = FileName(new Uri(endpoint), ".json")[..^".json".Length] + "_lookups.json";
        if (!lookups.TryGetValue(name, out var entries)) lookups[name] = entries = new(StringComparer.Ordinal);
        entries[url.ToString()] = response;

        var all = new JsonArray(entries.Select(entry => (JsonNode)new JsonObject
        {
            ["url"] = entry.Key,
            ["response"] = entry.Value?.DeepClone(),
        }).ToArray());
        File.WriteAllText(Path.Combine(Directory, name),
            $"// [Kronos Reversal] generated {stamp}\n// {endpoint} - {entries.Count} lookup(s)\n{all.ToJsonString(PrettyJsonOptions)}", Utf8NoBom);
        savedHosts[name] = url.Host;
        return (name, entries.Count);
    }

    static List<string> IdentityKeys(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => Uri.UnescapeDataString(pair.Split('=')[0]))
            .Where(key => !VolatileQueryKeys.Contains(key) &&
                          !key.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase) &&
                          !key.StartsWith("x-goog-", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    string? CloudId(Uri url)
    {
        string path = Uri.UnescapeDataString(url.AbsolutePath);
        int at = path.IndexOf("cloudstorage/", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;

        string[] segments = path[(at + "cloudstorage/".Length)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return null;
        if (segments.Length == 2 && segments[0].Equals("user", StringComparison.OrdinalIgnoreCase)) return null;

        string last = segments[^1];
        var hashed = HashedCloudName.Match(last);
        if (hashed.Success) return hashed.Groups[1].Value;
        if (CloudHexId.IsMatch(last)) return last;
        lock (gate) return cloudFiles.ContainsKey(last) ? last : null;
    }

    void Pending(string cloudId, string name)
    {
        if (!pendingCloud.TryGetValue(cloudId, out var names)) pendingCloud[cloudId] = names = [];
        if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
    }

    void Learn(byte[] body)
    {
        if (!StartsLikeJson(body)) return;
        try
        {
            using var document = JsonDocument.Parse(body.AsMemory(BomLength(body)));
            Walk(document.RootElement);
        }
        catch (JsonException)
        {
        }
    }

    void Walk(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("uniqueFilename", out var unique) && unique.ValueKind == JsonValueKind.String &&
                    item.TryGetProperty("filename", out var filename) && filename.ValueKind == JsonValueKind.String)
                    LearnCloudFile(unique.GetString()!, filename.GetString()!, HashOf(item));
                else
                    Walk(item);
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject()) Walk(property.Value);
        }
    }

    static string? HashOf(JsonElement entry)
    {
        foreach (string key in new[] { "hash256", "hash" })
            if (entry.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
                value.GetString() is { Length: > 0 } hash)
                return hash;
        return null;
    }

    void LearnCloudFile(string unique, string filename, string? hash)
    {
        lock (gate)
        {
            var file = new CloudFile(SafeName(filename), hash);
            cloudFiles[unique] = file;
            if (!pendingCloud.Remove(unique, out var waiting)) return;

            foreach (string old in waiting)
            {
                string renamed = old.EndsWith(".access.json", StringComparison.OrdinalIgnoreCase) ? file.Name + ".access.json" : file.Name;
                try
                {
                    File.Move(Path.Combine(Directory, old), Path.Combine(Directory, renamed), true);
                    savedHosts[renamed] = savedHosts.GetValueOrDefault(old, "");
                    savedHosts.Remove(old);
                    Log.Ok($"[Kronos Reversal] renamed {old} -> {renamed}");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log.Detail($"Reversal: couldn't rename {old} to {renamed}: {e.Message}");
                }
            }
        }
    }

    static string? Verify(byte[] body, string? expected)
    {
        if (string.IsNullOrEmpty(expected)) return null;
        string actual = expected.Length switch
        {
            64 => Convert.ToHexString(SHA256.HashData(body)),
            40 => Convert.ToHexString(SHA1.HashData(body)),
            _ => "",
        };
        if (actual.Length == 0) return null;
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase)
            ? "matches the listing hash"
            : "does NOT match the listing hash";
    }

    static void Write(string path, byte[] body, string extension, bool text, Uri url, string stamp)
    {
        string? content = !text ? null
            : extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ? PrettyJson(body)
            : Utf8(body);
        if (content != null) File.WriteAllText(path, Annotate(extension, content, url, stamp), Utf8NoBom);
        else File.WriteAllBytes(path, body);
    }

    static (byte[] Body, string? Undecoded) Decode(byte[] body, string? encoding)
    {
        string[] codings = (encoding ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(coding => coding.ToLowerInvariant())
            .Where(coding => coding != "identity")
            .Reverse()
            .ToArray();

        foreach (string coding in codings)
        {
            if (Decompress(body, coding) is not { } decoded) return (body, coding);
            body = decoded;
        }

        if (codings.Length == 0 && body is [0x1F, 0x8B, ..] && Decompress(body, "gzip") is { } gzip) return (gzip, null);
        return (body, null);
    }

    static byte[]? Decompress(byte[] body, string coding) => coding switch
    {
        "gzip" or "x-gzip" => Inflate(body, input => new GZipStream(input, CompressionMode.Decompress)),
        "deflate" => Inflate(body, input => new ZLibStream(input, CompressionMode.Decompress))
                     ?? Inflate(body, input => new DeflateStream(input, CompressionMode.Decompress)),
        "br" => Inflate(body, input => new BrotliStream(input, CompressionMode.Decompress)),
        _ => null,
    };

    static byte[]? Inflate(byte[] body, Func<Stream, Stream> open)
    {
        try
        {
            using var input = new MemoryStream(body);
            using var decoder = open(input);
            using var output = new MemoryStream();
            decoder.CopyTo(output);
            return output.ToArray();
        }
        catch
        {
            return null;
        }
    }

    static (byte[] Body, string Extension, bool Text)? Blurl(byte[] body)
    {
        if (body.Length <= 8 || !body.AsSpan().StartsWith("blul"u8)) return null;
        return Inflate(body[8..], input => new ZLibStream(input, CompressionMode.Decompress)) is { } json
            ? (json, ".json", true)
            : (body, ".blurl", false);
    }

    static (string Extension, bool Text) Resolve(string? contentType, Uri url, byte[] body)
    {
        string type = contentType?.Split(';')[0].Trim().ToLowerInvariant() ?? "";

        if (Sniff(body) is { } sniffed) return Typed(sniffed);
        if (UrlExtension(url) is { } fromUrl) return Typed(fromUrl);

        if (type.EndsWith("/json") || type.EndsWith("+json"))
        {
            if (body.Length == 0 || IsJson(body)) return (".json", true);
        }
        else if (MimeExtensions.TryGetValue(type, out string? mapped))
        {
            return Typed(mapped);
        }
        else if (type.EndsWith("/xml") || type.EndsWith("+xml"))
        {
            return (".xml", true);
        }
        else if (!GenericTypes.Contains(type) && type.StartsWith("text/"))
        {
            return (".txt", true);
        }

        if (body.Length == 0) return (".bin", false);
        if (IsJson(body)) return (".json", true);
        if (LooksLikeIni(body)) return (".ini", true);
        if (Markup(body) is { } markup) return (markup, true);
        if (LooksLikeText(body)) return (".txt", true);
        return (".bin", false);
    }

    static (string Extension, bool Text) Typed(string extension) => (extension, TextExtensions.Contains(extension));

    static string? UrlExtension(Uri url)
    {
        string extension = Path.GetExtension(Uri.UnescapeDataString(url.AbsolutePath)).ToLowerInvariant();
        if (!UrlExtensions.Contains(extension)) return null;
        return extension == ".jpeg" ? ".jpg" : extension;
    }

    static string? Sniff(byte[] body)
    {
        ReadOnlySpan<byte> s = body;
        if (s.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 })) return ".png";
        if (s.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return ".jpg";
        if (s.StartsWith("GIF8"u8)) return ".gif";
        if (s.Length >= 12 && s.StartsWith("RIFF"u8))
        {
            var kind = s.Slice(8, 4);
            if (kind.SequenceEqual("WEBP"u8)) return ".webp";
            if (kind.SequenceEqual("WAVE"u8)) return ".wav";
            if (kind.SequenceEqual("AVI "u8)) return ".avi";
        }
        if (s.Length >= 12 && s.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            return Encoding.ASCII.GetString(s.Slice(8, 4)).Trim() switch
            {
                "avif" or "avis" => ".avif",
                "heic" or "heix" or "mif1" or "msf1" => ".heic",
                "qt" => ".mov",
                "M4A" => ".m4a",
                "M4V" => ".m4v",
                _ => ".mp4",
            };
        }
        if (s.Length >= 8 && (s.Slice(4, 4).SequenceEqual("styp"u8) || s.Slice(4, 4).SequenceEqual("moof"u8))) return ".m4s";
        if (s.StartsWith(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 })) return ".webm";
        if (s.StartsWith("OggS"u8)) return ".ogg";
        if (s.StartsWith("fLaC"u8)) return ".flac";
        if (s.StartsWith("ID3"u8)) return ".mp3";
        if (s.StartsWith("%PDF"u8)) return ".pdf";
        if (s.StartsWith("PK\u0003\u0004"u8)) return ".zip";
        if (s.StartsWith(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C })) return ".7z";
        if (s.StartsWith(new byte[] { 0x1F, 0x8B })) return ".gz";
        if (s.StartsWith(new byte[] { 0x28, 0xB5, 0x2F, 0xFD })) return ".zst";
        if (s.StartsWith("wOFF"u8)) return ".woff";
        if (s.StartsWith("wOF2"u8)) return ".woff2";
        if (s.StartsWith("OTTO"u8)) return ".otf";
        if (s.StartsWith("ttcf"u8)) return ".ttc";
        if (s.StartsWith(new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00 })) return ".ttf";
        if (s.Length > 6 && s.StartsWith(new byte[] { 0x00, 0x00, 0x01, 0x00 })) return ".ico";
        if (s.StartsWith("II*\0"u8) || s.StartsWith("MM\0*"u8)) return ".tiff";
        if (s.StartsWith("DDS "u8)) return ".dds";
        if (s.StartsWith(new byte[] { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30 })) return ".ktx2";
        if (s.StartsWith("\0asm"u8)) return ".wasm";
        if (s.StartsWith("GVAS"u8)) return ".sav";
        if (s.StartsWith(new byte[] { 0xC1, 0x83, 0x2A, 0x9E })) return ".uasset";
        if (s.StartsWith("KB2"u8)) return ".bk2";
        if (s.StartsWith("BIK"u8)) return ".bik";
        if (s.StartsWith("SQLite format 3\0"u8)) return ".sqlite";
        if (s.Length >= 189 && s[0] == 0x47 && s[188] == 0x47) return ".ts";
        if (s.Length >= 26 && s.StartsWith("BM"u8) && BitConverter.ToUInt32(body, 2) == body.Length) return ".bmp";
        return null;
    }

    static bool StartsLikeJson(byte[] body)
    {
        var span = body.AsSpan(BomLength(body)).TrimStart(" \t\r\n"u8);
        return !span.IsEmpty && span[0] is (byte)'{' or (byte)'[';
    }

    static bool IsJson(byte[] body)
    {
        if (!StartsLikeJson(body)) return false;
        try
        {
            using var document = JsonDocument.Parse(body.AsMemory(BomLength(body)));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    static bool LooksLikeIni(byte[] body)
    {
        foreach (string line in Sample(body, 2048).Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] is ';' or '#') continue;
            if (trimmed[0] == '[' && trimmed.EndsWith(']')) return true;
            return trimmed.Contains('=') && trimmed[0] is not ('{' or '<');
        }
        return false;
    }

    static string? Markup(byte[] body)
    {
        string head = Sample(body, 256).TrimStart();
        if (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)) return ".xml";
        if (head.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) ||
            head.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            return ".html";
        return null;
    }

    static bool LooksLikeText(byte[] body)
    {
        if (Utf8(body) is not { } text) return false;
        foreach (char c in text.AsSpan(0, Math.Min(text.Length, 4096)))
            if (char.IsControl(c) && c is not ('\t' or '\r' or '\n' or '\f')) return false;
        return true;
    }

    static string Sample(byte[] body, int max)
    {
        int skip = BomLength(body);
        return Encoding.UTF8.GetString(body, skip, Math.Min(body.Length - skip, max));
    }

    static string? PrettyJson(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body.AsMemory(BomLength(body)));
            return JsonSerializer.Serialize(document.RootElement, PrettyJsonOptions);
        }
        catch (JsonException)
        {
            return Utf8(body);
        }
    }

    static string? Utf8(byte[] body)
    {
        try
        {
            return StrictUtf8.GetString(body, BomLength(body), body.Length - BomLength(body));
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    static int BomLength(byte[] body) => body.AsSpan().StartsWith("﻿"u8) ? 3 : 0;

    static string Annotate(string extension, string content, Uri url, string stamp)
    {
        string generated = $"[Kronos Reversal] generated {stamp}";
        string link = url.ToString();
        string xmlComment = $"<!-- {generated} -->\n<!-- {link.Replace("--", "%2D%2D")} -->\n";
        return extension.ToLowerInvariant() switch
        {
            ".json" or ".js" or ".mjs" or ".css" => $"// {generated}\n// {link}\n{content}",
            ".ini" or ".cfg" => $"; {generated}\n; {link}\n{content}",
            ".yaml" or ".yml" or ".toml" => $"# {generated}\n# {link}\n{content}",
            ".m3u8" or ".m3u" => AfterFirstLine(content, "#EXTM3U", $"# {generated}\n# {link}\n"),
            ".html" or ".htm" => xmlComment + content,
            ".xml" or ".svg" or ".mpd" => AfterXmlDeclaration(content, xmlComment),
            ".txt" => $"{generated}\n{link}\n\n{content}",
            _ => content,
        };
    }

    static string AfterFirstLine(string content, string marker, string insert)
    {
        if (!content.StartsWith(marker, StringComparison.Ordinal)) return insert + content;
        int newline = content.IndexOf('\n');
        return newline < 0 ? content + "\n" + insert : content[..(newline + 1)] + insert + content[(newline + 1)..];
    }

    static string AfterXmlDeclaration(string content, string insert)
    {
        if (!content.StartsWith("<?xml", StringComparison.Ordinal)) return insert + content;
        int end = content.IndexOf("?>", StringComparison.Ordinal);
        return end < 0 ? insert + content : content[..(end + 2)] + "\n" + insert + content[(end + 2)..].TrimStart('\r', '\n');
    }

    static string FileName(Uri url, string extension)
    {
        string stem = url.AbsolutePath.Trim('/');
        if (stem.Length == 0) stem = "root";
        string query = NameQuery(url.Query);
        if (query.Length > 0) stem += "_" + query;

        string name = SafeName(stem);
        if (name.Length > 120)
            name = name[..111] + "_" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(name)))[..8];
        return name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? name : name + extension;
    }

    static string WithHost(string name, string tail, string host)
    {
        string stem = name.EndsWith(tail, StringComparison.OrdinalIgnoreCase) ? name[..^tail.Length] : name;
        return $"{stem}@{SafeName(host)}{tail}";
    }

    internal static string SafeName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (char c in value.Trim())
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return builder.Length > 0 ? builder.ToString() : "unnamed";
    }

    static string NameQuery(string query)
    {
        var kept = new List<string>();
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            string key = Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals]);
            if (VolatileQueryKeys.Contains(key) ||
                key.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("x-goog-", StringComparison.OrdinalIgnoreCase))
                continue;
            kept.Add(Uri.UnescapeDataString(pair));
        }
        return string.Join("_", kept);
    }
}

internal sealed class IslandContent(string captureDirectory)
{
    static readonly string FortniteSaved = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved");
    static readonly string Journal = Path.Combine(FortniteSaved, "PersistentDownloadDir", "GameCustom", "BundleSourceGameCustom.journal");

    const string DefaultManifestPath = "alt/plugin.manifest";
    const string Guid = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";
    static readonly Regex JournalSection = new($@"\[({Guid})#({Guid})\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex CookedUrl = new($@"/valkyrie/cooked-content/({Guid})/[^/]+/v(\d+)/({Guid})/", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex ResolveLine = new(@"Resolving cooked content for (\d{4}-\d{4}-\d{4})", RegexOptions.Compiled);
    static readonly Regex RequestLine = new($@"InstallBundleSourceGameCustom: Requesting Content State for ({Guid})#({Guid})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex LinkInName = new(@"link_(\d{4}-\d{4}-\d{4})", RegexOptions.Compiled);
    static readonly Regex MapName = new(@"([A-Za-z0-9_]{2,80})\.umap", RegexOptions.Compiled);
    static readonly Regex GeneratedCell = new("^[0-9A-Z]{25}$", RegexOptions.Compiled);
    static readonly Regex ModulePath = new($@"/({Guid})/([A-Za-z0-9_][A-Za-z0-9_/]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly JsonSerializerOptions InfoJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    readonly object gate = new();
    readonly Dictionary<string, (string Folder, string Confidence)> saved = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> failures = new(StringComparer.OrdinalIgnoreCase);

    sealed record Link(string BundleId, string ModuleId, int Version, string BaseUrl, string ManifestPath);

    sealed record Identity(string? LinkCode, string? Title, string Confidence, string Source, string? RootModule = null)
    {
        public bool IsDependency(Link link) =>
            RootModule != null && !RootModule.Equals(link.ModuleId, StringComparison.OrdinalIgnoreCase);
    }

    sealed record IslandInfo(
        string? LinkCode, string? Title, string Confidence, string Source, string ModuleId,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DependencyOf,
        int Version, string BundleId,
        IReadOnlyList<string> Files, IReadOnlyList<string> Maps, IReadOnlyList<string> MapPaths, string DownloadedFrom);

    public int Count
    {
        get { lock (gate) return saved.Count; }
    }

    public void Collect(bool final)
    {
        lock (gate)
        {
            var pending = Links().Values
                .Where(link => saved.TryGetValue(link.BundleId, out var done)
                    ? done.Confidence != "confirmed"
                    : failures.GetValueOrDefault(link.BundleId) < 3)
                .ToList();
            if (pending.Count == 0) return;

            var (confirmedBundles, confirmedModules) = Confirmed();
            Dictionary<string, string>? assumed = null;
            Dictionary<string, HashSet<string>>? hotfixPaths = null;

            foreach (var link in pending)
            {
                bool known = saved.TryGetValue(link.BundleId, out var previous);

                Identity? identity = null;
                if (confirmedBundles.TryGetValue(link.BundleId, out var direct)) identity = direct;
                else if (confirmedModules.TryGetValue(link.ModuleId, out var sameIsland))
                    identity = sameIsland with { Source = sameIsland.Source + " for the same island (different version)" };
                else if (!known || final)
                {
                    assumed ??= AssumedFromLogs(pending.Select(p => p.BundleId));
                    identity = assumed.TryGetValue(link.BundleId, out string? code)
                        ? new Identity(code, null, "assumed", "game log order (link code resolved right before this bundle was requested)")
                        : new Identity(null, null, "unknown", "no captured content response or game log mentions it");
                }

                if (identity == null || known && Rank(identity.Confidence) <= Rank(previous.Confidence)) continue;

                hotfixPaths ??= HotfixMapPaths();
                if (known) Relabel(link, identity, previous.Folder, hotfixPaths);
                else Download(link, identity, hotfixPaths, final);
            }

            CopyJournal();
        }
    }

    void Download(Link link, Identity identity, Dictionary<string, HashSet<string>> hotfixPaths, bool final)
    {
        string root = Path.Combine(captureDirectory, "GameCustom");
        string staging = Path.Combine(root, link.BundleId + ".downloading");
        try
        {
            if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, true);
            var download = BpsDownload.Open(link.BaseUrl, link.ManifestPath, CancellationToken.None).GetAwaiter().GetResult();
            string label = identity.LinkCode != null ? $"{identity.LinkCode} {identity.Title}".Trim() : link.BundleId;
            if (identity.IsDependency(link)) label += $" - dependency {link.ModuleId[..8]} v{link.Version}";
            Log.Info($"[Kronos Reversal] downloading island {label} ({download.Size / 1048576.0:0} MB) from {new Uri(link.BaseUrl).Host}");
            download.SaveTo(staging, CancellationToken.None).GetAwaiter().GetResult();

            var info = Info(link, identity, MainMaps(staging), hotfixPaths, download.FileNames);
            string folder = FolderName(info, root, null);
            System.IO.Directory.Move(staging, Path.Combine(root, folder));
            WriteInfo(Path.Combine(root, folder), info);
            saved[link.BundleId] = (folder, identity.Confidence);
            failures.Remove(link.BundleId);
            Log.Ok($"[Kronos Reversal] island saved -> GameCustom\\{folder}  ({download.Size / 1048576.0:0} MB, " +
                   $"{download.FileNames.Count} files verified, {How(identity.Confidence)})");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or TaskCanceledException)
        {
            int count = failures[link.BundleId] = failures.GetValueOrDefault(link.BundleId) + 1;
            try
            {
                if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, true);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
            string message = $"[Kronos Reversal] couldn't download island {link.BundleId}: {e.Message}";
            if (final || count >= 3) Log.Warn(count >= 3 ? message + " - giving up" : message);
            else Log.Detail(message + " - retrying");
        }
    }

    void Relabel(Link link, Identity identity, string previousFolder, Dictionary<string, HashSet<string>> hotfixPaths)
    {
        string root = Path.Combine(captureDirectory, "GameCustom");
        string current = Path.Combine(root, previousFolder);
        try
        {
            var files = System.IO.Directory.EnumerateFiles(current).Select(Path.GetFileName)
                .Where(name => name != "island.json").Select(name => name!).ToList();
            var info = Info(link, identity, MainMaps(current), hotfixPaths, files);
            string folder = FolderName(info, root, previousFolder);
            if (!folder.Equals(previousFolder, StringComparison.OrdinalIgnoreCase))
                System.IO.Directory.Move(current, Path.Combine(root, folder));
            WriteInfo(Path.Combine(root, folder), info);
            saved[link.BundleId] = (folder, identity.Confidence);
            Log.Ok(folder.Equals(previousFolder, StringComparison.OrdinalIgnoreCase)
                ? $"[Kronos Reversal] GameCustom\\{folder} is now {identity.Confidence}"
                : $"[Kronos Reversal] GameCustom\\{previousFolder} is now {identity.Confidence}, renamed to GameCustom\\{folder}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Detail($"Islands: couldn't update {previousFolder}: {e.Message}");
        }
    }

    static IslandInfo Info(Link link, Identity identity, List<string> maps, Dictionary<string, HashSet<string>> hotfixPaths, IReadOnlyList<string> files) =>
        new(identity.LinkCode, identity.Title ?? maps.FirstOrDefault(), identity.Confidence, identity.Source, link.ModuleId,
            identity.IsDependency(link) ? identity.RootModule : null, link.Version, link.BundleId, files, maps,
            hotfixPaths.TryGetValue(link.ModuleId, out var paths)
                ? paths.Where(path => maps.Any(map => path.EndsWith("/" + map, StringComparison.OrdinalIgnoreCase))).Order().ToList()
                : [],
            link.BaseUrl.TrimEnd('/') + "/" + link.ManifestPath);

    static void WriteInfo(string folder, IslandInfo info) =>
        File.WriteAllText(Path.Combine(folder, "island.json"), JsonSerializer.Serialize(info, InfoJson), new UTF8Encoding(false));

    static string How(string confidence) => confidence switch
    {
        "confirmed" => "confirmed by a captured content response",
        "assumed" => "assumed from the game log",
        _ => "link code unknown",
    };

    static int Rank(string confidence) => confidence switch
    {
        "confirmed" => 2,
        "assumed" => 1,
        _ => 0,
    };

    string FolderName(IslandInfo info, string root, string? previousFolder)
    {
        string name = Reversal.SafeName(info.LinkCode == null
            ? $"unknown {info.Title} {info.ModuleId}".Replace("  ", " ")
            : info.DependencyOf != null
                ? $"{info.LinkCode} {info.Title} - dependency {info.ModuleId[..8]} v{info.Version}".Replace("  ", " ")
                : $"{info.LinkCode} {info.Title}".Trim());
        bool taken = saved.Values.Any(s => s.Folder.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                                           !s.Folder.Equals(previousFolder, StringComparison.OrdinalIgnoreCase)) ||
                     previousFolder == null && System.IO.Directory.Exists(Path.Combine(root, name));
        return taken ? Reversal.SafeName($"{name} ({info.ModuleId[..8]} v{info.Version})") : name;
    }

    Dictionary<string, Link> Links()
    {
        var links = new Dictionary<string, Link>(StringComparer.OrdinalIgnoreCase);
        foreach (var (baseUrl, manifestPath) in ReadJournal())
            if (LinkFrom(baseUrl, manifestPath) is { } link) links[link.BundleId] = link;

        if (System.IO.Directory.Exists(captureDirectory))
            foreach (string file in System.IO.Directory.EnumerateFiles(captureDirectory, "*cooked-content-package*"))
                foreach (string baseUrl in ReadContent(file).BaseUrls)
                    if (LinkFrom(baseUrl, DefaultManifestPath) is { } link) links.TryAdd(link.BundleId, link);
        return links;
    }

    static Link? LinkFrom(string baseUrl, string manifestPath) =>
        CookedUrl.Match(baseUrl) is { Success: true } m
            ? new Link(m.Groups[3].Value, m.Groups[1].Value, int.Parse(m.Groups[2].Value), baseUrl, manifestPath)
            : null;

    (Dictionary<string, Identity> Bundles, Dictionary<string, Identity> Modules) Confirmed()
    {
        var bundles = new Dictionary<string, Identity>(StringComparer.OrdinalIgnoreCase);
        var modules = new Dictionary<string, Identity>(StringComparer.OrdinalIgnoreCase);
        string? reversals = Path.GetDirectoryName(captureDirectory);
        if (reversals == null || !System.IO.Directory.Exists(reversals)) return (bundles, modules);

        foreach (string file in System.IO.Directory.EnumerateFiles(reversals, "*cooked-content-package*", SearchOption.AllDirectories)
                     .OrderBy(File.GetLastWriteTimeUtc))
        {
            var (identity, rootModule, baseUrls) = ReadContent(file);
            if (identity == null) continue;
            if (rootModule != null) modules[rootModule] = identity;
            foreach (string url in baseUrls)
                if (CookedUrl.Match(url) is { Success: true } cooked) bundles[cooked.Groups[3].Value] = identity;
        }
        return (bundles, modules);
    }

    static (Identity? Identity, string? RootModule, List<string> BaseUrls) ReadContent(string file)
    {
        try
        {
            using var document = ReadCapturedJson(file);
            if (document == null) return (null, null, []);
            var root = document.RootElement;
            string? code = Text(root, "projectFlags", "liveLink", "linkCode") ?? LinkInName.Match(Path.GetFileName(file)) switch
            {
                { Success: true } m => m.Groups[1].Value,
                _ => null,
            };
            string? title = Text(root, "projectFlags", "meta", "title") ?? Text(root, "projectFlags", "meta", "name");
            string? rootModule = Text(root, "resolved", "root", "moduleId");
            var urls = new List<string>();
            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                foreach (var item in content.EnumerateArray())
                    if (Text(item, "binaries", "baseUrl") is { } url && CookedUrl.IsMatch(url)) urls.Add(url);
            var identity = code == null ? null : new Identity(code, title, "confirmed",
                $"captured content response in {Path.GetFileName(Path.GetDirectoryName(file))}", rootModule);
            return (identity, rootModule, urls);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Detail($"Islands: couldn't read {Path.GetFileName(file)}: {e.Message}");
            return (null, null, []);
        }
    }

    static Dictionary<string, string> AssumedFromLogs(IEnumerable<string> wanted)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var need = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
        string logs = Path.Combine(FortniteSaved, "Logs");
        if (!System.IO.Directory.Exists(logs)) return result;

        foreach (string file in System.IO.Directory.EnumerateFiles(logs, "FortniteGame*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(8))
        {
            if (need.Count == 0) break;
            var codes = new List<string>();
            var requests = new List<string>();

            void Pair()
            {
                if (codes.Count > 0 && codes.Count == requests.Count)
                    for (int i = 0; i < codes.Count; i++)
                        if (need.Remove(requests[i])) result[requests[i]] = codes[i];
                codes.Clear();
                requests.Clear();
            }

            try
            {
                foreach (string line in ReadLinesShared(file))
                {
                    var resolve = ResolveLine.Match(line);
                    if (resolve.Success)
                    {
                        if (requests.Count > 0) Pair();
                        if (!codes.Contains(resolve.Groups[1].Value)) codes.Add(resolve.Groups[1].Value);
                        continue;
                    }
                    var request = RequestLine.Match(line);
                    if (request.Success && codes.Count > requests.Count && !requests.Contains(request.Groups[2].Value))
                        requests.Add(request.Groups[2].Value);
                }
                Pair();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Detail($"Islands: couldn't read {Path.GetFileName(file)}: {e.Message}");
            }
        }
        return result;
    }

    Dictionary<string, HashSet<string>> HotfixMapPaths()
    {
        var paths = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        string? reversals = Path.GetDirectoryName(captureDirectory);
        if (reversals == null || !System.IO.Directory.Exists(reversals)) return paths;

        foreach (string file in System.IO.Directory.EnumerateFiles(reversals, "*.ini", SearchOption.AllDirectories))
        {
            try
            {
                foreach (Match match in ModulePath.Matches(ReadShared(file)))
                {
                    if (!paths.TryGetValue(match.Groups[1].Value, out var set))
                        paths[match.Groups[1].Value] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add("/" + match.Groups[1].Value + "/" + match.Groups[2].Value.TrimEnd('/'));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Detail($"Islands: couldn't read {Path.GetFileName(file)}: {e.Message}");
            }
        }
        return paths;
    }

    static List<string> MainMaps(string bundleFolder)
    {
        string utoc = Path.Combine(bundleFolder, "plugin.utoc");
        if (!File.Exists(utoc)) return [];
        try
        {
            string raw = Encoding.Latin1.GetString(File.ReadAllBytes(utoc));
            return MapName.Matches(raw).Select(m => m.Groups[1].Value)
                .Where(name => !GeneratedCell.IsMatch(name))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    static List<(string BaseUrl, string ManifestPath)> ReadJournal()
    {
        var entries = new List<(string, string)>();
        if (!File.Exists(Journal)) return entries;

        string? baseUrl = null, manifestPath = null;

        void Flush()
        {
            if (baseUrl != null) entries.Add((baseUrl, manifestPath ?? DefaultManifestPath));
            (baseUrl, manifestPath) = (null, null);
        }

        try
        {
            foreach (string raw in ReadShared(Journal).Split('\n'))
            {
                if (JournalSection.IsMatch(raw))
                {
                    Flush();
                    continue;
                }
                string line = raw.Trim();
                if (line.StartsWith("BaseUrl=", StringComparison.Ordinal)) baseUrl = line["BaseUrl=".Length..].Trim('"');
                else if (line.StartsWith("ManifestCloudPath=", StringComparison.Ordinal)) manifestPath = line["ManifestCloudPath=".Length..].Trim('"');
            }
            Flush();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Detail($"Islands: couldn't read the bundle journal: {e.Message}");
        }
        return entries;
    }

    void CopyJournal()
    {
        try
        {
            if (!File.Exists(Journal)) return;
            string target = Path.Combine(captureDirectory, "GameCustom", Path.GetFileName(Journal));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, ReadShared(Journal));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Detail($"Islands: couldn't copy the bundle journal: {e.Message}");
        }
    }

    static JsonDocument? ReadCapturedJson(string file)
    {
        string text = ReadShared(file);
        int start = 0;
        while (text.AsSpan(start).TrimStart().StartsWith("//"))
        {
            int newline = text.IndexOf('\n', start);
            if (newline < 0) return null;
            start = newline + 1;
        }
        return JsonDocument.Parse(text.AsMemory(start));
    }

    static string? Text(JsonElement element, params string[] path)
    {
        foreach (string key in path)
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out element))
                return null;
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) yield return line;
    }
}

internal sealed class BuildManifests(Reversal capture, string buildRoot)
{
    internal const string CloudDir = "Builds/Fortnite/Content/CloudDir/";
    const string DistributionPoints = "https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/public/distributionpoints";

    static readonly string FortniteSaved = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved");
    static readonly string Cache = Path.Combine(FortniteSaved, "PersistentDownloadDir", "ManifestCache");
    static readonly string GameLog = Path.Combine(FortniteSaved, "Logs", "FortniteGame.log");
    static readonly string LauncherInstalled = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");

    static readonly string[] FallbackHosts =
        ["https://egdownload.fastly-edge.com/", "https://epicgames-download1.akamaized.net/", "https://egs-cloudfront-chunks.epicgamescdn.com/"];

    static readonly Regex CloudPath = new(@"Builds/Fortnite/Content/CloudDir/([A-Za-z0-9_\-./]+?\.(?:manifest|ini|json))\b", RegexOptions.Compiled);
    static readonly Regex SafePath = new(@"^(?:[A-Za-z0-9_\-]+/)*[A-Za-z0-9_\-.]+\.(?:manifest|ini|json)$", RegexOptions.Compiled);

    static readonly JsonSerializerOptions LinksJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static readonly HttpClient Http = CreateClient();

    readonly object gate = new();
    readonly DateTime started = DateTime.UtcNow;
    readonly SortedDictionary<string, string> saved = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> failures = new(StringComparer.OrdinalIgnoreCase);
    List<string>? hosts;

    sealed record DownloadedFile(string File, string Url);

    public int Count
    {
        get { lock (gate) return saved.Count; }
    }

    public void Collect(bool final)
    {
        lock (gate)
        {
            var pending = Paths(final)
                .Where(path => !saved.ContainsKey(path) && failures.GetValueOrDefault(path) < 3)
                .ToList();
            if (pending.Count == 0) return;

            hosts ??= Hosts();
            foreach (string path in pending) Download(path, final);
            WriteLinks();
        }
    }

    void Download(string path, bool final)
    {
        string target = Path.Combine(capture.Directory, "ManifestCache", path.Replace('/', Path.DirectorySeparatorChar));
        string staging = target + ".downloading";
        string? error = null;
        var urls = new List<string>();
        if (capture.CloudDirRequests.TryGetValue(path, out string? requested)) urls.Add(requested);
        urls.AddRange(hosts!.Select(host => host + CloudDir + path).Where(url => !urls.Contains(url)));
        foreach (string url in urls)
        {
            try
            {
                byte[] bytes = Http.GetByteArrayAsync(url).GetAwaiter().GetResult();
                if (path.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
                {
                    if (bytes.Length > 0 && bytes[0] == '{') JsonDocument.Parse(bytes).Dispose();
                    else BpsDownload.Verify(bytes);
                }

                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(staging, bytes);
                File.Move(staging, target, true);
                saved[path] = url;
                failures.Remove(path);
                Log.Ok($"[Kronos Reversal] saved ManifestCache\\{path.Replace('/', '\\')} ({Size(bytes.Length)}) from {url}{CacheNote(path, bytes)}");
                return;
            }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or JsonException
                                          or UnauthorizedAccessException or TaskCanceledException)
            {
                error = e.Message;
                try
                {
                    if (File.Exists(staging)) File.Delete(staging);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        int count = failures[path] = failures.GetValueOrDefault(path) + 1;
        string message = $"[Kronos Reversal] couldn't download {CloudDir}{path}: {error}";
        if (final || count >= 3) Log.Warn(count >= 3 ? message + " - giving up" : message);
        else Log.Detail(message + " - retrying");
    }

    static string CacheNote(string path, byte[] bytes)
    {
        string cached = Path.Combine(Cache, path.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (!File.Exists(cached)) return "";
            return File.ReadAllBytes(cached).AsSpan().SequenceEqual(bytes)
                ? " - same as Fortnite's cached copy"
                : " - differs from Fortnite's cached copy";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    static string Size(long bytes) => bytes >= 1048576 ? $"{bytes / 1048576.0:0.0} MB" : $"{bytes / 1024.0:0} KB";

    IEnumerable<string> Paths(bool final)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? manifest = BuildManifestPath();
        if (manifest != null)
        {
            paths.Add(manifest);
            paths.Add(Path.ChangeExtension(manifest, null) + "/Full.ini");
        }

        if (System.IO.Directory.Exists(Cache))
            foreach (string file in System.IO.Directory.EnumerateFiles(Cache, "*", SearchOption.AllDirectories))
                paths.Add(Path.GetRelativePath(Cache, file).Replace('\\', '/'));

        foreach (string path in capture.CloudDirRequests.Keys) paths.Add(path);

        if (final && File.Exists(GameLog) && File.GetLastWriteTimeUtc(GameLog) >= started)
        {
            try
            {
                foreach (Match match in CloudPath.Matches(ReadShared(GameLog))) paths.Add(match.Groups[1].Value);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Detail($"Manifests: couldn't read the game log: {e.Message}");
            }
        }

        return paths.Where(path => SafePath.IsMatch(path) && !path.Contains(".."));
    }

    string? BuildManifestPath()
    {
        string cloudContent = Path.Combine(InstalledFortnite() ?? buildRoot, "Cloud", "cloudcontent.json");
        try
        {
            if (!File.Exists(cloudContent)) return null;
            using var json = JsonDocument.Parse(ReadShared(cloudContent));
            return json.RootElement.TryGetProperty("ManifestPath", out var value) && value.GetString() is { } path &&
                   path.StartsWith(CloudDir, StringComparison.OrdinalIgnoreCase)
                ? path[CloudDir.Length..]
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Detail($"Manifests: couldn't read {cloudContent}: {e.Message}");
            return null;
        }
    }

    internal static string? InstalledFortnite()
    {
        try
        {
            if (!File.Exists(LauncherInstalled)) return null;
            using var json = JsonDocument.Parse(ReadShared(LauncherInstalled));
            if (!json.RootElement.TryGetProperty("InstallationList", out var list) || list.ValueKind != JsonValueKind.Array) return null;
            foreach (var install in list.EnumerateArray())
                if (install.TryGetProperty("AppName", out var app) && app.GetString() == "Fortnite" &&
                    install.TryGetProperty("InstallLocation", out var location) && location.GetString() is { Length: > 0 } path)
                    return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Detail($"Manifests: couldn't read the Epic Games Launcher's install list: {e.Message}");
        }
        return null;
    }

    static List<string> Hosts()
    {
        try
        {
            using var json = JsonDocument.Parse(Http.GetStringAsync(DistributionPoints).GetAwaiter().GetResult());
            var found = json.RootElement.TryGetProperty("distributions", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(item => item.GetString())
                    .OfType<string>()
                    .Where(url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    .Select(url => url.TrimEnd('/') + "/")
                    .ToList()
                : [];
            if (found.Count > 0) return found;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
        {
            Log.Detail($"Manifests: couldn't get Epic's download servers, using the usual ones: {e.Message}");
        }
        return [.. FallbackHosts];
    }

    void WriteLinks()
    {
        if (saved.Count == 0) return;
        try
        {
            string folder = Path.Combine(capture.Directory, "ManifestCache");
            System.IO.Directory.CreateDirectory(folder);
            var links = saved.Select(pair => new DownloadedFile(pair.Key, pair.Value)).ToList();
            File.WriteAllText(Path.Combine(folder, "links.json"), JsonSerializer.Serialize(links, LinksJson), new UTF8Encoding(false));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Detail($"Manifests: couldn't write links.json: {e.Message}");
        }
    }

    static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        })
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Kronos");
        return client;
    }
}

internal sealed class BpsDownload
{
    const uint ManifestMagic = 0x44BEC00C;
    const uint ChunkMagic = 0xB1FE3AA2;

    static readonly HttpClient Http = CreateClient();

    sealed record Chunk(string Guid, ulong Hash, byte Group);
    sealed record Part(string Guid, uint Offset, uint Size);
    sealed record ManifestFile(string Name, byte[] Sha1, List<Part> Parts);

    readonly string cloudDirectory;
    readonly string chunkDirectory;
    readonly Dictionary<string, Chunk> chunks;
    readonly List<ManifestFile> files;

    BpsDownload(string cloudDirectory, int featureLevel, Dictionary<string, Chunk> chunks, List<ManifestFile> files)
    {
        this.cloudDirectory = cloudDirectory;
        chunkDirectory = featureLevel >= 15 ? "ChunksV4" : featureLevel >= 6 ? "ChunksV3" : featureLevel >= 3 ? "ChunksV2" : "Chunks";
        this.chunks = chunks;
        this.files = files;
    }

    public long Size => files.Sum(file => file.Parts.Sum(part => (long)part.Size));

    public IReadOnlyList<string> FileNames => files.Select(file => file.Name).ToList();

    public static async Task<BpsDownload> Open(string baseUrl, string manifestPath, CancellationToken token)
    {
        string manifestUrl = baseUrl.TrimEnd('/') + "/" + manifestPath.TrimStart('/');
        byte[] blob = await Http.GetByteArrayAsync(manifestUrl, token);
        return Parse(blob, manifestUrl[..(manifestUrl.LastIndexOf('/') + 1)]);
    }

    public async Task SaveTo(string folder, CancellationToken token)
    {
        string root = Path.GetFullPath(folder);
        string chunkFolder = Path.Combine(root, ".chunks");
        Directory.CreateDirectory(chunkFolder);

        var needed = files.SelectMany(file => file.Parts).Select(part => part.Guid).Distinct().ToList();
        await Parallel.ForEachAsync(needed, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token },
            async (guid, ct) =>
            {
                if (!chunks.TryGetValue(guid, out var chunk)) throw new InvalidDataException($"manifest has no chunk {guid}");
                string url = $"{cloudDirectory}{chunkDirectory}/{chunk.Group:D2}/{chunk.Hash:X16}_{guid}.chunk";
                await File.WriteAllBytesAsync(Path.Combine(chunkFolder, guid), await FetchChunk(url, ct), ct);
            });

        foreach (var file in files)
        {
            string path = Path.GetFullPath(Path.Combine(root, file.Name));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"manifest file name {file.Name} points outside the download folder");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                string? loadedGuid = null;
                byte[] loaded = [];
                foreach (var part in file.Parts)
                {
                    if (part.Guid != loadedGuid)
                    {
                        loaded = await File.ReadAllBytesAsync(Path.Combine(chunkFolder, part.Guid), token);
                        loadedGuid = part.Guid;
                    }
                    if ((long)part.Offset + part.Size > loaded.Length)
                        throw new InvalidDataException($"{file.Name} needs bytes past the end of chunk {part.Guid}");
                    var slice = loaded.AsMemory((int)part.Offset, (int)part.Size);
                    await output.WriteAsync(slice, token);
                    sha1.AppendData(slice.Span);
                }
            }

            if (!sha1.GetHashAndReset().AsSpan().SequenceEqual(file.Sha1))
                throw new InvalidDataException($"{file.Name} failed its SHA-1 check");
        }

        Directory.Delete(chunkFolder, true);
    }

    static async Task<byte[]> FetchChunk(string url, CancellationToken token)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return Unpack(await Http.GetByteArrayAsync(url, token));
            }
            catch (Exception e) when (attempt < 3 && !token.IsCancellationRequested &&
                                      e is HttpRequestException or IOException or TaskCanceledException or InvalidDataException)
            {
                await Task.Delay(1000 * attempt, token);
            }
        }
    }

    static byte[] Unpack(byte[] raw)
    {
        if (raw.Length < 41 || BinaryPrimitives.ReadUInt32LittleEndian(raw) != ChunkMagic)
            throw new InvalidDataException("not a BPS chunk");
        int headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8));
        int compressedSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12));
        byte storedAs = raw[40];
        if ((storedAs & 2) != 0) throw new InvalidDataException("chunk is encrypted");
        if (headerSize + compressedSize > raw.Length) throw new InvalidDataException("chunk is truncated");
        if ((storedAs & 1) == 0) return raw[headerSize..(headerSize + compressedSize)];
        return Inflate(raw, headerSize, compressedSize);
    }

    static byte[] Inflate(byte[] data, int offset, int length)
    {
        using var input = new MemoryStream(data, offset, length);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    public static void Verify(byte[] blob) => Body(blob);

    static byte[] Body(byte[] blob)
    {
        if (blob.Length < 37 || BinaryPrimitives.ReadUInt32LittleEndian(blob) != ManifestMagic)
            throw new InvalidDataException("not a binary BPS manifest");
        int headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(4));
        int compressedSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(12));
        byte storedAs = blob[36];
        if ((storedAs & 2) != 0) throw new InvalidDataException("manifest is encrypted");
        if (headerSize + compressedSize > blob.Length) throw new InvalidDataException("manifest is truncated");

        byte[] data = (storedAs & 1) != 0 ? Inflate(blob, headerSize, compressedSize) : blob[headerSize..(headerSize + compressedSize)];
        if (!SHA1.HashData(data).AsSpan().SequenceEqual(blob.AsSpan(16, 20)))
            throw new InvalidDataException("manifest failed its SHA-1 check");
        return data;
    }

    static BpsDownload Parse(byte[] blob, string cloudDirectory)
    {
        byte[] data = Body(blob);
        using var reader = new BinaryReader(new MemoryStream(data));
        var stream = reader.BaseStream;

        long start = stream.Position;
        uint metaSize = reader.ReadUInt32();
        reader.ReadByte();
        int featureLevel = reader.ReadInt32();
        stream.Position = start + metaSize;

        start = stream.Position;
        uint chunkListSize = reader.ReadUInt32();
        reader.ReadByte();
        int chunkCount = reader.ReadInt32();
        var guids = Read(chunkCount, () => GuidOf(reader));
        var hashes = Read(chunkCount, reader.ReadUInt64);
        stream.Position += 20L * chunkCount;
        byte[] groups = reader.ReadBytes(chunkCount);
        stream.Position = start + chunkListSize;

        var chunks = new Dictionary<string, Chunk>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < chunkCount; i++) chunks[guids[i]] = new Chunk(guids[i], hashes[i], groups[i]);

        reader.ReadUInt32();
        reader.ReadByte();
        int fileCount = reader.ReadInt32();
        var names = Read(fileCount, () => FString(reader));
        Read(fileCount, () => FString(reader));
        var hashes1 = Read(fileCount, () => reader.ReadBytes(20));
        stream.Position += fileCount;
        for (int i = 0; i < fileCount; i++)
        {
            int tags = reader.ReadInt32();
            for (int t = 0; t < tags; t++) FString(reader);
        }
        var parts = Read(fileCount, () =>
        {
            int count = reader.ReadInt32();
            var list = new List<Part>(count);
            for (int p = 0; p < count; p++)
            {
                long partStart = stream.Position;
                uint size = reader.ReadUInt32();
                string guid = GuidOf(reader);
                uint offset = reader.ReadUInt32();
                uint length = reader.ReadUInt32();
                stream.Position = Math.Max(stream.Position, partStart + size);
                list.Add(new Part(guid, offset, length));
            }
            return list;
        });

        var files = new List<ManifestFile>(fileCount);
        for (int i = 0; i < fileCount; i++) files.Add(new ManifestFile(names[i], hashes1[i], parts[i]));
        return new BpsDownload(cloudDirectory, featureLevel, chunks, files);
    }

    static List<T> Read<T>(int count, Func<T> read)
    {
        var list = new List<T>(count);
        for (int i = 0; i < count; i++) list.Add(read());
        return list;
    }

    static string GuidOf(BinaryReader reader) =>
        $"{reader.ReadUInt32():X8}{reader.ReadUInt32():X8}{reader.ReadUInt32():X8}{reader.ReadUInt32():X8}";

    static string FString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length == 0) return "";
        if (length < 0)
        {
            byte[] utf16 = reader.ReadBytes(-length * 2);
            return Encoding.Unicode.GetString(utf16, 0, Math.Max(0, utf16.Length - 2));
        }
        byte[] bytes = reader.ReadBytes(length);
        return Encoding.Latin1.GetString(bytes, 0, Math.Max(0, bytes.Length - 1));
    }

    static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        })
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Kronos");
        return client;
    }
}

internal sealed class EpicLauncher
{
    const string ProcessName = "EpicGamesLauncher";
    public const string LaunchFortniteUrl = "com.epicgames.launcher://apps/Fortnite?action=launch&silent=true";

    static readonly string MarkerPath = Path.Combine(AppContext.BaseDirectory, "epic-launcher-proxy.txt");

    DateTime? startedWithProxyAt;

    public static void RestoreLeftover()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return;
            if (!DateTime.TryParse(File.ReadAllText(MarkerPath).Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var startedAt))
            {
                File.Delete(MarkerPath);
                return;
            }
            new EpicLauncher { startedWithProxyAt = startedAt }
                .Restore("Restarted the Epic Games Launcher an earlier Kronos session left pointed at its proxy");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Couldn't check {MarkerPath}: {e.Message}");
        }
    }

    public static bool IsRunning => AnyRunning(_ => true);

    public bool IsOurs => startedWithProxyAt is { } at &&
                          AnyRunning(process => process.StartTime >= at.AddSeconds(-2) && process.StartTime <= at.AddSeconds(30));

    public bool Start(string? proxyUrl, string? launchUrl)
    {
        if (FindExe() is not { } exe)
        {
            Log.Error("Couldn't find the Epic Games Launcher - is it installed?");
            return false;
        }

        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        if (launchUrl != null) info.ArgumentList.Add(launchUrl);
        RedirectProxy.ApplyTo(info.Environment, proxyUrl);

        try
        {
            startedWithProxyAt = proxyUrl != null ? DateTime.Now : null;
            using var process = Process.Start(info);
            if (startedWithProxyAt is { } at) WriteMarker(at);
            else DeleteMarker();
            return true;
        }
        catch (Exception e)
        {
            startedWithProxyAt = null;
            Log.Error($"Couldn't start the Epic Games Launcher: {e.Message}");
            return false;
        }
    }

    public void Restore(string message = "Restarted the Epic Games Launcher without Kronos's proxy")
    {
        if (!IsOurs)
        {
            DeleteMarker();
            return;
        }
        Close();
        if (Start(null, null)) Log.Ok(message);
    }

    static void WriteMarker(DateTime startedAt)
    {
        try
        {
            File.WriteAllText(MarkerPath, startedAt.ToString("o", CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Detail($"Couldn't write {MarkerPath}: {e.Message}");
        }
    }

    static void DeleteMarker()
    {
        try
        {
            File.Delete(MarkerPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Detail($"Couldn't delete {MarkerPath}: {e.Message}");
        }
    }

    public static void Close()
    {
        foreach (var process in Process.GetProcessesByName(ProcessName))
        {
            using (process)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
    }

    public static void Open(string url)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Log.Warn($"Couldn't ask the Epic Games Launcher to open {url}: {e.Message}");
        }
    }

    static bool AnyRunning(Func<Process, bool> match)
    {
        bool found = false;
        foreach (var process in Process.GetProcessesByName(ProcessName))
        {
            using (process)
            {
                try
                {
                    found |= match(process);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
        return found;
    }

    static string? FindExe()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"com.epicgames.launcher\shell\open\command");
            if (key?.GetValue(null) is string command)
            {
                string path = command.TrimStart();
                int end = path.StartsWith('"') ? path.IndexOf('"', 1) : path.IndexOf(' ');
                path = path.StartsWith('"') ? (end > 0 ? path[1..end] : path.Trim('"')) : (end > 0 ? path[..end] : path);
                if (File.Exists(path)) return path;
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        foreach (string bits in new[] { "Win64", "Win32" })
        {
            string path = Path.Combine(Environment.GetFolderPath(folder), "Epic Games", "Launcher", "Portal", "Binaries", bits, "EpicGamesLauncher.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
