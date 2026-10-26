# Examples

| File | Shows |
|---|---|
| [hello.vibe](hello.vibe) | a rule |
| [greeter.vibe](greeter.vibe) | `while infinity`, an inline prompt, string interpolation |
| [calls.vibe](calls.vibe) | functions, calls without parentheses, a generated function |
| [taste.vibe](taste.vibe) | strongly-loose typing, `kinda`, `maybe`, `#prompt`, typo fixing, runtime coercion |
| [web.vibe](web.vibe) | `use http`, named arguments, durations, `json` |
| [nocap.vibe](nocap.vibe) | the Gen Z dictionary: `vibeshift`, `based`, `rentfree`, `yeet`, `touchgrass`, ... |

```powershell
vibec greeter.vibe              # → bin/greeter.exe
vibec greeter.vibe -t html      # → bin/greeter.html
vibec taste.vibe --dump-ast    # see what semantic resolution decided: .vibe-int/taste.ast.vibe
```

Every file here is checked by the test suite: it must parse, pass static analysis, and round-trip
through the printer.
