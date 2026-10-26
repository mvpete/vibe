namespace Vibec;

public enum Tok { Ident, Keyword, Int, Float, Duration, String, Interp, Prompt, InlinePrompt, Op, Newline, Eof }

public record Token(Tok Type, string Text, int Line, object? Value = null);

public class VibeError(string message, int line = 0)
    : Exception(line > 0 ? $"line {line}: {message}" : message)
{
    public int Line { get; } = line;
}

/// Turns .vibe source into tokens. Newlines separate statements, except inside () and [].
public static class Lexer
{
    public static readonly HashSet<string> Keywords =
    [
        "int", "float", "string", "bool", "vibe", "list", "void", "var", "let",
        "return", "if", "else", "while", "for", "in", "of", "repeat", "times",
        "say", "ask", "vibecheck", "assert", "try", "sus", "catch", "break", "continue",
        "yeah", "nah", "true", "false", "maybe", "idk", "null",
        "and", "or", "not", "kinda",
        // the rest of the gen z dictionary, with standard spellings where one exists
        "nocap", "cap", "W", "L", "fr", "ghost", "nvm", "itsgiving", "glowup",
        "throw", "yeet", "exit", "periodt", "sleep", "touchgrass", "switch", "vibeshift",
        "const", "based", "global", "rentfree", "assume", "delulu", "ick",
        "spill", "lowkey", "highkey", "maincharacter",
        // modules
        "json", "use", "as", "public", "flex", "native",
    ];

    static readonly string[] Operators =
        ["->", "=>", "==", "!=", "<=", ">=", "+=", "-=", "*=", "/=", "++", "--", "&&", "||",
         "+", "-", "*", "/", "%", "<", ">", "=", "!",
         "(", ")", "{", "}", "[", "]", ",", ".", ":", ";"];

