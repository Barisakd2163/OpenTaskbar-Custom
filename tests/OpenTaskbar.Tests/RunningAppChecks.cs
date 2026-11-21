using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenTaskbar.Models;
using OpenTaskbar.Services;

internal static class RunningAppChecks
{
    public static int Run()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        var resource = CreateIcon();
        var settings = CreateIcon();
        var calculator = CreateIcon();
        var generic = CreateIcon();
        const string Desktop = @"C:\Apps\Desktop.exe";
        const string Packaged = @"C:\Program Files\WindowsApps\Vendor.App_1.0.0.0_x64__abc\App.exe";
        const string FrameHost = @"C:\Windows\System32\ApplicationFrameHost.exe";
        var appIds = new Dictionary<nint, string?> { [1] = null, [2] = "Vendor.App_abc!App", [3] = "Settings!App", [4] = "Calculator!App" };
        var shellIcons = new Dictionary<string, BitmapSource> { ["Vendor.App_abc!App"] = settings, ["Settings!App"] = settings, ["Calculator!App"] = calculator };
        var resourceCalls = 0;
        var identityCalls = 0;
        var shellCalls = 0;
        var clock = new TestClock();
        var cache = new RunningAppIconCache(
            path => { resourceCalls++; return path == Desktop ? resource : null; },
            window => { identityCalls++; return appIds.GetValueOrDefault(window); },
            appId => { shellCalls++; return shellIcons.GetValueOrDefault(appId); },
            _ => generic,
            clock);

        Check(ReferenceEquals(cache.Get(Desktop, 1), resource) && identityCalls == 0,
            "Executable resources must win without querying the window identity.");
        cache.Get(Desktop, 1);
        Check(resourceCalls == 1, "Executable resources must be extracted once per executable.");

        Check(ReferenceEquals(cache.Get(Packaged, 2), settings), "A packaged executable without resources must use its AppsFolder icon.");
        Check(!cache.HasPendingLookups, "A resolved identity icon must not schedule retries.");

        Check(ReferenceEquals(cache.Get(FrameHost, 3), settings) && ReferenceEquals(cache.Get(FrameHost, 4), calculator),
            "Hosted Store apps must not share an icon through their common host executable.");
        var shellCallsBefore = shellCalls;
        Check(ReferenceEquals(cache.Get(FrameHost, 3), settings) && shellCalls == shellCallsBefore,
            "A resolved identity icon must be reused without another Shell lookup.");

        appIds[5] = null;
        var identityBefore = identityCalls;
        Check(ReferenceEquals(cache.Get(FrameHost, 5), generic) && cache.HasPendingLookups,
            "A window without an identity yet must show the generic icon and wait for a retry.");
        cache.Get(FrameHost, 5);
        Check(identityCalls == identityBefore + 1, "Retries must wait for the retry delay.");
        clock.Advance(RunningAppIconCache.RetryDelay);
        appIds[5] = "Calculator!App";
        Check(ReferenceEquals(cache.Get(FrameHost, 5), calculator) && !cache.HasPendingLookups,
            "An identity published after the window appeared must replace the generic icon.");

        appIds[6] = null;
        for (var attempt = 0; attempt < RunningAppIconCache.AttemptLimit + 3; attempt++)
        {
            cache.Get(FrameHost, 6);
            clock.Advance(RunningAppIconCache.RetryDelay);
        }
        Check(identityCalls == identityBefore + 2 + RunningAppIconCache.AttemptLimit && !cache.HasPendingLookups,
            "Identity lookups must stop after the attempt limit.");

        var idleClock = new TestClock();
        var idle = new RunningAppIconCache(_ => null, _ => null, _ => null, _ => generic, idleClock);
        idle.Get(FrameHost, 7);
        idleClock.Advance(RunningAppIconCache.RetryDelay * RunningAppIconCache.AttemptLimit);
        Check(!idle.HasPendingLookups, "Retries must end for a window that is no longer asked for.");

        cache.Retain([new TrackedWindow(2, "App", Packaged)]);
        var resourceBefore = resourceCalls;
        identityBefore = identityCalls;
        Check(ReferenceEquals(cache.Get(Packaged, 2), settings) && resourceCalls == resourceBefore && identityCalls == identityBefore,
            "Tracked windows must keep their cached icons.");
        cache.Get(Desktop, 1);
        cache.Get(FrameHost, 6);
        Check(resourceCalls == resourceBefore + 2 && identityCalls == identityBefore + 1,
            "Entries of closed windows and executables must be dropped.");
        Check(cache.HasPendingLookups, "A reopened window must be looked up again.");

        // Offloaded mode: lookups run in the background and Get never blocks the dock.
        var queued = new Queue<(Func<BitmapSource?> Load, TaskCompletionSource<BitmapSource?> Done)>();
        Task<BitmapSource?> Offload(Func<BitmapSource?> load)
        {
            var done = new TaskCompletionSource<BitmapSource?>();
            queued.Enqueue((load, done));
            return done.Task;
        }
        void FinishLoads()
        {
            while (queued.TryDequeue(out var item)) item.Done.SetResult(item.Load());
        }

