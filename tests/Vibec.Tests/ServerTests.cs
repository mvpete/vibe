namespace Vibec.Tests;

public class ObjectLiteralTests
{
    [Theory]
    [InlineData("json j = { ok: true }", "json j = { ok: true }")]
    [InlineData("json j = { \"two words\": 1, n: [1, 2] }", "json j = { \"two words\": 1, n: [1, 2] }")]
    [InlineData("json j = {}", "json j = {}")]
    [InlineData("json j = { a: { b: null } }", "json j = { a: { b: null } }")]
    public void ObjectsPrintBack(string src, string expected)
    {
        var (program, pass) = Vibe.Analyze(src);
        Assert.Empty(pass.Errors());
        Assert.Equal(expected, Printer.Print(program).Trim());
    }

    [Fact]
    public void ObjectsCanSpanLines()
    {
        var (program, pass) = Vibe.Analyze("int n = 1\njson j = {\n    id: n,\n    title: \"x\",\n}\nprint j.id");
        Assert.Empty(pass.Errors());
        Assert.Contains("json j = { id: n, title: \"x\" }", Printer.Print(program));
    }

    [Fact]
    public void DuplicateKeysAreAnError()
    {
        Assert.Throws<VibeError>(() => Vibe.Parse("json j = { a: 1, a: 2 }"));
    }

    [Fact]
    public void BlocksAreStillBlocks()
    {
        var (_, pass) = Vibe.Analyze("bool ok = true\nif ok { print 1 }\nwhile ok { break }");
        Assert.Empty(pass.Errors());
    }
}

public class ServerTests
{
    [Fact]
    public void FunctionsPassAsHandlers()
    {
        var (program, pass) = Vibe.Analyze("use server\nstring home(json req) { return \"hi\" }\nserver.get \"/\", home\nserver.listen 8080");
        Assert.Empty(pass.Errors());
        Assert.Contains("fnref", pass.Features);
        Assert.Contains("server.get(\"/\", home)", Printer.Print(program));
        Assert.Equal(["get", "listen"], pass.UsedStd["server"]);
    }

    [Fact]
    public void LambdasPassAsHandlers()
    {
        var (_, pass) = Vibe.Analyze("use server\nserver.post \"/echo\", req => req.body");
        Assert.Empty(pass.Errors());
    }

    [Fact]
    public void AHandlerHasToBeAFunction()
    {
        var (_, pass) = Vibe.Analyze("use server\nserver.get \"/\", \"home\"");
        Assert.Contains(pass.Errors(), e => e.Contains("expected a function"));
    }

    [Fact]
    public void AFunctionNameIsStillACallElsewhere()
    {
        var (program, _) = Vibe.Analyze("int answer() { return 42 }\nprint answer");
        Assert.Contains("print(answer())", Printer.Print(program));
    }

    [Fact]
    public void TheWebserverExampleChecksOut()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "examples", "webserver"))) dir = Path.GetDirectoryName(dir);
        var (modules, _) = ModuleLoader.Load(Path.Combine(dir!, "examples", "webserver", "main.vibe"));
        var a = ProjectAnalysis.Run(modules, new Knowledge(null), final: false);
        Assert.Empty(a.Diagnostics.Where(d => d.IsError));
        Assert.Empty(a.Questions); // nothing left to vibe: it's all rules
        Assert.Contains("server", a.UsedStd.Keys);
    }
}
