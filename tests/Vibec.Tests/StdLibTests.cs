using System.Text.Json.Nodes;

namespace Vibec.Tests;

public class StdLibTests
{
    [Fact]
    public void EveryStdModuleLoadsAndOnlyExportsNatives()
    {
        Assert.NotEmpty(StdLib.Names);
        foreach (var name in StdLib.Names)
        {
            var module = StdLib.Get(name);
            Assert.NotNull(module);
            Assert.NotEmpty(module.Prompt);
            Assert.All(module.Functions.Values, f => Assert.True(f.Native, $"{name}.{f.Name} should be native"));
        }
    }

    [Fact]
    public void UseBindsToTheContract()
    {
        var (program, pass) = Vibe.Analyze("use http\njson res = http.get \"https://bing.ca\" timeout: 2s; print res.field");
        Assert.Empty(pass.Errors());
        Assert.Empty(pass.Questions);
        Assert.Contains("get", pass.UsedStd["http"]);
        var printed = Printer.Print(program);
        Assert.Contains("http.get(\"https://bing.ca\", timeout: 2s)", printed);
        Assert.Contains("print(res.field)", printed);
    }

    [Fact]
    public void WithoutUseTheModuleIsVibed()
    {
        var (_, pass) = Vibe.Analyze("json res = http.get \"https://bing.ca\" timeout: 2s");
        var q = Assert.Single(pass.Questions);
        Assert.Equal(QuestionKind.Member, q.Kind);
        Assert.Equal("http", q.Module);
        Assert.Equal("json", q.Expected!.ToString());
        Assert.Empty(pass.UsedStd);
    }

    [Fact]
    public void AVibedModuleCallWarnsAndPointsAtUse()
    {
        var key = Knowledge.ModuleMemberKey("http", "get", [VType.Str], [], VType.Str);
        var answer = new Answer { Ok = true, Returns = "string", Meaning = "fetches the URL as text" };
        var (_, pass) = Vibe.Analyze("string s = http.get \"x\"", Vibe.Knows((key, answer)));
        Assert.Contains(pass.Warnings(), w => w.Contains("isn't imported") && w.Contains("use http"));
    }

    [Fact]
    public void AliasesWork()
    {
        var (_, pass) = Vibe.Analyze("use http as web\nstring s = web.get \"x\"");
        Assert.Empty(pass.Errors());
        Assert.Contains("get", pass.UsedStd["http"]);
    }

    [Theory]
    [InlineData("use nope", "can't find module")]
    [InlineData("use http\nhttp.get \"x\" timeut: 2s", "no parameter 'timeut'")]
    [InlineData("use http\nhttp.post \"x\"", "needs 'body'")]
    [InlineData("use http\nhttp.fetch \"x\"", "has no 'fetch'")]
    [InlineData("use http\nhttp.get \"a\", \"b\", 3, 4", "takes 2 argument(s)")]
    [InlineData("if true { use http }", "top level")]
    public void ContractViolationsAreErrors(string src, string message)
    {
        var (_, pass) = Vibe.Analyze(src);
        Assert.Contains(pass.Errors(), e => e.Contains(message));
    }

    [Fact]
    public void StdArgumentsAreStronglyLoose()
    {
        var key = Knowledge.CoerceKey(JsonValue.Create("two seconds"), VType.Float, null);
        var (program, pass) = Vibe.Analyze("use time\ntime.sleep \"two seconds\"", Vibe.Knows((key, Vibe.Value(2.0))));
        Assert.Empty(pass.Errors());
        Assert.Contains("time.sleep(2.0)", Printer.Print(program));
    }

    [Fact]
    public void ListOfVibeAcceptsAnyList()
    {
        var (program, pass) = Vibe.Analyze("use random\nlist<int> xs = [1, 2, 3]\nvibe x = random.pick xs");
        Assert.Empty(pass.Errors());
        Assert.Contains("random.pick(xs)", Printer.Print(program));
    }
}

public class LanguageAdditionTests
{
    [Theory]
    [InlineData("500ms", 0.5)]
    [InlineData("2s", 2.0)]
    [InlineData("1.5m", 90.0)]
    [InlineData("1h", 3600.0)]
    public void DurationsAreSeconds(string literal, double seconds)
    {
        var decl = Assert.IsType<Decl>(Assert.Single(Vibe.Parse($"float t = {literal}")));
        var lit = Assert.IsType<Lit>(decl.Init);
        Assert.Equal(seconds, (double)lit.Value!, 6);
        Assert.Equal(literal, lit.Display);
    }

    [Fact]
    public void NamedArgumentsAndDefaults()
    {
        var (program, pass) = Vibe.Analyze("int add(int a, int b = 2) { return a + b }\nprint add(1), add(1, b: 5), add 1 b: 7");
        Assert.Empty(pass.Errors());
        Assert.Contains("print(add(1), add(1, b: 5), add(1, b: 7))", Printer.Print(program));
    }

    [Fact]
    public void PositionalAfterNamedIsAnError()
    {
        Assert.Throws<VibeError>(() => Vibe.Parse("f(a: 1, 2)"));
    }

    [Fact]
    public void JsonFieldsAreJson()
    {
        var (_, pass) = Vibe.Analyze("json j = \"{}\"\nstring name = j.user.name\nfor x in j.items { print x }");
        Assert.Empty(pass.Errors());
        Assert.Empty(pass.Questions);
        Assert.Contains("json", pass.Features);
    }

    [Fact]
    public void ContractOnlyFunctionsAskForABody()
    {
        var (_, pass) = Vibe.Analyze("int twice(int x)\nprint twice 4");
        var q = Assert.Single(pass.Questions);
        Assert.Equal(QuestionKind.Function, q.Kind);
        Assert.Contains("int twice(int x)", q.Context);
    }

    [Fact]
    public void ContractBodiesAreAdopted()
    {
        var key = Knowledge.FunctionKey("twice", [VType.Int], VType.Int) + " contract";
        var answer = new Answer { Ok = true, Source = "int twice(int x) { return x * 2 }" };
        var (program, pass) = Vibe.Analyze("int twice(int x)\nprint twice 4", Vibe.Knows((key, answer)));
        Assert.Empty(pass.Errors());
        Assert.Contains("return x * 2", Printer.Print(program));
        Assert.Contains(pass.Warnings(), w => w.Contains("vibed one up"));
    }

    [Fact]
    public void NativeIsForTheStandardLibrary()
    {
        var (_, pass) = Vibe.Analyze("native int f()");
        Assert.Contains(pass.Errors(), e => e.Contains("standard library"));
    }

    [Fact]
    public void FlexIsPublic()
    {
        Assert.Equal(Vibe.Print("public int f() { return 1 }"), Vibe.Print("flex int f() { return 1 }"));
    }
}
