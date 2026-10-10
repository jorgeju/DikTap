using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using DikTap.Localization;
using DikTap.Services.Settings;

namespace DikTap.Services.Transcription;

internal sealed record TranscriptionOptions(string Model, IReadOnlyList<string> Languages, IReadOnlyList<string> Keywords, string? Prompt)
{
    public const int MaxKeywords = 100;
    public const int MaxKeywordLength = 100;
    public const int MaxPromptLength = 1000;
    public const int MaxLanguages = 8;

    private static readonly Regex LanguageCode = new(@"^[a-z]{2,3}(-[a-z0-9]{2,4})?$", RegexOptions.Compiled);

    public static TranscriptionOptions FromSettings(AppSettings s) => new(
        AppInfo.TranscriptionModel,
        ParseLanguages(s.Languages),
        ParseKeywords(s.Keywords),
        NormalizePrompt(s.Context));

    public static IReadOnlyList<string> ParseLanguages(string? text) =>
        (text ?? string.Empty)
            .Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.ToLowerInvariant())
            .Where(l => LanguageCode.IsMatch(l))
            .Distinct()
            .Take(MaxLanguages)
            .ToList();

    // The API rejects keywords containing <, >, CR or LF; they are removed and each keyword takes one line.
    public static IReadOnlyList<string> ParseKeywords(string? text) =>
        (text ?? string.Empty)
            .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(k => k.Replace("<", "").Replace(">", "").Trim())
            .Where(k => k.Length > 0)
            .Select(k => k.Length > MaxKeywordLength ? k[..MaxKeywordLength].TrimEnd() : k)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxKeywords)
            .ToList();

    public static string? NormalizePrompt(string? text)
    {
        string p = (text ?? string.Empty).Trim();
        if (p.Length == 0) return null;
        return p.Length > MaxPromptLength ? p[..MaxPromptLength] : p;
    }
}

// Usage reported by the API in the response: billed seconds (duration-based models) or tokens (token-based models).
internal sealed record TranscriptionUsage(double? Seconds, int InputTokens, int OutputTokens);

internal sealed record TranscriptionResult(string Text, TranscriptionUsage? Usage = null);

internal interface ITranscriptionClient
{
    // Returns the transcribed text (may be empty). Does not retry or resend on its own.
    Task<TranscriptionResult> TranscribeAsync(Stream wav, string apiKey, TranscriptionOptions options, CancellationToken cancellationToken);

    // Opens the TLS connection ahead of time while the user speaks. Never throws: if it fails, the send opens its own.
    Task WarmUpAsync(CancellationToken cancellationToken);
}

internal sealed class OpenAITranscriptionClient : ITranscriptionClient
{
    public static readonly Uri DefaultEndpoint = new("https://api.openai.com/v1/audio/transcriptions");
    public static readonly Uri Endpoint = ResolveEndpoint(Environment.GetEnvironmentVariable("DIKTAP_TEST_ENDPOINT"));
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    // Only for end-to-end tests with a simulated server: only http on loopback is accepted,
    // so the key can never be redirected to a remote host.
    internal static Uri ResolveEndpoint(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var u) && u.IsLoopback && u.Scheme == Uri.UriSchemeHttp ? u : DefaultEndpoint;

    private readonly Lazy<HttpClient> _http;
    private readonly Uri _endpoint;

    // The HttpClient is created on the first dictation and reused; at idle no network stack is loaded.
    public OpenAITranscriptionClient(Func<HttpClient> httpFactory, Uri? endpoint = null)
    {
        _http = new Lazy<HttpClient>(httpFactory);
        _endpoint = endpoint ?? Endpoint;
    }

    public static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            // The connection opened by WarmUpAsync waits for the send; if the dictation is cancelled or lasts longer, it closes itself.
            // Each send carries Connection: close, so no idle connection is left after use.
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        };
        var client = new HttpClient(handler) { Timeout = RequestTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DikTap/1.0");
        return client;
    }

    // HEAD without credentials or data: only establishes DNS, TCP and TLS so the later POST does not pay for them.
    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Head, _endpoint);
            using var response = await _http.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // This is only an optimization.
        }
    }

    // Multipart: model, response_format, prompt, keywords[], languages[] and file. HttpClient generates the boundary.
    internal static MultipartFormDataContent BuildContent(Stream wav, TranscriptionOptions options)
    {
        var form = new MultipartFormDataContent();
        AddText(form, "model", options.Model);
        AddText(form, "response_format", "json");
        if (!string.IsNullOrEmpty(options.Prompt)) AddText(form, "prompt", options.Prompt);
        foreach (string k in options.Keywords) AddText(form, "keywords[]", k);
        foreach (string l in options.Languages) AddText(form, "languages[]", l);

        var file = new StreamContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "dictation.wav");
        return form;
    }

    private static void AddText(MultipartFormDataContent form, string name, string value)
    {
        var c = new StringContent(value);
        c.Headers.ContentType = null;
        form.Add(c, name);
    }

    public async Task<TranscriptionResult> TranscribeAsync(Stream wav, string apiKey, TranscriptionOptions options, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.ConnectionClose = true;
        request.Content = BuildContent(wav, options);

        HttpResponseMessage response;
        try
        {
            response = await _http.Value.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new DictationException(Str.ApiTimeout(RequestTimeout.TotalSeconds), ErrorKind.Network, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new DictationException(Str.ApiConnectFailed, ErrorKind.Network, ex);
        }

        using (response)
        {
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                throw new DictationException(Str.ApiInterrupted, ErrorKind.Network, ex);
            }

            if (!response.IsSuccessStatusCode)
                throw MapError(response.StatusCode, ReadErrorCode(body));

            return new TranscriptionResult(ParseText(body), ParseUsage(body));
        }
    }

    // The usage field is optional: if missing or of another shape, the dictation is still valid and is estimated from the recorded duration.
    internal static TranscriptionUsage? ParseUsage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object)
                return null;

            double? seconds = usage.TryGetProperty("seconds", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : null;
            int input = usage.TryGetProperty("input_tokens", out var i) && i.TryGetInt32(out int iv) ? iv : 0;
            int output = usage.TryGetProperty("output_tokens", out var o) && o.TryGetInt32(out int ov) ? ov : 0;
            return new TranscriptionUsage(seconds, input, output);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string ParseText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
        }
        throw new DictationException(Str.ApiUnexpectedResponse, ErrorKind.Api);
    }

    internal static string? ReadErrorCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
                && err.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
                return code.GetString();
        }
        catch (JsonException)
        {
        }
        return null;
    }

    internal static DictationException MapError(HttpStatusCode status, string? code)
    {
        int n = (int)status;
        return status switch
        {
            HttpStatusCode.BadRequest => new DictationException(Str.Api400, ErrorKind.Api),
            HttpStatusCode.Unauthorized => new DictationException(Str.Api401, ErrorKind.Auth),
            HttpStatusCode.Forbidden => new DictationException(Str.Api403, ErrorKind.Auth),
            HttpStatusCode.NotFound => new DictationException(Str.Api404, ErrorKind.Api),
            HttpStatusCode.RequestEntityTooLarge => new DictationException(Str.Api413, ErrorKind.Api),
            HttpStatusCode.TooManyRequests when code == "insufficient_quota" => new DictationException(Str.Api429Quota, ErrorKind.Api),
            HttpStatusCode.TooManyRequests => new DictationException(Str.Api429, ErrorKind.Api),
            >= HttpStatusCode.InternalServerError => new DictationException(Str.Api5xx(n), ErrorKind.Api),
            _ => new DictationException(Str.ApiOther(n), ErrorKind.Api),
        };
    }
}
