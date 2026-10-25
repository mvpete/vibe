using System.Text.Json.Nodes;

namespace Vibec.Tests;

static class Vibe
{
    public static List<Stmt> Parse(string src) => Parser.ParseSource(src);

    /// Runs the static pass with an optional set of pre-answered questions.
    public static (List<Stmt> Program, StaticPass Pass) Analyze(string src, Knowledge? knowledge = null, bool final = false)
    {
        var program = Parse(src);
        var pass = new StaticPass(knowledge ?? new Knowledge(null)) { Final = final };
        pass.Run(program);
        return (program, pass);
    }

    public static string Print(string src)
    {
        var (program, pass) = Analyze(src);
        return Printer.Print(program.Concat(pass.Generated));
    }

    public static Knowledge Knows(params (string Key, Answer Answer)[] facts)
    {
        var k = new Knowledge(null);
        foreach (var (key, answer) in facts) k.Set(key, answer);
        return k;
    }

    public static Answer Value(JsonNode? value, string? why = null) => new() { Ok = true, Value = value, Why = why };

    public static IEnumerable<string> Errors(this StaticPass pass) => pass.Diagnostics.Where(d => d.IsError).Select(d => d.Message);
    public static IEnumerable<string> Warnings(this StaticPass pass) => pass.Diagnostics.Where(d => !d.IsError).Select(d => d.Message);
}
