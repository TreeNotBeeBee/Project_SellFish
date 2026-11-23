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
} finally { Pop-Location }
Write-Host "Ready: $(Join-Path $OutputPath 'sell fish.exe')"
