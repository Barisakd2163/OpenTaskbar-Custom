using OpenTaskbar.Widgets;

namespace OpenTaskbar.Services;

/// <summary>
/// Orchestrates Bluetooth PnP and multi-protocol wireless battery providers concurrently
/// with per-provider timeouts, failure isolation, wired-device filtering, deduplication,
/// and a 30-minute smart idle/stale reading cache.
/// </summary>
internal sealed class WirelessBatteryAggregator
{
    public const ushort WraithVendorId = 0x2E3C;
    public const ushort WraithW75ProductId = 0xC368;
    public static readonly TimeSpan DefaultProviderTimeout = TimeSpan.FromMilliseconds(1200);
    public static readonly TimeSpan StaleExpirationWindow = TimeSpan.FromMinutes(30);

    private static readonly WirelessBatteryAggregator Shared = new(
        bluetoothReader: BluetoothBatteryWidget.ReadLiveBluetoothReadingsAsync,
        providers:
        [
            new SteelSeriesBatteryProvider(),
            new CompxAtkVxeBatteryProvider(),
            new LogitechBatteryProvider(),
            new RazerBatteryProvider(),
            new XInputBatteryProvider()
        ],
        clock: () => DateTimeOffset.UtcNow,
        providerTimeout: DefaultProviderTimeout);

    private readonly Func<CancellationToken, Task<IReadOnlyList<WirelessDeviceReading>>> _bluetoothReader;
    private readonly IReadOnlyList<IWirelessBatteryProvider> _providers;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _providerTimeout;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, WirelessDeviceReading> _cache = new(StringComparer.OrdinalIgnoreCase);

    internal WirelessBatteryAggregator(
        Func<CancellationToken, Task<IReadOnlyList<WirelessDeviceReading>>> bluetoothReader,
        IReadOnlyList<IWirelessBatteryProvider> providers,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? providerTimeout = null)
    {
        _bluetoothReader = bluetoothReader;
        _providers = providers;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _providerTimeout = providerTimeout ?? DefaultProviderTimeout;
    }

    public static async Task<IReadOnlyList<BluetoothDeviceBatteryInfo>> ReadAllAsync()
    {
        var readings = await Shared.PollAsync().ConfigureAwait(false);
        return readings.Select(r => r.ToBatteryInfo()).ToList();
    }

    public static Task<IReadOnlyList<WirelessDeviceReading>> ReadAllReadingsAsync(CancellationToken cancellationToken = default) =>
        Shared.PollAsync(cancellationToken);

