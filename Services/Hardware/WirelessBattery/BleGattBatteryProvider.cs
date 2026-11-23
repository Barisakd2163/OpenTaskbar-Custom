using System.Runtime.InteropServices;
using OpenTaskbar.Widgets;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace OpenTaskbar.Services;

/// <summary>
/// Clean-room WinRT BLE GATT Battery Service (UUID 0x180F, Characteristic 0x2A19) fallback provider.
/// Queries paired and connected Bluetooth Low Energy peripherals whose PnP nodes did not populate
/// the cached DEVPKEY_Bluetooth_Battery byte. Deterministically disposes every WinRT BluetoothLEDevice
/// and GattDeviceService handle.
/// </summary>
internal sealed class BleGattBatteryProvider : IWirelessBatteryProvider
{
    public const ushort BatteryServiceShortId = 0x180F;
    public const ushort BatteryLevelCharacteristicShortId = 0x2A19;

    public static readonly Guid BatteryServiceUuid = new("0000180f-0000-1000-8000-00805f9b34fb");
    public static readonly Guid BatteryLevelCharacteristicUuid = new("00002a19-0000-1000-8000-00805f9b34fb");

    private const string BatteryLevelProperty = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string BatteryLifeProperty = "System.Devices.BatteryLife";
    private const string ContainerProperty = "System.Devices.ContainerId";
    private const string AepIdProperty = "System.Devices.Aep.AepId";
    private const string PresentProperty = "System.Devices.Present";
    private const string DevPkeyIsPresentProperty = "{540b947e-8b40-45bc-a8a2-6a0b894cbda2} 5";

    private static readonly string[] NodeProperties =
    [
        ContainerProperty,
        AepIdProperty,
        BatteryLevelProperty,
        BatteryLifeProperty,
        PresentProperty,
        DevPkeyIsPresentProperty
    ];

    public WirelessBatterySource Source => WirelessBatterySource.BluetoothGatt;

    public Task<IReadOnlyList<WirelessDeviceReading>> ProbeAsync(
        IReadOnlyList<HidInterfaceDescriptor> hidInterfaces,
        CancellationToken cancellationToken) =>
        ProbeBleDevicesAsync(cancellationToken);

    internal static async Task<IReadOnlyList<WirelessDeviceReading>> ProbeBleDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return [];

        DeviceInformationCollection nodes;
        try
        {
            nodes = await DeviceInformation
                .FindAllAsync(string.Empty, NodeProperties, DeviceInformationKind.Device)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return [];
        }

        // Group by container: if any node in the container already has a valid PnP battery percentage,
        // the fast PnP Bluetooth reader already handles it, so we only probe BLE containers missing PnP battery.
        var presentContainers = new HashSet<Guid>();
        var containersWithPnpBattery = new HashSet<Guid>();
        var bleCandidatesByContainer = new Dictionary<Guid, (string AepId, string Name)>();

