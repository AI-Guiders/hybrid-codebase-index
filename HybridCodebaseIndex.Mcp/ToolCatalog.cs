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
                        full_rebuild = new { type = "boolean", description = "Если true — полный rebuild вместо инкремента." },
                    },
                    required = RequiredWorkspace,
                }),
            },
        ];
    }
}
