using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vibec;

public enum QuestionKind { Coerce, Function, Member, Name }

/// Something the static pass couldn't settle on its own; the vibe pass asks the model.
public sealed class Question
{
    public required QuestionKind Kind { get; init; }
    public required string Key { get; init; }
    public required int Line { get; init; }

    // Coerce
    public JsonNode? Value { get; init; }
    public VType? To { get; init; }

    // Function / Member
    public string? Name { get; init; }
    public VType? Receiver { get; init; }
    public string? Module { get; init; } // member of a module the program didn't `use`
    public List<string>? Named { get; init; } // names of named arguments, e.g. timeout
    public List<VType>? ArgTypes { get; init; }
    public bool IsProperty { get; init; }

    public VType? Expected { get; init; } // return type implied by context, e.g. int foo = Make()

    // Name
    public List<string>? Candidates { get; init; }

    /// One line of context for the model, e.g. `int five = "five"`.
    public string? Context { get; init; }
}

public sealed class Answer
{
    public bool Ok { get; set; }
    public JsonNode? Value { get; set; }
    public string? Returns { get; set; }
    public string? Meaning { get; set; }
    public string? Rename { get; set; }
    public string? Why { get; set; }
    public string? Source { get; set; } // Vibe source for a function the model wrote
}

/// Everything the vibe pass has learned, keyed by question. Persisted as .vibecache.json
/// next to the source so unchanged vibes aren't re-asked on every build.
public sealed class Knowledge
{
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    readonly string? _path;
    Dictionary<string, Answer> _answers = [];

    /// fresh: start empty (but still save), so --fresh replaces the cache instead of ignoring it.
    public Knowledge(string? path, bool fresh = false)
    {
        _path = path;
        if (!fresh && path != null && File.Exists(path))
        {
            try { _answers = JsonSerializer.Deserialize<Dictionary<string, Answer>>(File.ReadAllText(path)) ?? []; }
            catch (JsonException) { _answers = []; } // a corrupt cache just means re-asking
        }
    }

    public bool TryGet(string key, out Answer answer) => _answers.TryGetValue(key, out answer!);

    public void Set(string key, Answer answer) => _answers[key] = answer;

    public void Save()
    {
        if (_path != null) File.WriteAllText(_path, JsonSerializer.Serialize(_answers, JsonOpts));
    }

    public static string Hash(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();

    public static string CoerceKey(JsonNode? value, VType to, string? context) =>
        $"coerce:{to}:{value?.ToJsonString() ?? "null"}" + (context != null ? $" ({context})" : "");
    public static string FunctionKey(string name, IEnumerable<VType> args, VType? expected) =>
        $"fn:{name}({string.Join(",", args)})->{expected?.ToString() ?? "?"}";
    public static string MemberKey(VType receiver, string name, IEnumerable<VType>? args) =>
        args == null ? $"member:{receiver}.{name}" : $"member:{receiver}.{name}({string.Join(",", args)})";
    public static string ModuleMemberKey(string module, string name, IEnumerable<VType> args, IEnumerable<string> named, VType? expected) =>
        $"module:{module}.{name}({string.Join(",", args.Select(a => a.ToString()).Concat(named.Select(n => n + ":")))})->{expected?.ToString() ?? "?"}";
    public static string NameKey(string name, IEnumerable<string> candidates, VType? expected) =>
        $"name:{name}[{string.Join(",", candidates.Order())}]->{expected?.ToString() ?? "?"}";
}
