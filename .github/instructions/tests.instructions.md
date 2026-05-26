---
description: "Conventions for the xUnit test project under tests/ss14-editor.Tests/."
applyTo: "tests/**/*.cs"
---

# Test Project Rules

## Layout
- Single xUnit project at [tests/ss14-editor.Tests/](../../tests/ss14-editor.Tests/).
- AssemblyName: `ss14-editor.Tests`. Root namespace: `Content.Editor.Tests`.
- Internals from the main project are visible via `[InternalsVisibleTo]` declared in [src/InternalsVisibleTo.cs](../../src/InternalsVisibleTo.cs). Do **not** mark test targets `public` in the main project — keep them `internal`.

## Conventions
- Use **xUnit**: `[Fact]`, `[Theory]`/`[InlineData]`. No FluentAssertions, no Moq — stay vanilla.
- One test class per source file under test. Name it `<SourceFile>Tests.cs`.
- Use the `TempDir` helper in [tests/ss14-editor.Tests/TempDir.cs](../../tests/ss14-editor.Tests/TempDir.cs) for any filesystem work. Always `using (var tmp = new TempDir())` — never leak temp directories.
- Prefer table-driven tests via `[Theory]` for input/output matrices.
- Tests must be order-independent and parallel-safe (xUnit runs classes in parallel by default).

## When you add a new source file to `src/`
Create a matching `XxxTests.cs` in this folder. The mapping is enforced by the file-scoped rule in [.github/instructions/csharp-server.instructions.md](../csharp-server.instructions.md).

## Running
- `& 'C:\Program Files\dotnet\dotnet.exe' test tests\ss14-editor.Tests\ss14-editor.Tests.csproj`
- Test output is in Russian on this machine (locale issue, cosmetic). Watch for the final summary line: `пройдено N` = passed N.

## What this project does **not** test
- `MetadataLoadContext`-loaded game assemblies — requires a built SS14 fork. Avoid in unit tests; cover via integration scripts.
- The actual `EditorServer` HTTP loop end-to-end. Test handlers in isolation via `ApiRouter.DispatchAsync` against a stub `HttpListener` (see [HttpJsonTests.cs](../../tests/ss14-editor.Tests/HttpJsonTests.cs) for the ephemeral-port pattern).
