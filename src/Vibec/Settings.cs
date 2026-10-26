using System.Text.Json;

namespace Vibec;

/// appsettings.json next to vibec.exe. Command-line flags and env vars override it.
public sealed class Settings
{
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? BaseUrl { get; set; }
    public OpenAISettings OpenAI { get; set; } = new();

    /// USD per million tokens, by model id: { "my-model": { "InputPerMTok": 1.25, "OutputPerMTok": 10 } }
    public Dictionary<string, Providers.Price> Pricing { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public sealed class OpenAISettings
    {
        public string? ApiKey { get; set; }
    }

    public static Settings Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path)) return new Settings();
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                ?? new Settings();
        }
        catch (JsonException ex)
        {
            throw new VibeError($"appsettings.json is broken: {ex.Message}");
        }
    }
}
