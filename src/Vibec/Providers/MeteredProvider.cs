using System.Globalization;

namespace Vibec.Providers;

public sealed record Price(double InputPerMTok, double OutputPerMTok);

/// Wraps any provider and keeps a running total of tokens (and estimated cost) across every
/// model call in a compile: resolution, lowering, backend, repairs, toolchain discovery.
public sealed class MeteredProvider(IModelProvider inner, Price? price) : IModelProvider
{
    // Defaults, in USD per million tokens. Prices change; override them in appsettings.json ("Pricing").
    public static readonly Dictionary<string, Price> DefaultPrices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-5"] = new(1.25, 10.00),
        ["gpt-5-mini"] = new(0.25, 2.00),
        ["gpt-5-nano"] = new(0.05, 0.40),
    };

    public int Calls { get; private set; }
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }

    public string Name => inner.Name;
    public string Model => inner.Model;
    public bool SupportsJsonSchema => inner.SupportsJsonSchema;

    public async Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        var reply = await inner.CompleteAsync(request, ct);
        Calls++;
        InputTokens += reply.InputTokens;
        OutputTokens += reply.OutputTokens;
        return reply;
    }

    public Task<ProbeResult> ProbeAsync(CancellationToken ct) => inner.ProbeAsync(ct);

    public double? Cost => price == null ? null : (InputTokens * price.InputPerMTok + OutputTokens * price.OutputPerMTok) / 1_000_000;

    public string Summary()
    {
        var inv = CultureInfo.InvariantCulture;
        var tokens = $"{Calls} model call(s), {InputTokens.ToString("N0", inv)} in + {OutputTokens.ToString("N0", inv)} out tokens";
        return Cost is { } cost ? $"{tokens}, ~${cost.ToString("0.0000", inv)} (estimated)" : $"{tokens} (no price for {Model}; add one under \"Pricing\" in appsettings.json)";
    }
}
