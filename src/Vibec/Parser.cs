namespace Vibec;

/// Recursive-descent parser. Statements end at a newline or ';'.
public class Parser(List<Token> tokens)
{
    int _pos;
    bool _inPattern; // switch patterns: `name =>` is a pattern, not a lambda

    public static List<Stmt> ParseSource(string src) => new Parser(Lexer.Lex(src)).ParseProgram();

    Token Peek(int ahead = 0) => tokens[Math.Min(_pos + ahead, tokens.Count - 1)];
    Token Next() => tokens[_pos++];
    bool AtTok(Token t, string text) => t.Type is Tok.Op or Tok.Keyword && t.Text == text;
    bool At(string text) => AtTok(Peek(), text);
    bool Match(string text) { if (At(text)) { _pos++; return true; } return false; }

    bool MatchAny(params string[] texts)
    {
        foreach (var t in texts) if (Match(t)) return true;
        return false;
    }

    Token Expect(string text)
    {
        if (At(text)) return Next();
        throw new VibeError($"expected '{text}' but got '{Peek().Text}'. not the vibe", Peek().Line);
    }

    Token ExpectIdent()
    {
        if (Peek().Type == Tok.Ident) return Next();
        throw new VibeError($"expected a name but got '{Peek().Text}'", Peek().Line);
    }

    void SkipNewlines() { while (Peek().Type == Tok.Newline) _pos++; }

    void EndStatement()
    {
        if (Peek().Type is Tok.Newline) { SkipNewlines(); return; }
        if (Peek().Type is Tok.Eof || At("}")) return;
        throw new VibeError($"unexpected '{Peek().Text}'. one thought per line, please", Peek().Line);
    }

    public List<Stmt> ParseProgram()
    {
        var stmts = new List<Stmt>();
        SkipNewlines();
        while (Peek().Type != Tok.Eof) { stmts.Add(Statement()); SkipNewlines(); }
        foreach (var p in stmts.TakeWhile(s => s is PromptStmt).Cast<PromptStmt>()) p.Global = true;
        return stmts;
    }

    // ---------- types ----------

    static readonly HashSet<string> TypeWords = ["int", "float", "string", "bool", "vibe", "json", "list", "void"];

    bool AtType(int ahead = 0) => Peek(ahead).Type == Tok.Keyword && TypeWords.Contains(Peek(ahead).Text);

    VType ParseType()
    {
        var t = Next();
        switch (t.Text)
        {
            case "int": return VType.Int;
            case "float": return VType.Float;
            case "string": return VType.Str;
            case "bool": return VType.Bool;
            case "vibe": return VType.Vibe;
            case "json": return VType.Json;
            case "void": return VType.Void;
            case "list":
                if (!Match("<")) return VType.ListOf(VType.Vibe);
                var elem = ParseType();
                Expect(">");
                return VType.ListOf(elem);
            default: throw new VibeError($"'{t.Text}' isn't a type", t.Line);
        }
    }

    /// Skips over a type without consuming it; returns how many tokens it spans.
    int TypeLength(int ahead)
    {
        if (!AtType(ahead)) return 0;
        if (Peek(ahead).Text != "list" || !AtTok(Peek(ahead + 1), "<")) return 1;
        int inner = TypeLength(ahead + 2);
        return inner == 0 ? 0 : inner + 3;
    }

    // ---------- statements ----------

    List<Stmt> Block()
    {
        SkipNewlines();
        Expect("{");
        SkipNewlines();
        var body = new List<Stmt>();
        while (!At("}"))
        {
            if (Peek().Type == Tok.Eof) throw new VibeError("block never closed. you left a '{' on read", Peek().Line);
            body.Add(Statement());
            SkipNewlines();
        }
        Expect("}");
        return body;
    }

