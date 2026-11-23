using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace OpenTaskbar.Services;

public sealed record CountryItem(string Name, string Iso2);

public static class CountryCityService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> CityCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyList<CountryItem> FallbackCountries =
    [
        new("Turkey", "TR"),
        new("Germany", "DE"),
        new("United States", "US"),
        new("United Kingdom", "GB"),
        new("France", "FR"),
        new("Italy", "IT"),
        new("Spain", "ES"),
        new("Netherlands", "NL"),
        new("Canada", "CA"),
        new("Australia", "AU"),
        new("Azerbaijan", "AZ"),
        new("Switzerland", "CH"),
        new("Austria", "AT"),
        new("Belgium", "BE"),
        new("Sweden", "SE"),
        new("Norway", "NO"),
        new("Denmark", "DK"),
        new("Poland", "PL"),
        new("Japan", "JP"),
        new("South Korea", "KR"),
        new("Brazil", "BR")
    ];

    // Starts null so GetCountriesAsync actually attempts the live fetch once; GetInitialCountries
    // serves the fallback synchronously without triggering the network path.
    private static IReadOnlyList<CountryItem>? _cachedCountries;

    private static readonly IReadOnlyList<string> TurkeyProvinces =
    [
        "Adana", "Adıyaman", "Afyonkarahisar", "Ağrı", "Amasya", "Ankara", "Antalya", "Artvin", "Aydın",
        "Balıkesir", "Bilecik", "Bingöl", "Bitlis", "Bolu", "Burdur", "Bursa", "Çanakkale", "Çankırı",
        "Çorum", "Denizli", "Diyarbakır", "Edirne", "Elazığ", "Erzincan", "Erzurum", "Eskişehir",
        "Gaziantep", "Giresun", "Gümüşhane", "Hakkari", "Hatay", "Isparta", "Mersin", "İstanbul",
        "İzmir", "Kars", "Kastamonu", "Kayseri", "Kırklareli", "Kırşehir", "Kocaeli", "Konya",
        "Kütahya", "Malatya", "Manisa", "Kahramanmaraş", "Mardin", "Muğla", "Muş", "Nevşehir",
        "Niğde", "Ordu", "Rize", "Sakarya", "Samsun", "Siirt", "Sinop", "Sivas", "Tekirdağ",
        "Tokat", "Trabzon", "Tunceli", "Şanlıurfa", "Uşak", "Van", "Yozgat", "Zonguldak",
        "Aksaray", "Bayburt", "Karaman", "Kırıkkale", "Batman", "Şırnak", "Bartın", "Ardahan",
        "Iğdır", "Yalova", "Karabük", "Kilis", "Osmaniye", "Düzce"
    ];

    static CountryCityService()
    {
        CityCache["Turkey"] = TurkeyProvinces;
        CityCache["Türkiye"] = TurkeyProvinces;
    }

    public static IReadOnlyList<CountryItem> GetInitialCountries() => _cachedCountries ?? FallbackCountries;

    /// <summary>
    /// Looks up a cached country whose city list contains <paramref name="city"/> (case-insensitive).
    /// Returns <c>(null, [])</c> when the city belongs to an uncached or custom location.
    /// </summary>
    public static (string? Country, IReadOnlyList<string> Cities) FindCachedCountryForCity(string? city)
    {
        if (string.IsNullOrWhiteSpace(city)) return (null, []);
        var trimmed = city.Trim();
        if (TurkeyProvinces.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            return ("Turkey", TurkeyProvinces);

        foreach (var (country, cities) in CityCache)
        {
            if (string.Equals(country, "Türkiye", StringComparison.OrdinalIgnoreCase)) continue;
            if (cities.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                return (country, cities);
        }

        return (null, []);
    }

    public static async Task<IReadOnlyList<CountryItem>> GetCountriesAsync(CancellationToken cancellation = default)
    {
        if (_cachedCountries is not null) return _cachedCountries;
        try
        {
            var json = await Client.GetStringAsync("https://countriesnow.space/api/v0.1/countries/iso", cancellation);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                var list = new List<CountryItem>();
                foreach (var el in data.EnumerateArray())
                {
                    var name = el.GetProperty("name").GetString();
                    var iso2 = el.GetProperty("Iso2").GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        list.Add(new CountryItem(name, iso2));
                    }
                }
                if (list.Count > 0)
                {
                    list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                    _cachedCountries = list;
                    return list;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            AppLog.Warning($"Country list fetch failed, using fallback: {ex.Message}");
        }

        return FallbackCountries;
    }

    public static async Task<IReadOnlyList<string>> GetCitiesAsync(string country, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(country)) return [];
        if (CityCache.TryGetValue(country, out var cached)) return cached;

        try
        {
            var payload = JsonSerializer.Serialize(new { country });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await Client.PostAsync("https://countriesnow.space/api/v0.1/countries/cities", content, cancellation);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellation);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    var cities = new List<string>();
                    foreach (var cityEl in data.EnumerateArray())
                    {
                        if (cityEl.ValueKind != JsonValueKind.String) continue;
                        var cityName = cityEl.GetString();
                        if (!string.IsNullOrWhiteSpace(cityName)) cities.Add(cityName);
                    }
                    if (cities.Count > 0)
                    {
                        cities.Sort(StringComparer.OrdinalIgnoreCase);
                        CityCache[country] = cities;
                        return cities;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidOperationException)
        {
            AppLog.Warning($"City list fetch for {country} failed: {ex.Message}");
        }

        if (country.Contains("Turk", StringComparison.OrdinalIgnoreCase))
        {
            CityCache[country] = TurkeyProvinces;
            return TurkeyProvinces;
        }

        return [];
    }
}
