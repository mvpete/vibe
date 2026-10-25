namespace Vibec.Tests;

public class ParserTests
{
    [Fact]
    public void CallsDoNotNeedParentheses()
    {
        var printed = Vibe.Print("""
            int add(int a, int b) { return a + b }
            int total = add 1, 2
            """);
        Assert.Contains("int total = add(1, 2)", printed);
    }

    [Fact]
    public void ParenlessCallsNest()
    {
        var printed = Vibe.Print("""
            string greet(string who) { return who }
            print greet "you"
            """);
        Assert.Contains("print(greet(\"you\"))", printed);
    }

    [Fact]
    public void OperatorsAreNotArguments()
    {
        var printed = Vibe.Print("int x = 5\nint y = x - 1");
        Assert.Contains("int y = x - 1", printed);
    }

    [Fact]
    public void ParenlessMethodCall()
    {
        Assert.Contains("xs.push(4)", Vibe.Print("list<int> xs = [1]\nxs.push 4"));
    }

    [Theory]
    [InlineData("print \"hi\"")]
    [InlineData("print(\"hi\")")]
    [InlineData("say \"hi\"")]
    [InlineData("console.log(\"hi\")")]
    public void EveryPrintSpellingIsASay(string src)
    {
        Assert.IsType<Say>(Assert.Single(Vibe.Parse(src)));
    }

    [Theory]
    [InlineData("true"), InlineData("yeah"), InlineData("nocap"), InlineData("W")]
    public void TrueAliases(string word)
    {
        var decl = Assert.IsType<Decl>(Assert.Single(Vibe.Parse($"bool b = {word}")));
        Assert.Equal(true, Assert.IsType<Lit>(decl.Init).Value);
    }

    [Theory]
    [InlineData("false"), InlineData("nah"), InlineData("cap"), InlineData("L")]
    public void FalseAliases(string word)
    {
        var decl = Assert.IsType<Decl>(Assert.Single(Vibe.Parse($"bool b = {word}")));
        Assert.Equal(false, Assert.IsType<Lit>(decl.Init).Value);
    }

    [Theory]
    [InlineData("while true { break }", "while true { ghost }")]
    [InlineData("while true { continue }", "while true { nvm }")]
    [InlineData("throw \"x\"", "yeet \"x\"")]
    [InlineData("exit", "periodt")]
    [InlineData("assert 1 < 2", "fr 1 < 2")]
    [InlineData("const int x = 1", "based int x = 1")]
    [InlineData("global int x = 1", "rentfree int x = 1")]
    [InlineData("try { } catch (e) { }", "try { } sus (e) { }")]
    public void GenZSpellingsMeanTheSameThing(string standard, string genZ)
    {
        Assert.Equal(Vibe.Print(standard), Vibe.Print(genZ));
    }

    [Fact]
    public void GlowupIncrements()
    {
        Assert.Contains("x += 1", Vibe.Print("int x = 0\nglowup x"));
    }

    [Fact]
    public void SwitchWithDefault()
    {
        var sw = Assert.IsType<Switch>(Assert.Single(Vibe.Parse("""
            vibeshift mood {
                "great", "amazing" => print "slay"
                _ => print "ok"
            }
            """)));
        Assert.Equal(2, sw.Arms.Count);
        Assert.Equal(2, sw.Arms[0].Patterns.Count);
        Assert.Empty(sw.Arms[1].Patterns);
    }

    [Fact]
    public void SwitchPatternNamesAreNotLambdas()
    {
        var sw = Assert.IsType<Switch>(Assert.Single(Vibe.Parse("vibeshift x {\n  y => print 1\n}")));
        Assert.IsType<Ident>(Assert.Single(sw.Arms[0].Patterns));
    }

    [Fact]
    public void InlinePromptOnItsOwnLineIsAStep()
    {
        Assert.IsType<PromptStmt>(Assert.Single(Vibe.Parse("%say goodbye%")));
    }

    [Fact]
    public void LeadingPromptsAreGlobal()
    {
        var program = Vibe.Parse("#prompt be nice\nprint 1\n#prompt later");
        Assert.True(((PromptStmt)program[0]).Global);
        Assert.False(((PromptStmt)program[2]).Global);
    }

    [Fact]
    public void ElseCanSitOnTheNextLine()
    {
        var stmt = Assert.IsType<If>(Assert.Single(Vibe.Parse("if true {\n  print 1\n}\nelse {\n  print 2\n}")));
        Assert.NotNull(stmt.Else);
    }

    [Fact]
    public void TryWithoutCatchIsAnError()
    {
        Assert.Throws<VibeError>(() => Vibe.Parse("try { print 1 }"));
    }
}
