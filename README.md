# Vibe

**Stop vibe coding. Start writing rules.**

![license: MIT](https://img.shields.io/badge/license-MIT-blue)
![.NET 9](https://img.shields.io/badge/.NET-9-512BD4)
![typing: strongly-loose](https://img.shields.io/badge/typing-strongly--loose-ff69b4)
![vibes: checked](https://img.shields.io/badge/vibes-checked-brightgreen)

Vibe coding is chaos. You describe what you want, you hope, and you get something that is
roughly the shape of your hopes. Nobody knows what it will do. Including you.

So I tried something different. What if, instead of describing a *vibe*, you wrote down
**exactly what should happen, step by step, in order**? Precise instructions. Rules. A
machine follows them one after another, and you know what it will do because you said so.

I've been calling this an **imperative** approach, because each rule is a command.
As far as I can tell, nobody has tried this.

```vibe
print("hello, world")
```

That's a rule. It does one thing. It always does that thing. Incredible.

---

## Core concepts

These took a while to work out. I'm sharing them freely.

### Rules run in order

The first rule happens first. Then the second one. I call this **order**.

```vibe
print("first")
print("second")
print("third")
```

You will never get "third" before "first". Try getting that guarantee from a vibe.

### Named vibes

Sometimes you want to remember something and use it later. Give it a name.

```vibe
int count = 12
print(count)
```

I call these **named vibes**. A colleague says the industry term might be "variables",
but they also still vibe code, so.

### Vibe guarantees

Notice the `int` above? That's a **vibe guarantee**: a promise about what kind of thing
the named vibe holds. Whole numbers, in this case.

And here is the breakthrough. The guarantee always wins, no matter what you put in:

```vibe
int count = "twelve"                 # 12
float price = "about 2k"             # 2000.0
bool ready = "yeah fr"               # true
list<int> evens = "first five evens" # [2, 4, 6, 8, 10]
```

Other systems would reject `"twelve"`. Vibe understands what you *meant*, then holds you
to it. I call this **strongly-loose typing**. It is strong, and it is loose.

### Doing things more than once

```vibe
repeat 3 times {
    print "again"
}

for n in [1, 2, 3] {
    print "number {$n}"
}
```

People disagree about how to write a loop, so Vibe accepts all of it. `for` or `foreach`, with or
without parentheses, `in` or `of` or `:`, and a loop variable that's bare, `var`, or typed:

```vibe
for n in numbers { }
foreach (var n of numbers) { }
for (int n : numbers) { }               # a typed variable bends each element to fit
for (int i = 0; i < 10; i++) { }        # the classic, also accepted
```

Every combination means the same thing. I checked all 48.

### Making decisions

```vibe
if count > 10 {
    print("that's a lot")
} else {
    print("that's fine")
}
```

### Reusable rule groups

Write a group of rules once and use it by name. The first word says what kind of thing
comes back.

```vibe
int area(int width, int height) {
    return width * height
}

print(area(3, 4))
```

### Rule groups you didn't write

This one surprised me. If you use a rule group that doesn't exist, Vibe works out what you
obviously meant and writes it for you. It tells you, so you stay in control:

```vibe
int lucky = GenerateLuckyNumber()
```
```
warning: line 1: 'GenerateLuckyNumber' isn't defined. vibed one up: int GenerateLuckyNumber()
```

### Compiler... you know what I meant.

Sometimes you write one thing and obviously mean another. Vibe notices, fixes your file, and
tells you:

```vibe
for item : res {
    say "${res.ticker} : ${res.sentiment}}"
}
```
```
ftfy    line 2: res.ticker → item.ticker (you're looping over res as item)
ftfy    line 2: res.sentiment → item.sentiment (you're looping over res as item)
ftfy    line 2: "...${res.sentiment}}" → "...${res.sentiment}" (stray } after an interpolation)
ftfy    fixed 3 thing(s) in your source. changed your mind? vibec undo app.vibe
```

I call this **FTFY**. It only fixes what you obviously meant: typos of names in scope, loops that
read the list instead of the item, stray braces, a counter you forgot to declare. Everything else is still your problem.

Not ready to let go? `--no-ftfy` turns the fixes into warnings. Regret it? `vibec undo app.vibe`
(or `vibec nvm app.vibe`) puts back exactly what was there, one FTFY at a time.

### The vibe check

Some things must be true. Say so.

```vibe
vibecheck count > 0
```

If it isn't true, the program stops and tells you. Rules have consequences.

### When you really can't be precise

Sometimes you know *what* you want but not *how*. Wrap it in `%...%`:

```vibe
int roll = %a fair six-sided dice roll%
string name = %ask the user their name%
```

Or give a direct instruction with `#prompt`:

```vibe
#prompt make all output sound mildly impressed
```

To be clear: this is **not** vibe coding. It's a clearly labelled, precisely positioned
instruction inside a rule. Totally different.

---

## How it works

You write rules. `vibec`, the **Vibe compiler**, turns them into a real program in the
language of your choice: C#, Rust, Go, HTML, whatever you like.

```
your rules → parse → semantic resolution → lowering → backend → build → program
```

1. **Parse.** Your rules are read and structured.
2. **Semantic resolution.** Vibe guarantees are enforced. `"twelve"` becomes `12`. Missing rule groups get written. Typos get fixed.
3. **Lowering.** Your program becomes a precise specification for the target language.
4. **Backend.** The specification becomes source code.
5. **Build.** The code is compiled with your real compiler. If it doesn't compile, the backend is told exactly what went wrong and tries again.

Nothing is vibed. Everything is checked. You can read every step:

```
.vibe-int/app.rust.prompt.md        the specification the backend receives
.vibe-int/app.rust.transcript.md    every backend attempt and build error
.vibe-int/app.ast.vibe              your rules after semantic resolution (with --dump-ast)
```

> Fine print: semantic resolution, lowering, and the backend are performed by a large
> language model (OpenAI by default, or anything OpenAI-compatible, like a local Ollama).
> This is an implementation detail. You are still writing rules.

---

## Getting started

You need the [.NET 9 SDK](https://dot.net/download) and an OpenAI API key (or a local
[Ollama](https://ollama.com)).

```powershell
git clone https://github.com/YOUR-USERNAME/vibe.git
cd vibe

# put your key in appsettings.json (it's gitignored)
copy src\Vibec\appsettings.example.json src\Vibec\appsettings.json
notepad src\Vibec\appsettings.json

dotnet build
$vibec = "src\Vibec\bin\Debug\net9.0\vibec.exe"

& $vibec doctor csharp                  # check the backend and your toolchain
& $vibec examples\hello.vibe            # → examples\bin\hello.exe
```

The first time you target a language, `vibec` works out how to compile it on your machine and
shows you the exact commands before running anything. Approve once and it remembers.

Every compile ends with what it cost:

```
  usage        2 model call(s), 1,196 in + 1,116 out tokens, $0.0007 (OpenAI pricing page, 2026-10-25)
```

Output goes next to your rules:

```
examples/
├── hello.vibe
├── .vibe-int/        intermediates: resolved rules, the specification, generated source (src/)
└── bin/              the program
```

| Flag | What it does |
|---|---|
| `-t, --target <lang>` | any language (default `csharp`) |
| `-o, --out <dir>` | where the program goes (default `bin/`) |
| `--dump-ast` | also write your rules after semantic resolution |
| `--no-ftfy` | warn about obvious mistakes instead of fixing them in your file |
| `vibec undo <file>` | put back what FTFY changed (alias: `nvm`) |
| `--no-build` | skip the build (not recommended; rules deserve verification) |
| `--provider`, `--model`, `--base-url` | which backend |

The full rulebook is in [SPEC.md](SPEC.md). The reasoning behind all this is in [VIBE.md](VIBE.md).

---

## FAQ

**Isn't this just programming?**
Programming is when you write code. This is when you write rules, and then the rules become code.

**Isn't strongly-loose typing an oxymoron?**
So is jumbo shrimp. People still order it.

**Isn't `int count = "twelve"` exactly the kind of ambiguity you're trying to avoid?**
No. It's a guarantee. The ambiguity is resolved at compile time and you're warned about anything
that couldn't be. That's the opposite of a vibe.

**So I can't vibe code anymore?**
You can. You just shouldn't. Write rules instead.

---

## Contributing

Rules welcome. See [CONTRIBUTING.md](CONTRIBUTING.md). `dotnet test` runs the whole suite without a
model or a key.

## License

[MIT](LICENSE). Write rules responsibly.
