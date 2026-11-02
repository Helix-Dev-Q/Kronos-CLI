using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace KronosCLI;

internal sealed class StreamedVideos(string captureDirectory)
{
    static readonly HttpClient Http = CreateClient();
    static readonly Regex Attribute = new(@"([A-Z0-9-]+)=(""[^""]*""|[^,]*)", RegexOptions.Compiled);
    static readonly Regex EmptyPrefix = new(@"\sxmlns:(\w+)=""""", RegexOptions.Compiled);

    readonly object gate = new();
    readonly HashSet<string> saved = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> failures = new(StringComparer.OrdinalIgnoreCase);

    sealed record Playlist(string Url, string? Data, Dictionary<string, string> Variants);
    sealed record Representation(string Mime, long Bandwidth, string? Width, string? Height, string Url);

    public int Count
    {
        get { lock (gate) return saved.Count; }
    }

    public void Collect(bool final)
    {
        lock (gate)
        {
            if (!Directory.Exists(captureDirectory)) return;
            foreach (string file in Directory.EnumerateFiles(captureDirectory, "*.blurl.json", SearchOption.AllDirectories))
            {
                if (ReadPlaylist(file) is not { } playlist) continue;
                string id = new Uri(playlist.Url).AbsolutePath.Trim('/').Split('/')[0];
                if (id.Length == 0 || saved.Contains(id) || failures.GetValueOrDefault(id) >= 3) continue;
                Save(id, playlist, final);
            }
        }
    }

    void Save(string id, Playlist playlist, bool final)
    {
        string name = Reversal.SafeName(id) + ".mp4";
        string folder = Path.Combine(captureDirectory, "Videos");
        string staging = Path.Combine(folder, name + ".downloading");
        try
        {
            bool dash = new Uri(playlist.Url).AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase);
            var (video, audio, size) = dash ? Dash(playlist) : Hls(playlist);
            byte[] mp4 = Mp4Merge.Combine(video, audio);

            Directory.CreateDirectory(folder);
            File.WriteAllBytes(staging, mp4);
            File.Move(staging, Path.Combine(folder, name), true);
            saved.Add(id);
            failures.Remove(id);
            Log.Ok($"[Kronos Reversal] video saved -> Videos\\{name}  ({size}, {(audio != null ? "with sound" : "no sound")}, {mp4.Length / 1048576.0:0.0} MB)");
        }
        catch (NotSupportedException e)
        {
            failures[id] = 3;
            Log.Warn($"[Kronos Reversal] can't turn video {id} into an mp4: {e.Message}");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or TaskCanceledException
                                      or UnauthorizedAccessException or XmlException or UriFormatException)
        {
            int count = failures[id] = failures.GetValueOrDefault(id) + 1;
            try
            {
                if (File.Exists(staging)) File.Delete(staging);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
            string message = $"[Kronos Reversal] couldn't save video {id}: {e.Message}";
            if (final || count >= 3) Log.Warn(count >= 3 ? message + " - giving up" : message);
            else Log.Detail(message + " - retrying");
        }
    }

    static Playlist? ReadPlaylist(string file)
    {
        try
        {
            string json = string.Join('\n', File.ReadLines(file).Where(line => !line.TrimStart().StartsWith("//")));
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("playlists", out var playlists) || playlists.ValueKind != JsonValueKind.Array)
                return null;

            var variants = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            JsonElement? best = null;
            double bestFps = -1;
            foreach (var entry in playlists.EnumerateArray())
            {
                string? type = Text(entry, "type");
                if (type == "variant" && Text(entry, "rel_url") is { } rel && Text(entry, "data") is { } data)
                    variants[rel] = data;
                else if (type == "main" && Text(entry, "url") is not null)
                {
                    double fps = entry.TryGetProperty("fps", out var value) && value.TryGetDouble(out double f) ? f : 0;
                    if (fps > bestFps) (best, bestFps) = (entry, fps);
                }
            }
            return best is { } main ? new Playlist(Text(main, "url")!, Text(main, "data"), variants) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Detail($"Videos: couldn't read {Path.GetFileName(file)}: {e.Message}");
            return null;
        }
    }

    static (byte[] Video, byte[]? Audio, string Size) Hls(Playlist playlist)
    {
        var masterUri = new Uri(playlist.Url);
        string[] lines = Lines(playlist.Data ?? GetString(masterUri));

        string? audio = null, video = null, size = "";
        long bestBandwidth = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("#EXT-X-MEDIA:") && Attr(lines[i], "TYPE") == "AUDIO" && Attr(lines[i], "URI") is { } uri)
            {
                if (audio == null || Attr(lines[i], "DEFAULT") == "YES") audio = uri;
            }
            else if (lines[i].StartsWith("#EXT-X-STREAM-INF:") && Attr(lines[i], "RESOLUTION") is { } resolution)
            {
                long bandwidth = long.TryParse(Attr(lines[i], "BANDWIDTH"), out long value) ? value : 0;
                string? next = lines.Skip(i + 1).FirstOrDefault(line => line.Length > 0 && !line.StartsWith('#'));
                if (next != null && bandwidth > bestBandwidth) (video, bestBandwidth, size) = (next, bandwidth, resolution);
            }
        }
        if (video == null) throw new InvalidDataException("its playlist has no video stream");

        return (HlsTrack(masterUri, video, playlist), audio == null ? null : HlsTrack(masterUri, audio, playlist), size);
    }

    static byte[] HlsTrack(Uri master, string relative, Playlist playlist)
    {
        var uri = new Uri(master, relative);
        string text = playlist.Variants.TryGetValue(relative, out string? data) ? data : GetString(uri);
        if (text.Contains("#EXT-X-KEY:") && !text.Contains("METHOD=NONE")) throw new NotSupportedException("it's encrypted");
        if (text.Contains("#EXT-X-BYTERANGE")) throw new NotSupportedException("it uses byte-range segments");

        string? init = null;
        var segments = new List<Uri>();
        foreach (string line in Lines(text))
        {
            if (line.StartsWith("#EXT-X-MAP:")) init ??= Attr(line, "URI");
            else if (line.Length > 0 && !line.StartsWith('#')) segments.Add(new Uri(uri, line));
        }
        if (init == null) throw new NotSupportedException("it isn't an MP4 stream");

        return Concat([new Uri(uri, init), .. segments]);
    }

    static (byte[] Video, byte[]? Audio, string Size) Dash(Playlist playlist)
    {
        var mpdUri = new Uri(playlist.Url);
        string text = playlist.Data ?? GetString(mpdUri);
        foreach (Match empty in EmptyPrefix.Matches(text))
            text = Regex.Replace(text, $@"\s{empty.Groups[1].Value}:[\w-]+=""[^""]*""", "");
        var mpd = XDocument.Parse(EmptyPrefix.Replace(text, ""));
        XNamespace ns = mpd.Root!.Name.Namespace;
        if (mpd.Descendants(ns + "ContentProtection").Any()) throw new NotSupportedException("it's encrypted");