    internal static bool IsIgnoredWiredDevice(ushort vendorId, ushort productId, string? deviceName = null)
    {
        if (vendorId == WraithVendorId && (productId == WraithW75ProductId || productId == 0))
            return true;
        if (!string.IsNullOrWhiteSpace(deviceName)
            && deviceName.Contains("Wraith W75", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    internal static bool IsIgnoredWiredReading(WirelessDeviceReading reading)
    {
        if (string.IsNullOrWhiteSpace(reading.Name) && string.IsNullOrWhiteSpace(reading.Id))
            return true;
        if (IsIgnoredWiredDevice(0, 0, reading.Name))
            return true;
        if (reading.Id.Contains("2E3C", StringComparison.OrdinalIgnoreCase)
            && reading.Id.Contains("C368", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    public async Task<IReadOnlyList<WirelessDeviceReading>> PollAsync(CancellationToken cancellationToken = default)
    {
        var tasks = new List<Task<IReadOnlyList<WirelessDeviceReading>>>(_providers.Count + 1)
        {
            RunIsolatedAsync(_bluetoothReader, cancellationToken)
        };

        foreach (var provider in _providers)
        {
            tasks.Add(RunIsolatedAsync(provider.ProbeAsync, cancellationToken));
        }

        var batches = await Task.WhenAll(tasks).ConfigureAwait(false);
        var now = _clock();

        var freshReadings = new List<WirelessDeviceReading>();
        foreach (var batch in batches)
        {
            foreach (var reading in batch)
            {
                if (IsIgnoredWiredReading(reading)) continue;
                freshReadings.Add(reading.SampledAt == default ? reading with { SampledAt = now } : reading);
            }
        }

        var deduplicated = DeduplicateReadings(freshReadings);
        return MergeWithCache(deduplicated, now);
    }

    private async Task<IReadOnlyList<WirelessDeviceReading>> RunIsolatedAsync(
        Func<CancellationToken, Task<IReadOnlyList<WirelessDeviceReading>>> probe,
        CancellationToken outerToken)
    {
        var softTimeoutMs = Math.Max(80.0, _providerTimeout.TotalMilliseconds * 0.8);
        using var softCts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        softCts.CancelAfter(TimeSpan.FromMilliseconds(softTimeoutMs));

        using var hardCts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        hardCts.CancelAfter(_providerTimeout);
        try
        {
            var probeTask = Task.Run(() => probe(softCts.Token), CancellationToken.None);
            var completed = await Task.WhenAny(probeTask, Task.Delay(_providerTimeout, hardCts.Token)).ConfigureAwait(false);
            if (completed != probeTask)
                return [];

            return await probeTask.ConfigureAwait(false) ?? [];
        }
        catch
        {
            return [];
        }
    }

    internal static IReadOnlyList<WirelessDeviceReading> DeduplicateReadings(IEnumerable<WirelessDeviceReading> readings)
    {
        // First collapse exact same-source, same-id entries before counting distinct devices per (Source, Name).
        var bySourceAndId = new List<WirelessDeviceReading>();
        var exactIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in readings)
        {
            var normalizedId = candidate.Id.Trim();
            if (normalizedId.Length > 0)
            {
                var exactKey = $"{candidate.Source}:{normalizedId.ToLowerInvariant()}";
                if (exactIndex.TryGetValue(exactKey, out var existingIdx))
                {
                    var existing = bySourceAndId[existingIdx];
                    bySourceAndId[existingIdx] = ShouldPreferReading(existing, candidate)
                        ? MergeMetadata(primary: candidate, secondary: existing)
                        : MergeMetadata(primary: existing, secondary: candidate);
                    continue;
                }
                exactIndex[exactKey] = bySourceAndId.Count;
            }
            bySourceAndId.Add(candidate);
        }

        var sourceNameCounts = CountBySourceAndName(bySourceAndId);
        var byCanonicalKey = new Dictionary<string, WirelessDeviceReading>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in bySourceAndId)
        {
            var key = GetDeduplicationKey(candidate, sourceNameCounts, hasExistingSourceIdKey: false);
            if (!byCanonicalKey.TryGetValue(key, out var existing))
            {
                byCanonicalKey[key] = candidate;
                continue;
            }

            if (ShouldPreferReading(existing, candidate))
            {
                byCanonicalKey[key] = MergeMetadata(primary: candidate, secondary: existing);
            }
            else
            {
                byCanonicalKey[key] = MergeMetadata(primary: existing, secondary: candidate);
            }
        }

        return byCanonicalKey.Values.ToList();
    }

    private IReadOnlyList<WirelessDeviceReading> MergeWithCache(
        IReadOnlyList<WirelessDeviceReading> freshReadings,
        DateTimeOffset now)
    {
        lock (_cacheLock)
        {
            var sourceNameCounts = CountBySourceAndName(freshReadings);
            var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var output = new List<WirelessDeviceReading>();

            foreach (var reading in freshReadings)
            {
                var idKey = $"src:{reading.Source}:id:{reading.Id.Trim().ToLowerInvariant()}";
                var key = GetDeduplicationKey(reading, sourceNameCounts, _cache.ContainsKey(idKey));
                activeKeys.Add(key);

                // -1 is the only "unknown" level; a genuine 0% non-charging device is valid data.
                var hasValidFreshBattery = reading.BatteryLevel >= 0
                                           || !string.IsNullOrWhiteSpace(reading.LevelOverrideText);

                if (reading.IsConnected && hasValidFreshBattery)
                {
                    var live = reading with
                    {
                        IsStale = false,
                        SampledAt = reading.SampledAt == default ? now : reading.SampledAt
                    };
                    _cache[key] = live;
                    output.Add(live);
                    continue;
                }

                // Never overwrite a valid cached percentage with 0% on timeout/sleep, and keep the last reading
                // marked stale while within the 30-minute expiration window.
                if (_cache.TryGetValue(key, out var cached) && now - cached.SampledAt <= StaleExpirationWindow)
                {
                    var retained = cached with
                    {
                        IsConnected = true,
                        IsCharging = reading.IsCharging,
                        IsStale = true,
                        SecondaryBatteryLevel = reading.SecondaryBatteryLevel ?? cached.SecondaryBatteryLevel
                    };
                    output.Add(retained);
                }
                else if (reading.IsConnected && reading.BatteryLevel >= 0)
                {
                    output.Add(reading);
                }
            }

            // Retain recently seen devices that went to sleep or temporarily dropped off the bus (up to 30 minutes).
            var expiredKeys = new List<string>();
            foreach (var (key, cached) in _cache)
            {
                if (now - cached.SampledAt > StaleExpirationWindow)
                {
                    expiredKeys.Add(key);
                    continue;
                }

                if (activeKeys.Contains(key)) continue;

                output.Add(cached with
                {
                    IsConnected = true,
                    IsCharging = false,
                    IsStale = true
                });
            }

            foreach (var key in expiredKeys)
                _cache.Remove(key);

            return output
                .OrderBy(r => r.IsStale ? 1 : 0)
                .ThenBy(r => GetSourcePriority(r.Source))
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private static Dictionary<(WirelessBatterySource Source, string NormalizedName), int> CountBySourceAndName(
        IEnumerable<WirelessDeviceReading> readings)
    {
        var counts = new Dictionary<(WirelessBatterySource, string), int>();
        foreach (var reading in readings)
        {
            var normalizedName = NormalizeDeviceName(reading.Name);
            if (string.IsNullOrWhiteSpace(normalizedName)) continue;
            var tuple = (reading.Source, normalizedName);
            counts[tuple] = counts.TryGetValue(tuple, out var current) ? current + 1 : 1;
        }
        return counts;
    }

    private static string GetDeduplicationKey(
        WirelessDeviceReading reading,
        IReadOnlyDictionary<(WirelessBatterySource Source, string NormalizedName), int> sourceNameCounts,
        bool hasExistingSourceIdKey)
    {
        var normalizedName = NormalizeDeviceName(reading.Name);
        if (!string.IsNullOrWhiteSpace(normalizedName))
        {
            var hasCollisionInSource = sourceNameCounts.TryGetValue((reading.Source, normalizedName), out var count) && count > 1;
            if ((hasCollisionInSource || hasExistingSourceIdKey) && !string.IsNullOrWhiteSpace(reading.Id))
                return $"src:{reading.Source}:id:{reading.Id.Trim().ToLowerInvariant()}";
            return $"name:{normalizedName}";
        }
        return $"id:{reading.Id.Trim().ToLowerInvariant()}";
    }

    internal static string GetCanonicalDeviceKey(WirelessDeviceReading reading)
    {
        // Name is the only identity shared across providers (e.g. SteelSeries HID and its GG
        // fallback report different IDs for the same physical headset), so it must be the
        // primary dedup key. Fall back to the provider ID for nameless devices.
        var normalizedName = NormalizeDeviceName(reading.Name);
        if (!string.IsNullOrWhiteSpace(normalizedName))
            return $"name:{normalizedName}";
        return $"id:{reading.Id.Trim().ToLowerInvariant()}";
    }

    private static string NormalizeDeviceName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var span = name.Trim();
        return string.Join(' ', span.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }

    private static bool ShouldPreferReading(WirelessDeviceReading existing, WirelessDeviceReading candidate)
    {
        if (existing.IsConnected != candidate.IsConnected)
            return candidate.IsConnected;

        var existingHasValid = existing.BatteryLevel >= 0 || !string.IsNullOrWhiteSpace(existing.LevelOverrideText);
        var candidateHasValid = candidate.BatteryLevel >= 0 || !string.IsNullOrWhiteSpace(candidate.LevelOverrideText);
        if (existingHasValid != candidateHasValid)
            return candidateHasValid;

        if (existing.IsStale != candidate.IsStale)
            return !candidate.IsStale;

        return GetSourcePriority(candidate.Source) < GetSourcePriority(existing.Source);
    }

    private static WirelessDeviceReading MergeMetadata(WirelessDeviceReading primary, WirelessDeviceReading secondary) =>
        primary with
        {
            SecondaryBatteryLevel = primary.SecondaryBatteryLevel ?? secondary.SecondaryBatteryLevel,
            VoltageMv = primary.VoltageMv ?? secondary.VoltageMv
        };

    private static int GetSourcePriority(WirelessBatterySource source) => source switch
    {
        WirelessBatterySource.SteelSeriesHid => 0,
        WirelessBatterySource.CompxAtkVxe => 1,
        WirelessBatterySource.LogitechHidPlusPlus => 2,
        WirelessBatterySource.RazerHid => 3,
        WirelessBatterySource.Bluetooth => 4,
        WirelessBatterySource.XInput => 5,
        WirelessBatterySource.SteelSeriesGg => 6,
        WirelessBatterySource.LogitechGHub => 7,
        _ => 10
    };
}
