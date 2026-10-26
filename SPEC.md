# Vibe

A strongly-loose typed, imperative language for vibe coding. C#'s types, Python's ease,
JavaScript's looseness. The declared type always wins; values get *interpreted* to fit it.

```vibe
int five = "five"                       # → 5
list<int> evens = "first five evens"    # → [2, 4, 6, 8, 10]
int lucky = GenerateLuckyNumber()       # not defined anywhere: the compiler writes it
```

`vibec` compiles Vibe to real code in any language by way of a language model.

## Pipeline

```
.vibe → lex → parse → static pass ⇄ vibe pass 🤖 → annotated AST
      → prompt pass 🤖 → prompt (IR) → codegen 🤖 → build ⇄ repair → output
```

| Stage | Job |
|---|---|
| **static pass** | Scopes, type inference, every place a value must bend to fit a type. Settles the deterministic cases (`"42"` → `42`) and turns the rest into *vibe questions*. |
| **vibe pass** | One batched, structured model call answers the questions: literal coercions, undefined functions (it writes them, in Vibe), unknown methods, typos. Answers are cached in `.vibe-int/vibecache.json`. The static pass re-runs with the answers, up to 3 rounds. |
| **prompt pass** | A model turns the annotated AST into a precise implementation spec for the target: the IR. |
| **codegen** | A model implements the IR. The output is built with the real toolchain, and compiler errors are fed back as follow-up turns until it builds (`--max-repairs`, default 3). |

Toolchains aren't hardcoded. The first time you use a target, the model writes a *recipe*: how to
detect the toolchain and how to compile-check one file. You approve it once, and it's cached
in `~/.vibec/toolchains/<target>.json`. Commands never run through a shell.

## Types

`int` (64-bit) · `float` (double) · `string` · `bool` · `list<T>` · `json` · `vibe` (dynamic) · `void` (functions only)

`json` holds parsed JSON. `x.field` and `x[i]` are json too (missing → null). Assigning a string to a
`json` parses it; assigning a json to another type reads it as that type. Both happen at runtime.

Duration literals are float seconds: `500ms`, `2s`, `1.5m`, `1h`, `1d`.

`var` / `let` infers the type from the value and never coerces.

## Strongly-loose typing

A value is coerced toward the expected type in declarations, assignments, arguments, returns,
conditions, indexes, arithmetic and `repeat` counts.

| Source | Resolved by | Example |
|---|---|---|
| obvious literal | static pass | `int x = "42"` → `42`, `bool b = "yes"` → `true`, `int n = 2.6` → `3` |
| meaningful literal | vibe pass (model) | `float rent = "about 2k"` → `2000.0` |
| uninterpretable literal | deferred to the generator, with a warning | `int lots = "a lot"` |
| runtime value | `vibe<T>(x)` in the generated code | `int x = ask("number?")` |

Runtime coercion rules: strings to numbers accept digits or English number words; strings to
bools accept yes/yeah/y/true/1 and no/nah/n/false/0/empty; numbers to strings format as `print`
does; float to int rounds half away from zero. A failed runtime coercion raises an error you can
catch.

## Syntax

```vibe
#prompt keep all output lowercase          # global instruction (top of file)

string greet(string who) {                 # C#-style functions
    return f"hey {who}"                    # f"..." or $"..." interpolation
}

void main_loop(list<int> xs) {
    for x in xs {                          # also: for (x in xs), for (var x of xs)
        if x kinda 5 {                     # fuzzy equality (within 10%)
            print("close enough:", x)
        } else if x > 100 {
            break
        }
    }
}

var names = [1, 2, 3].map(n => f"user{n}") # lambdas, members
int roll = %a fair six-sided dice roll%    # inline prompt: the generated code implements it
string who = %ask the user their name%     # ...including I/O
string mood = ask("how are we feeling?")   # input (also: input("..."))
print "hello {$who}"                       # parens optional; {$x} / ${x} interpolate plain strings

while infinity {                           # undeclared words get a meaning from context: true
    break
}
repeat 3 times { print("hip hip") }
vibecheck roll >= 1                        # assert (also: assert)

try {
    int n = mood
} catch (e) {                              # also: sus (e)
    print(e)
}

%prompt {
    Finish with a sign-off that mentions the roll.
}                                          # local instruction: performed right here
```

