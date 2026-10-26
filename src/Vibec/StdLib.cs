using System.Reflection;

namespace Vibec;

/// A module's public face: what it's for, and the functions it offers.
public sealed class ModuleInfo(string name, string prompt, Dictionary<string, FnDecl> functions)
{
    public string Name { get; } = name;
    public string Prompt { get; } = prompt;
    public Dictionary<string, FnDecl> Functions { get; } = functions;
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
            var module = src == null ? null : Load(name, src);
            Cache[name] = module;
            return module;
        }
    }

    public static ModuleInfo Load(string name, string source)
    {
        var program = Parser.ParseSource(source);
        var prompt = string.Join("\n", program.OfType<PromptStmt>().Where(p => p.Global).Select(p => p.Text));
        var functions = program.OfType<FnDecl>().Where(f => f.Public).ToDictionary(f => f.Name);
        return new ModuleInfo(name, prompt, functions);
    }
}
