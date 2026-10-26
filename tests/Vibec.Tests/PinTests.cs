namespace Vibec.Tests;

public class PinTests
{
    static Recipe CSharp(params string[] constraints) =>
        new() { Target = "csharp", Language = "C#", FileName = "Program.cs", Constraints = [.. constraints] };

    [Fact]
    public void SameIlSameTargetSameHash()
    {
        Assert.Equal(Pin.HashFor("# Task\n1. Print `hi`.", CSharp("std only")), Pin.HashFor("# Task\n1. Print `hi`.\n", CSharp("std only")));
    }

    [Fact]
    public void AnyChangeToTheIlOrTargetChangesTheHash()
    {
        var baseline = Pin.HashFor("# Task\n1. Print `hi`.", CSharp("std only"));
        Assert.NotEqual(baseline, Pin.HashFor("# Task\n1. Print `hello`.", CSharp("std only")));
        Assert.NotEqual(baseline, Pin.HashFor("# Task\n1. Print `hi`.", CSharp("std only", "no warnings")));
        Assert.NotEqual(baseline, Pin.HashFor("# Task\n1. Print `hi`.",
            new Recipe { Target = "rust", Language = "Rust", FileName = "main.rs", Constraints = ["std only"] }));
    }

    [Fact]
    public void PinsRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pin-{Guid.NewGuid():N}.json");
        try
        {
            new Pin("h", "csharp", "Program.cs", "Console.WriteLine(\"hi\");", null, DateTimeOffset.Now).Save(path);
            var pin = Pin.Load(path)!;
            Assert.Equal("h", pin.Hash);
            Assert.Equal("Console.WriteLine(\"hi\");", pin.Code);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AMissingOrBrokenPinIsNoPin()
    {
        Assert.Null(Pin.Load(Path.Combine(Path.GetTempPath(), "no-such-pin.json")));
        var path = Path.Combine(Path.GetTempPath(), $"pin-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try { Assert.Null(Pin.Load(path)); } finally { File.Delete(path); }
    }
}
