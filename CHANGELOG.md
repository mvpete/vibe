# Changelog

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
- `vibec doctor`. Every stage writes its output to `.vibe-int/`: annotated AST, the IR prompt, the codegen transcript.
