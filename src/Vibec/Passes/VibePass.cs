using System.Text.Json;
using System.Text.Json.Nodes;
using Vibec.Providers;

namespace Vibec.Passes;

/// Pass 2. Sends every open question from the static pass to the model in one structured call,
/// validates the answers, and records them in the knowledge base.
public sealed class VibePass(IModelProvider provider)
{
    const string System = """
        You are the semantic "vibe pass" of vibec, the compiler for Vibe: a strongly-loose typed,
        imperative language flavoured like C#, Python and JavaScript. Declared types always win, and
        values get interpreted to fit them the way a sensible human would.

        You receive the program source and a list of questions the static analyser couldn't answer.
        Answer every question by id.

        Question kinds:
        - "coerce": interpret the literal `value` as type `to`. Put the result in `value` as JSON
          text: "5", "2000.0", "true", "\"hi\"", "[2,4,6]". Use program context when it helps. If the
          question has a `meaning` (e.g. "a duration in seconds"), answer in those terms: "500ms" → "0.5".
          Only set ok=false when there is genuinely no sensible reading; explain in `why`.
        - "function": the program calls a function nobody defined. Write it, in Vibe, in `source`:
          exactly one function named `name`, with parameter types matching `argTypes` and return type
          `expected` when given (otherwise choose one). Keep it short and obvious. For things only the
          final program can do (randomness, time, I/O, network) use an inline prompt %description%,
          e.g. `return %a random int from 1 to 100%`. Also set `returns` and a one-sentence `meaning`.
          If it has a `context`, follow it exactly: it gives the signature to implement, what the module
          is for, and the modules you can call. Call those modules (e.g. fs.read, fs.write) for real work
          instead of describing it with an inline prompt.
        - "member": a method/property that isn't built in. Set `returns` (a Vibe type) and a
          one-sentence `meaning` describing what it does. ok=false only if it's nonsense.
          If `module` is set, it's a call into a module the program didn't import (like http.get):
          say what it most plausibly does with its arguments; prefer `expected` for `returns`.
        - "name": an undeclared identifier. If it's clearly a typo of one of `candidates`, set
          `rename`. Otherwise, if the word has an obvious meaning where it's used (`infinity` as a
          loop condition → true, `pi` as a float → 3.14159, `nothing` as a string → ""), set `value`
          (JSON text) and `returns` (its type; must equal `expected` when given). Otherwise ok=false.

        Vibe types: int, float, string, bool, list<T>, json, vibe (dynamic), void.

        Vibe syntax, for writing functions:
            int add(int a, int b) {
                int total = a + b
                if total > 10 { return 10 } else { return total }
            }
            list<string> labels(list<int> xs) {
                return xs.map(x => f"item {x}")
            }
            string pick() { return %a random fruit name% }
        Statements: typed declarations or `var x = ...`, assignment (= += -= ++), if / else if / else,
        while, `for x in xs`, `repeat n times { }`, return, print(a, b), try { } catch (e) { }.
        Lists: [1, 2], xs.length, xs.push(x), xs.map(lambda), xs.filter(lambda). Strings: f"{x}", .upper().
        No semicolons. No classes. No imports.

        Keep `why`, `meaning` short. Use null for fields that don't apply.
        """;

