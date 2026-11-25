using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenTaskbar.Services;

/// <summary>Offline evidence assessment. Does not initialize WPF, upload data, or change raw evidence.</summary>
internal static class CrashAssessment
{
    private const int MaxFiles = 1024;
    private const int MaxFileBytes = 2 * 1024 * 1024;
    private const long MaxScanBytes = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private static readonly Regex ExceptionType = new(@"(?:^|--->\s*)([\w.]+(?:Exception|Error))(?=:|\s|$)", RegexOptions.CultureInvariant);
    private static readonly Regex Frame = new(@"^\s*(?:at|konum:)\s+(.+)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Returns Analysis/latest.md, or null when there is no actionable evidence or another analyzer holds the lock.
    /// Storage failures propagate to the caller; malformed/partial individual inputs are listed in scanWarnings.
    /// </summary>
    internal static string? Analyze(string crashDirectory)
    {
        var directory = Path.GetFullPath(crashDirectory);
        if (!Directory.Exists(directory)) return null;
        var lockId = Hash(directory.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant());
        using var gate = new Mutex(false, @"Local\OpenTaskbar.CrashAssessment." + lockId);
        bool acquired;
        try { acquired = gate.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) return null;
        try { return AnalyzeLocked(directory); }
        finally { gate.ReleaseMutex(); }
    }

    private static string? AnalyzeLocked(string directory)
    {
        var scan = new Scan(directory);
        var occurrences = new List<Occurrence>();
        foreach (var path in scan.Files("crash-*.txt"))
        {
            var text = scan.Read(path);
            if (text is null) continue;
            var occurrence = ParseReport(path, text, scan.Warnings);
            if (occurrence is not null) occurrences.Add(occurrence);
        }
        var exits = ReadExits(scan);
        var events = ReadEvents(scan);
        var samples = ReadSamples(scan);
        foreach (var occurrence in occurrences)
        {
            AddWindowsEvidence(occurrence, events);
            AddResourceEvidence(occurrence, samples);
        }
        foreach (var exit in exits)
        {
            // PID alone is not enough: Windows can reuse it. Match a close timestamp as well.
            var matching = occurrences.Where(o => o.Kind == "managed-report" && o.Pid == exit.Pid &&
                    Math.Abs((o.Utc - exit.Utc).TotalSeconds) <= 120)
                .OrderBy(o => Math.Abs((o.Utc - exit.Utc).TotalSeconds)).FirstOrDefault();
            if (matching is not null)
            {
                matching.Evidence.Add(new Evidence("native-exit", exit.File,
                    $"reason={exit.Reason}; exitCode={exit.ExitCode}; UTC={exit.Utc:O}"));
                if (exit.Reason == "heartbeat-timeout")
                    matching.Caveats.Add("Watchdog heartbeat zaman aşımı gözledi; kilitlenmenin nedeni tek başına bu kayıtla belirlenemez.");
                continue;
            }
            var orphan = new Occurrence
            {
                ReportId = exit.File, Kind = "native-exit", Utc = exit.Utc, Pid = exit.Pid,
                Category = "unexplained-process-exit", ExceptionTypes = [],
                Fingerprint = Hash("native-exit|" + exit.Reason + "|" + exit.ExitCode),
                Summary = exit.Reason == "heartbeat-timeout"
                    ? "Watchdog yanıt/heartbeat zaman aşımı kaydetti."
                    : "Yönetilen hata raporu olmadan süreç sonlanması kaydedildi.",
                Confidence = "low"
            };
            orphan.Evidence.Add(new Evidence("native-exit", exit.File,
                $"reason={exit.Reason}; exitCode={exit.ExitCode}; UTC={exit.Utc:O}"));
            orphan.Caveats.Add("End Task, harici sonlandırma veya uygulama hatası ayrıştırılamıyor; bu kayıt kanıtlanmış uygulama hatası değildir.");
            if (exit.ExitCode == "0x00000000")
                orphan.Caveats.Add("Çıkış kodu sıfır, ancak watchdog temiz çıkış sinyali görmedi; sıfır kodu tek başına temiz kapanışı kanıtlamaz.");
            orphan.Caveats.Add("Eşleşen yönetilen exception/stack raporu yok.");
            orphan.NextSteps.Add("Aynı zaman aralığındaki Windows Application/WER kayıtlarını ve varsa dump dosyasını inceleyin; kullanıcının süreci sonlandırıp sonlandırmadığını kontrol edin.");
            AddWindowsEvidence(orphan, events);
            AddResourceEvidence(orphan, samples);
            occurrences.Add(orphan);
        }
        if (occurrences.Count == 0 && scan.Warnings.Count == 0) return null;
        var ordered = occurrences.OrderBy(o => o.Utc).ThenBy(o => o.ReportId, StringComparer.Ordinal).ToList();
        var groups = ordered.GroupBy(o => o.Fingerprint).Select(g => MakeGroup(g.ToList()))
            .OrderByDescending(g => g.LastUtc).ThenBy(g => g.Fingerprint, StringComparer.Ordinal).ToList();
        var result = new Assessment
        {
            LatestReportId = ordered.LastOrDefault()?.ReportId,
            UpdatedUtc = DateTimeOffset.UtcNow,
            Groups = groups,
            ScanWarnings = scan.Warnings.Distinct(StringComparer.Ordinal).ToList()
        };
        var outputDirectory = Path.Combine(directory, "Analysis");
        Directory.CreateDirectory(outputDirectory);
        // Both outputs carry the same assessmentId so an interrupted pair replacement is detectable.
        result.AssessmentId = Hash(JsonSerializer.Serialize(new { result.LatestReportId, result.Groups, result.ScanWarnings }, JsonOptions));
        AtomicWrite(Path.Combine(outputDirectory, "latest.json"), JsonSerializer.Serialize(result, JsonOptions));
        var markdown = Path.Combine(outputDirectory, "latest.md");
        AtomicWrite(markdown, Render(result));
        return markdown;
    }

    private static Occurrence? ParseReport(string path, string text, List<string> warnings)
    {
        var file = Path.GetFileName(path);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string? Field(string name) => lines.FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.Ordinal))?[(name.Length + 1)..].Trim();
        var marker = Array.FindIndex(lines, l => l == "Exception (including inner exceptions):");
        if (!text.StartsWith("OpenTaskbar local crash report", StringComparison.Ordinal) ||
            !TryUtc(Field("UTC"), out var utc) || marker < 0)
        {
            warnings.Add(file + ": geçersiz/eksik rapor başlığı, UTC veya exception bölümü; atlandı.");
            return null;
        }
        var exceptionLines = lines.Skip(marker + 1).TakeWhile(l =>
            !l.StartsWith("Safe layout/widget context:", StringComparison.Ordinal) &&
            !l.StartsWith("Settings context unavailable:", StringComparison.Ordinal)).ToArray();
        var types = exceptionLines.Select(l => ExceptionType.Match(l.Trim())).Where(m => m.Success)
            .Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();
        var frames = exceptionLines.Select(l => Frame.Match(l)).Where(m => m.Success)
            .Select(m => NormalizeFrame(m.Groups[1].Value)).Where(f => f.Length > 0)
            .Distinct(StringComparer.Ordinal).Take(12).ToList();
        if (types.Count == 0 && frames.Count == 0 && !exceptionLines.Any(l => l.Contains("No managed exception was supplied.", StringComparison.Ordinal)))
        {
            warnings.Add(file + ": exception kimliği/stack eksik; kısmi rapor atlandı.");
            return null;
        }
        var occurrence = new Occurrence
        {
            ReportId = file, Kind = "managed-report", Utc = utc,
            Pid = int.TryParse(Field("PID"), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0 ? pid : null,
            Build = Field("Build") is { Length: > 0 } build ? build : "unknown",
            ExceptionTypes = types, Frames = frames
        };
        var exceptionText = string.Join('\n', exceptionLines);
        var oom = types.Any(t => t.EndsWith("OutOfMemoryException", StringComparison.Ordinal));
        var rendering = frames.Any(f => f.Contains("System.Windows.Media.Composition.DUCE", StringComparison.Ordinal) ||
            f.Contains("HwndTarget.UpdateWindowSettings", StringComparison.Ordinal) ||
            f.Contains("System.Windows.Media.MediaContext", StringComparison.Ordinal));
        if (oom && rendering)
        {
            occurrence.Category = "wpf-rendering-resource-exhaustion";
            occurrence.Summary = "OutOfMemoryException WPF render/pencere oluşturma yolunda gözlendi; kaynak tükenmesi belirtisi var.";
            occurrence.Confidence = "moderate";
            occurrence.Caveats.Add("OutOfMemoryException tek başına genel RAM yetersizliğini veya sızıntıyı başlatan metodu kanıtlamaz; GDI/USER, handle ve adres alanı kaynakları da araştırılmalıdır.");
            occurrence.NextSteps.Add("Kaynak örneklerinde GDI/USER/handle eğilimini inceleyin; pencere/bitmap/HICON sahipliği ve Dispose/DeleteObject/DestroyIcon eşleşmelerini kaynak izi veya dump ile doğrulayın.");
            occurrence.NextSteps.Add("WPF DUCE/HwndTarget stack'i hatanın görünür olduğu yoldur; sızıntının başladığı metot olarak kabul etmeyin.");
        }
        else if (oom || types.Any(t => t.EndsWith("InsufficientMemoryException", StringComparison.Ordinal)))
        {
            occurrence.Category = "memory-error";
            occurrence.Summary = "Bellek tahsisi hatası raporlandı; hangi kaynağın tükendiği henüz bilinmiyor.";
            occurrence.Confidence = "moderate";
            occurrence.NextSteps.Add("Private/managed byte ve handle eğilimlerini, süreç mimarisini ve aynı zamandaki dump/bellek tahsis stack'lerini karşılaştırın.");
            occurrence.Caveats.Add("Exception genel sistem RAM yetersizliği veya belirli bir sızıntının kanıtı değildir.");
        }
        else if (types.Any(t => t.EndsWith("NullReferenceException", StringComparison.Ordinal)) &&
                 frames.Any(f => f.Contains("MediaWidget", StringComparison.Ordinal) &&
                     (f.Contains("Refresh", StringComparison.OrdinalIgnoreCase) || f.Contains("Update", StringComparison.OrdinalIgnoreCase))))
        {
            occurrence.Category = "media-refresh-null";
            occurrence.Summary = "Medya yenileme/güncelleme yolunda NullReferenceException gözlendi.";
            occurrence.Confidence = "high";
            occurrence.NextSteps.Add("İlk medya stack frame'inde oturum değişimi/iptal ve null playback/timeline verisi yaşam döngüsünü inceleyin; hata anındaki build ile düzeltme build'ini karşılaştırın.");
            occurrence.Caveats.Add("Stack null olan nesnenin kimliğini tek başına göstermez.");
        }
        else if (types.Any(t => t.EndsWith("ArgumentException", StringComparison.Ordinal) || t.EndsWith("ArgumentOutOfRangeException", StringComparison.Ordinal)) &&
                 Regex.IsMatch(exceptionText, @"\bNaN\b", RegexOptions.CultureInvariant) &&
                 (exceptionText.Contains("MaxWidth", StringComparison.Ordinal) || exceptionText.Contains("MaxHeight", StringComparison.Ordinal)) &&
                 frames.Any(f => f.Contains("Dock", StringComparison.Ordinal) || f.Contains("MainWindow", StringComparison.Ordinal)))
        {
            occurrence.Category = "dock-sizing-nan";
            occurrence.Summary = "Dock boyutlandırma yolunda geçersiz NaN boyut değeri gözlendi.";
            occurrence.Confidence = "high";
            occurrence.NextSteps.Add("Auto/NaN ölçülerinin MaxWidth/MaxHeight hesaplarına taşınmasını ve kenar değiştirme anındaki ölçüm sırasını kontrol edin.");
        }
        else
        {
            occurrence.Category = "unknown-managed-failure";
            occurrence.Summary = "Yönetilen hata kaydedildi; bilinen yerel kurallarla kök neden belirlenemedi.";
            occurrence.Confidence = "low";
            occurrence.NextSteps.Add("İlk uygulama stack frame'ini ve aynı build'in kaynak kodunu inceleyin; gerekirse hata anı dump'ını yerel olarak toplayın.");
            occurrence.Caveats.Add("Bilinmeyen imza; otomatik kök neden iddiası yok.");
        }
        // Method identity, not message (which may include PIDs, paths, numbers or user data).
        // Retain only the top frames for stable grouping across source line/build changes.
        occurrence.Fingerprint = Hash(string.Join('|', types) + "|" + string.Join('|', frames.Take(8)) + "|" + occurrence.Category);
        occurrence.Evidence.Add(new Evidence("managed-report", file, "Exception: " + (types.Count > 0 ? string.Join(" -> ", types) : "unknown")));
        if (frames.Count > 0) occurrence.Evidence.Add(new Evidence("stack", file, string.Join(" -> ", frames)));
        else occurrence.Caveats.Add("Stack frame bilgisi yok; parmak izi ve neden ayrımı daha az özgül.");
        if (occurrence.Pid is null) occurrence.Caveats.Add("Raporda PID yok; PID tabanlı kaynak/çıkış korelasyonu yapılamadı.");
        return occurrence;
    }

