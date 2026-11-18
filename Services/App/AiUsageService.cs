using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Security;
using System.Text.Json;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

/// <summary>One quota window; Severity is the provider's own level ("normal", "warning", ...) when it sends one.</summary>
public sealed record AiUsageWindow(string LabelKey, double Used, DateTimeOffset? ResetsAt, long? WindowSeconds, string? Severity = null);
public sealed record AiUsageShare(string Name, double Used);
public sealed record AiUsageSpend(decimal Used, decimal? Limit, string Currency, double? Used01);

public sealed record AiUsageReport(AiProviders Provider, string? Plan, IReadOnlyList<AiUsageWindow> Windows, DateTimeOffset FetchedAt)
{
    public bool LimitReached { get; init; }
    /// <summary>Claude weekly usage split by surface, as a share of the weekly limit; only non-zero rows.</summary>
    public IReadOnlyList<AiUsageShare> Breakdown { get; init; } = [];
    public AiUsageSpend? Spend { get; init; }
    /// <summary>Codex credit balance; null when the account has no credits.</summary>
    public double? Credits { get; init; }
    public bool UnlimitedCredits { get; init; }
    public int ResetCredits { get; init; }
}

/// <summary>Borrowed CLI sign-in, read on each poll. Never refreshes, writes or caches credentials.</summary>
public static class AiUsageService
{
    private static readonly HttpClient Client = CreateClient();
    private const double MaxResetSeconds = 4e9;
    internal sealed record LoadResult(AiUsageReport? Report, string? Error, TimeSpan? RetryAfter = null);
    private sealed record Credential(string Token, string? Account = null, string? Plan = null);

    public static async Task<(AiUsageReport? Report, string? Error)> LoadAsync(AiProviders provider, CancellationToken cancellation)
    {
        var result = await LoadWithPolicyAsync(provider, cancellation);
        return (result.Report, result.Error);
    }

