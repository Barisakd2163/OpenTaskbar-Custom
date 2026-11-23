using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace OpenTaskbar.Services;

/// <summary>Looks up the latest GitHub release and applies in-place updates.</summary>
public static class UpdateChecker
{
    /// <summary>The repository whose releases are checked (the project's origin remote).</summary>
    public const string Repository = "alztrk/opentaskbar";

    public static string ReleasesPage => $"https://github.com/{Repository}/releases/latest";

    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    /// <summary>False when installed from the Microsoft Store (MSIX), where Store manages updates per Policy 10.2.5.</summary>
    public static bool SupportsInAppUpdates => !AppIdentity.IsPackaged;

    /// <summary>Architecture token used by release assets (x64/x86); empty for other runtimes.</summary>
    public static string CurrentArchitecture => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X86 => "x86",
        Architecture.X64 => "x64",
        _ => string.Empty
    };

    public sealed record Release(Version Version, string Tag, string Url, string? DownloadUrl = null, long? Size = null, string? Sha256 = null);

    private const long MaxDownloadBytes = 350L * 1024 * 1024;
    private static readonly HashSet<string> AllowedDownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "github-releases.githubusercontent.com"
    };

    private static readonly HttpClient ApiClient = CreateApiClient();
    private static readonly HttpClient DownloadClient = CreateDownloadClient();

    internal static bool IsAllowedDownloadUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && AllowedDownloadHosts.Contains(uri.Host);
    }

    private static HttpClient CreateApiClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OpenTaskbar", CurrentVersion.ToString(3)));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static HttpClient CreateDownloadClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OpenTaskbar", CurrentVersion.ToString(3)));
        return client;
    }

    /// <summary>The newer release (null when up to date); <c>Checked</c> is false when GitHub could not be reached.</summary>
    public static async Task<(Release? Release, bool Checked)> FindNewerAsync(CancellationToken cancellation)
    {
        if (!SupportsInAppUpdates) return (null, true);
        try
        {
            using var response = await ApiClient.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancellation);
            if (!response.IsSuccessStatusCode)
            {
                AppLog.Info($"Update check: GitHub returned {(int)response.StatusCode}.");
                return (null, false);
            }

            var json = await response.Content.ReadAsStringAsync(cancellation);
            return (ParseRelease(json, CurrentVersion, CurrentArchitecture), true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException
            or TimeoutException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            AppLog.Info($"Update check failed: {ex.Message}");
            return (null, false);
        }
    }

    /// <summary>
    /// Parses release json and returns a newer Release if available, or null. <paramref name="architecture"/> is
    /// "x64"/"x86" for portable builds so the matching EXE is selected; releases before the dual-architecture
    /// pipeline only have a single "OpenTaskbar.exe" asset, which stays supported.
    /// </summary>
    public static Release? ParseRelease(string json, Version currentVersion, string? architecture = null)
    {
        using var document = JsonDocument.Parse(json);
        var tag = document.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        var url = document.RootElement.TryGetProperty("html_url", out var html) ? html.GetString() ?? ReleasesPage : ReleasesPage;
        if (!Version.TryParse(tag.TrimStart('v', 'V').Split('-', '+')[0], out var version)) return null;

        string? downloadUrl = null;
        long? size = null;
        string? sha256 = null;

        if (document.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            var selected = SelectAsset(assets, architecture);
            if (selected is { } asset)
            {
                downloadUrl = asset.TryGetProperty("browser_download_url", out var bdu) ? bdu.GetString() : null;
                size = asset.TryGetProperty("size", out var sz) ? sz.GetInt64() : null;
                sha256 = asset.TryGetProperty("digest", out var dg) ? dg.GetString() : null;
            }
        }

        return Normalize(version) > Normalize(currentVersion) ? new Release(version, tag, url, downloadUrl, size, sha256) : null;
    }

    /// <summary>
    /// Picks one EXE asset: architecture-specific first (e.g. OpenTaskbar-v0.3.3-win-x64.exe), then the legacy
    /// single "OpenTaskbar.exe" for old releases, and only then any EXE as a last resort. Never gives an x86
    /// machine the x64 binary just because it appears first in the GitHub asset array.
    /// </summary>
    private static JsonElement? SelectAsset(JsonElement assets, string? architecture)
    {
        JsonElement? firstExe = null;
        JsonElement? legacy = null;
        JsonElement? matching = null;
        var suffix = string.IsNullOrEmpty(architecture) ? null : $"-{architecture}.exe";
        var marker = string.IsNullOrEmpty(architecture) ? null : $"-win-{architecture}.";

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrEmpty(name)) continue;

            if (string.Equals(name, "OpenTaskbar.exe", StringComparison.OrdinalIgnoreCase))
                legacy ??= asset;

            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            firstExe ??= asset;

            if (matching is null && suffix is not null
                && (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                    || name.Contains(marker!, StringComparison.OrdinalIgnoreCase)))
            {
                matching = asset;
            }
        }

        return matching ?? legacy ?? firstExe;
    }

    /// <summary>Finds the currently running executable path on disk.</summary>
    public static string? GetExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath) && File.Exists(processPath))
            return Path.GetFullPath(processPath);

        var modulePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(modulePath) && File.Exists(modulePath))
            return Path.GetFullPath(modulePath);

        return null;
    }

    /// <summary>
    /// Downloads the updated binary from the release asset, validates it, and executes the replacement script before shutdown.
    /// </summary>
    public static async Task<(bool Success, string? ErrorKey, int? HttpStatusCode)> DownloadAndApplyAsync(
        Release release,
        CancellationToken cancellation = default)
    {
        if (!SupportsInAppUpdates)
        {
            return (false, "UpdateFailed", null);
        }

        var targetPath = GetExecutablePath();
        if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath))
        {
            return (false, "ErrorUpdatePath", null);
        }

        if (string.Equals(Path.GetFileName(targetPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "ErrorUpdateDeveloperMode", null);
        }

        if (string.IsNullOrEmpty(release.DownloadUrl))
        {
            ShellShortcuts.OpenUri(release.Url);
            return (false, "ErrorUpdateBinaryMissing", null);
        }

        if (!IsAllowedDownloadUrl(release.DownloadUrl))
        {
            return (false, "ErrorUpdateExecutableInvalid", null);
        }

        // GitHub release asset digests are the minimum integrity requirement for unattended replacement.
        // Older releases without a digest can still be installed manually from the release page.
        if (!IsValidSha256Digest(release.Sha256))
        {
            return (false, "ErrorUpdateExecutableInvalid", null);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var baseDir = string.IsNullOrWhiteSpace(localAppData) ? Path.GetTempPath() : localAppData;
        var tempDir = Path.Combine(baseDir, "OpenTaskbar", "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var tempExe = Path.Combine(tempDir, "OpenTaskbar.exe");
        var partFile = tempExe + ".part";

        try
        {
            if (File.Exists(partFile)) File.Delete(partFile);

            using var response = await DownloadClient.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                AppLog.Warning($"Update server returned HTTP {statusCode}.");
                return (false, "ErrorUpdateHttp", statusCode);
            }

            if (!IsAllowedDownloadUrl(response.RequestMessage?.RequestUri?.AbsoluteUri))
            {
                AppLog.Warning("Update redirect targeted a disallowed host.");
                return (false, "ErrorUpdateExecutableInvalid", null);
            }

            if (response.Content.Headers.ContentLength is > MaxDownloadBytes)
            {
                return (false, "ErrorUpdateSizeInvalid", null);
            }

            await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellation))
            await using (var fileStream = new FileStream(partFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long totalWritten = 0;
                while (true)
                {
                    var read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellation);
                    if (read == 0) break;
                    totalWritten += read;
                    if (totalWritten > MaxDownloadBytes)
                    {
                        TryDelete(partFile);
                        return (false, "ErrorUpdateSizeInvalid", null);
                    }
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellation);
                }
            }

            var fileInfo = new FileInfo(partFile);
            if (fileInfo.Length < 1_000_000)
            {
                TryDelete(partFile);
                return (false, "ErrorUpdateSizeInvalid", null);
            }

            if (release.Size.HasValue && release.Size.Value > 0 && fileInfo.Length != release.Size.Value)
            {
                TryDelete(partFile);
                return (false, "ErrorUpdateSizeMismatch", null);
            }

            byte[] header = new byte[2];
            using (var fs = File.OpenRead(partFile))
            {
                _ = fs.Read(header, 0, 2);
            }
            if (header[0] != 0x4D || header[1] != 0x5A) // 'M', 'Z'
            {
                TryDelete(partFile);
                return (false, "ErrorUpdateExecutableInvalid", null);
            }

            await using (var fileStream = File.OpenRead(partFile))
            {
                var actualHash = await SHA256.HashDataAsync(fileStream, cancellation);
                var expectedHash = Convert.FromHexString(release.Sha256![7..]);
                if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                {
                    TryDelete(partFile);
                    return (false, "ErrorUpdateExecutableInvalid", null);
                }
            }

            if (File.Exists(tempExe)) File.Delete(tempExe);
            File.Move(partFile, tempExe);

            AppLog.Info($"Downloaded update to {tempExe}. Applying to {targetPath}...");
            try
            {
                LaunchUpdaterAndRestart(tempExe, targetPath);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or UnauthorizedAccessException or IOException)
            {
                AppLog.Error($"Update launch failed: {ex.Message}");
                return (false, "ErrorUpdateFailed", null);
            }
            return (true, null, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            TryDelete(partFile);
            AppLog.Error($"Update download/apply error: {ex.Message}");
            return (false, "ErrorUpdateFailed", null);
        }
    }

    internal static bool IsValidSha256Digest(string? digest) =>
        digest is { Length: 71 } && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
        && digest[7..].All(Uri.IsHexDigit);

    // Inside set "VAR=..." only a quote, percent/delayed-expansion marker or line break can break quoting.
    // Rejecting &|^<> as well would make self-update fail on legal Windows paths such as "Tom & Jerry".
    private static readonly char[] UnsafeBatchChars = ['"', '%', '!', '\r', '\n'];

    private static void EnsureSafeBatchPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(UnsafeBatchChars) >= 0)
            throw new InvalidOperationException("Unsafe path for updater script.");
    }

    internal static string GenerateUpdaterScript(string tempExe, string targetExe)
    {
        EnsureSafeBatchPath(tempExe);
        EnsureSafeBatchPath(targetExe);
        return $"""
        @echo off
        set "SRC={tempExe}"
        set "DST={targetExe}"
        set ATTEMPTS=0
        :wait_loop
        timeout /t 1 /nobreak >nul
        copy /y "%SRC%" "%DST%" >nul 2>&1
        if not errorlevel 1 goto launch
        set /a ATTEMPTS+=1
        if %ATTEMPTS% lss 20 goto wait_loop
        goto cleanup

        :launch
        del /f /q "%SRC%" >nul 2>&1
        start "" "%DST%"

        :cleanup
        del /f /q "%~f0" >nul 2>&1
        """;
    }

    internal static void LaunchUpdaterAndRestart(string tempExe, string targetPath)
    {
        EnsureSafeBatchPath(tempExe);
        EnsureSafeBatchPath(targetPath);
        var targetDir = Path.GetDirectoryName(targetPath) ?? AppContext.BaseDirectory;
        var scriptPath = Path.Combine(Path.GetDirectoryName(tempExe)!, "apply-update.cmd");
        EnsureSafeBatchPath(scriptPath);
        var script = GenerateUpdaterScript(tempExe, targetPath);
        File.WriteAllText(scriptPath, script);

        var requiresElevation = !IsDirectoryWritable(targetDir);
        var cmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = cmdPath,
            UseShellExecute = requiresElevation,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = targetDir
        };

        if (requiresElevation)
        {
            startInfo.Verb = "runas";
            startInfo.Arguments = $"/c \"\"{scriptPath}\"\"";
        }
        else
        {
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(scriptPath);
        }

        using var updater = Process.Start(startInfo);
        AppLog.Info($"Updater started: pid={updater?.Id}, elevation={requiresElevation}");

        if (Application.Current is { } app)
        {
            app.Dispatcher.BeginInvoke(() => app.Shutdown(0));
        }
        else
        {
            Environment.Exit(0);
        }
    }

    private static bool IsDirectoryWritable(string dirPath)
    {
        try
        {
            var testFile = Path.Combine(dirPath, $"_update_test_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
}
