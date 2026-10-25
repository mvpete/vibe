using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vibec.Providers;

namespace Vibec.Passes;

/// Asks the model how to find and drive a target's toolchain on this machine. The answer is
/// validated, shown to the user once for approval, and cached in ~/.vibec/toolchains/.
public sealed class RecipePass(IModelProvider provider, TextWriter log)
{
    const string System = """
        You configure toolchains for vibec, a compiler whose output is a single source file in some
        target language. Given a target name and the host machine, describe how to detect the
        toolchain and how to compile-check one generated file with it.

        Rules:
        - `filename`: the conventional single-file name (main.rs, Program.cs, index.html, main.go...).
        - `candidates`: the likely ways this toolchain is installed on this OS, most common first.
          Each has its own `probe` (prints the version, e.g. "rustc --version"), a `versionRegex`
          whose group 1 captures a dotted version like 1.79, a `minVersion` for the language
          features you'll require, and a `build` command using that same program.
        - Commands run directly, never through a shell: one program plus arguments. No pipes,
          redirects, &&, ;, env vars, or globbing. Quote arguments with spaces using "double quotes".
        - `build` must check the file compiles (or parses, for interpreted languages) without
          needing network access or package downloads, and exit non-zero on errors. Use null only
          if nothing reasonable exists.
        - `build` should produce the real deliverable (an executable, a bundle...), not just a
          syntax check, when the language has one.
        - `output`: the path the build produces, relative to the build folder - a file (e.g. "{out}")
          or a folder (e.g. "out" for `dotnet build -o out`). It gets copied to the user's bin folder,
          so it must be everything needed to run: if the executable needs companion files (runtime
          config, DLLs, shared libraries, assets), give the folder, not the executable.
        - Target the toolchain that is actually installed: use {major}/{minor} for framework or
          language versions (e.g. net{major}.0) instead of hardcoding an older one.
          Use null when the source file itself is the deliverable (Python, JavaScript, HTML...).
        - Placeholders: {name} (the program's name; name projects and binaries after it),
          {file} (the source file), {out} (the executable name, {name}.exe on Windows),
          {major}/{minor} (the detected toolchain version).
        - `files`: extra files the build needs next to the source (e.g. a .csproj for C#), with
          full contents. Otherwise empty.
        - `errorFilter`: a regex selecting the error lines from build output, or null.
        - `constraints`: 2-4 short rules for the code generator (single file, standard library
          only, language version, how print/input should work for this target).
        - `fence`: the Markdown code-fence language tag.
        """;

    static readonly JsonObject Schema = (JsonObject)JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "language": { "type": "string" },
            "filename": { "type": "string" },
            "fence": { "type": "string" },
            "installHint": { "type": "string" },
            "constraints": { "type": "array", "items": { "type": "string" } },
            "candidates": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "probe": { "type": "string" },
                  "versionRegex": { "type": "string" },
                  "minVersion": { "type": "string" },
                  "build": { "type": ["string", "null"] },
                  "errorFilter": { "type": ["string", "null"] },
                  "output": { "type": ["string", "null"] },
                  "files": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "properties": { "path": { "type": "string" }, "content": { "type": "string" } },
                      "required": ["path", "content"],
                      "additionalProperties": false
                    }
                  }
                },
                "required": ["probe", "versionRegex", "minVersion", "build", "errorFilter", "output", "files"],
                "additionalProperties": false
              }
            }
          },
          "required": ["language", "filename", "fence", "installHint", "constraints", "candidates"],
          "additionalProperties": false
        }
        """)!;

    static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };

    /// Returns an approved recipe for `target`, discovering it if needed. Null if the user declines.
    public async Task<Recipe?> GetAsync(string target, bool refresh, bool assumeYes, CancellationToken ct)
    {
        var recipe = refresh ? null : Toolchains.Load(target);
        if (recipe is { Approved: true }) return recipe;

        if (recipe == null)
        {
            log.WriteLine($"  {"toolchain",-12} first time targeting '{target}'; consulting the backend ({provider.Name}/{provider.Model})");
            recipe = await DiscoverAsync(target, ct);
        }

        if (!Approve(recipe, assumeYes)) return null;
        recipe.Approved = true;
        Toolchains.Save(recipe);
        log.WriteLine($"  {"toolchain",-12} recipe saved → {Toolchains.PathFor(target)}");
        return recipe;
    }

    async Task<Recipe> DiscoverAsync(string target, CancellationToken ct)
    {
        var user = $"""
            Target: {target}
            OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})
            Executable suffix: {(OperatingSystem.IsWindows() ? ".exe" : "(none)")}
            """;

        var turns = new List<ChatTurn> { new(Role.User, user) };
        for (int attempt = 0; ; attempt++)
        {
            var (json, _) = await Structured.AskJsonAsync(provider, new ModelRequest(System, turns, new JsonSchemaSpec("toolchain_recipe", Schema), Effort.Low), ct);
            var recipe = json.Deserialize<Recipe>(ReadOpts) ?? throw new ModelException("the toolchain recipe came back empty");
            recipe.Target = target;
            recipe.WrittenBy = $"{provider.Name}/{provider.Model}";
            recipe.Approved = false;
            recipe.Schema = Recipe.CurrentSchema;

            var problem = Toolchains.Validate(recipe);
            if (problem == null) return recipe;
            if (attempt == 1) throw new ModelException($"the toolchain recipe for '{target}' is unusable: {problem}");

            turns.Add(new ChatTurn(Role.Assistant, json.ToJsonString()));
            turns.Add(new ChatTurn(Role.User, $"That recipe is invalid: {problem}. Fix it and return the whole recipe."));
        }
    }

    bool Approve(Recipe recipe, bool assumeYes)
    {
        log.WriteLine($"\n  vibec wants to run these commands for '{recipe.Target}' ({recipe.Language}), written by {recipe.WrittenBy}:");
        foreach (var c in recipe.Candidates)
        {
            log.WriteLine($"    detect: {c.Probe}");
            log.WriteLine($"    build:  {c.Build ?? "(none, output stays unverified)"}");
            if (c.Output != null) log.WriteLine($"    output: {c.Output} → bin/");
            foreach (var f in c.Files) log.WriteLine($"            + writes {f.Path} in the build folder");
        }
        log.WriteLine($"    (commands run without a shell, inside .vibe-int/src/; you can edit {Toolchains.PathFor(recipe.Target)} later)");

        if (assumeYes) { log.WriteLine("  approved (--yes)\n"); return true; }
        if (Console.IsInputRedirected)
        {
            log.WriteLine("  can't ask for approval without a terminal. rerun with --yes to approve.\n");
            return false;
        }
        log.Write("  approve? [y/N] ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        log.WriteLine();
        return answer is "y" or "yes";
    }
}
