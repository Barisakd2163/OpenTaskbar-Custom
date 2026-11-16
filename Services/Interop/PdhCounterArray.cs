using System.Runtime.InteropServices;

namespace OpenTaskbar.Services;

/// <summary>
/// A wildcard Windows performance counter (for example <c>\GPU Engine(*)\Utilization Percentage</c>) read through PDH.
/// Rate counters need two collections, so the first read after creation returns no values.
/// </summary>
internal sealed class PdhCounterArray : IDisposable
{
    private const uint FormatDouble = 0x200;
    private const uint MoreData = 0x800007D2;
    private const uint ValidData = 0;
    private const uint NewData = 1;

    private readonly nint _query;
    private readonly nint _counter;

    private PdhCounterArray(nint query, nint counter)
    {
        _query = query;
        _counter = counter;
    }

    /// <summary>Returns null when the counter set does not exist on this machine.</summary>
    public static PdhCounterArray? TryCreate(string path)
    {
        if (PdhOpenQuery(0, 0, out var query) != 0) return null;
        if (PdhAddEnglishCounterW(query, path, 0, out var counter) != 0)
        {
            PdhCloseQuery(query);
            return null;
        }

        return new PdhCounterArray(query, counter);
    }

    /// <summary>Collects a sample; returns null when no valid values are available yet.</summary>
    public IReadOnlyList<(string Instance, double Value)>? Read()
    {
        if (PdhCollectQueryData(_query) != 0) return null;
        uint size = 0;
        if (PdhGetFormattedCounterArrayW(_counter, FormatDouble, ref size, out _, 0) != MoreData || size == 0) return null;
        var buffer = Marshal.AllocHGlobal((nint)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(_counter, FormatDouble, ref size, out var count, buffer) != 0) return null;
            var values = new List<(string, double)>((int)count);
            var itemSize = Marshal.SizeOf<FormattedItem>();
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<FormattedItem>(buffer + index * itemSize);
                if (item.Status is not (ValidData or NewData) || Marshal.PtrToStringUni(item.Name) is not { } name) continue;
                values.Add((name, item.Value));
            }

            return values.Count > 0 ? values : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose() => PdhCloseQuery(_query);

    // PDH_FMT_COUNTERVALUE_ITEM_W with the double member of the value union.
    [StructLayout(LayoutKind.Sequential)]
    private struct FormattedItem
    {
        public nint Name;
        public uint Status;
        public double Value;
    }

    [DllImport("pdh.dll")]
    private static extern uint PdhOpenQuery(nint dataSource, nint userData, out nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(nint query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize, out uint itemCount, nint buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(nint query);
}
