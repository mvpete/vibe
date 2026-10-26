namespace Vibec.Tests;

public class IncDecTests
{
    [Theory]
    [InlineData("int i = 0\nprint i++", "print(i++)")]
    [InlineData("int i = 0\nprint ++i", "print(++i)")]
    [InlineData("int i = 0\nprint i--", "print(i--)")]
    [InlineData("int i = 0\nprint \"${i++}\"", "print(f\"{i++}\")")]
    public void IncrementsAreValues(string src, string expected)
    {
        var (program, pass) = Vibe.Analyze(src);
        Assert.Empty(pass.Errors());
        Assert.Contains(expected, Printer.Print(program));
        Assert.Contains("incdec", pass.Features);
    }

    [Fact]
    public void OnItsOwnItsStillAStatement()
    {
        var (program, _) = Vibe.Analyze("int i = 0\ni++");
        Assert.IsType<Assign>(program[1]);
    }

    [Fact]
    public void LeftoversInAnInterpolationAreAnError()
    {
        var ex = Assert.Throws<VibeError>(() => Vibe.Parse("print \"${a ]}\""));
        Assert.Contains("isn't a single expression", ex.Message);
    }

    [Theory]
    [InlineData("const int i = 0\nprint i++", "is const")]
    [InlineData("string s = \"a\"\nprint s++", "can't ++ a string")]
    [InlineData("print 5++", "needs a variable")]
    public void IncrementErrors(string src, string message)
    {
        var (_, pass) = Vibe.Analyze(src);
        Assert.Contains(pass.Errors(), e => e.Contains(message));
    }

    const string Snippet = """
        use http
        json res = http.get "https://example.com"
        for item : res {
            say "${i++}${item.ticker} : ${item.sentiment}"
        }
        """;

    [Fact]
    public void AnUndeclaredCounterIsDeclaredBeforeItsLoop()
    {
        var (_, pass) = Vibe.Analyze(Snippet);
        var fix = Assert.Single(pass.Fixes);
        Assert.True(fix.Insert);
        Assert.Equal(3, fix.Line);                 // the for line, not the say inside it
        Assert.Equal("int i = 0", fix.New);
    }

    [Fact]
    public void TheSnippetFromTheChatComesOutRight()
    {
        var src = Snippet.Replace("\r", "");
        var (_, pass) = Vibe.Analyze(src);
        var (fixedSrc, applied) = Ftfy.Apply(src, pass.Fixes);
        Assert.Single(applied);
        Assert.Contains("int i = 0\nfor item : res {", fixedSrc);

        var (program, after) = Vibe.Analyze(fixedSrc);
        Assert.Empty(after.Errors());
        Assert.Empty(after.Fixes);
        Assert.Contains("print(f\"{i++}{item.ticker} : {item.sentiment}\")", Printer.Print(program));
    }

    [Fact]
    public void InsertsKeepIndentationAndLaterFixesStillLandRight()
    {
        var (text, applied) = Ftfy.Apply("void f() {\n    for x in xs {\n        print lukcy\n    }\n}",
            [new Fix(2, "", "int i = 0", "counter", Insert: true), new Fix(3, "lukcy", "lucky", "typo")]);
        Assert.Equal(2, applied.Count);
        Assert.Equal("void f() {\n    int i = 0\n    for x in xs {\n        print lucky\n    }\n}", text);
    }
}