        foreach (var node in nodes)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!TryReadContainerGuid(node.Properties, out var container) || container == Guid.Empty)
                continue;

            var isPresent = (node.Properties.TryGetValue(PresentProperty, out var p) && p is true)
                || (node.Properties.TryGetValue(DevPkeyIsPresentProperty, out var dp) && dp is true);
            if (isPresent)
                presentContainers.Add(container);

            if (HasValidPnpBattery(node.Properties))
            {
                containersWithPnpBattery.Add(container);
                continue;
            }

            if (!isPresent)
                continue;

            var aepId = node.Properties.TryGetValue(AepIdProperty, out var aepVal) ? aepVal?.ToString() : null;
            if (!IsBleAssociationEndpointId(aepId))
                continue;

            if (!bleCandidatesByContainer.ContainsKey(container) || !string.IsNullOrWhiteSpace(node.Name))
            {
                bleCandidatesByContainer[container] = (aepId!, node.Name ?? string.Empty);
            }
        }

        var targets = bleCandidatesByContainer
            .Where(kvp => presentContainers.Contains(kvp.Key) && !containersWithPnpBattery.Contains(kvp.Key))
            .Select(kvp => kvp.Value)
            .Take(6)
            .ToList();

        if (targets.Count == 0) return [];

        var results = new List<WirelessDeviceReading>();
        var now = DateTimeOffset.UtcNow;

        foreach (var (aepId, fallbackName) in targets)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var reading = await TryReadSingleBleDeviceAsync(aepId, fallbackName, now, cancellationToken).ConfigureAwait(false);
            if (reading is not null)
            {
                results.Add(reading);
            }
        }

        return results;
    }

    internal static bool IsBleAssociationEndpointId(string? aepId) =>
        !string.IsNullOrWhiteSpace(aepId)
        && aepId.StartsWith("BluetoothLE#", StringComparison.OrdinalIgnoreCase);

    internal static bool TryParseGattBatteryPayload(
        ReadOnlySpan<byte> payload,
        string id,
        string name,
        BluetoothDeviceKind kind,
        DateTimeOffset sampledAt,
        out WirelessDeviceReading? reading)
    {
        reading = null;
        if (payload.Length < 1) return false;

        var level = (int)payload[0];
        if (level is < 0 or > 100) return false;

        var cleanName = string.IsNullOrWhiteSpace(name) ? "BLE Wireless Device" : name.Trim();
        if (WirelessBatteryAggregator.IsIgnoredWiredDevice(0, 0, cleanName))
            return false;

        var resolvedKind = kind != BluetoothDeviceKind.Generic
            ? kind
            : BluetoothBatteryWidget.ClassifyNode(cleanName, id);

        reading = new WirelessDeviceReading(
            Id: string.IsNullOrWhiteSpace(id) ? $"ble:gatt:{cleanName.ToLowerInvariant()}" : id.Trim(),
            Name: cleanName,
            Kind: resolvedKind,
            BatteryLevel: level,
            IsConnected: true,
            IsCharging: false,
            IsStale: false,
            Source: WirelessBatterySource.BluetoothGatt,
            SampledAt: sampledAt);
        return true;
    }

    private static async Task<WirelessDeviceReading?> TryReadSingleBleDeviceAsync(
        string aepId,
        string fallbackName,
        DateTimeOffset sampledAt,
        CancellationToken cancellationToken)
    {
        BluetoothLEDevice? bleDevice = null;
        IReadOnlyList<GattDeviceService>? services = null;
        try
        {
            using var deviceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deviceCts.CancelAfter(TimeSpan.FromMilliseconds(350));

            bleDevice = await BluetoothLEDevice.FromIdAsync(aepId).AsTask(deviceCts.Token).ConfigureAwait(false);
            if (bleDevice is null || bleDevice.ConnectionStatus != BluetoothConnectionStatus.Connected)
                return null;

            var servicesResult = await bleDevice
                .GetGattServicesForUuidAsync(BatteryServiceUuid, BluetoothCacheMode.Cached)
                .AsTask(deviceCts.Token)
                .ConfigureAwait(false);

            services = servicesResult?.Services;
            if (servicesResult?.Status != GattCommunicationStatus.Success || services is null || services.Count == 0)
                return null;

            var service = services[0];
            var charsResult = await service
                .GetCharacteristicsForUuidAsync(BatteryLevelCharacteristicUuid, BluetoothCacheMode.Cached)
                .AsTask(deviceCts.Token)
                .ConfigureAwait(false);

            if (charsResult?.Status != GattCommunicationStatus.Success
                || charsResult.Characteristics is null
                || charsResult.Characteristics.Count == 0)
            {
                return null;
            }

            var characteristic = charsResult.Characteristics[0];
            var readResult = await characteristic
                .ReadValueAsync(BluetoothCacheMode.Cached)
                .AsTask(deviceCts.Token)
                .ConfigureAwait(false);

            if (readResult?.Status != GattCommunicationStatus.Success || readResult.Value is null || readResult.Value.Length == 0)
                return null;

            using var reader = DataReader.FromBuffer(readResult.Value);
            var rawByte = reader.ReadByte();
            ReadOnlySpan<byte> span = [rawByte];

            var displayName = !string.IsNullOrWhiteSpace(bleDevice.Name) ? bleDevice.Name : fallbackName;
            var kind = BluetoothBatteryWidget.ClassifyNode(displayName, aepId);

            return TryParseGattBatteryPayload(span, aepId, displayName, kind, sampledAt, out var reading)
                ? reading
                : null;
        }
        catch (Exception ex) when (ex is COMException or OperationCanceledException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (services is not null)
            {
                foreach (var svc in services)
                {
                    try { svc?.Dispose(); }
                    catch { }
                }
            }

            try { bleDevice?.Dispose(); }
            catch { }
        }
    }

    private static bool TryReadContainerGuid(IReadOnlyDictionary<string, object> properties, out Guid container)
    {
        container = Guid.Empty;
        if (!properties.TryGetValue(ContainerProperty, out var val) || val is null) return false;
        if (val is Guid g)
        {
            container = g;
            return true;
        }
        return Guid.TryParse(val.ToString(), out container);
    }

    private static bool HasValidPnpBattery(IReadOnlyDictionary<string, object> properties)
    {
        if (properties.TryGetValue(BatteryLevelProperty, out var raw) && TryConvertPercent(raw, out _))
            return true;
        if (properties.TryGetValue(BatteryLifeProperty, out var life) && TryConvertPercent(life, out _))
            return true;
        return false;
    }

    private static bool TryConvertPercent(object? value, out int percent)
    {
        percent = -1;
        if (value is null) return false;
        try
        {
            var converted = Convert.ToInt32(value);
            if (converted is >= 0 and <= 100)
            {
                percent = converted;
                return true;
            }
        }
        catch
        {
        }
        return false;
    }
}
