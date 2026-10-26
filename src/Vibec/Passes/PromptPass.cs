using System.Text;
using Vibec.Providers;

namespace Vibec.Passes;

/// Prompt pass. Turns the annotated AST into the IR: a precise, self-contained implementation
/// prompt for the codegen model, written by a model that knows Vibe's semantics.
public sealed class PromptPass(IModelProvider provider)
{
    const string System = """
        You are the prompt pass of vibec, the compiler for Vibe, a strongly-loose typed language.
        You receive an annotated Vibe program (already type-checked; coercions resolved where
        possible, with `# notes` on the lines where the compiler intervened), the runtime semantics
        of the Vibe features it uses, and a target language with constraints.

        Write the prompt that a code-generation model will receive. That model will NOT see the Vibe
        source or know Vibe exists - your prompt is its entire world. So:
        - Translate every Vibe-specific behaviour (kinda, maybe, vibe<T>(...), %inline prompts%, #prompt
          instructions, vibecheck) into concrete, testable instructions for the target language:
          exact tolerances, exact messages, exact library choices.
        - Give every resolved constant exactly. Preserve program order, all output text verbatim,
          and the observable behaviour of every statement.
        - For deferred values (`vibe<T>("...")` marked deferred), decide the value yourself and say why.
        - Spell out the target's constraints and how its I/O maps to print/ask.
        - Mention anything the generator might trip on (unused variables, integer division, etc.).
        - Faithfulness is the first rule. The program does exactly what the Vibe program says and
          nothing more: no extra exits or breaks, no input validation, no null/end-of-input handling,
          no retries, no error messages, no features the source doesn't contain. If the source loops
          forever, the program loops forever. State this rule explicitly in the prompt you write.
        - Do not write the program itself. Write the specification.

        Output only the prompt, as Markdown, starting with a "# Task" heading.
        """;

