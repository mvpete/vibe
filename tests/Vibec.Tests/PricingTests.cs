using Vibec.Providers;

namespace Vibec.Tests;

public class PricingTests
{
    // trimmed from the shape of developers.openai.com/api/docs/pricing
    const string Page = """
        <html><head><script>var price = "$999";</script><style>.x{}</style></head><body>
        <p>Prices per 1M tokens.</p>
        <table><tr><th>Model</th><th>Input</th><th>Output</th><th>Cached input</th></tr>
        <tr><td>gpt-6.1-sol</td><td>$2.00</td><td>$10.00</td><td>$0.10</td></tr>
        <tr><td>gpt-6-luna</td><td>$0.10</td><td>$0.50</td><td>$0.01</td></tr>
        <tr><td>gpt-5.3-codex</td><td>$1.75</td><td>$14.00</td></tr></table>
        <h3>Batch</h3>
        <table><tr><td>gpt-6.1-sol</td><td>$1.00</td><td>$5.00</td><td>$0.05</td></tr></table>
        </body></html>
        """;

    [Fact]
    public void ParsesStandardTierRows()
    {
        var prices = OpenAIPricing.Parse(Page);
        Assert.Equal(new Price(2.00, 10.00, 0.10), prices["gpt-6.1-sol"]); // first row wins: Standard, not Batch
        Assert.Equal(new Price(0.10, 0.50, 0.01), prices["gpt-6-luna"]);
        Assert.Equal(new Price(1.75, 14.00, null), prices["gpt-5.3-codex"]);
    }

    [Fact]
    public void IgnoresScriptsAndStyles()
    {
        Assert.DoesNotContain(OpenAIPricing.Parse(Page).Values, p => p.InputPerMTok == 999);
    }

    [Fact]
    public void CachedTokensArePricedSeparately()
    {
        var meter = new MeteredProvider(new FakeProvider(new ModelReply("", false, null, InputTokens: 1_000_000, OutputTokens: 1_000_000, CachedInputTokens: 500_000)));
        meter.CompleteAsync(new ModelRequest("", []), CancellationToken.None).Wait();
        // 0.5M uncached @ $2 + 0.5M cached @ $0.10 + 1M out @ $10
        Assert.Equal(1.00 + 0.05 + 10.00, meter.CostOf(new Price(2.00, 10.00, 0.10)), 6);
    }

    sealed class FakeProvider(ModelReply reply) : IModelProvider
    {
        public string Name => "fake";
        public string Model => "fake-1";
        public bool SupportsJsonSchema => true;
        public Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken ct) => Task.FromResult(reply);
        public Task<ProbeResult> ProbeAsync(CancellationToken ct) => Task.FromResult(new ProbeResult(true, "ok"));
        public Task<PriceQuote?> GetPriceAsync(CancellationToken ct) => Task.FromResult<PriceQuote?>(null);
    }
}
