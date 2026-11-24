param(
    [string]$DotnetPath = "dotnet",
    [string]$ArtifactsPath = (Join-Path $PSScriptRoot "artifacts"),
    [string]$OutputPath = (Join-Path $PSScriptRoot "dist\WindowPilot-win-x64"),
    [string]$PackagesPath = ""
)
$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
if (Test-Path -LiteralPath $DotnetPath -PathType Leaf) { $DotnetPath = (Resolve-Path -LiteralPath $DotnetPath).Path }
$ArtifactsPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ArtifactsPath)
$OutputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
if ($PackagesPath) { $PackagesPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PackagesPath) }
$taskPublishArgs = @("publish", (Join-Path $PSScriptRoot "WindowPilot.csproj"), "-c", "Release", "-r", "win-x64", "--self-contained", "true", "--artifacts-path", $ArtifactsPath, "-o", $OutputPath, "-p:Platform=x64", "--disable-build-servers")
if ($PackagesPath) { $taskPublishArgs += @("-p:RestorePackagesPath=$PackagesPath") }
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot "packages.lock.json")) { $taskPublishArgs += "-p:RestoreLockedMode=true" }
Push-Location -LiteralPath $PSScriptRoot
try {
    & $DotnetPath @taskPublishArgs
    if ($LASTEXITCODE -ne 0) { throw "Publish failed ($LASTEXITCODE)." }
    $bridgeDirectory = Join-Path $ArtifactsPath 'browser-host-publish'
    $bridgeProject = Join-Path $PSScriptRoot 'BrowserBridge\Host\SellFish.BrowserHost.csproj'
    $bridgeArgs = @('publish', $bridgeProject, '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '--artifacts-path', $ArtifactsPath, '-o', $bridgeDirectory)
    if ($PackagesPath) { $bridgeArgs += "-p:RestorePackagesPath=$PackagesPath" }
    & $DotnetPath @bridgeArgs
    if ($LASTEXITCODE -ne 0) { throw "Browser host publish failed ($LASTEXITCODE)." }
    foreach ($suffix in @('.exe','.dll','.deps.json','.runtimeconfig.json')) {
        Copy-Item -LiteralPath (Join-Path $bridgeDirectory ('SellFish.BrowserHost' + $suffix)) -Destination $OutputPath -Force
    }
    foreach ($scriptName in @('Install-BrowserBridge.ps1','Uninstall-BrowserBridge.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "BrowserBridge\$scriptName") -Destination $OutputPath -Force
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'browser-extension') -Destination $OutputPath -Recurse -Force
} finally { Pop-Location }
Write-Host "Ready: $(Join-Path $OutputPath 'sell fish.exe')"
