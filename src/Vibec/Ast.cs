namespace Vibec;

public sealed class VType
{
    public string Name { get; }
    public VType? Elem { get; }

    VType(string name, VType? elem = null) { Name = name; Elem = elem; }

    public static readonly VType Int = new("int");
    public static readonly VType Float = new("float");
    public static readonly VType Str = new("string");
    public static readonly VType Bool = new("bool");
    public static readonly VType Vibe = new("vibe");
    public static readonly VType Void = new("void");
    public static readonly VType Fn = new("fn");
    public static readonly VType Json = new("json");
    public static VType ListOf(VType elem) => new("list", elem);

    public bool IsList => Name == "list";
    public bool IsNumeric => this == Int || this == Float;
    public bool IsDynamic => this == Vibe || this == Json;

    /// Parses the names this compiler prints ("int", "list<string>", ...), for model answers.
    public static VType? Parse(string? s)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (s.StartsWith("list<") && s.EndsWith('>'))
            return Parse(s[5..^1]) is { } e ? ListOf(e) : null;
        return s switch
        {
            "int" => Int, "float" => Float, "string" => Str, "bool" => Bool,
            "vibe" => Vibe, "void" => Void, "json" => Json, "list" => ListOf(Vibe), _ => null,
        };
    }

    public override string ToString() => IsList ? $"list<{Elem}>" : Name;
    public override bool Equals(object? obj) => obj is VType t && t.ToString() == ToString();
    public override int GetHashCode() => ToString().GetHashCode();
    public static bool operator ==(VType? a, VType? b) => a?.ToString() == b?.ToString();
    public static bool operator !=(VType? a, VType? b) => !(a == b);
}

/// The `maybe` literal: a coin flip, decided at runtime.
public sealed class Maybe { public static readonly Maybe Instance = new(); }

// ---------- expressions ----------

public abstract class Expr(int line)
{
    public int Line { get; } = line;
    public VType Type { get; set; } = VType.Vibe;
}

/// Value is long, double, string, bool, Maybe, or null.
public sealed class Lit : Expr
{
    public object? Value { get; }
    public string? Display { get; init; } // how it was written, when that's nicer than the value (2s)
    public Lit(int line, object? value, VType type) : base(line) { Value = value; Type = type; }
}

public sealed class ListLit(int line, List<Expr> items) : Expr(line)
{
    public List<Expr> Items { get; } = items;
}

/// f"..." / $"..." - parts are string chunks or expressions.
public sealed class Interp(int line, List<object> parts) : Expr(line)
{
    public List<object> Parts { get; } = parts;
}

public sealed class Ident(int line, string name) : Expr(line)
{
    public string Name { get; set; } = name;
    public string? RenamedFrom { get; set; } // set when the vibe pass fixed a typo
}

public sealed class Unary(int line, string op, Expr operand) : Expr(line)
{
    public string Op { get; } = op;
    public Expr Operand { get; set; } = operand;
}

public sealed class Binary(int line, string op, Expr left, Expr right) : Expr(line)
{
    public string Op { get; } = op;
    public Expr Left { get; set; } = left;
    public Expr Right { get; set; } = right;
}

/// `timeout: 2s`
public sealed class NamedArg(string name, Expr value)
{
    public string Name { get; } = name;
    public Expr Value { get; set; } = value;
}

public sealed class Call(int line, string name, List<Expr> args) : Expr(line)
{
    public string Name { get; } = name;
    public List<Expr> Args { get; } = args;
    public List<NamedArg> Named { get; init; } = [];
    public string? Meaning { get; set; } // for functions the vibe pass had to interpret
}

/// `target.name` or `target.name(args)`.
public sealed class Member(int line, Expr target, string name, List<Expr>? args) : Expr(line)
{
    public Expr Target { get; set; } = target;
    public string Name { get; } = name;
    public List<Expr>? Args { get; } = args; // null = property access
    public List<NamedArg> Named { get; init; } = [];
    public string? Module { get; set; } // set when this is a call into a module (std or vibed)
    public string? Meaning { get; set; }
}

public sealed class Index(int line, Expr target, Expr index) : Expr(line)
{
    public Expr Target { get; set; } = target;
    public Expr IndexExpr { get; set; } = index;
}

public sealed class Lambda(int line, List<string> ps, Expr body) : Expr(line)
{
    public List<string> Params { get; } = ps;
    public Expr Body { get; set; } = body;
    public List<VType> ParamTypes { get; set; } = [];
}

public sealed class Ask(int line, Expr? prompt) : Expr(line)
{
    public Expr? Prompt { get; set; } = prompt;
}

/// `%...%` - freeform natural-language behaviour the generated code implements.
public sealed class VibeExpr(int line, string text) : Expr(line)
{
    public string Text { get; } = text;
}

/// `vibe<T>(x)` - an explicit strongly-loose conversion; same rules as the implicit kind.
public sealed class Cast(int line, VType to, Expr inner) : Expr(line)
{
    public VType To { get; } = to;
    public Expr Inner { get; set; } = inner;
}

public enum CoerceStatus { Runtime, Folded, Deferred }

/// Strongly-loose typing in one node. Literal sources are resolved at compile time
/// (Folded) or handed to the generator (Deferred); anything else converts at Runtime.
public sealed class Coerce : Expr
{
    public Expr Inner { get; }
    public CoerceStatus Status { get; set; } = CoerceStatus.Runtime;
    public object? Result { get; set; }
    public string? Source { get; set; } // "rule" or "vibe"
    public string? Why { get; set; }
    public VType To => Type;

    public Coerce(int line, Expr inner, VType to) : base(line) { Inner = inner; Type = to; }
}

// ---------- statements ----------

