using DikTap.Localization;
using DikTap.Services.Settings;
using DikTap.Services.Transcription;
using DikTap.Services.Usage;
using DikTap.Views;

namespace DikTap.Tests;

internal sealed class MemoryUsageStore : IUsageStore
{
    public readonly List<UsageRecord> Records = new();
    public Exception? AppendFails;

    public void Append(UsageRecord record)
    {
        if (AppendFails is not null) throw AppendFails;
        lock (Records) Records.Add(record);
    }

    public IReadOnlyList<UsageRecord> Load() { lock (Records) return Records.ToList(); }

    public int Remove(Func<UsageRecord, bool> predicate) { lock (Records) return Records.RemoveAll(r => predicate(r)); }
}

public class UsageParsingTests
{
    [Fact]
    public void Token_usage_is_read_from_the_response()
    {
        var u = OpenAITranscriptionClient.ParseUsage(
            """{"text":"hola","usage":{"type":"tokens","input_tokens":14,"input_token_details":{"text_tokens":0,"audio_tokens":14},"output_tokens":45,"total_tokens":59}}""");
        Assert.NotNull(u);
        Assert.Null(u.Seconds);
        Assert.Equal(14, u.InputTokens);
        Assert.Equal(45, u.OutputTokens);
    }

    [Fact]
    public void Duration_usage_is_read_from_the_response()
    {
        var u = OpenAITranscriptionClient.ParseUsage("""{"text":"hola","usage":{"type":"duration","seconds":27.4}}""");
        Assert.NotNull(u);
        Assert.Equal(27.4, u.Seconds);
        Assert.Equal(0, u.InputTokens);
    }

    [Theory]
    [InlineData("""{"text":"hola"}""")]
    [InlineData("""{"text":"hola","usage":null}""")]
    [InlineData("""{"text":"hola","usage":"x"}""")]
    [InlineData("not json")]
    public void Missing_or_malformed_usage_gives_null_and_never_throws(string body)
        => Assert.Null(OpenAITranscriptionClient.ParseUsage(body));

    private sealed class FixedResponse(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }

    [Fact]
    public async Task A_successful_response_carries_text_and_usage()
    {
        using var http = new HttpClient(new FixedResponse("""{"text":"hola","usage":{"type":"duration","seconds":3}}"""));
        var client = new OpenAITranscriptionClient(() => http);
        var wav = new DikTap.Services.Audio.WavMemoryStream(16000, 1 << 20, TimeSpan.FromMinutes(1));
        var result = await client.TranscribeAsync(wav, "sk-test-0000000000", new TranscriptionOptions("gpt-transcribe", [], [], null), CancellationToken.None);
        Assert.Equal("hola", result.Text);
        Assert.Equal(3, result.Usage!.Seconds);
    }
}

public class UsagePricingTests
{
    [Fact]
    public void One_minute_of_gpt_transcribe_costs_the_published_rate()
        => Assert.Equal(0.0045, UsagePricing.Estimate("gpt-transcribe", 60), 10);

    [Theory]
    [InlineData("gpt-4o-transcribe", 0.006)]
    [InlineData("gpt-4o-mini-transcribe", 0.003)]
    [InlineData("whisper-1", 0.006)]
    [InlineData("modelo-desconocido", 0.0045)]
    public void Rates_per_model(string model, double perMinute)
        => Assert.Equal(perMinute, UsagePricing.PerMinute(model));

    [Fact]
    public void The_key_tag_only_reveals_the_last_four_characters_and_never_a_short_key()
    {
        Assert.Equal("abcd", UsagePricing.KeyTag("sk-proj-0000000000abcd"));
        Assert.Equal(UsagePricing.UnknownKey, UsagePricing.KeyTag("sk-short"));
        Assert.Equal(UsagePricing.UnknownKey, UsagePricing.KeyTag(null));
    }
}

