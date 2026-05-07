using System.Text.Json;
using ModelContextProtocol.Protocol;
using Tool = ModelContextProtocol.Protocol.Tool;

namespace HybridCodebaseIndex.Mcp;

internal static class ToolCatalog
{
    private static JsonElement Schema(object schema) => JsonSerializer.SerializeToElement(schema);

    private static readonly string[] RequiredWorkspace = ["workspace_path"];

    internal static List<Tool> Build()
    {
        return
        [
            new Tool
            {
                Name = "codebase_index_version",
                Description = "Версия MCP сервера: assembly version + informational version (commit), runtime.",
                InputSchema = Schema(new
                {
                    type = "object",
                    properties = new { },
                }),
            },
            new Tool
            {
                Name = "codebase_index_search",
                Description =
                    "Гибридный полнотекстовый поиск по индексу workspace (SQLite FTS5, ADR 0105). hit_kind=text_fts. До reindex база может отсутствовать.",
                InputSchema = Schema(new
                {
                    type = "object",
                    properties = new
                    {
                        workspace_path = new { type = "string", description = "Корень workspace (решение/репозиторий)." },
                        solution_path = new { type = "string", description = "Опционально: путь к .sln/.slnx/.csproj (relative to workspace или absolute). Используется для области индекса: одна БД на (workspace_root, solution_path)." },
                        query = new { type = "string", description = "Поисковая строка (ключевые слова, AND по токенам)." },
                        top_n = new { type = "integer", description = "Максимум попаданий (по умолчанию 15)." },
                        path_prefix = new { type = "string", description = "Опционально: ограничить попадания путями, начинающимися с префикса (unix-style, например 'src/')." },
                        exclude_path_prefixes = new { type = "array", items = new { type = "string" }, description = "Опционально: исключить пути по префиксам (unix-style)." },
                        extensions = new { type = "array", items = new { type = "string" }, description = "Опционально: ограничить расширениями (например ['.md','.csproj'] или ['md','csproj'])." },
                    },
                    required = new[] { "workspace_path", "query" },
                }),
            },
            new Tool
            {
                Name = "codebase_index_explain",
                Description = "Explain для одного попадания (по hit_id из search): вернуть контекст чанка и метаданные.",
                InputSchema = Schema(new
                {
                    type = "object",
                    properties = new
                    {
                        workspace_path = new { type = "string", description = "Корень workspace." },
                        solution_path = new { type = "string", description = "Опционально: путь к .sln/.slnx/.csproj (relative to workspace или absolute). Должен совпадать со scope при search, иначе hit_id может не найтись." },
                        hit_id = new { type = "integer", description = "Идентификатор попадания (hitId из search)." },
                    },
                    required = new[] { "workspace_path", "hit_id" },
                }),
            },
            new Tool
            {
                Name = "codebase_index_status",
                Description = "Статус локального индекса: путь к SQLite, число документов, версия формата.",
                InputSchema = Schema(new
                {
                    type = "object",
                    properties = new
                    {
                        workspace_path = new { type = "string", description = "Корень workspace." },
                        solution_path = new { type = "string", description = "Опционально: путь к .sln/.slnx/.csproj (relative to workspace или absolute). Используется для области индекса: одна БД на (workspace_root, solution_path)." },
                    },
                    required = RequiredWorkspace,
                }),
            },
            new Tool
            {
                Name = "codebase_index_reindex",
                Description = "Перестройка индекса (FTS5). По умолчанию инкрементальная по файлам; full_rebuild=true — полная.",
                InputSchema = Schema(new
                {
                    type = "object",
                    properties = new
                    {
                        workspace_path = new { type = "string", description = "Корень workspace." },
                        solution_path = new { type = "string", description = "Опционально: путь к .sln/.slnx/.csproj (relative to workspace или absolute). Используется для области индекса: одна БД на (workspace_root, solution_path)." },
                        full_rebuild = new { type = "boolean", description = "Если true — полный rebuild вместо инкремента." },
                    },
                    required = RequiredWorkspace,
                }),
            },
            new Tool
            {
                Name = "codebase_index_watch",
                Description =
                    "Включить/выключить watcher для авто-инкрементальной индексации (debounced). Важно: это best-effort фоновая синхронизация, не заменяет явный reindex.",
                InputSchema = Schema(new
                {
                    type = "object",
                    properties = new
                    {
                        workspace_path = new { type = "string", description = "Корень workspace." },
                        solution_path = new { type = "string", description = "Опционально: путь к .sln/.slnx/.csproj (relative to workspace или absolute). Определяет область watcher (одна БД на (workspace_root, solution_path))." },
                        enabled = new { type = "boolean", description = "true — включить watcher, false — выключить." },
                        debounce_ms = new { type = "integer", description = "Опционально: debounce в миллисекундах (по умолчанию 750)." },
                    },
                    required = new[] { "workspace_path", "enabled" },
                }),
            },
            new Tool
            {
                Name = "codebase_index_verify",
                Description =
                    "Анти-галлюцинации: проверить список идентификаторов через индекс (FTS) и вернуть exists/missing + подсказки похожих (prefix search).",
                InputSchema = Schema(new
                {
                    type = "object",
                    properties = new
                    {
                        workspace_path = new { type = "string", description = "Корень workspace." },
                        solution_path = new { type = "string", description = "Опционально: путь к .sln/.slnx/.csproj (relative to workspace или absolute). Scope: одна БД на (workspace_root, solution_path)." },
                        identifiers = new { type = "array", items = new { type = "string" }, description = "Список идентификаторов для проверки (методы/типы/свойства; можно с точками/дженериками)." },
                        top_n = new { type = "integer", description = "Максимум попаданий на идентификатор (по умолчанию 5)." },
                        suggestions = new { type = "integer", description = "Максимум подсказок на missing-идентификатор (по умолчанию 8)." },
                        extensions = new { type = "array", items = new { type = "string" }, description = "Опционально: ограничить расширениями (по умолчанию ['.cs'])." },
                        path_prefix = new { type = "string", description = "Опционально: ограничить попадания путями, начинающимися с префикса (unix-style)." },
                        exclude_path_prefixes = new { type = "array", items = new { type = "string" }, description = "Опционально: исключить пути по префиксам (unix-style)." },
                    },
                    required = new[] { "workspace_path", "identifiers" },
                }),
            },
            new Tool
            {
                Name = "codebase_index_draft_doc",
                Description =
                    "Синтез документации (черновик): собрать markdown-скелет с выдержками из изменённых файлов (по индексу).",
                InputSchema = Schema(new
                {
                    type = "object",
                    properties = new
                    {
                        workspace_path = new { type = "string", description = "Корень workspace." },
                        solution_path = new { type = "string", description = "Опционально: путь к .sln/.slnx/.csproj (relative to workspace или absolute). Scope: одна БД на (workspace_root, solution_path)." },
                        title = new { type = "string", description = "Заголовок документа (например 'ADR: ...' или 'Design notes: ...')." },
                        changed_paths = new { type = "array", items = new { type = "string" }, description = "Список путей (relative to workspace) для включения в черновик." },
                    },
                    required = new[] { "workspace_path", "title", "changed_paths" },
                }),
            },
        ];
    }
}
