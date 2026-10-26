using System.Text.RegularExpressions;

namespace Vibec;

/// A "you know what I mean" error the compiler is confident about: on this line, `Old` should be `New`.
public sealed record Fix(int Line, string Old, string New, string Why, string? File = null)
{
    public override string ToString() => $"{(File != null ? File + " " : "")}line {Line}: {Old} → {New} ({Why})";
}

/// FTFY: fixed that for you. Applies fixes to source text, one line at a time.
public static class Ftfy
{
    /// Applies the fixes that still match; returns the new text and the fixes that applied.
    public static (string Text, List<Fix> Applied) Apply(string text, IEnumerable<Fix> fixes)
    {
        var lines = text.Split('\n');
        var applied = new List<Fix>();
        foreach (var fix in fixes)
        {
            int i = fix.Line - 1;
            if (i < 0 || i >= lines.Length) continue;
            // whole words for names (lukcy, res.ticker); exact text otherwise (string literals)
            var pattern = Regex.Escape(fix.Old);
            if (Regex.IsMatch(fix.Old, @"^\w")) pattern = @"(?<![\w])" + pattern;
            if (Regex.IsMatch(fix.Old, @"\w$")) pattern += @"(?![\w])";
            var re = new Regex(pattern);
            if (!re.IsMatch(lines[i])) continue;
            lines[i] = re.Replace(lines[i], fix.New.Replace("$", "$$"), 1);
            applied.Add(fix);
        }
        return (string.Join('\n', lines), applied);
    }

    /// Rewrites a file in place. Each run keeps a before/after pair in backupDir so it can be undone.
    public static List<Fix> ApplyToFile(string path, IEnumerable<Fix> fixes, string backupDir)
    {
        var original = File.ReadAllText(path);
        var (text, applied) = Apply(original, fixes);
        if (applied.Count == 0) return applied;
        Directory.CreateDirectory(backupDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var name = Path.GetFileNameWithoutExtension(path);
        File.WriteAllText(Path.Combine(backupDir, $"{name}.{stamp}.before.vibe"), original);
        File.WriteAllText(Path.Combine(backupDir, $"{name}.{stamp}.after.vibe"), text);
        File.WriteAllText(path, text);
        return applied;
    }

    /// `vibec undo file.vibe`: puts back what the most recent FTFY run changed. Refuses if the file
    /// has been edited since (unless forced), so later edits aren't lost.
    public static string Undo(string path, bool force)
    {
        var backupDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, ".vibe-int", "ftfy");
        var name = Path.GetFileNameWithoutExtension(path);
        var before = Directory.Exists(backupDir)
            ? Directory.GetFiles(backupDir, $"{name}.*.before.vibe").Order().LastOrDefault()
            : null;
        if (before == null) throw new VibeError($"nothing to undo: FTFY hasn't changed {Path.GetFileName(path)}");

        var after = before[..^".before.vibe".Length] + ".after.vibe";
        if (!force && File.Exists(after) && File.ReadAllText(after) != File.ReadAllText(path))
            throw new VibeError($"{Path.GetFileName(path)} has been edited since FTFY fixed it, so undo would lose those edits. --force to undo anyway");

        File.Copy(before, path, overwrite: true);
        File.Delete(before);
        if (File.Exists(after)) File.Delete(after);
        var remaining = Directory.GetFiles(backupDir, $"{name}.*.before.vibe").Length;
        return $"restored {Path.GetFileName(path)} to before FTFY touched it" +
               (remaining > 0 ? $" ({remaining} earlier FTFY run(s) can still be undone)" : "");
    }

    /// Plain strings with `${x}` / `{$x}`: a `}` straight after an interpolation closes is a typo.
    static readonly Regex StrayBrace = new(@"(\$\{[^{}]*\}|\{\$[^{}]*\})\}");

    public static string? WithoutStrayBraces(string rawString) =>
        StrayBrace.IsMatch(rawString) ? StrayBrace.Replace(rawString, "$1") : null;
}
