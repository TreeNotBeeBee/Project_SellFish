param([string]$Dotnet = 'dotnet', [string]$Artifacts = (Join-Path $env:TEMP ('sell-fish-bridge-' + [guid]::NewGuid())), [string]$Packages = '')
$ErrorActionPreference = 'Stop'
$arguments = @('--artifacts-path', $Artifacts, '-c', 'Release', '--nologo')
if ($Packages) { $arguments += "-p:RestorePackagesPath=$Packages" }
$projects = @((Join-Path $PSScriptRoot '..\..\BrowserBridge\Host\SellFish.BrowserHost.csproj'), (Join-Path $PSScriptRoot 'BridgeTests.csproj'))
foreach ($project in $projects) {
    & $Dotnet build $project @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Browser bridge build failed.' }
}
$folder = (Get-ChildItem -LiteralPath (Join-Path $Artifacts 'bin\BridgeTests') -Recurse -Filter 'sell fish.exe').DirectoryName
$hostFolder = (Get-ChildItem -LiteralPath (Join-Path $Artifacts 'bin\SellFish.BrowserHost') -Recurse -Filter 'SellFish.BrowserHost.exe').DirectoryName
foreach ($suffix in @('exe', 'dll', 'deps.json', 'runtimeconfig.json')) {
    Copy-Item -LiteralPath (Join-Path $hostFolder "SellFish.BrowserHost.$suffix") -Destination $folder -Force
}
$env:DOTNET_ROOT = (Get-Item -LiteralPath (Get-Command $Dotnet).Source).DirectoryName
& (Join-Path $folder 'sell fish.exe')
if ($LASTEXITCODE -ne 0) { throw 'Browser bridge test failed.' }
