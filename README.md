# Hybrid Codebase Index (HCI) — MCP

Local hybrid index for “search-before-read” workflows. Формальное решение **ADR 0105** — в репозитории **CascadeIDE**: [0105](https://github.com/KarataevDmitry/cascade-ide/blob/main/docs/adr/0105-hybrid-codebase-index-for-csharp-web.md); краткое обоснование без ADR-серии здесь — **[design-rationale.md](docs/design-rationale.md)**. Keyword-поиск (SQLite FTS5) по docs/config/web/axaml и опционально `*.cs` как текст; **семантическая корректность по C# — в Roslyn** (rename/usages/diagnostics).

**Cursor:** примеры правил для копипаста и ссылки на соседние репо — **[docs/cursor-rules-examples.md](docs/cursor-rules-examples.md)**.

## What you get

- **MCP server**: `HybridCodebaseIndex.Mcp` (stdio)
- **Index core**: NuGet **[AIGuiders.HybridCodebaseIndex.Core](https://www.nuget.org/packages/AIGuiders.HybridCodebaseIndex.Core)** (исходники — [hybrid-codebase-index-core](https://github.com/KarataevDmitry/hybrid-codebase-index-core); SQLite FTS5)
- **Tools**:
  - `codebase_index_version`
  - `codebase_index_status`
  - `codebase_index_reindex` (incremental by default; `full_rebuild=true` for full rebuild)
  - `codebase_index_search` (with optional path/ext filters)
  - `codebase_index_explain`

Generated docs:
- `mcp-tools.manifest.json`
- `docs/MCP-TOOLS.md`

## Install / publish (Windows)

Run from repo root:

```powershell
.\publish-and-deploy.ps1
```

This produces a self-contained exe and mirrors it to:
- `D:\hybrid-codebase-index\HybridCodebaseIndex.Mcp.exe`

### Note: local publish vs releases

- `.\publish-and-deploy.ps1` is for **local Cursor MCP** workflow: publish + mirror to a fixed target path (easy to point `mcp.json` at), and avoids “file locked” issues.
- If you add release automation (zip + upload), keep it separate from local publish (typically under `scripts/`).

Then add to Cursor MCP config (`mcp.json`):

```json
{
  "hybrid-codebase-index": {
    "command": "D:\\hybrid-codebase-index\\HybridCodebaseIndex.Mcp.exe",
    "args": []
  }
}
```

## Workspace settings

Per-workspace override file:
- `<workspace>/.hybrid-codebase-index/settings.toml`

Default settings are embedded into the **Core** assembly (packaged in the dependency):
- в репозитории ядра: `DefaultSettings/settings.default.toml` в [hybrid-codebase-index-core](https://github.com/KarataevDmitry/hybrid-codebase-index-core)

### Supported keys (snake_case)

- `include_cs_in_fts` (bool): include `*.cs` as plain text documents.
- `extra_include_roots` (string[]): extra roots (relative to workspace root) to index.
- `include_extensions` (string[]): extensions to index (with or without dot).
- `exclude_extensions` (string[]): subtract from effective extensions.
- `exclude_path_segments` (string[]): denylist path segments (directory names) applied before ignore rules.
- `ignore_files` (string[]): which gitignore-like files to apply (paths relative to workspace root).
- `max_indexed_file_bytes` (int): files larger than this are skipped as `too_large`.
- `chunk_lines` (int): FTS chunk size (line windows).
- `chunk_overlap_lines` (int): overlap between chunks.
- `binary_probe_bytes` (int): probe size for NUL detection.

## Usage

1) Build (first time) / update index:

```json
{ "workspace_path": "D:\\path\\to\\workspace" }
```

Call `codebase_index_reindex`.

2) Search:

```json
{
  "workspace_path": "D:\\path\\to\\workspace",
  "query": "GroupedTreeFilterBuilder BuildItemWhereClause",
  "top_n": 10,
  "path_prefix": "src/",
  "exclude_path_prefixes": ["bin/", "obj/"],
  "extensions": ["md", "axaml", ".csproj"]
}
```

3) Then open the top hits in IDE, and use Roslyn MCP for exact refactors.

