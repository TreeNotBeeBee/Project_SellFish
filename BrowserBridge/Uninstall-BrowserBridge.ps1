param([string]$InstallDirectory = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $InstallDirectory).Path
$sha = [Security.Cryptography.SHA256]::Create()
try { $dirHash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($root.ToUpperInvariant())) }
finally { $sha.Dispose() }
$dirKey = [BitConverter]::ToString($dirHash, 0, 12).Replace('-', '')
$hostName = 'com.sellfish.bridge'
$manifestPath = Join-Path $env:LOCALAPPDATA "WindowPilot\BrowserBridge\$dirKey\$hostName.json"
foreach ($browser in @('Google\Chrome', 'Microsoft\Edge')) {
    $path = "Software\$browser\NativeMessagingHosts\$hostName"
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($path, $true)
    if ($null -eq $key) { continue }
    try {
        # An older portable copy must not unregister a more recent installation.
        if ($key.GetValue('') -eq $manifestPath) {
            $key.DeleteValue('')
            if ($key.ValueCount -eq 0 -and $key.SubKeyCount -eq 0) {
                [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey($path, $false)
            }
        }
    } finally { $key.Dispose() }
}
if (Test-Path -LiteralPath $manifestPath -PathType Leaf) { Remove-Item -LiteralPath $manifestPath }
Write-Output 'Unregistered this sell fish installation. The browser extension can be removed from the browser extensions page.'
