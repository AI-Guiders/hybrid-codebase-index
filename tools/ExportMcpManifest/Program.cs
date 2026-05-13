using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExportMcpManifest;
using HybridCodebaseIndex.Mcp;
using McpToolManifest;
using Tool = ModelContextProtocol.Protocol.Tool;

// Regenerates mcp-tools.manifest.json and docs/MCP-TOOLS.md from ToolCatalog.
//   JSON на stdout:  dotnet run --project tools/ExportMcpManifest
//   только MD stdout: dotnet run --project tools/ExportMcpManifest -- --md-only
//   записать оба файла (cwd = корень hybrid-codebase-index): dotnet run --project tools/ExportMcpManifest -- --write

var tools = ToolCatalog.Build();
var cliArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();

if (cliArgs.Contains("--write", StringComparer.Ordinal))
{
    var root = Directory.GetCurrentDirectory();
    var jsonPath = Path.Combine(root, "mcp-tools.manifest.json");
    var mdDir = Path.Combine(root, "docs");
    var mdPath = Path.Combine(mdDir, "MCP-TOOLS.md");
    Directory.CreateDirectory(mdDir);

    WriteJsonFile(jsonPath, tools);
    var md = McpToolsDocMarkdown.Build(tools.Select(t => (t.Name!, t.Description!)));
    File.WriteAllText(mdPath, md, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    Console.Error.WriteLine($"Wrote {jsonPath}");
    Console.Error.WriteLine($"Wrote {mdPath}");
    return;
}

if (cliArgs.Contains("--md-only", StringComparer.Ordinal))
{
    Console.WriteLine(McpToolsDocMarkdown.Build(tools.Select(t => (t.Name!, t.Description!))).TrimEnd());
    return;
}

var doc = new McpToolManifestDocument
{
    SchemaVersion = 1,
    McpId = "hybrid-codebase-index",
    Title = "Hybrid Codebase Index MCP",
    Tools = tools.Select(t => new McpToolManifestTool { Name = t.Name, Description = t.Description }).ToList()
};

Console.WriteLine(JsonSerializer.Serialize(doc, new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
}));

static void WriteJsonFile(string path, List<Tool> tools)
{
    var doc = new McpToolManifestDocument
    {
        SchemaVersion = 1,
        McpId = "hybrid-codebase-index",
        Title = "Hybrid Codebase Index MCP",
        Tools = tools.Select(t => new McpToolManifestTool { Name = t.Name, Description = t.Description }).ToList()
    };

    File.WriteAllText(
        path,
        JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }) + Environment.NewLine,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}

