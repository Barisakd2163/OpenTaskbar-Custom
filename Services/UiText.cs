using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

public sealed class UiText : INotifyPropertyChanged
{
    private static readonly IReadOnlyDictionary<string, (string Turkish, string English)> Entries =
        new Dictionary<string, (string, string)>
        {
            ["Settings"] = ("Ayarlar", "Settings"),
            ["SettingsTitle"] = ("OpenTaskbar ayarları", "OpenTaskbar settings"),
            ["General"] = ("Genel", "General"),
            ["Appearance"] = ("Görünüm", "Appearance"),
            ["Widgets"] = ("Widget’ler", "Widgets"),
            ["WidgetsDescription"] = ("İstediğiniz widget’ı kurup dock’ta gösterin. Kurulu widget’ları anahtarla gizleyebilir, değişiklikleri Uygula ile kaydedebilirsiniz.", "Install the widgets you want on the dock. Hide installed widgets with the switch, then save changes with Apply."),
            ["WidgetInstall"] = ("Kur", "Install"),
            ["WidgetRemove"] = ("Kaldır", "Remove"),
            ["WidgetSearch"] = ("Widget ara", "Search widgets"),
            ["WidgetNoResults"] = ("Aramanızla eşleşen widget yok.", "No widgets match your search."),
            ["WidgetAvailable"] = ("Kurulmadı", "Not installed"),
            ["WidgetInstalled"] = ("Kurulu", "Installed"),
            ["WidgetShow"] = ("Dock’ta göster", "Show on dock"),
            ["WidgetStopwatch"] = ("Kronometre", "Stopwatch"),
            ["WidgetStopwatchDescription"] = ("Başlatın, duraklatın veya sıfırlayın. Süre uygulama kapandığında sıfırlanır.", "Start, pause, or reset. The elapsed time resets when the app closes."),
            ["TimedWidgetStart"] = ("Başlat", "Start"),
            ["TimedWidgetPause"] = ("Duraklat", "Pause"),
            ["TimedWidgetReset"] = ("Sıfırla", "Reset"),
            ["WidgetCountdown"] = ("Zamanlayıcı", "Timer"),
            ["WidgetCountdownDescription"] = ("Dakika olarak süre girin; geri sayımı başlatın, duraklatın veya sıfırlayın.", "Enter a duration in minutes; start, pause, or reset the countdown."),
            ["CountdownChooseDuration"] = ("Dakika girin", "Enter minutes"),
            ["CountdownDurationError"] = ("{0}–{1} dakika girin", "Enter {0}–{1} minutes"),
            ["CountdownFinished"] = ("Süre doldu", "Time is up"),
            ["CountdownMinutes"] = ("Süre (dakika)", "Duration (minutes)"),
            ["MinuteUnit"] = ("dk", "min"),
            ["WidgetInputFocusError"] = ("Süre alanı klavye odağı alamadı. Alana yeniden tıklayın.", "The duration field could not receive keyboard focus. Click it again."),
            ["WidgetClockDescription"] = ("Yerel saati 24 saat biçiminde gösterir.", "Local time in 24-hour format."),
            ["WidgetDateDescription"] = ("Haftanın günü ve bugünün tarihi.", "The weekday and today's date."),
            ["WidgetBatteryDescription"] = ("Windows’un bildirdiği pil seviyesi ve şarj durumu.", "Battery level and charging state reported by Windows."),
            ["WidgetMemory"] = ("RAM", "Memory"),
            ["WidgetMemoryDescription"] = ("Kullanılan fiziksel belleğin yüzdesi.", "The percentage of physical memory in use."),
            ["WidgetStorage"] = ("Sistem diski", "System drive"),
            ["WidgetStorageDescription"] = ("Windows’un kurulu olduğu diskte kullanılabilir alan.", "Available space on the drive containing Windows."),
            ["StorageFree"] = ("{0} GiB boş", "{0} GiB free"),
            ["WidgetDayProgress"] = ("Günün ilerlemesi", "Day progress"),
            ["WidgetDayProgressDescription"] = ("Yerel saate göre günün tamamlanan yüzdesi.", "The elapsed percentage of the day in local time."),
            ["WidgetClock"] = ("Saat", "Clock"),
            ["WidgetDate"] = ("Tarih", "Date"),
            ["WidgetBattery"] = ("Pil", "Battery"),
            ["Charging"] = ("Şarj oluyor", "Charging"),
            ["SystemDataUnavailable"] = ("Kullanılamıyor", "Unavailable"),
            ["ResizeDockFromLeft"] = ("Dock’u sol kenarından yeniden boyutlandır", "Resize dock from left edge"),
            ["ResizeDockFromRight"] = ("Dock’u sağ kenarından yeniden boyutlandır", "Resize dock from right edge"),
            ["Startup"] = ("Windows ile başlat", "Start with Windows"),
            ["StartupDescription"] = ("Oturum açtığında OpenTaskbar otomatik başlasın.", "Launch OpenTaskbar when you sign in."),
            ["Animations"] = ("Animasyonlar", "Animations"),
            ["AnimationsDescription"] = ("Simge büyütme, açılış ve geçiş hareketleri.", "Icon magnification, launch, and transition motion."),
            ["Previews"] = ("Pencere önizlemeleri", "Window previews"),
            ["PreviewsDescription"] = ("Simgenin üzerine geldiğinde açık pencereleri göster.", "Show open windows when you hover over an icon."),
            ["Badges"] = ("Bildirim göstergeleri", "Notification indicators"),
            ["BadgesDescription"] = ("Windows’un bildirdiği uyarıları simgelerde göster.", "Show alerts reported by Windows on application icons."),
            ["Theme"] = ("Tema", "Theme"),
            ["SurfaceOpacity"] = ("Yüzey opaklığı", "Surface opacity"),
            ["SurfaceOpacityHelp"] = ("Ayarlar, görev çubuğu ve menülerin arka plan opaklığı.", "Background opacity for settings, the taskbar, and menus."),
            ["OpacityPercent"] = ("%{0}", "{0}%"),
            ["Minimize"] = ("Küçült", "Minimize"),
            ["Maximize"] = ("Büyüt", "Maximize"),
            ["Restore"] = ("Önceki boyut", "Restore"),
            ["ThemeDescription"] = ("Görev çubuğu, menüler ve ayarların renkleri.", "Colors for the taskbar, menus, and settings."),
            ["Dark"] = ("Siyah", "Dark"),
            ["Light"] = ("Beyaz", "Light"),
            ["Language"] = ("Dil", "Language"),
            ["LanguageDescription"] = ("OpenTaskbar arayüzünün dili.", "Language used by the OpenTaskbar interface."),
            ["Position"] = ("Görev çubuğu konumu", "Taskbar position"),
            ["PositionDescription"] = ("Simgelerin ekranın alt kenarındaki hizası.", "Align icons along the bottom of your screen."),
            ["Left"] = ("Sol", "Left"), ["Center"] = ("Orta", "Center"), ["Right"] = ("Sağ", "Right"),
            ["AutoHide"] = ("Otomatik gizleme", "Auto-hide"),
            ["AutoHideDescription"] = ("Pencereler dock’un üzerine geldiğinde veya fare ayrıldığında dock’u gizle.", "Hide the dock when windows overlap or when the mouse leaves."),
            ["AutoHideDisabled"] = ("Kapalı", "Off"),
            ["AutoHideSmart"] = ("Pencerelerle örtüştüğünde", "When windows overlap"),
            ["AutoHideAlways"] = ("Fare ayrıldığında", "When the pointer leaves"),
            ["Apply"] = ("Uygula", "Apply"),
            ["SettingsSaved"] = ("Ayarlar kaydedildi.", "Settings saved."),
            ["Close"] = ("Kapat", "Close"),
            ["Exit"] = ("OpenTaskbar’dan çık", "Quit OpenTaskbar"),
            ["OpenApplication"] = ("Uygulamayı aç", "Open application"),
            ["ApplicationCommands"] = ("Uygulama komutları", "Application commands"),
            ["WindowCommands"] = ("Pencere komutları", "Window commands"),
            ["Pin"] = ("Görev çubuğuna sabitle", "Pin to taskbar"),
            ["Unpin"] = ("Sabitlemeyi kaldır", "Unpin from taskbar"),
            ["CloseApplication"] = ("Uygulamayı kapat", "Close application"),
            ["Terminate"] = ("Süreci sonlandır…", "End process…"),
            ["Start"] = ("Başlat", "Start"),
            ["ActivateWindow"] = ("Pencereyi öne getir", "Bring window to front"),
            ["MinimizeWindow"] = ("Pencereyi küçült", "Minimize window"),
            ["Tasks"] = ("Görevler", "Tasks"),
            ["PinnedDestinations"] = ("Sabitlenenler", "Pinned"),
            ["Recent"] = ("Son kullanılanlar", "Recent"),
            ["Frequent"] = ("Sık kullanılanlar", "Frequent"),
            ["StartupReadError"] = ("Windows başlangıç tercihi okunamadı. Erişim izinlerini denetleyin.", "Cannot read the Windows startup preference. Check access permissions."),
            ["StartupWriteError"] = ("Windows başlangıç tercihi kaydedilemedi. Erişim izinlerini denetleyin.", "Cannot save the Windows startup preference. Check access permissions."),
            ["StartupPathError"] = ("Başlangıç kaydı için geçerli bir OpenTaskbar.exe yolu gerekiyor.", "A valid OpenTaskbar.exe path is required for startup registration."),
            ["DiscardTitle"] = ("Kaydedilmemiş değişiklikler", "Unsaved changes"),
            ["DiscardChanges"] = ("Uygulanmamış değişiklikler var. Kaydetmeden kapatılsın mı?", "You have unapplied changes. Close without saving?"),
            ["StartupRestoreError"] = ("Başlangıç tercihi de geri alınamadı. Windows başlangıç ayarlarını denetleyin.", "The startup preference could not be restored either. Check Windows startup settings."),
            ["AlreadyRunning"] = ("OpenTaskbar zaten çalışıyor.", "OpenTaskbar is already running."),
            ["CloseWindow"] = ("Pencereyi kapat", "Close window"),
            ["PreviewUnavailable"] = ("Önizleme kullanılamıyor", "Preview unavailable"),
            ["TasksLoading"] = ("Görevler yükleniyor…", "Loading tasks…"),
            ["TasksEmpty"] = ("Bu uygulama görev listesi sunmuyor", "This application has no task list"),
            ["TasksIdentityUnavailable"] = ("Uygulamanın görev listesi eşleştirilemedi", "Cannot match this application's task list"),
            ["TasksUnsupported"] = ("Görev listesi bu Windows sürümünde desteklenmiyor", "Task lists are not supported on this Windows version"),
            ["TasksReadError"] = ("Görev listesi okunamadı; menüyü yeniden açın", "Cannot read the task list; reopen the menu"),
            ["TasksTimeout"] = ("Görev listesi zamanında yanıt vermedi", "The task list did not respond in time"),
            ["TasksServiceError"] = ("Windows görev hizmeti başlatılamadı", "Cannot start the Windows task service"),
            ["SkippedPins"] = ("Windows görev çubuğundaki {0} kısayol desteklenmedi veya okunamadı; diğerleri içe aktarıldı.", "{0} Windows taskbar shortcuts were unsupported or unreadable; the others were imported."),
            ["TerminateTitle"] = ("OpenTaskbar · Süreci sonlandır", "OpenTaskbar · End process"),
            ["TerminatePrompt"] = ("{0} uygulamasına ait {1} süreç zorla sonlandırılacak. Kaydedilmemiş çalışmalar kaybolabilir. Devam edilsin?", "Force quit {1} processes belonging to {0}? Unsaved work may be lost. Continue?")
        };