public abstract class Stmt(int line) { public int Line { get; } = line; }

public sealed class Decl(int line, VType? type, string name, Expr? init) : Stmt(line)
{
    public VType? DeclType { get; } = type;     // null = var/let
    public VType Resolved { get; set; } = VType.Vibe;
    public string Name { get; } = name;
    public Expr? Init { get; set; } = init;
    public bool Const { get; init; }     // const / based
    public bool Global { get; init; }    // global / rentfree: visible everywhere, even before it's declared
    public bool Public { get; init; }    // public / flex
}

public sealed class Assign(int line, Expr target, string op, Expr value) : Stmt(line)
{
    public Expr Target { get; set; } = target;
    public string Op { get; } = op;
    public Expr Value { get; set; } = value;
}

public sealed class ExprStmt(int line, Expr expr) : Stmt(line) { public Expr Expr { get; set; } = expr; }

public record Param(VType Type, string Name, Expr? Default = null);

public sealed class FnDecl(int line, string name, List<Param> ps, VType ret, List<Stmt> body) : Stmt(line)
{
    public string Name { get; } = name;
    public List<Param> Params { get; } = ps;
    public VType Ret { get; } = ret;
    public List<Stmt> Body { get; } = body;
    public bool Generated { get; set; } // written by the vibe pass, not the programmer
    public bool HasBody { get; init; } = true; // false = a contract: just the signature
    public bool Native { get; init; }   // implemented by the backend in the target (standard library)
    public bool Public { get; init; }   // public / flex: visible to modules that use this one
}

public sealed class If(int line, Expr cond, List<Stmt> then, List<Stmt>? els) : Stmt(line)
{
    public Expr Cond { get; set; } = cond;
    public List<Stmt> Then { get; } = then;
    public List<Stmt>? Else { get; } = els;
}

public sealed class While(int line, Expr cond, List<Stmt> body) : Stmt(line)
{
    public Expr Cond { get; set; } = cond;
    public List<Stmt> Body { get; } = body;
}

public sealed class For(int line, string var, Expr iter, List<Stmt> body) : Stmt(line)
{
    public string Var { get; } = var;
    public Expr Iter { get; set; } = iter;
    public List<Stmt> Body { get; } = body;
    public VType VarType { get; set; } = VType.Vibe;
    public VType? DeclType { get; init; } // for (string s in xs): each element converts to this
}

/// for (int i = 0; i < n; i++) { }
public sealed class CFor(int line, Stmt? init, Expr? cond, Stmt? step, List<Stmt> body) : Stmt(line)
{
    public Stmt? Init { get; } = init;
    public Expr? Cond { get; set; } = cond;
    public Stmt? Step { get; } = step;
    public List<Stmt> Body { get; } = body;
}

public sealed class Repeat(int line, Expr count, List<Stmt> body) : Stmt(line)
{
    public Expr Count { get; set; } = count;
    public List<Stmt> Body { get; } = body;
}

public sealed class Return(int line, Expr? value) : Stmt(line) { public Expr? Value { get; set; } = value; }

public enum SayMode { Normal, Low, High, Spill }

/// print / say, plus lowkey (stderr), highkey (shouted) and spill (debug: `total = 3`).
public sealed class Say(int line, List<Expr> args) : Stmt(line)
{
    public List<Expr> Args { get; } = args;
    public SayMode Mode { get; init; }
    public string? Label { get; set; } // spill: the expression as written
    public List<NamedArg> Named { get; init; } = []; // end: and sep:, Python style
}

/// assert / vibecheck / fr. With Assume (delulu), it's taken as true and never checked.
public sealed class VibeCheck(int line, Expr cond) : Stmt(line)
{
    public Expr Cond { get; set; } = cond;
    public bool Assume { get; init; }
}

/// throw / yeet
public sealed class Throw(int line, Expr value) : Stmt(line) { public Expr Value { get; set; } = value; }

/// exit / periodt
public sealed class Exit(int line, Expr? code) : Stmt(line) { public Expr? Code { get; set; } = code; }

/// sleep / touchgrass - the duration is strongly-loose: "2 seconds", "half a minute", 1.5
public sealed class Sleep(int line, Expr seconds) : Stmt(line) { public Expr Seconds { get; set; } = seconds; }

public sealed record SwitchArm(List<Expr> Patterns, List<Stmt> Body); // empty Patterns = default

/// switch / vibeshift. Patterns bend to the subject's type, like everything else.
public sealed class Switch(int line, Expr subject, List<SwitchArm> arms) : Stmt(line)
{
    public Expr Subject { get; set; } = subject;
    public List<SwitchArm> Arms { get; } = arms;
}

/// maincharacter { } - the program's entry point
public sealed class MainBlock(int line, List<Stmt> body) : Stmt(line) { public List<Stmt> Body { get; } = body; }

public sealed class TryCatch(int line, List<Stmt> body, string err, List<Stmt> handler) : Stmt(line)
{
    public List<Stmt> Body { get; } = body;
    public string ErrName { get; } = err;
    public List<Stmt> Handler { get; } = handler;
}

/// `#prompt ...` / `%prompt { ... }` - raw instructions passed verbatim to the generator.
public sealed class PromptStmt(int line, string text) : Stmt(line)
{
    public string Text { get; } = text;
    public bool Global { get; set; } // at the top of the file, before any code
}

/// `use http` / `use http as web`
public sealed class UseStmt(int line, string module, string? alias) : Stmt(line)
{
    public string Module { get; } = module;
    public string? Alias { get; } = alias;
    public string Name => Alias ?? Module;
}

public sealed class Break(int line) : Stmt(line);
public sealed class Continue(int line) : Stmt(line);