        var baseUri = mpd.Root.Element(ns + "BaseURL")?.Value is { Length: > 0 } root ? new Uri(mpdUri, root.Trim()) : mpdUri;
        var representations = mpd.Descendants(ns + "Representation")
            .Select(r => (Element: r, Url: r.Element(ns + "BaseURL")?.Value.Trim()))
            .Where(r => !string.IsNullOrEmpty(r.Url))
            .Select(r => new Representation(
                (string?)r.Element.Attribute("mimeType") ?? (string?)r.Element.Parent?.Attribute("mimeType") ?? "",
                long.TryParse((string?)r.Element.Attribute("bandwidth"), out long bandwidth) ? bandwidth : 0,
                (string?)r.Element.Attribute("width"), (string?)r.Element.Attribute("height"), r.Url!))
            .ToList();

        var video = representations.Where(r => r.Mime.StartsWith("video")).MaxBy(r => r.Bandwidth)
                    ?? throw new NotSupportedException("its manifest has no single-file video stream");
        var audio = representations.Where(r => r.Mime.StartsWith("audio")).MaxBy(r => r.Bandwidth);

        return (Get(new Uri(baseUri, video.Url)), audio == null ? null : Get(new Uri(baseUri, audio.Url)), $"{video.Width}x{video.Height}");
    }

    static byte[] Concat(List<Uri> uris)
    {
        var parts = new byte[uris.Count][];
        Parallel.ForEachAsync(Enumerable.Range(0, uris.Count), new ParallelOptions { MaxDegreeOfParallelism = 6 },
            async (i, _) => parts[i] = await GetAsync(uris[i])).GetAwaiter().GetResult();

        byte[] all = new byte[parts.Sum(part => (long)part.Length)];
        int at = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(all, at);
            at += part.Length;
        }
        return all;
    }

    static byte[] Get(Uri uri) => GetAsync(uri).GetAwaiter().GetResult();

    static async Task<byte[]> GetAsync(Uri uri)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await Http.GetByteArrayAsync(uri);
            }
            catch (Exception e) when (attempt < 3 && e is HttpRequestException or IOException or TaskCanceledException)
            {
                await Task.Delay(1000 * attempt);
            }
        }
    }

    static string GetString(Uri uri) => Encoding.UTF8.GetString(Get(uri));

    static string[] Lines(string text) => text.Split('\n').Select(line => line.Trim()).ToArray();

    static string? Attr(string line, string name)
    {
        foreach (Match match in Attribute.Matches(line[(line.IndexOf(':') + 1)..]))
            if (match.Groups[1].Value == name) return match.Groups[2].Value.Trim('"');
        return null;
    }

    static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

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

