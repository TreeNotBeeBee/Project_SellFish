param(
    [string]$PublishPath = (Join-Path $PSScriptRoot "dist\WindowPilot-win-x64"),
    [string]$ArchivePath = (Join-Path $PSScriptRoot "dist\WindowPilot-win-x64.zip")
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = (Resolve-Path -LiteralPath $PublishPath).Path
$manifest = 'sell-fish-files.txt'
$files = @(Get-Content -LiteralPath (Join-Path $root $manifest) -Encoding UTF8) + $manifest
foreach ($required in @('sell fish.exe', 'sell fish.dll', 'Interop.UIAutomationClient.dll', 'README.md', 'THIRD-PARTY-NOTICES.md')) {
    if ($required -notin $files) { throw "Missing distribution file: $required. Build with build.ps1 first." }
}
$archive = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ArchivePath)
$parent = Split-Path -Parent $archive
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$temporary = Join-Path $parent ([IO.Path]::GetRandomFileName())
try {
    $zip = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($relative in ($files | Sort-Object -Unique)) {
            $entry = $relative.Replace('\', '/')
            if ([IO.Path]::IsPathRooted($relative) -or $entry -match '(^|/)\.\.(/|$)') {
                throw "Invalid distribution path: $relative"
            }
            $file = Join-Path $root $relative
            if ([IO.Path]::GetFullPath($file) -eq $archive) { throw 'Choose an archive path outside the published files.' }
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file, $entry, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zip.Dispose() }
    Move-Item -LiteralPath $temporary -Destination $archive -Force
} finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary }
}
$sha = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$sha  $(Split-Path -Leaf $archive)" | Set-Content -LiteralPath "$archive.sha256" -Encoding utf8
Write-Host "Created: $archive"