public class UsageStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "diktap-usage-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "usage.jsonl");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static UsageRecord Rec(string key, double seconds, int daysAgo = 0) => new()
    {
        Time = DateTimeOffset.Now.AddDays(-daysAgo),
        Model = "gpt-transcribe",
        Key = key,
        AudioSeconds = seconds,
        InputTokens = 3,
        OutputTokens = 4,
        CostUsd = UsagePricing.Estimate("gpt-transcribe", seconds),
    };

    [Fact]
    public void Records_round_trip_and_a_missing_file_is_empty()
    {
        var store = new JsonlUsageStore(FilePath);
        Assert.Empty(store.Load());
        store.Append(Rec("abcd", 12.5));
        store.Append(Rec("wxyz", 30));
        var all = store.Load();
        Assert.Equal(2, all.Count);
        Assert.Equal("abcd", all[0].Key);
        Assert.Equal(12.5, all[0].AudioSeconds);
        Assert.Equal(3, all[0].InputTokens);
    }

    [Fact]
    public void The_file_holds_numbers_and_a_key_tag_and_nothing_else_sensitive()
    {
        new JsonlUsageStore(FilePath).Append(Rec("abcd", 5));
        string text = File.ReadAllText(FilePath);
        Assert.DoesNotContain("sk-", text);
        Assert.DoesNotContain("\"Text\"", text);
        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void A_damaged_line_is_skipped_without_losing_the_rest()
    {
        var store = new JsonlUsageStore(FilePath);
        store.Append(Rec("abcd", 1));
        File.AppendAllText(FilePath, "{ esto no es json" + Environment.NewLine);
        store.Append(Rec("abcd", 2));
        Assert.Equal(2, store.Load().Count);
    }

    [Fact]
    public void Remove_deletes_only_matching_records_and_reports_how_many()
    {
        var store = new JsonlUsageStore(FilePath);
        store.Append(Rec("abcd", 1));
        store.Append(Rec("wxyz", 2));
        store.Append(Rec("abcd", 3));
        Assert.Equal(2, store.Remove(r => r.Key == "abcd"));
        var left = Assert.Single(store.Load());
        Assert.Equal("wxyz", left.Key);
        Assert.Equal(0, store.Remove(r => r.Key == "nada"));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Removing_everything_deletes_the_file()
    {
        var store = new JsonlUsageStore(FilePath);
        store.Append(Rec("abcd", 1));
        Assert.Equal(1, store.Remove(_ => true));
        Assert.False(File.Exists(FilePath));
        Assert.Empty(store.Load());
    }

    [Fact]
    public async Task Concurrent_appends_do_not_lose_records()
    {
        var store = new JsonlUsageStore(FilePath);
        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Task.Run(() => store.Append(Rec("abcd", i + 1)))));
        Assert.Equal(50, store.Load().Count);
    }
}