internal static class Mp4Merge
{
    readonly record struct Box(string Type, int Start, int Size, int Header)
    {
        public int Payload => Start + Header;
        public int End => Start + Size;
    }

    readonly record struct Sample(int Offset, int Size, uint Duration, int CompositionOffset, bool Sync);

    sealed class Track(byte[] data, Box trak, long timescale, uint[] defaults)
    {
        public byte[] Data { get; } = data;
        public Box Trak { get; } = trak;
        public long Timescale { get; } = timescale;
        public uint[] Defaults { get; } = defaults;
        public List<Sample> Samples { get; } = [];
        public List<long> ChunkOffsets { get; } = [];
        public List<int> ChunkCounts { get; } = [];
        public long MediaDuration => Samples.Sum(sample => (long)sample.Duration);
    }

    sealed record Chunk(Track Track, int First, int Count, double Time);

    static readonly byte[] FileType = Build("ftyp", [.. "isom"u8, 0, 0, 2, 0, .. "isomiso2avc1mp41"u8]);

    public static byte[] Combine(byte[] video, byte[]? audio)
    {
        byte[][] sources = audio == null ? [video] : [video, audio];
        var tracks = sources.Select(ReadTrack).ToList();
        var chunks = tracks.SelectMany(ReadChunks).OrderBy(chunk => chunk.Time).ToList();
        if (chunks.Count == 0) throw new InvalidDataException("the video has no media");

        long mdatSize = 0;
        foreach (var chunk in chunks)
        {
            chunk.Track.ChunkOffsets.Add(mdatSize);
            chunk.Track.ChunkCounts.Add(chunk.Count);
            for (int i = chunk.First; i < chunk.First + chunk.Count; i++) mdatSize += chunk.Track.Samples[i].Size;
        }

        bool large = mdatSize + 16 * 1024 * 1024 > uint.MaxValue;
        int mdatHeader = large ? 16 : 8;
        long shift = FileType.Length + Movie(video, tracks, 0, large).Length + mdatHeader;
        byte[] moov = Movie(video, tracks, shift, large);

        using var output = new MemoryStream();
        output.Write(FileType);
        output.Write(moov);
        if (large)
        {
            output.Write(Be32(1));
            output.Write("mdat"u8);
            output.Write(Be64((ulong)(mdatSize + 16)));
        }
        else
        {
            output.Write(Be32((uint)(mdatSize + 8)));
            output.Write("mdat"u8);
        }
        foreach (var chunk in chunks)
            for (int i = chunk.First; i < chunk.First + chunk.Count; i++)
            {
                var sample = chunk.Track.Samples[i];
                output.Write(chunk.Track.Data, sample.Offset, sample.Size);
            }
        return output.ToArray();
    }