- No semicolons needed (newlines end statements; `;` also works).
- Calls don't need parentheses: `greet "world"`, `add 1, 2`, `xs.push 4`, `for i in range 5`.
  A name followed on the same line by a value is a call; arguments are comma-separated. Operators
  keep their meaning (`x - 1` is subtraction). A declared function with no parameters can be called by
  its bare name.
- Strings: `f"{x}"` / `$"{x}"` interpolate every `{...}`; plain `"..."` strings interpolate only `{$x}` and `${x}`.
- Comments: `#` and `//` to end of line, `/* ... */` blocks.
- Operators: `+ - * / %`, `== != < > <= >=`, `kinda`, `and or not` / `&& || !`, `= += -= *= /= ++ --`.
- `+` with a string concatenates; `int / int` is integer division.
- `maybe` is a fresh coin flip each time it's evaluated.

### Aliases

Every construct with a standard spelling also has a Gen Z one. Both always work; `--dump-ast`
prints the standard form.

| Standard | Gen Z | Notes |
|---|---|---|
| `true` / `false` | `yeah` / `nah`, `nocap` / `cap`, `W` / `L` | |
| `null` | `idk` | |
| `print ...` | `say ...` | |
| `break` / `continue` | `ghost` / `nvm` | |
| `return` | `itsgiving` | a bare return inside `maincharacter` ends it |
| `x += 1` | `glowup x` | |
| `throw x` | `yeet x` | message is `x` stringified |
| `try { } catch (e) { }` | `sus (e)`, `ick (e)` | |
| `assert cond` | `vibecheck cond`, `fr cond` | fails loudly |
| `assume cond` | `delulu cond` | trusted, never checked |
| `const int x = 1` | `based int x = 1` | can't be reassigned |
| `global int x = 0` | `rentfree int x = 0` | visible everywhere, even before its declaration; needs an explicit type |
| `switch x { a, b => ...  _ => ... }` | `vibeshift x { ... }` | first match wins, no fallthrough; `_` or `else` is the default; patterns are coerced to `x`'s type |
| `sleep d` | `touchgrass d` | strongly-loose duration in seconds: `1.5`, `"half a second"`, `"2 minutes"` |
| `exit` / `exit code` | `periodt` | |
| | `maincharacter { }` | the entry point; runs after other top-level statements |
| | `spill x` | debug print to stderr: `x = 3` |
| | `lowkey a, b` | print to stderr |
| | `highkey a, b` | print, uppercased, with `!!` |

`W`, `L` and `cap` are reserved words, so they can't be used as names.

### Built-ins

`len(x)`, `range(n)` / `range(a, b)`.
Lists: `.length`, `.push(x)`, `.pop()`, `.contains(x)`, `.indexOf(x)`, `.join(sep)`, `.reverse()`, `.sort()`,
`.map(f)`, `.filter(f)`, `.any(f)`, `.all(f)`, `.find(f)`, `.forEach(f)`.
Strings: `.length`, `.upper()`, `.lower()`, `.trim()`, `.split(sep)`, `.contains(s)`, `.startsWith(s)`,
`.endsWith(s)`, `.replace(a, b)`, `.indexOf(s)`.
Anything else is sent to the vibe pass, which decides what it means.

### Named arguments and defaults

```vibe
int add(int a, int b = 2) { return a + b }
add 1                 # 3
add 1, b: 5           # 6
add 1 b: 5            # named arguments don't need a comma
```

Positional arguments come first. Every parameter without a default must be given.

### Contracts

A function with a signature and no body is a contract. Semantic resolution writes the body, in Vibe,
and it's checked like the rest of your code (`--dump-ast` shows it):

```vibe
list<string> slugify(list<string> titles)    # lowercase, dashes for spaces
```

`public` (Gen Z: `flex`) marks what a module exports. `native` is reserved for the standard library:
a native function is implemented by the backend, in the target language.

### Standard library

