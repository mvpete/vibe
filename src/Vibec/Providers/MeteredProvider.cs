using System.Globalization;

namespace Vibec.Providers;

/// USD per million tokens. CachedInputPerMTok is null when the provider has no cache discount.
public sealed record Price(double InputPerMTok, double OutputPerMTok, double? CachedInputPerMTok = null);

public sealed record PriceQuote(Price Price, string Source);

/// Wraps any provider and keeps a running total of tokens across every model call in a compile:
/// resolution, lowering, backend, repairs, toolchain discovery.
public sealed class MeteredProvider(IModelProvider inner) : IModelProvider
{
    public int Calls { get; private set; }
    public long InputTokens { get; private set; }
    public long CachedInputTokens { get; private set; }
    public long OutputTokens { get; private set; }

    public string Name => inner.Name;
    public string Model => inner.Model;
    public bool SupportsJsonSchema => inner.SupportsJsonSchema;

    public async Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        var reply = await inner.CompleteAsync(request, ct);
        Calls++;
        InputTokens += reply.InputTokens;
        CachedInputTokens += reply.CachedInputTokens;
        OutputTokens += reply.OutputTokens;
        return reply;
    }

    public Task<ProbeResult> ProbeAsync(CancellationToken ct) => inner.ProbeAsync(ct);
    public Task<PriceQuote?> GetPriceAsync(CancellationToken ct) => inner.GetPriceAsync(ct);

    public double CostOf(Price p) =>
        ((InputTokens - CachedInputTokens) * p.InputPerMTok
         + CachedInputTokens * (p.CachedInputPerMTok ?? p.InputPerMTok)
         + OutputTokens * p.OutputPerMTok) / 1_000_000;

    public string Summary(PriceQuote? quote)
    {
        var inv = CultureInfo.InvariantCulture;
        var cached = CachedInputTokens > 0 ? $" ({CachedInputTokens.ToString("N0", inv)} cached)" : "";
        var tokens = $"{Calls} model call(s), {InputTokens.ToString("N0", inv)} in{cached} + {OutputTokens.ToString("N0", inv)} out tokens";
        return quote == null
            ? $"{tokens}. no price found for {Model}; set one under \"Pricing\" in appsettings.json"
            : $"{tokens}, ${CostOf(quote.Price).ToString("0.0000", inv)} ({quote.Source})";
    }
}
