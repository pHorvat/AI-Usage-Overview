using System.Text.Json;

namespace CodexUsageNotch;

internal enum ConnectionStatus { Connecting, Ready, Retrying, MissingCodex, NeedsLogin, SigningIn, LoginFailed }
internal sealed record UsageWindow(int UsedPercent, DateTimeOffset? ResetsAt, long? DurationMinutes)
{
    public int Remaining => Math.Clamp(100 - UsedPercent, 0, 100);
}

// Presence flags distinguish omitted notification fields from explicit nulls.
internal sealed record WindowUpdate(int UsedPercent, DateTimeOffset? ResetsAt, long? DurationMinutes, bool HasReset, bool HasDuration)
{
    public UsageWindow Apply(UsageWindow? old) => new(UsedPercent,
        HasReset ? ResetsAt : old?.ResetsAt, HasDuration ? DurationMinutes : old?.DurationMinutes);
}
internal sealed record UsageUpdate(WindowUpdate? Primary, WindowUpdate? Secondary, string? Plan,
    bool HasPrimary, bool HasSecondary, bool HasPlan)
{
    public UsageSnapshot Apply(UsageSnapshot? old, bool full) => new(
        HasPrimary ? Primary?.Apply(full ? null : old?.Primary) : full ? null : old?.Primary,
        HasSecondary ? Secondary?.Apply(full ? null : old?.Secondary) : full ? null : old?.Secondary,
        HasPlan && (full || Plan is not null) ? Plan : full ? null : old?.Plan);

    public static UsageUpdate Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Missing rate limits.");
        JsonElement limits;
        if (root.TryGetProperty("rateLimitsByLimitId", out var buckets) && buckets.ValueKind == JsonValueKind.Object && buckets.TryGetProperty("codex", out var codex))
            limits = codex;
        else if (!root.TryGetProperty("rateLimits", out limits))
            throw new InvalidDataException("Missing rate limits.");
        if (limits.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Missing rate limits.");
        if (limits.TryGetProperty("limitId", out var limitId) && limitId.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            throw new InvalidDataException("Invalid limit identifier.");
        if (limits.TryGetProperty("limitId", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { } name && name != "codex")
            throw new UnsupportedLimitException();
        var primary = limits.TryGetProperty("primary", out var p);
        var secondary = limits.TryGetProperty("secondary", out var s);
        var plan = limits.TryGetProperty("planType", out var t);
        if (plan && t.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)) throw new InvalidDataException("Invalid plan.");
        return new(primary ? ParseWindow(p) : null, secondary ? ParseWindow(s) : null,
            plan ? t.GetString() : null, primary, secondary, plan);
    }
    private static WindowUpdate? ParseWindow(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("usedPercent", out var used) ||
            used.ValueKind != JsonValueKind.Number || !used.TryGetInt32(out var percent) || percent is < 0 or > 100)
            throw new InvalidDataException("Invalid allowance percentage.");
        var hasReset = value.TryGetProperty("resetsAt", out var reset);
        DateTimeOffset? at = null;
        if (hasReset && reset.ValueKind != JsonValueKind.Null)
        {
            if (reset.ValueKind != JsonValueKind.Number || !reset.TryGetInt64(out var seconds) || seconds < -62135596800L || seconds > 253402300799L)
                throw new InvalidDataException("Invalid reset time.");
            at = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        var hasDuration = value.TryGetProperty("windowDurationMins", out var duration);
        long? minutes = null;
        if (hasDuration && duration.ValueKind != JsonValueKind.Null)
        {
            if (duration.ValueKind != JsonValueKind.Number || !duration.TryGetInt64(out var n) || n <= 0) throw new InvalidDataException("Invalid window duration.");
            minutes = n;
        }
        return new(percent, at, minutes, hasReset, hasDuration);
    }
}
internal sealed class UnsupportedLimitException : Exception;
internal sealed record UsageSnapshot(UsageWindow? Primary, UsageWindow? Secondary, string? Plan);

internal sealed record UsageState(UsageSnapshot? Snapshot, ConnectionStatus Status, DateTimeOffset? UpdatedAt)
{
    public static UsageState Initial => new(null, ConnectionStatus.Connecting, null);
    public bool IsStale(DateTimeOffset now) => Snapshot is not null &&
        (Status != ConnectionStatus.Ready || UpdatedAt is null || now - UpdatedAt >= TimeSpan.FromMinutes(20));
    public string StatusText(DateTimeOffset now) => Status switch
    {
        ConnectionStatus.Connecting => "Connecting to Codex…",
        ConnectionStatus.MissingCodex => "Codex not found. Install Codex, then refresh.",
        ConnectionStatus.NeedsLogin => "Connect ChatGPT to view your allowance.",
        ConnectionStatus.SigningIn => "Finish signing in in your browser.",
        ConnectionStatus.LoginFailed => "Sign-in did not complete. Try connecting again.",
        _ when IsStale(now) => "Last known usage · " + UsageText.Age(UpdatedAt, now) + " · retrying",
        ConnectionStatus.Retrying => "Codex unavailable. Retrying shortly…",
        _ when Snapshot?.Primary is null => Snapshot?.Secondary is null ? "No allowance window supplied by Codex." : "Primary allowance unavailable.",
        _ => "Updated " + UsageText.Age(UpdatedAt, now)
    };
}
internal static class UsageText
{
    public static string Window(UsageWindow? window) => window?.DurationMinutes switch
    {
        null => "Allowance window",
        var n when n % 1440 == 0 => $"{n / 1440:N0}-day window",
        var n when n % 60 == 0 => $"{n / 60:N0}-hour window",
        var n => $"{n:N0}-minute window"
    };
    public static string Reset(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is null) return "Reset time unavailable";
        var remaining = at.Value - now;
        if (remaining <= TimeSpan.Zero) return "Awaiting reset update";
        if (remaining.TotalDays >= 1) return $"Resets in {(int)remaining.TotalDays}d {remaining.Hours}h";
        return remaining.TotalHours >= 1 ? $"Resets in {(int)remaining.TotalHours}h {remaining.Minutes:D2}m"
            : $"Resets in {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m";
    }
    public static string Age(DateTimeOffset? at, DateTimeOffset now) => at is null ? "time unknown"
        : now - at < TimeSpan.FromMinutes(1) ? "just now"
        : now - at < TimeSpan.FromHours(1) ? $"{Math.Max(1, (int)(now - at.Value).TotalMinutes)}m ago"
        : $"{Math.Max(1, (int)(now - at.Value).TotalHours)}h ago";
}
internal sealed class RefreshSchedule(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private int _failures;
    private int _unchanged;
    private UsageSnapshot? _lastSnapshot;
    public DateTimeOffset Next { get; private set; } = DateTimeOffset.MinValue;
    public bool Due => _clock.GetUtcNow() >= Next;
    public void Succeeded(UsageSnapshot snapshot)
    {
        _failures = 0;
        _unchanged = snapshot == _lastSnapshot ? Math.Min(_unchanged + 1, 4) : 0;
        _lastSnapshot = snapshot;
        int[] minutes = [1, 2, 5, 10, 15];
        var now = _clock.GetUtcNow();
        Next = now.AddMinutes(minutes[_unchanged]);
        CapAtReset(snapshot, now);
    }
    public void Changed(UsageSnapshot snapshot)
    {
        if (snapshot == _lastSnapshot) return;
        _lastSnapshot = snapshot;
        _unchanged = -1;
        var now = _clock.GetUtcNow();
        if (Next > now.AddMinutes(1)) Next = now.AddMinutes(1);
        CapAtReset(snapshot, now);
    }
    private void CapAtReset(UsageSnapshot snapshot, DateTimeOffset now)
    {
        var reset = new[] { snapshot.Primary?.ResetsAt, snapshot.Secondary?.ResetsAt }
            .Where(value => value > now).Min();
        if (reset is { } at && at.AddSeconds(30) < Next) Next = at.AddSeconds(30);
    }
    public void Failed()
    {
        int[] delays = [15, 30, 60, 120, 300];
        Next = _clock.GetUtcNow().AddSeconds(delays[_failures]);
        _failures = Math.Min(_failures + 1, delays.Length - 1);
    }
    public void Now() => Next = DateTimeOffset.MinValue;
}
