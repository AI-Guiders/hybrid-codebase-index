using System.Collections.Frozen;
using System.Text.Json;
using HybridCodebaseIndex.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

var tools = ToolCatalog.Build();

var options = new McpServerOptions
{
    ServerInfo = new Implementation { Name = "HybridCodebaseIndex.Mcp", Version = "0.1.0" },
    ProtocolVersion = "2024-11-05",
    ServerInstructions =
        "Ops: call man tool=<name> (or man with no args for TOC). Empty codebase_index_search ≠ auto-reindex — status first, then reindex once or Grep/Glob. man is MCP ops manual, not shell.",
    Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = false } },
    Handlers = new McpServerHandlers
    {
        ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = tools }),

        CallToolHandler = (request, _) =>
        {
            var name = request.Params?.Name ?? "";
            var args = request.Params?.Arguments is IReadOnlyDictionary<string, JsonElement> d
                ? d
                : FrozenDictionary<string, JsonElement>.Empty;

            try
            {
                var text = ToolHandlers.Handle(name, args);
                return ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = text }] });
            }
            catch (ArgumentException ex)
            {
                return ValueTask.FromResult(new CallToolResult
                {
                    Content = [new TextContentBlock { Text = $"Error: {ex.Message}" }],
                    IsError = true,
                });
            }
            catch (Exception ex)
            {
                return ValueTask.FromResult(new CallToolResult
                {
                    Content = [new TextContentBlock { Text = "Error: " + ex.Message }],
                    IsError = true,
                });
            }
        },
    },
};

var transport = new StdioServerTransport("HybridCodebaseIndex.Mcp");
await using var server = McpServer.Create(transport, options);
await server.RunAsync();
return 0;
