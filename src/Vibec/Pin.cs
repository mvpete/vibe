using System.Text.Json;

namespace Vibec;

/// Pinned codegen: the code the backend wrote for one IL and one target. Same IL, same target,
/// same code: no model call, just a rebuild. Saved as .vibe-int/<name>.<target>.lock.json.
public sealed record Pin(string Hash, string Target, string FileName, string Code, string? Notes, DateTimeOffset Created)
{
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// What the code depends on: the IL, what the target asks for, and the backend's instructions.
    /// Not the toolchain version: a pinned file is rebuilt every time, which catches that.
    public static string HashFor(string il, Recipe target) =>
        Knowledge.Hash(string.Join("\n---\n",
            Passes.CodegenPass.Version, il.Trim(), target.Target, target.Language, target.FileName,
            string.Join("\n", target.Constraints)));

    public static Pin? Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Pin>(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
}
