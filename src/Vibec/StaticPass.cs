using System.Globalization;
using System.Text.Json.Nodes;

namespace Vibec;

public sealed record Diagnostic(int Line, string Message, bool IsError, string? File = null)
{
    public override string ToString() => $"{(File != null ? File + " " : "")}line {Line}: {Message}";
}

/// Pass 1. Scopes, type inference, and every place a value has to bend to fit a type.
/// Settles what it can deterministically, consults the knowledge from earlier vibe passes,
/// and turns everything else into a Question.
public sealed class StaticPass(Knowledge knowledge)
{
    public List<Question> Questions { get; } = [];
    public List<Diagnostic> Diagnostics { get; } = [];
    public HashSet<string> Features { get; } = [];
    public List<FnDecl> Generated { get; } = []; // functions the vibe pass wrote
    public Dictionary<string, SortedSet<string>> UsedStd { get; } = []; // std module → functions called
    public List<Fix> Fixes { get; } = []; // FTFY: what the programmer obviously meant

    readonly List<Dictionary<string, VType>> _scopes = [];
    readonly List<HashSet<string>> _consts = [];
    readonly Dictionary<string, FnDecl> _functions = [];
    readonly Dictionary<string, ModuleInfo> _modules = []; // name (or alias) → module, from `use`
    readonly HashSet<string> _asked = [];
    FnDecl? _currentFn;
    int _stmtLine;           // the statement being checked
    int? _outerLoopLine;     // the outermost loop around it, if any
    bool _inMain;
    VType? _expected; // type the surrounding code wants from a call, for generated functions
    int _loopDepth;

    public bool HasErrors => Diagnostics.Any(d => d.IsError);

    /// Last round: nothing more will be asked, so unresolved things get reported instead.
    public bool Final { get; init; }

    /// A module other modules use (not the entry file): declarations only, no statements.
    public bool IsLibrary { get; init; }
    public string? ModuleName { get; init; }

    /// Finds the module behind `use x`: the project's own modules first, then the standard library.
    public Func<string, ModuleInfo?> ResolveModule { get; init; } = StdLib.Get;

    string _modulePrompt = "";

    void Error(int line, string msg) => Diagnostics.Add(new Diagnostic(line, msg, true));
    void Warn(int line, string msg) => Diagnostics.Add(new Diagnostic(line, msg, false));

    void Ask(Question q)
    {
        if (!_asked.Add(q.Key)) return;
        if (!Final) { Questions.Add(q); return; }
        switch (q.Kind)
        {
            case QuestionKind.Name: Error(q.Line, $"'{q.Name}' isn't a thing (yet). declare it first"); break;
            case QuestionKind.Coerce: Warn(q.Line, $"couldn't vibe {q.Value?.ToJsonString()} into a {q.To}; the generator decides"); break;
            default: Warn(q.Line, $"couldn't work out what '{q.Name}' means; the generator will guess from the name"); break;
        }
    }

    public void Run(List<Stmt> program)
    {
        // functions are hoisted, so they can be called before they're declared
        foreach (var fn in program.OfType<FnDecl>())
        {
            if (!_functions.TryAdd(fn.Name, fn)) Error(fn.Line, $"'{fn.Name}' is declared twice. pick a lane");
        }

        var mains = All(program).OfType<MainBlock>().ToList();
        if (mains.Count > 1) Error(mains[1].Line, "only one maincharacter allowed. this isn't an ensemble cast");
        foreach (var m in mains.Where(m => !program.Contains(m))) Error(m.Line, "maincharacter goes at the top level");

        foreach (var u in All(program).OfType<UseStmt>())
        {
            if (!program.Contains(u)) { Error(u.Line, "use goes at the top level"); continue; }
            var module = ResolveModule(u.Module);
            if (module == null) { Error(u.Line, $"can't find module '{u.Module}'. the standard library has: {string.Join(", ", StdLib.Names)}"); continue; }
            if (u.Module == ModuleName) { Error(u.Line, "a module can't use itself"); continue; }
            if (!_modules.TryAdd(u.Name, module)) Error(u.Line, $"'{u.Name}' is used twice");
        }

        _modulePrompt = string.Join("\n", program.OfType<PromptStmt>().Where(p => p.Global).Select(p => p.Text));

        // only the entry file runs things; the modules it uses just declare them
        if (IsLibrary)
        {
            foreach (var s in program.Where(s => s is not (FnDecl or Decl or PromptStmt or UseStmt)))
                Error(s.Line, $"{ModuleName} is a module, so it can only declare things (functions, variables, prompts). statements run in the entry file");
        }

        Push();
        // globals (rentfree) live in everyone's head from the start, wherever they're declared
        foreach (var g in All(program).OfType<Decl>().Where(d => d.Global))
        {
            if (g.DeclType == null) { Error(g.Line, $"global '{g.Name}' needs an explicit type"); continue; }
            Declare(g.Name, g.DeclType, g.Const);
            Features.Add("global");
        }
        CheckBlock(program, newScope: false);
        // generated functions are checked like hand-written ones; they may generate more
        for (int k = 0; k < Generated.Count; k++) CheckStmt(Generated[k]);
        Pop();
    }

    /// Check, telling a call what type its caller wants back.
    Expr CheckExpecting(Expr e, VType? expected)
    {
        _expected = e is Call or Ident or Member ? expected : null;
        var result = Check(e);
        _expected = null;
        return result;
    }

    // ---------- scopes ----------

    void Push() { _scopes.Add([]); _consts.Add([]); }
    void Pop() { _scopes.RemoveAt(_scopes.Count - 1); _consts.RemoveAt(_consts.Count - 1); }

    void Declare(string name, VType type, bool isConst = false)
    {
        _scopes[^1][name] = type;
        if (isConst) _consts[^1].Add(name); else _consts[^1].Remove(name);
    }