    public static List<Token> Lex(string src)
    {
        var tokens = new List<Token>();
        int i = 0, line = 1, depth = 0;

        void Push(Tok type, string text, object? value = null) => tokens.Add(new Token(type, text, line, value));

        while (i < src.Length)
        {
            char c = src[i];

            if (c == '\n')
            {
                if (depth == 0 && tokens.Count > 0 && tokens[^1].Type != Tok.Newline) Push(Tok.Newline, "\\n");
                line++; i++;
                continue;
            }
            if (char.IsWhiteSpace(c)) { i++; continue; }

            // %inline prompt%: where a value is expected and it closes on the same line.
            // Between two values, % is still the remainder operator.
            if (c == '%' && AtOperandPosition(tokens) && InlinePromptEnd(src, i) is int end)
            {
                var text = src[(i + 1)..end].Replace("%%", "%").Trim();
                if (text.Length == 0) throw new VibeError("an empty %prompt%. say something!", line);
                Push(Tok.InlinePrompt, text, text);
                i = end + 1;
                continue;
            }

            // #prompt / %prompt: raw text straight to the model, to end of line or as a { block }
            if ((c == '#' || c == '%') && IsPromptDirective(src, i))
            {
                int start = line;
                i += "#prompt".Length;
                while (i < src.Length && src[i] is ' ' or '\t') i++;
                string text;
                if (i < src.Length && src[i] == '{')
                {
                    int depth2 = 1, s = ++i;
                    while (i < src.Length && depth2 > 0)
                    {
                        if (src[i] == '{') depth2++;
                        else if (src[i] == '}') depth2--;
                        if (src[i] == '\n') line++;
                        i++;
                    }
                    if (depth2 > 0) throw new VibeError("this #prompt block never closes", start);
                    text = src[s..(i - 1)];
                }
                else
                {
                    int s = i;
                    while (i < src.Length && src[i] != '\n') i++;
                    text = src[s..i];
                }
                text = Dedent(text);
                if (text.Length == 0) throw new VibeError("an empty #prompt. say something!", start);
                tokens.Add(new Token(Tok.Prompt, text, start, text));
                continue;
            }

            // # (Python) and // (C#/JS) line comments, /* */ block comments
            if (c == '#' || (c == '/' && i + 1 < src.Length && src[i + 1] == '/'))
            {
                while (i < src.Length && src[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                i += 2;
                while (i < src.Length && !(src[i] == '*' && i + 1 < src.Length && src[i + 1] == '/'))
                {
                    if (src[i] == '\n') line++;
                    i++;
                }
                i += 2;
                continue;
            }

            // f"..." (Python) and $"..." (C#) interpolated strings
            bool interp = (c == 'f' || c == '$') && i + 1 < src.Length && src[i + 1] == '"';
            if (interp || c == '"')
            {
                if (interp) i++;
                int start = line;
                var sb = new System.Text.StringBuilder();
                i++;
                while (i < src.Length && src[i] != '"')
                {
                    if (src[i] == '\\' && i + 1 < src.Length)
                    {
                        char n = src[++i];
                        sb.Append(n switch { 'n' => '\n', 't' => '\t', _ => n });
                    }
                    else
                    {
                        if (src[i] == '\n') line++;
                        sb.Append(src[i]);
                    }
                    i++;
                }
                if (i >= src.Length) throw new VibeError("this string never ends. commitment issues?", start);
                i++;
                var text = sb.ToString();
                if (!interp && PlainInterpolation(text) is { } converted) { interp = true; text = converted; }
                tokens.Add(new Token(interp ? Tok.Interp : Tok.String, text, start, text));
                continue;
            }

            if (char.IsAsciiDigit(c))
            {
                int s = i;
                while (i < src.Length && (char.IsAsciiDigit(src[i]) || src[i] == '_')) i++;
                bool isFloat = false;
                if (i + 1 < src.Length && src[i] == '.' && char.IsAsciiDigit(src[i + 1]))
                {
                    isFloat = true;
                    i++;
                    while (i < src.Length && (char.IsAsciiDigit(src[i]) || src[i] == '_')) i++;
                }
                var text = src[s..i].Replace("_", "");

                // 500ms, 2s, 5m, 1h, 1d: durations, in seconds
                if (DurationUnit(src, i) is { } unit)
                {
                    var seconds = double.Parse(text, System.Globalization.CultureInfo.InvariantCulture) * unit.Seconds;
                    i += unit.Suffix.Length;
                    Push(Tok.Duration, text + unit.Suffix, seconds);
                    continue;
                }
                if (isFloat) Push(Tok.Float, text, double.Parse(text, System.Globalization.CultureInfo.InvariantCulture));
                else Push(Tok.Int, text, long.Parse(text));
                continue;
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                int s = i;
                while (i < src.Length && (char.IsAsciiLetterOrDigit(src[i]) || src[i] == '_')) i++;
                var id = src[s..i];
                Push(Keywords.Contains(id) ? Tok.Keyword : Tok.Ident, id);
                continue;
            }

            var op = Operators.FirstOrDefault(o => string.CompareOrdinal(src, i, o, 0, o.Length) == 0);
            if (op != null)
            {
                if (op is "(" or "[") depth++;
                if (op is ")" or "]") depth = Math.Max(0, depth - 1);
                if (op == ";") Push(Tok.Newline, ";");
                else Push(Tok.Op, op);
                i += op.Length;
                continue;
            }

            throw new VibeError($"'{c}' is not a vibe", line);
        }
        Push(Tok.Eof, "<eof>");
        return tokens;
    }

    static readonly HashSet<string> ValueKeywords = ["true", "false", "yeah", "nah", "nocap", "cap", "W", "L", "null", "idk", "maybe"];

    static readonly (string Suffix, double Seconds)[] DurationUnits = [("ms", 0.001), ("s", 1), ("m", 60), ("h", 3600), ("d", 86400)];

    static (string Suffix, double Seconds)? DurationUnit(string src, int i)
    {
        foreach (var u in DurationUnits)
        {
            int end = i + u.Suffix.Length;
            if (string.CompareOrdinal(src, i, u.Suffix, 0, u.Suffix.Length) == 0
                && (end >= src.Length || !(char.IsAsciiLetterOrDigit(src[end]) || src[end] == '_')))
                return u;
        }
        return null;
    }

    /// True when the next token would start a value (so `%` opens a prompt, not a remainder).
    static bool AtOperandPosition(List<Token> tokens)
    {
        if (tokens.Count == 0) return true;
        var prev = tokens[^1];
        return prev.Type switch
        {
            Tok.Ident or Tok.Int or Tok.Float or Tok.Duration or Tok.String or Tok.Interp or Tok.InlinePrompt => false,
            Tok.Op => prev.Text is not (")" or "]"),
            Tok.Keyword => !ValueKeywords.Contains(prev.Text),
            _ => true,
        };
    }

    static int? InlinePromptEnd(string src, int i)
    {
        for (int j = i + 1; j < src.Length && src[j] != '\n'; j++)
        {
            if (src[j] != '%') continue;
            if (j + 1 < src.Length && src[j + 1] == '%') { j++; continue; } // %% is a literal %
            return j;
        }
        return null;
    }

    /// Plain strings interpolate `{$expr}` and `${expr}`; every other brace stays literal.
    /// Returns null when there's nothing to interpolate.
    static string? PlainInterpolation(string s)
    {
        if (!s.Contains("{$") && !s.Contains("${")) return null;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            bool open = (s[i] == '{' && i + 1 < s.Length && s[i + 1] == '$') || (s[i] == '$' && i + 1 < s.Length && s[i + 1] == '{');
            int close = open ? s.IndexOf('}', i) : -1;
            if (open && close > 0)
            {
                sb.Append('{').Append(s[(i + 2)..close]).Append('}');
                i = close;
            }
            else if (s[i] is '{' or '}') sb.Append(s[i], 2);
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    static bool IsPromptDirective(string src, int i) =>
        string.CompareOrdinal(src, i + 1, "prompt", 0, 6) == 0
        && (i + 7 >= src.Length || src[i + 7] is ' ' or '\t' or '{' or '\n' or '\r');

    static string Dedent(string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        int indent = lines.Where(l => l.Trim().Length > 0)
                          .Select(l => l.Length - l.TrimStart().Length)
                          .DefaultIfEmpty(0).Min();
        return string.Join("\n", lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart())).Trim();
    }
}
