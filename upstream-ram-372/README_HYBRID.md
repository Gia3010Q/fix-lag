# Bản nguồn tách riêng Mizuauto

Bản sửa WinForms hiện tại dùng hồ sơ riêng và có thể phối hợp Multi Roblox với RAM gốc. Xem hướng dẫn cài/build và giới hạn kiểm chứng tại [STANDALONE.vi.md](STANDALONE.vi.md). Các hướng dẫn bên dưới về đặt EXE cạnh RAM và dùng dữ liệu chung thuộc bản cũ, không áp dụng cho bản tách riêng.

---

# Mizuauto: WinForms UI + Rust optimizer

The main account manager and optimizer windows use the existing C# WinForms layouts unchanged. `build/Release/mizuauto.exe` is the x64 WinForms frontend; `build/Release/mizuauto_engine.dll` is the Rust optimizer backend. Keep the DLL beside the exe. The existing CefSharp files and `locales` directory in `build/Release` are still needed by the account browser.

The Rust backend scans Roblox processes, reports RAM/CPU/time, trims the working set, applies the optional 100–3000 MB hard working-set maximum, and applies/restores CPU affinity and priority. The account manager's account storage, browser, server tools, and Roblox launch logic still run in C#; they have not yet been switched to Rust. If the DLL cannot load, the optimizer displays a fallback notice and uses its original C# logic. In that fallback, the RAM limit menu is disabled.

The optimizer window shows a **RAM** dropdown below the cycle/CPU controls. Choose **Thu hồi tối đa** or a working-set maximum in 100 MB steps. Closing the optimizer window hides it while Auto keeps running; the Rust backend is disposed when the main app exits. `state.json` keeps the selected value under `ram_limit_mb`.

## Build without launching the app

Build the Rust library from `mizuauto-rust`:

```powershell
cargo build --release --lib --target x86_64-pc-windows-msvc
```

Build the C# project as x64. A .NET Framework 4.7.2 targeting pack is required. On this machine, reference assemblies were extracted from the official `Microsoft.NETFramework.ReferenceAssemblies.net472` NuGet package and supplied using `FrameworkPathOverride`:

```powershell
$refs = Join-Path $env:LOCALAPPDATA 'MizuautoBuildTools\net472-ref\package\build\.NETFramework\v4.7.2'
& 'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe' 'RBX Alt Manager\RBX Alt Manager.csproj' /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU /p:PlatformTarget=x64 /p:Prefer32Bit=false /p:MizuSkipLegacyNugetOutput=true '/p:PostBuildEvent=' "/p:FrameworkPathOverride=$refs"
```

Then copy `mizuauto-rust/target/x86_64-pc-windows-msvc/release/mizuauto_engine.dll` beside `build/Release/mizuauto.exe`.

When deploying the frontend to a different folder, copy `mizuauto_engine.dll` into that same folder as well. The optimizer initializes the Rust backend when its window is first created, so restart the main application after installing a missing DLL. A missing or incompatible DLL is reported in the optimizer status line with a specific reason. Keep user account data in the installed folder; do not replace it with build artifacts.

Do not run either exe as part of the build. No real `AccountData.json` was read or copied during this work.
