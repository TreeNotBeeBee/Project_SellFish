param([string]$ArchivePath = (Join-Path $PSScriptRoot "dist\WindowPilot-source.zip"))
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$taskSourceFiles = Get-ChildItem -LiteralPath $taskRoot -Recurse -File -Force | Where-Object {
    $taskRelative = $_.FullName.Substring($taskRoot.Length + 1).Replace('\', '/')
    $taskRelative -notmatch '(^|/)(bin|obj|artifacts|dist|\.git|\.vs|__pycache__)/' -and
    $taskRelative -notmatch '^tests/.*\.json$' -and
    $taskRelative -notmatch '\.(zip|sha256|user|suo)$' -and
    $_.FullName -ne [IO.Path]::GetFullPath($ArchivePath)
}
$taskArchiveParent = Split-Path -Parent $ArchivePath
if ($taskArchiveParent) { New-Item -ItemType Directory -Path $taskArchiveParent -Force | Out-Null }
$taskArchiveStream = [IO.File]::Open($ArchivePath, [IO.FileMode]::Create)
$taskZip = [IO.Compression.ZipArchive]::new($taskArchiveStream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($taskFile in $taskSourceFiles) {
        $taskEntry = 'WindowPilot/' + $taskFile.FullName.Substring($taskRoot.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskZip, $taskFile.FullName, $taskEntry, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally {
    $taskZip.Dispose()
    $taskArchiveStream.Dispose()
}
$taskArchiveHash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$taskArchiveHash  $(Split-Path -Leaf $ArchivePath)" | Set-Content -LiteralPath "$ArchivePath.sha256" -Encoding utf8
Write-Host "Created: $ArchivePath"