    internal static async Task<LoadResult> LoadWithPolicyAsync(AiProviders provider, CancellationToken cancellation)
    {
        if (provider is not (AiProviders.Claude or AiProviders.Codex)) return new(null, "ErrorAiUsageLoad");
        int? status = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (!await Task.Run(NetworkInterface.GetIsNetworkAvailable, cancellation).ConfigureAwait(false))
                return new(null, "ErrorAiUsageLoad");
            var credential = await Task.Run(() => ReadCredential(provider, CredentialPath(provider)), cancellation).ConfigureAwait(false);
            if (credential is null) return new(null, "ErrorAiUsageSignIn");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(HttpMethod.Get, provider == AiProviders.Claude
                ? "https://api.anthropic.com/api/oauth/usage"
                : "https://chatgpt.com/backend-api/wham/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token);
            if (provider == AiProviders.Claude) request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            else if (!string.IsNullOrWhiteSpace(credential.Account)) request.Headers.Add("ChatGPT-Account-Id", credential.Account);
            using var response = await Client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                AppLog.Info($"AI usage ({provider}) unavailable: {status}");
                return HttpFailure(response.StatusCode, response.Headers.RetryAfter);
            }
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var (report, error) = provider == AiProviders.Claude ? ParseClaude(json, credential.Plan) : ParseCodex(json);
            return new(report, error);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TimeoutException
            or IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException
            or InvalidOperationException or ArgumentException or FormatException or NotSupportedException or SecurityException)
        {
            AppLog.Info($"AI usage ({provider}) unavailable: {status} {ex.GetType().Name}");
            return new(null, "ErrorAiUsageLoad");
        }
    }

    internal static LoadResult HttpFailure(HttpStatusCode status, RetryConditionHeaderValue? retryAfter) => status switch
    {
        HttpStatusCode.Unauthorized => new(null, "ErrorAiUsageSignIn"),
        HttpStatusCode.TooManyRequests => new(null, "ErrorAiUsageRateLimited",
            retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null)),
        _ => new(null, "ErrorAiUsageLoad")
    };

    internal static string CredentialPath(AiProviders provider)
    {
        var directory = Environment.GetEnvironmentVariable(provider == AiProviders.Claude ? "CLAUDE_CONFIG_DIR" : "CODEX_HOME");
        if (string.IsNullOrWhiteSpace(directory)) directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), provider == AiProviders.Claude ? ".claude" : ".codex");
        return Path.Combine(directory, provider == AiProviders.Claude ? ".credentials.json" : "auth.json");
    }

    internal static DateTime CredentialStamp(AiProviders provider)
    {
        try { return File.GetLastWriteTimeUtc(CredentialPath(provider)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or SecurityException) { return DateTime.MinValue; }
    }

    internal static string? ReadClaudeToken(string? path = null) => ReadCredential(AiProviders.Claude, path ?? CredentialPath(AiProviders.Claude))?.Token;
    internal static string? ReadCodexToken(string? path = null) => ReadCredential(AiProviders.Codex, path ?? CredentialPath(AiProviders.Codex))?.Token;

    private static Credential? ReadCredential(AiProviders provider, string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var auth = root.GetProperty(provider == AiProviders.Claude ? "claudeAiOauth" : "tokens");
            var token = auth.GetProperty(provider == AiProviders.Claude ? "accessToken" : "access_token").GetString();
            if (string.IsNullOrWhiteSpace(token)) return null;
            if (provider == AiProviders.Claude)
            {
                if (auth.TryGetProperty("expiresAt", out var expiry)
                    && DateTimeOffset.FromUnixTimeMilliseconds(expiry.GetInt64()) <= DateTimeOffset.UtcNow) return null;
                return new(token, Plan: Text(auth, "subscriptionType") ?? Text(auth, "rateLimitTier"));
            }
            return new(token, Text(auth, "account_id"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException
            or NotSupportedException or SecurityException) { return null; }
    }

    internal static (AiUsageReport? Report, string? Error) ParseClaude(string json, string? plan) => Parse(json, AiProviders.Claude, plan);
    internal static (AiUsageReport? Report, string? Error) ParseCodex(string json) => Parse(json, AiProviders.Codex, null);

    private static (AiUsageReport? Report, string? Error) Parse(string json, AiProviders provider, string? plan)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var now = DateTimeOffset.UtcNow;
            var windows = new List<AiUsageWindow>();
            AiUsageReport report;
            if (provider == AiProviders.Claude)
            {
                var severity = new Dictionary<string, string>();
                if (Property(root, "limits") is { ValueKind: JsonValueKind.Array } limits)
                    foreach (var limit in limits.EnumerateArray())
                        if (Text(limit, "group") is { } group && Text(limit, "severity") is { } level) severity.TryAdd(group, level);
                foreach (var property in root.EnumerateObject())
                {
                    if (property.Name != "five_hour" && property.Name != "seven_day"
                        && !property.Name.StartsWith("seven_day_", StringComparison.Ordinal)) continue;
                    var value = property.Value;
                    if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("utilization", out var usage)
                        || Percent(usage) is not { } used) continue;
                    var reset = Text(value, "resets_at");
                    var label = property.Name == "five_hour" ? "AiUsageSession" : property.Name == "seven_day"
                        ? "AiUsageWeekly" : "AiUsageWeeklyModel:" + property.Name[10..];
                    windows.Add(new(label, used,
                        DateTimeOffset.TryParse(reset, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var resetsAt) ? resetsAt : null,
                        property.Name == "five_hour" ? 18000 : 604800,
                        property.Name == "five_hour" ? severity.GetValueOrDefault("session")
                            : property.Name == "seven_day" ? severity.GetValueOrDefault("weekly") : null));
                }
                var breakdown = new List<AiUsageShare>();
                if (Property(Property(root, "seven_day_breakdown"), "rows") is { ValueKind: JsonValueKind.Array } rows)
                    foreach (var row in rows.EnumerateArray())
                        if (Text(row, "display_name") is { Length: > 0 } name && Property(row, "percent_of_limit") is { } share
                            && Percent(share) is { } part && Math.Round(part * 100) >= 1) breakdown.Add(new(name, part));
                report = new(provider, PlanLabel(plan), windows, now) { Breakdown = breakdown, Spend = ParseSpend(root) };
            }
            else
            {
                plan = Text(root, "plan_type");
                var limits = root.GetProperty("rate_limit");
                foreach (var name in new[] { "primary_window", "secondary_window" })
                {
                    if (limits.ValueKind != JsonValueKind.Object || !limits.TryGetProperty(name, out var value)
                        || value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("used_percent", out var usage)
                        || Percent(usage) is not { } used) continue;
                    long? seconds = Property(value, "limit_window_seconds") is { } duration && Number(duration) is { } length
                        && length is > 0 and < 1e9 ? (long)length : null;
                    // An unusable absolute reset falls back to the relative one; out-of-range values drop only the reset.
                    var reset = (Property(value, "reset_at") is { } absolute ? UnixReset(absolute) : null)
                        ?? (Property(value, "reset_after_seconds") is { } relative && Number(relative) is { } after
                            && after is >= 0 and < MaxResetSeconds ? (DateTimeOffset?)now.AddSeconds(after) : null);
                    var weekly = seconds is { } span ? span >= 604800 : name == "secondary_window";
                    windows.Add(new(weekly ? "AiUsageWeekly" : "AiUsageSession", used, reset, seconds));
                }
                var credits = Property(root, "credits");
                var unlimited = Flag(credits, "unlimited");
                report = new(provider, PlanLabel(plan), windows, now)
                {
                    LimitReached = Flag(limits, "limit_reached") || Property(limits, "allowed") is { ValueKind: JsonValueKind.False },
                    UnlimitedCredits = unlimited,
                    Credits = !unlimited && Flag(credits, "has_credits") && Property(credits, "balance") is { } balance
                        && Number(balance) is { } amount && double.IsFinite(amount) ? amount : null,
                    // Only resets usable right now count; available_count also includes ones that do not apply yet.
                    ResetCredits = Property(Property(root, "rate_limit_reset_credits"), "applicable_available_count") is { } count
                        && Number(count) is { } available && available is > 0 and < 1000 ? (int)available : 0
                };
            }
            return windows.Count == 0 ? (null, "ErrorAiUsageLoad") : (report, null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException
            or FormatException or ArgumentException or OverflowException) { return (null, "ErrorAiUsageLoad"); }
    }

    /// <summary>Claude pay-as-you-go spend, only when the account has it switched on.</summary>
    private static AiUsageSpend? ParseSpend(JsonElement root)
    {
        var spend = Property(root, "spend");
        if (!Flag(spend, "enabled") && !Flag(Property(root, "extra_usage"), "is_enabled")) return null;
        if (Money(Property(spend, "used")) is not { } used) return null;
        var limit = Money(Property(spend, "limit"));
        var percent = Property(spend, "percent") is { } value ? Percent(value) : null;
        return new(used.Amount, limit?.Amount, used.Currency, percent);
    }

    private static (decimal Amount, string Currency)? Money(JsonElement? value)
    {
        if (value is not { } money || Property(money, "amount_minor") is not { } minor || Number(minor) is not { } amount
            || !double.IsFinite(amount) || Math.Abs(amount) > 1e12) return null;
        var exponent = Property(money, "exponent") is { } digits && Number(digits) is { } places && places is >= 0 and <= 6 ? (int)places : 2;
        var currency = Text(money, "currency") is { Length: 3 } code ? code : "USD";
        return ((decimal)amount / (decimal)Math.Pow(10, exponent), currency);
    }

    private static JsonElement? Property(JsonElement? element, string key) => element is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty(key, out var result) && result.ValueKind != JsonValueKind.Null ? result : null;

    private static bool Flag(JsonElement? element, string key) => Property(element, key) is { ValueKind: JsonValueKind.True };

    private static string? Text(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement value) => value.ValueKind == JsonValueKind.Number ? value.GetDouble()
        : value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var number) ? number : null;

    private static DateTimeOffset? UnixReset(JsonElement value)
    {
        if (Number(value) is not { } number || !double.IsFinite(number)) return null;
        var seconds = number > 1e12 ? number / 1000 : number;
        return seconds is > 0 and < MaxResetSeconds ? DateTimeOffset.UnixEpoch.AddSeconds(seconds) : null;
    }

    internal static string? PlanLabel(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan)) return null;
        var words = plan.Trim().Split('_', StringSplitOptions.RemoveEmptyEntries);
        plan = words.Length > 2 && words[0] == "default" && words[1] == "claude"
            ? words[2] : string.Join(' ', words);
        if (plan.Length == 0) return null;
        plan = plan[..Math.Min(24, plan.Length)];
        return char.ToUpperInvariant(plan[0]) + plan[1..];
    }

    private static double? Percent(JsonElement value)
    {
        if (Number(value) is not { } number) return null;
        return double.IsFinite(number) ? Math.Clamp(number / 100, 0, 1) : 0;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
        { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 1 << 20 };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenTaskbar");
        return client;
    }
}
