using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vibec.Providers;

/// Prices straight from OpenAI's pricing page (OpenAI has no pricing API). Parsed from the page's
/// tables, Standard tier, and cached for a day in ~/.vibec/openai-prices.json.
public static class OpenAIPricing
{
    public const string Url = "https://developers.openai.com/api/docs/pricing";
    static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    sealed record Cache(DateTimeOffset Fetched, Dictionary<string, Price> Prices);

    static string CachePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vibec", "openai-prices.json");

    public static async Task<PriceQuote?> LookupAsync(string model, CancellationToken ct)
    {
        var cache = Load();
        if (cache == null || DateTimeOffset.UtcNow - cache.Fetched > MaxAge)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("vibec (+https://github.com)");
                var html = await http.GetStringAsync(Url, ct);
                var prices = Parse(html);
                if (prices.Count > 0)
                {
                    cache = new Cache(DateTimeOffset.UtcNow, prices);
                    Save(cache);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                // offline: a stale cache is better than nothing
            }
        }

        if (cache?.Prices.GetValueOrDefault(model) is not { } price) return null;
        return new PriceQuote(price, $"OpenAI pricing page, {cache.Fetched.LocalDateTime:yyyy-MM-dd}");
    }

    /// Rows look like "gpt-6.1-sol $2.00 $10.00 $0.10 ..." (input, output, cached input). A model's
    /// first row is the Standard tier; later rows are Batch, Priority and so on.
    public static Dictionary<string, Price> Parse(string html)
    {
        var text = Regex.Replace(html, @"<(script|style)[\s\S]*?</\1>", " ", RegexOptions.IgnoreCase);
        text = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", " "));
        text = Regex.Replace(text, @"\s+", " ");

        var prices = new Dictionary<string, Price>(StringComparer.OrdinalIgnoreCase);
        var row = new Regex(@"(?:^| )((?:gpt|o\d|chatgpt|codex)[a-z0-9.\-]*) \$([\d.]+) \$([\d.]+)(?: \$([\d.]+))?");
        foreach (Match m in row.Matches(text))
        {
            if (prices.ContainsKey(m.Groups[1].Value)) continue;
            double D(Group g) => double.Parse(g.Value, CultureInfo.InvariantCulture);
            prices[m.Groups[1].Value] = new Price(D(m.Groups[2]), D(m.Groups[3]), m.Groups[4].Success ? D(m.Groups[4]) : null);
        }
        return prices;
    }

    static Cache? Load()
    {
        try { return File.Exists(CachePath) ? JsonSerializer.Deserialize<Cache>(File.ReadAllText(CachePath)) : null; }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    static void Save(Cache cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
    }
}
