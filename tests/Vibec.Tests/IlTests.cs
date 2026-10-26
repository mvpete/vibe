using Vibec.Passes;

namespace Vibec.Tests;

public class IlTests
{
    [Fact]
    public void IlFilesRoundTrip()
    {
        var il = new IlFile("greeter.vibe", "abc123", ["hello", "caught:"], "# Task\n\n1. Print `hello`.\n");
        var parsed = IlFile.Parse(il.Render());
        Assert.Equal("greeter.vibe", parsed.Source);
        Assert.Equal("abc123", parsed.Hash);
        Assert.Equal(["hello", "caught:"], parsed.OutputText);
        Assert.StartsWith("# Task", parsed.Body);
    }

    [Fact]
    public void TheHeaderIsAnHtmlCommentSoTheIlReadsAsMarkdown()
    {
        Assert.StartsWith("<!-- vibe-il 1\n", new IlFile("a.vibe", "h", [], "# Task").Render());
    }

    [Fact]
    public void NotAnIl()
    {
        Assert.Throws<VibeError>(() => IlFile.Parse("# Task\nno header"));
    }

    [Fact]
    public void AFutureIlVersionIsRefused()
    {
        var ex = Assert.Throws<VibeError>(() => IlFile.Parse("<!-- vibe-il 9\n{}\n-->\n# Task"));
        Assert.Contains("version 9", ex.Message);
    }

    [Theory]
    [InlineData("greeter.il.md", true, "greeter")]
    [InlineData(".vibe-int/web.il.md", true, "web")]
    [InlineData("greeter.vibe", false, null)]
    public void IlPaths(string path, bool isIl, string? name)
    {
        Assert.Equal(isIl, IlFile.IsIlPath(path));
        if (isIl) Assert.Equal(name, IlFile.NameOf(path));
    }

    [Fact]
    public void TheBackendPromptIsTheIlPlusTheTarget()
    {
        var recipe = new Recipe { Target = "rust", Language = "Rust", FileName = "main.rs", Constraints = ["std only"] };
        var prompt = CodegenPass.BackendPrompt("# Task\n\n1. Print `hello`.", recipe, toolchain: null);
        Assert.Contains("Language: Rust", prompt);
        Assert.Contains("Output file: main.rs", prompt);
        Assert.Contains("- std only", prompt);
        Assert.EndsWith("1. Print `hello`.", prompt.TrimEnd());
    }
}
