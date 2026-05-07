# Hybrid Codebase Index MCP — каталог тулов

<!-- GENERATED:ToolCatalog START -->

> Автогенерация из `ToolCatalog.Build()`. Не править этот блок вручную.
>
> Обновление: из каталога `hybrid-codebase-index` выполнить `dotnet run --project tools/ExportMcpManifest -- --write`.
>
> Тексты совпадают с полем `description` у инструментов MCP; полная схема — в `inputSchema`.

### `codebase_index_version`

Версия MCP сервера: assembly version + informational version (commit), runtime.

### `codebase_index_search`

Гибридный полнотекстовый поиск по индексу workspace (SQLite FTS5, ADR 0105). hit_kind=text_fts. До reindex база может отсутствовать.

### `codebase_index_explain`

Explain для одного попадания (по hit_id из search): вернуть контекст чанка и метаданные.

### `codebase_index_status`

Статус локального индекса: путь к SQLite, число документов, версия формата.

### `codebase_index_reindex`

Полная перестройка индекса (v0): обход расширений, FTS5, без инкрементального watcher.

<!-- GENERATED:ToolCatalog END -->

