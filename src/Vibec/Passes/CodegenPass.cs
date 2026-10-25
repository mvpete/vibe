using System.Text;
using System.Text.Json.Nodes;
using Vibec.Providers;

namespace Vibec.Passes;

/// Final pass. Sends the IR to the model, builds what comes back with the real toolchain, and
/// feeds compiler errors back as follow-up turns until it builds or stops improving.
public sealed class CodegenPass(IModelProvider provider)
{
    const string System = """
        You are a meticulous code generator. Implement the specification you are given exactly,
        as one complete, compilable source file. Follow every constraint. No placeholders, no TODOs,
        no omitted sections.

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

    public sealed record Result(string Code, string? Notes, bool Built, bool Verified, int Attempts, string? Errors, string Transcript);

    public async Task<Result> RunAsync(string ir, Recipe target, Toolchain? toolchain, int maxRepairs, string workDir, string name, Action<string> log, CancellationToken ct)
    {
        var turns = new List<ChatTurn> { new(Role.User, ir) };
        var transcript = new StringBuilder($"# System\n\n{System}\n\n# User (IR)\n\n{ir}\n");
        string? lastErrors = null;
        string code = "", notes = "";

        for (int attempt = 1; ; attempt++)
        {
            var request = new ModelRequest(System, turns, new JsonSchemaSpec("generated_file", Schema), Effort.High, MaxOutputTokens: 64000);
            var (json, reply) = await Structured.AskJsonAsync(provider, request, ct);
            code = json["code"]?.GetValue<string>() ?? "";
            notes = json["notes"]?.GetValue<string>() ?? "";
            transcript.Append($"\n# Assistant (attempt {attempt})\n\n```{target.Fence}\n{code}\n```\n");
            if (notes.Length > 0) transcript.Append($"\nNotes: {notes}\n");
            log($"attempt {attempt}: {code.Split('\n').Length} lines ({reply.OutputTokens} tokens)");

            if (toolchain == null)
            {
                File.WriteAllText(Path.Combine(workDir, target.FileName), code);
                return new Result(code, notes, Built: false, Verified: false, attempt, null, transcript.ToString());
            }

            var build = Toolchains.Build(target, toolchain, code, workDir, name);
            if (build.Ok)
            {
                log($"attempt {attempt}: builds with {toolchain.Display}");
                return new Result(code, notes, Built: true, Verified: true, attempt, null, transcript.ToString());
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
            // append-only: the model sees its own attempt next to the compiler's verdict
            turns.Add(new ChatTurn(Role.Assistant, json.ToJsonString()));
            turns.Add(new ChatTurn(Role.User, $"""
                The build failed with {toolchain.Display}:

                ```
                {errors}
                ```

                Fix every error and return the full corrected file in the same JSON format.
                Keep the behaviour identical to the specification.
                """));
        }
    }
}
