using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace OpenTaskbar.Services;

public sealed record WeatherDay(DateOnly Date, int Code, double Min, double Max);

public sealed record WeatherReport(string Place, double Temperature, int Code, double ApparentTemperature, IReadOnlyList<WeatherDay> Days);

/// <summary>
/// Weather from Open-Meteo (free, no API key). The location is the city typed in Settings, or Windows location
/// services when no city is set.
/// </summary>
public static class WeatherService
{
    private static readonly HttpClient Client = CreateClient();

    public static async Task<(WeatherReport? Report, string? Error)> LoadAsync(string city, bool english, CancellationToken cancellation)
    {
        try
        {
            var location = string.IsNullOrWhiteSpace(city)
                ? await LocateDeviceAsync(cancellation)
                : await GeocodeAsync(city.Trim(), english, cancellation);
            if (location is not { } place)
            {
                return (null, string.IsNullOrWhiteSpace(city)
                    ? UiText.Get("ErrorWeatherLocation")
                    : UiText.Get("ErrorWeatherCityNotFound"));
            }

            var invariant = CultureInfo.InvariantCulture;
            var url = "https://api.open-meteo.com/v1/forecast?latitude=" + place.Latitude.ToString("F3", invariant)
                + "&longitude=" + place.Longitude.ToString("F3", invariant)
                + "&current=temperature_2m,apparent_temperature,weather_code"
                + "&daily=weather_code,temperature_2m_max,temperature_2m_min&forecast_days=4&timezone=auto";
            using var document = JsonDocument.Parse(await Client.GetStringAsync(url, cancellation));
            var current = document.RootElement.GetProperty("current");
            var daily = document.RootElement.GetProperty("daily");
            var dates = daily.GetProperty("time").EnumerateArray().Select(day => DateOnly.Parse(day.GetString()!, invariant)).ToList();
            var codes = daily.GetProperty("weather_code").EnumerateArray().Select(code => code.GetInt32()).ToList();
            var maxima = daily.GetProperty("temperature_2m_max").EnumerateArray().Select(value => value.GetDouble()).ToList();
            var minima = daily.GetProperty("temperature_2m_min").EnumerateArray().Select(value => value.GetDouble()).ToList();
            var days = dates.Select((date, index) => new WeatherDay(date, codes[index], minima[index], maxima[index])).ToList();
            return (new WeatherReport(place.Name,
                current.GetProperty("temperature_2m").GetDouble(),
                current.GetProperty("weather_code").GetInt32(),
                current.GetProperty("apparent_temperature").GetDouble(),
                days), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException
            or TimeoutException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            AppLog.Info($"Weather unavailable: {ex.Message}");
            return (null, UiText.Get("ErrorWeatherLoad"));
        }
    }

    private static async Task<(string Name, double Latitude, double Longitude)?> GeocodeAsync(string city, bool english, CancellationToken cancellation)
    {
        var url = "https://geocoding-api.open-meteo.com/v1/search?count=1&format=json&language=" + (english ? "en" : "tr")
            + "&name=" + Uri.EscapeDataString(city);
        using var document = JsonDocument.Parse(await Client.GetStringAsync(url, cancellation));
        if (!document.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
        var first = results[0];
        return (first.GetProperty("name").GetString() ?? city, first.GetProperty("latitude").GetDouble(), first.GetProperty("longitude").GetDouble());
    }

    private static async Task<(string Name, double Latitude, double Longitude)?> LocateDeviceAsync(CancellationToken cancellation)
    {
        try
        {
            var access = await Windows.Devices.Geolocation.Geolocator.RequestAccessAsync().AsTask(cancellation);
            if (access != Windows.Devices.Geolocation.GeolocationAccessStatus.Allowed) return null;
            var locator = new Windows.Devices.Geolocation.Geolocator { DesiredAccuracy = Windows.Devices.Geolocation.PositionAccuracy.Default };
            var position = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(10)).AsTask(cancellation);
            var point = position.Coordinate.Point.Position;
            return (string.Empty, point.Latitude, point.Longitude);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException or TaskCanceledException
            or OperationCanceledException or TimeoutException)
        {
            AppLog.Info($"Device location unavailable: {ex.Message}");
            return null;
        }
    }

    /// <summary>WMO weather code to an icon key in Design/Widgets.xaml and a localized description key.</summary>
    public static (string Icon, string Text) Describe(int code) => code switch
    {
        0 => ("IconSun", "WeatherClear"),
        1 or 2 => ("IconCloudSun", "WeatherPartlyCloudy"),
        3 => ("IconCloud", "WeatherCloudy"),
        45 or 48 => ("IconCloudFog", "WeatherFog"),
        >= 51 and <= 67 or >= 80 and <= 82 => ("IconCloudRain", "WeatherRain"),
        >= 71 and <= 77 or 85 or 86 => ("IconSnow", "WeatherSnow"),
        >= 95 => ("IconCloudLightning", "WeatherStorm"),
        _ => ("IconCloud", "WeatherCloudy")
    };

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenTaskbar");
        return client;
    }
}