    static Track ReadTrack(byte[] data)
    {
        var moov = Children(data, 0, data.Length).FirstOrDefault(box => box.Type == "moov");
        if (moov.Size == 0) throw new InvalidDataException("a stream has no init segment");
        var parts = Children(data, moov.Payload, moov.End);
        var trak = parts.FirstOrDefault(box => box.Type == "trak");
        if (trak.Size == 0) throw new InvalidDataException("a stream has no track");

        uint[] defaults = [1, 0, 0, 0];
        if (parts.FirstOrDefault(box => box.Type == "mvex") is { Size: > 0 } mvex)
            foreach (var trex in Children(data, mvex.Payload, mvex.End).Where(box => box.Type == "trex"))
                for (int i = 0; i < 4; i++) defaults[i] = U32(data, trex.Payload + 8 + 4 * i);

        var mdhd = Find(data, trak, "mdia", "mdhd") ?? throw new InvalidDataException("a track has no media header");
        long timescale = U32(data, mdhd.Payload + 4 + (data[mdhd.Payload] == 1 ? 16 : 8));
        return new Track(data, trak, Math.Max(1, timescale), defaults);
    }

    static IEnumerable<Chunk> ReadChunks(Track track)
    {
        byte[] data = track.Data;
        var top = Children(data, 0, data.Length);
        long decodeTime = 0;
        var chunks = new List<Chunk>();
        for (int i = 0; i < top.Count; i++)
        {
            if (top[i].Type != "moof") continue;
            var moof = top[i];
            int next = top.FindIndex(i + 1, box => box.Type is "mdat" or "moof");
            if (next < 0 || top[next].Type != "mdat") throw new InvalidDataException("a fragment has no media data");
            var mdat = top[next];

            foreach (var traf in Children(data, moof.Payload, moof.End).Where(box => box.Type == "traf"))
            {
                var parts = Children(data, traf.Payload, traf.End);
                var tfhd = parts.FirstOrDefault(box => box.Type == "tfhd");
                if (tfhd.Size == 0) throw new InvalidDataException("a fragment has no header");

                uint flags = U24(data, tfhd.Payload + 1);
                int at = tfhd.Payload + 8;
                long dataStart = moof.Start;
                if ((flags & 0x1) != 0) { dataStart = (long)U64(data, at); at += 8; }
                if ((flags & 0x2) != 0) at += 4;
                uint duration = track.Defaults[1], size = track.Defaults[2], sampleFlags = track.Defaults[3];
                if ((flags & 0x8) != 0) { duration = U32(data, at); at += 4; }
                if ((flags & 0x10) != 0) { size = U32(data, at); at += 4; }
                if ((flags & 0x20) != 0) sampleFlags = U32(data, at);

                if (parts.FirstOrDefault(box => box.Type == "tfdt") is { Size: > 0 } tfdt)
                    decodeTime = data[tfdt.Payload] == 1 ? (long)U64(data, tfdt.Payload + 4) : U32(data, tfdt.Payload + 4);

                int first = track.Samples.Count;
                long cursor = dataStart;
                foreach (var trun in parts.Where(box => box.Type == "trun"))
                {
                    uint trunFlags = U24(data, trun.Payload + 1);
                    uint count = U32(data, trun.Payload + 4);
                    int p = trun.Payload + 8;
                    if ((trunFlags & 0x1) != 0) { cursor = dataStart + (int)U32(data, p); p += 4; }
                    uint? firstFlags = null;
                    if ((trunFlags & 0x4) != 0) { firstFlags = U32(data, p); p += 4; }

                    for (uint s = 0; s < count; s++)
                    {
                        uint d = duration, z = size, f = s == 0 && firstFlags is { } given ? given : sampleFlags;
                        int c = 0;
                        if ((trunFlags & 0x100) != 0) { d = U32(data, p); p += 4; }
                        if ((trunFlags & 0x200) != 0) { z = U32(data, p); p += 4; }
                        if ((trunFlags & 0x400) != 0) { f = U32(data, p); p += 4; }
                        if ((trunFlags & 0x800) != 0) { c = (int)U32(data, p); p += 4; }
                        if (p > trun.End) throw new InvalidDataException("a fragment's sample table is cut off");
                        if (cursor < mdat.Payload || cursor + z > mdat.End) throw new InvalidDataException("a sample points outside its media data");
                        track.Samples.Add(new Sample((int)cursor, (int)z, d, c, (f & 0x10000) == 0));
                        cursor += z;
                    }
                }

                int added = track.Samples.Count - first;
                if (added == 0) continue;
                chunks.Add(new Chunk(track, first, added, decodeTime / (double)track.Timescale));
                for (int s = first; s < track.Samples.Count; s++) decodeTime += track.Samples[s].Duration;
            }
        }
        return chunks;
    }

