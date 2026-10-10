using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DikTap;
using DikTap.Services.Audio;
using DikTap.Services.Settings;
using DikTap.Services.Transcription;

namespace DikTap.Tests;

public class TranscriptionClientTests
{
    private const string Key = "sk-test-SECRET-1234567890";

    private sealed class StubHandler : HttpMessageHandler
    {
        public readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond;
        public HttpRequestMessage? Request;
        public string? Body;
        public string? ContentType;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => Respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            if (request.Content is not null)
            {
                ContentType = request.Content.Headers.ContentType?.ToString();
                using var ms = new MemoryStream();
                await request.Content.CopyToAsync(ms, ct);
                Body = Encoding.Latin1.GetString(ms.ToArray());
            }
            return await Respond(request, ct);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static WavMemoryStream Wav()
    {
        var w = new WavMemoryStream(16000, RecordingLimits.MaxWavBytes, RecordingLimits.MaxDuration);
        w.Append(Enumerable.Repeat((short)1234, 3200).ToArray());
        w.Seal();
        return w;
    }

    private static TranscriptionOptions Options(string[]? langs = null, string[]? keywords = null, string? prompt = "contexto")
        => new("gpt-transcribe", langs ?? new[] { "es", "en" }, keywords ?? new[] { "Spring Boot", "Kafka" }, prompt);

    private static (OpenAITranscriptionClient Client, StubHandler Handler) Make(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        var handler = new StubHandler(respond);
        var http = new HttpClient(handler);
        return (new OpenAITranscriptionClient(() => http), handler);
    }

    private static List<(string Name, string? FileName, string? ContentType, string Body)> Parts(string contentType, string body)
    {
        string boundary = Regex.Match(contentType, "boundary=\"?([^\";]+)\"?").Groups[1].Value;
        var parts = new List<(string, string?, string?, string)>();
        foreach (var raw in body.Split("--" + boundary))
        {
            var seg = raw.Trim('\r', '\n');
            if (seg.Length == 0 || seg == "--") continue;
            int sep = seg.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            string headers = seg[..sep];
            string content = seg[(sep + 4)..];
            string name = Regex.Match(headers, "name=\"?([^\";\r\n]+)\"?").Groups[1].Value;
            var fn = Regex.Match(headers, "filename=\"?([^\";\r\n]+)\"?");
            var ct = Regex.Match(headers, "Content-Type: ([^\r\n]+)");
            parts.Add((name, fn.Success ? fn.Groups[1].Value : null, ct.Success ? ct.Groups[1].Value : null, content));
        }
        return parts;
    }

    [Fact]
    public async Task Sends_expected_multipart_request_with_bearer_auth_and_no_key_in_url()
    {
        var (client, h) = Make((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{\"text\":\"hola\",\"languages\":[{\"code\":\"es\"}]}")));
        using var wav = Wav();
        long wavLength = wav.Length; // the HTTP content closes the stream when done
        string text = (await client.TranscribeAsync(wav, Key, Options(), CancellationToken.None)).Text;

        Assert.Equal("hola", text);
        Assert.Equal(HttpMethod.Post, h.Request!.Method);
        Assert.Equal("https://api.openai.com/v1/audio/transcriptions", h.Request.RequestUri!.ToString());
        Assert.DoesNotContain(Key, h.Request.RequestUri.ToString());
        Assert.Equal("Bearer", h.Request.Headers.Authorization!.Scheme);
        Assert.Equal(Key, h.Request.Headers.Authorization.Parameter);
        Assert.StartsWith("multipart/form-data", h.ContentType);

        var parts = Parts(h.ContentType!, h.Body!);
        string Single(string n) => Assert.Single(parts, p => p.Name == n).Body;
        Assert.Equal("gpt-transcribe", Single("model"));
        Assert.Equal("json", Single("response_format"));
        Assert.Equal("contexto", Single("prompt"));
        Assert.Equal(new[] { "Spring Boot", "Kafka" }, parts.Where(p => p.Name == "keywords[]").Select(p => p.Body));
        Assert.Equal(new[] { "es", "en" }, parts.Where(p => p.Name == "languages[]").Select(p => p.Body));
        Assert.DoesNotContain(parts, p => p.Name is "language" or "temperature");

        var file = Assert.Single(parts, p => p.Name == "file");
        Assert.Equal("dictation.wav", file.FileName);
        Assert.Equal("audio/wav", file.ContentType);
        Assert.StartsWith("RIFF", file.Body);
        Assert.Equal(wavLength, file.Body.Length);
    }

