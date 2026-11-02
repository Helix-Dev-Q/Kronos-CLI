using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace KronosCLI;

internal sealed class RedirectProxy(TcpListener listener, CertificateAuthority authority, Options options, bool live) : IDisposable
{
    static readonly string[] EpicDomains =
        ["epicgames.com", "epicgames.net", "epicgames.dev", "akamaized.net", "superawesome.com", "cosmo.fdeb.live.use1a.on.epicgames.com"];

    static readonly string[] PassThroughDomains = ["eac-prod.on.epicgames.com", "easyanticheat.net", "eac-cdn.com", "battleye.com"];

    const long CaptureLimit = 25 * 1024 * 1024;
    const long FullDownloadLimit = 256 * 1024 * 1024;

    static readonly string[] BulkPaths =
    [
        "/Builds/Fortnite/Content/CloudDir/",
        "/ias/fortnite/",
        "/iad/fortnite/",
        "/valkyrie/cooked-content/",
    ];

    static readonly string[] ContentPaths =
    [
        "/launcher/api/public/distributionpoints",
        "/launcher/api/public/assets/",
        .. BulkPaths,
    ];

    static readonly HashSet<string> SkippedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authorization", "Proxy-Authenticate",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Expect", "Content-Length",
    };

    static readonly HashSet<string> ConditionalHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "If-None-Match", "If-Modified-Since",
    };

    static readonly HashSet<string> RangeHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Range", "If-Range",
    };

    static readonly HashSet<string> DecodableEncodings = new(StringComparer.OrdinalIgnoreCase)
    {
        "gzip", "x-gzip", "deflate", "br", "identity",
    };

    static readonly UriCreationOptions RawPath = new() { DangerousDisablePathAndQueryCanonicalization = true };

    const string Established = "HTTP/1.1 200 Connection Established\r\n\r\n";

    static readonly string[] ProxyVariables = ["HTTP_PROXY", "http_proxy", "HTTPS_PROXY", "https_proxy", "ALL_PROXY", "all_proxy"];
    static readonly string[] NoProxyVariables = ["NO_PROXY", "no_proxy"];
    const string LocalAddresses = "localhost,127.0.0.1,::1";

    public static void ApplyTo(IDictionary<string, string?> environment, string? proxyUrl)
    {
        foreach (string key in ProxyVariables)
            if (proxyUrl != null) environment[key] = proxyUrl;
            else environment.Remove(key);
        foreach (string key in NoProxyVariables)
            if (proxyUrl != null) environment[key] = LocalAddresses;
            else environment.Remove(key);
    }

    readonly CancellationTokenSource stop = new();
    readonly ConcurrentDictionary<string, bool> failedHandshakes = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim fullDownloads = new(2);
    readonly HttpClient http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ActivityHeadersPropagator = null,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        RequestHeaderEncodingSelector = (_, _) => Encoding.Latin1,
        ResponseHeaderEncodingSelector = (_, _) => Encoding.Latin1,
    })
    {
        Timeout = TimeSpan.FromMinutes(2),
    };

    int backendDown;
    volatile Reversal? reversal;

    string Backend => options.Backend.TrimEnd('/');

    public Reversal? Capture => reversal;
    public void StartCapture(Reversal target) => reversal = target;
    public void StopCapture() => reversal = null;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public static RedirectProxy? Start(Options options)
    {
        if (!IPEndPoint.TryParse(options.Proxy, out var endpoint) || endpoint.Port == 0)
        {
            Log.Error($"Proxy:   {options.Proxy} is not an ip:port to listen on");
            return null;
        }
        if (!Uri.TryCreate(options.Backend, UriKind.Absolute, out _))
        {
            Log.Error($"Proxy:   {options.Backend} is not a backend URL");
            return null;
        }
        return Open(options, endpoint, live: false);
    }

    public static RedirectProxy? StartLive(Options options) => Open(options, new IPEndPoint(IPAddress.Loopback, 0), live: true);

    static RedirectProxy? Open(Options options, IPEndPoint endpoint, bool live)
    {
        CertificateAuthority? authority;
        try
        {
            authority = CertificateAuthority.FromFiddler();
        }
        catch (Exception e)
        {
            Log.Error($"Proxy:   couldn't load the Fiddler root certificate: {e.Message}");
            return null;
        }
        if (authority == null)
        {
            Log.Error(@"Proxy:   no trusted Fiddler root certificate with a private key in CurrentUser\My, so HTTPS can't be redirected");
            return null;
        }

        var listener = new TcpListener(endpoint);
        try
        {
            listener.Start();
        }
        catch (SocketException e)
        {
            authority.Dispose();
            if (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
                Log.Warn($"Proxy:   {endpoint} is already taken (Fiddler?) - the game will go through that instead");
            else
                Log.Error($"Proxy:   can't listen at {endpoint}: {e.Message}");
            return null;
        }

        var proxy = new RedirectProxy(listener, authority, options, live);
        _ = proxy.AcceptLoop();
        if (live) Log.Ok($"[Kronos Reversal] Fortnite-only proxy listening at 127.0.0.1:{proxy.Port}");
        else Log.Ok($"Proxy:   listening at {options.Proxy}");
        return proxy;
    }

    async Task AcceptLoop()
    {
        while (true)
        {
            try
            {
                _ = Serve(await listener.AcceptTcpClientAsync(stop.Token));
            }
            catch (Exception) when (stop.IsCancellationRequested) { return; }
            catch (SocketException) { }
        }
    }

    async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var owner = new Owner(client.Client.RemoteEndPoint as IPEndPoint, client.Client.LocalEndPoint as IPEndPoint);
                await ServeRequests(client.GetStream(), null, owner);
            }
            catch (Exception e) when (e is IOException or SocketException or InvalidDataException or AuthenticationException
                                          or ObjectDisposedException or OperationCanceledException) { }
            catch (Exception e)
            {
                Log.Detail($"Proxy: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    async Task ServeRequests(Stream stream, string? origin, Owner owner)
    {
        var reader = new HttpReader(stream);
        while (await reader.ReadHead() is { } head)
        {
            var request = HttpRequest.Parse(head);

            if (request.Method == "CONNECT" && origin == null)
            {
                await Connect(reader, request.Target, owner);
                return;
            }

            string url = !request.Target.StartsWith('/') ? request.Target
                : origin != null ? origin + request.Target
                : $"http://{request.Header("Host")}{request.Target}";
            if (!Uri.TryCreate(url, in RawPath, out var target))
            {
                await WriteError(stream, 400, "Bad Request", $"Can't route {request.Target}");
                return;
            }

            if (!await Forward(reader, request, target, owner)) return;
        }
    }

    async Task Connect(HttpReader client, string authorityText, Owner owner)
    {
        if (!Uri.TryCreate($"https://{authorityText}", UriKind.Absolute, out var target))
        {
            await WriteError(client.Stream, 400, "Bad Request", $"Can't connect to {authorityText}");
            return;
        }

        string host = target.DnsSafeHost;
        if (Intercept(host, target.Port, owner))
        {
            await WriteRaw(client.Stream, Established);
            await using var tls = new SslStream(client.Stream, leaveInnerStreamOpen: true);
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = authority.For(host),
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                });
            }
            catch (Exception e) when (e is AuthenticationException or IOException)
            {
                if (failedHandshakes.TryAdd(host, true))
                    Log.Warn(reversal != null && !IsEpicHost(host)
                        ? $"Proxy: Fortnite didn't accept Kronos's certificate for {host} ({e.Message}) - its connections pass through uncaptured from now on"
                        : $"Proxy: TLS handshake for {host} failed - does the game trust the Fiddler root? ({e.Message})");
                return;
            }

            await ServeRequests(tls, $"https://{target.Authority}", owner);
            return;
        }

        using var upstream = new TcpClient();
        try
        {
            await upstream.ConnectAsync(host, target.Port, stop.Token);
        }
        catch (SocketException e)
        {
            await WriteError(client.Stream, 502, "Bad Gateway", e.Message);
            return;
        }

        string who = reversal != null ? $" ({owner.Name ?? "unknown process"})" : "";
        Log.Detail($"Proxy: CONNECT {host}:{target.Port} -> passthrough{who}");
        await WriteRaw(client.Stream, Established);
        await Pipe(client, upstream.GetStream());
    }

    async Task<bool> Forward(HttpReader client, HttpRequest request, Uri target, Owner owner)
    {
        var rev = reversal;
        bool content = IsContentPath(target);
        bool redirected = !live && rev == null && !content && IsEpicHost(target.DnsSafeHost);
        bool fortnite = rev != null && !InDomains(target.DnsSafeHost, PassThroughDomains) && owner.IsFortnite;
        bool capture = fortnite && !InPaths(target, BulkPaths);
        if (fortnite && !capture) rev!.Requested(target);
        var destination = redirected ? new Uri(Backend + target.PathAndQuery, in RawPath) : target;
        string route = $"{request.Method} {target.Host}{target.AbsolutePath} -> {(redirected ? "backend" : capture ? "capture" : "upstream")}";

        if (request.Header("Upgrade") != null)
        {
            Log.Detail($"Proxy: {route} (upgrade)");
            await Upgrade(client, request, destination);
            return false;
        }

        if (string.Equals(request.Header("Expect"), "100-continue", StringComparison.OrdinalIgnoreCase))
            await WriteRaw(client.Stream, "HTTP/1.1 100 Continue\r\n\r\n");
        byte[]? body = await client.ReadBody(request);
        bool keepAlive = request.KeepAlive;

        using var message = new HttpRequestMessage(new HttpMethod(request.Method), destination);
        if (body != null) message.Content = new ByteArrayContent(body);
        foreach (var (name, value) in request.Headers)
        {
            if (SkippedHeaders.Contains(name) || capture && ConditionalHeaders.Contains(name)) continue;
            string sent = capture && name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase) ? Decodable(value) : value;
            if (!message.Headers.TryAddWithoutValidation(name, sent))
                message.Content?.Headers.TryAddWithoutValidation(name, sent);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, stop.Token);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Log.Detail($"Proxy: {route} failed: {e.Message}");
            if (redirected && Interlocked.Exchange(ref backendDown, 1) == 0)
                Log.Warn($"Proxy: backend at {Backend} isn't answering ({e.Message})", "backend");
            await WriteError(client.Stream, 502, "Bad Gateway", e.Message);
            return keepAlive;
        }

        using (response)
        {
            if (redirected && Interlocked.Exchange(ref backendDown, 0) == 1)
            {
                Log.Resolve("backend");
                Log.Ok($"Proxy: backend at {Backend} is answering again");
            }
            Log.Detail($"Proxy: {route} {(int)response.StatusCode}");

            if (capture && CanCapture(request, response))
            {
                byte[] bytes = await response.Content.ReadAsByteArrayAsync(stop.Token);
                rev!.Save(request.Method, target, response.Content.Headers.ContentType?.ToString(),
                    string.Join(",", response.Content.Headers.ContentEncoding), bytes);
                await WriteBuffered(client.Stream, request, response, bytes, keepAlive);
            }
            else
            {
                await WriteResponse(client.Stream, request, response, keepAlive);
                if (capture && FullDownloadReason(request, response) is { } reason) DownloadInFull(rev!, request, target, reason);
            }
        }
        return keepAlive;
    }

    static bool CanCapture(HttpRequest request, HttpResponseMessage response)
    {
        if (request.Method is "HEAD" or "CONNECT") return false;
        if ((int)response.StatusCode is < 200 or >= 300 or 204 or 206) return false;
        return response.Content.Headers.ContentLength is not > CaptureLimit;
    }

    static string? FullDownloadReason(HttpRequest request, HttpResponseMessage response)
    {
        if (request.Method is not ("GET" or "HEAD")) return null;
        return (int)response.StatusCode switch
        {
            206 => "Fortnite only streamed part of it",
            304 => "Fortnite used its cached copy",
            200 when request.Method == "HEAD" => "Fortnite only checked it",
            >= 200 and < 300 and not 204 when response.Content.Headers.ContentLength > CaptureLimit => "too big to keep while passing it on",
            _ => null,
        };
    }

    void DownloadInFull(Reversal rev, HttpRequest request, Uri target, string reason)
    {
        if (!rev.ClaimFullDownload(target)) return;
        var headers = request.Headers
            .Where(h => !SkippedHeaders.Contains(h.Key) && !ConditionalHeaders.Contains(h.Key) && !RangeHeaders.Contains(h.Key))
            .ToList();
        string label = target.Host + target.AbsolutePath;

        _ = Task.Run(async () =>
        {
            try
            {
                await fullDownloads.WaitAsync(stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, target);
                foreach (var (name, value) in headers)
                    message.Headers.TryAddWithoutValidation(name,
                        name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase) ? Decodable(value) : value);

                using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, stop.Token);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    Log.Detail($"Proxy: full download of {label} got {(int)response.StatusCode}, not saved");
                    return;
                }
                if (await ReadLimited(response.Content, FullDownloadLimit, stop.Token) is not { } bytes)
                {
                    Log.Warn($"[Kronos Reversal] {label} is over {FullDownloadLimit / 1048576} MB, not saved");
                    return;
                }

                Log.Info($"[Kronos Reversal] downloaded {label} in full ({reason})");
                rev.Save("GET", target, response.Content.Headers.ContentType?.ToString(),
                    string.Join(",", response.Content.Headers.ContentEncoding), bytes);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
            {
                Log.Detail($"Proxy: couldn't download {label} in full: {e.Message}");
            }
            finally
            {
                fullDownloads.Release();
            }
        });
    }

    static async Task<byte[]?> ReadLimited(HttpContent content, long limit, CancellationToken token)
    {
        if (content.Headers.ContentLength > limit) return null;
        await using var stream = await content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > limit) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    static string Decodable(string acceptEncoding)
    {
        var kept = acceptEncoding.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => DecodableEncodings.Contains(token.Split(';')[0].Trim()));
        string value = string.Join(", ", kept);
        return value.Length > 0 ? value : "identity";
    }

    static bool IsContentPath(Uri target) => InPaths(target, ContentPaths);

    static bool InPaths(Uri target, string[] paths) =>
        paths.Any(path => target.PathAndQuery.Contains(path, StringComparison.Ordinal));

    static async Task WriteBuffered(Stream client, HttpRequest request, HttpResponseMessage response, byte[] body, bool keepAlive)
    {
        int status = (int)response.StatusCode;
        bool hasBody = request.Method != "HEAD" && status is >= 200 and not 204 and not 304;

        var head = new StringBuilder($"HTTP/1.1 {status} {response.ReasonPhrase}\r\n");
        foreach (var header in response.Headers.NonValidated.Concat(response.Content.Headers.NonValidated))
            if (!SkippedHeaders.Contains(header.Key))
                foreach (var value in header.Value)
                    head.Append($"{header.Key}: {value}\r\n");
        head.Append($"Content-Length: {(hasBody ? body.Length : 0)}\r\n");
        if (!keepAlive) head.Append("Connection: close\r\n");
        await WriteRaw(client, head.Append("\r\n").ToString());

        if (hasBody) await client.WriteAsync(body);
    }

    async Task Upgrade(HttpReader client, HttpRequest request, Uri destination)
    {
        using var upstream = new TcpClient();
        try
        {
            await upstream.ConnectAsync(destination.DnsSafeHost, destination.Port, stop.Token);
        }
        catch (SocketException e)
        {
            await WriteError(client.Stream, 502, "Bad Gateway", e.Message);
            return;
        }

        Stream server = upstream.GetStream();
        if (destination.Scheme == Uri.UriSchemeHttps)
        {
            var tls = new SslStream(server);
            await tls.AuthenticateAsClientAsync(destination.DnsSafeHost);
            server = tls;
        }

        await using (server)
        {
            var head = new StringBuilder($"{request.Method} {destination.PathAndQuery} HTTP/1.1\r\nHost: {destination.Authority}\r\n");
            foreach (var (name, value) in request.Headers)
                if (!name.Equals("Host", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase))
                    head.Append($"{name}: {value}\r\n");
            await WriteRaw(server, head.Append("\r\n").ToString());
            await Pipe(client, server);
        }
    }

    static async Task WriteResponse(Stream client, HttpRequest request, HttpResponseMessage response, bool keepAlive)
    {
        int status = (int)response.StatusCode;
        bool hasBody = request.Method != "HEAD" && status is >= 200 and not 204 and not 304;
        long? length = response.Content.Headers.ContentLength;

        var head = new StringBuilder($"HTTP/1.1 {status} {response.ReasonPhrase}\r\n");
        foreach (var header in response.Headers.NonValidated.Concat(response.Content.Headers.NonValidated))
            if (!SkippedHeaders.Contains(header.Key))
                foreach (var value in header.Value)
                    head.Append($"{header.Key}: {value}\r\n");
        if (length != null) head.Append($"Content-Length: {length}\r\n");
        else if (hasBody) head.Append("Transfer-Encoding: chunked\r\n");
        if (!keepAlive) head.Append("Connection: close\r\n");
        await WriteRaw(client, head.Append("\r\n").ToString());

        if (!hasBody) return;
        await using var content = await response.Content.ReadAsStreamAsync();
        if (length != null) await content.CopyToAsync(client);
        else await CopyChunked(content, client);
    }

    static async Task CopyChunked(Stream source, Stream destination)
    {
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            await WriteRaw(destination, $"{read:X}\r\n");
            await destination.WriteAsync(buffer.AsMemory(0, read));
            await WriteRaw(destination, "\r\n");
        }
        await WriteRaw(destination, "0\r\n\r\n");
    }

    static async Task Pipe(HttpReader client, Stream server)
    {
        byte[] buffered = client.TakeBuffered();
        if (buffered.Length > 0) await server.WriteAsync(buffered);
        await Task.WhenAny(client.Stream.CopyToAsync(server), server.CopyToAsync(client.Stream));
    }

    static Task WriteError(Stream stream, int status, string reason, string message)
    {
        byte[] body = Encoding.UTF8.GetBytes(message);
        return WriteRaw(stream,
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\n\r\n", body);
    }

    static async Task WriteRaw(Stream stream, string text, byte[]? body = null)
    {
        await stream.WriteAsync(Encoding.Latin1.GetBytes(text));
        if (body != null) await stream.WriteAsync(body);
    }

    bool Intercept(string host, int port, Owner owner) =>
        reversal != null ? Capturable(host, port) && owner.IsFortnite : !live && IsEpicHost(host);

    bool Capturable(string host, int port) =>
        IsEpicHost(host) || port == 443 && !InDomains(host, PassThroughDomains) && !failedHandshakes.ContainsKey(host);

    static bool IsEpicHost(string host) => InDomains(host, EpicDomains) && !InDomains(host, PassThroughDomains);

    static bool InDomains(string host, string[] domains) =>
        domains.Any(domain => host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                              host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
        http.Dispose();
        authority.Dispose();
    }

    sealed class Owner(IPEndPoint? client, IPEndPoint? proxy)
    {
        string? name;
        bool resolved;

        public string? Name
        {
            get
            {
                if (!resolved)
                {
                    name = client != null && proxy != null ? ProcessScope.OwnerName(client, proxy) : null;
                    resolved = true;
                }
                return name;
            }
        }

        public bool IsFortnite => ProcessScope.IsFortnite(Name);
    }
}

