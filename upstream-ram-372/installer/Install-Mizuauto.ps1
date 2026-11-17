param([switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'Programs\Mizuauto'
$mutex = [System.Threading.Mutex]::new($false, 'Local\Mizuauto.Manager.66f908fc-b7d0-45da-9050-c98e1095a3d1')
$locked = $false
try {
    try { $locked = $mutex.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { throw 'Close Mizuauto before installing or updating it.' }
    foreach ($name in @('Roblox Account Manager.exe', 'RBX Alt Manager.exe')) {
        if (Test-Path -LiteralPath (Join-Path $target $name)) { throw 'The target contains Roblox Account Manager. Installation stopped.' }
    }
    $manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'package-manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.appId -ne '66f908fc-b7d0-45da-9050-c98e1095a3d1') { throw 'Wrong package identity.' }
    $sourceRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
    $targetRoot = [IO.Path]::GetFullPath($target).TrimEnd('\') + '\'
    # Verify the whole package before copying anything into the installed application.
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
    $shortcut = $shell.CreateShortcut((Join-Path $startMenu 'Mizuauto.lnk'))
    $shortcut.TargetPath = Join-Path $target 'mizuauto.exe'
    $shortcut.WorkingDirectory = $target
    $shortcut.IconLocation = (Join-Path $target 'mizuauto.exe') + ',0'
    $shortcut.Save()
    Write-Host "Installed Mizuauto: $target"
    Write-Host 'Roblox Account Manager and its account data were not changed.'
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
if (-not $NoLaunch) { Start-Process -FilePath (Join-Path $target 'mizuauto.exe') }