    private static readonly IReadOnlyDictionary<string, string> ErrorTranslations = new Dictionary<string, string>
    {
        ["Dock penceresi Windows'a bağlanamadı."] = "Cannot connect the dock window to Windows.",
        ["OpenTaskbar Alt+Tab listesinden çıkarılamadı."] = "Cannot exclude OpenTaskbar from Alt+Tab.",
        ["Windows uygulama bildirimlerini dinleyemedi; bildirim rozeti kullanılamıyor."] = "Cannot listen for Windows application alerts; notification indicators are unavailable.",
        ["Dock penceresi hazır olmadan görev çubuğu modu açılamaz."] = "The dock window must be ready before taskbar replacement can start.",
        ["Windows Başlat menüsüne açılma isteği gönderilemedi."] = "Cannot request the Windows Start menu.",
        ["Pencereyi öne getirme veya küçültme isteği tamamlanamadı. Windows odağı engellemiş olabilir."] = "Cannot activate or minimize the window. Windows may have blocked focus.",
        ["Uygulama dosyası bulunamadı. Windows görev çubuğundaki kısayolu güncelleyin."] = "Application file not found. Update its Windows taskbar shortcut.",
        ["Uygulama Windows tarafından başlatılamadı."] = "Windows could not start the application.",
        ["Uygulama başlatma isteği tamamlanamadı."] = "The application launch request could not be completed.",
        ["Uygulamayı başlatmak için erişim izni yok."] = "Permission to start the application was denied.",
        ["Uygulama dosyasına erişilemedi."] = "Cannot access the application file.",
        ["Önizleme penceresi etkinleştirilemedi. Windows odağı engellemiş olabilir."] = "Cannot activate the previewed window. Windows may have blocked focus.",
        ["Önizlenen pencereye kapatma isteği gönderilemedi."] = "Cannot send a close request to the previewed window.",
        ["Uygulama görevi çalıştırılamadı. Menüyü yeniden açıp deneyin."] = "Cannot run the application task. Reopen the menu and try again.",
        ["Uygulama komutu çalıştırılamadı; pencereyi veya menüyü yeniden açın."] = "Cannot run the application command; reopen the window or menu.",
        ["Uygulama sabitlenemedi."] = "Cannot pin the application.",
        ["Uygulamaya normal kapanma isteği gönderilemedi. Zorla kapatmak için süreç sonlandırma seçeneğini kullanın."] = "Cannot request a normal application close. Use End process to force quit.",
        ["Bazı uygulama süreçlerine normal kapanma isteği gönderilemedi."] = "Cannot send a normal close request to some application processes.",
        ["Uygulamanın çalışan süreci bulunamadı."] = "No running application process was found.",
        ["Uygulama süreçlerine erişilemiyor; uygulama yöneticisi iznini denetleyin."] = "Cannot access the application processes; check administrator permissions.",
        ["Uygulama süreçleri sonlandırılamadı. Yetkiyi ve uygulamanın tekrar başlamadığını denetleyin."] = "Cannot end the application processes. Check permissions and whether the application restarted.",
        ["Bazı uygulama süreçleri sonlandırılamadı."] = "Some application processes could not be ended.",
        ["Windows görev çubuğu geri yüklenemedi. Uygulama açık bırakıldı."] = "Cannot restore the Windows taskbar. OpenTaskbar remains open.",
        ["Dock ayarları geçersiz. Dosyayı düzeltin veya silip uygulamayı yeniden başlatın."] = "Dock settings are invalid. Correct or delete the settings file and restart.",
        ["Dock ayarları okunamadı. JSON biçimini düzeltin veya dosyayı silip yeniden başlatın."] = "Cannot read dock settings. Correct the JSON or delete the settings file and restart.",
        ["Dock ayarları diskten okunamadı."] = "Cannot read dock settings from disk.",
        ["Dock ayarlarına erişim izni yok."] = "Permission to read dock settings was denied.",
        ["Ayar değerleri geçersiz."] = "Settings contain invalid values.",
        ["Dock ayarları için kayıt yolu oluşturulamadı."] = "Cannot create a location for dock settings.",
        ["Dock ayarları diske kaydedilemedi."] = "Cannot save dock settings to disk.",
        ["Dock ayarlarına yazma izni yok."] = "Permission to write dock settings was denied.",
        ["Pencere başlığı değişiklikleri dinlenemedi."] = "Cannot listen for window title changes.",
        ["Görev çubuğu kurtarma kaydı bozuk. Windows ayarlarını elle denetleyin."] = "The taskbar recovery record is invalid. Check Windows taskbar settings manually.",
        ["Görev çubuğu kurtarma kaydı okunamadı."] = "Cannot read the taskbar recovery record.",
        ["Görev çubuğu kurtarma kaydına erişim izni yok."] = "Permission to access the taskbar recovery record was denied.",
        ["Görev çubuğu ayarı değiştirilmedi; kurtarma kaydı yazılamadı."] = "The Windows taskbar was left unchanged because its recovery record could not be saved.",
        ["Görev çubuğu ayarı değiştirilmedi; kurtarma kaydına yazma izni yok."] = "The Windows taskbar was left unchanged because permission to save its recovery record was denied.",
        ["Windows görev çubuğunun kenar tetiklemesi kapatılamadı."] = "Cannot disable the Windows taskbar edge trigger.",
        ["Windows görev çubuğu gizlenemedi."] = "Cannot hide the Windows taskbar.",
        ["Windows görev çubuğu geri yükleme işlemi devam ediyor."] = "The Windows taskbar is still being restored.",
        ["Windows görev çubuğu geri yüklenemedi. Kapatmayı yeniden deneyin veya Windows görev çubuğu ayarlarını denetleyin."] = "Cannot restore the Windows taskbar. Try closing again or check Windows taskbar settings.",
        ["Ekran boyutları alınamadı."] = "Cannot read display dimensions.",
        ["OpenTaskbar ekranın alt kenarına yerleştirilemedi."] = "Cannot position OpenTaskbar along the bottom of the screen.",
        ["Windows görev çubuğunun otomatik açılması yeniden etkinleşti."] = "Windows taskbar auto-hide activation was enabled again.",
        ["Windows görev çubuğu yeniden gizlenemedi."] = "Cannot hide the Windows taskbar again.",
        ["Görev çubuğu kurtarma kaydı için yol oluşturulamadı."] = "Cannot create a location for the taskbar recovery record.",
        ["Görev çubuğu kurtarma kaydı geçersiz."] = "The taskbar recovery record is invalid.",
        ["Görev çubuğu ayarı geri yüklendi ancak kurtarma kaydı silinemedi."] = "The Windows taskbar was restored, but its recovery record could not be removed.",
        ["Görev çubuğu ayarı geri yüklendi ancak kurtarma kaydını silme izni yok."] = "The Windows taskbar was restored, but permission to remove its recovery record was denied.",
        ["Windows görev çubuğu kısayol klasörü okunamadı."] = "Cannot read the Windows taskbar shortcut folder.",
        ["Windows görev çubuğu kısayol klasörüne erişim izni yok."] = "Permission to access the Windows taskbar shortcut folder was denied.",
        ["Windows kısayol hizmeti başlatılamadı; görev çubuğu uygulamaları içe aktarılamadı."] = "Cannot start the Windows shortcut service; taskbar applications could not be imported.",
        ["Windows kısayol hizmeti durdu; görev çubuğu uygulamalarının bir bölümü içe aktarılamadı."] = "The Windows shortcut service stopped; some taskbar applications could not be imported.",
        ["Windows görev çubuğu sabitleme klasörü oluşturulamadı."] = "Cannot create the Windows taskbar pin folder.",
        ["Windows kısayol hizmeti başlatılamadı; sabitleme oluşturulamadı."] = "Cannot start the Windows shortcut service; the pin could not be created.",
        ["Kısayol nesnesi oluşturulamadı."] = "Cannot create a shortcut object.",
        ["Uygulama Windows görev çubuğuna sabitlenemedi."] = "Cannot pin the application to the Windows taskbar.",
        ["Kısayol Windows görev çubuğu sabitleme klasöründe değil."] = "The shortcut is outside the Windows taskbar pin folder.",
        ["Uygulama Windows görev çubuğu sabitlerinden kaldırılamadı."] = "Cannot remove the application from Windows taskbar pins.",
        ["Görev çubuğu sabitini kaldırmak için erişim izni yok."] = "Permission to remove the taskbar pin was denied."
    };

    private volatile AppLanguage _language;
    public static UiText Current { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;
    public string this[string key]
    {
        get
        {
            var entry = Entries[key];
            return _language == AppLanguage.English ? entry.English : entry.Turkish;
        }
    }
    public static string Get(string key) => Current[key];
    public static string Format(string key, params object[] values) => string.Format(
        System.Globalization.CultureInfo.GetCultureInfo(Current._language == AppLanguage.English ? "en-US" : "tr-TR"), Get(key), values);

    // Preserve externally supplied errors when they are not one of our own localized messages.
    public static string LocalizeError(string message) => Current._language == AppLanguage.English
        && ErrorTranslations.TryGetValue(message, out var translated) ? translated : message;
    public void SetLanguage(AppLanguage language)
    {
        if (_language == language) return;
        _language = language;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{key}]") { Source = UiText.Current, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