internal sealed record HttpRequest(string Method, string Target, string Version, List<KeyValuePair<string, string>> Headers)
{
    public string? Header(string name) =>
        Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    public bool KeepAlive
    {
        get
        {
            string connection = $"{Header("Connection")},{Header("Proxy-Connection")}";
            return Version == "HTTP/1.0"
                ? connection.Contains("keep-alive", StringComparison.OrdinalIgnoreCase)
                : !connection.Contains("close", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static HttpRequest Parse(string head)
    {
        string[] lines = head.Split("\r\n");
        string[] parts = lines[0].Split(' ');
        if (parts.Length != 3) throw new InvalidDataException($"Bad request line: {lines[0]}");

        var headers = new List<KeyValuePair<string, string>>();
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) headers.Add(new(line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }
        return new HttpRequest(parts[0], parts[1], parts[2], headers);
    }
}

internal sealed class HttpReader(Stream stream)
{
    static readonly byte[] LineEnd = "\r\n"u8.ToArray();
    static readonly byte[] HeadEnd = "\r\n\r\n"u8.ToArray();

    readonly byte[] buffer = new byte[64 * 1024];
    int start, end;

    public Stream Stream => stream;

    public Task<string?> ReadHead() => ReadUntil(HeadEnd);

    public async Task<byte[]?> ReadBody(HttpRequest request)
    {
        if (request.Header("Transfer-Encoding")?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true)
            return await ReadChunked();
        return int.TryParse(request.Header("Content-Length"), out int length) ? await ReadBytes(length) : null;
    }

    public byte[] TakeBuffered()
    {
        byte[] taken = buffer[start..end];
        start = end = 0;
        return taken;
    }

    async Task<byte[]> ReadChunked()
    {
        using var body = new MemoryStream();
        while (true)
        {
            string line = await ReadUntil(LineEnd) ?? throw new IOException("Connection closed mid-body");
            int size = Convert.ToInt32(line.Split(';')[0].Trim(), 16);
            if (size == 0) break;
            body.Write(await ReadBytes(size));
            await ReadUntil(LineEnd);
        }
        while (await ReadUntil(LineEnd) is { Length: > 0 }) { }
        return body.ToArray();
    }

    async Task<byte[]> ReadBytes(int count)
    {
        byte[] result = new byte[count];
        int buffered = Math.Min(count, end - start);
        Array.Copy(buffer, start, result, 0, buffered);
        start += buffered;
        if (buffered < count) await stream.ReadExactlyAsync(result.AsMemory(buffered));
        return result;
    }

    async Task<string?> ReadUntil(byte[] delimiter)
    {
        while (true)
        {
            int index = buffer.AsSpan(start, end - start).IndexOf(delimiter);
            if (index >= 0)
            {
                string text = Encoding.Latin1.GetString(buffer, start, index);
                start += index + delimiter.Length;
                return text;
            }

            if (start > 0)
            {
                Array.Copy(buffer, start, buffer, 0, end - start);
                end -= start;
                start = 0;
            }
            if (end == buffer.Length) throw new InvalidDataException("Request head is too large");

            int read = await stream.ReadAsync(buffer.AsMemory(end));
            if (read == 0) return end == 0 ? null : throw new IOException("Connection closed mid-request");
            end += read;
        }
    }
}

internal sealed class CertificateAuthority(X509Certificate2 root, RSA key) : IDisposable
{
    const string FiddlerRoot = "CN=DO_NOT_TRUST_FiddlerRoot";
    const string KeyName = "KronosCLI.Proxy";

    readonly ConcurrentDictionary<string, Lazy<X509Certificate2>> issued = new(StringComparer.OrdinalIgnoreCase);

    public static CertificateAuthority? FromFiddler()
    {
        using var trusted = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        using var personal = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        trusted.Open(OpenFlags.ReadOnly);
        personal.Open(OpenFlags.ReadOnly);

        var root = personal.Certificates
            .Where(c => c.Subject.StartsWith(FiddlerRoot, StringComparison.Ordinal) && c.HasPrivateKey && c.NotAfter > DateTime.Now)
            .Where(c => trusted.Certificates.Find(X509FindType.FindByThumbprint, c.Thumbprint, false).Count > 0)
            .MaxBy(c => c.NotBefore);
        return root == null ? null : new CertificateAuthority(root, OpenKey());
    }

    static RSA OpenKey()
    {
        try
        {
            if (CngKey.Exists(KeyName)) return new RSACng(CngKey.Open(KeyName));
            return new RSACng(CngKey.Create(CngAlgorithm.Rsa, KeyName, new CngKeyCreationParameters
            {
                Parameters = { new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None) },
            }));
        }
        catch (CryptographicException) when (CngKey.Exists(KeyName))
        {
            return new RSACng(CngKey.Open(KeyName));
        }
    }

    public X509Certificate2 For(string host) =>
        issued.GetOrAdd(host, h => new Lazy<X509Certificate2>(() => Issue(h))).Value;

    X509Certificate2 Issue(string host)
    {
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var names = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(host, out var address)) names.AddIpAddress(address);
        else names.AddDnsName(host);

        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root, true, false));

