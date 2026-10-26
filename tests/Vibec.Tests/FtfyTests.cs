namespace Vibec.Tests;

public class FtfyTests
{
    static List<Fix> FixesFor(string src, Knowledge? knowledge = null)
    {
        var lexFixes = new List<Fix>();
        var program = Parser.ParseSource(src, lexFixes);
        var pass = new StaticPass(knowledge ?? new Knowledge(null));
        pass.Run(program);
        return lexFixes.Concat(pass.Fixes).ToList();
    }

    const string Tradestie = """
        use http
        json res = http.get "https://example.com"
        for item : res {
            say "${res.ticker} : ${res.sentiment}}"
        }
        """;

    [Fact]
    public void LoopingOverResButReadingResMeansItem()
    {
        var fixes = FixesFor(Tradestie);
        Assert.Contains(fixes, f => f is { Line: 4, Old: "res.ticker", New: "item.ticker" });
        Assert.Contains(fixes, f => f is { Line: 4, Old: "res.sentiment", New: "item.sentiment" });
    }

    [Fact]
    public void UsingTheLoopVariableAnywhereMeansNoGuessing()
    {
        var fixes = FixesFor("use http\njson res = http.get \"x\"\nfor item in res {\n  print item.id, res.total\n}");
        Assert.DoesNotContain(fixes, f => f.Old.StartsWith("res."));
    }

    [Fact]
    public void StrayBraceAfterInterpolation()
    {
        var fix = Assert.Single(FixesFor(Tradestie), f => f.Why.Contains("stray"));
        Assert.Equal("\"${res.ticker} : ${res.sentiment}\"", fix.New);
    }

    [Fact]
    public void FStringDoubleBracesAreNotStray()
    {
        Assert.DoesNotContain(FixesFor("int x = 1\nprint f\"{{literal}} {x}\""), f => f.Why.Contains("stray"));
    }

    [Fact]
    public void TyposBecomeFixes()
    {
        var key = Knowledge.NameKey("lukcy", ["lucky"], null);
        var fixes = FixesFor("int lucky = 7\nprint lukcy", Vibe.Knows((key, new Answer { Ok = true, Rename = "lucky" })));
        Assert.Contains(fixes, f => f is { Line: 2, Old: "lukcy", New: "lucky" });
    }

    [Fact]
    public void ApplyingFixesRewritesJustThoseSpots()
    {
        var src = Tradestie.Replace("\r", "");
        var (text, applied) = Ftfy.Apply(src, FixesFor(src));
        Assert.Equal(3, applied.Count);
        Assert.Contains("say \"${item.ticker} : ${item.sentiment}\"", text);
        Assert.Contains("for item : res {", text);           // the loop itself is untouched
        Assert.Contains("json res = http.get", text);
        Assert.Empty(FixesFor(text));                        // fixed means fixed
    }

    [Fact]
    public void NamesAreReplacedAsWholeWords()
    {
        var (text, _) = Ftfy.Apply("print lukcy, lukcyish", [new Fix(1, "lukcy", "lucky", "typo")]);
        Assert.Equal("print lucky, lukcyish", text);
    }

    [Fact]
    public void FixesThatNoLongerMatchAreSkipped()
    {
        var (text, applied) = Ftfy.Apply("print lucky", [new Fix(1, "lukcy", "lucky", "typo")]);
        Assert.Empty(applied);
        Assert.Equal("print lucky", text);
    }
}

public class UndoTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "vibec-undo-" + Guid.NewGuid().ToString("N"));
    string File_ => Path.Combine(_dir, "t.vibe");
    string Backups => Path.Combine(_dir, ".vibe-int", "ftfy");

    public UndoTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    void FtfyRun(string old, string @new)
    {
        Ftfy.ApplyToFile(File_, [new Fix(1, old, @new, "test")], Backups);
        Thread.Sleep(5); // distinct timestamps
    }

    [Fact]
    public void UndoPutsBackTheOriginal()
    {
        File.WriteAllText(File_, "print lukcy");
        FtfyRun("lukcy", "lucky");
        Assert.Equal("print lucky", File.ReadAllText(File_));
        Ftfy.Undo(File_, force: false);
        Assert.Equal("print lukcy", File.ReadAllText(File_));
    }

    [Fact]
    public void UndoStepsBackOneRunAtATime()
    {
        File.WriteAllText(File_, "print a");
        FtfyRun("a", "b");
        FtfyRun("b", "c");
        Assert.Contains("1 earlier", Ftfy.Undo(File_, force: false));
        Assert.Equal("print b", File.ReadAllText(File_));
        Ftfy.Undo(File_, force: false);
        Assert.Equal("print a", File.ReadAllText(File_));
        Assert.Throws<VibeError>(() => Ftfy.Undo(File_, force: false));
    }

    [Fact]
    public void UndoWontClobberLaterEdits()
    {
        File.WriteAllText(File_, "print lukcy");
        FtfyRun("lukcy", "lucky");
        File.WriteAllText(File_, "print lucky\nprint 2");
        var ex = Assert.Throws<VibeError>(() => Ftfy.Undo(File_, force: false));
        Assert.Contains("edited since", ex.Message);
        Assert.Equal("print lucky\nprint 2", File.ReadAllText(File_));
        Ftfy.Undo(File_, force: true);
        Assert.Equal("print lukcy", File.ReadAllText(File_));
    }
}
