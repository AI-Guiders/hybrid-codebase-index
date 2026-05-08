# Publish Release (win-x64, self-contained) and mirror to a fixed path for Cursor MCP.
# Run from repo:  cd ...\hybrid-codebase-index  ;  .\publish-and-deploy.ps1
# Optional: -Target "D:\hybrid-codebase-index"
[CmdletBinding()]
param(
    [string] $Target = "D:\hybrid-codebase-index"
)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$csproj = Join-Path $here "HybridCodebaseIndex.Mcp\HybridCodebaseIndex.Mcp.csproj"
if (-not (Test-Path -LiteralPath $csproj)) {
    Write-Error "HybridCodebaseIndex.Mcp.csproj not found. Run this script from the hybrid-codebase-index directory (PSScriptRoot=$here)."
    exit 1
}

Push-Location $here
try {
    # Keep docs/manifests in sync with ToolCatalog.
    & dotnet run --project (Join-Path $here "tools\\ExportMcpManifest\\ExportMcpManifest.csproj") -- --write | Out-Null
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $exe = Join-Path $Target "HybridCodebaseIndex.Mcp.exe"
    $exeJson = $exe.Replace('\', '\\')

    # Prefer local tool (repo-pinned), but global install works too.
    if (Test-Path -LiteralPath (Join-Path $here ".config\\dotnet-tools.json")) {
        & dotnet aid-publish -Project $csproj -Target $Target -Runtime "win-x64" -Configuration "Release" -SelfContained -KillRunning
    } else {
        & aid-publish -Project $csproj -Target $Target -Runtime "win-x64" -Configuration "Release" -SelfContained -KillRunning
    }
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Host ""
    Write-Host "Cursor MCP: paste into mcp.json ->"
    Write-Host @"
  "hybrid-codebase-index": {
    "command": "$exeJson",
    "args": []
  }
"@
    Write-Host ""
} finally {
    Pop-Location
}

