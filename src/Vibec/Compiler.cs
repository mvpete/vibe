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
    public bool Ftfy { get; init; } = true; // fix "you know what I mean" errors in the source; --no-ftfy just warns
}

/// Lex → parse → static pass ⇄ vibe pass → prompt pass → codegen (+ build/repair).
public sealed class Compiler(IModelProvider provider, TextWriter log)
{
    const int MaxVibeRounds = 3;

    public async Task<int> CompileAsync(CompileOptions o, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(o.SourcePath))!;
        bool fromIl = IlFile.IsIlPath(o.SourcePath);
        var name = fromIl ? IlFile.NameOf(o.SourcePath) : Path.GetFileNameWithoutExtension(o.SourcePath);
        // an IL sitting in .vibe-int/ builds next to its source, not inside .vibe-int/
        if (fromIl && Path.GetFileName(dir) == ".vibe-int") dir = Path.GetDirectoryName(dir)!;
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

        if (fromIl) return await CompileIlAsync(o, target, toolchain, intDir, srcDir, binDir, ct);

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
            var outcome = await Indicator.Run("resolution", () => vibe.AnswerAsync(questions, knowledge, analysis.Sources, ct));
            knowledge.Save();
            Step("resolution", $"{outcome.Resolved} resolved, {outcome.Rejected} unresolved ({provider.Name}/{provider.Model}, {outcome.InputTokens}+{outcome.OutputTokens} tokens)");
        }

        // FTFY: "you know what I mean" errors get fixed in the .vibe itself, then re-checked
        var fixes = analysis.Modules.SelectMany(m => m.Fixes.Select(f => (m.Module, Fix: f))).ToList();
        if (fixes.Count > 0 && o.Ftfy)
        {
            int applied = 0;
            foreach (var group in fixes.GroupBy(x => x.Module))
                foreach (var fix in Ftfy.ApplyToFile(group.Key.Path, group.Select(x => x.Fix), Path.Combine(intDir, "ftfy")))
                {
                    Step("ftfy", fix.ToString());
                    applied++;
                }
            if (applied > 0)
            {
                Step("ftfy", $"fixed {applied} thing(s) in your source. changed your mind? vibec undo {Path.GetFileName(o.SourcePath)}");
                (modules, _) = ModuleLoader.Load(o.SourcePath);
                analysis = ProjectAnalysis.Run(modules, knowledge, final: true);
            }
        }
        else foreach (var (_, fix) in fixes) log.WriteLine($"  warning: {fix}. left as written (--no-ftfy)");

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
        // lowering writes the IL once; it's target-neutral, so any target can reuse it
        var ilPath = Path.Combine(intDir, $"{name}.il.md");
        var printed = analysis.PrintedText;
        var hash = Knowledge.Hash(string.Join("\n---\n", PromptPass.Version, annotated, library ?? "", PromptPass.SemanticsFor(analysis.Features)));
        var cached = !o.Fresh && File.Exists(ilPath) ? TryParseIl(File.ReadAllText(ilPath)) : null;
        IlFile il;
        if (cached?.Hash == hash)
        {
            il = cached;
            Step("lowering", $"reusing {Path.GetRelativePath(Environment.CurrentDirectory, ilPath)} (your rules haven't changed)");
        }
        else
        {
            var written = await Indicator.Run("lowering", () => new PromptPass(provider).WriteAsync(annotated, analysis.Features, library, printed, m => Step("lowering", m), ct));
            Step("lowering", $"IL ready ({written.OutputTokens} tokens{(written.Revisions > 0 ? $", {written.Revisions} revision(s)" : "")})");
            if (written.Missing.Count > 0)
                log.WriteLine($"  warning: the IL still leaves out: {string.Join(", ", written.Missing.Select(m => $"\"{m}\""))}");
            il = new IlFile(Path.GetFileName(o.SourcePath), hash, printed, written.Prompt);
            Intermediate(ilPath, il.Render());
        }

        return await BackendAsync(il, name, target, toolchain, intDir, srcDir, binDir, o, ct);
    }

    /// `vibec greeter.il.md -t rust`: compile an IL directly, no Vibe needed.
    async Task<int> CompileIlAsync(CompileOptions o, Recipe target, Toolchain? toolchain, string intDir, string srcDir, string binDir, CancellationToken ct)
    {
        var il = IlFile.Parse(File.ReadAllText(o.SourcePath));
        Step("IL", $"{Path.GetFileName(o.SourcePath)} (written from {il.Source})");
        return await BackendAsync(il, IlFile.NameOf(o.SourcePath), target, toolchain, intDir, srcDir, binDir, o, ct);
    }

    /// Rebuilds pinned code if it's for this exact IL and target. Null means: generate it.
    CodegenPass.Result? UsePin(Pin? pin, string hash, Recipe target, Toolchain? toolchain, string srcDir, string name, IReadOnlyList<string> printed)
    {
        if (pin == null) return null;
        if (pin.Hash != hash) { Step("backend", "the IL or the target changed since the code was pinned; writing it again"); return null; }

        var when = pin.Created.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        if (toolchain == null)
        {
            File.WriteAllText(Path.Combine(srcDir, target.FileName), pin.Code);
            Step("backend", $"pinned code from {when} (no model call)");
            return new CodegenPass.Result(pin.Code, pin.Notes, Built: false, Verified: false, Attempts: 0, null, "");
        }

        var build = Toolchains.Build(target, toolchain, pin.Code, srcDir, name);
        if (!build.Ok) { Step("backend", $"the pinned code doesn't build with {toolchain.Display} any more; writing it again"); return null; }
        Step("backend", $"pinned code from {when}, rebuilt with {toolchain.Display} (no model call)");
        return new CodegenPass.Result(pin.Code, pin.Notes, Built: true, Verified: true, Attempts: 0, null, "");
    }

    static IlFile? TryParseIl(string text)
    {
        try { return IlFile.Parse(text); } catch (VibeError) { return null; }
    }

    async Task<int> BackendAsync(IlFile il, string name, Recipe target, Toolchain? toolchain, string intDir, string srcDir, string binDir, CompileOptions o, CancellationToken ct)
    {
        var prompt = CodegenPass.BackendPrompt(il.Body, target, toolchain);
        Intermediate(Path.Combine(intDir, $"{name}.{target.Target}.prompt.md"), prompt);
        var printed = il.OutputText;

        if (Directory.Exists(srcDir)) Directory.Delete(srcDir, recursive: true);
        Directory.CreateDirectory(srcDir);

        // same IL, same target: the pinned code, rebuilt, with no model call
        var pinPath = Path.Combine(intDir, $"{name}.{target.Target}.lock.json");
        var pinHash = Pin.HashFor(il.Body, target);
        var result = o.Fresh ? null : UsePin(Pin.Load(pinPath), pinHash, target, toolchain, srcDir, name, printed);

        if (result == null)
        {
            result = await new CodegenPass(provider).RunAsync(prompt, target, toolchain, o.MaxRepairs, srcDir, name, printed, m => Step("backend", m), ct);
            if (result.Missing is { Count: > 0 } stillMissing)
                log.WriteLine($"  warning: the generated code still doesn't contain this output text: {string.Join(", ", stillMissing.Select(m => $"\"{m}\""))}");
            Intermediate(Path.Combine(intDir, $"{name}.{target.Target}.transcript.md"), result.Transcript);

            // only code that built (or couldn't be built here) and says everything it should gets pinned
            if ((result.Built || !result.Verified) && result.Missing is not { Count: > 0 })
            {
                new Pin(pinHash, target.Target, target.FileName, result.Code, result.Notes, DateTimeOffset.Now).Save(pinPath);
                Step("pinned", $"{Path.GetRelativePath(Environment.CurrentDirectory, pinPath)}: same rules, same code, from now on");
            }
        }

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