    static readonly Dictionary<string, string> Semantics = new()
    {
        ["base"] = """
            - int is a 64-bit signed integer, float is a double, string is UTF-8 text, list<T> is a growable list.
            - print(a, b, ...) writes the arguments separated by single spaces, then a newline. Floats print in
              shortest round-trip form with at least one decimal (2.0, 0.1); bools print true/false; lists print like [1, 2, 3].
            - `+` with a string on either side concatenates, stringifying the other side as print would.
            - int / int is integer division truncating toward zero; % is remainder.
            - Any error that nothing catches prints `error: <message>` to stderr and exits with code 1. No stack traces.
            - f"...{expr}..." interpolates exactly like print would format the value.
            - Duration literals (500ms, 2s, 5m, 1h) are float seconds.
            - Named arguments (`timeout: 2s`) bind to the parameter with that name; omitted parameters take their defaults.
            """,
        ["kinda"] = "- `a kinda b` is fuzzy equality: numbers match if |a-b| <= 10% of max(|a|,|b|) (or both are 0); strings match ignoring case, surrounding whitespace and punctuation; anything else falls back to ==.",
        ["maybe"] = "- `maybe` is a fresh 50/50 random bool every time it is evaluated.",
        ["runtime-coercion"] = """
            - `vibe<T>(x)` converts a runtime value to T:
              string→int/float: trim; accept digits (with optional sign/decimal point); otherwise accept English number
              words ("seven", "twenty one", "a dozen", "one hundred"); otherwise fail.
              string→bool: yes/yeah/y/true/1 → true; no/nah/n/false/0/empty → false; otherwise fail.
              number→string: as print formats it. bool→int: 1/0. float→int: round half away from zero.
              list→string: elements joined with ", ". scalar→list<T>: a one-element list.
              A failed conversion raises an error (catchable by try/catch) whose message says what couldn't be converted.
            """,
        ["deferred"] = "- `vibe<T>(\"literal\")` marked `deferred` means the compiler couldn't interpret the literal; pick the most sensible constant of type T and state it.",
        ["vibe-expr"] = "- `%description%` is an inline prompt: implement exactly what the description says and nothing beyond it, producing the annotated type. If it reads input, it behaves like ask (including at end of input).",
        ["ask"] = "- `ask(prompt)` writes the prompt (no newline, followed by a space) and reads one line of input, without the line ending. At end of input it returns an empty string; it never stops, breaks out of, or exits the program.",
        ["vibecheck"] = "- `vibecheck cond` is an assertion: if false, write `vibe check failed: <the condition as written>` to stderr and exit with code 1.",
        ["try"] = "- `try { } catch (e) { }` catches any error raised in the block; `e` is the error's message as a string.",
        ["for-int"] = "- `for i in n` (n an int) iterates i = 0 .. n-1.",
        ["repeat"] = "- `repeat n times { }` runs the block n times.",
        ["global-prompt"] = "- `#prompt` lines at the very top are global instructions from the programmer that apply to the whole program. Follow them.",
        ["prompt"] = "- A `#prompt` statement in the middle of code is a step to perform at exactly that point, described in natural language.",
        ["generated-fn"] = "- Functions under `# generated by the vibe pass` were written by the compiler; implement them exactly like the others.",
        ["const"] = "- `const` declarations never change after initialisation; use the target's constant/readonly form.",
        ["global"] = "- `global` variables are visible everywhere, including inside functions declared before them; declare them at program/module scope.",
        ["throw"] = "- `throw x` raises an error whose message is x stringified as print would. Uncaught, it prints `error: <message>` to stderr and exits with code 1.",
        ["exit"] = "- `exit` / `exit code` ends the program immediately (code 0 if none given).",
        ["sleep"] = "- `sleep seconds` pauses for that many seconds (a float). In a browser target, await a timer instead of blocking.",
        ["switch"] = "- `switch x { a, b => ...  _ => ... }` runs the first arm with a pattern equal to x (no fallthrough); `_` is the default; no match and no default does nothing.",
        ["main"] = "- `maincharacter { }` marks the program's entry point. Top-level statements outside it still run first, in order, then it runs.",
        ["lowkey"] = "- `lowkey a, b` prints like print but to stderr: quiet diagnostics.",
        ["highkey"] = "- `highkey a, b` prints like print, but uppercased and followed by \"!!\" - it's shouting.",
        ["spill"] = "- `spill x` is a debug print: writes `<x as written> = <value>` (e.g. `total = 3`) to stderr.",
        ["assume"] = "- `assume cond` is a promise from the programmer that cond is true here. Don't check it; you may rely on it.",
        ["json"] = "- `json` holds parsed JSON (object, array, string, number, bool or null). `x.field` and `x[i]` read into it and are json too (missing → null). Converting a string to json parses it (invalid JSON raises an error); converting json to another type reads it as that type.",
        ["std"] = "- `module.function(...)` calls into Vibe's standard library. Implement each with the target's idiomatic standard library, following its contract under \"Standard library\" exactly: parameters, defaults, units, what throws.",
        ["vibed-call"] ="- Calls and members with a `# name: ...` note aren't built-ins; the note says what they do. Implement them.",
    };

    public static string SemanticsFor(IEnumerable<string> features)
    {
        var sb = new StringBuilder(Semantics["base"]);
        foreach (var f in features.Order())
            if (Semantics.TryGetValue(f, out var text)) sb.AppendLine(text);
        return sb.ToString().TrimEnd();
    }

    public async Task<(string Prompt, ModelReply Reply)> WriteAsync(
        string annotatedSource, IEnumerable<string> features, Recipe target, Toolchain? toolchain, string? library, CancellationToken ct)
    {
        var user = $"""
            ## Target
            Language: {target.Language}
            Output file: {target.FileName}
            Toolchain: {toolchain?.Display ?? "not installed; the code won't be compile-checked, so be conservative"}
            Constraints:
            {string.Join("\n", target.Constraints.Select(c => "- " + c))}

            ## Vibe semantics used by this program
            {SemanticsFor(features)}

            {(library == null ? "" : "## Standard library\n" + library + "\n")}
            ## Annotated program
            ```vibe
            {annotatedSource}
            ```
            """;

        var reply = await provider.CompleteAsync(new ModelRequest(System, [new ChatTurn(Role.User, user)], Effort: Effort.Medium), ct);
        if (reply.Refused) throw new ModelException($"the prompt pass was refused: {reply.RefusalReason ?? "no reason given"}");
        if (string.IsNullOrWhiteSpace(reply.Text)) throw new ModelException("the prompt pass came back empty");
        return (reply.Text.Trim(), reply);
    }
}
