---
description: "Rules for editing Program.cs — the CLI entry point. Covers subcommand wiring and README synchronisation."
applyTo: "src/Program.cs"
---

# Program.cs — CLI Rules

## Subcommand contract
The current CLI surface is:

| Command | Effect |
|---|---|
| `ss14-editor` | starts the server in setup mode and opens the browser |
| `ss14-editor serve [path] [port]` | starts the server pointed at a project |
| `ss14-editor extract [path]` | extracts `metadata.json` without starting the server |

## Required follow-ups when changing CLI behaviour

1. **README.md sync.** The "CLI usage" section in [README.md](../../README.md) **is** user-facing documentation. Adding a subcommand, renaming a flag, changing default ports, or changing the `extract` output path requires editing the matching section. Failure to update README is the single most common source of user confusion.
2. **Browser-open behaviour.** If you change when the browser is auto-opened, also update the "Quick start" section of README.
3. **Exit codes.** Stick to `0` (success) / `1` (general error) / `2` (bad arguments). Don't invent new codes without updating docs.
4. **Tests.** There is no test for `Program.cs` itself (it's a thin shell). If you move logic from `Program.cs` into a service, write a test for the service.

## Things this file should **not** do
- Heavy lifting. Delegate to services: [src/EditorServer.cs](../../src/EditorServer.cs), [src/MetadataExtractor.cs](../../src/MetadataExtractor.cs).
- Logging setup beyond `Console.WriteLine`. Adding a logger is a separate, discussed refactor.
