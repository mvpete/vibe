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
        var source = File.ReadAllText(o.SourcePath);
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

        // the static and vibe passes alternate: answers can raise new questions
        // (a generated function may call another undefined one), so re-run on a fresh parse
        var knowledge = new Knowledge(o.Fresh ? null : Path.Combine(intDir, "vibecache.json"));
        List<Stmt> program;
        StaticPass pass;
        var vibe = new VibePass(provider);
        for (int round = 1; ; round++)
        {
            program = Parser.ParseSource(source);
            pass = new StaticPass(knowledge) { Final = round == MaxVibeRounds };
            pass.Run(program);
            if (round == 1) Step("parse", $"{program.Count} top-level statements");
            if (pass.Questions.Count == 0) break;

            Step("analysis", $"{pass.Questions.Count} thing(s) need semantic resolution");
            var outcome = await vibe.AnswerAsync(pass.Questions, knowledge, source, ct);
            knowledge.Save();
            Step("resolution", $"{outcome.Resolved} resolved, {outcome.Rejected} unresolved ({provider.Name}/{provider.Model}, {outcome.InputTokens}+{outcome.OutputTokens} tokens)");
        }

        foreach (var d in pass.Diagnostics.Where(d => !d.IsError)) log.WriteLine($"  warning: {d}");
        if (pass.HasErrors)
        {
            foreach (var d in pass.Diagnostics.Where(d => d.IsError)) log.WriteLine($"  error: {d}");
            log.WriteLine("these rules don't check out. fix the errors above.");
            return 1;
        }

        var annotated = Printer.Print(program.Concat(pass.Generated));
        if (o.DumpAst) Intermediate(Path.Combine(intDir, $"{name}.ast.vibe"), annotated);

        var (ir, promptReply) = await new PromptPass(provider).WriteAsync(annotated, pass.Features, target, toolchain, ct);
        Step("lowering", $"specification ready ({promptReply.OutputTokens} tokens)");
        Intermediate(Path.Combine(intDir, $"{name}.prompt"), ir);

        if (Directory.Exists(srcDir)) Directory.Delete(srcDir, recursive: true);
        Directory.CreateDirectory(srcDir);

        var result = await new CodegenPass(provider).RunAsync(ir, target, toolchain, o.MaxRepairs, srcDir, name, m => Step("backend", m), ct);
        Intermediate(Path.Combine(intDir, $"{name}.transcript.md"), result.Transcript);

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