    static byte[] Movie(byte[] video, List<Track> tracks, long shift, bool large)
    {
        var videoMoov = Children(video, 0, video.Length).First(box => box.Type == "moov");
        var mvhdBox = Children(video, videoMoov.Payload, videoMoov.End).FirstOrDefault(box => box.Type == "mvhd");
        if (mvhdBox.Size == 0) throw new InvalidDataException("the video has no movie header");
        byte[] mvhd = Slice(video, mvhdBox);
        bool v1 = mvhd[8] == 1;
        long movieScale = Math.Max(1, U32(mvhd, v1 ? 28 : 20));
        long movieDuration = tracks.Max(track => track.MediaDuration * movieScale / track.Timescale);
        WriteDuration(mvhd, v1 ? 32 : 24, v1, movieDuration);
        Be32((uint)tracks.Count + 1).CopyTo(mvhd, mvhd.Length - 4);

        var parts = new List<byte[]> { mvhd };
        for (int i = 0; i < tracks.Count; i++)
            parts.Add(Trak(tracks[i], (uint)i + 1, tracks[i].MediaDuration * movieScale / tracks[i].Timescale, shift, large));
        return Container("moov", parts);
    }

    static byte[] Trak(Track track, uint id, long movieDuration, long shift, bool large)
    {
        byte[] data = track.Data;
        return Container("trak", Children(data, track.Trak.Payload, track.Trak.End).Select(child => child.Type switch
        {
            "tkhd" => Tkhd(Slice(data, child), id, movieDuration),
            "edts" => Edts(Slice(data, child), movieDuration),
            "mdia" => Container("mdia", Children(data, child.Payload, child.End).Select(media => media.Type switch
            {
                "mdhd" => Mdhd(Slice(data, media), track.MediaDuration),
                "minf" => Container("minf", Children(data, media.Payload, media.End).Select(info =>
                    info.Type == "stbl" ? Stbl(track, data, info, shift, large) : Slice(data, info))),
                _ => Slice(data, media),
            })),
            _ => Slice(data, child),
        }));
    }

    static byte[] Tkhd(byte[] tkhd, uint id, long duration)
    {
        bool v1 = tkhd[8] == 1;
        Be32(id).CopyTo(tkhd, v1 ? 28 : 20);
        WriteDuration(tkhd, v1 ? 36 : 28, v1, duration);
        return tkhd;
    }

    static byte[] Mdhd(byte[] mdhd, long duration)
    {
        bool v1 = mdhd[8] == 1;
        WriteDuration(mdhd, v1 ? 32 : 24, v1, duration);
        return mdhd;
    }

    static byte[] Edts(byte[] edts, long duration)
    {
        foreach (var elst in Children(edts, 8, edts.Length).Where(box => box.Type == "elst"))
        {
            bool v1 = edts[elst.Payload] == 1;
            int entry = v1 ? 20 : 12;
            uint count = U32(edts, elst.Payload + 4);
            for (int i = 0, at = elst.Payload + 8; i < count && at + entry <= elst.End; i++, at += entry)
                if ((v1 ? U64(edts, at) : U32(edts, at)) == 0) WriteDuration(edts, at, v1, duration);
        }
        return edts;
    }

