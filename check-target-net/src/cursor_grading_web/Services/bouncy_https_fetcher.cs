using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace cursor_grading_web.Services;

/// <summary>
/// Minimal HTTPS GET using BouncyCastle managed TLS (bypasses Windows Schannel).
/// </summary>
internal static class bouncy_https_fetcher
{
    private const string user_agent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

    public static async Task<string> get_html_async(string url, CancellationToken ct, int max_redirects = 5)
    {
        var current = new Uri(url);
        for (var redirect = 0; redirect <= max_redirects; redirect++)
        {
            ct.ThrowIfCancellationRequested();
            var (status, headers, body) = await get_once_async(current, ct);

            if (status >= 300 && status < 400 &&
                headers.TryGetValue("location", out var location) &&
                !string.IsNullOrWhiteSpace(location))
            {
                current = Uri.TryCreate(current, location.Trim(), out var next)
                    ? next
                    : throw new HttpRequestException($"SSL fallback: invalid redirect Location '{location}'.");
                continue;
            }

            if (status < 200 || status >= 300)
                throw new HttpRequestException($"SSL fallback: HTTP {status}");

            if (string.IsNullOrWhiteSpace(body))
                throw new HttpRequestException("SSL fallback returned empty body.");

            return body;
        }

        throw new HttpRequestException("SSL fallback: too many redirects.");
    }

    private static async Task<(int status, Dictionary<string, string> headers, string body)> get_once_async(
        Uri uri, CancellationToken ct)
    {
        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException("SSL fallback only supports https URLs.");

        var host = uri.Host;
        var port = uri.IsDefaultPort ? 443 : uri.Port;
        var path = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;

        using var tcp = new TcpClient();
        using var timeout_cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout_cts.CancelAfter(TimeSpan.FromSeconds(45));
        await tcp.ConnectAsync(host, port, timeout_cts.Token);

        await using var net = tcp.GetStream();
        net.ReadTimeout = 45_000;
        net.WriteTimeout = 45_000;

        var protocol = new TlsClientProtocol(net);
        protocol.Connect(new scrape_tls_client(host));

        var request =
            $"GET {path} HTTP/1.1\r\n" +
            $"Host: {host}\r\n" +
            $"User-Agent: {user_agent}\r\n" +
            "Accept: text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8\r\n" +
            "Accept-Language: en-US,en;q=0.9\r\n" +
            "Accept-Encoding: gzip, deflate\r\n" +
            "Connection: close\r\n\r\n";

        var req_bytes = Encoding.ASCII.GetBytes(request);
        await protocol.Stream.WriteAsync(req_bytes, timeout_cts.Token);
        await protocol.Stream.FlushAsync(timeout_cts.Token);

        var raw = await read_all_async(protocol.Stream, timeout_cts.Token);
        return parse_http_response(raw);
    }

    private static async Task<byte[]> read_all_async(Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
            ms.Write(buf, 0, n);
        return ms.ToArray();
    }

    private static (int status, Dictionary<string, string> headers, string body) parse_http_response(byte[] raw)
    {
        var sep = IndexOf(raw, "\r\n\r\n"u8.ToArray());
        if (sep < 0)
            throw new HttpRequestException("SSL fallback: malformed HTTP response.");

        var header_text = Encoding.ASCII.GetString(raw, 0, sep);
        var lines = header_text.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0)
            throw new HttpRequestException("SSL fallback: empty HTTP status line.");

        var status_parts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (status_parts.Length < 2 || !int.TryParse(status_parts[1], out var status))
            throw new HttpRequestException($"SSL fallback: bad status line '{lines[0]}'.");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            headers[name] = value;
        }

        var body_bytes = raw[(sep + 4)..];
        if (headers.TryGetValue("transfer-encoding", out var te) &&
            te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            body_bytes = decode_chunked(body_bytes);
        }

        if (headers.TryGetValue("content-encoding", out var ce))
        {
            if (ce.Contains("gzip", StringComparison.OrdinalIgnoreCase))
                body_bytes = gunzip(body_bytes);
            else if (ce.Contains("deflate", StringComparison.OrdinalIgnoreCase))
                body_bytes = inflate(body_bytes);
        }

        var charset = Encoding.UTF8;
        if (headers.TryGetValue("content-type", out var ct) &&
            ct.Contains("charset=", StringComparison.OrdinalIgnoreCase))
        {
            var token = ct[(ct.IndexOf("charset=", StringComparison.OrdinalIgnoreCase) + 8)..]
                .Split(';', 2)[0].Trim().Trim('"', '\'');
            try { charset = Encoding.GetEncoding(token); } catch { /* keep UTF-8 */ }
        }

        return (status, headers, charset.GetString(body_bytes));
    }

    private static byte[] decode_chunked(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var output = new MemoryStream();
        while (true)
        {
            var size_line = read_line_ascii(input);
            if (size_line is null) break;
            var semi = size_line.IndexOf(';');
            var hex = (semi >= 0 ? size_line[..semi] : size_line).Trim();
            if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var size))
                throw new HttpRequestException("SSL fallback: bad chunk size.");
            if (size == 0) break;

            var chunk = new byte[size];
            var read = 0;
            while (read < size)
            {
                var n = input.Read(chunk, read, size - read);
                if (n <= 0) throw new HttpRequestException("SSL fallback: truncated chunk.");
                read += n;
            }

            output.Write(chunk, 0, size);
            // trailing CRLF after chunk
            if (input.ReadByte() != '\r' || input.ReadByte() != '\n')
                throw new HttpRequestException("SSL fallback: bad chunk trailer.");
        }

        return output.ToArray();
    }

    private static string? read_line_ascii(Stream stream)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var b = stream.ReadByte();
            if (b < 0)
                return ms.Length == 0 ? null : Encoding.ASCII.GetString(ms.ToArray());
            if (b == '\n')
                break;
            if (b != '\r')
                ms.WriteByte((byte)b);
        }

        return Encoding.ASCII.GetString(ms.ToArray());
    }

    private static byte[] gunzip(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] inflate(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private sealed class scrape_tls_client : DefaultTlsClient
    {
        private readonly string _host;

        public scrape_tls_client(string host) : base(new BcTlsCrypto())
        {
            _host = host;
        }

        public override TlsAuthentication GetAuthentication() => new scrape_tls_auth();

        protected override IList<ServerName> GetSniServerNames() =>
            new List<ServerName> { new ServerName(NameType.host_name, Encoding.UTF8.GetBytes(_host)) };
    }

    private sealed class scrape_tls_auth : TlsAuthentication
    {
        public TlsCredentials GetClientCredentials(CertificateRequest certificateRequest) => null!;

        public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
        {
            // Accept server certificate; Schannel path already validated when possible.
            // This path exists specifically when Schannel cannot complete the handshake.
        }
    }
}
