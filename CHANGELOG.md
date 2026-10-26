# Changelog

## Unreleased

### Language
- Standard library, opt-in with `use`: `http`, `fs`, `time`, `random`, `env`, `math`. Contracts are written in Vibe
  with `native` functions the backend implements. Without `use`, module calls are vibed, with a warning.
- `json` type with field access.
- Named arguments (`timeout: 2s`, no comma needed after a paren-less call) and parameter defaults.
- Duration literals: `500ms`, `2s`, `1.5m`, `1h`.
- Contracts: functions without bodies get their bodies written by semantic resolution.
- `public` / `flex`, `use x as y`.
- Project modules: `use storage` loads `storage.vibe`, with privacy, cross-module type checking, declaration-only
  library modules and cycle detection. Contract bodies are written from the module's prompt and the modules it uses.
- Every `for` spelling: `for`/`foreach`, optional parens, typed or `var` loop variables, `in`/`of`/`:`, plus C-style `for (;;)`.
- `int(x)`, `float(x)`, `string(x)`, `bool(x)`, `json(x)` conversions.
- `print ... end: ""` / `sep: ", "`, and `write` / `output` (print without a newline).
- `i++`, `i--`, `++i`, `--i` as expressions. FTFY declares an undeclared counter (`int i = 0`) before its loop.
- Fixed: anything after the first expression inside `${...}` was silently ignored; it's now an error.
- FTFY: obvious mistakes (typos of names in scope, loops that read the list instead of the item, stray `}` after an
  interpolation) are fixed in your source file. `--no-ftfy` warns instead; `vibec undo` / `vibec nvm` puts it back.

### Compiler
- Every compile reports its model calls, tokens (cached input priced separately) and cost, using prices from OpenAI's pricing page (`IModelProvider.GetPriceAsync`); override in appsettings.json.
- Default model is now `gpt-6-luna`.
- Fixed: contract and generated-function questions now send their context (signature, module prompt) to the model.
- A contract's cache key includes its context, so editing a module's prompt re-generates its bodies. `--fresh` now replaces the cache.
- Uncaught errors in generated programs print `error: <message>` and exit 1, instead of a stack trace.

## 0.1.0: the discovery

First release. Rules run in order.

### Language
- Strongly-loose typing: `int`, `float`, `string`, `bool`, `list<T>`, `vibe`, `var`. Declared types always win.
- C#-style functions, lambdas, `if` / `while` / `for` / `repeat`, `try` / `catch`, `switch`.
- Calls without parentheses: `greet "world"`, `add 1, 2`.
- Interpolation: `f"{x}"`, `$"{x}"`, and `{$x}` / `${x}` in plain strings.
- Inline prompts: `%ask the user their name%` as a value or a step; `#prompt` / `%prompt { }` for longer instructions.
- Explicit conversion: `vibe<int>("twelve")`.
- Undefined functions are written by the compiler. Undeclared words get a meaning from context
  (`while infinity`), and typos get fixed.
- Gen Z spellings for everything: `nocap`, `yeet`, `ghost`, `nvm`, `vibeshift`, `based`, `rentfree`,
  `touchgrass`, `periodt`, `maincharacter`, `spill`, `lowkey`, `highkey`, `delulu`, and more.

### Compiler
- Pipeline: parse → static pass ⇄ semantic resolution → lowering → backend → build ⇄ repair.
- Model-discovered toolchains for any target language, approved once and cached.
- Build errors are fed back to the backend until it compiles.
- Output layout: `.vibe-int/` for intermediates, `bin/` for the program.
- Providers: OpenAI and anything OpenAI-compatible (Ollama, LM Studio, vLLM).
- `vibec doctor`. The IR prompt (`.vibe-int/<name>.<target>.prompt.md`) and codegen transcript are always written; `--dump-ast` adds the annotated AST.
