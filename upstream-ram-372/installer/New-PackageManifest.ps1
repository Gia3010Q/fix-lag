param([Parameter(Mandatory = $true)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
$separator = [IO.Path]::DirectorySeparatorChar
$root = (Resolve-Path -LiteralPath $PackageDirectory).Path.TrimEnd($separator) + $separator
$entries = @()
foreach ($file in Get-ChildItem -LiteralPath $PackageDirectory -File -Recurse | Sort-Object FullName) {
    if ($file.Name -eq 'package-manifest.json') { continue }
    if ($file.Name -in @('AccountData.json', 'AccountData.json.backup', 'RAMSettings.ini', 'RAMTheme.ini', 'state.json', 'startup-error.log', 'diagnostics-result.txt', 'log.txt')) {
        throw "User data or logs must not be packaged: $($file.FullName)"
    }
    $entries += @{ path = $file.FullName.Substring($root.Length); sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
}
@{ appId = '66f908fc-b7d0-45da-9050-c98e1095a3d1'; version = '3.7.2.4-test'; files = $entries } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $PackageDirectory 'package-manifest.json') -Encoding UTF8
