# Security

`vibec` sends your source code to a language model and runs commands on your machine. Here's
exactly what it does, so you can decide what to trust.

## What leaves your machine

Your `.vibe` source, the questions semantic resolution asks about it, the generated specification,
and any compiler errors are sent to the configured provider (OpenAI by default). Don't compile code
you can't share with that provider. Use a local provider (`--provider ollama`) if you need to keep it local.

## What runs on your machine

- **Toolchain probes and builds.** The commands are written by the model, shown to you, and only run
  after you approve them (or pass `--yes`). Approved recipes are stored in `~/.vibec/toolchains/` as
  plain JSON you can read and edit. Commands run directly, never through a shell, so they can't pipe,
  redirect or chain, and recipes containing shell syntax are rejected.
- **The generated program is compiled but never run** by `vibec`. What you run from `bin/` is up to you.
  Read it first; it was written by a model.

## Keys

Keep your API key in `src/Vibec/appsettings.json` (gitignored) or the `OPENAI_API_KEY` environment
variable. Never commit it. Use a project key with a spending limit.

## Reporting a vulnerability

Please report security issues privately through GitHub's "Report a vulnerability" (Security tab →
Advisories), not in a public issue.
