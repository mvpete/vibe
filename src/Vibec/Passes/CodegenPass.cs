using System.Text;
using System.Text.Json.Nodes;
using Vibec.Providers;

namespace Vibec.Passes;

/// Final pass. Sends the IR to the model, builds what comes back with the real toolchain, and
/// feeds compiler errors back as follow-up turns until it builds or stops improving.
public sealed class CodegenPass(IModelProvider provider)
{
    const string System = """
        You are a meticulous code generator. You get a target and a language-neutral specification.
        Implement the specification exactly, in the target, as one complete, compilable source file. Follow every constraint. No placeholders, no TODOs,
        no omitted sections.

        Add nothing. No control flow, exits, breaks, validation, null or end-of-input checks, error
        handling, logging or features beyond what the specification states. Where it is silent,
        choose the behaviour that adds the least. A faithful program that seems unwise is correct;
        a "safer" program that behaves differently is wrong.

        Keep the structure. Translate statement by statement: every variable, branch, loop, function,
        throw and catch in the specification exists in the code and runs at runtime. Never pre-compute
        what the program will do and hardcode the result, never inline a value a variable will hold
        (such as a caught exception's message), and never remove code that looks redundant.

        Return JSON: `filename` (the requested output file name), `code` (the entire file), and
        `notes` (one or two sentences on any judgment calls; empty string if none).
        """;

    static readonly JsonObject Schema = (JsonObject)JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "filename": { "type": "string" },
            "code": { "type": "string" },
            "notes": { "type": "string" }
          },
          "required": ["filename", "code", "notes"],
          "additionalProperties": false
        }
        """)!;

    /// Bump when the backend's instructions change, so pinned code is regenerated.
    public const string Version = "codegen-1";

    /// The backend prompt: the IL plus the target. Written by the compiler, not a model.
    public static string BackendPrompt(string il, Recipe target, Toolchain? toolchain) => $"""
        # Target

        Language: {target.Language}
        Output file: {target.FileName}
        Toolchain: {toolchain?.Display ?? "not installed; the code won't be compile-checked, so be conservative"}
        Constraints:
        {string.Join("\n", target.Constraints.Select(c => "- " + c))}

        Map the specification's neutral terms (standard output, standard error, input lines, exit codes,
        files, HTTP, time) onto this target's standard facilities.

        ---

        {il.Trim()}
        """;

    public sealed record Result(string Code, string? Notes, bool Built, bool Verified, int Attempts, string? Errors, string Transcript, IReadOnlyList<string>? Missing = null);

    /// requiredText: every string the program prints. Code that builds but leaves any of them out
    /// isn't the program, so it goes back for repair like a build error.
    public async Task<Result> RunAsync(string ir, Recipe target, Toolchain? toolchain, int maxRepairs, string workDir, string name,
        IReadOnlyList<string> requiredText, Action<string> log, CancellationToken ct)
    {
        var turns = new List<ChatTurn> { new(Role.User, ir) };
        var transcript = new StringBuilder($"# System\n\n{System}\n\n# User (IR)\n\n{ir}\n");
        string? lastErrors = null;
        string code = "", notes = "";

        for (int attempt = 1; ; attempt++)
        {
            var request = new ModelRequest(System, turns, new JsonSchemaSpec("generated_file", Schema), Effort.High, MaxOutputTokens: 64000);
            var (json, reply) = await Indicator.Run("backend", () => Structured.AskJsonAsync(provider, request, ct));
            code = json["code"]?.GetValue<string>() ?? "";
            notes = json["notes"]?.GetValue<string>() ?? "";
            transcript.Append($"\n# Assistant (attempt {attempt})\n\n```{target.Fence}\n{code}\n```\n");
            if (notes.Length > 0) transcript.Append($"\nNotes: {notes}\n");
            log($"attempt {attempt}: {code.Split('\n').Length} lines ({reply.OutputTokens} tokens)");

            var missing = requiredText.Where(t => !ProjectAnalysis.ContainsText(code, t)).ToList();
            var missingNote = missing.Count == 0 ? "" :
                "The program prints these, but the code doesn't contain them, so it can't produce the program's output: " +
                string.Join(", ", missing.Select(m => $"\"{m}\"")) + ". Every print in the specification must be in the code, with its exact text.";
            if (missing.Count > 0) log($"attempt {attempt}: {missing.Count} piece(s) of output text missing ({string.Join(", ", missing.Take(4).Select(m => $"\"{m}\""))}{(missing.Count > 4 ? ", ..." : "")})");

            if (toolchain == null)
            {
                File.WriteAllText(Path.Combine(workDir, target.FileName), code);
                if (missing.Count == 0 || attempt > maxRepairs)
                    return new Result(code, notes, Built: false, Verified: false, attempt, null, transcript.ToString(), missing);
                Repair(json, missingNote);
                continue;
            }

            var build = Toolchains.Build(target, toolchain, code, workDir, name);
            if (build.Ok)
            {
                log($"attempt {attempt}: builds with {toolchain.Display}");
                if (missing.Count == 0 || attempt > maxRepairs)
                    return new Result(code, notes, Built: true, Verified: true, attempt, null, transcript.ToString(), missing);
                transcript.Append($"\n# Missing output text (attempt {attempt})\n\n{missingNote}\n");
                Repair(json, missingNote);
                continue;
            }

            var errors = build.Errors;
            var errorCount = errors.Split('\n').Length;
            log($"attempt {attempt}: {errorCount} error line(s)");
            transcript.Append($"\n# Build errors (attempt {attempt})\n\n```\n{errors}\n```\n");

            if (errors == lastErrors)
            {
                log("same errors twice; not converging, giving up");
                return new Result(code, notes, Built: false, Verified: true, attempt, errors, transcript.ToString());
            }
            if (attempt > maxRepairs)
                return new Result(code, notes, Built: false, Verified: true, attempt, errors, transcript.ToString());

            lastErrors = errors;
            Repair(json, $"""
                The build failed with {toolchain.Display}:

                ```
                {errors}
                ```
                {missingNote}
                """);
        }

        // append-only: the model sees its own attempt next to the verdict
        void Repair(JsonObject attempt, string problem)
        {
            turns.Add(new ChatTurn(Role.Assistant, attempt.ToJsonString()));
            turns.Add(new ChatTurn(Role.User, $"""
                {problem.Trim()}

                Fix it and return the full corrected file in the same JSON format.
                Keep the behaviour identical to the specification.
                """));
        }
    }
}