public class UsageReportTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);

    private static UsageRecord Rec(DateOnly day, string key, double seconds) => new()
    {
        Time = new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(new TimeOnly(12, 0)))),
        Model = "gpt-transcribe",
        Key = key,
        AudioSeconds = seconds,
        CostUsd = UsagePricing.Estimate("gpt-transcribe", seconds),
    };

    private static readonly UsageRecord[] Sample =
    [
        Rec(Today, "aaaa", 60),
        Rec(Today, "bbbb", 120),
        Rec(Today.AddDays(-2), "aaaa", 30),
        Rec(Today.AddDays(-10), "aaaa", 600),
        Rec(new DateOnly(2026, 8, 1), "bbbb", 60),
    ];

    [Theory]
    [InlineData("Today", 2)]
    [InlineData("Last7Days", 3)]
    [InlineData("Last30Days", 4)]
    [InlineData("ThisMonth", 3)]
    [InlineData("All", 5)]
    public void Preset_periods_select_the_expected_records(string period, int expected)
    {
        var report = UsageReport.Build(Sample, UsageFilter.ForPeriod(Enum.Parse<UsagePeriod>(period), Today, null));
        Assert.Equal(expected, report.Count);
    }

    [Fact]
    public void Filtering_by_key_narrows_every_total()
    {
        var report = UsageReport.Build(Sample, UsageFilter.ForPeriod(UsagePeriod.All, Today, "aaaa"));
        Assert.Equal(3, report.Count);
        Assert.Equal(690, report.Seconds);
        Assert.Equal(UsagePricing.Estimate("gpt-transcribe", 690), report.CostUsd, 10);
        Assert.Equal("aaaa", Assert.Single(report.ByKey).Key);
    }

    [Fact]
    public void Custom_range_is_inclusive_on_both_ends()
    {
        var report = UsageReport.Build(Sample, new UsageFilter(Today.AddDays(-2), Today, null));
        Assert.Equal(3, report.Count);
        report = UsageReport.Build(Sample, new UsageFilter(Today.AddDays(-1), Today.AddDays(-1), null));
        Assert.Equal(0, report.Count);
    }

    [Fact]
    public void Days_are_continuous_and_include_days_without_activity()
    {
        var report = UsageReport.Build(Sample, UsageFilter.ForPeriod(UsagePeriod.Last7Days, Today, null));
        Assert.Equal(7, report.Days.Count);
        Assert.Equal(Today.AddDays(-6), report.Days[0].Day);
        Assert.Equal(Today, report.Days[^1].Day);
        Assert.Equal(0, report.Days.Single(d => d.Day == Today.AddDays(-1)).Count);
        Assert.Equal(2, report.Days[^1].Count);
        Assert.Equal(report.Count, report.Days.Sum(d => d.Count));
    }

    [Fact]
    public void All_time_spans_from_the_first_to_the_last_record_and_caps_the_chart_days()
    {
        var report = UsageReport.Build(Sample, UsageFilter.ForPeriod(UsagePeriod.All, Today, null), maxDays: 62);
        Assert.Equal(62, report.Days.Count);
        Assert.Equal(Today, report.Days[^1].Day);
        // Totals still count everything even though the chart only shows the last days.
        Assert.Equal(5, report.Count);
    }

    [Fact]
    public void By_key_is_ordered_by_cost_and_average_cost_is_derived()
    {
        var report = UsageReport.Build(Sample, UsageFilter.ForPeriod(UsagePeriod.All, Today, null));
        Assert.Equal(["aaaa", "bbbb"], report.ByKey.Select(k => k.Key));
        Assert.Equal(report.CostUsd / 5, report.AverageCostUsd, 12);
        Assert.Equal(0, UsageReport.Build([], UsageFilter.ForPeriod(UsagePeriod.All, Today, null)).AverageCostUsd);
    }

    [Fact]
    public void Number_and_date_formatting_follows_the_language_without_culture_data()
    {
        Loc.Configure(UiLanguage.Spanish);
        Assert.Equal("$0,0045", Fmt.Usd(0.0045));
        Assert.Equal("1.234", Fmt.Integer(1234));
        Assert.Equal("1,50", Fmt.Minutes(90));
        Loc.Configure(UiLanguage.English);
        Assert.Equal("$0.0045", Fmt.Usd(0.0045));
        Assert.Equal("1,234", Fmt.Integer(1234));
        Loc.Configure(UiLanguage.Spanish);

        Assert.Equal("2026-10-09", Fmt.Date(Today));
        Assert.True(Fmt.TryParseDate(" 2026-02-28 ", out var d));
        Assert.Equal(new DateOnly(2026, 2, 28), d);
        Assert.False(Fmt.TryParseDate("28/02/2026", out _));
        Assert.False(Fmt.TryParseDate("2026-02-30", out _));
    }
}

public class UsageControllerTests
{
    private static async Task<(DictationController Controller, FakeTranscriber Transcriber)> RunAsync(IUsageStore store, Action<FakeTranscriber>? tune = null)
    {
        var transcriber = new FakeTranscriber();
        tune?.Invoke(transcriber);
        var controller = new DictationController(new FakeRecorder(), transcriber, new FakePaste(), new FakeDestination(),
            new FakeCredentials(), new FakeOverlay(), new FakeEscape(), () => new AppSettings(), store);
        controller.Toggle();
        await Wait.UntilAsync(() => controller.State == SessionState.Recording, what: "Recording");
        controller.Toggle();
        await Wait.UntilAsync(() => controller.State == SessionState.Idle, what: "Idle");
        return (controller, transcriber);
    }

