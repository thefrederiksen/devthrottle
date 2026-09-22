using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CcDirector.Core.Network;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Every Gateway client dials through <see cref="GatewayHttp.Handler"/>. The Gateway compresses its JSON
/// answers, but only for a client that advertises it can decode them, and this handler never did - so
/// every answer crossed the wire uncompressed. These tests put a real loopback socket on the other end:
/// they read the request exactly as it arrived, and answer with a genuinely compressed body.
/// </summary>
public class GatewayHttpCompressionTests
{
    private const string Json = "{\"sessions\":[{\"id\":\"one\"},{\"id\":\"two\"}]}";

    [Fact]
    public async Task Handler_Request_AdvertisesGzipAndBrotli()
    {
        var (request, _) = await RoundTripAsync("identity", Encoding.UTF8.GetBytes(Json));

        var acceptEncoding = request
            .Split("\r\n")
            .Single(line => line.StartsWith("Accept-Encoding:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("gzip", acceptEncoding, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("br", acceptEncoding, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Handler_GzipAnswer_IsDecoded()
    {
        var (_, body) = await RoundTripAsync("gzip", Compress(Json, s => new GZipStream(s, CompressionLevel.Optimal)));

        Assert.Equal(Json, body);
    }

    [Fact]
    public async Task Handler_BrotliAnswer_IsDecoded()
    {
        var (_, body) = await RoundTripAsync("br", Compress(Json, s => new BrotliStream(s, CompressionLevel.Optimal)));

        Assert.Equal(Json, body);
    }

    private static byte[] Compress(string text, Func<Stream, Stream> wrap)
    {
        using var buffer = new MemoryStream();
        using (var compressor = wrap(buffer))
            compressor.Write(Encoding.UTF8.GetBytes(text));
        return buffer.ToArray();
    }

    /// <summary>Serve one request on a loopback port; return the raw request head and the body the client read.</summary>
    private static async Task<(string Request, string Body)> RoundTripAsync(string contentEncoding, byte[] payload)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serve = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var head = new StringBuilder();
                var buffer = new byte[4096];
                while (!head.ToString().Contains("\r\n\r\n"))
                {
                    var read = await stream.ReadAsync(buffer);
                    if (read == 0) break;
                    head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                var encodingHeader = contentEncoding == "identity" ? "" : $"Content-Encoding: {contentEncoding}\r\n";
                var responseHead = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" + encodingHeader +
                                   $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(responseHead));
                await stream.WriteAsync(payload);
                await stream.FlushAsync();
                return head.ToString();
            });

            using var http = new HttpClient(GatewayHttp.Handler()) { Timeout = TimeSpan.FromSeconds(10) };
            var body = await http.GetStringAsync($"http://127.0.0.1:{port}/sessions");
            return (await serve, body);
        }
        finally
        {
            listener.Stop();
        }
    }
}
