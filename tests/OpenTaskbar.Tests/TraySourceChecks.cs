using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ManagedShell.WindowsTray;
using OpenTaskbar.Services;
using OpenTaskbar.Views.Shell;

// P3: exercises tray identity and snapshot logic with injected in-memory data. The live ManagedShell
// collection, Explorer and HKCU are never touched; the private merge step is invoked directly so the
// dedupe rules can be simulated deterministically.
internal static class TraySourceChecks
{
    private const string AppA = @"C:\Apps\Alpha.exe";
    private const string AppB = @"C:\Apps\Bravo.exe";
    private const string AppC = @"C:\Apps\Charlie.exe";

    internal static int RunHeadless()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks++;
        }

        var guid = new Guid("11111111-2222-3333-4444-555555555555");

        // Legacy key migration: guid:, path:, path:|uid and wildcard path:|* all parse without throwing.
        Check(TrayPinIdentity.TryParseRef($"guid:{guid:D}", out var guidRef)
            && guidRef.Guid == guid && guidRef.KeyKind == PinKeyKind.Guid
            && string.IsNullOrEmpty(guidRef.NormalizedPath) && guidRef.Uid == 0,
            "guid:<D> must parse into a GUID-keyed identity.");
        Check(TrayPinIdentity.TryParseRef(@"path:C:\Apps\Alpha.exe", out var pathRef)
            && pathRef.Guid is null && pathRef.NormalizedPath == @"c:\apps\alpha.exe"
            && pathRef.Uid == 0 && pathRef.KeyKind == PinKeyKind.Path,
            "A bare path key must normalize to lowercase and carry no UID.");
        Check(TrayPinIdentity.TryParseRef(@"path:C:\Apps\Multi.EXE|7", out var uidRef)
            && uidRef.NormalizedPath == @"c:\apps\multi.exe" && uidRef.Uid == 7,
            "path:|uid must preserve the numeric UID.");
        Check(TrayPinIdentity.TryParseRef(@"path:C:\Apps\Alpha.exe|*", out var wildcardRef)
            && wildcardRef.NormalizedPath == @"c:\apps\alpha.exe" && wildcardRef.Uid == 0,
            "path:|* (any UID) must parse to UID 0.");
        foreach (var invalid in new string?[] { null, "  ", "unknown:value", "guid:not-a-guid", @"path:|9", "{\"guid\":\"bad\"}" })
        {
            Check(!TrayPinIdentity.TryParseRef(invalid, out _),
                $"Unknown or malformed pin keys must not parse: '{invalid}'.");
        }

        // Structured JSON round-trips and stays forward compatible (numeric or string UID, any casing).
        var structured = new TrayIconRef(guid, AppA, 12, IconSource.Live).ToJson();
        Check(structured.Contains("schemaVersion") && TrayPinIdentity.TryParseRef(structured, out var fromJson)
            && fromJson.Guid == guid && fromJson.NormalizedPath == @"c:\apps\alpha.exe" && fromJson.Uid == 12,
            "A structured JSON pin must round-trip through TryParseRef with schemaVersion metadata.");
        Check(TrayPinIdentity.TryParseRef("{\"PATH\":\"C:\\\\Apps\\\\Bravo.exe\",\"UID\":\"9\"}", out var caseJson)
            && caseJson.NormalizedPath == @"c:\apps\bravo.exe" && caseJson.Uid == 9,
            "Structured pin parsing must accept case-insensitive property names and string UIDs.");
        Check(TrayPinIdentity.TryParseRef(TrayPinIdentity.ToStructuredJson(guidRef), out var migratedGuid)
            && migratedGuid.Guid == guid && migratedGuid.KeyKind == PinKeyKind.Guid,
            "Migrating a legacy guid: pin to structured JSON must preserve its identity.");

        // Comparer identity is (path, guid, uid) only; discovery source is deliberately ignored.
        var comparer = TrayIconRefComparer.Instance;
        var live = new TrayIconRef(guid, AppA, 5, IconSource.Live);
        var registryTwin = live.WithSource(IconSource.Registry);
        Check(comparer.Equals(live, registryTwin) && comparer.GetHashCode(live) == comparer.GetHashCode(registryTwin),
            "The same identity from a different source must collapse to one snapshot entry.");
        Check(!comparer.Equals(live, new TrayIconRef(guid, AppA, 6, IconSource.Live)),
            "Different UIDs from the same executable must stay distinct.");
        Check(!comparer.Equals(live, new TrayIconRef(new Guid("22222222-2222-2222-2222-222222222222"), AppA, 5, IconSource.Live)),
            "Different GUIDs on the same path and UID must stay distinct.");
        Check(!comparer.Equals(live, new TrayIconRef(guid, AppB, 5, IconSource.Live)),
            "Different executable paths must stay distinct even with the same GUID and UID.");

        // Snapshot dedupe simulation: same exe + different GUID/UID stays distinct; a shared tooltip never merges.
        var sameExe = Merge(
            [new TrayApp("Same", AppA, null, guid, 1, IconSource.Live),
             new TrayApp("Same", AppA, null, new Guid("22222222-2222-2222-2222-222222222222"), 2, IconSource.Live)],
            []);
        Check(sameExe.Count == 2 && sameExe[0].Uid == 1 && sameExe[1].Uid == 2,
            "Two live icons from one executable with different GUID/UID must both survive the snapshot merge.");

        var sameTooltip = Merge(
            [new TrayApp("Helper", AppA, null, null, 0, IconSource.Live),
             new TrayApp("Helper", AppB, null, null, 0, IconSource.Live)],
            []);
        Check(sameTooltip.Count == 2 && sameTooltip.Select(app => app.ExecutablePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
            "Identical tooltips from different executables must never be deduplicated.");

        var withFallback = Merge(
            [new TrayApp("Live", AppC, null, guid, 3, IconSource.Live)],
            [new TrayApp("Hidden App", AppB, null, null, 0, IconSource.Registry),
             new TrayApp("Registry Twin", AppC, null, guid, 3, IconSource.Registry)]);
        Check(withFallback.Count == 2
            && withFallback.Any(app => app.Name == "Hidden App" && app.Source == IconSource.Registry)
            && !withFallback.Any(app => app.Name == "Registry Twin"),
            "A registry fallback app with no live icon must be included, while a live/registry twin collapses.");

        return checks;
    }

    internal static int RunUi()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks++;
        }

        var strip = new PinnedTrayStrip();
        var guid = new Guid("33333333-3333-3333-3333-333333333333");
        const string path = @"C:\Apps\GuidPathApp.exe";
        var icon = new NotifyIcon(null!) { GUID = guid, Path = path, UID = 11, Title = "Guid Path App" };
        var telemetry = new List<(string Kind, string Strategy)>();
        var opened = new List<TrayApp>();

        strip.PinClickTelemetry = (kind, strategy) => telemetry.Add((kind, strategy));
        strip.ClickHandler = (_, _) => throw new InvalidOperationException(
            "A stored path pin must not send a synthetic tray click even when the live icon has a GUID.");
        strip.OpenAppHandler = (app, _) => opened.Add(app);
        strip.AppsProvider = () => [];

        // Stored intent is a path pin; live metadata supplies a GUID that must NOT change the strategy.
        var storedKey = TrayPinIdentity.KeyForPath(path);
        strip.UpdateFromIcons([icon], [storedKey]);
        strip.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(telemetry.Count == 1 && telemetry[0] == ("Path", "open-path"),
            "PinClick telemetry must report the stored key kind and the path-opening strategy.");
        Check(opened.Count == 1 && string.Equals(opened[0].ExecutablePath, path, StringComparison.OrdinalIgnoreCase),
            "A path pin matched to a GUID-bearing live icon must open by the stored executable path.");

        // The same live icon pinned by GUID keeps the tray-click strategy instead.
        strip.ClickHandler = (_, button) => opened.Add(new TrayApp("tray-click:" + button, string.Empty, null));
        strip.UpdateFromIcons([icon], [$"guid:{guid:D}"]);
        strip.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(telemetry.Count == 2 && telemetry[1] == ("Guid", "tray-click")
            && opened.Count == 2 && opened[1].Name == "tray-click:Left",
            "A GUID pin must keep the tray-click strategy for the same live icon.");

        return checks;
    }

    private static IReadOnlyList<TrayApp> Merge(IReadOnlyList<TrayApp> live, IReadOnlyList<TrayApp> fallback)
    {
        var method = typeof(TrayIconSource).GetMethod("Merge", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (IReadOnlyList<TrayApp>)method.Invoke(null, [live, fallback])!;
    }
}