    static readonly JsonObject Schema = (JsonObject)JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "answers": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "integer" },
                  "ok": { "type": "boolean" },
                  "value": { "type": ["string", "null"] },
                  "returns": { "type": ["string", "null"] },
                  "meaning": { "type": ["string", "null"] },
                  "rename": { "type": ["string", "null"] },
                  "source": { "type": ["string", "null"] },
                  "why": { "type": ["string", "null"] }
                },
                "required": ["id", "ok", "value", "returns", "meaning", "rename", "source", "why"],
                "additionalProperties": false
              }
            }
          },
          "required": ["answers"],
          "additionalProperties": false
        }
        """)!;

    public sealed record Outcome(int Resolved, int Rejected, int InputTokens, int OutputTokens);

    public async Task<Outcome> AnswerAsync(List<Question> questions, Knowledge knowledge, string source, CancellationToken ct)
    {
        var payload = new JsonArray();
        for (int i = 0; i < questions.Count; i++) payload.Add(Describe(i, questions[i]));

        var user = $"""
            Program:
            ```vibe
            {source}
            ```

            Questions:
            {payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true })}
            """;

        var request = new ModelRequest(System, [new ChatTurn(Role.User, user)], new JsonSchemaSpec("vibe_answers", Schema), Effort.Low);
        var (json, reply) = await Structured.AskJsonAsync(provider, request, ct);

        int resolved = 0, rejected = 0;
        foreach (var node in json["answers"]?.AsArray() ?? [])
        {
            if (node is not JsonObject a || a["id"]?.GetValue<int>() is not int id || id < 0 || id >= questions.Count) continue;
            var q = questions[id];
            var answer = new Answer
            {
                Ok = a["ok"]?.GetValue<bool>() ?? false,
                Returns = Str(a, "returns"),
                Meaning = Str(a, "meaning"),
                Rename = Str(a, "rename"),
                Source = Str(a, "source"),
                Why = Str(a, "why"),
            };

            if (q.Kind == QuestionKind.Name && answer.Ok && answer.Rename == null)
            {
                answer.Value = ParseValue(Str(a, "value"));
                var type = q.Expected ?? VType.Parse(answer.Returns) ?? VType.Vibe;
                if (!StaticPass.TryFromJson(answer.Value, type, out _)) answer.Ok = false;
            }
            if (q.Kind == QuestionKind.Coerce && answer.Ok)
            {
                // the model's value has to actually be a valid `to`, or it doesn't count
                answer.Value = ParseValue(Str(a, "value"));
                if (!StaticPass.TryFromJson(answer.Value, q.To!, out _))
                {
                    answer.Ok = false;
                    answer.Why = $"answered {Str(a, "value") ?? "nothing"}, which isn't a valid {q.To}";
                }
            }
            if (q.Kind == QuestionKind.Function && answer.Ok && answer.Source == null && answer.Meaning == null) answer.Ok = false;

            knowledge.Set(q.Key, answer);
            if (answer.Ok) resolved++; else rejected++;
        }
        return new Outcome(resolved, rejected, reply.InputTokens, reply.OutputTokens);
    }

    public static JsonObject Describe(int id, Question q)
    {
        var o = new JsonObject { ["id"] = id, ["kind"] = q.Kind.ToString().ToLowerInvariant(), ["line"] = q.Line };
        switch (q.Kind)
        {
            case QuestionKind.Coerce:
                o["value"] = q.Value?.DeepClone();
                o["to"] = q.To!.ToString();
                if (q.Context != null) o["meaning"] = q.Context;
                break;
            case QuestionKind.Function:
                o["name"] = q.Name;
                o["argTypes"] = new JsonArray(q.ArgTypes!.Select(t => (JsonNode)t.ToString()).ToArray());
                o["expected"] = q.Expected?.ToString();
                if (q.Context != null) o["context"] = q.Context;
                break;
            case QuestionKind.Member:
                if (q.Module != null) o["module"] = q.Module; else o["receiver"] = q.Receiver!.ToString();
                if (q.Expected != null) o["expected"] = q.Expected.ToString();
                if (q.Named is { Count: > 0 }) o["namedArgs"] = new JsonArray(q.Named.Select(n => (JsonNode)n).ToArray());
                o["name"] = q.Name;
                o["isProperty"] = q.IsProperty;
                if (q.ArgTypes != null) o["argTypes"] = new JsonArray(q.ArgTypes.Select(t => (JsonNode)t.ToString()).ToArray());
                break;
            case QuestionKind.Name:
                o["name"] = q.Name;
                o["expected"] = q.Expected?.ToString();
                o["candidates"] = new JsonArray(q.Candidates!.Select(c => (JsonNode)c).ToArray());
                break;
        }
        return o;
    }

    static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static JsonNode? ParseValue(string? text)
    {
        if (text == null) return null;
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return JsonValue.Create(text); } // a bare word: treat as a string
    }
}
