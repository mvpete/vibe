namespace Vibec;

/// One .vibe file in a project. The entry is the file you compile; the rest are found through `use`.
public sealed class ProjectModule(string name, string path, string source, bool isEntry)
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    public string Source { get; } = source;
    public bool IsEntry { get; } = isEntry;
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// Follows `use` from the entry file. `use storage` means storage.vibe next to the entry file;
/// failing that, the standard library. Returns modules in dependency order, entry last.
public static class ModuleLoader
{
    public static (List<ProjectModule> Modules, List<string> Warnings) Load(string entryPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(entryPath))!;
        var order = new List<ProjectModule>();
        var loaded = new HashSet<string>();
        var stack = new List<string>();
        var warnings = new List<string>();

        void Visit(string name, string path, bool isEntry)
        {
            int cycleStart = stack.IndexOf(name);
            if (cycleStart >= 0)
                throw new VibeError($"circular vibes: {string.Join(" uses ", stack.Skip(cycleStart).Append(name))}");
            if (!loaded.Add(name)) return;

            var source = File.ReadAllText(path);
            var module = new ProjectModule(name, path, source, isEntry);
            List<Stmt> program;
            try { program = Parser.ParseSource(source); }
            catch (VibeError ex) { throw new VibeError($"{module.FileName} {ex.Message}"); }

            stack.Add(name);
            foreach (var use in program.OfType<UseStmt>())
            {
                var file = Path.Combine(dir, use.Module + ".vibe");
                if (File.Exists(file))
                {
                    if (StdLib.Get(use.Module) != null)
                        warnings.Add($"{module.FileName} line {use.Line}: {use.Module}.vibe shadows the standard library's {use.Module}");
                    Visit(use.Module, file, isEntry: false);
                }
                else if (StdLib.Get(use.Module) == null)
                {
                    throw new VibeError($"{module.FileName} line {use.Line}: can't find module '{use.Module}'. " +
                                        $"there's no {use.Module}.vibe in {dir}, and the standard library has: {string.Join(", ", StdLib.Names)}");
                }
            }
            stack.RemoveAt(stack.Count - 1);
            order.Add(module);
        }

        Visit(Path.GetFileNameWithoutExtension(entryPath), Path.GetFullPath(entryPath), isEntry: true);
        return (order, warnings);
    }
}
