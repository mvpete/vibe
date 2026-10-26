# Vibe

**Deterministic vibes. Post-vibe coding.**

[![ci](https://github.com/mvpete/vibe/actions/workflows/ci.yml/badge.svg)](https://github.com/mvpete/vibe/actions/workflows/ci.yml)
[![release](https://img.shields.io/github/v/release/mvpete/vibe?include_prereleases)](https://github.com/mvpete/vibe/releases)
![license: MIT](https://img.shields.io/badge/license-MIT-blue)
![.NET 9](https://img.shields.io/badge/.NET-9-512BD4)
![typing: strongly-loose](https://img.shields.io/badge/typing-strongly--loose-ff69b4)
![vibes: checked](https://img.shields.io/badge/vibes-checked-brightgreen)

Vibe coding is chaos. You describe what you want, you hope, and you get something that is
roughly the shape of your hopes. Nobody knows what it will do. Including you.

So we tried something different. What if, instead of describing a *vibe*, you wrote down
**exactly what should happen, step by step, in order**? Precise instructions. Rules. A
machine follows them one after another, and you know what it will do because you said so.

We've been calling this an **imperative** approach, because each rule is a command.
As far as we can tell, nobody has tried this.

```vibe
print("hello, world")
```

That's a rule. It does one thing. It always does that thing. Incredible.

---

## Core concepts

These took a while to work out. We're sharing them freely.

### Rules run in order

The first rule happens first. Then the second one. We call this **order**.

```vibe
print("first")
print("second")
print("third")
```

You will never get "third" before "first". Try getting that guarantee from a vibe.

### Vibe grammar

When you vibe code, you write English and hope. When you write Vibe, you still write English.
Sort of. It's **Vibe grammar**: the same words you'd type into a chat box, arranged into rules
a machine can't argue with.

```
Vibe coding:  "make it say hi to whoever's there. forever, I guess"

Vibe:         while infinity {
                  string name = %ask the user their name%
                  print "hello {$name}"
              }
```

Same idea. Less vibe, more rigor. It's less verbose too.

Some people call this "pseudocode". We just decided to make it compile. [The whole grammar fits on one
page](SPEC.md#vibe-grammar), which is more than English can say.

### Named vibes

Sometimes you want to remember something and use it later. Give it a name.

```vibe
int count = 12
print(count)
```

We call these **named vibes**. A colleague says the industry term might be "variables",
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
to it. We call this **strongly-loose typing**. It is strong, and it is loose.

### Doing things more than once

Some rules deserve to happen more than once. You could write them out three times. We tried
that. It was a lot of typing. Instead, say how many times:

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

Every combination means the same thing. We checked all 48.

And no, we didn't forget about `while`. For when you really don't know how many times you want
to do something, only that you're *not done yet*:

```vibe
while infinity {
    print "are we there yet?"
}
```

Yes, `infinity` works. We know what you meant, and so does Vibe.

There's no `do ... while`. If you want to do something first, write it first: rules run in
order. Or just use `for`. We did forget about `until` and `loop`, but nobody's noticed yet.

### Making decisions

Sometimes a rule should only happen if something is true. We call this a **decision**. You make
thousands a day. Now your program can make one.

```vibe
if count > 10 {
    print "that's a lot"
} else {
    print "that's fine"
}
```

For more than two outcomes there's `else if`, and when the ladder of `else if`s gets embarrassing,
`switch` (or `vibeshift`, if you're feeling it):

```vibe
vibeshift mood {
    "great", "amazing" => print "slay"
    "meh" => print "it's giving mid"
    _ => print "no thoughts, head empty"
}
```

### Reusable rule groups

Write a group of rules once, give it a name, and use it whenever you like. The first word is a
promise about what comes back.

```vibe
int area(int width, int height) {
    return width * height
}

print area(3, 4)
print area 3, 4        # the parentheses were only ever a suggestion
```

"Programmers" of yester-year called these "functions" (or "methods", or "procedures"). We liked the idea. The parentheses are optional.

### Rule groups you didn't write

This one surprised us. If you use a rule group that doesn't exist, Vibe works out what you
obviously meant by the name and writes it for you. It tells you, so you stay in control:

```vibe
int lucky = GenerateLuckyNumber()
```
```
warning: line 1: 'GenerateLuckyNumber' isn't defined. vibed one up: int GenerateLuckyNumber()
```

### Rules in other files

Once you have enough rules, they stop fitting in one file. Put them in several and `use` the ones
you need. We call each file a **module**, which sounds more organised than "another file".

```vibe
# game.vibe
public int score(list<int> rolls) {
    return rolls.filter(r => r > 3).length * 10
}

# main.vibe
use game
print game.score [4, 6, 2, 5]
```

Only `public` (or `flex` cause y'know) rules can be used from outside. Everything else stays between a module
and itself. Modules that use each other in a circle are an error: *circular vibes*.

### Other people's rules

Some rules everybody needs: fetching a web page, reading a file, waiting a bit. Vibe comes with
those, and you bring them in with `use`:

```vibe
use http
json res = http.get "https://example.com/api" timeout: 2s
print res.price
```

There's `http`, `fs`, `time`, `random`, `env`, `math` and `server` (yes, you can
[write a web server](examples/webserver) in rules). Forget the `use` and Vibe will still guess
what `http.get` means, but it will warn you. Guessing is the thing we're trying to stop.

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

We call this **FTFY**. It only fixes what you obviously meant: typos of names in scope, loops that
read the list instead of the item, stray braces, a counter you forgot to declare, `if x = 5`,
`xs.lenght`. The full list is in [SPEC.md](SPEC.md#ftfy). Everything else is still your problem.

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
your rules → parse → semantic resolution → lowering → IL → backend → build → program
```

1. **Parse.** Your rules are read and structured. Carefully. In order.
2. **Semantic resolution.** Vibe guarantees are enforced. `"twelve"` becomes `12`. Missing rule groups get written. Typos get fixed.
3. **Lowering.** Your program becomes the **IL**: a precise, step-by-step specification that doesn't mention any programming language.
4. **Backend.** The IL, plus the language you asked for, becomes source code. It's the only step
   that knows which language you wanted, and it isn't allowed to tell the others.
5. **Build.** The code is compiled with your real compiler. If it doesn't compile, or leaves out anything your rules print, the backend is told exactly what went wrong and tries again.

Nothing is vibed. Everything is checked. You can read every step:

```
.vibe-int/app.il.md                 the IL: your program, in no language in particular
.vibe-int/app.rust.prompt.md        what the backend receives: the IL plus the target
.vibe-int/app.rust.transcript.md    every backend attempt and build error
.vibe-int/app.ast.vibe              your rules after semantic resolution (with --dump-ast)
```

### Write once, compile anywhere

The IL is a plain Markdown file of numbered steps: "read a line of standard input", "print
`hello ` followed by the name". It names no language, so it compiles to any of them:

```powershell
vibec greeter.vibe                          # rules → IL → C#
vibec .vibe-int/greeter.il.md -t javascript # the same IL → JavaScript
vibec .vibe-int/greeter.il.md -t rust       # → Rust
```

Lowering only runs when your rules change. Compiling the same rules to a second language reuses the
IL, so it's one model call instead of two. You can also hand someone an `.il.md` file and they can
build it for whatever they've got, without ever seeing Vibe.

We believe this is a first.

> Fine print: semantic resolution, lowering, and the backend are performed by a large
> language model (OpenAI by default, or anything OpenAI-compatible, like a local Ollama).
> This is an implementation detail. You are still writing rules.

---

## Getting started

**The quick way:** grab the zip for your platform from [Releases](https://github.com/mvpete/vibe/releases),
unzip it, copy `appsettings.example.json` to `appsettings.json` next to `vibec`, put your key in it,
and run `vibec doctor csharp`. No .NET install needed to run the compiler itself. (Compiling *to* C#
still needs the .NET SDK, the same way compiling to Rust needs Rust.)

**From source:**

You need the [.NET 9 SDK](https://dot.net/download) and an OpenAI API key (or a local
[Ollama](https://ollama.com)). Rules need a little help getting started. We don't talk about it.

```powershell
git clone https://github.com/mvpete/vibe.git
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

Every compile ends with what it cost, because precision includes the bill:

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
Programming is when you write code. This is when you write rules, and then the rules become code. Not the same.

**Isn't strongly-loose typing an oxymoron?**
So is jumbo shrimp. People still order it.

**Isn't `int count = "twelve"` exactly the kind of ambiguity you're trying to avoid?**
No. It's a guarantee. The ambiguity is resolved at compile time and you're warned about anything
that couldn't be. That's the opposite of a vibe.

**The IL is just a prompt. Why not write it myself?**
You could! You'd be describing what you want in plain English and hoping it comes out right,
which is a lovely way to spend an afternoon. It's also vibe coding. The IL is written *from* your
rules and checked *against* them, so it's only ever as vague as you were. Hand-vibing is like
hand-writing assembly: technically allowed, rarely recommended, always impressive at parties.

**So I can't vibe code anymore?**
You can. You just shouldn't. Write rules instead.

**Wait... wasn't Vibe vibe coded?**
Yes. That's how we know it's a problem.

---

## Contributing

Rules welcome. See [CONTRIBUTING.md](CONTRIBUTING.md). `dotnet test` runs the whole suite without a
model or a key.

## License

[MIT](LICENSE). Write rules responsibly.
