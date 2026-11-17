using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

public static class LanguageCatalog
{
    private const string ResourcePrefix = "OpenTaskbar.Localization.Languages.";
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Catalogs = LoadCatalogs();

    public static IReadOnlyList<AppLanguage> Languages { get; } = Catalogs.Keys
        .Select(name => new AppLanguage(name))
        .OrderBy(language => language.Culture.EnglishName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static bool IsSupported(AppLanguage language) => Catalogs.Keys.Any(name =>
        string.Equals(CultureInfo.GetCultureInfo(name).TwoLetterISOLanguageName,
            language.Culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase));

    public static AppLanguage FromCulture(CultureInfo culture)
    {
        if (Catalogs.ContainsKey(culture.Name)) return new AppLanguage(culture.Name);
        var matchingLanguage = Languages
            .Where(language => string.Equals(language.Culture.TwoLetterISOLanguageName,
                culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
            .Select(language => (AppLanguage?)language)
            .FirstOrDefault();
        if (matchingLanguage.HasValue)
            return culture.IsNeutralCulture ? matchingLanguage.Value : new AppLanguage(culture.Name);
        return AppLanguage.English;
    }

    public static string Get(string key, AppLanguage language)
    {
        if (Catalogs.TryGetValue(language.CultureName, out var exactCatalog)
            && exactCatalog.TryGetValue(key, out var exact))
            return exact;

        foreach (var catalog in GetLanguageFallbacks(language.Culture))
        {
            if (catalog.TryGetValue(key, out var translated)) return translated;
        }

        if (!string.Equals(language.CultureName, AppLanguage.English.CultureName, StringComparison.OrdinalIgnoreCase)
            && Catalogs[AppLanguage.English.CultureName].TryGetValue(key, out var english))
            return english;

        throw new KeyNotFoundException($"Localization key '{key}' is missing from the English catalog.");
    }

    public static IReadOnlyCollection<string> GetTranslations(string key) => Catalogs.Values
        .Where(catalog => catalog.ContainsKey(key))
        .Select(catalog => catalog[key])
        .ToArray();

    public static bool ContainsKey(string key) => Catalogs.Values.Any(catalog => catalog.ContainsKey(key));

    public static bool TryFindKey(string text, out string key)
    {
        foreach (var catalog in Catalogs.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase).Select(entry => entry.Value))
        {
            var match = catalog.FirstOrDefault(entry => string.Equals(entry.Value, text, StringComparison.Ordinal));
            if (match.Key is null) continue;
            key = match.Key;
            return true;
        }

        key = string.Empty;
        return false;
    }

    private static IEnumerable<IReadOnlyDictionary<string, string>> GetLanguageFallbacks(CultureInfo culture) =>
        Catalogs
            .Where(entry => string.Equals(CultureInfo.GetCultureInfo(entry.Key).TwoLetterISOLanguageName,
                culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entry.Key, culture.Name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => CultureInfo.GetCultureInfo(entry.Key).IsNeutralCulture ? 0 : 1)
            .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Value);

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LoadCatalogs()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var catalogs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                         && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            var cultureName = resourceName[ResourcePrefix.Length..^5];
            var culture = CultureInfo.GetCultureInfo(cultureName);
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidDataException($"Localization resource '{resourceName}' could not be opened.");
            var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                ?? throw new InvalidDataException($"Localization resource '{resourceName}' is empty.");
            if (!catalogs.TryAdd(culture.Name, translations))
                throw new InvalidDataException($"Localization culture '{culture.Name}' is defined more than once.");
        }

        if (!catalogs.ContainsKey(AppLanguage.English.CultureName))
            throw new InvalidDataException("The required English localization resource is missing.");
        return catalogs;
    }
}