    static byte[] Stbl(Track track, byte[] data, Box stbl, long shift, bool large)
    {
        var stsd = Children(data, stbl.Payload, stbl.End).FirstOrDefault(box => box.Type == "stsd");
        if (stsd.Size == 0) throw new InvalidDataException("a track has no sample description");
        var samples = track.Samples;
        var tables = new List<byte[]> { Slice(data, stsd) };

        tables.Add(Table("stts", 0, RunLength(samples.Select(sample => sample.Duration)).Select(run => new[] { run.Count, run.Value })));

        if (samples.Any(sample => sample.CompositionOffset != 0))
        {
            byte version = (byte)(samples.Any(sample => sample.CompositionOffset < 0) ? 1 : 0);
            tables.Add(Table("ctts", version, RunLength(samples.Select(sample => (uint)sample.CompositionOffset)).Select(run => new[] { run.Count, run.Value })));
        }

        if (samples.Any(sample => !sample.Sync))
            tables.Add(Table("stss", 0, samples.Select((sample, index) => (sample, index)).Where(s => s.sample.Sync).Select(s => new[] { (uint)s.index + 1 })));

        var stsc = new List<uint[]>();
        for (int i = 0; i < track.ChunkCounts.Count; i++)
            if (stsc.Count == 0 || stsc[^1][1] != track.ChunkCounts[i]) stsc.Add([(uint)i + 1, (uint)track.ChunkCounts[i], 1]);
        tables.Add(Table("stsc", 0, stsc));

        tables.Add(FullBox("stsz", 0, Words([0, (uint)samples.Count, .. samples.Select(sample => (uint)sample.Size)])));

        if (large)
        {
            using var co64 = new MemoryStream();
            co64.Write(Be32((uint)track.ChunkOffsets.Count));
            foreach (long offset in track.ChunkOffsets) co64.Write(Be64((ulong)(offset + shift)));
            tables.Add(FullBox("co64", 0, co64.ToArray()));
        }
        else
        {
            tables.Add(Table("stco", 0, track.ChunkOffsets.Select(offset => new[] { (uint)(offset + shift) })));
        }

        return Container("stbl", tables);
    }

    static byte[] Table(string type, byte version, IEnumerable<uint[]> entries)
    {
        var list = entries.ToList();
        return FullBox(type, version, Words([(uint)list.Count, .. list.SelectMany(entry => entry)]));
    }

    static byte[] Words(uint[] words)
    {
        byte[] bytes = new byte[words.Length * 4];
        for (int i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4), words[i]);
        return bytes;
    }

    static List<(uint Count, uint Value)> RunLength(IEnumerable<uint> values)
    {
        var runs = new List<(uint Count, uint Value)>();
        foreach (uint value in values)
            if (runs.Count > 0 && runs[^1].Value == value) runs[^1] = (runs[^1].Count + 1, value);
            else runs.Add((1, value));
        return runs;
    }

    static void WriteDuration(byte[] box, int at, bool wide, long duration)
    {
        if (wide) Be64((ulong)duration).CopyTo(box, at);
        else Be32((uint)Math.Min(duration, uint.MaxValue)).CopyTo(box, at);
    }

    static Box? Find(byte[] data, Box parent, params string[] path)
    {
        Box current = parent;
        foreach (string type in path)
        {
            var child = Children(data, current.Payload, current.End).FirstOrDefault(box => box.Type == type);
            if (child.Size == 0) return null;
            current = child;
        }
        return current;
    }

    static List<Box> Children(byte[] data, int start, int end)
    {
        var boxes = new List<Box>();
        int at = start;
        while (at + 8 <= end)
        {
            long size = U32(data, at);
            int header = 8;
            if (size == 1)
            {
                if (at + 16 > end) throw new InvalidDataException("broken MP4 box");
                size = (long)U64(data, at + 8);
                header = 16;
            }
            else if (size == 0)
            {
                size = end - at;
            }
            if (size < header || at + size > end) throw new InvalidDataException("broken MP4 box");
            boxes.Add(new Box(Encoding.ASCII.GetString(data, at + 4, 4), at, (int)size, header));
            at += (int)size;
        }
        return boxes;
    }

    static uint U24(byte[] data, int at) => (uint)(data[at] << 16 | data[at + 1] << 8 | data[at + 2]);
    static uint U32(byte[] data, int at) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
    static ulong U64(byte[] data, int at) => BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(at));

    static byte[] Be32(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    static byte[] Be64(ulong value)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    static byte[] Slice(byte[] data, Box box) => data[box.Start..box.End];

    static byte[] Container(string type, IEnumerable<byte[]> children)
    {
        using var payload = new MemoryStream();
        foreach (byte[] child in children) payload.Write(child);
        return Build(type, payload.ToArray());
    }

    static byte[] FullBox(string type, byte version, byte[] payload)
    {
        byte[] full = new byte[4 + payload.Length];
        full[0] = version;
        payload.CopyTo(full, 4);
        return Build(type, full);
    }

    static byte[] Build(string type, byte[] payload)
    {
        byte[] box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type, box.AsSpan(4));
        payload.CopyTo(box, 8);
        return box;
    }
}