    Stmt Statement()
    {
        int line = Peek().Line;
        Stmt s;

        if (Peek().Type == Tok.Prompt) return new PromptStmt(line, (string)Next().Value!);

        if (Match("use"))
        {
            var module = ExpectIdent().Text;
            string? alias = Match("as") ? ExpectIdent().Text : null;
            EndStatement();
            return new UseStmt(line, module, alias);
        }

        bool isPublic = false, isNative = false;
        while (true)
        {
            if (MatchAny("public", "flex")) isPublic = true;
            else if (Match("native")) isNative = true;
            else break;
        }

        int typeLen = TypeLength(0);
        if (typeLen > 0 && Peek(typeLen).Type is Tok.Ident or Tok.Keyword && AtTok(Peek(typeLen + 1), "("))
        {
            // C#-style function (a keyword can name one: modules are called as time.sleep): string greet(string who) { ... }
            return Function(line, isPublic, isNative);
        }
        if (isNative) throw new VibeError("only functions can be native", line);
        if (isPublic)
        {
            var decl = Declaration(line);
            EndStatement();
            return new Decl(decl.Line, decl.DeclType, decl.Name, decl.Init) { Const = decl.Const, Global = decl.Global, Public = true };
        }

        if ((typeLen > 0 && Peek(typeLen).Type == Tok.Ident) || At("var") || At("let") || At("const") || At("based") || At("global") || At("rentfree"))
            s = Declaration(line);
        else if (Match("if")) return IfStmt(line);
        else if (MatchAny("switch", "vibeshift")) return SwitchStmt(line);
        else if (Match("maincharacter")) { var body = Block(); SkipNewlines(); return new MainBlock(line, body); }
        else if (Match("while")) { var c = Expression(); return new While(line, c, Block()); }
        else if (Match("for")) return ForStmt(line);
        else if (Match("repeat"))
        {
            var count = Expression();
            Expect("times");
            return new Repeat(line, count, Block());
        }
        else if (Match("try"))
        {
            var body = Block();
            SkipNewlines();
            if (!MatchAny("catch", "sus", "ick")) throw new VibeError("a 'try' with no 'catch'. bold. reckless.", Peek().Line);
            string err = "e";
            if (Match("(")) { err = ExpectIdent().Text; Expect(")"); }
            return new TryCatch(line, body, err, Block());
        }
        else if (MatchAny("return", "itsgiving"))
            s = new Return(line, AtStatementEnd() ? null : Expression());
        else if (MatchAny("throw", "yeet")) s = new Throw(line, Expression());
        else if (MatchAny("exit", "periodt")) s = new Exit(line, AtStatementEnd() ? null : Expression());
        else if (MatchAny("sleep", "touchgrass")) s = new Sleep(line, Expression());
        else if (Match("glowup"))
        {
            var target = Expression();
            RequireAssignable(target, line);
            s = new Assign(line, target, "+=", new Lit(line, 1L, VType.Int));
        }
        else if (MatchAny("lowkey", "highkey", "spill"))
        {
            var mode = tokens[_pos - 1].Text switch { "lowkey" => SayMode.Low, "highkey" => SayMode.High, _ => SayMode.Spill };
            var args = new List<Expr> { Expression() };
            while (mode != SayMode.Spill && Match(",")) args.Add(Expression());
            s = new Say(line, args) { Mode = mode };
        }
        else if (Peek().Type == Tok.Ident && Peek().Text == "print" && !AtTok(Peek(1), "("))
        {
            // print "hello" - Python 2 never really left us
            Next();
            var args = new List<Expr> { Expression() };
            while (Match(",")) args.Add(Expression());
            s = new Say(line, args);
        }
        else if (Match("say"))
        {
            var args = new List<Expr> { Expression() };
            while (Match(",")) args.Add(Expression());
            s = new Say(line, args);
        }
        else if (MatchAny("vibecheck", "assert", "fr")) s = new VibeCheck(line, Expression());
        else if (MatchAny("assume", "delulu")) s = new VibeCheck(line, Expression()) { Assume = true };
        else if (MatchAny("break", "ghost")) s = new Break(line);
        else if (MatchAny("continue", "nvm")) s = new Continue(line);
        else s = SimpleStatement(line);

        EndStatement();
        return s;
    }

    bool AtStatementEnd() => Peek().Type is Tok.Newline or Tok.Eof || At("}");

    /// [const|based] [global|rentfree] (type | var | let) name [= value]
    Decl Declaration(int line)
    {
        bool isConst = false, isGlobal = false;
        while (true)
        {
            if (MatchAny("const", "based")) isConst = true;
            else if (MatchAny("global", "rentfree")) isGlobal = true;
            else break;
        }

        VType? type = null;
        if (!MatchAny("var", "let"))
        {
            if (!AtType()) throw new VibeError($"expected a type or 'var' but got '{Peek().Text}'", line);
            type = ParseType();
            if (type == VType.Void) throw new VibeError("a void variable? that's just nothing with extra steps", line);
        }
        var name = ExpectIdent().Text;
        return new Decl(line, type, name, Match("=") ? Expression() : null) { Const = isConst, Global = isGlobal };
    }

