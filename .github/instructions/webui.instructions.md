---
description: "Rules for editing the WebUI/ folder (vanilla HTML, CSS, JS embedded as resources)."
applyTo: "WebUI/**"
---

# WebUI Rules

## No build step
- The frontend is **vanilla JS / HTML / CSS**. There is no bundler, no transpiler, no npm, no TypeScript.
- Files are embedded into the binary via `<EmbeddedResource Include="WebUI\**\*">` in [ss14-editor.csproj](../../ss14-editor.csproj). Adding a file there means it ships in the binary automatically — no manual list to update.
- Do not introduce `package.json`, `vite.config`, `tsconfig`, or similar. If the user explicitly asks for it, push back first.

## Network calls
- All `fetch` calls live in [WebUI/js/api.js](../../WebUI/js/api.js). Other modules must call functions exported from there, never `fetch` directly.
- Endpoint paths must match what's registered in [src/ApiRouter.cs](../../src/ApiRouter.cs). When the server route changes, update `api.js` and vice versa.

## Verify before deleting "dead" code
- IDs / classes / function names are often referenced indirectly: CSS selectors, route dispatch tables, dynamic `getElementById`, event-handler attribute strings, JSON payload fields. Grep the entire `WebUI/`, the C# server, and embedded resource paths before removing.

## Dev mode
- When running with `dotnet run`, the server serves files from `WebUI/` on disk first, falling back to embedded resources. Edits are reflected on browser reload — no rebuild needed.

## Style
- Prefer `const`/`let` over `var`. No `class` keyword; existing code uses module-level functions + closures over a shared state object exported from [WebUI/js/state.js](../../WebUI/js/state.js).
- Keep imports relative and explicit. Modules use `<script type="module">` from [WebUI/index.html](../../WebUI/index.html); the entry point is [WebUI/js/init.js](../../WebUI/js/init.js).