        var loaded = 0;
        var asyncIds = new Dictionary<nint, string?> { [2] = "Vendor.App_abc!App", [8] = "Missing!App" };
        var background = new RunningAppIconCache(
            path => path == Desktop ? resource : null,
            window => asyncIds.GetValueOrDefault(window),
            appId => shellIcons.GetValueOrDefault(appId),
            _ => generic,
            new TestClock(),
            Offload);
        background.IconLoaded += () => loaded++;

        Check(background.Get(Desktop, 1) is null && background.IsLoading,
            "A first lookup must not block the caller; there is no icon until the background load ends.");
        FinishLoads();
        Check(loaded == 1 && !background.IsLoading && ReferenceEquals(background.Get(Desktop, 1), resource),
            "A finished load must notify the caller and then serve the icon from the cache.");

        Check(background.Get(Packaged, 2) is null, "The resource lookup of a new executable runs in the background.");
        FinishLoads();
        Check(background.Get(Packaged, 2) is null && background.IsLoading,
            "Without executable resources the Start menu image is loaded next, still in the background.");
        Check(background.IsLoadingFor(Packaged, 2) && !background.IsLoadingFor(Desktop, 1),
            "A pending lookup must only hold back the icon of its own app.");
        // The dock refreshes (and prunes the cache with Retain) whenever a load finishes; a Store app such as
        // WhatsApp lost its freshly loaded image there and reloaded it in a loop, never showing an icon.
        background.Retain([new TrackedWindow(1, "Desktop", Desktop), new TrackedWindow(2, "App", Packaged)]);
        FinishLoads();
        background.Retain([new TrackedWindow(1, "Desktop", Desktop), new TrackedWindow(2, "App", Packaged)]);
        Check(ReferenceEquals(background.Get(Packaged, 2), settings) && !background.HasPendingLookups && !background.IsLoading,
            "A Start menu image loaded in the background must survive the refresh it triggers.");

        background.Get(FrameHost, 8);
        FinishLoads();
        background.Get(FrameHost, 8);
        FinishLoads();
        background.Get(FrameHost, 8);
        FinishLoads();
        Check(ReferenceEquals(background.Get(FrameHost, 8), generic) && background.HasPendingLookups,
            "A missing Start menu image must fall back to the file icon and keep its identity retries.");

        // Issue #83: Pinned web apps (IconLocation) and packaged Store apps (WindowsApps + AppxManifest.xml)
        {
            Check(ApplicationIconProvider.TryParseIconLocation(@"C:\Web Applications\_crx_abc\YouTube Music.ico,0", out var pwaPath, out var pwaIdx)
                  && pwaPath == @"C:\Web Applications\_crx_abc\YouTube Music.ico" && pwaIdx == 0,
                "TryParseIconLocation must parse Chrome PWA .ico paths with index 0.");
            Check(ApplicationIconProvider.TryParseIconLocation("\"%SystemRoot%\\System32\\imageres.dll\", -184", out var resPath, out var resIdx)
                  && resPath.EndsWith(@"\System32\imageres.dll", StringComparison.OrdinalIgnoreCase) && resIdx == -184,
                "TryParseIconLocation must expand environment variables, strip quotes, and parse negative resource indices.");
            Check(!ApplicationIconProvider.TryParseIconLocation("   ", out _, out _),
                "TryParseIconLocation must reject blank icon locations.");

            var fakeRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ot-winapps-{Guid.NewGuid():N}");
            try
            {
                var pkgDir = System.IO.Path.Combine(fakeRoot, "WindowsApps", "5319275A.WhatsAppDesktop_2.2500.1.0_x64__cv1g1gvanyjgm");
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(pkgDir, "Sub"));
                System.IO.File.WriteAllText(System.IO.Path.Combine(pkgDir, "AppxManifest.xml"), """
                    <?xml version="1.0" encoding="utf-8"?>
                    <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
                      <Identity Name="5319275A.WhatsAppDesktop" Publisher="CN=Test" Version="2.2500.1.0" />
                      <Applications>
                        <Application Id="App" Executable="WhatsApp.Root.exe" />
                        <Application Id="Helper" Executable="Sub\Helper.exe" />
                      </Applications>
                    </Package>
                    """);
                var mainExe = System.IO.Path.Combine(pkgDir, "WhatsApp.Root.exe");
                var helperExe = System.IO.Path.Combine(pkgDir, "Sub", "Helper.exe");
                Check(ApplicationIconProvider.TryResolvePackagedAppUserModelId(mainExe) == "5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App",
                    "TryResolvePackagedAppUserModelId must resolve the Store app AUMID from package folder + AppxManifest.xml.");
                Check(ApplicationIconProvider.TryResolvePackagedAppUserModelId(helperExe) == "5319275A.WhatsAppDesktop_cv1g1gvanyjgm!Helper",
                    "TryResolvePackagedAppUserModelId must match nested relative executables in multi-app manifests.");
                Check(ApplicationIconProvider.TryResolvePackagedAppUserModelId(@"C:\Program Files\App\app.exe") is null,
                    "Non-WindowsApps executables must not resolve a packaged AUMID.");
            }
            finally
            {
                if (System.IO.Directory.Exists(fakeRoot)) System.IO.Directory.Delete(fakeRoot, recursive: true);
            }
        }

        return checks;
    }

    private static BitmapSource CreateIcon()
    {
        var icon = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        icon.Freeze();
        return icon;
    }
}
