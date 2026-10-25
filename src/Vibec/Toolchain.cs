using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vibec;

public sealed record ProcResult(int ExitCode, string Output, bool TimedOut);

public static class Proc
{
    /// Runs a command directly (never through a shell, so model-written commands can't pipe,
    /// redirect or chain). Returns null if the program doesn't exist. On Windows, .cmd/.bat shims
    /// on PATH (npx, tsc, ...) are run via cmd.
    public static ProcResult? Run(string commandLine, string? workDir = null, int timeoutMs = 120_000)
    {
        var parts = Split(commandLine);
        if (parts.Count == 0) return null;
        var exe = parts[0];
        var args = string.Join(' ', parts.Skip(1).Select(Quote));
        try { return RunCore(exe, args, workDir, timeoutMs); }
        catch (Win32Exception) when (OperatingSystem.IsWindows() && HasShim(exe))
        {
            return RunCore("cmd.exe", $"/d /c {exe} {args}", workDir, timeoutMs);
        }
        catch (Win32Exception) { return null; }
    }

    public static List<string> Split(string commandLine)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        bool quoted = false, any = false;
        foreach (var c in commandLine)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) { parts.Add(current.ToString()); current.Clear(); any = false; }
                continue;
            }
            current.Append(c);
            any = true;
        }
        if (any) parts.Add(current.ToString());
        return parts;
    }

    static string Quote(string arg) => arg.Length == 0 || arg.Any(char.IsWhiteSpace) ? $"\"{arg}\"" : arg;

    static bool HasShim(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Any(dir => File.Exists(Path.Combine(dir, exe + ".cmd")) || File.Exists(Path.Combine(dir, exe + ".bat")));

    static ProcResult RunCore(string exe, string args, string? workDir, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workDir ?? Environment.CurrentDirectory,
        };
        using var p = Process.Start(psi)!;
        var output = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new ProcResult(-1, output.ToString(), TimedOut: true);
        }
        p.WaitForExit();
        return new ProcResult(p.ExitCode, output.ToString(), TimedOut: false);
    }
}

// ---------- recipes: how to find and drive a target's toolchain, as written by the model ----------

public sealed class SetupFile
{
    public string Path { get; set; } = "";
    public string Content { get; set; } = "";
}

/// One way to get a working toolchain: how to detect it, and how to build with that same program.
public sealed class Candidate
{
    public string Probe { get; set; } = "";
    public string VersionRegex { get; set; } = "";
    public string MinVersion { get; set; } = "0.0";
    public string? Build { get; set; }          // null = nothing to check with
    public string? ErrorFilter { get; set; }    // regex picking error lines out of build output
    public List<SetupFile> Files { get; set; } = []; // e.g. a .csproj next to Program.cs
    public string? Output { get; set; }         // what a successful build produces (file or folder); null = the source is the deliverable
}

public sealed class Recipe
{
    public const int CurrentSchema = 2;

    public int Schema { get; set; }
    public string Target { get; set; } = "";
    public string Language { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Fence { get; set; } = "";
    public string InstallHint { get; set; } = "";
    public List<string> Constraints { get; set; } = [];
    public List<Candidate> Candidates { get; set; } = [];
    public bool Approved { get; set; }
    public string? WrittenBy { get; set; }

    public string Extension => System.IO.Path.GetExtension(FileName);
}

public sealed record Toolchain(Candidate Candidate, Version Version, bool TooOld)
{
    public string Program => Proc.Split(Candidate.Probe).FirstOrDefault() ?? "?";
    public string Display => $"{Program} {Version}";
}

public sealed record BuildResult(bool Ok, string Errors);

public static class Toolchains
{
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    static string StoreDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vibec", "toolchains");

    public static string PathFor(string target) => Path.Combine(StoreDir, $"{Sanitize(target)}.json");

    public static Recipe? Load(string target)
    {
        var path = PathFor(target);
        if (!File.Exists(path)) return null;
        try
        {
            var recipe = JsonSerializer.Deserialize<Recipe>(File.ReadAllText(path));
            return recipe?.Schema == Recipe.CurrentSchema ? recipe : null; // older recipes get re-discovered
        }
        catch (JsonException) { return null; }
    }

    public static void Save(Recipe recipe)
    {
        Directory.CreateDirectory(StoreDir);
        File.WriteAllText(PathFor(recipe.Target), JsonSerializer.Serialize(recipe, JsonOpts));
    }

    public static IEnumerable<Recipe> All() =>
        Directory.Exists(StoreDir)
            ? Directory.GetFiles(StoreDir, "*.json").Select(f => Load(Path.GetFileNameWithoutExtension(f))).OfType<Recipe>()
            : [];

