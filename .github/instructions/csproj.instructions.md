---
description: "Rules for editing .csproj project files in this repo. Covers the main-project-vs-tests glob trap."
applyTo: "**/*.csproj"
---

# .csproj Rules

## The tests glob trap (most important)
The repo holds **two** projects but only **one** is in the SDK glob's natural scope:

- [ss14-editor.csproj](../../ss14-editor.csproj) — main exe, sits at the **repo root**.
- [tests/ss14-editor.Tests/ss14-editor.Tests.csproj](../../tests/ss14-editor.Tests/ss14-editor.Tests.csproj) — xUnit project under `tests/`.

The main `.csproj` uses `Microsoft.NET.Sdk`, which by default globs `**/*.cs` under the project directory. That includes `tests/**`. The main project does **not** reference xUnit, so picking up test files breaks the build with `CS0246` for `Fact`, `Theory`, `InlineData`.

The main csproj therefore contains:

```xml
<ItemGroup>
  <Compile Remove="tests\**\*" />
  <None Remove="tests\**\*" />
  <EmbeddedResource Remove="tests\**\*" />
</ItemGroup>
```

**Do not remove that block.** If you reorganise folders, preserve the exclusion (rename it accordingly).

## Other invariants in the main csproj
- `<OutputType>Exe</OutputType>`, `<RootNamespace>Content.Editor</RootNamespace>` — load-bearing.
- `<EmbeddedResource Include="WebUI\**\*">` — frontend ships inside the binary. Do not remove or convert to `Content`.
- The XML doc files of the loaded game assemblies are read at runtime — do not enable `<GenerateDocumentationFile>` on the main project itself.

## Test csproj invariants
- `<AssemblyName>ss14-editor.Tests</AssemblyName>` must match the string in [src/InternalsVisibleTo.cs](../../src/InternalsVisibleTo.cs).
- `<IsPackable>false</IsPackable>` — never publish the test project.
- References: `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`. Do not add Moq / FluentAssertions / NSubstitute without explicit user request.

## Adding a NuGet package
- Use `<PackageReference Include="X" Version="Y" />`. Pin exact versions, no floating ranges.
- Verify the package targets `.NET 10` (or `netstandard2.0`). Some packages still lag.