    bool IsConst(string name)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].ContainsKey(name)) return _consts[i].Contains(name);
        return false;
    }

    /// Every expression in these statements (and the statements nested in them), sub-expressions included.
    public static IEnumerable<Expr> AllExprs(IEnumerable<Stmt> stmts) =>
        All(stmts).SelectMany(StmtExprs).SelectMany(Walk);

    static IEnumerable<Expr> StmtExprs(Stmt s) => s switch
    {
        Decl d => d.Init is { } i ? [i] : [],
        Assign a => [a.Target, a.Value],
        ExprStmt e => [e.Expr],
        If i => [i.Cond],
        While w => [w.Cond],
        For f => [f.Iter],
        CFor cf => (cf.Cond is { } c ? [c] : Array.Empty<Expr>())
                   .Concat(cf.Init is { } init ? StmtExprs(init) : [])
                   .Concat(cf.Step is { } step ? StmtExprs(step) : []),
        Repeat r => [r.Count],
        Return r => r.Value is { } v ? [v] : [],
        Say s2 => s2.Args,
        VibeCheck v => [v.Cond],
        Throw t => [t.Value],
        Exit e => e.Code is { } code ? [code] : [],
        Sleep sl => [sl.Seconds],
        Switch sw => sw.Arms.SelectMany(a => a.Patterns).Prepend(sw.Subject),
        FnDecl fn => fn.Params.Select(p => p.Default).OfType<Expr>(),
        _ => [],
    };

    static IEnumerable<Expr> Walk(Expr e)
    {
        yield return e;
        IEnumerable<Expr> children = e switch
        {
            ListLit l => l.Items,
            ObjLit o => o.Entries.Select(x => x.Value),
            Interp i => i.Parts.OfType<Expr>(),
            Unary u => [u.Operand],
            Binary b => [b.Left, b.Right],
            Call c => c.Args.Concat(c.Named.Select(n => n.Value)),
            Member m => (m.Args ?? []).Concat(m.Named.Select(n => n.Value)).Prepend(m.Target),
            Index ix => [ix.Target, ix.IndexExpr],
            Lambda lam => [lam.Body],
            IncDec inc => [inc.Target],
            Ask a => a.Prompt is { } p ? [p] : [],
            Cast c => [c.Inner],
            Coerce c => [c.Inner],
            _ => [],
        };
        foreach (var child in children)
            foreach (var x in Walk(child)) yield return x;
    }

    /// Every statement, nested ones included.
    static IEnumerable<Stmt> All(IEnumerable<Stmt> stmts)
    {
        foreach (var s in stmts)
        {
            yield return s;
            IEnumerable<Stmt> inner = s switch
            {
                FnDecl f => f.Body,
                If i => i.Then.Concat(i.Else ?? []),
                While w => w.Body,
                For f => f.Body,
                CFor cf => cf.Body,
                Repeat r => r.Body,
                TryCatch t => t.Body.Concat(t.Handler),
                Switch sw => sw.Arms.SelectMany(a => a.Body),
                MainBlock m => m.Body,
                _ => [],
            };
            foreach (var x in All(inner)) yield return x;
        }
    }

    VType? Lookup(string name)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].TryGetValue(name, out var t)) return t;
        return null;
    }

    IEnumerable<string> VisibleNames() => _scopes.SelectMany(s => s.Keys).Distinct();

    // ---------- statements ----------

    void CheckBlock(List<Stmt> stmts, bool newScope = true)
    {
        if (newScope) Push();
        foreach (var s in stmts) CheckStmt(s);
        if (newScope) Pop();
    }

    void CheckStmt(Stmt stmt)
    {
        _stmtLine = stmt.Line;
        bool outermostLoop = stmt is For or While or CFor or Repeat && _outerLoopLine == null;
        if (outermostLoop) _outerLoopLine = stmt.Line;
        try { CheckStmtCore(stmt); }
        finally { if (outermostLoop) _outerLoopLine = null; }
    }

    void CheckStmtCore(Stmt stmt)
    {
        switch (stmt)
        {
            case Decl d:
                if (d.DeclType == null)
                {
                    if (d.Init == null) { Error(d.Line, $"'var {d.Name}' needs a value to infer from. vibes can't come from nothing"); d.Resolved = VType.Vibe; }
                    else
                    {
                        d.Init = Check(d.Init);
                        if (d.Init.Type == VType.Void) Error(d.Line, "that expression doesn't produce anything to store");
                        d.Resolved = d.Init.Type == VType.Fn ? VType.Vibe : d.Init.Type;
                    }
                }
                else
                {
                    d.Resolved = d.DeclType;
                    if (d.DeclType == VType.Json) Features.Add("json");
                    if (d.Init != null) d.Init = CoerceTo(CheckExpecting(d.Init, d.DeclType), d.DeclType, d.Line);
                }
                if (d.Const)
                {
                    Features.Add("const");
                    if (d.Init == null) Error(d.Line, $"'{d.Name}' is const but has no value. commit to something");
                }
                if (d.Global) break; // already declared up front
                if (_scopes[^1].ContainsKey(d.Name)) Warn(d.Line, $"'{d.Name}' redeclared. new phone, who dis");
                Declare(d.Name, d.Resolved, d.Const);
                break;

            case Assign a:
                a.Target = Check(a.Target);
                if (a.Target is Member { Module: { } owner }) Error(a.Line, $"can't change {owner}'s variables from outside {owner}");
                if (a.Target is Ident tid && IsConst(tid.Name)) Error(a.Line, $"'{tid.Name}' is const. it doesn't change. that's the point");
                var target = a.Target.Type;
                a.Value = CheckExpecting(a.Value, a.Op == "=" ? target : null);
                if (a.Op == "=") a.Value = CoerceTo(a.Value, target, a.Line);
                else if (a.Op == "+=" && target == VType.Str) { /* concatenation stringifies */ }
                else if (a.Op == "+=" && target.IsList)
                    a.Value = a.Value.Type.IsList ? CoerceTo(a.Value, target, a.Line) : CoerceTo(a.Value, target.Elem!, a.Line);
                else a.Value = CoerceTo(a.Value, target.IsNumeric ? target : VType.Float, a.Line);
                break;

            case ExprStmt e:
                e.Expr = Check(e.Expr);
                break;

            case FnDecl fn:
                if (_currentFn != null) { Error(fn.Line, "functions inside functions? keep it flat for now"); break; }
                for (int k = 0; k < fn.Params.Count; k++)
                    if (fn.Params[k].Default is { } def)
                        fn.Params[k] = fn.Params[k] with { Default = CoerceTo(Check(def), fn.Params[k].Type, fn.Line) };
                if (fn.Native && !fn.Generated) { Error(fn.Line, "native functions live in the standard library. write a body, or leave it off and let the vibes write one"); break; }
                if (!fn.HasBody && fn.Body.Count == 0) FillContract(fn);
                _currentFn = fn;
                Push();
                foreach (var p in fn.Params) Declare(p.Name, p.Type);
                CheckBlock(fn.Body);
                Pop();
                _currentFn = null;
                break;

            case If i:
                i.Cond = CoerceTo(CheckExpecting(i.Cond, VType.Bool), VType.Bool, i.Line);
                CheckBlock(i.Then);
                if (i.Else != null) CheckBlock(i.Else);
                break;

            case While w:
                w.Cond = CoerceTo(CheckExpecting(w.Cond, VType.Bool), VType.Bool, w.Line);
                _loopDepth++;
                CheckBlock(w.Body);
                _loopDepth--;
                break;

            case For f:
                f.Iter = Check(f.Iter);
                MeantTheLoopVariable(f);
                var it = f.Iter.Type;
                if (it.IsList) f.VarType = it.Elem!;
                else if (it == VType.Str) f.VarType = VType.Str;
                else if (it == VType.Int) { f.VarType = VType.Int; Features.Add("for-int"); }
                else if (it.IsDynamic) f.VarType = it;
                else Error(f.Line, $"can't loop over a {it}");
                if (f.DeclType != null && f.DeclType != f.VarType)
                {
                    // for (int n in strings): each element bends to the declared type
                    if (!(f.VarType == VType.Int && f.DeclType == VType.Float)) { Features.Add("typed-for"); Features.Add("runtime-coercion"); }
                    f.VarType = f.DeclType;
                }
                _loopDepth++;
                Push();
                Declare(f.Var, f.VarType);
                CheckBlock(f.Body);
                Pop();
                _loopDepth--;

                // for item in res { print res.ticker }: item is never used, res's fields are. you meant item.
                if (f.Iter is Ident iterId && (f.Iter.Type.IsList || f.Iter.Type.IsDynamic)
                    && !AllExprs(f.Body).OfType<Ident>().Any(x => x.Name == f.Var))
                {
                    foreach (var m in AllExprs(f.Body).OfType<Member>().Where(m => m.Target is Ident t && t.Name == iterId.Name))
                        Fixes.Add(new Fix(m.Line, $"{iterId.Name}.{m.Name}", $"{f.Var}.{m.Name}", $"you're looping over {iterId.Name} as {f.Var}"));
                }
                break;

            case CFor cf:
                Features.Add("c-for");
                Push();
                if (cf.Init != null) CheckStmt(cf.Init);
                if (cf.Cond != null) cf.Cond = CoerceTo(CheckExpecting(cf.Cond, VType.Bool), VType.Bool, cf.Line);
                _loopDepth++;
                CheckBlock(cf.Body);
                _loopDepth--;
                if (cf.Step != null) CheckStmt(cf.Step);
                Pop();
                break;

            case Repeat r:
                Features.Add("repeat");
                r.Count = CoerceTo(CheckExpecting(r.Count, VType.Int), VType.Int, r.Line);
                _loopDepth++;
                CheckBlock(r.Body);
                _loopDepth--;
                break;

            case Return ret:
                if (_currentFn == null && _inMain)
                {
                    // a bare return just ends the main block
                    if (ret.Value != null) Error(ret.Line, "maincharacter doesn't return a value. use exit <code>");
                    break;
                }
                if (_currentFn == null) { Error(ret.Line, "return to where? you're not in a function"); break; }
                if (ret.Value == null)
                {
                    if (_currentFn.Ret != VType.Void) Error(ret.Line, $"'{_currentFn.Name}' promised a {_currentFn.Ret} and returned nothing");
                }
                else if (_currentFn.Ret == VType.Void) Error(ret.Line, $"'{_currentFn.Name}' is void. it can't return a value");
                else ret.Value = CoerceTo(CheckExpecting(ret.Value, _currentFn.Ret), _currentFn.Ret, ret.Line);
                break;

            case Say s:
                if (s.Mode == SayMode.Spill) s.Label = Printer.Print(s.Args[0]);
                for (int k = 0; k < s.Args.Count; k++) s.Args[k] = Check(s.Args[k]);
                foreach (var n in s.Named)
                {
                    if (n.Name is not ("end" or "sep")) Error(s.Line, $"print takes end: and sep:, not '{n.Name}:'");
                    n.Value = CoerceTo(Check(n.Value), VType.Str, s.Line);
                    Features.Add("print-options");
                }
                if (s.Mode == SayMode.Low) Features.Add("lowkey");
                if (s.Mode == SayMode.High) Features.Add("highkey");
                if (s.Mode == SayMode.Spill) Features.Add("spill");
                break;

            case Throw th:
                Features.Add("throw");
                th.Value = Check(th.Value);
                break;

            case Exit ex:
                Features.Add("exit");
                if (ex.Code != null) ex.Code = CoerceTo(CheckExpecting(ex.Code, VType.Int), VType.Int, ex.Line);
                break;

            case Sleep sl:
                Features.Add("sleep");
                sl.Seconds = CoerceTo(CheckExpecting(sl.Seconds, VType.Float), VType.Float, sl.Line, "a duration in seconds");
                break;

            case Switch sw:
                Features.Add("switch");
                sw.Subject = Check(sw.Subject);
                foreach (var arm in sw.Arms)
                {
                    for (int k = 0; k < arm.Patterns.Count; k++)
                        arm.Patterns[k] = CoerceTo(CheckExpecting(arm.Patterns[k], sw.Subject.Type), sw.Subject.Type, sw.Line);
                    CheckBlock(arm.Body);
                }
                break;

            case MainBlock mb:
                Features.Add("main");
                _inMain = true;
                CheckBlock(mb.Body);
                _inMain = false;
                break;

            case VibeCheck v:
                Features.Add(v.Assume ? "assume" : "vibecheck");
                v.Cond = CoerceTo(CheckExpecting(v.Cond, VType.Bool), VType.Bool, v.Line);
                break;

            case TryCatch t:
                Features.Add("try");
                CheckBlock(t.Body);
                Push();
                Declare(t.ErrName, VType.Str);
                CheckBlock(t.Handler);
                Pop();
                break;

            case UseStmt:
                break;

            case PromptStmt p:
                Features.Add(p.Global ? "global-prompt" : "prompt");
                break;

            case Break or Continue:
                if (_loopDepth == 0) Error(stmt.Line, "break/continue outside a loop. there's nothing to escape");
                break;
        }
    }

    // ---------- expressions ----------

    Expr Check(Expr e)
    {
        switch (e)
        {
            case Lit l:
                if (l.Value is Maybe) Features.Add("maybe");
                return l;

            case ObjLit obj:
                foreach (var entry in obj.Entries) entry.Value = Check(entry.Value);
                obj.Type = VType.Json;
                Features.Add("json");
                Features.Add("objlit");
                return obj;

            case ListLit list:
                for (int k = 0; k < list.Items.Count; k++) list.Items[k] = Check(list.Items[k]);
                list.Type = VType.ListOf(Unify(list.Items.Select(x => x.Type)));
                return list;

            case Interp interp:
                for (int k = 0; k < interp.Parts.Count; k++)
                    if (interp.Parts[k] is Expr pe) interp.Parts[k] = Check(pe);
                interp.Type = VType.Str;
                return interp;

            case Ident id:
                return CheckIdent(id);

            case Unary u:
                u.Operand = Check(u.Operand);
                if (u.Op == "not") { u.Operand = CoerceTo(u.Operand, VType.Bool, u.Line); u.Type = VType.Bool; }
                else
                {
                    if (!u.Operand.Type.IsNumeric) u.Operand = CoerceTo(u.Operand, NumericGuess(u.Operand), u.Line);
                    u.Type = u.Operand.Type;
                }
                return u;

            case Binary b:
                return CheckBinary(b);

            case Call c:
                return CheckCall(c);

            case Member m:
                return CheckMember(m);

            case Index ix:
                ix.Target = Check(ix.Target);
                ix.IndexExpr = Check(ix.IndexExpr);
                var tt = ix.Target.Type;
                if (tt.IsList) ix.Type = tt.Elem!;
                else if (tt == VType.Str) ix.Type = VType.Str;
                else if (tt.IsDynamic) ix.Type = tt;
                else Error(ix.Line, $"can't index into a {tt}");
                if (!tt.IsDynamic) ix.IndexExpr = CoerceTo(ix.IndexExpr, VType.Int, ix.Line);
                return ix;

            case Lambda lam:
                return CheckLambda(lam, lam.Params.Select(_ => VType.Vibe).ToList());

            case IncDec inc:
                return CheckIncDec(inc);

            case Ask a:
                Features.Add("ask");
                if (a.Prompt != null) a.Prompt = Check(a.Prompt);
                a.Type = VType.Str;
                return a;

            case VibeExpr v:
                Features.Add("vibe-expr");
                return v;

            case Cast cast:
                return CoerceTo(Check(cast.Inner), cast.To, cast.Line);
        }
        return e;
    }

    Expr CheckIdent(Ident id)
    {
        var expected = _expected;
        _expected = null;
        var t = Lookup(id.Name);
        if (t != null) { id.Type = t; return id; }
        if (_functions.TryGetValue(id.Name, out var fn))
        {
            // where a function is expected, a function's name is the function itself
            if (expected == VType.Fn) { Features.Add("fnref"); return new FuncRef(id.Line, fn.Name) { Type = VType.Fn }; }
            // a bare function name is a call, if it needs no arguments
            if (fn.Params.Count == 0) { _expected = expected; return CheckCall(new Call(id.Line, id.Name, [])); }
            Error(id.Line, $"'{id.Name}' needs {fn.Params.Count} argument(s)");
            return id;
        }

        // undeclared: either a typo of something in scope, or a word with an obvious meaning
        // where it's used (`while infinity`, `float x = pi`)
        var candidates = VisibleNames().ToList();

        // same name, different case (Total vs total): you know what I mean
        var sameName = candidates.Where(n => string.Equals(n, id.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (sameName.Count == 1)
        {
            Fixes.Add(new Fix(id.Line, id.Name, sameName[0], $"'{id.Name}' isn't declared; you meant '{sameName[0]}'", All: true));
            id.RenamedFrom = id.Name;
            id.Name = sameName[0];
            id.Type = Lookup(sameName[0])!;
            return id;
        }
        if (FunctionByCase(id.Name) is { Params.Count: 0 } caseFn)
        {
            Fixes.Add(new Fix(id.Line, id.Name, caseFn.Name, $"'{id.Name}' isn't defined; you meant '{caseFn.Name}'", All: true));
            _expected = expected;
            return CheckCall(new Call(id.Line, caseFn.Name, []));
        }
        var key = Knowledge.NameKey(id.Name, candidates, expected);
        if (knowledge.TryGet(key, out var ans))
        {
            if (ans.Ok && ans.Rename != null && Lookup(ans.Rename) is { } rt)
            {
                id.RenamedFrom = id.Name;
                id.Name = ans.Rename;
                id.Type = rt;
                Fixes.Add(new Fix(id.Line, id.RenamedFrom, id.Name, $"'{id.RenamedFrom}' isn't declared; you meant '{id.Name}'"));
                Warn(id.Line, $"'{id.RenamedFrom}' isn't declared. vibed it to '{id.Name}'");
                return id;
            }
            var type = expected ?? VType.Parse(ans.Returns) ?? VType.Vibe;
            if (ans.Ok && TryFromJson(ans.Value, type, out var constant))
            {
                Warn(id.Line, $"'{id.Name}' isn't declared. vibed it to {ans.Value!.ToJsonString()}");
                return new Coerce(id.Line, id, type) { Status = CoerceStatus.Folded, Result = constant, Source = "vibe", Why = ans.Why };
            }
            Error(id.Line, $"'{id.Name}' isn't a thing (yet). declare it first");
            return id;
        }

        Ask(new Question { Kind = QuestionKind.Name, Key = key, Line = id.Line, Name = id.Name, Candidates = candidates, Expected = expected });
        id.Type = expected ?? VType.Vibe;
        return id;
    }

    static readonly HashSet<string> IndexNames = ["i", "j", "k", "n", "x", "y", "idx", "index", "num", "number"];

    /// A counting loop whose variable is never used, and whose body uses exactly one undeclared
    /// index-like name: that name is the loop variable. Renamed before the body is checked.
    void MeantTheLoopVariable(For f)
    {
        bool counting = f.Iter is Call { Name: "range" } || f.Iter.Type == VType.Int || f.Iter.Type == VType.ListOf(VType.Int);
        if (!counting) return;
        var idents = AllExprs(f.Body).OfType<Ident>().ToList();
        if (idents.Any(x => x.Name == f.Var)) return;

        var declaredInside = All(f.Body).OfType<Decl>().Select(d => d.Name)
            .Concat(All(f.Body).OfType<For>().Select(x => x.Var))
            .Concat(All(f.Body).OfType<TryCatch>().Select(t => t.ErrName))
            .Concat(AllExprs(f.Body).OfType<Lambda>().SelectMany(l => l.Params))
            .Concat(AllExprs(f.Body).OfType<IncDec>().Select(x => x.Target).OfType<Ident>().Select(x => x.Name))
            .ToHashSet();
        var unknown = idents.Select(x => x.Name).Distinct()
            .Where(n => Lookup(n) == null && !_functions.ContainsKey(n) && !declaredInside.Contains(n)
                        && !_modules.ContainsKey(n) && StdLib.Get(n) == null)
            .ToList();
        if (unknown is not [var meant] || !IndexNames.Contains(meant)) return;

        foreach (var x in idents.Where(x => x.Name == meant))
        {
            Fixes.Add(new Fix(x.Line, meant, f.Var, $"the loop counts with {f.Var}, which is never used; you meant {f.Var}", All: true));
            x.RenamedFrom = meant;
            x.Name = f.Var;
        }
    }

    FnDecl? FunctionByCase(string name)
    {
        var matches = _functions.Values.Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase) && f.Name != name).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    static readonly string[] ListMembers = ["length", "count", "push", "append", "add", "pop", "contains", "includes", "indexOf", "join", "reverse", "sort", "map", "select", "filter", "where", "any", "all", "some", "every", "find", "forEach", "each"];
    static readonly string[] StringMembers = ["length", "upper", "lower", "trim", "strip", "toUpperCase", "toLowerCase", "split", "contains", "includes", "startsWith", "endsWith", "replace", "indexOf"];

    Expr CheckIncDec(IncDec inc)
    {
        Features.Add("incdec");
        inc.Type = VType.Int;
        if (inc.Target is Ident counter && Lookup(counter.Name) == null && !_functions.ContainsKey(counter.Name))
        {
            // i++ on a name nobody declared: it's a counter. declare it before the loop it lives in
            counter.Type = VType.Int;
            Fixes.Add(new Fix(_outerLoopLine ?? _stmtLine, "", $"int {counter.Name} = 0", $"'{counter.Name}' is a counter nobody declared", Insert: true));
            Error(inc.Line, $"'{counter.Name}' isn't declared. it's obviously a counter: FTFY adds 'int {counter.Name} = 0' before the loop");
            return inc;
        }
        inc.Target = Check(inc.Target);
        if (inc.Target is not (Ident or Index or Member { Args: null })) Error(inc.Line, $"{inc.Op} needs a variable");
        else if (inc.Target is Ident id && IsConst(id.Name)) Error(inc.Line, $"'{id.Name}' is const. it doesn't change. that's the point");
        else if (!inc.Target.Type.IsNumeric && !inc.Target.Type.IsDynamic) Error(inc.Line, $"can't {inc.Op} a {inc.Target.Type}");
        inc.Type = inc.Target.Type;
        return inc;
    }

    Expr CheckBinary(Binary b)
    {
        b.Left = Check(b.Left);
        b.Right = Check(b.Right);
        VType l = b.Left.Type, r = b.Right.Type;

        switch (b.Op)
        {
            case "and" or "or":
                b.Left = CoerceTo(b.Left, VType.Bool, b.Line);
                b.Right = CoerceTo(b.Right, VType.Bool, b.Line);
                b.Type = VType.Bool;
                break;

            case "+":
                if (l == VType.Str || r == VType.Str) b.Type = VType.Str; // concatenation stringifies the other side
                else if (l.IsList && r.IsList) b.Type = l;
                else if (l == VType.Vibe || r == VType.Vibe) b.Type = VType.Vibe;
                else Arithmetic();
                break;

            case "-" or "*" or "/" or "%":
                Arithmetic();
                break;

            case "<" or ">" or "<=" or ">=":
                if (l.IsNumeric && !r.IsNumeric) b.Right = CoerceTo(b.Right, l, b.Line);
                else if (r.IsNumeric && !l.IsNumeric) b.Left = CoerceTo(b.Left, r, b.Line);
                b.Type = VType.Bool;
                break;

            case "==" or "!=":
                // loose equality: the right side bends to the left
                if (l != r && l != VType.Vibe && r != VType.Vibe && !(l.IsNumeric && r.IsNumeric))
                    b.Right = CoerceTo(b.Right, l, b.Line);
                b.Type = VType.Bool;
                break;

            case "kinda":
                Features.Add("kinda");
                b.Type = VType.Bool;
                break;
        }
        return b;

        void Arithmetic()
        {
            if (l == VType.Vibe && r == VType.Vibe) { b.Type = VType.Vibe; return; }
            var num = l == VType.Float || r == VType.Float ? VType.Float : VType.Int;
            b.Left = CoerceTo(b.Left, num, b.Line);
            b.Right = CoerceTo(b.Right, num, b.Line);
            b.Type = num;
        }
    }

    static readonly Dictionary<string, Func<List<VType>, VType?>> Builtins = new()
    {
        ["len"] = a => a.Count == 1 ? VType.Int : null,
        ["range"] = a => a.Count is 1 or 2 ? VType.ListOf(VType.Int) : null,
    };

    Expr CheckCall(Call c)
    {
        var expected = _expected;
        _expected = null;
        if (c.Name == "print") { Error(c.Line, "print(...) is a statement, not a value"); return c; }
        if (Lookup(c.Name) != null) { Error(c.Line, $"'{c.Name}' is a variable, not a function"); return c; }

        if (_functions.TryGetValue(c.Name, out var fn))
        {
            Bind(fn, c.Args, c.Named, c.Line, fn.Name);
            c.Type = fn.Ret;
            return c;
        }

        if (!Builtins.ContainsKey(c.Name) && FunctionByCase(c.Name) is { } byCase)
        {
            Fixes.Add(new Fix(c.Line, c.Name, byCase.Name, $"'{c.Name}' isn't defined; you meant '{byCase.Name}'", All: true));
            _expected = expected;
            return CheckCall(new Call(c.Line, byCase.Name, c.Args) { Named = c.Named });
        }

        for (int k = 0; k < c.Args.Count; k++) c.Args[k] = Check(c.Args[k]);
        foreach (var n in c.Named) n.Value = Check(n.Value);
        var argTypes = c.Args.Select(a => a.Type).ToList();

        if (Builtins.TryGetValue(c.Name, out var builtin))
        {
            c.Type = builtin(argTypes) ?? VType.Vibe;
            if (c.Name == "range") for (int k = 0; k < c.Args.Count; k++) c.Args[k] = CoerceTo(c.Args[k], VType.Int, c.Line);
            return c;
        }

        // not declared anywhere: the vibe pass writes it
        var key = Knowledge.FunctionKey(c.Name, argTypes, expected);
        if (knowledge.TryGet(key, out var ans))
        {
            if (!ans.Ok) { Error(c.Line, $"'{c.Name}' isn't a function anyone's heard of"); return c; }
            if (ans.Source != null && Adopt(ans.Source, c.Name, c.Line) is { } gen)
            {
                for (int k = 0; k < Math.Min(c.Args.Count, gen.Params.Count); k++)
                    c.Args[k] = CoerceTo(c.Args[k], gen.Params[k].Type, c.Line);
                c.Type = gen.Ret;
                return c;
            }
            c.Type = VType.Parse(ans.Returns) ?? VType.Vibe;
            c.Meaning = ans.Meaning;
            Features.Add("vibed-call");
            return c;
        }
        Ask(new Question { Kind = QuestionKind.Function, Key = key, Line = c.Line, Name = c.Name, ArgTypes = argTypes, Expected = expected });
        c.Type = expected ?? VType.Vibe;
        return c;
    }

    /// Parses a function the model wrote and registers it as if the programmer had.
    FnDecl? Adopt(string source, string name, int line)
    {
        try
        {
            var fn = Parser.ParseSource(source).OfType<FnDecl>().FirstOrDefault(f => f.Name == name);
            if (fn == null) { Warn(line, $"the vibe pass wrote something for '{name}', but not a function by that name"); return null; }
            fn.Generated = true;
            _functions[name] = fn;
            Generated.Add(fn);
            Features.Add("generated-fn");
            Warn(line, $"'{name}' isn't defined. vibed one up: {fn.Ret} {name}({string.Join(", ", fn.Params.Select(p => $"{p.Type} {p.Name}"))})");
            return fn;
        }
        catch (VibeError ex)
        {
            Warn(line, $"the vibe pass wrote a broken '{name}' ({ex.Message}); describing it instead");
            return null;
        }
    }

    static readonly HashSet<string> ElementLambdas = ["map", "filter", "where", "select", "any", "all", "find", "forEach", "each", "sortBy", "orderBy", "count", "some", "every"];

    /// Matches arguments to a function's parameters: positional first, then named, then defaults.
    void Bind(FnDecl fn, List<Expr> args, List<NamedArg> named, int line, string display)
    {
        for (int k = 0; k < args.Count; k++)
            args[k] = k < fn.Params.Count ? CoerceTo(CheckExpecting(args[k], fn.Params[k].Type), fn.Params[k].Type, line) : Check(args[k]);
        if (args.Count > fn.Params.Count) Error(line, $"'{display}' takes {fn.Params.Count} argument(s), got {args.Count}");

        var given = fn.Params.Take(args.Count).Select(p => p.Name).ToHashSet();
        foreach (var n in named)
        {
            var p = fn.Params.FirstOrDefault(p => p.Name == n.Name);
            if (p == null)
            {
                Error(line, $"'{display}' has no parameter '{n.Name}'. it takes: {string.Join(", ", fn.Params.Select(x => x.Name))}");
                n.Value = Check(n.Value);
                continue;
            }
            if (!given.Add(p.Name)) Error(line, $"'{n.Name}' is already given");
            n.Value = CoerceTo(CheckExpecting(n.Value, p.Type), p.Type, line);
        }
        foreach (var p in fn.Params.Where(p => p.Default == null && !given.Contains(p.Name)))
            Error(line, $"'{display}' needs '{p.Name}'");
    }

    /// A function with a signature and no body: the vibe pass writes the body, in Vibe.
    void FillContract(FnDecl fn)
    {
        var context = $"implement this exact signature, keeping the parameter names: {Printer.Signature(fn)}" +
                      (ModuleName != null ? $". it belongs to module '{ModuleName}'" : "") +
                      (_modulePrompt.Length > 0 ? $", which is described as: {_modulePrompt}" : "") +
                      UsableModules();
        // the context is part of the key: change what the module is for, or what it uses, and it's re-asked
        var key = Knowledge.FunctionKey(fn.Name, fn.Params.Select(p => p.Type), fn.Ret) + " contract " + Knowledge.Hash(context);
        if (knowledge.TryGet(key, out var ans) && ans.Ok && ans.Source != null)
        {
            try
            {
                var gen = Parser.ParseSource(ans.Source).OfType<FnDecl>().FirstOrDefault(f => f.Name == fn.Name);
                if (gen != null && gen.Ret == fn.Ret && gen.Params.Select(p => (p.Type, p.Name)).SequenceEqual(fn.Params.Select(p => (p.Type, p.Name))))
                {
                    // same signature, same parameter names: the body slots straight in
                    fn.Body.AddRange(gen.Body);
                    fn.Generated = true;
                    Features.Add("generated-fn");
                    Warn(fn.Line, $"'{fn.Name}' has no body. vibed one up");
                    return;
                }
                Warn(fn.Line, $"the vibe pass wrote '{fn.Name}' with a different signature; the generator will implement it from the contract");
            }
            catch (VibeError ex) { Warn(fn.Line, $"the vibe pass wrote a broken '{fn.Name}' ({ex.Message}); the generator will implement it from the contract"); }
            return;
        }
        if (ans != null) return;
        Ask(new Question
        {
            Kind = QuestionKind.Function, Key = key, Line = fn.Line, Name = fn.Name,
            ArgTypes = fn.Params.Select(p => p.Type).ToList(), Expected = fn.Ret,
            Context = context,
        });
    }

    /// What a generated body may call: the public surface of every module this one uses.
    string UsableModules()
    {
        if (_modules.Count == 0) return "";
        var parts = _modules.Select(kv =>
            $"{kv.Key}: " + string.Join("; ", kv.Value.Functions.Values.Where(f => f.Public).Select(Printer.Signature)));
        return ". call these modules (as module.function) rather than describing what they do: " + string.Join(" | ", parts);
    }

    Expr CheckMember(Member m)
    {
        var expected = _expected;
        _expected = null;

        // module calls: `use http` gets the standard library's rules; no `use` gets vibes
        if (m.Target is Ident mid && Lookup(mid.Name) == null && !_functions.ContainsKey(mid.Name))
        {
            if (_modules.TryGetValue(mid.Name, out var module)) return CheckModuleMember(m, mid.Name, module);
            if (StdLib.Get(mid.Name) != null) return CheckVibedModuleMember(m, mid.Name, expected);
        }

        m.Target = Check(m.Target);
        var recv = m.Target.Type;

        if (m.Args != null)
        {
            for (int k = 0; k < m.Args.Count; k++)
            {
                if (m.Args[k] is Lambda lam)
                {
                    var elem = recv.IsList ? recv.Elem! : recv == VType.Str ? VType.Str : VType.Vibe;
                    m.Args[k] = CheckLambda(lam, lam.Params.Select(_ => elem).ToList());
                }
                else m.Args[k] = Check(m.Args[k]);
            }
        }

        if (recv.IsDynamic) { m.Type = recv; return m; } // dynamic: anything goes at runtime; json.field is json

        if (KnownMember(m, recv) is { } known) { m.Type = known; return m; }

        // xs.lenght, name.uper(): one or two keystrokes from a member everyone agrees on
        var members = recv.IsList ? ListMembers : recv == VType.Str ? StringMembers : [];
        if (Ftfy.Closest(m.Name, members) is { } meant)
        {
            var corrected = new Member(m.Line, m.Target, meant, m.Args) { Named = m.Named };
            if (KnownMember(corrected, recv) is { } type)
            {
                Fixes.Add(new Fix(m.Line, "." + m.Name, "." + meant, $"a {recv} has no '{m.Name}'; you meant '{meant}'", All: true));
                corrected.Type = type;
                return corrected;
            }
        }

        var argTypes = m.Args?.Select(a => a is Lambda l ? LambdaSig(l) : a.Type).ToList();
        var key = Knowledge.MemberKey(recv, m.Name, argTypes);
        if (knowledge.TryGet(key, out var ans))
        {
            if (!ans.Ok) { Error(m.Line, $"a {recv} has no '{m.Name}', and the vibes couldn't invent one"); return m; }
            m.Type = VType.Parse(ans.Returns) ?? VType.Vibe;
            m.Meaning = ans.Meaning;
            Features.Add("vibed-call");
            return m;
        }
        Ask(new Question
        {
            Kind = QuestionKind.Member, Key = key, Line = m.Line, Name = m.Name,
            Receiver = recv, ArgTypes = argTypes, IsProperty = m.Args == null,
        });
        return m;
    }

    /// `storage.save x` or `http.get url`: a member of a module this one uses, checked against
    /// that module's public functions and variables.
    Expr CheckModuleMember(Member m, string alias, ModuleInfo module)
    {
        m.Module = module.Name;
        var display = $"{alias}.{m.Name}";

        if (module.Variables.TryGetValue(m.Name, out var variable) && m.Args == null && m.Named.Count == 0)
        {
            if (!variable.Public) Error(m.Line, $"'{m.Name}' is private to {module.Name}. mark it public (or flex) to share it");
            m.Type = variable.Resolved;
            return m;
        }

        if (!module.Functions.TryGetValue(m.Name, out var fn))
        {
            Error(m.Line, $"{module.Name} has no '{m.Name}'. it has: {string.Join(", ", module.PublicNames)}");
            foreach (var a in m.Args ?? []) Check(a);
            return m;
        }
        if (!fn.Public) Error(m.Line, $"'{m.Name}' is private to {module.Name}. mark it public (or flex) to share it");
        Bind(fn, m.Args ?? [], m.Named, m.Line, display);
        m.Type = fn.Ret;

        if (module.IsStd)
        {
            Features.Add("std");
            if (!UsedStd.TryGetValue(module.Name, out var used)) UsedStd[module.Name] = used = [];
            used.Add(fn.Name);
        }
        return m;
    }

    /// `http.get(...)` without `use http`: the vibe pass decides what it probably means.
    Expr CheckVibedModuleMember(Member m, string name, VType? expected)
    {
        m.Module = name;
        var args = m.Args ?? [];
        for (int k = 0; k < args.Count; k++) args[k] = Check(args[k]);
        foreach (var n in m.Named) n.Value = Check(n.Value);

        var key = Knowledge.ModuleMemberKey(name, m.Name, args.Select(a => a.Type), m.Named.Select(n => n.Name), expected);
        if (knowledge.TryGet(key, out var ans))
        {
            if (!ans.Ok) { Error(m.Line, $"couldn't work out what {name}.{m.Name} means. try `use {name}`"); return m; }
            m.Type = VType.Parse(ans.Returns) ?? expected ?? VType.Vibe;
            m.Meaning = ans.Meaning;
            Features.Add("vibed-call");
            Warn(m.Line, $"'{name}' isn't imported, so {name}.{m.Name} was vibed: {ans.Meaning}. `use {name}` for the standard library's rules");
            return m;
        }
        Ask(new Question
        {
            Kind = QuestionKind.Member, Key = key, Line = m.Line, Name = m.Name, Module = name,
            ArgTypes = args.Select(a => a.Type).ToList(), Named = m.Named.Select(n => n.Name).ToList(), IsProperty = m.Args == null, Expected = expected,
        });
        m.Type = expected ?? VType.Vibe;
        return m;
    }

    /// The members everyone agrees on. Anything else goes to the vibe pass.
    static VType? KnownMember(Member m, VType recv)
    {
        var name = m.Name;
        int argc = m.Args?.Count ?? -1; // -1 = property
        var lam = m.Args?.FirstOrDefault() as Lambda;

        if (recv.IsList)
        {
            var elem = recv.Elem!;
            return (name, argc) switch
            {
                ("length" or "count" or "Count" or "Length", -1) => VType.Int,
                ("push" or "append" or "add" or "Add", 1) => VType.Void,
                ("pop", 0) => elem,
                ("contains" or "includes" or "Contains", 1) => VType.Bool,
                ("indexOf" or "IndexOf", 1) => VType.Int,
                ("join", 0 or 1) => VType.Str,
                ("reverse" or "sort", 0) => recv,
                ("map" or "select" or "Select", 1) when lam != null => VType.ListOf(lam.Body.Type == VType.Void ? VType.Vibe : lam.Body.Type),
                ("filter" or "where" or "Where", 1) when lam != null => recv,
                ("any" or "all" or "some" or "every", 1) when lam != null => VType.Bool,
                ("find", 1) when lam != null => elem,
                ("forEach" or "each", 1) when lam != null => VType.Void,
                _ => null,
            };
        }
        if (recv == VType.Str)
        {
            return (name, argc) switch
            {
                ("length" or "Length", -1) => VType.Int,
                ("upper" or "lower" or "trim" or "strip" or "toUpperCase" or "toLowerCase" or "ToUpper" or "ToLower" or "Trim", 0) => VType.Str,
                ("split" or "Split", 0 or 1) => VType.ListOf(VType.Str),
                ("contains" or "includes" or "startsWith" or "endsWith" or "Contains" or "StartsWith" or "EndsWith", 1) => VType.Bool,
                ("replace" or "Replace", 2) => VType.Str,
                ("indexOf" or "IndexOf", 1) => VType.Int,
                _ => null,
            };
        }
        return null;
    }

    Lambda CheckLambda(Lambda lam, List<VType> paramTypes)
    {
        lam.ParamTypes = paramTypes;
        Push();
        for (int k = 0; k < lam.Params.Count; k++) Declare(lam.Params[k], paramTypes[k]);
        lam.Body = Check(lam.Body);
        Pop();
        lam.Type = VType.Fn;
        return lam;
    }

    static VType LambdaSig(Lambda l) => VType.Fn;

    static VType Unify(IEnumerable<VType> types)
    {
        var distinct = types.Distinct().ToList();
        if (distinct.Count == 0) return VType.Vibe;
        if (distinct.Count == 1) return distinct[0];
        if (distinct.All(t => t.IsNumeric)) return VType.Float;
        return VType.Vibe;
    }

    static VType NumericGuess(Expr e) =>
        e is Lit { Value: string s } && s.Contains('.') ? VType.Float : VType.Int;

    // ---------- coercion ----------

    /// Bends `e` toward `to`. Literals resolve now (by rule or by vibe); everything else at runtime.
    Expr CoerceTo(Expr e, VType to, int line, string? hint = null)
    {
        var from = e.Type;
        if (to == VType.Vibe || from == to) return e;
        if (from == VType.Void) { Error(line, "that expression doesn't produce a value"); return e; }
        if (to == VType.Fn && from != VType.Fn) { Error(line, $"expected a function here (a function's name or a lambda), not a {from}"); return e; }
        if (from == VType.Fn) { Error(line, $"a lambda can't become a {to}"); return e; }
        if (to == VType.Void) return e;
        if (e is Lit { Value: null }) return e;                     // null fits anywhere
        if (from == VType.Int && to == VType.Float) return e;       // widening is free
        if (e is VibeExpr) { e.Type = to; return e; }               // the generator produces a `to` directly
        if (to.IsList && from.IsList && to.Elem!.IsDynamic) return e; // list<vibe> takes any list
        if (to == VType.Json || from == VType.Json)
        {
            // json parses from text and reads into anything, at runtime
            Features.Add("json");
            Features.Add("runtime-coercion");
            return new Coerce(line, e, to) { Status = CoerceStatus.Runtime };
        }
        if (e is ListLit list && to.IsList)
        {
            for (int k = 0; k < list.Items.Count; k++) list.Items[k] = CoerceTo(list.Items[k], to.Elem!, line, hint);
            list.Type = to;
            return list;
        }

        if (!TryLiteral(e, out var value))
        {
            Features.Add("runtime-coercion");
            return new Coerce(line, e, to) { Status = CoerceStatus.Runtime };
        }

        if (hint == null && TryRule(value, from, to, out var ruled))
            return new Coerce(line, e, to) { Status = CoerceStatus.Folded, Result = ruled, Source = "rule" };

        var json = ToJson(value);
        var key = Knowledge.CoerceKey(json, to, hint);
        if (knowledge.TryGet(key, out var ans))
        {
            if (ans.Ok && TryFromJson(ans.Value, to, out var vibed))
                return new Coerce(line, e, to) { Status = CoerceStatus.Folded, Result = vibed, Source = "vibe", Why = ans.Why };
            Features.Add("deferred");
            Warn(line, $"couldn't vibe {json?.ToJsonString()} into a {to}" + (ans.Why != null ? $" ({ans.Why})" : "") + "; the generator decides");
            return new Coerce(line, e, to) { Status = CoerceStatus.Deferred, Why = ans.Why };
        }

        Ask(new Question { Kind = QuestionKind.Coerce, Key = key, Line = line, Value = json, To = to, Context = hint });
        Features.Add("deferred");
        return new Coerce(line, e, to) { Status = CoerceStatus.Deferred };
    }

    static bool TryLiteral(Expr e, out object? value)
    {
        value = null;
        switch (e)
        {
            case Lit { Value: Maybe }: return false;
            case Lit l: value = l.Value; return true;
            case Unary { Op: "-", Operand: Lit { Value: long n } }: value = -n; return true;
            case Unary { Op: "-", Operand: Lit { Value: double d } }: value = -d; return true;
            case ListLit list:
                var items = new List<object?>();
                foreach (var item in list.Items)
                {
                    if (!TryLiteral(item, out var v)) return false;
                    items.Add(v);
                }
                value = items;
                return true;
        }
        return false;
    }

    static readonly string[] Truthy = ["true", "yes", "yeah", "y"];
    static readonly string[] Falsy = ["false", "no", "nah", "n"];

    /// The boring, deterministic coercions. Anything with meaning in it goes to the model.
    static bool TryRule(object? v, VType from, VType to, out object? result)
    {
        result = null;
        var inv = CultureInfo.InvariantCulture;
        if (to.IsList && from == to.Elem && v is not List<object?>) { result = new List<object?> { v }; return true; }

        switch (v)
        {
            case string s:
                var t = s.Trim();
                if (to == VType.Int && long.TryParse(t, NumberStyles.Integer, inv, out var li)) { result = li; return true; }
                if (to == VType.Float && double.TryParse(t, NumberStyles.Float, inv, out var dv)) { result = dv; return true; }
                if (to == VType.Bool && Truthy.Contains(t.ToLowerInvariant())) { result = true; return true; }
                if (to == VType.Bool && Falsy.Contains(t.ToLowerInvariant())) { result = false; return true; }
                return false;

            case long n:
                if (to == VType.Str) { result = n.ToString(inv); return true; }
                if (to == VType.Bool) { result = n != 0; return true; }
                if (to.IsList && TryRule(v, from, to.Elem!, out var el)) { result = new List<object?> { el }; return true; }
                return false;

            case double d:
                if (to == VType.Str) { result = d.ToString("R", inv); return true; }
                if (to == VType.Int) { result = (long)Math.Round(d, MidpointRounding.AwayFromZero); return true; }
                if (to == VType.Bool) { result = d != 0; return true; }
                return false;

            case bool b:
                if (to == VType.Str) { result = b ? "true" : "false"; return true; }
                if (to == VType.Int) { result = b ? 1L : 0L; return true; }
                if (to == VType.Float) { result = b ? 1.0 : 0.0; return true; }
                return false;

            case List<object?> list:
                if (to == VType.Int) { result = (long)list.Count; return true; }
                return false;
        }
        return false;
    }

    public static JsonNode? ToJson(object? v) => v switch
    {
        null => null,
        long n => JsonValue.Create(n),
        double d => JsonValue.Create(d),
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        List<object?> l => new JsonArray(l.Select(ToJson).ToArray()),
        _ => JsonValue.Create(v.ToString()),
    };

    /// Validates a model's answer against the type it was supposed to produce.
    public static bool TryFromJson(JsonNode? node, VType to, out object? value)
    {
        value = null;
        if (node == null) return false;
        node = JsonNode.Parse(node.ToJsonString());
        if (node == null) return false;
        try
        {
            if (to == VType.Int)
            {
                var d = node.GetValue<double>();
                if (d != Math.Floor(d)) return false;
                value = (long)d;
                return true;
            }
            if (to == VType.Float) { value = node.GetValue<double>(); return true; }
            if (to == VType.Bool) { value = node.GetValue<bool>(); return true; }
            if (to == VType.Str) { value = node.GetValue<string>(); return true; }
            if (to.IsList && node is JsonArray arr)
            {
                var items = new List<object?>();
                foreach (var item in arr)
                {
                    if (!TryFromJson(item, to.Elem!, out var iv)) return false;
                    items.Add(iv);
                }
                value = items;
                return true;
            }
            if (to == VType.Vibe) { value = node.ToJsonString(); return true; }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { }
        return false;
    }
}