    [Fact]
    public async Task Optional_context_fields_are_omitted_when_empty()
    {
        var (client, h) = Make((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{\"text\":\"x\"}")));
        using var wav = Wav();
        await client.TranscribeAsync(wav, Key, new TranscriptionOptions("gpt-transcribe", Array.Empty<string>(), Array.Empty<string>(), null), CancellationToken.None);
        var names = Parts(h.ContentType!, h.Body!).Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "model", "response_format", "file" }, names);
    }

    [Fact]
    public async Task Model_is_never_substituted()
    {
        var (client, h) = Make((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{\"text\":\"x\"}")));
        using var wav = Wav();
        await client.TranscribeAsync(wav, Key, TranscriptionOptions.FromSettings(new AppSettings()), CancellationToken.None);
        Assert.Equal("gpt-transcribe", Parts(h.ContentType!, h.Body!).Single(p => p.Name == "model").Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, ErrorKind.Api, "400")]
    [InlineData(HttpStatusCode.Unauthorized, ErrorKind.Auth, "401")]
    [InlineData(HttpStatusCode.Forbidden, ErrorKind.Auth, "403")]
    [InlineData(HttpStatusCode.TooManyRequests, ErrorKind.Api, "429")]
    [InlineData(HttpStatusCode.InternalServerError, ErrorKind.Api, "500")]
    [InlineData(HttpStatusCode.BadGateway, ErrorKind.Api, "502")]
    [InlineData(HttpStatusCode.ServiceUnavailable, ErrorKind.Api, "503")]
    public async Task Http_errors_become_short_actionable_messages_without_secrets(HttpStatusCode code, ErrorKind kind, string number)
    {
        var (client, _) = Make((_, _) => Task.FromResult(Json(code, "{\"error\":{\"message\":\"Incorrect API key provided: " + Key + "\",\"code\":\"x\"}}")));
        using var wav = Wav();
        var ex = await Assert.ThrowsAsync<DictationException>(() => client.TranscribeAsync(wav, Key, Options(), CancellationToken.None));
        Assert.Equal(kind, ex.Kind);
        Assert.Contains(number, ex.Message);
        Assert.DoesNotContain("sk-", ex.Message);
        Assert.True(ex.Message.Length < 160);
    }

    [Fact]
    public async Task Quota_errors_are_distinguished_from_rate_limits()
    {
        var (client, _) = Make((_, _) => Task.FromResult(Json(HttpStatusCode.TooManyRequests, "{\"error\":{\"code\":\"insufficient_quota\"}}")));
        using var wav = Wav();
        var ex = await Assert.ThrowsAsync<DictationException>(() => client.TranscribeAsync(wav, Key, Options(), CancellationToken.None));
        Assert.Contains("crédito", ex.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"foo\":1}")]
    [InlineData("{\"text\":42}")]
    [InlineData("[1,2]")]
    [InlineData("")]
    public async Task Unexpected_success_bodies_are_reported(string body)
    {
        var (client, _) = Make((_, _) => Task.FromResult(Json(HttpStatusCode.OK, body)));
        using var wav = Wav();
        var ex = await Assert.ThrowsAsync<DictationException>(() => client.TranscribeAsync(wav, Key, Options(), CancellationToken.None));
        Assert.Equal(ErrorKind.Api, ex.Kind);
        Assert.Contains("inesperada", ex.Message);
    }

    [Fact]
    public async Task Connection_failures_are_reported_as_network_errors()
    {
        var (client, _) = Make((_, _) => throw new HttpRequestException("dns", new System.Net.Sockets.SocketException()));
        using var wav = Wav();
        var ex = await Assert.ThrowsAsync<DictationException>(() => client.TranscribeAsync(wav, Key, Options(), CancellationToken.None));
        Assert.Equal(ErrorKind.Network, ex.Kind);
    }

    [Fact]
    public async Task Timeout_is_reported_but_user_cancellation_propagates_as_cancellation()
    {
        var (timeoutClient, _) = Make((_, _) => throw new TaskCanceledException("timeout", new TimeoutException()));
        using (var wav = Wav())
        {
            var ex = await Assert.ThrowsAsync<DictationException>(() => timeoutClient.TranscribeAsync(wav, Key, Options(), CancellationToken.None));
            Assert.Equal(ErrorKind.Network, ex.Kind);
            Assert.Contains("120", ex.Message);
        }

        var (client, _) = Make(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; });
        using var cts = new CancellationTokenSource();
        using var wav2 = Wav();
        var task = client.TranscribeAsync(wav2, Key, Options(), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Does_not_retry_automatically()
    {
        int calls = 0;
        var (client, _) = Make((_, _) => { calls++; return Task.FromResult(Json(HttpStatusCode.InternalServerError, "{}")); });
        using var wav = Wav();
        await Assert.ThrowsAsync<DictationException>(() => client.TranscribeAsync(wav, Key, Options(), CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Default_http_client_has_finite_120s_timeout()
    {
        using var http = OpenAITranscriptionClient.CreateHttpClient();
        Assert.Equal(TimeSpan.FromSeconds(120), http.Timeout);
    }

    [Fact]
    public void Keywords_are_sanitized_for_the_api_restrictions()
    {
        var k = TranscriptionOptions.ParseKeywords("Java, <Spring>\r\nBoot; Kafka,,  kafka ,  \n x<y>z");
        Assert.Equal(new[] { "Java", "Spring", "Boot", "Kafka", "xyz" }, k);
        Assert.All(k, x => Assert.DoesNotContain('<', x));
        Assert.Equal(TranscriptionOptions.MaxKeywords, TranscriptionOptions.ParseKeywords(string.Join(",", Enumerable.Range(0, 300).Select(i => "k" + i))).Count);
        Assert.Equal(TranscriptionOptions.MaxKeywordLength, TranscriptionOptions.ParseKeywords(new string('a', 500))[0].Length);
    }

    [Fact]
    public void Languages_are_validated_lowercased_and_deduplicated()
    {
        Assert.Equal(new[] { "es", "en", "zh-cn" }, TranscriptionOptions.ParseLanguages("ES, en; es zh-CN 12 español"));
        Assert.Empty(TranscriptionOptions.ParseLanguages(""));
    }

    [Fact]
    public void Prompt_is_trimmed_and_bounded()
    {
        Assert.Null(TranscriptionOptions.NormalizePrompt("   "));
        Assert.Equal(TranscriptionOptions.MaxPromptLength, TranscriptionOptions.NormalizePrompt(new string('p', 5000))!.Length);
    }

    [Fact]
    public void Endpoint_override_only_accepts_loopback_http()
    {
        Assert.Equal(OpenAITranscriptionClient.DefaultEndpoint, OpenAITranscriptionClient.ResolveEndpoint(null));
        Assert.Equal(OpenAITranscriptionClient.DefaultEndpoint, OpenAITranscriptionClient.ResolveEndpoint("https://evil.example.com/v1/audio/transcriptions"));
        Assert.Equal(OpenAITranscriptionClient.DefaultEndpoint, OpenAITranscriptionClient.ResolveEndpoint("http://evil.example.com/x"));
        Assert.Equal(OpenAITranscriptionClient.DefaultEndpoint, OpenAITranscriptionClient.ResolveEndpoint("https://localhost/x"));
        Assert.Equal("http://127.0.0.1:5999/v1/audio/transcriptions", OpenAITranscriptionClient.ResolveEndpoint("http://127.0.0.1:5999/v1/audio/transcriptions").ToString());
        Assert.Equal("http://localhost:5999/", OpenAITranscriptionClient.ResolveEndpoint("http://localhost:5999/").ToString());
    }

    [Fact]
    public void Default_settings_send_no_language_vocabulary_or_context_hints()
    {
        var o = TranscriptionOptions.FromSettings(new AppSettings());
        Assert.Equal("gpt-transcribe", o.Model);
        Assert.Empty(o.Languages);
        Assert.Empty(o.Keywords);
        Assert.True(string.IsNullOrWhiteSpace(o.Prompt));
    }
}