        var now = DateTime.Now;
        var notBefore = now.AddDays(-1) < root.NotBefore ? root.NotBefore : now.AddDays(-1);
        var notAfter = now.AddDays(365) > root.NotAfter ? root.NotAfter : now.AddDays(365);

        using var certificate = request.Create(root, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));
        return certificate.CopyWithPrivateKey(key);
    }

    public void Dispose()
    {
        foreach (var certificate in issued.Values.Where(c => c.IsValueCreated)) certificate.Value.Dispose();
        root.Dispose();
        key.Dispose();
    }
}

internal sealed record ProxySettings(int Flags, string? Server, string? Bypass, string? AutoConfigUrl);

internal static class SystemProxy
{
    static readonly string BackupPath = Path.Combine(AppContext.BaseDirectory, "proxy-backup.json");

    public static void RestoreLeftover()
    {
        try
        {
            if (!File.Exists(BackupPath)) return;
            if (JsonSerializer.Deserialize<ProxySettings>(File.ReadAllText(BackupPath)) is { } settings) Apply(settings);
            File.Delete(BackupPath);
            Log.Info("Restored the Windows proxy settings an earlier version of Kronos left behind");
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Couldn't restore the Windows proxy settings from {BackupPath}: {e.Message}");
        }
    }

    static void Apply(ProxySettings settings)
    {
        IntPtr server = Marshal.StringToHGlobalUni(settings.Server);
        IntPtr bypass = Marshal.StringToHGlobalUni(settings.Bypass);
        IntPtr autoConfigUrl = Marshal.StringToHGlobalUni(settings.AutoConfigUrl);
        try
        {
            Call(
            [
                new() { dwOption = Native.INTERNET_PER_CONN_FLAGS, dwValue = settings.Flags },
                new() { dwOption = Native.INTERNET_PER_CONN_PROXY_SERVER, pszValue = server },
                new() { dwOption = Native.INTERNET_PER_CONN_PROXY_BYPASS, pszValue = bypass },
                new() { dwOption = Native.INTERNET_PER_CONN_AUTOCONFIG_URL, pszValue = autoConfigUrl },
            ]);
            Native.InternetSetOption(IntPtr.Zero, Native.INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            Native.InternetSetOption(IntPtr.Zero, Native.INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(server);
            Marshal.FreeHGlobal(bypass);
            Marshal.FreeHGlobal(autoConfigUrl);
        }
    }

    static void Call(Native.INTERNET_PER_CONN_OPTION[] options)
    {
        var pinned = GCHandle.Alloc(options, GCHandleType.Pinned);
        try
        {
            var list = new Native.INTERNET_PER_CONN_OPTION_LIST
            {
                dwSize = Marshal.SizeOf<Native.INTERNET_PER_CONN_OPTION_LIST>(),
                dwOptionCount = options.Length,
                pOptions = pinned.AddrOfPinnedObject(),
            };
            if (!Native.InternetSetOption(IntPtr.Zero, Native.INTERNET_OPTION_PER_CONNECTION_OPTION, ref list, list.dwSize))
                throw new Win32Exception();
        }
        finally
        {
            pinned.Free();
        }
    }
}
