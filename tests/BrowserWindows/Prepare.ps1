param(
    [Parameter(Mandatory)][string]$Release,
    [Parameter(Mandatory)][string]$Scratch,
    [string]$Dotnet = 'dotnet',
    [string]$Packages = ''
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$releaseDir = (Resolve-Path -LiteralPath $Release).Path
$project = Join-Path $Scratch 'source'
$build = Join-Path $Scratch 'build'
$run = Join-Path $Scratch 'app'
if (Test-Path -LiteralPath $run) { throw "Choose a fresh scratch directory: $Scratch" }
New-Item -ItemType Directory -Path $project -Force | Out-Null
Get-ChildItem -LiteralPath $repo -File | Where-Object Extension -in '.cs','.xaml','.csproj','.manifest','.json' |
    Copy-Item -Destination $project
foreach ($folder in @('Assets','BrowserBridge','Detached')) {
    Copy-Item -LiteralPath (Join-Path $repo $folder) -Destination $project -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ProbeStartup.cs') -Destination (Join-Path $project 'App.xaml.cs') -Force
# Only a new managed entry point is needed. Reuse the packaged runtime below.
$arguments = @('build', (Join-Path $project 'WindowPilot.csproj'), '-c','Release','--artifacts-path',$build,
    '-p:Platform=x64','-p:SelfContained=false','-p:WindowsAppSDKSelfContained=true','--nologo')
if ($Packages) { $arguments += "-p:RestorePackagesPath=$Packages" }
& $Dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'GUI fixture failed to compile.' }
$appDll = @(Get-ChildItem -LiteralPath (Join-Path $build 'bin\WindowPilot') -Filter 'sell fish.dll' -File -Recurse)
if ($appDll.Count -ne 1) { throw 'Unexpected probe assembly output.' }
[IO.Directory]::CreateDirectory($run) | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $releaseDir -Recurse -File) {
    $relative = $file.FullName.Substring($releaseDir.Length + 1)
    if ($relative -in @('sell fish.dll', 'sell fish.pdb')) { continue }
    $dest = Join-Path $run $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($dest)) | Out-Null
    # The fixture reads the release runtime files. It writes only its own new entry DLL.
    try { New-Item -ItemType HardLink -Path $dest -Target $file.FullName -ErrorAction Stop | Out-Null }
    catch { Copy-Item -LiteralPath $file.FullName -Destination $dest }
}
Copy-Item -LiteralPath $appDll[0].FullName -Destination (Join-Path $run 'sell fish.dll')
Write-Output "Test shell: $(Join-Path $run 'sell fish.exe')"
