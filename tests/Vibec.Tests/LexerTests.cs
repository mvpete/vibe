namespace Vibec.Tests;

public class LexerTests
{
    static List<Token> Lex(string src) => Lexer.Lex(src).Where(t => t.Type is not (Tok.Newline or Tok.Eof)).ToList();

    [Fact]
    public void PercentStartsAnInlinePromptWhereAValueIsExpected()
    {
        var tokens = Lex("string name = %ask the user their name%");
        var prompt = Assert.Single(tokens, t => t.Type == Tok.InlinePrompt);
        Assert.Equal("ask the user their name", prompt.Text);
    }

    [Fact]
    public void PercentBetweenValuesIsStillRemainder()
    {
        var tokens = Lex("int r = 10 % 3");
        Assert.DoesNotContain(tokens, t => t.Type == Tok.InlinePrompt);
        Assert.Contains(tokens, t => t is { Type: Tok.Op, Text: "%" });
    }

    [Fact]
    public void DoublePercentIsALiteralPercentInsideAPrompt()
    {
        var prompt = Assert.Single(Lex("int chance = %a 50%% chance%"), t => t.Type == Tok.InlinePrompt);
        Assert.Equal("a 50% chance", prompt.Text);
    }

    [Fact]
    public void PromptDirectiveTakesTheRestOfTheLine()
    {
        var prompt = Assert.Single(Lex("#prompt keep it friendly"));
        Assert.Equal(Tok.Prompt, prompt.Type);
        Assert.Equal("keep it friendly", prompt.Text);
    }

    [Fact]
    public void PromptBlockIsDedented()
    {
        var prompt = Assert.Single(Lex("%prompt {\n    line one\n      line two\n}"));
        Assert.Equal("line one\n  line two", prompt.Text);
    }

    [Fact]
    public void HashWithoutPromptIsAComment()
    {
        Assert.Empty(Lex("# prompting is fun\n#promptly ignored"));
    }

    [Theory]
    [InlineData("\"hello {$name}\"", "hello {name}")]
    [InlineData("\"hello ${name}\"", "hello {name}")]
    [InlineData("\"braces {stay} {$x}\"", "braces {{stay}} {x}")]
    public void PlainStringsInterpolateDollarBraces(string src, string expected)
    {
        var token = Assert.Single(Lex(src));
        Assert.Equal(Tok.Interp, token.Type);
        Assert.Equal(expected, token.Text);
    }

    [Fact]
    public void PlainStringsWithoutDollarStayPlain()
    {
        Assert.Equal(Tok.String, Assert.Single(Lex("\"{not interpolated}\"")).Type);
    }

    [Fact]
    public void UnterminatedStringIsAVibeError()
    {
        var ex = Assert.Throws<VibeError>(() => Lexer.Lex("print \"oops"));
        Assert.Contains("never ends", ex.Message);
    }
}
