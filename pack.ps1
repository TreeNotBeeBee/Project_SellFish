param(
    [string]$PublishPath = (Join-Path $PSScriptRoot "dist\WindowPilot-win-x64"),
    [string]$ArchivePath = (Join-Path $PSScriptRoot "dist\WindowPilot-win-x64.zip"),
    [string]$ExtensionArchivePath
)
$ErrorActionPreference = "Stop"
foreach ($required in @('sell fish.exe', 'SellFish.BrowserHost.exe', 'SellFish.BrowserHost.dll',
    'SellFish.BrowserHost.runtimeconfig.json', 'SellFish.BrowserHost.deps.json',
    'Install-BrowserBridge.ps1', 'Uninstall-BrowserBridge.ps1',
    'browser-extension\manifest.json', 'browser-extension\worker.js', 'browser-extension\page.js')) {
    if (!(Test-Path -LiteralPath (Join-Path $PublishPath $required) -PathType Leaf)) {
        throw "Missing distribution file: $required. Build with build.ps1 first."
    }
}
foreach ($taskName in @("README.md", "THIRD-PARTY-NOTICES.md")) {
    $taskSource = Join-Path $PSScriptRoot $taskName
    if (Test-Path -LiteralPath $taskSource) { Copy-Item -LiteralPath $taskSource -Destination $PublishPath -Force }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "licenses") -Destination $PublishPath -Recurse -Force
function Write-ZipWithChecksum([string]$Directory, [string]$Zip) {
    $parent = Split-Path -Parent $Zip
    if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Compress-Archive -Path (Join-Path $Directory "*") -DestinationPath $Zip -CompressionLevel Optimal -Force
    $hash = (Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path -Leaf $Zip)" | Set-Content -LiteralPath "$Zip.sha256" -Encoding utf8
    Write-Host "Created: $Zip"
}
if ($ExtensionArchivePath -and [IO.Path]::GetFullPath($ExtensionArchivePath) -eq [IO.Path]::GetFullPath($ArchivePath)) {
    throw "The extension and Windows package require different output paths."
}
Write-ZipWithChecksum $PublishPath $ArchivePath
if ($ExtensionArchivePath) { Write-ZipWithChecksum (Join-Path $PublishPath 'browser-extension') $ExtensionArchivePath }
