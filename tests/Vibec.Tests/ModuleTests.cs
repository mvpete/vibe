namespace Vibec.Tests;

public class ModuleTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "vibec-modules-" + Guid.NewGuid().ToString("N"));

    public ModuleTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string Write(string name, string source)
    {
        var path = Path.Combine(_dir, name + ".vibe");
        File.WriteAllText(path, source);
        return path;
    }

    ProjectAnalysis Analyze(string entry, Knowledge? knowledge = null)
    {
        var (modules, _) = ModuleLoader.Load(Path.Combine(_dir, entry + ".vibe"));
        return ProjectAnalysis.Run(modules, knowledge ?? new Knowledge(null), final: false);
    }

    static IEnumerable<string> Errors(ProjectAnalysis a) => a.Diagnostics.Where(d => d.IsError).Select(d => d.ToString());

    [Fact]
    public void LoadsDependenciesFirstEntryLast()
    {
        Write("a", "use b\nprint b.x");
        Write("b", "use c\npublic int x = c.y");
        Write("c", "public int y = 1");
        var (modules, _) = ModuleLoader.Load(Path.Combine(_dir, "a.vibe"));
        Assert.Equal(["c", "b", "a"], modules.Select(m => m.Name));
        Assert.True(modules[^1].IsEntry);
    }

    [Fact]
    public void CyclesAreRejected()
    {
        Write("game", "use ui\npublic int x = 1");
        Write("ui", "use game\npublic int y = 1");
        Write("main", "use game");
        var ex = Assert.Throws<VibeError>(() => ModuleLoader.Load(Path.Combine(_dir, "main.vibe")));
        Assert.Contains("circular vibes: game uses ui uses game", ex.Message);
    }

    [Fact]
    public void MissingModulesAreErrors()
    {
        Write("main", "use nowhere");
        var ex = Assert.Throws<VibeError>(() => ModuleLoader.Load(Path.Combine(_dir, "main.vibe")));
        Assert.Contains("can't find module 'nowhere'", ex.Message);
    }

    [Fact]
    public void CrossModuleCallsAreTypeChecked()
    {
        Write("math2", "public int add(int a, int b = 2) { return a + b }");
        Write("main", "use math2\nint x = math2.add 1\nint y = math2.add 1, b: 3\nstring s = math2.add(1)");
        var a = Analyze("main");
        Assert.Empty(Errors(a));
        Assert.Contains("string s = vibe<string>(math2.add(1))", a.Annotated().Replace("  ", " ")
            .Split('\n').Select(l => l.Split("    #")[0].Trim()).Where(l => l.Length > 0).Aggregate((x, y) => x + "\n" + y));
    }

    [Fact]
    public void CrossModuleArgumentErrors()
    {
        Write("math2", "public int add(int a, int b) { return a + b }");
        Write("main", "use math2\nprint math2.add 1");
        Assert.Contains(Errors(Analyze("main")), e => e.Contains("main.vibe") && e.Contains("needs 'b'"));
    }

    [Fact]
    public void PrivateMembersStayPrivate()
    {
        Write("lib", "int secret() { return 1 }\nint hidden = 2\npublic int open() { return secret() }");
        Write("main", "use lib\nprint lib.open\nprint lib.secret()\nprint lib.hidden");
        var errors = Errors(Analyze("main")).ToList();
        Assert.Contains(errors, e => e.Contains("'secret' is private to lib"));
        Assert.Contains(errors, e => e.Contains("'hidden' is private to lib"));
        Assert.DoesNotContain(errors, e => e.Contains("'open'"));
    }

    [Fact]
    public void ModulesOnlyDeclare()
    {
        Write("lib", "print \"side effect\"\npublic int x = 1");
        Write("main", "use lib\nprint lib.x");
        Assert.Contains(Errors(Analyze("main")), e => e.Contains("lib.vibe") && e.Contains("can only declare things"));
    }

    [Fact]
    public void ModuleVariablesAreReadOnlyFromOutside()
    {
        Write("lib", "public int count = 1");
        Write("main", "use lib\nlib.count = 2");
        Assert.Contains(Errors(Analyze("main")), e => e.Contains("can't change lib's variables"));
    }

    [Fact]
    public void ModulesCanUseTheStandardLibrary()
    {
        Write("net", "use http\npublic string page(string url) { return http.get url timeout: 2s }");
        Write("main", "use net\nprint net.page \"https://example.com\"");
        var a = Analyze("main");
        Assert.Empty(Errors(a));
        Assert.Contains("get", a.UsedStd["http"]);
        Assert.Contains("modules", a.Features);
    }

    [Fact]
    public void ContractQuestionsCarryTheModulePrompt()
    {
        Write("storage", "#prompt keeps scores in a file\npublic void save(string name, int points)");
        Write("main", "use storage\nstorage.save \"ada\", 3");
        var q = Assert.Single(Analyze("main").Questions);
        Assert.Contains("keeps scores in a file", q.Context);
        Assert.Contains("storage", q.Context);
    }

    [Fact]
    public void ProjectFilesShadowTheStandardLibrary()
    {
        Write("http", "public string get(string url) { return url }");
        Write("main", "use http\nprint http.get \"x\"");
        var (_, warnings) = ModuleLoader.Load(Path.Combine(_dir, "main.vibe"));
        Assert.Contains(warnings, w => w.Contains("shadows the standard library"));
    }

    [Fact]
    public void TheScoresExampleChecksOut()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "examples", "scores"))) dir = Path.GetDirectoryName(dir);
        var (modules, _) = ModuleLoader.Load(Path.Combine(dir!, "examples", "scores", "main.vibe"));
        var a = ProjectAnalysis.Run(modules, new Knowledge(null), final: false);
        Assert.Empty(Errors(a));
        Assert.Equal(new[] { "game", "storage", "ui", "main" }, modules.Select(m => m.Name));
        // storage is contract-only: its two bodies are the only things left to resolve
        Assert.Equal(2, a.Questions.Count(q => q.Kind == QuestionKind.Function));
    }
}

