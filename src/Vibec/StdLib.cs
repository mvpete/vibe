using System.Reflection;

namespace Vibec;

/// A module's face to the modules that use it: what it's for, its functions and variables
/// (with their visibility), and whether it's the standard library.
public sealed class ModuleInfo(string name, string prompt, Dictionary<string, FnDecl> functions, Dictionary<string, Decl> variables, bool isStd)
{
    public string Name { get; } = name;
    public string Prompt { get; } = prompt;
    public Dictionary<string, FnDecl> Functions { get; } = functions;
    public Dictionary<string, Decl> Variables { get; } = variables;
    public bool IsStd { get; } = isStd;

    public IEnumerable<string> PublicNames =>
        Functions.Values.Where(f => f.Public).Select(f => f.Name)
            .Concat(Variables.Values.Where(v => v.Public).Select(v => v.Name))
            .Order();

    static string PromptOf(List<Stmt> program) =>
        string.Join("\n", program.OfType<PromptStmt>().Where(p => p.Global).Select(p => p.Text));

    /// A project module, after its static pass (so variable types are resolved).
    public static ModuleInfo FromProject(string name, List<Stmt> program, IEnumerable<FnDecl> generated)
    {
        var functions = new Dictionary<string, FnDecl>();
        foreach (var f in program.OfType<FnDecl>().Concat(generated)) functions.TryAdd(f.Name, f);
        var variables = new Dictionary<string, Decl>();
        foreach (var d in program.OfType<Decl>()) variables.TryAdd(d.Name, d);
        return new ModuleInfo(name, PromptOf(program), functions, variables, isStd: false);
    }

    public static ModuleInfo FromStd(string name, List<Stmt> program) =>
        new(name, PromptOf(program), program.OfType<FnDecl>().Where(f => f.Public).ToDictionary(f => f.Name), [], isStd: true);
}

/// Vibe's standard library: contracts written in Vibe (src/Vibec/std/*.vibe, embedded), with
/// `native` functions the backend implements in the target. Only used through `use`.
public static class StdLib
{
    static readonly Dictionary<string, ModuleInfo?> Cache = [];

    public static IEnumerable<string> Names =>
        Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(n => n.StartsWith("std.") && n.EndsWith(".vibe"))
            .Select(n => n[4..^5])
            .Order();

    public static string? Source(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"std.{name}.vibe");
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static ModuleInfo? Get(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;
            var src = Source(name);
            var module = src == null ? null : ModuleInfo.FromStd(name, Parser.ParseSource(src));
            Cache[name] = module;
            return module;
        }
    }
}