    /// switch subject { a, b => stmt   c => { ... }   _ => ... }
    Switch SwitchStmt(int line)
    {
        var subject = Expression();
        SkipNewlines();
        Expect("{");
        SkipNewlines();
        var arms = new List<SwitchArm>();
        while (!At("}"))
        {
            if (Peek().Type == Tok.Eof) throw new VibeError("this switch never closes", line);
            var patterns = new List<Expr>();
            if (MatchAny("else") || (Peek().Type == Tok.Ident && Peek().Text == "_" && Next() != null)) { /* default */ }
            else
            {
                _inPattern = true;
                do patterns.Add(Expression()); while (Match(","));
                _inPattern = false;
            }
            Expect("=>");
            var body = At("{") ? Block() : [Statement()];
            arms.Add(new SwitchArm(patterns, body));
            SkipNewlines();
        }
        Expect("}");
        SkipNewlines();
        if (arms.Count(a => a.Patterns.Count == 0) > 1) throw new VibeError("a switch can only have one default arm", line);
        return new Switch(line, subject, arms);
    }

    Stmt SimpleStatement(int line)
    {
        var e = Expression();

        if (At("++") || At("--"))
        {
            RequireAssignable(e, line);
            var op = Next().Text == "++" ? "+=" : "-=";
            return new Assign(line, e, op, new Lit(line, 1L, VType.Int));
        }
        if (At("=") || At("+=") || At("-=") || At("*=") || At("/="))
        {
            RequireAssignable(e, line);
            var op = Next().Text;
            return new Assign(line, e, op, Expression());
        }

        // a %prompt% on its own line is a step, not a value
        if (e is VibeExpr v) return new PromptStmt(line, v.Text);

        // print(...) is the Python/JS spelling of `say`
        if (e is Call { Name: "print" } call) return new Say(line, call.Args);
        if (e is Member { Target: Ident { Name: "console" }, Name: "log", Args: { } logArgs }) return new Say(line, logArgs);
        return new ExprStmt(line, e);
    }

    static void RequireAssignable(Expr e, int line)
    {
        if (e is not (Ident or Index or Member { Args: null }))
            throw new VibeError("you can only assign to a name, an index, or a property", line);
    }

    FnDecl Function(int line, bool isPublic = false, bool isNative = false)
    {
        var ret = ParseType();
        var name = Peek().Type == Tok.Keyword ? Next().Text : ExpectIdent().Text;
        Expect("(");
        var ps = new List<Param>();
        if (!At(")"))
        {
            do
            {
                if (!AtType()) throw new VibeError("params need a type. even a loose one. try 'vibe'", Peek().Line);
                var pt = ParseType();
                var pname = ExpectIdent().Text;
                ps.Add(new Param(pt, pname, Match("=") ? Expression() : null));
            } while (Match(","));
        }
        Expect(")");

        // no body: a contract (or a native, for the standard library)
        int save = _pos;
        SkipNewlines();
        if (!At("{"))
        {
            _pos = save;
            EndStatement();
            return new FnDecl(line, name, ps, ret, []) { HasBody = false, Native = isNative, Public = isPublic };
        }
        if (isNative) throw new VibeError($"native '{name}' can't have a body; the target implements it", line);
        var body = Block();
        SkipNewlines();
        return new FnDecl(line, name, ps, ret, body) { Public = isPublic };
    }

    If IfStmt(int line)
    {
        var cond = Expression();
        var then = Block();
        List<Stmt>? els = null;

        // `else` may sit on the line after `}`
        int save = _pos;
        SkipNewlines();
        if (Match("else"))
        {
            if (At("if")) { int l = Next().Line; els = [IfStmt(l)]; }
            else { els = Block(); SkipNewlines(); }
        }
        else _pos = save;

        SkipNewlines();
        return new If(line, cond, then, els);
    }

    For ForStmt(int line)
    {
        // for x in xs { }  |  for (x in xs) { }  |  for (var x of xs) { }
        bool parens = At("(");
        if (parens) Next();
        MatchAny("var", "let");
        var v = ExpectIdent().Text;
        if (!MatchAny("in", "of")) throw new VibeError("for loops go 'for x in things'", Peek().Line);
        var iter = Expression();
        if (parens) Expect(")");
        var body = Block();
        SkipNewlines();
        return new For(line, v, iter, body);
    }

