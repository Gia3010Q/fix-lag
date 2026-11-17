param([switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
$appId = '66f908fc-b7d0-45da-9050-c98e1095a3d1'
$target = Join-Path $env:LOCALAPPDATA 'Programs\RobloxManager'
$mutex = [System.Threading.Mutex]::new($false, 'Local\Mizuauto.Manager.66f908fc-b7d0-45da-9050-c98e1095a3d1')
$locked = $false
try {
    try { $locked = $mutex.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { throw 'Close Roblox Manager or its previous Mizuauto version before updating.' }
    foreach ($name in @('Roblox Account Manager.exe', 'RBX Alt Manager.exe')) {
        if (Test-Path -LiteralPath (Join-Path $target $name)) { throw 'The target contains Roblox Account Manager. Installation stopped.' }
    }
    if (Test-Path -LiteralPath (Join-Path $target 'RobloxManager.exe')) {
        $installed = Get-Content -LiteralPath (Join-Path $target 'package-manifest.json') -Raw | ConvertFrom-Json
        if ($installed.appId -ne $appId) { throw 'The target belongs to another application.' }
    }
    $manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'package-manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.appId -ne $appId) { throw 'Wrong package identity.' }
    $separator = [IO.Path]::DirectorySeparatorChar
    $sourceRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd($separator) + $separator
    $targetRoot = [IO.Path]::GetFullPath($target).TrimEnd($separator) + $separator
    foreach ($entry in $manifest.files) {
        $source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $entry.path))
        $destination = [IO.Path]::GetFullPath((Join-Path $target $entry.path))
        if (-not $source.StartsWith($sourceRoot, [StringComparison]::OrdinalIgnoreCase) -or
            -not $destination.StartsWith($targetRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid package path.' }
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Package file is damaged: $($entry.path)" }
    }
    [IO.Directory]::CreateDirectory($target) | Out-Null
    if (-not [string]::Equals($sourceRoot, $targetRoot, [StringComparison]::OrdinalIgnoreCase)) {
        foreach ($entry in $manifest.files) {
            $destination = Join-Path $target $entry.path
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot $entry.path) -Destination $destination -Force
        }
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'package-manifest.json') -Destination $target -Force
    }
    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
    [IO.Directory]::CreateDirectory($startMenu) | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut((Join-Path $startMenu 'Roblox Manager.lnk'))
    $shortcut.TargetPath = Join-Path $target 'RobloxManager.exe'
    $shortcut.WorkingDirectory = $target
    $shortcut.IconLocation = (Join-Path $target 'RobloxManager.exe') + ',0'
    $shortcut.Save()
    # Migrate only a known installation of this same app, never another manager.
    $legacyRoot = Join-Path $env:LOCALAPPDATA 'Programs\Mizuauto'
    $legacyManifestPath = Join-Path $legacyRoot 'package-manifest.json'
    if (Test-Path -LiteralPath $legacyManifestPath) {
        $legacy = Get-Content -LiteralPath $legacyManifestPath -Raw | ConvertFrom-Json
        if ($legacy.appId -eq $appId) {
            $legacyExe = Join-Path $legacyRoot 'mizuauto.exe'
            $oldLinkPath = Join-Path $startMenu 'Mizuauto.lnk'
            if (Test-Path -LiteralPath $oldLinkPath) {
                $oldLink = $shell.CreateShortcut($oldLinkPath)
                if ([string]::Equals($oldLink.TargetPath, $legacyExe, [StringComparison]::OrdinalIgnoreCase)) {
                    Remove-Item -LiteralPath $oldLinkPath
                }
            }
            $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
            $oldRun = Get-ItemPropertyValue -LiteralPath $runKey -Name 'Mizuauto' -ErrorAction SilentlyContinue
            if ($oldRun -and [string]::Equals($oldRun.Trim('"'), $legacyExe, [StringComparison]::OrdinalIgnoreCase)) {
                Set-ItemProperty -LiteralPath $runKey -Name 'Roblox Manager' -Value ('"' + (Join-Path $target 'RobloxManager.exe') + '"')
                Remove-ItemProperty -LiteralPath $runKey -Name 'Mizuauto'
            }
        }
    }
    Write-Host "Installed Roblox Manager: $target"
    Write-Host 'Existing accounts remain in the same separate profile.'
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
if (-not $NoLaunch) { Start-Process -FilePath (Join-Path $target 'RobloxManager.exe') }
