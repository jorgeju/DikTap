namespace DikTap.Services.Usage;

// Public per-minute audio rates (https://developers.openai.com/api/docs/pricing). The API does not return the amount.
internal static class UsagePricing
{
    public const double DefaultPerMinute = 0.0045;

    public static double PerMinute(string model) => model switch
    {
        "gpt-transcribe" => 0.0045,
        "gpt-4o-transcribe" or "gpt-4o-transcribe-diarize" or "whisper-1" => 0.006,
        "gpt-4o-mini-transcribe" => 0.003,
        _ => DefaultPerMinute,
    };

    public static double Estimate(string model, double audioSeconds) => audioSeconds / 60.0 * PerMinute(model);

    public const string UnknownKey = "?";

    // Last 4 characters, as shown in the OpenAI dashboard; "?" if the key is too short to avoid revealing it entirely.
    public static string KeyTag(string? apiKey) => apiKey is { Length: >= 12 } ? apiKey[^4..] : UnknownKey;
}

internal enum UsagePeriod
{
    Today,
    Last7Days,
    Last30Days,
    ThisMonth,
    All,
    Custom,
}

// Dates in local time; From/To inclusive and optional.
internal sealed record UsageFilter(DateOnly? From, DateOnly? To, string? Key)
{
    public static UsageFilter ForPeriod(UsagePeriod period, DateOnly today, string? key) => period switch
    {
        UsagePeriod.Today => new(today, today, key),
        UsagePeriod.Last7Days => new(today.AddDays(-6), today, key),
        UsagePeriod.Last30Days => new(today.AddDays(-29), today, key),
        UsagePeriod.ThisMonth => new(new DateOnly(today.Year, today.Month, 1), today, key),
        _ => new(null, null, key),
    };

    public bool Matches(UsageRecord r)
    {
        var day = DayOf(r);
        return (From is null || day >= From) && (To is null || day <= To) && (string.IsNullOrEmpty(Key) || r.Key == Key);
    }

    internal static DateOnly DayOf(UsageRecord r) => DateOnly.FromDateTime(r.Time.ToLocalTime().DateTime);
}

internal sealed record DayUsage(DateOnly Day, int Count, double Seconds, double CostUsd);

internal sealed record KeyUsage(string Key, int Count, double Seconds, double CostUsd);

internal sealed record UsageReport(
    int Count,
    double Seconds,
    double CostUsd,
    int InputTokens,
    int OutputTokens,
    IReadOnlyList<DayUsage> Days,
    IReadOnlyList<KeyUsage> ByKey)
{
    public double AverageCostUsd => Count == 0 ? 0 : CostUsd / Count;

    // Days includes days with no activity within the range (the chart needs gaps), but never more than maxDays.
    public static UsageReport Build(IEnumerable<UsageRecord> records, UsageFilter filter, int maxDays = 62)
    {
        var rows = records.Where(filter.Matches).ToList();
        if (rows.Count == 0)
            return new UsageReport(0, 0, 0, 0, 0, Array.Empty<DayUsage>(), Array.Empty<KeyUsage>());

        var perDay = rows.GroupBy(UsageFilter.DayOf).ToDictionary(g => g.Key);
        var first = filter.From ?? perDay.Keys.Min();
        var last = filter.To ?? perDay.Keys.Max();
        if (last.DayNumber - first.DayNumber + 1 > maxDays) first = last.AddDays(1 - maxDays);

        var days = new List<DayUsage>();
        for (var d = first; d <= last; d = d.AddDays(1))
        {
            days.Add(perDay.TryGetValue(d, out var g)
                ? new DayUsage(d, g.Count(), g.Sum(r => r.AudioSeconds), g.Sum(r => r.CostUsd))
                : new DayUsage(d, 0, 0, 0));
        }

        var byKey = rows.GroupBy(r => r.Key)
            .Select(g => new KeyUsage(g.Key, g.Count(), g.Sum(r => r.AudioSeconds), g.Sum(r => r.CostUsd)))
            .OrderByDescending(k => k.CostUsd)
            .ToList();

        return new UsageReport(
            rows.Count,
            rows.Sum(r => r.AudioSeconds),
            rows.Sum(r => r.CostUsd),
            rows.Sum(r => r.InputTokens),
            rows.Sum(r => r.OutputTokens),
            days,
            byKey);
    }
}
