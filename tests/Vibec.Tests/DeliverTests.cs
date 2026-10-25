namespace Vibec.Tests;

public class DeliverTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vibec-tests-" + Guid.NewGuid().ToString("N"));
    string Src => Path.Combine(_root, "src");
    string Bin => Path.Combine(_root, "bin");

    public DeliverTests() => Directory.CreateDirectory(Src);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    static Recipe RecipeWith(string? output) => new()
    {
        Target = "test", FileName = "main.src", Schema = Recipe.CurrentSchema,
        Candidates = [new Candidate { Probe = "x --version", VersionRegex = "(\\d+\\.\\d+)", Build = "x build", Output = output }],
    };

    static Toolchain ToolchainFor(Recipe r) => new(r.Candidates[0], new Version(9, 0), TooOld: false);

    void Touch(params string[] paths)
    {
        foreach (var p in paths)
        {
            var full = Path.Combine(Src, p);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, p);
        }
    }

    static string[] Names(IEnumerable<string> paths) => paths.Select(Path.GetFileName).Order().ToArray()!;

    [Fact]
    public void AnExecutableInAnOutputFolderShipsWithItsSiblings()
    {
        // what dotnet build -o out produces: the .exe is useless without the .dll
        Touch("main.src", "out/app.exe", "out/app.dll", "out/app.runtimeconfig.json");
        var r = RecipeWith("out/app.exe");
        var delivered = Toolchains.Deliver(r, ToolchainFor(r), Src, Bin, "app");
        Assert.Equal(["app.dll", "app.exe", "app.runtimeconfig.json"], Names(delivered));
    }

    [Fact]
    public void AnExecutableBuiltNextToTheSourceShipsAlone()
    {
        Touch("main.src", "app.exe");
        var r = RecipeWith("app.exe");
        var delivered = Toolchains.Deliver(r, ToolchainFor(r), Src, Bin, "app");
        Assert.Equal(["app.exe"], Names(delivered));
    }

    [Fact]
    public void AnOutputFolderShipsWhole()
    {
        Touch("main.src", "out/a", "out/sub/b");
        var r = RecipeWith("out");
        var delivered = Toolchains.Deliver(r, ToolchainFor(r), Src, Bin, "app");
        Assert.True(File.Exists(Path.Combine(Bin, "sub", "b")));
        Assert.Equal(2, delivered.Count);
    }

    [Fact]
    public void WithoutAnOutputTheSourceIsTheDeliverable()
    {
        Touch("main.src");
        var r = RecipeWith(null);
        var delivered = Toolchains.Deliver(r, ToolchainFor(r), Src, Bin, "app");
        Assert.Equal(["app.src"], Names(delivered));
    }

    [Fact]
    public void AMissingOutputIsAnError()
    {
        Touch("main.src");
        var r = RecipeWith("out/app.exe");
        Assert.Throws<VibeError>(() => Toolchains.Deliver(r, ToolchainFor(r), Src, Bin, "app"));
    }
}