    // ---------- expressions ----------

    public Expr Expression() => Or();

    Expr Or()
    {
        var e = And();
        while (At("or") || At("||")) { int l = Next().Line; e = new Binary(l, "or", e, And()); }
        return e;
    }

    Expr And()
    {
        var e = Not();
        while (At("and") || At("&&")) { int l = Next().Line; e = new Binary(l, "and", e, Not()); }
        return e;
    }

    Expr Not()
    {
        if (At("not") || At("!")) { int l = Next().Line; return new Unary(l, "not", Not()); }
        return Comparison();
    }

    static readonly string[] CompareOps = ["==", "!=", "<", ">", "<=", ">=", "kinda"];

    Expr Comparison()
    {
        var e = Additive();
        while (CompareOps.Any(At))
        {
            var op = Next();
            e = new Binary(op.Line, op.Text, e, Additive());
        }
        return e;
    }

    Expr Additive()
    {
        var e = Multiplicative();
        while (At("+") || At("-")) { var op = Next(); e = new Binary(op.Line, op.Text, e, Multiplicative()); }
        return e;
    }

    Expr Multiplicative()
    {
        var e = UnaryExpr();
        while (At("*") || At("/") || At("%")) { var op = Next(); e = new Binary(op.Line, op.Text, e, UnaryExpr()); }
        return e;
    }

    Expr UnaryExpr()
    {
        if (At("-")) { int l = Next().Line; return new Unary(l, "-", UnaryExpr()); }
        return Postfix();
    }

    bool NamedArgAhead() => Peek().Type is Tok.Ident or Tok.Keyword && AtTok(Peek(1), ":");

    void Arg(List<Expr> args, List<NamedArg> named)
    {
        if (NamedArgAhead())
        {
            var name = Next().Text;
            Next(); // :
            if (named.Any(n => n.Name == name)) throw new VibeError($"'{name}:' given twice", Peek().Line);
            named.Add(new NamedArg(name, Expression()));
            return;
        }
        if (named.Count > 0) throw new VibeError("positional arguments go before named ones", Peek().Line);
        args.Add(Expression());
    }

    (List<Expr> Args, List<NamedArg> Named) Args()
    {
        Expect("(");
        var args = new List<Expr>();
        var named = new List<NamedArg>();
        while (!At(")"))
        {
            Arg(args, named);
            if (!Match(",") && !NamedArgAhead()) break;
        }
        Expect(")");
        return (args, named);
    }

    static readonly HashSet<string> ValueKeywords = ["true", "false", "yeah", "nah", "nocap", "cap", "W", "L", "null", "idk", "maybe", "ask"];

    /// Could this token begin an argument of a paren-less call (`greet "world"`)?
    /// Operators never do, so `x - 1` stays subtraction.
    bool StartsArgument(Token t) => t.Type switch
    {
        Tok.Int or Tok.Float or Tok.Duration or Tok.String or Tok.Interp or Tok.InlinePrompt or Tok.Ident => true,
        Tok.Keyword => ValueKeywords.Contains(t.Text),
        Tok.Op => t.Text == "[",
        _ => false,
    };

    /// `greet "world"`, `add 1, 2`, `http.get url timeout: 2s` (named args need no comma)
    (List<Expr> Args, List<NamedArg> Named) BareArgs()
    {
        var args = new List<Expr>();
        var named = new List<NamedArg>();
        Arg(args, named);
        while (Match(",") || NamedArgAhead()) Arg(args, named);
        return (args, named);
    }

    Expr Postfix()
    {
        var e = Primary();
        if (e is Ident bare && StartsArgument(Peek()) && !LambdaAhead())
        {
            var (a, n) = BareArgs();
            return new Call(bare.Line, bare.Name, a) { Named = n };
        }

        while (true)
        {
            if (At("("))
            {
                if (e is not Ident id) throw new VibeError("only named functions can be called", e.Line);
                var (a, n) = Args();
                e = new Call(id.Line, id.Name, a) { Named = n };
            }
            else if (At("["))
            {
                int l = Next().Line;
                var idx = Expression();
                Expect("]");
                e = new Index(l, e, idx);
            }
            else if (At("."))
            {
                int l = Next().Line;
                var name = Peek().Type is Tok.Ident or Tok.Keyword ? Next().Text : throw new VibeError("expected a member name after '.'", l);
                if (At("(")) { var (a, n) = Args(); e = new Member(l, e, name, a) { Named = n }; }
                else if (StartsArgument(Peek())) { var (a, n) = BareArgs(); return new Member(l, e, name, a) { Named = n }; }
                else e = new Member(l, e, name, null);
            }
            else return e;
        }
    }