public class ForLoopTests
{
    public static TheoryData<string> Spellings()
    {
        var data = new TheoryData<string>();
        foreach (var keyword in new[] { "for", "foreach" })
        foreach (var variable in new[] { "item", "var item", "let item", "int item" })
        foreach (var sep in new[] { "in", "of", ":" })
        {
            data.Add($"{keyword} {variable} {sep} xs {{ print item }}");
            data.Add($"{keyword} ({variable} {sep} xs) {{ print item }}");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Spellings))]
    public void EveryCombinationMeansTheSameLoop(string loop)
    {
        var (program, pass) = Vibe.Analyze($"list<int> xs = [1, 2]\n{loop}");
        Assert.Empty(pass.Errors());
        var f = Assert.IsType<For>(program[1]);
        Assert.Equal("item", f.Var);
        Assert.Equal(VType.Int, f.VarType);
    }

    [Fact]
    public void TypedLoopVariablesConvertEachElement()
    {
        var (program, pass) = Vibe.Analyze("list<string> xs = [\"1\"]\nfor (int n : xs) { print n + 1 }");
        Assert.Empty(pass.Errors());
        Assert.Contains("typed-for", pass.Features);
        Assert.Contains("for int n in xs", Printer.Print(program));
    }

    [Fact]
    public void CStyleFor()
    {
        var (program, pass) = Vibe.Analyze("for (int i = 0; i < 3; i++) { print i }");
        Assert.Empty(pass.Errors());
        Assert.IsType<CFor>(Assert.Single(program));
        Assert.Contains("for (int i = 0; i < 3; i += 1)", Printer.Print(program));
    }

    [Fact]
    public void CStyleForPartsAreOptional()
    {
        var (_, pass) = Vibe.Analyze("int i = 0\nfor (; i < 3;) { i++ }");
        Assert.Empty(pass.Errors());
    }

    [Theory]
    [InlineData("for item : res {\n    say \"${res.ticker} : ${res.sentiment}}\"\n    say newline\n}")]
    [InlineData("foreach item in res {\n    say \"${res.ticker} : ${res.sentiment}}\"\n    say newline\n}")]
    public void TheSnippetsFromTheChat(string loop)
    {
        var (program, pass) = Vibe.Analyze($"use http\njson res = http.get \"https://example.com\" timeout: 2s\n{loop}");
        Assert.Empty(pass.Errors());
        var q = Assert.Single(pass.Questions); // `newline` isn't declared: semantic resolution decides
        Assert.Equal("newline", q.Name);
        Assert.Contains("print(f\"{res.ticker} : {res.sentiment}}}\")", Printer.Print(program));
    }
}

public class VibePassPayloadTests
{
    [Fact]
    public void FunctionQuestionsSendTheirContext()
    {
        var q = new Question
        {
            Kind = QuestionKind.Function, Key = "k", Line = 1, Name = "save",
            ArgTypes = [VType.Str], Expected = VType.Void, Context = "keeps scores in a file",
        };
        Assert.Equal("keeps scores in a file", Vibec.Passes.VibePass.Describe(0, q)["context"]?.GetValue<string>());
    }

    [Fact]
    public void ContractContextListsUsableModules()
    {
        var (_, pass) = Vibe.Analyze("use fs\npublic void save(string name, int points)");
        var q = Assert.Single(pass.Questions);
        Assert.Contains("fs: public native string read(string path)", q.Context);
    }
}

public class ConversionTests
{
    [Theory]
    [InlineData("int n = int(\"42\")", "int n = 42")]
    [InlineData("float f = float(\"2.5\")", "float f = 2.5")]
    [InlineData("string s = string(7)", "string s = \"7\"")]
    [InlineData("bool b = bool(\"yes\")", "bool b = true")]
    public void TypeNamesConvert(string src, string expected)
    {
        var (program, pass) = Vibe.Analyze(src);
        Assert.Empty(pass.Errors());
        Assert.Equal(expected, Printer.Print(program).Trim());
    }

    [Fact]
    public void ConversionAtRuntime()
    {
        var (program, _) = Vibe.Analyze("string s = ask()\nint n = int(s) + 1");
        Assert.Contains("vibe<int>(s) + 1", Printer.Print(program));
    }
}

public class CacheKeyTests
{
    [Fact]
    public void ChangingAModulePromptReasksItsContracts()
    {
        var a = Assert.Single(Vibe.Analyze("#prompt keeps scores in a file\npublic void save(string name)").Pass.Questions).Key;
        var b = Assert.Single(Vibe.Analyze("#prompt keeps scores in a database\npublic void save(string name)").Pass.Questions).Key;
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void RenamedParametersAreNotAdopted()
    {
        var key = Assert.Single(Vibe.Analyze("int twice(int x)").Pass.Questions).Key;
        var answer = new Answer { Ok = true, Source = "int twice(int n) { return n * 2 }" };
        var (_, pass) = Vibe.Analyze("int twice(int x)", Vibe.Knows((key, answer)));
        Assert.Contains(pass.Warnings(), w => w.Contains("different signature"));
        Assert.Empty(pass.Errors());
    }
}
