# Contributing to Vibe

Thank you for wanting to write rules with us.

## Ground rules

1. **Order matters.** Read this file top to bottom.
2. **Say what you mean.** Issues and PRs should describe exactly what should happen, not what
   it should feel like.
3. **Check your work.** `dotnet test` must pass.

## Building

```powershell
dotnet build
dotnet test
```

The tests cover everything that doesn't need a model: lexer, parser, static pass, printer, and
every file in `examples/`. They're fast and free, so run them often.

To try the full pipeline you'll need a key. Copy `src/Vibec/appsettings.example.json` to
`src/Vibec/appsettings.json` and fill it in. That file is gitignored. Never commit a key.

## Where things live

| Path | What |
|---|---|
| `src/Vibec/Lexer.cs`, `Parser.cs` | source → AST |
| `src/Vibec/StaticPass.cs` | types, scopes, coercions; produces the questions for semantic resolution |
| `src/Vibec/Passes/` | the model passes: resolution (`VibePass`), lowering (`PromptPass`), backend (`CodegenPass`), toolchain discovery (`RecipePass`) |
| `src/Vibec/Providers/` | model providers behind `IModelProvider` |
| `src/Vibec/Printer.cs` | AST → normalized Vibe (`--dump-ast`) |
| `examples/` | every example is tested to parse, check and round-trip |

## Adding things

- **A keyword or alias:** add it to `Lexer.Keywords`, handle it in `Parser.Statement` or `Parser.Primary`,
  add a test in `ParserTests`, and document it in `SPEC.md`. Gen Z spellings must map to an existing
  construct or come with a standard spelling too.
- **A language feature:** parser, static pass, printer (it must round-trip), a semantics line in
  `PromptPass.Semantics` so the backend knows what it means, tests, and `SPEC.md`.
- **A provider:** implement `IModelProvider` and register it in `ProviderFactory`. If it can't enforce a
  JSON schema, return `SupportsJsonSchema = false` and `Structured.AskJsonAsync` handles the rest.
  Wanted: Anthropic, Claude CLI, Copilot CLI.
- **An example:** drop a `.vibe` file in `examples/`. The tests will hold it to account.

## Style

Match the code around you. Compiler messages are allowed to have personality; they are not allowed
to be unclear.

## Pull requests

Keep them small and focused. Describe what changes and how you checked it. "It vibes" is not a test plan.