    bool LambdaAhead()
    {
        if (_inPattern) return false;
        // x => ...
        if (Peek().Type == Tok.Ident && AtTok(Peek(1), "=>")) return true;
        // (a, b) => ...   or   () => ...
        if (!At("(")) return false;
        int i = 1;
        if (AtTok(Peek(i), ")")) return AtTok(Peek(i + 1), "=>");
        while (Peek(i).Type == Tok.Ident)
        {
            if (AtTok(Peek(i + 1), ")")) return AtTok(Peek(i + 2), "=>");
            if (!AtTok(Peek(i + 1), ",")) return false;
            i += 2;
        }
        return false;
    }

    Expr LambdaExpr()
    {
        int line = Peek().Line;
        var ps = new List<string>();
        if (Peek().Type == Tok.Ident) ps.Add(Next().Text);
        else
        {
            Expect("(");
            if (!At(")")) do ps.Add(ExpectIdent().Text); while (Match(","));
            Expect(")");
        }
        Expect("=>");
        return new Lambda(line, ps, Expression());
    }

    Expr Primary()
    {
        if (LambdaAhead()) return LambdaExpr();

        var t = Next();
        switch (t.Type)
        {
            case Tok.Int: return new Lit(t.Line, t.Value, VType.Int);
            case Tok.Float: return new Lit(t.Line, t.Value, VType.Float);
            case Tok.Duration: return new Lit(t.Line, t.Value, VType.Float) { Display = t.Text };
            case Tok.String: return new Lit(t.Line, t.Value, VType.Str);
            case Tok.Interp: return Interpolated(t);
            case Tok.InlinePrompt: return new VibeExpr(t.Line, (string)t.Value!);
            case Tok.Ident:
                // input("...") is the Python spelling of ask
                if (t.Text == "input" && At("(")) { var (a, _) = Args(); return new Ask(t.Line, a.FirstOrDefault()); }
                return new Ident(t.Line, t.Text);
        }

        switch (t.Text)
        {
            case "yeah": case "true": case "nocap": case "W": return new Lit(t.Line, true, VType.Bool);
            case "nah": case "false": case "cap": case "L": return new Lit(t.Line, false, VType.Bool);
            case "maybe": return new Lit(t.Line, Maybe.Instance, VType.Bool);
            case "idk": case "null": return new Lit(t.Line, null, VType.Vibe);
            case "vibe" when At("<"):
                Next();
                var to = ParseType();
                Expect(">");
                Expect("(");
                var inner = Expression();
                Expect(")");
                return new Cast(t.Line, to, inner);
            case "ask":
                if (At("(")) { var (a, _) = Args(); return new Ask(t.Line, a.FirstOrDefault()); }
                return new Ask(t.Line, Peek().Type == Tok.String ? Primary() : null);
            case "(":
                var e = Expression();
                Expect(")");
                return e;
            case "[":
                var items = new List<Expr>();
                if (!At("]")) do items.Add(Expression()); while (Match(","));
                Expect("]");
                return new ListLit(t.Line, items);
        }

        throw new VibeError($"'{t.Text}' is not a vibe", t.Line);
    }

    static Interp Interpolated(Token t)
    {
        var text = (string)t.Value!;
        var parts = new List<object>();
        var chunk = new System.Text.StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '{' && i + 1 < text.Length && text[i + 1] == '{') { chunk.Append('{'); i++; continue; }
            if (text[i] == '}' && i + 1 < text.Length && text[i + 1] == '}') { chunk.Append('}'); i++; continue; }
            if (text[i] != '{') { chunk.Append(text[i]); continue; }

            int close = text.IndexOf('}', i);
            if (close < 0) throw new VibeError("unclosed '{' in an f-string", t.Line);
            if (chunk.Length > 0) { parts.Add(chunk.ToString()); chunk.Clear(); }
            var inner = Lexer.Lex(text[(i + 1)..close]).Select(k => k with { Line = t.Line }).ToList();
            parts.Add(new Parser(inner).Expression());
            i = close;
        }
        if (chunk.Length > 0) parts.Add(chunk.ToString());
        return new Interp(t.Line, parts);
    }
}
