param(
    [string]$PublishPath = (Join-Path $PSScriptRoot "dist\WindowPilot-win-x64"),
    [string]$ArchivePath = (Join-Path $PSScriptRoot "dist\WindowPilot-win-x64.zip")
)
$ErrorActionPreference = "Stop"
if (!(Test-Path -LiteralPath (Join-Path $PublishPath "WindowPilot.exe"))) { throw "Build with build.ps1 first." }
foreach ($taskName in @("README.md", "THIRD-PARTY-NOTICES.md")) {
    $taskSource = Join-Path $PSScriptRoot $taskName
    if (Test-Path -LiteralPath $taskSource) { Copy-Item -LiteralPath $taskSource -Destination $PublishPath -Force }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "licenses") -Destination $PublishPath -Recurse -Force
$taskArchiveParent = Split-Path -Parent $ArchivePath
if ($taskArchiveParent) { New-Item -ItemType Directory -Path $taskArchiveParent -Force | Out-Null }
Compress-Archive -Path (Join-Path $PublishPath "*") -DestinationPath $ArchivePath -CompressionLevel Optimal -Force
$taskArchiveHash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$taskArchiveHash  $(Split-Path -Leaf $ArchivePath)" | Set-Content -LiteralPath "$ArchivePath.sha256" -Encoding utf8
Write-Host "Created: $ArchivePath"
