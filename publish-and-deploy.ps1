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

$outDir = Join-Path $here "publish"

Push-Location $here
try {
    $publishArgs = @(
        "publish", $csproj,
        "-c", "Release",
        "-r", "win-x64",
        "-o", $outDir,
        "-v", "minimal"
    )

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    if (-not (Test-Path -LiteralPath $Target)) {
        New-Item -ItemType Directory -Path $Target -Force | Out-Null
    }

    # If Cursor is currently running the MCP from $Target, files can be locked (clrjit.dll, etc.).
    # Best-effort: stop the running process before mirroring.
    try {
        Get-Process -Name "HybridCodebaseIndex.Mcp" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 300
    } catch {
        # ignore
    }

    robocopy $outDir $Target /E /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS | Out-Null
    $robocode = $LASTEXITCODE
    if ($robocode -ge 8) {
        Write-Error "robocopy failed with exit code $robocode"
        exit $robocode
    }

    $exe = Join-Path $Target "HybridCodebaseIndex.Mcp.exe"
    if (-not (Test-Path -LiteralPath $exe)) {
        Write-Error "Expected exe not found: $exe"
        exit 1
    }

    $ts = (Get-Item -LiteralPath $exe).LastWriteTimeUtc.ToString("o")
    $exeJson = $exe.Replace('\', '\\')
    Write-Host ""
    Write-Host "OK: $exe  (UTC $ts)"
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

