using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vibec;

/// The IL: a target-neutral specification of a program, written once by lowering and compiled to
/// any target. Saved as <name>.il.md with a small header, so it can be compiled on its own later:
///
///   <!-- vibe-il 1
///   {"source":"greeter.vibe","hash":"...","outputText":["hello"]}
///   -->
///   # Task
///   ...
public sealed record IlFile(string Source, string Hash, IReadOnlyList<string> OutputText, string Body)
{
    const int Version = 1;
    static readonly Regex Header = new(@"^<!-- vibe-il (\d+)\r?\n(.*?)\r?\n-->\r?\n", RegexOptions.Singleline);

    sealed record Meta(string Source, string Hash, List<string> OutputText);

    public string Render() =>
        $"<!-- vibe-il {Version}\n{JsonSerializer.Serialize(new Meta(Source, Hash, [.. OutputText]))}\n-->\n{Body.TrimEnd()}\n";

    public static IlFile Parse(string text)
    {
        var m = Header.Match(text);
        if (!m.Success) throw new VibeError("this isn't a vibe IL file (no '<!-- vibe-il' header)");
        if (int.Parse(m.Groups[1].Value) != Version) throw new VibeError($"vibe IL version {m.Groups[1].Value} isn't supported; this vibec reads version {Version}");
        var meta = JsonSerializer.Deserialize<Meta>(m.Groups[2].Value) ?? throw new VibeError("the vibe IL header is empty");
        return new IlFile(meta.Source, meta.Hash, meta.OutputText, text[m.Length..]);
    }

    public static bool IsIlPath(string path) => path.EndsWith(".il.md", StringComparison.OrdinalIgnoreCase);

    /// greeter.il.md → greeter
    public static string NameOf(string path) => Path.GetFileName(path)[..^".il.md".Length];
}