    private static string NormalizeFrame(string frame)
    {
        var arguments = frame.IndexOf('(');
        if (arguments >= 0) return frame[..arguments].Trim();
        var source = frame.IndexOf(" in ", StringComparison.Ordinal);
        return (source >= 0 ? frame[..source] : frame).Trim();
    }

    private static List<ExitRecord> ReadExits(Scan scan)
    {
        var result = new List<ExitRecord>();
        foreach (var path in scan.Files("exit-*.json"))
        {
            var text = scan.Read(path);
            if (text is null) continue;
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                var reason = String(root, "reason");
                var code = ExitCode(root);
                if (!Pid(root, out var pid) || !TryUtc(String(root, "utc"), out var utc)) throw new FormatException();
                if (reason is "parent-unavailable" or "watchdog-error")
                {
                    scan.Warnings.Add(Path.GetFileName(path) + ": watchdog gözlem hatası; süreç çıkışı/çökmesi kanıtlanmadı.");
                    continue;
                }
                var explicitlyUnknownCode = root.TryGetProperty("exitCode", out var codeElement) && codeElement.ValueKind == JsonValueKind.Null;
                if (reason is not ("process-exited" or "heartbeat-timeout") || (code is null && !explicitlyUnknownCode))
                    throw new FormatException();
                result.Add(new ExitRecord(Path.GetFileName(path), pid, utc, code ?? "unknown", reason));
            }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or OverflowException)
            { scan.Warnings.Add(Path.GetFileName(path) + ": geçersiz/kısmi native çıkış kaydı; atlandı."); }
        }
        return result;
    }

    private static string? ExitCode(JsonElement root)
    {
        if (!root.TryGetProperty("exitCode", out var code)) return null;
        if (code.ValueKind == JsonValueKind.Number && code.TryGetInt64(out var number) && number >= int.MinValue && number <= uint.MaxValue)
            return "0x" + unchecked((uint)number).ToString("X8", CultureInfo.InvariantCulture);
        if (code.ValueKind != JsonValueKind.String) return null;
        var text = code.GetString() ?? "";
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            return "0x" + hex.ToString("X8", CultureInfo.InvariantCulture);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= int.MinValue && value <= uint.MaxValue
            ? "0x" + unchecked((uint)value).ToString("X8", CultureInfo.InvariantCulture) : null;
    }

    private static List<WindowsEvent> ReadEvents(Scan scan)
    {
        var result = new List<WindowsEvent>();
        var path = Path.Combine(scan.Directory, "windows-events.json");
        if (!File.Exists(path)) return result;
        var text = scan.Read(path);
        if (text is null) return result;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                scan.Warnings.Add("windows-events.json: Windows olay toplayıcısı hata bildirdi; olay kapsamı eksik olabilir.");
            if (!root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array) throw new FormatException();
            var count = 0;
            foreach (var item in events.EnumerateArray())
            {
                if (++count > 4096) { scan.Warnings.Add("windows-events.json: 4096 olay sınırı; kalanlar atlandı."); break; }
                if (item.ValueKind != JsonValueKind.Object || !TryUtc(String(item, "utc"), out var utc) || String(item, "application") is not { Length: > 0 } app ||
                    !item.TryGetProperty("eventId", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var eventId))
                { scan.Warnings.Add("windows-events.json: eksik/geçersiz olay atlandı."); continue; }
                int? pid = null;
                if (item.TryGetProperty("pid", out var pidElement) && pidElement.ValueKind != JsonValueKind.Null)
                {
                    if (!Pid(item, out var validPid)) { scan.Warnings.Add("windows-events.json: geçersiz PID içeren olay atlandı."); continue; }
                    pid = validPid;
                }
                result.Add(new WindowsEvent(utc, eventId, String(item, "provider") ?? "unknown", String(item, "eventName") ?? "unknown", app, pid));
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        { scan.Warnings.Add("windows-events.json: geçersiz/kısmi olay dosyası; olaylar eksik olabilir."); }
        return result;
    }

    private static List<ResourceSample> ReadSamples(Scan scan)
    {
        var result = new List<ResourceSample>();
        foreach (var path in scan.Files("resources-*.jsonl"))
        {
            var text = scan.Read(path);
            if (text is null) continue;
            var count = 0;
            var bad = false;
            using var reader = new StringReader(text);
            while (reader.ReadLine() is { } line)
            {
                if (++count > 2048) { scan.Warnings.Add(Path.GetFileName(path) + ": 2048 örnek sınırı; kalanlar atlandı."); break; }
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var item = document.RootElement;
                    if (!Pid(item, out var pid) || !TryUtc(String(item, "utc"), out var utc)) throw new FormatException();
                    var gdi = Metric(item, "gdiObjects");
                    var user = Metric(item, "userObjects");
                    var handles = Metric(item, "handles");
                    var privateBytes = Metric(item, "privateBytes");
                    var workingSet = Metric(item, "workingSetBytes");
                    var managed = Metric(item, "managedBytes");
                    var gdiPeak = Metric(item, "gdiPeakObjects");
                    var userPeak = Metric(item, "userPeakObjects");
                    if (gdi is null && user is null && handles is null && privateBytes is null && workingSet is null && managed is null && gdiPeak is null && userPeak is null)
                        throw new FormatException();
                    if (String(item, "error") is not null)
                        scan.Warnings.Add(Path.GetFileName(path) + ": kaynak toplayıcısı bazı sayaçları okuyamadı.");
                    result.Add(new ResourceSample(Path.GetFileName(path), pid, utc, gdi, user, handles, privateBytes, workingSet, managed, gdiPeak, userPeak, String(item, "build")));
                }
                catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
                { bad = true; }
            }
            if (bad) scan.Warnings.Add(Path.GetFileName(path) + ": geçersiz/kısmi kaynak örnekleri atlandı.");
        }
        return result;
    }

    private static void AddWindowsEvidence(Occurrence occurrence, List<WindowsEvent> events)
    {
        var matches = events.Where(e => IsApplication(e.Application) && Math.Abs((e.Utc - occurrence.Utc).TotalMinutes) <= 5 &&
            (e.Pid is null || occurrence.Pid == e.Pid)).ToList();
        foreach (var e in matches)
            occurrence.Evidence.Add(new Evidence("windows-event", "windows-events.json",
                $"UTC={e.Utc:O}; eventId={e.EventId}; provider={e.Provider}; eventName={e.EventName}; pid={e.Pid?.ToString(CultureInfo.InvariantCulture) ?? "missing"}"));
        if (matches.Count == 0)
            occurrence.Caveats.Add("±5 dakika içinde uygulama/zaman/PID koşullarına uyan Windows/WER olayı yok veya kayıtlar erişilemedi.");
        if (matches.Any(e => e.Pid is null))
            occurrence.Caveats.Add("Eşleşen Windows olayında PID yok: yalnızca uygulama adı ve ±5 dakika zaman yakınlığı ile ilişkilendirildi; aynı süreç olduğu kesin değil.");
        var gdi = matches.Any(e => e.EventId == 1001 &&
            e.Provider.Contains("Windows Error Reporting", StringComparison.OrdinalIgnoreCase) &&
            e.EventName.Equals("GDIObjectLeak", StringComparison.OrdinalIgnoreCase));
        if (gdi && occurrence.Category == "wpf-rendering-resource-exhaustion")
        {
            var sameProcess = matches.Any(e => e.EventId == 1001 &&
                e.Provider.Contains("Windows Error Reporting", StringComparison.OrdinalIgnoreCase) &&
                e.EventName.Equals("GDIObjectLeak", StringComparison.OrdinalIgnoreCase) &&
                e.Pid is not null && e.Pid == occurrence.Pid);
            occurrence.Confidence = sameProcess ? "high" : "moderate";
            occurrence.Summary = sameProcess
                ? "WPF DUCE/HwndTarget OutOfMemoryException ve aynı süreçte WER GDIObjectLeak, grafik/GDI kaynak tükenmesine güçlü kanıt sağlıyor."
                : "WPF render OutOfMemoryException yakınında GDIObjectLeak var; PID eşleşmesi olmadan aynı süreç olduğu doğrulanamadı.";
            occurrence.Caveats.Add("Bu kanıtlar sızıntıyı başlatan metot/nesneyi veya genel RAM yetersizliğini kanıtlamaz.");
        }
    }

    private static bool IsApplication(string application)
    {
        var name = application.Replace('\\', '/').Split('/').Last();
        return name.Equals("OpenTaskbar.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("OpenTaskbar", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddResourceEvidence(Occurrence occurrence, List<ResourceSample> samples)
    {
        var matching = samples.Where(s => s.Pid == occurrence.Pid && s.Utc <= occurrence.Utc.AddSeconds(30) && s.Utc >= occurrence.Utc.AddMinutes(-10))
            .OrderBy(s => s.Utc).ThenBy(s => s.File, StringComparer.Ordinal).ToList();
        if (matching.Count == 0)
        {
            occurrence.Caveats.Add("Aynı PID için hata çevresinde (-10 dakika/+30 saniye) kaynak örneği yok; GDI/USER/handle veya bellek eğilimi ölçülemedi.");
            return;
        }
        var first = matching[0];
        var last = matching[^1];
        if (occurrence.Build == "unknown")
            occurrence.Build = matching.LastOrDefault(sample => !string.IsNullOrWhiteSpace(sample.Build))?.Build ?? "unknown";
        occurrence.Resources = new ResourceWindow
        {
            SampleCount = matching.Count, FirstUtc = first.Utc, LastUtc = last.Utc,
            First = first, Last = last,
            PeakGdiObjects = matching.Max(s => s.GdiObjects), PeakUserObjects = matching.Max(s => s.UserObjects),
            ProcessPeakGdiObjects = matching.Max(s => s.GdiPeakObjects), ProcessPeakUserObjects = matching.Max(s => s.UserPeakObjects),
            PeakHandles = matching.Max(s => s.Handles), PeakPrivateBytes = matching.Max(s => s.PrivateBytes),
            PeakWorkingSetBytes = matching.Max(s => s.WorkingSetBytes), PeakManagedBytes = matching.Max(s => s.ManagedBytes)
        };
        foreach (var file in matching.Select(s => s.File).Distinct(StringComparer.Ordinal))
            occurrence.Evidence.Add(new Evidence("resource-samples", file,
                $"samples={matching.Count}; UTC={first.Utc:O}..{last.Utc:O}; GDI={first.GdiObjects?.ToString() ?? "missing"}->{last.GdiObjects?.ToString() ?? "missing"}; peakGDI={occurrence.Resources.PeakGdiObjects?.ToString() ?? "missing"}"));
        if (matching.Count < 2 || first.Utc == last.Utc)
            occurrence.Caveats.Add("Tek zamanlı kaynak örneği büyüme/sızıntı eğilimini göstermez.");
        occurrence.Caveats.Add("Kaynak sayaçları gözlemdir; artış tek başına sızıntının kaynağını veya nedenini kanıtlamaz.");
        if (occurrence.Resources.ProcessPeakGdiObjects is not null || occurrence.Resources.ProcessPeakUserObjects is not null)
            occurrence.Caveats.Add("ProcessPeak sayaçları süreç ömrü zirvesidir; yalnızca bu zaman penceresinde oluştuğunu kanıtlamaz.");
        if (matching.Any(s => s.GdiObjects is null || s.UserObjects is null || s.Handles is null || s.PrivateBytes is null || s.WorkingSetBytes is null || s.ManagedBytes is null))
            occurrence.Caveats.Add("Bazı kaynak sayaçları kullanılamıyor; null değerler sıfır olarak yorumlanmadı.");
    }

    private static Group MakeGroup(List<Occurrence> occurrences)
    {
        var latest = occurrences[^1];
        var strongest = occurrences.OrderByDescending(o => o.Confidence == "high" ? 2 : o.Confidence == "moderate" ? 1 : 0).First();
        return new Group
        {
            Fingerprint = latest.Fingerprint, Count = occurrences.Count,
            Category = latest.Category, ExceptionTypes = latest.ExceptionTypes,
            Confidence = strongest.Confidence, Summary = strongest.Summary,
            FirstUtc = occurrences[0].Utc, LastUtc = latest.Utc,
            Builds = occurrences.GroupBy(o => o.Build).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new BuildCount(g.Key, g.Count())).ToList(),
            Evidence = occurrences.SelectMany(o => o.Evidence).Distinct().ToList(),
            Caveats = occurrences.SelectMany(o => o.Caveats).Distinct(StringComparer.Ordinal).ToList(),
            NextSteps = occurrences.SelectMany(o => o.NextSteps).Distinct(StringComparer.Ordinal).ToList(),
            Occurrences = occurrences
        };
    }

    private static string Render(Assessment assessment)
    {
        var text = new StringBuilder("# Yerel çökme değerlendirmesi\n\n");
        text.AppendLine($"Güncelleme (UTC): {assessment.UpdatedUtc:O}  ")
            .AppendLine($"Son kayıt: `{assessment.LatestReportId ?? "yok"}`  ")
            .AppendLine($"Değerlendirme kimliği: `{assessment.AssessmentId}`\n")
            .AppendLine("Çevrimdışı, kural tabanlı değerlendirme; yükleme, otomatik onarım veya yeniden başlatma yapılmaz. Güven düzeyi istatistiksel olasılık değil, kanıt desteğidir. Ham kanıtlar korunur. Exception/stack yerel yollar içerebilir; paylaşmadan önce gözden geçirin.\n");
        foreach (var group in assessment.Groups)
        {
            text.AppendLine($"## {group.Category} — {group.Count} kayıt")
                .AppendLine($"{group.Summary}  ")
                .AppendLine($"Kanıt güveni: {(group.Confidence == "high" ? "yüksek" : group.Confidence == "moderate" ? "orta" : "düşük")}; parmak izi: `{group.Fingerprint}`  ")
                .AppendLine($"İlk/son UTC: {group.FirstUtc:O} / {group.LastUtc:O}  ")
                .AppendLine("Build sayımları: " + string.Join(", ", group.Builds.Select(b => $"{Safe(b.Build)}: {b.Count}")))
                .AppendLine("\nKanıt:");
            foreach (var evidence in group.Evidence.Take(8))
                text.AppendLine($"- `{Safe(evidence.File)}`: {Safe(evidence.Detail)}");
            if (group.Evidence.Count > 8) text.AppendLine("- Diğer kanıtlar ve her olayın ayrıntıları latest.json içinde.");
            text.AppendLine("\nSınırlar / eksik veri:");
            foreach (var caveat in group.Caveats) text.AppendLine("- " + caveat);
            text.AppendLine("\nSonraki tanı adımları:");
            foreach (var step in group.NextSteps) text.AppendLine("- " + step);
            text.AppendLine();
        }
        if (assessment.ScanWarnings.Count > 0)
        {
            text.AppendLine("## Tarama uyarıları");
            foreach (var warning in assessment.ScanWarnings) text.AppendLine("- " + Safe(warning));
        }
        return text.ToString();
    }

    private static string Safe(string value) => value.Replace('`', '\'').Replace('\r', ' ').Replace('\n', ' ').Replace('<', '[').Replace('>', ']');
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool TryUtc(string? value, out DateTimeOffset utc) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);
    private static string? String(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Pid(JsonElement item, out int pid)
    {
        pid = 0;
        return item.ValueKind == JsonValueKind.Object && item.TryGetProperty("pid", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out pid) && pid > 0;
    }
    private static long? Metric(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var metric) && metric >= 0 ? metric : null;

    private static void AtomicWrite(string path, string content)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class Scan(string directory)
    {
        internal string Directory { get; } = directory;
        internal List<string> Warnings { get; } = [];
        private long bytes;
        internal IEnumerable<string> Files(string pattern)
        {
            // Keep the newest evidence even after years of reports; do not let directory enumeration order
            // permanently hide new crashes behind the file cap. The queue bounds retained metadata too.
            var newest = new PriorityQueue<string, (long Ticks, string Path)>(
                Comparer<(long Ticks, string Path)>.Create((a, b) =>
                    a.Ticks != b.Ticks ? a.Ticks.CompareTo(b.Ticks) : StringComparer.Ordinal.Compare(a.Path, b.Path)));
            var truncated = false;
            foreach (var path in System.IO.Directory.EnumerateFiles(Directory, pattern, SearchOption.TopDirectoryOnly))
            {
                try
                {
                    newest.Enqueue(path, (File.GetLastWriteTimeUtc(path).Ticks, path));
                    if (newest.Count > MaxFiles)
                    {
                        newest.Dequeue();
                        truncated = true;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Warnings.Add(Path.GetFileName(path) + ": dosya zamanı okunamadı (" + ex.GetType().Name + ").");
                }
            }
            if (truncated) Warnings.Add(pattern + ": en yeni 1024 dosya değerlendirildi; tarama tam geçmişi kapsamıyor.");
            return newest.UnorderedItems.OrderByDescending(item => item.Priority.Ticks)
                .ThenBy(item => item.Element, StringComparer.Ordinal).Select(item => item.Element);
        }
        internal string? Read(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > MaxFileBytes || bytes + stream.Length > MaxScanBytes)
                { Warnings.Add(Path.GetFileName(path) + ": dosya/64 MiB toplam tarama sınırı aşıldı; atlandı."); return null; }
                // Bound the read even if another process is still appending to the file.
                var length = checked((int)stream.Length);
                bytes += length;
                var buffer = new byte[length];
                var read = 0;
                while (read < length)
                {
                    var next = stream.Read(buffer, read, length - read);
                    if (next == 0) break;
                    read += next;
                }
                return Encoding.UTF8.GetString(buffer, 0, read);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Warnings.Add(Path.GetFileName(path) + ": okunamadı (" + ex.GetType().Name + ")."); return null; }
        }
    }

    private sealed class Assessment
    {
        public int SchemaVersion { get; set; } = 1;
        public string AssessmentId { get; set; } = "";
        public string? LatestReportId { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
        public List<Group> Groups { get; set; } = [];
        public List<string> ScanWarnings { get; set; } = [];
    }
    private sealed class Group
    {
        public string Fingerprint { get; set; } = "";
        public int Count { get; set; }
        public string Category { get; set; } = "";
        public List<string> ExceptionTypes { get; set; } = [];
        public string Confidence { get; set; } = "";
        public string Summary { get; set; } = "";
        public DateTimeOffset FirstUtc { get; set; }
        public DateTimeOffset LastUtc { get; set; }
        public List<BuildCount> Builds { get; set; } = [];
        public List<Evidence> Evidence { get; set; } = [];
        public List<string> Caveats { get; set; } = [];
        public List<string> NextSteps { get; set; } = [];
        public List<Occurrence> Occurrences { get; set; } = [];
    }
    private sealed class Occurrence
    {
        public string ReportId { get; set; } = "";
        public string Kind { get; set; } = "";
        public DateTimeOffset Utc { get; set; }
        public int? Pid { get; set; }
        public string Build { get; set; } = "unknown";
        public string Fingerprint { get; set; } = "";
        public string Category { get; set; } = "";
        public string Confidence { get; set; } = "";
        public string Summary { get; set; } = "";
        public List<string> ExceptionTypes { get; set; } = [];
        public List<string> Frames { get; set; } = [];
        public List<Evidence> Evidence { get; set; } = [];
        public List<string> Caveats { get; set; } = [];
        public List<string> NextSteps { get; set; } = [];
        public ResourceWindow? Resources { get; set; }
    }
    private sealed class ResourceWindow
    {
        public int SampleCount { get; set; }
        public DateTimeOffset FirstUtc { get; set; }
        public DateTimeOffset LastUtc { get; set; }
        public ResourceSample? First { get; set; }
        public ResourceSample? Last { get; set; }
        public long? PeakGdiObjects { get; set; }
        public long? PeakUserObjects { get; set; }
        public long? ProcessPeakGdiObjects { get; set; }
        public long? ProcessPeakUserObjects { get; set; }
        public long? PeakHandles { get; set; }
        public long? PeakPrivateBytes { get; set; }
        public long? PeakWorkingSetBytes { get; set; }
        public long? PeakManagedBytes { get; set; }
    }
    private sealed record Evidence(string Kind, string File, string Detail);
    private sealed record BuildCount(string Build, int Count);
    private sealed record ExitRecord(string File, int Pid, DateTimeOffset Utc, string ExitCode, string Reason);
    private sealed record WindowsEvent(DateTimeOffset Utc, int EventId, string Provider, string EventName, string Application, int? Pid);
    private sealed record ResourceSample(string File, int Pid, DateTimeOffset Utc, long? GdiObjects, long? UserObjects, long? Handles, long? PrivateBytes, long? WorkingSetBytes, long? ManagedBytes, long? GdiPeakObjects, long? UserPeakObjects, string? Build);
}
