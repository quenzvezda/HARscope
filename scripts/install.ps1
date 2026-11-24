<#
.SYNOPSIS
  Build, test and install HARscope to a fixed folder used by MCP clients.

.DESCRIPTION
  1. Runs the test suite (skip with -SkipTests).
  2. Publishes to a staging folder.
  3. Stops running HarScope.Mcp processes started from the destination (they hold the files open;
     MCP clients such as Claude Code start them again on the next tool call or session).
  4. Copies the staged files and verifies them.
  5. Optionally registers the server with Claude Code (-Register -HarRoot <folder>).

.EXAMPLE
  .\scripts\install.ps1
.EXAMPLE
  .\scripts\install.ps1 -Register -HarRoot D:\network-logs
#>
[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $env:USERPROFILE ".mcp-servers\harscope"),
    [switch]$SkipTests,
    [switch]$Register,
    [string]$HarRoot
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo "src\HarScope.Mcp\HarScope.Mcp.csproj"
$stage = Join-Path ([System.IO.Path]::GetTempPath()) ("harscope-stage-" + [guid]::NewGuid().ToString("N"))

if (-not $SkipTests) {
    Write-Host "Running tests..."
    dotnet test (Join-Path $repo "HarScope.slnx") --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Tests failed; nothing was installed." }
}

Write-Host "Publishing to staging folder..."
dotnet publish $project -c Release -o $stage --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

New-Item -ItemType Directory -Force $Destination | Out-Null
$destFull = (Resolve-Path $Destination).Path

$copied = $false
for ($i = 1; $i -le 10 -and -not $copied; $i++) {
    $running = Get-Process HarScope.Mcp -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($destFull, [StringComparison]::OrdinalIgnoreCase) }
    if ($running) {
        Write-Host ("Stopping {0} running HARscope process(es): {1}" -f @($running).Count, (($running | ForEach-Object Id) -join ", "))
        $running | Stop-Process -Force -Confirm:$false -ErrorAction SilentlyContinue
    }
    try {
        Copy-Item (Join-Path $stage "*") $destFull -Recurse -Force -ErrorAction Stop
        $copied = $true
    }
    catch {
        Start-Sleep -Milliseconds 300
    }
}
if (-not $copied) { throw "Could not copy files to $destFull (still locked). Close the sessions using HARscope and retry." }

$a = (Get-FileHash (Join-Path $stage "HarScope.Mcp.dll")).Hash
$b = (Get-FileHash (Join-Path $destFull "HarScope.Mcp.dll")).Hash
Remove-Item $stage -Recurse -Force -Confirm:$false
if ($a -ne $b) { throw "Verification failed: installed HarScope.Mcp.dll differs from the build." }

$exe = Join-Path $destFull "HarScope.Mcp.exe"
Write-Host "Installed: $exe"

if ($Register) {
    if (-not $HarRoot) { throw "-Register needs -HarRoot <folder with .har files>." }
    New-Item -ItemType Directory -Force $HarRoot | Out-Null
    claude mcp remove harscope -s user 2>$null | Out-Null
    claude mcp add harscope --scope user --env "HAR_ROOT=$HarRoot" -- $exe
}

if (Get-Command claude -ErrorAction SilentlyContinue) {
    claude mcp get harscope
}
Write-Host "Open a new Claude session to pick up changed tool definitions."