    static string Sanitize(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9+#-]", "_");

    /// Shell syntax can't do anything (commands never go through a shell), but a recipe that
    /// contains it was written for a shell and won't work - reject it outright.
    public static string? Validate(Recipe r)
    {
        if (string.IsNullOrWhiteSpace(r.FileName) || r.FileName.IndexOfAny(['/', '\\']) >= 0) return $"bad filename '{r.FileName}'";
        if (r.Candidates.Count == 0) return "no toolchain candidates";
        foreach (var c in r.Candidates)
        {
            foreach (var cmd in new[] { c.Probe, c.Build }.OfType<string>())
                if (Regex.IsMatch(cmd, @"[|<>;&`$]") ) return $"command uses shell syntax: {cmd}";
            if (string.IsNullOrWhiteSpace(c.Probe)) return "a candidate has no probe command";
            try { _ = new Regex(c.VersionRegex); if (c.ErrorFilter != null) _ = new Regex(c.ErrorFilter); }
            catch (ArgumentException ex) { return $"bad regex: {ex.Message}"; }
            if (!Version.TryParse(c.MinVersion, out _)) return $"bad minVersion '{c.MinVersion}'";
            foreach (var path in c.Files.Select(f => f.Path).Append(c.Output).OfType<string>())
                if (Path.IsPathRooted(path) || path.Contains("..")) return $"path escapes the build dir: {path}";
        }
        return null;
    }

    /// Tries each candidate: it must run, exit 0, and report a version (which rules out stubs
    /// like the Microsoft Store `python3`). Newest version wins when a tool lists several.
    public static (Toolchain? Found, List<string> Notes) Detect(Recipe recipe)
    {
        var notes = new List<string>();
        Toolchain? tooOld = null;
        foreach (var c in recipe.Candidates)
        {
            var r = Proc.Run(c.Probe, timeoutMs: 15_000);
            if (r == null) continue;
            var v = Regex.Matches(r.Output, c.VersionRegex, RegexOptions.Multiline)
                .Select(m => m.Groups.Count > 1 && Version.TryParse(m.Groups[1].Value, out var parsed) ? parsed : null)
                .Where(x => x != null).Max();
            if (r.ExitCode != 0 || v == null)
            {
                notes.Add($"'{c.Probe}' exists but didn't report a version (stub?), skipped");
                continue;
            }
            var tc = new Toolchain(c, v, v < Version.Parse(c.MinVersion));
            if (!tc.TooOld) return (tc, notes);
            notes.Add($"{tc.Display} is older than {c.MinVersion}");
            tooOld ??= tc;
        }
        return (tooOld, notes);
    }

    public static BuildResult Build(Recipe recipe, Toolchain tc, string code, string dir, string name)
    {
        var c = tc.Candidate;
        File.WriteAllText(Path.Combine(dir, recipe.FileName), code);
        if (c.Build == null) return new BuildResult(true, "");

        foreach (var f in c.Files)
        {
            var path = Path.Combine(dir, Fill(f.Path, recipe, tc, name));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Fill(f.Content, recipe, tc, name));
        }

        var r = Proc.Run(Fill(c.Build, recipe, tc, name), dir, timeoutMs: 300_000);
        if (r == null) return new BuildResult(false, $"couldn't start '{c.Build}'");
        if (r.TimedOut) return new BuildResult(false, "the build timed out");
        if (r.ExitCode == 0) return new BuildResult(true, "");

        var lines = r.Output.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
        var errors = c.ErrorFilter == null ? lines : lines.Where(l => Regex.IsMatch(l, c.ErrorFilter)).Distinct().ToList();
        if (errors.Count == 0) errors = lines; // filter missed: send everything
        return new BuildResult(false, string.Join("\n", errors.Take(150)));
    }

    /// Copies what the build produced into binDir. Returns the paths delivered.
    /// No toolchain, or a recipe with no output: the source file itself is the deliverable.
    public static List<string> Deliver(Recipe recipe, Toolchain? tc, string srcDir, string binDir, string name)
    {
        Directory.CreateDirectory(binDir);
        var delivered = new List<string>();
        var output = tc?.Candidate.Output is { } o ? Path.Combine(srcDir, Fill(o, recipe, tc, name)) : null;

        if (output == null)
        {
            var dest = Path.Combine(binDir, name + recipe.Extension);
            File.Copy(Path.Combine(srcDir, recipe.FileName), dest, overwrite: true);
            delivered.Add(dest);
        }
        else if (Directory.Exists(output))
        {
            foreach (var file in Directory.GetFiles(output, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(binDir, Path.GetRelativePath(output, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
                delivered.Add(dest);
            }
        }
        else if (File.Exists(output))
        {
            var dest = Path.Combine(binDir, Path.GetFileName(output));
            File.Copy(output, dest, overwrite: true);
            delivered.Add(dest);
        }
        else throw new VibeError($"the build said it would produce '{tc!.Candidate.Output}', but it isn't there. try --refresh");

        return delivered;
    }

    /// Placeholders a recipe may use: {name}, {file}, {out}, {major}, {minor}.
    static string Fill(string s, Recipe recipe, Toolchain tc, string name) => s
        .Replace("{name}", name)
        .Replace("{file}", recipe.FileName)
        .Replace("{out}", OperatingSystem.IsWindows() ? $"{name}.exe" : name)
        .Replace("{major}", tc.Version.Major.ToString())
        .Replace("{minor}", Math.Max(tc.Version.Minor, 0).ToString());
}