    [Fact]
    public async Task A_dictation_that_reached_OpenAI_is_recorded_with_estimated_cost_and_key_tag()
    {
        var store = new MemoryUsageStore();
        await RunAsync(store, t => t.Usage = new TranscriptionUsage(Seconds: 30, InputTokens: 7, OutputTokens: 9));

        var r = Assert.Single(store.Records);
        Assert.Equal("gpt-transcribe", r.Model);
        Assert.Equal("0000", r.Key);
        Assert.Equal(30, r.AudioSeconds);
        Assert.Equal(7, r.InputTokens);
        Assert.Equal(9, r.OutputTokens);
        Assert.Equal(UsagePricing.Estimate("gpt-transcribe", 30), r.CostUsd, 10);
    }

    [Fact]
    public async Task Without_usage_in_the_response_the_recorded_duration_is_used()
    {
        var store = new MemoryUsageStore();
        await RunAsync(store);
        var r = Assert.Single(store.Records);
        Assert.True(r.AudioSeconds > 0);
        Assert.Equal(0, r.InputTokens);
    }

    [Fact]
    public async Task A_billed_request_with_no_speech_is_still_recorded()
    {
        var store = new MemoryUsageStore();
        await RunAsync(store, t => t.Text = "   ");
        Assert.Single(store.Records);
    }

    [Fact]
    public async Task A_failed_request_is_not_recorded()
    {
        var store = new MemoryUsageStore();
        await RunAsync(store, t => t.Fails = new DictationException("fallo", ErrorKind.Network));
        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task A_failing_usage_store_never_breaks_the_dictation()
    {
        var store = new MemoryUsageStore { AppendFails = new IOException("disco lleno") };
        var (controller, _) = await RunAsync(store);
        Assert.Equal("hola mundo", controller.LastDictation);
    }
}

public class UsageWindowTests : IDisposable
{
    public UsageWindowTests() => Loc.Configure(UiLanguage.Spanish);
    public void Dispose() => Loc.Configure(UiLanguage.Spanish);

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Exception("fallo en el hilo STA", error);
    }

    private static IEnumerable<string> Texts(System.Windows.DependencyObject root)
    {
        if (root is System.Windows.Controls.TextBlock tb && !string.IsNullOrEmpty(tb.Text)) yield return tb.Text;
        if (root is System.Windows.Controls.ContentControl { Content: string s }) yield return s;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var t in Texts(System.Windows.Media.VisualTreeHelper.GetChild(root, i))) yield return t;
    }

    private static MemoryUsageStore SampleStore()
    {
        var store = new MemoryUsageStore();
        for (int i = 0; i < 12; i++)
        {
            store.Append(new UsageRecord { Time = DateTimeOffset.Now.AddDays(-i), Model = "gpt-transcribe", Key = i % 2 == 0 ? "aaaa" : "bbbb", AudioSeconds = 20 + i, CostUsd = UsagePricing.Estimate("gpt-transcribe", 20 + i) });
        }
        return store;
    }

    [Fact]
    public void The_window_shows_totals_chart_and_per_key_breakdown_in_the_chosen_language()
    {
        OnSta(() =>
        {
            Loc.Configure(UiLanguage.English);
            var window = new UsageWindow(SampleStore());
            var content = (System.Windows.FrameworkElement)window.Content;
            content.Measure(new System.Windows.Size(640, double.PositiveInfinity));
            content.Arrange(new System.Windows.Rect(0, 0, 640, content.DesiredSize.Height));
            content.UpdateLayout();

            var texts = Texts(content).ToList();
            Assert.Contains("Dictations", texts);
            Assert.Contains("Estimated cost", texts);
            Assert.Contains("By key", texts);
            Assert.Contains(texts, t => t.StartsWith("Cost per day"));
            Assert.Contains(texts, t => t.StartsWith("…aaaa:"));
            Assert.Contains("12", texts);
            foreach (var t in texts)
                Assert.False(t.Any(c => "áéíóúñÁÉÍÓÚÑ¿¡«»".Contains(c)), $"texto en español: «{t}»");
        });
    }

    [Fact]
    public void An_empty_store_shows_the_no_data_message()
    {
        OnSta(() =>
        {
            var window = new UsageWindow(new MemoryUsageStore());
            var content = (System.Windows.FrameworkElement)window.Content;
            content.Measure(new System.Windows.Size(640, double.PositiveInfinity));
            Assert.Contains(Str.NoData, Texts(content));
        });
    }
}
