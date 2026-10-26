namespace Vibec;

public sealed class AnalyzedModule(ProjectModule module, List<Stmt> program, StaticPass pass)
{
    public ProjectModule Module { get; } = module;
    public List<Stmt> Program { get; } = program;
    public StaticPass Pass { get; } = pass;
}

/// The static pass over a whole project: every module, in dependency order, each seeing the
/// modules it uses through their public members.
public sealed class ProjectAnalysis
{
    public List<AnalyzedModule> Modules { get; } = [];

    public static ProjectAnalysis Run(IReadOnlyList<ProjectModule> modules, Knowledge knowledge, bool final)
    {
        var analysis = new ProjectAnalysis();
        var checkedModules = new Dictionary<string, ModuleInfo>();
        foreach (var module in modules)
        {
            var program = Parser.ParseSource(module.Source);
            var pass = new StaticPass(knowledge)
            {
                Final = final,
                IsLibrary = !module.IsEntry,
                ModuleName = module.Name,
                ResolveModule = name => checkedModules.GetValueOrDefault(name) ?? StdLib.Get(name),
            };
            pass.Run(program);
            checkedModules[module.Name] = ModuleInfo.FromProject(module.Name, program, pass.Generated);
            analysis.Modules.Add(new AnalyzedModule(module, program, pass));
        }
        return analysis;
    }

    public bool IsMultiModule => Modules.Count > 1;

    public List<Question> Questions => Modules.SelectMany(m => m.Pass.Questions).DistinctBy(q => q.Key).ToList();

    public List<Diagnostic> Diagnostics =>
        Modules.SelectMany(m => m.Pass.Diagnostics.Select(d => IsMultiModule ? d with { File = m.Module.FileName } : d)).ToList();

    public bool HasErrors => Modules.Any(m => m.Pass.HasErrors);

    public HashSet<string> Features
    {
        get
        {
            var features = Modules.SelectMany(m => m.Pass.Features).ToHashSet();
            if (IsMultiModule) features.Add("modules");
            return features;
        }
    }

    public Dictionary<string, SortedSet<string>> UsedStd
    {
        get
        {
            var used = new Dictionary<string, SortedSet<string>>();
            foreach (var (module, functions) in Modules.SelectMany(m => m.Pass.UsedStd))
            {
                if (!used.TryGetValue(module, out var set)) used[module] = set = [];
                set.UnionWith(functions);
            }
            return used;
        }
    }

    /// Every module's source, for semantic resolution's context.
    public string Sources => IsMultiModule
        ? string.Join("\n\n", Modules.Select(m => $"// {m.Module.FileName}\n{m.Module.Source}"))
        : Modules[0].Module.Source;

    /// The annotated program. With several modules, one section per module, dependencies first.
    public string Annotated()
    {
        if (!IsMultiModule) return Printer.Print(Modules[0].Program.Concat(Modules[0].Pass.Generated));
        return string.Join("\n", Modules.Select(m =>
            $"# ===== {(m.Module.IsEntry ? "entry" : "module")} {m.Module.Name} ({m.Module.FileName}) =====\n" +
            Printer.Print(m.Program.Concat(m.Pass.Generated))));
    }
}