`use` brings in a module from Vibe's standard library, and with it that module's rules: typed
signatures, defaults, units, and what throws. The contracts are plain Vibe in
[`src/Vibec/std/`](src/Vibec/std).

```vibe
use http
json res = http.get "https://bing.ca" timeout: 2s
print res.field
```

| Module | Functions |
|---|---|
| `http` | `get`, `post`, `put`, `delete`, `status`; `timeout` in seconds, non-2xx throws |
| `fs` | `read`, `lines`, `write`, `append`, `exists`, `delete`, `files` |
| `time` | `now`, `millis`, `since`, `sleep`, `format` |
| `random` | `between`, `unit`, `chance`, `pick`, `shuffle` |
| `env` | `args`, `get`, `cwd` |
| `math` | `sqrt`, `pow`, `abs`, `floor`, `ceil`, `round`, `min`, `max`, `pi` |

`use http as web` gives a module another name. Wrong arguments, unknown parameters and unknown
functions are compile errors.

**Without `use`, you get vibes.** `http.get(...)` with no `use http` still compiles: semantic
resolution decides what it probably means, and you get a warning pointing at `use`. The standard
library's rules only apply when you ask for them.

### Undefined functions

Calling a function nobody defined isn't an error. The vibe pass writes it in Vibe, using the
call's argument types and the type the caller expects back. The generated function is checked like
your own code, and you get a warning. Use `--dump-ast` to read it.

### Explicit conversion

`vibe<T>(x)` converts explicitly, using the same rules as implicit coercion: `vibe<int>("twelve")`,
`vibe<list<string>>("red, green and blue")`. It's also how `--dump-ast` writes runtime and
deferred coercions, so its output is valid Vibe.

### Undeclared words

An undeclared name isn't automatically an error. Semantic resolution looks at where it's used: a typo
of something in scope gets fixed (`lukcy` → `lucky`), and a word with an obvious meaning gets
one, typed by its context: `while infinity` → `true`, `float x = pi` → `3.14159`. Either way you
get a warning. Anything else is an error.

### Prompts

Natural language, clearly marked, in three forms:

| Form | Meaning |
|---|---|
| `%text%` used as a value | an inline prompt: produces a value of the type expected there (`string name = %ask the user their name%`) |
| `%text%` on its own line | a step to perform right there |
| `#prompt text` / `%prompt { ... }` | a longer instruction, to end of line or as a block |

Prompts at the top of the file, before any code, apply to the whole program. `%` between two values
is still the remainder operator. `#prompt` only counts as a directive when followed by a space or `{`;
other `#` lines are comments. Write `%%` for a literal `%` inside an inline prompt.

## CLI

```
vibec app.vibe --target rust              → bin/app.exe (built and repaired with rustc)
vibec app.vibe -t html -o site            → site/app.html
vibec doctor [target]                     model reachability + toolchain status

  -o, --out DIR     output folder (default: bin/ next to the source)
  --dump-ast        also write the annotated AST → .vibe-int/app.ast.vibe
  --no-build        skip build/repair (output is unverified)
  --max-repairs N   default 3
  --provider P      openai (default) | ollama
  --model M         default gpt-5
  --base-url U      any OpenAI-compatible endpoint
  --fresh           ignore the vibe cache
  --refresh         re-discover the toolchain recipe
  -y, --yes         approve a new toolchain recipe without asking
```

Layout, next to the source:

```
.vibe-int/vibecache.json        resolved vibes, so unchanged code isn't re-asked
.vibe-int/<name>.<target>.prompt.md      the IR: exactly what the backend receives
.vibe-int/<name>.<target>.transcript.md  every backend attempt and build error
.vibe-int/<name>.ast.vibe                annotated AST, with --dump-ast
.vibe-int/src/<name>-<target>/  generated source and build files; the build runs here
bin/                            the deliverable: an executable, or the source for interpreted targets
```

Settings come from the command line, then environment variables (`VIBEC_PROVIDER`, `VIBEC_MODEL`,
`OPENAI_API_KEY`), then `appsettings.json` next to `vibec.exe` (see `src/Vibec/appsettings.example.json`).
Approved toolchain recipes live in `~/.vibec/toolchains/`.
