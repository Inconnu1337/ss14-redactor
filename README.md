# SS14 Prototype Redactor

A standalone visual editor for [Space Station 14](https://github.com/space-wizards/space-station-14) prototype YAML files.  
Works with **any SS14 fork** — just point it at your project folder.

---

## What it does

- Browse and edit prototype YAML files through a browser-based UI
- Auto-completes entity IDs, component names, field types from your project's assemblies
- Shows inheritance chains, validates required fields, renders sprites inline
- Watches the `Resources/Prototypes/` folder for external changes and reloads open files
- Runs as a self-contained local HTTP server — no internet required, no install required

---

## Quick start

### Option A — Download a binary

Go to the [Releases](../../releases) page and download the binary for your platform:

| Platform | File |
|----------|------|
| Windows  | `ss14-redactor-win-x64.zip` → extract → run `ss14-redactor.exe` |
| Linux    | `ss14-redactor-linux-x64.tar.gz` → extract → `./ss14-redactor` |
| macOS (Intel) | `ss14-redactor-osx-x64.tar.gz` |
| macOS (Apple Silicon) | `ss14-redactor-osx-arm64.tar.gz` |

The binary is fully self-contained — no .NET runtime needed.

### Option B — Build from source

```bash
git clone https://github.com/your-org/ss14-redactor
cd ss14-redactor
dotnet run
```

Requires [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).

---

## Using the editor

1. **Build your SS14 project first** — the editor reads prototype metadata from the compiled game DLLs:
   ```
   cd your-ss14-fork
   dotnet build
   ```

2. **Run the redactor** (binary or `dotnet run` from this repo).  
   Your browser opens automatically at `http://localhost:5555`.

3. **Pick a project folder** in the setup screen that appears.  
   Enter the absolute path to your SS14 fork root (e.g. `C:\repos\space-station-14`).  
   The editor validates the path and extracts metadata automatically.

4. **Edit prototypes** — the file tree on the left shows all YAML files under `Resources/Prototypes/`. Click a file to open it, edit fields in the visual form on the right, save with **Ctrl+S**.

The last used project path is remembered — next time you open the editor it skips the setup screen.

---

## CLI usage

```
ss14-redactor                         # start editor, open browser (setup mode)
ss14-redactor serve [path] [port]     # start editor pointing at a specific project
ss14-redactor extract [path]          # extract metadata.json without starting the server
```

---

## How it works

The tool uses `System.Reflection.MetadataLoadContext` to read the compiled game DLLs **without executing any game code**. This makes it safe and fast to load.  
The web UI is embedded inside the binary as resources, so there are no external dependencies at runtime.

---

## Releases and GitHub Actions

The [`.github/workflows/release.yml`](.github/workflows/release.yml) workflow builds and publishes a new release automatically when you push a version tag.

### Creating a release

```bash
git tag v1.0.0
git push origin v1.0.0
```

This triggers a 4-platform matrix build (win-x64, linux-x64, osx-x64, osx-arm64). When all builds pass, a GitHub Release is created automatically with the binaries attached. See [Workflow details](#workflow-details) below.

### Manual build via Actions tab

You can also trigger the `workflow_dispatch` trigger manually:
1. Go to **Actions → Release → Run workflow**
2. Select the branch and click **Run workflow**

Manual triggers build all 4 platforms and upload the artifacts, but do **not** create a GitHub Release (no tag = no release). Use this to test that builds pass before tagging.

---

## Development

For live UI editing without recompiling, run with `dotnet run` — the server checks for a `WebUI/` folder next to the binary first and serves files directly from disk, bypassing embedded resources. Edit JS/CSS and reload the browser.

```
ss14-redactor/
  WebUI/          ← edit JS/CSS/HTML here during development
  *.cs            ← C# source (server, API, metadata extractor)
  ss14-redactor.csproj
```
