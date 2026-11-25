using System.IO;
using OpenTaskbar.Services;

internal static class ShellProcessIdentityChecks
{
    internal static int Run()
    {
        var expected = Path.GetFileName(Environment.ProcessPath)
            ?? throw new InvalidOperationException("The isolated check process has no executable identity.");
        var pid = (uint)Environment.ProcessId;
        Require(ShellShortcuts.IsProcessImage(pid, expected), "The real process image must be recognized without a global process-name snapshot.");
        Require(ShellShortcuts.IsProcessImage(pid, expected.ToUpperInvariant()), "Windows executable identity comparisons must be case-insensitive.");
        Require(!ShellShortcuts.IsProcessImage(pid, "UnrelatedShellHost.exe"), "An unrelated foreground process must not be recognized as a shell host.");
        Require(!ShellShortcuts.IsProcessImage(uint.MaxValue, expected) && !ShellShortcuts.IsProcessImage(0, expected),
            "Missing/unqueryable processes must not be assigned a cached or fabricated executable identity.");
        return 4;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
