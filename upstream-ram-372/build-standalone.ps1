# Requires Windows x64, MSBuild/VS Build Tools, .NET Framework 4.7.2 targeting pack,
# NuGet CLI, and the Rust stable x86_64-pc-windows-msvc toolchain.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$stage = Join-Path $root 'artifacts\win-x64'
if (Test-Path $stage) {
    throw "Build output already exists: $stage. Move it aside before building a fresh package."
}
foreach ($name in @('nuget', 'cargo', 'msbuild')) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "$name is required. Run this script from a Visual Studio Developer PowerShell with NuGet and Rust on PATH."
    }
}
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Push-Location $root
try {
    & nuget restore (Join-Path $root 'RBX Alt Manager.sln') -NonInteractive
    if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed.' }

    Push-Location (Join-Path $root 'mizuauto-engine')
    try {
        & cargo build --locked --release --lib --target x86_64-pc-windows-msvc
        if ($LASTEXITCODE -ne 0) { throw 'Rust build failed.' }
    } finally { Pop-Location }

    & msbuild (Join-Path $root 'RBX Alt Manager\RBX Alt Manager.csproj') /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU /p:PlatformTarget=x64 /p:Prefer32Bit=false /p:CefSharpAnyCpuSupport=false /p:MizuSkipLegacyNugetOutput=true "/p:OutputPath=$stage\"
    if ($LASTEXITCODE -ne 0) { throw 'WinForms build failed.' }
    Copy-Item (Join-Path $root 'mizuauto-engine\target\x86_64-pc-windows-msvc\release\mizuauto_engine.dll') $stage
    Copy-Item (Join-Path $root 'RBX Alt Manager\Resources\libsodium.dll') (Join-Path $stage 'libsodium.dll') -Force
    Copy-Item (Join-Path $root 'RBX Alt Manager\Resources\libsodium.dll') (Join-Path $stage 'libsodium-64.dll') -Force
    foreach ($name in @('RobloxManager.exe', 'mizuauto_engine.dll')) {
        if (-not (Test-Path (Join-Path $stage $name))) { throw "Missing build artifact: $name" }
    }
    foreach ($name in @('Roblox Account Manager.exe', 'RBX Alt Manager.exe', 'AccountData.json', 'RAMSettings.ini')) {
        if (Test-Path (Join-Path $stage $name)) { throw "Unexpected upstream executable or user data in build: $name" }
    }
    $msbuildDir = Split-Path (Get-Command msbuild).Source
    $compiler = @((Join-Path $msbuildDir 'Roslyn\csc.exe'), (Join-Path $msbuildDir '..\Roslyn\csc.exe')) |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $compiler) { throw 'Cannot locate the Visual Studio Roslyn C# compiler for diagnostics.' }
    $sodiumManaged = Join-Path $root 'packages\libsodium-net.0.10.0\lib\Net40\Sodium.dll'
    Copy-Item $sodiumManaged (Join-Path $stage 'Sodium.dll') -Force
    & $compiler /nologo /target:exe /platform:x64 "/reference:$sodiumManaged" "/out:$stage\RobloxManager.Diagnostics.exe" (Join-Path $root 'tests\Diagnostics.cs') (Join-Path $root 'tests\IsolationChecks.cs') (Join-Path $root 'RBX Alt Manager\Classes\AppIdentity.cs') (Join-Path $root 'RBX Alt Manager\Classes\ApiAuthentication.cs') (Join-Path $root 'RBX Alt Manager\Classes\Cryptography.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Diagnostics build failed.' }
    $crtRoot = $env:VCToolsRedistDir
    if (-not $crtRoot) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path $vswhere) {
            $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
            $version = Get-ChildItem (Join-Path $vs 'VC\Redist\MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
            if ($version) { $crtRoot = $version.FullName }
        }
    }
    if (-not $crtRoot) { throw 'The Visual C++ x64 redistributable directory is required for a standalone package.' }
    Get-ChildItem (Join-Path $crtRoot 'x64\Microsoft.VC*.CRT\*.dll') | Copy-Item -Destination $stage -Force
    foreach ($name in @('vcruntime140.dll', 'msvcp140.dll', 'vcruntime140_1.dll')) {
        if (-not (Test-Path (Join-Path $stage $name))) { throw "Missing standalone VC++ runtime: $name" }
    }
    foreach ($name in @('Install-RobloxManager.ps1', 'Cai-Roblox-Manager.cmd', 'Kiem-tra-Roblox-Manager.cmd')) {
        Copy-Item (Join-Path $root "installer\$name") $stage
    }
    Copy-Item (Join-Path $root 'STANDALONE.vi.md') (Join-Path $stage 'HUONG-DAN.md')
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE.txt')
    & (Join-Path $root 'installer\New-PackageManifest.ps1') -PackageDirectory $stage
    Write-Host "Build ready at $stage. No application was launched."
    Write-Host "Compile installer\roblox-manager.iss with Inno Setup 6.3 or newer after Windows smoke testing."
} finally { Pop-Location }
