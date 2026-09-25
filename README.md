[English](README.md) · [Português (BR)](docs/readme/README.pt-BR.md) · [Español](docs/readme/README.es.md) · [Deutsch](docs/readme/README.de.md) · [Italiano](docs/readme/README.it.md) · [Français](docs/readme/README.fr.md) · [עברית](docs/readme/README.he.md) · [العربية](docs/readme/README.ar.md) · [فارسی](docs/readme/README.fa.md) · [日本語](docs/readme/README.ja.md) · [한국어](docs/readme/README.ko.md) · [Русский](docs/readme/README.ru.md)

# LoopGolem

**Give it a goal. It keeps working.**

LoopGolem is an open-source autonomous task orchestrator for Windows and Linux. It turns a high-level goal into persistent, resumable, cost-aware agent work made of small tasks.

> **Early development:** architecture and interfaces are expected to evolve before the first stable release.

## Core principles

- **Persistent and resumable:** mission state survives process restarts.
- **Quota-aware:** hitting an included-plan limit pauses work instead of destroying the mission.
- **Cost-conscious:** inexpensive models handle focused tasks; stronger models are escalation tools.
- **Verify before spending AI:** builds, tests and deterministic checks run locally whenever possible.
- **Safe by default:** no password scraping, silent paid-credit purchases or automatic paid API fallback.
- **Provider-friendly:** Codex is the first target, but integrations live behind adapters.
- **Cross-platform:** Windows and Linux are first-class targets.

## Launch languages

English, Portuguese (Brazil), Spanish, German, Italian, French, Hebrew, Arabic, Persian, Japanese, Korean and Russian. Arabic, Hebrew and Persian are right-to-left from the start.

## Current autonomous flow

Codex missions use a persistent GPT-6 Luna High Supervisor for planning/recovery, bounded GPT-6 Luna Low workers with optional context affinity, deterministic host verification, and a separate persistent GPT-6 Luna High Validator for an independent final review. Recovery, session/turn telemetry and mission state are persisted in SQLite.

The controlled TaskForge benchmark procedure is documented in [docs/benchmark-v2.md](docs/benchmark-v2.md).

## Development

Requires the .NET 10 SDK.

```bash
dotnet restore LoopGolem.sln
dotnet build LoopGolem.sln
dotnet run --project src/LoopGolem.Desktop/LoopGolem.Desktop.csproj
```

Technical documentation, source code, comments and agent instructions are intentionally written in English.

## Planned distribution and updates

Windows installer/portable builds and Linux `.deb`, AppImage and tarball releases are planned. Update discovery will use GitHub Releases. Automatic update checking may be enabled, but installation remains user-controlled by default.

## License

Apache License 2.0. Commercial use, modification and redistribution are allowed under its terms. See [LICENSE](LICENSE) and [NOTICE](NOTICE).

LoopGolem is an independent open-source project and is not affiliated with or endorsed by OpenAI.
