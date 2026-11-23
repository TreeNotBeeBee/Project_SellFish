param(
    [string]$DotnetPath = 'dotnet',
    [string]$ArtifactRoot = (Join-Path ([System.IO.Path]::GetTempPath()) ('WindowPilot-EffectsProbe-' + [guid]::NewGuid().ToString('N'))),
    [string]$NugetPackagePath = ''
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $ArtifactRoot | Out-Null
$probeProject = Join-Path $PSScriptRoot 'EffectsProbe\EffectsProbe.csproj'
$probeArguments = @('build', $probeProject, '-c', 'Release', '--nologo', '--artifacts-path', (Join-Path $ArtifactRoot 'build'))
if ($NugetPackagePath) { $probeArguments += ('-p:RestorePackagesPath=' + $NugetPackagePath) }
& $DotnetPath @probeArguments
if ($LASTEXITCODE -ne 0) { throw 'EffectsProbe build failed.' }
$probeExecutables = @(Get-ChildItem -LiteralPath (Join-Path $ArtifactRoot 'build\bin\EffectsProbe') -Recurse -File -Filter 'EffectsProbe.exe')
if ($probeExecutables.Count -ne 1) { throw 'EffectsProbe executable could not be resolved uniquely.' }
$probeResultPath = Join-Path $ArtifactRoot 'effects-probe.json'
$previousResult = $env:WINDOWPILOT_EFFECTS_RESULT
try {
    $env:WINDOWPILOT_EFFECTS_RESULT = $probeResultPath
    $probeProcess = Start-Process -FilePath $probeExecutables[0].FullName -WindowStyle Hidden -PassThru
    if (-not $probeProcess.WaitForExit(45000)) {
        $probeProcess.Kill()
        throw 'EffectsProbe timed out.'
    }
} finally {
    $env:WINDOWPILOT_EFFECTS_RESULT = $previousResult
}
if (-not (Test-Path -LiteralPath $probeResultPath)) { throw 'EffectsProbe did not produce a result.' }
$probeResult = Get-Content -LiteralPath $probeResultPath -Raw | ConvertFrom-Json
Write-Output ('Result: ' + $probeResultPath)
if (-not $probeResult.success) {
    Get-Content -LiteralPath $probeResultPath -Raw | Write-Output
    throw 'EffectsProbe reported a failure.'
}
Write-Output 'PASS: raw capture, inversion, grayscale+alpha, input hit testing, normal/topmost occlusion, resize, static repaint, minimized/hidden/zero presentation.'
