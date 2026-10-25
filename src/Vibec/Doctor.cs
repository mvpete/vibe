using Vibec.Passes;
using Vibec.Providers;

namespace Vibec;

/// `vibec doctor [target]`: whether the model is reachable, and which toolchains work here.
public static class Doctor
{
    public static async Task<int> RunAsync(IModelProvider provider, string? target, bool refresh, bool assumeYes, CancellationToken ct)
    {
        Console.WriteLine("vibec doctor\n");
        var probe = await provider.ProbeAsync(ct);
        Console.WriteLine($"  {"model",-11} {(probe.Ok ? "✅" : "❌")} {provider.Name}: {probe.Detail}\n");

        IEnumerable<Recipe> recipes = Toolchains.All().OrderBy(r => r.Target).ToList();
        if (target != null)
        {
            if (!probe.Ok) return 2;
            var recipe = await new RecipePass(provider, Console.Out).GetAsync(target.ToLowerInvariant(), refresh, assumeYes, ct);
            if (recipe == null) return 1;
            recipes = [recipe];
        }
        else if (!recipes.Any())
        {
            Console.WriteLine("  no toolchains discovered yet. try: vibec doctor rust");
        }

        foreach (var r in recipes)
        {
            var (found, notes) = Toolchains.Detect(r);
            var line = found switch
            {
                null => $"❌ not found → {r.InstallHint}",
                { TooOld: true } => $"⚠️  {found.Display} (too old; builds may fail)",
                _ => $"✅ {found.Display}",
            };
            Console.WriteLine($"  {r.Target,-11} {line}");
            foreach (var n in notes) Console.WriteLine($"  {"",-11}    {n}");
        }
        return probe.Ok ? 0 : 2;
    }
}
