namespace Vibec.Tests;

/// The FTFY rules added after the first four: each one fires on the obvious case and backs off otherwise.
public class FtfyRuleTests
{
    static (List<Stmt> Program, StaticPass Pass, List<Fix> Fixes) Run(string src)
    {
        var fixes = new List<Fix>();
        var program = Parser.ParseSource(src, fixes);
        var pass = new StaticPass(new Knowledge(null));
        pass.Run(program);
        return (program, pass, fixes.Concat(pass.Fixes).ToList());
    }

    static string Fixed(string src)
    {
        var (_, _, fixes) = Run(src);
        return Ftfy.Apply(src, fixes).Text;
    }

    // ---- if x = 5 ----

    [Theory]
    [InlineData("int x = 1\nif x = 5 { print 1 }", "int x = 1\nif x == 5 { print 1 }")]
    [InlineData("int x = 1\nwhile x = 5 { break }", "int x = 1\nwhile x == 5 { break }")]
    [InlineData("for (int i = 0; i = 3; i++) { }", "for (int i = 0; i == 3; i++) { }")]
    public void ASingleEqualsInAConditionIsComparison(string src, string expected)
    {
        Assert.Equal(expected, Fixed(src));
        Assert.Empty(Run(src).Pass.Errors());
    }

    [Fact]
    public void OnlyThatEqualsSignChanges()
    {
        // the column pins the fix: the <= before it is left alone
        Assert.Equal("int x = 1\nif x <= 2 and x == 1 { }", Fixed("int x = 1\nif x <= 2 and x = 1 { }"));
    }

    [Fact]
    public void EqualsOutsideConditionsIsStillAssignment()
    {
        Assert.Empty(Run("int x = 1\nx = 5").Fixes);
    }

    // ---- xs.lenght ----

    [Theory]
    [InlineData("list<int> xs = [1]\nprint xs.lenght", "print xs.length")]
    [InlineData("string s = \"a\"\nprint s.uper()", "print s.upper()")]
    [InlineData("list<int> xs = [1]\nxs.psuh 2", "xs.push 2")]
    public void MemberTyposOnKnownTypes(string src, string expectedLine)
    {
        Assert.Contains(expectedLine, Fixed(src));
        var (_, pass, _) = Run(src);
        Assert.Empty(pass.Questions); // no need to ask the model
    }

    [Fact]
    public void FarOffMembersStillGoToSemanticResolution()
    {
        var (_, pass, fixes) = Run("list<int> xs = [1]\nprint xs.shuffleAll()");
        Assert.Empty(fixes);
        Assert.Single(pass.Questions);
    }

    // ---- Greet vs greet ----

    [Fact]
    public void WrongCaseOnAFunction()
    {
        var src = "string greet(string who) { return who }\nprint Greet(\"a\")";
        Assert.Contains("print greet(\"a\")", Fixed(src));
        Assert.Empty(Run(src).Pass.Questions);
    }

    [Fact]
    public void WrongCaseOnAVariable()
    {
        var src = "int total = 3\nprint Total, Total + 1";
        Assert.Contains("print total, total + 1", Fixed(src));
        Assert.Empty(Run(src).Pass.Questions);
    }

    // ---- for i in range 5 { print n } ----

    [Theory]
    [InlineData("for i in range 5 { print n }", "for i in range 5 { print i }")]
    [InlineData("for i in 3 { print n * n }", "for i in 3 { print i * i }")]
    public void TheIndexYouMeant(string src, string expected)
    {
        Assert.Equal(expected, Fixed(src));
        Assert.Empty(Run(src).Pass.Errors());
    }

    [Theory]
    [InlineData("for i in range 3 { say newline }")]      // not index-like: semantic resolution decides
    [InlineData("for i in range 3 { print a, b }")]       // two unknowns: no guessing
    [InlineData("for i in range 3 { print i, n }")]       // i is used: n is something else
    [InlineData("list<string> s = [\"a\"]\nfor i in s { print n }")] // not a counting loop
    public void BacksOffWhenItIsNotObvious(string src)
    {
        Assert.DoesNotContain(Run(src).Fixes, f => f.Why.Contains("the loop counts"));
    }
}
