using McpToolManifest;
using HybridCodebaseIndex.Mcp;

var tools = ToolCatalog.Build().Select(t => (t.Name!, (string?)t.Description)).ToList();
return McpToolManifestExporter.Run(
    args,
    tools,
    new McpToolManifestExportOptions
    {
        McpId = "hybrid-codebase-index",
        Title = "Hybrid Codebase Index MCP",
        RepoFolderName = "hybrid-codebase-index",
    });
