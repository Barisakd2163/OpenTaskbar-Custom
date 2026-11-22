using System.Collections.Generic;
using System.Threading;

namespace OpenTaskbar.Services;

/// <summary>
/// Structured, low-overhead event log for shell integration. Every event carries a process-local
/// correlation sequence so a broadcast we sent ourselves can be distinguished from a real Explorer
/// restart, and so a symptom reported later can be tied to the exact chain of OS events that preceded it.
/// </summary>
public static class ShellTelemetry
{
    private static long _sequence;

    /// <summary>Monotonically increasing per-process event/broadcast sequence (never 0).</summary>
    public static long NextSequence() => Interlocked.Increment(ref _sequence);

    /// <summary>Writes a shell event with named fields in <c>key=value</c> form.</summary>
    public static void Event(string name, params (string Key, object? Value)[] fields)
    {
        if (fields.Length == 0)
        {
            AppLog.Info($"EVENT {name}");
            return;
        }
        var parts = new string[fields.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            var value = fields[i].Value;
            parts[i] = $"{fields[i].Key}={(value is null ? "null" : value)}";
        }
        AppLog.Info($"EVENT {name} {string.Join(' ', parts)}");
    }

    /// <summary>Logs the result of a reconciliation/repair attempt, including whether it was verified.</summary>
    public static void Result(string operation, string reason, bool success, bool verified,
        params (string Key, object? Value)[] fields)
    {
        var all = new List<(string, object?)>
        {
            ("reason", reason),
            ("ok", success),
            ("verified", verified)
        };
        all.AddRange(fields);
        Event(operation, all.ToArray());
    }
}
