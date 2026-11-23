using System.IO;
using Microsoft.Win32;

namespace OpenTaskbar.Services;

internal static class NightLightControl
{
    private const string StatePath =
        @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate";

    private const int StoreHeaderSize = 16;
    private static ReadOnlySpan<byte> EnvelopePrefix => [0x43, 0x42, 1, 0, 0x0A, 2, 1, 0, 0x2A, 6];
    private static ReadOnlySpan<byte> PayloadPrefix => [0x2A, 0x2B, 0x0E];
    private static ReadOnlySpan<byte> BondHeader => [0x43, 0x42, 1, 0];
    private static ReadOnlySpan<byte> EmptyEnvelope => [0x43, 0x42, 1, 0, 0x0A, 2, 1, 0, 0x2A, 0x2A, 0, 0, 0];

    internal static bool? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StatePath);
        return key?.GetValue("Data") is byte[] data ? DecodeState(data) : null;
    }

    internal static bool Set(bool on)
    {
        using var key = Registry.CurrentUser.OpenSubKey(StatePath, writable: true);
        if (key?.GetValue("Data") is not byte[] data || EncodeChange(data, on, DateTimeOffset.UtcNow) is not { } encoded)
            return false;

        if (!ReferenceEquals(data, encoded))
            key.SetValue("Data", encoded, RegistryValueKind.Binary);
        return true;
    }

    internal static byte[]? EncodeChange(byte[] data, bool on, DateTimeOffset now) =>
        TryRead(data, out var state) && state.Usable
            ? (state.On == on ? data : Encode(data, state, on, now))
            : null;

    internal static bool? DecodeState(byte[] data) =>
        TryRead(data, out var state) && state.Usable ? state.On : null;

    private readonly record struct State(bool On, bool Usable, ulong Modified, bool ExplicitUsable, int HeaderSize);

    // CloudStore is private to Windows. Only write recognized CompactBinary state records.
    // The schedule and strength live in a separate registry value.
    private static bool TryRead(ReadOnlySpan<byte> data, out State state)
    {
        state = default;
        var headerSize = data.Length >= StoreHeaderSize && data.StartsWith<byte>([2, 0, 0, 0]) ? StoreHeaderSize : 0;
        var envelope = data[headerSize..];
        if (envelope.SequenceEqual(EmptyEnvelope))
        {
            state = new State(false, true, 0, false, headerSize);
            return true;
        }
        if (!Take(ref envelope, EnvelopePrefix) || !ReadVarInt(ref envelope, out var modified)
            || modified > long.MaxValue - 2 || !Take(ref envelope, PayloadPrefix)
            || !ReadVarInt(ref envelope, out var length) || envelope.Length < 3
            || length != (ulong)(envelope.Length - 3) || !envelope[^3..].SequenceEqual<byte>([0, 0, 0]))
            return false;

        var payload = envelope[..^3];
        if (!Take(ref payload, BondHeader)) return false;
        var on = Take(ref payload, [0x10, 0]);
        if (Take(ref payload, [0xD0, 0x0A]) && !ReadVarInt(ref payload, out _)) return false;
        if (Take(ref payload, [0xC6, 0x14]) && !ReadVarInt(ref payload, out _)) return false;
        var explicitUsable = Take(ref payload, [0xC2, 0x1E]);
        var usable = true;
        if (explicitUsable)
        {
            if (payload.IsEmpty || payload[0] > 1) return false;
            usable = payload[0] == 1;
            payload = payload[1..];
        }

        if (!payload.SequenceEqual<byte>([0])) return false;
        state = new State(on, usable, modified, explicitUsable, headerSize);
        return true;
    }

    private static byte[] Encode(byte[] original, State state, bool on, DateTimeOffset now)
    {
        using var payload = new MemoryStream();
        payload.Write(BondHeader);
        if (on) payload.Write([0x10, 0]); // Field 0: running.
        payload.Write([0xD0, 0x0A, 2, 0xC6, 0x14]); // Manual cause and transition FILETIME.
        WriteVarInt(payload, (ulong)now.ToFileTime());
        if (state.ExplicitUsable) payload.Write([0xC2, 0x1E, 1]);
        payload.WriteByte(0);

        using var result = new MemoryStream();
        result.Write(original.AsSpan(0, state.HeaderSize));
        result.Write(EnvelopePrefix);
        WriteVarInt(result, Math.Max((ulong)now.ToUnixTimeSeconds(), state.Modified + 2));
        result.Write(PayloadPrefix);
        WriteVarInt(result, (ulong)payload.Length);
        payload.WriteTo(result);
        result.Write([0, 0, 0]);
        return result.ToArray();
    }

    private static bool Take(ref ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix)
    {
        if (!data.StartsWith(prefix)) return false;
        data = data[prefix.Length..];
        return true;
    }

    private static bool ReadVarInt(ref ReadOnlySpan<byte> data, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 64 && !data.IsEmpty; shift += 7)
        {
            var next = data[0];
            data = data[1..];
            if (shift == 63 && next > 1) return false;
            value |= (ulong)(next & 0x7F) << shift;
            if (next < 0x80) return true;
        }
        return false;
    }

    private static void WriteVarInt(Stream output, ulong value)
    {
        while (value >= 0x80)
        {
            output.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        output.WriteByte((byte)value);
    }
}
