using System.Reflection;
using Vibec;
using Vibec.Providers;

const string Usage = """
    vibec - the Vibe compiler

    usage:
      vibec <file.vibe> [options]
      vibec <file.il.md> -t <target>       compile an IL directly (write once, compile anywhere)
      vibec doctor [<target>] [--refresh] [--provider <p>] [--model <m>]
      vibec undo <file.vibe> [--force]     put back what FTFY changed (also: vibec nvm)
      vibec --version

    options:
      -t, --target <t>     any language: csharp (default), rust, go, html, python, zig, ...
                           (the model works out the toolchain; you approve it once)
      -o, --out <dir>      where the output goes (default: bin/ next to the source)
      --provider <p>       openai (default), ollama          [env VIBEC_PROVIDER, appsettings Provider]
      --model <m>          model id                          [env VIBEC_MODEL, appsettings Model]
      --base-url <url>     any OpenAI-compatible endpoint
      --dump-ast           also write the annotated AST to .vibe-int/<name>.ast.vibe
      --no-build           skip the toolchain build/repair loop
      --max-repairs <n>    build-repair rounds (default 3)
      --fresh              re-ask everything and replace the vibe cache
      --refresh            re-discover the target's toolchain recipe
      --no-ftfy            don't fix obvious mistakes in your .vibe files; just warn
      -y, --yes            approve a new toolchain recipe without asking
    """;

var opts = new Dictionary<string, string?>();
var positional = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    var a = args[i];
    string Value() => i + 1 < args.Length ? args[++i] : throw new VibeError($"{a} needs a value");
    try
    {
        switch (a)
        {
            case "-t" or "--target": opts["target"] = Value(); break;
            case "-o" or "--out": opts["out"] = Value(); break;
            case "--provider": opts["provider"] = Value(); break;
            case "--model": opts["model"] = Value(); break;
            case "--base-url": opts["base-url"] = Value(); break;
            case "--max-repairs": opts["max-repairs"] = Value(); break;
            case "--dump-ast" or "--no-build" or "--fresh" or "--refresh" or "--no-ftfy" or "--force": opts[a[2..]] = "1"; break;
            case "-y" or "--yes": opts["yes"] = "1"; break;
            case "-h" or "--help": Console.WriteLine(Usage); return 0;
            case "--version":
                var version = typeof(Compiler).Assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
                Console.WriteLine($"vibec {version.Split('+')[0]}");
                return 0;
            default:
                if (a.StartsWith('-')) { Console.Error.WriteLine($"unknown option {a}\n\n{Usage}"); return 64; }
                positional.Add(a);
                break;
        }
    }
    catch (VibeError ex) { Console.Error.WriteLine(ex.Message); return 64; }
}

if (positional.Count == 0) { Console.WriteLine(Usage); return 64; }

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
Console.OutputEncoding = System.Text.Encoding.UTF8;

MeteredProvider? provider = null;
Dictionary<string, Price> pricing = [];
try
{
    var settings = Settings.Load();
    provider = new MeteredProvider(ProviderFactory.Create(opts.GetValueOrDefault("provider"), opts.GetValueOrDefault("model"), opts.GetValueOrDefault("base-url"), settings));
    pricing = settings.Pricing;

    if (positional[0] is "undo" or "nvm")
    {
        if (positional.Count < 2) { Console.Error.WriteLine("usage: vibec undo <file.vibe> [--force]"); return 64; }
        Console.WriteLine($"  {"undo",-12} {Ftfy.Undo(positional[1], opts.ContainsKey("force"))}");
        return 0;
    }

    if (positional[0] == "doctor")
        return await Doctor.RunAsync(provider, positional.ElementAtOrDefault(1), opts.ContainsKey("refresh"), opts.ContainsKey("yes"), cts.Token);

    var path = positional[0];
    if (!File.Exists(path)) { Console.Error.WriteLine($"no such file: {path}"); return 66; }

    var target = (opts.GetValueOrDefault("target") ?? "csharp").ToLowerInvariant();

    var options = new CompileOptions
    {
        SourcePath = path,
        Target = target,
        OutDir = opts.GetValueOrDefault("out"),
        DumpAst = opts.ContainsKey("dump-ast"),
        Build = !opts.ContainsKey("no-build"),
        MaxRepairs = int.TryParse(opts.GetValueOrDefault("max-repairs"), out var n) ? n : 3,
        Fresh = opts.ContainsKey("fresh"),
        AssumeYes = opts.ContainsKey("yes"),
        RefreshToolchain = opts.ContainsKey("refresh"),
        Ftfy = !opts.ContainsKey("no-ftfy"),
    };

    Console.WriteLine($"vibec {Path.GetFileName(path)} → {target}");
    return await new Compiler(provider, Console.Out).CompileAsync(options, cts.Token);
}
catch (VibeError ex)
{
    Console.Error.WriteLine($"  error: {ex.Message}");
    return 1;
}
catch (ModelException ex)
{
    Console.Error.WriteLine($"  model error: {ex.Message}");
    return 2;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("  cancelled. the vibes will keep.");
    return 130;
}
finally
{
    // what this compile cost, success or not: an appsettings override, else the provider's own prices
    if (provider is { Calls: > 0 })
    {
        PriceQuote? quote = pricing.GetValueOrDefault(provider.Model) is { } own ? new PriceQuote(own, "appsettings.json") : null;
        try { quote ??= await provider.GetPriceAsync(CancellationToken.None); }
        catch (Exception) { /* pricing must never break a compile */ }
        Console.WriteLine($"  {"usage",-12} {provider.Summary(quote)}");
    }
}
