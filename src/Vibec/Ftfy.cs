using System.Text.RegularExpressions;

namespace Vibec;

/// A "you know what I mean" error the compiler is confident about: on this line, `Old` should be `New`.
/// With Insert, New goes on a line of its own before Line (Old is unused).
/// With Column, Old must sit exactly there. With All, every whole-word Old on the line is replaced.
public sealed record Fix(int Line, string Old, string New, string Why, string? File = null, bool Insert = false, int? Column = null, bool All = false)
{
    public override string ToString() =>
        $"{(File != null ? File + " " : "")}line {Line}: " + (Insert ? $"+ {New}" : $"{Old} → {New}") + $" ({Why})";
}

/// FTFY: fixed that for you. Applies fixes to source text, one line at a time.
public static class Ftfy
{
    /// Applies the fixes that still match; returns the new text and the fixes that applied.
    public static (string Text, List<Fix> Applied) Apply(string text, IEnumerable<Fix> fixes)
    {
        var lines = text.Split('\n').ToList();
        var applied = new List<Fix>();
        // bottom-up, so inserting a line doesn't move the lines later fixes point at
        foreach (var fix in fixes.OrderByDescending(f => f.Line))
        {
            int i = fix.Line - 1;
            if (i < 0 || i >= lines.Count) continue;
            if (fix.Insert)
            {
                var indent = lines[i][..(lines[i].Length - lines[i].TrimStart().Length)];
                lines.Insert(i, indent + fix.New + (lines[i].EndsWith('\r') ? "\r" : ""));
                applied.Add(fix);
                continue;
            }
            if (fix.Column is int c)
            {
                if (c + fix.Old.Length > lines[i].Length || string.CompareOrdinal(lines[i], c, fix.Old, 0, fix.Old.Length) != 0) continue;
                lines[i] = lines[i][..c] + fix.New + lines[i][(c + fix.Old.Length)..];
                applied.Add(fix);
                continue;
            }
            // whole words for names (lukcy, res.ticker); exact text otherwise (string literals)
            var pattern = Regex.Escape(fix.Old);
            if (Regex.IsMatch(fix.Old, @"^\w")) pattern = @"(?<![\w])" + pattern;
            if (Regex.IsMatch(fix.Old, @"\w$")) pattern += @"(?![\w])";
            var re = new Regex(pattern);
            if (!re.IsMatch(lines[i])) continue;
            lines[i] = re.Replace(lines[i], fix.New.Replace("$", "$$"), fix.All ? -1 : 1);
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

    /// Optimal string alignment distance: edits, counting a swap of neighbours as one (lenght → length).
    public static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }

    /// The single closest candidate within reach, or null if none (or several tie).
    public static string? Closest(string name, IEnumerable<string> candidates)
    {
        int reach = name.Length <= 4 ? 1 : 2;
        var scored = candidates.Distinct().Select(c => (c, d: Distance(name, c))).Where(x => x.d > 0 && x.d <= reach).OrderBy(x => x.d).ToList();
        if (scored.Count == 0 || (scored.Count > 1 && scored[1].d == scored[0].d)) return null;
        return scored[0].c;
    }

    /// Plain strings with `${x}` / `{$x}`: a `}` straight after an interpolation closes is a typo.
    static readonly Regex StrayBrace = new(@"(\$\{[^{}]*\}|\{\$[^{}]*\})\}");

    public static string? WithoutStrayBraces(string rawString) =>
        StrayBrace.IsMatch(rawString) ? StrayBrace.Replace(rawString, "$1") : null;
}
