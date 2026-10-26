using Vibec.Passes;
using Vibec.Providers;

namespace Vibec;

public sealed class CompileOptions
{
    public required string SourcePath { get; init; }
    public required string Target { get; init; }
    public string? OutDir { get; init; }
    public bool DumpAst { get; init; }
    public bool Build { get; init; } = true;
    public int MaxRepairs { get; init; } = 3;
    public bool Fresh { get; init; }
    public bool AssumeYes { get; init; }
    public bool RefreshToolchain { get; init; }
}

/// Lex → parse → static pass ⇄ vibe pass → prompt pass → codegen (+ build/repair).
public sealed class Compiler(IModelProvider provider, TextWriter log)
{
    const int MaxVibeRounds = 3;

    public async Task<int> CompileAsync(CompileOptions o, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(o.SourcePath))!;
        var name = Path.GetFileNameWithoutExtension(o.SourcePath);
        // toolchain first: you should know before paying for codegen
        var intDir = Path.Combine(dir, ".vibe-int");
        var srcDir = Path.Combine(intDir, "src", $"{name}-{o.Target}");
        var binDir = o.OutDir ?? Path.Combine(dir, "bin");
        Directory.CreateDirectory(intDir);

        var target = await new RecipePass(provider, log).GetAsync(o.Target, o.RefreshToolchain, o.AssumeYes, ct);
        if (target == null)
        {
            log.WriteLine($"  no approved toolchain recipe for '{o.Target}'; nothing compiled.");
            return 1;
        }

        Toolchain? toolchain = null;
        if (o.Build)
        {
            var (found, notes) = Toolchains.Detect(target);
            foreach (var n in notes) Step("toolchain", n);
            if (found == null) Step("toolchain", $"⚠ no {target.Language} toolchain ({target.InstallHint}); output will be unverified");
            else Step("toolchain", found.TooOld ? $"⚠ {found.Display} (old; builds may fail)" : found.Display);
            toolchain = found;
        }

        // the entry file plus every module it uses, dependencies first
        var (modules, loadWarnings) = ModuleLoader.Load(o.SourcePath);
        foreach (var w in loadWarnings) log.WriteLine($"  warning: {w}");
        if (modules.Count > 1) Step("modules", string.Join(" → ", modules.Select(m => m.Name)));

        // the static and vibe passes alternate: answers can raise new questions
        // (a generated function may call another undefined one), so re-run on a fresh parse
        var knowledge = new Knowledge(Path.Combine(intDir, "vibecache.json"), fresh: o.Fresh);
        ProjectAnalysis analysis;
        var vibe = new VibePass(provider);
        for (int round = 1; ; round++)
        {
            analysis = ProjectAnalysis.Run(modules, knowledge, final: round == MaxVibeRounds);
            if (round == 1) Step("parse", $"{analysis.Modules.Sum(m => m.Program.Count)} top-level statements");
            var questions = analysis.Questions;
            if (questions.Count == 0) break;

            Step("analysis", $"{questions.Count} thing(s) need semantic resolution");
            var outcome = await vibe.AnswerAsync(questions, knowledge, analysis.Sources, ct);
            knowledge.Save();
            Step("resolution", $"{outcome.Resolved} resolved, {outcome.Rejected} unresolved ({provider.Name}/{provider.Model}, {outcome.InputTokens}+{outcome.OutputTokens} tokens)");
        }

        var diagnostics = analysis.Diagnostics;
        foreach (var d in diagnostics.Where(d => !d.IsError)) log.WriteLine($"  warning: {d}");
        if (analysis.HasErrors)
        {
            foreach (var d in diagnostics.Where(d => d.IsError)) log.WriteLine($"  error: {d}");
            log.WriteLine("these rules don't check out. fix the errors above.");
            return 1;
        }

        var annotated = analysis.Annotated();
        if (o.DumpAst) Intermediate(Path.Combine(intDir, $"{name}.ast.vibe"), annotated);

        var usedStd = analysis.UsedStd;
        var library = usedStd.Count == 0 ? null : string.Join("\n\n", usedStd.OrderBy(u => u.Key).Select(u =>
        {
            var module = StdLib.Get(u.Key)!;
            var signatures = string.Join("\n", u.Value.Select(f => Printer.Signature(module.Functions[f])));
            return $"### {module.Name}\n{module.Prompt}\n```vibe\n{signatures}\n```";
        }));
        var (ir, promptReply) = await new PromptPass(provider).WriteAsync(annotated, analysis.Features, target, toolchain, library, ct);
        Step("lowering", $"specification ready ({promptReply.OutputTokens} tokens)");
        Intermediate(Path.Combine(intDir, $"{name}.{target.Target}.prompt.md"), ir);

        if (Directory.Exists(srcDir)) Directory.Delete(srcDir, recursive: true);
        Directory.CreateDirectory(srcDir);

        var result = await new CodegenPass(provider).RunAsync(ir, target, toolchain, o.MaxRepairs, srcDir, name, m => Step("backend", m), ct);
        Intermediate(Path.Combine(intDir, $"{name}.{target.Target}.transcript.md"), result.Transcript);

        var sourceFile = Path.Combine(srcDir, target.FileName);
        if (result.Verified && !result.Built)
        {
            log.WriteLine($"  ✗ still failing after {result.Attempts} attempt(s). last attempt: {sourceFile}");
            log.WriteLine(Indent(result.Errors ?? ""));
            return 3;
        }

        if (!string.IsNullOrWhiteSpace(result.Notes)) Step("notes", result.Notes!);
        Step("source", sourceFile);

        // a compiled artifact only ships if it built; an interpreted source ships either way
        if (!result.Built && toolchain?.Candidate.Output != null)
        {
            log.WriteLine("  ⚠ not built, so nothing in bin/");
            return 0;
        }
        var delivered = Toolchains.Deliver(target, result.Built ? toolchain : null, srcDir, binDir, name);
        var main = delivered.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == name && (p.EndsWith(".exe") || !Path.HasExtension(p) || p.EndsWith(target.Extension)))
                   ?? delivered.First();
        var extra = delivered.Count > 1 ? $" (+{delivered.Count - 1} file(s))" : "";
        log.WriteLine(result.Built ? $"  ✓ {main}{extra}" : $"  ⚠ {main} (unverified: no toolchain)");
        return 0;
    }

    void Step(string label, string message) => log.WriteLine($"  {label,-12} {message}");

    /// Every stage leaves its output in .vibe-int/ as soon as it finishes, so a failure
    /// later on still leaves everything before it to inspect.
    void Intermediate(string path, string content)
    {
        File.WriteAllText(path, content);
        Step("wrote", Path.GetRelativePath(Environment.CurrentDirectory, path));
    }

    static string Indent(string s) => string.Join("\n", s.Split('\n').Take(30).Select(l => "    " + l));
}
