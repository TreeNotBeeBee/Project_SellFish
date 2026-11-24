param([string]$InstallDirectory = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $InstallDirectory).Path
$hostName = 'com.sellfish.bridge'
$hostFile = Join-Path $root 'SellFish.BrowserHost.exe'
foreach ($file in @($hostFile, (Join-Path $root 'sell fish.exe'), (Join-Path $root 'browser-extension\manifest.json'))) {
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing file in the extracted release: $file" }
}
$manifest = Get-Content -Raw -LiteralPath (Join-Path $root 'browser-extension\manifest.json') -Encoding UTF8 | ConvertFrom-Json
$sha = [Security.Cryptography.SHA256]::Create()
try {
    $keyHash = $sha.ComputeHash([Convert]::FromBase64String($manifest.key))
    $dirHash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($root.ToUpperInvariant()))
} finally { $sha.Dispose() }
$extensionId = -join $(foreach ($n in $keyHash[0..15]) {
    [char](97 + ($n -shr 4)); [char](97 + ($n -band 15))
})
if ($extensionId -ne 'kahblpkceponikaklinoblfmgdnahdfc') { throw 'Extension public key does not match this host.' }
$dirKey = [BitConverter]::ToString($dirHash, 0, 12).Replace('-', '')
$config = Join-Path $env:LOCALAPPDATA "WindowPilot\BrowserBridge\$dirKey"
[IO.Directory]::CreateDirectory($config) | Out-Null
$manifestPath = Join-Path $config "$hostName.json"
$body = [ordered]@{
    name = $hostName
    description = 'sell fish browser bridge'
    path = $hostFile
    type = 'stdio'
    allowed_origins = @("chrome-extension://$extensionId/")
} | ConvertTo-Json
[IO.File]::WriteAllText($manifestPath, $body, [Text.UTF8Encoding]::new($false))
foreach ($browser in @('Google\Chrome', 'Microsoft\Edge')) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("Software\$browser\NativeMessagingHosts\$hostName")
    try { $key.SetValue('', $manifestPath, [Microsoft.Win32.RegistryValueKind]::String) }
    finally { $key.Dispose() }
}
Write-Output "Registered sell fish for the current Windows user. Load the unpacked extension from: $(Join-Path $root 'browser-extension')"
