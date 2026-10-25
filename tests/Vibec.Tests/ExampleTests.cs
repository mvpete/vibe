namespace Vibec.Tests;

/// Every example in the repo must parse and pass static analysis, and the normalized output
/// must itself be valid Vibe that normalizes to the same thing.
public class ExampleTests
{
    static string ExamplesDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "examples"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? throw new DirectoryNotFoundException("examples/"), "examples");
    }

    public static TheoryData<string> Examples()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(ExamplesDir(), "*.vibe").Order()) data.Add(Path.GetFileName(f));
        return data;
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void ExampleChecksOut(string file)
    {
        var (_, pass) = Vibe.Analyze(File.ReadAllText(Path.Combine(ExamplesDir(), file)));
        Assert.Empty(pass.Errors());
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void NormalizedOutputRoundTrips(string file)
    {
        var once = Vibe.Print(File.ReadAllText(Path.Combine(ExamplesDir(), file)));
        var twice = Vibe.Print(once);
        Assert.Equal(StripNotes(once), StripNotes(twice));
    }

    // compiler notes (`# vibed from ...`) are output-only; compare the code itself
    static string StripNotes(string s) =>
        string.Join("\n", s.Split('\n').Select(l => l.Contains("    # ") ? l[..l.IndexOf("    # ", StringComparison.Ordinal)] : l));
}
