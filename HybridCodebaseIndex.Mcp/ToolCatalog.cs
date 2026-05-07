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
                Description = "Полная перестройка индекса (v0): обход расширений, FTS5, без инкрементального watcher.",
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
        ];
    }
}
