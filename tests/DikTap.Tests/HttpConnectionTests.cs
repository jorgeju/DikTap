using System.Net;
using System.Net.Sockets;
using System.Text;
using DikTap.Services.Transcription;

namespace DikTap.Tests;

// Minimal TCP server that counts how many connections are opened: shows that WarmUpAsync preconnects
// and that each send uses Connection: close (no connection stays open after dictation).
public class HttpConnectionTests
{
    private sealed class CountingServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        public int Connections;
        public int Posts, Heads;
        public Uri Uri => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1/audio/transcriptions");

        public CountingServer()
        {
            _listener.Start();
            _ = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var c = await _listener.AcceptTcpClientAsync(_cts.Token);
                    Interlocked.Increment(ref Connections);
                    _ = Task.Run(() => Serve(c));
                }
            }
            catch (Exception) { }
        }

        private async Task Serve(TcpClient c)
        {
            using var _ = c;
            try
            {
                var stream = c.GetStream();
                var buf = new byte[65536];
                var head = new StringBuilder();
                while (true)
                {
                    head.Clear();
                    int bodyBytesInHead = 0;
                    while (!head.ToString().Contains("\r\n\r\n"))
                    {
                        int n = await stream.ReadAsync(buf, _cts.Token);
                        if (n == 0) return;
                        head.Append(Encoding.Latin1.GetString(buf, 0, n));
                    }
                    string text = head.ToString();
                    int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
                    bodyBytesInHead = text.Length - end;
                    bool post = text.StartsWith("POST", StringComparison.Ordinal);
                    bool close = text.Contains("Connection: close", StringComparison.OrdinalIgnoreCase);
                    if (post)
                    {
                        Interlocked.Increment(ref Posts);
                        int len = 0;
                        foreach (var line in text[..end].Split("\r\n"))
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) len = int.Parse(line[15..].Trim());
                        int remaining = len - bodyBytesInHead;
                        while (remaining > 0)
                        {
                            int n = await stream.ReadAsync(buf, _cts.Token);
                            if (n == 0) return;
                            remaining -= n;
                        }
                    }
                    else Interlocked.Increment(ref Heads);

                    string body = post ? "{\"text\":\"hola\"}" : "";
                    string resp = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n{(close ? "Connection: close\r\n" : "")}\r\n{(post ? body : "")}";
                    await stream.WriteAsync(Encoding.Latin1.GetBytes(resp), _cts.Token);
                    if (close) return;
                }
            }
            catch (Exception) { }
        }

        public void Dispose() { _cts.Cancel(); _listener.Stop(); }
    }

    private static Stream Wav() => new MemoryStream(new byte[2000]);
    private static readonly TranscriptionOptions Options = new("m", Array.Empty<string>(), Array.Empty<string>(), null);

    [Fact]
    public async Task WarmUp_opens_the_connection_and_the_post_reuses_it()
    {
        using var server = new CountingServer();
        using var http = OpenAITranscriptionClient.CreateHttpClient();
        var client = new OpenAITranscriptionClient(() => http, server.Uri);

        await client.WarmUpAsync(CancellationToken.None);
        Assert.Equal(1, server.Connections);
        string text = (await client.TranscribeAsync(Wav(), "sk-test", Options, CancellationToken.None)).Text;

        Assert.Equal("hola", text);
        Assert.Equal(1, server.Connections);
        Assert.Equal(1, server.Heads);
        Assert.Equal(1, server.Posts);
    }

    [Fact]
    public async Task Each_post_closes_its_connection_so_none_is_left_open_after_dictating()
    {
        using var server = new CountingServer();
        using var http = OpenAITranscriptionClient.CreateHttpClient();
        var client = new OpenAITranscriptionClient(() => http, server.Uri);

        await client.TranscribeAsync(Wav(), "sk-test", Options, CancellationToken.None);
        await client.TranscribeAsync(Wav(), "sk-test", Options, CancellationToken.None);

        Assert.Equal(2, server.Connections);
        Assert.Equal(2, server.Posts);
    }

    [Fact]
    public async Task WarmUp_failure_is_swallowed_and_never_throws()
    {
        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; }
        using var http = OpenAITranscriptionClient.CreateHttpClient();
        var client = new OpenAITranscriptionClient(() => http, new Uri($"http://127.0.0.1:{port}/x"));

        await client.WarmUpAsync(CancellationToken.None); // nobody is listening: must not throw
    }

    [Fact]
    public async Task WarmUp_respects_cancellation_without_throwing()
    {
        using var server = new CountingServer();
        using var http = OpenAITranscriptionClient.CreateHttpClient();
        var client = new OpenAITranscriptionClient(() => http, server.Uri);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await client.WarmUpAsync(cts.Token);
    }

    [Fact]
    public void Endpoint_override_only_accepts_loopback_http()
    {
        Assert.Equal(OpenAITranscriptionClient.DefaultEndpoint, OpenAITranscriptionClient.ResolveEndpoint("http://example.com/x"));
        Assert.Equal(OpenAITranscriptionClient.DefaultEndpoint, OpenAITranscriptionClient.ResolveEndpoint("https://127.0.0.1/x"));
        Assert.Equal(new Uri("http://127.0.0.1:5000/x"), OpenAITranscriptionClient.ResolveEndpoint("http://127.0.0.1:5000/x"));
        Assert.Equal(OpenAITranscriptionClient.DefaultEndpoint, OpenAITranscriptionClient.ResolveEndpoint(null));
    }
}
