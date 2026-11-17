# Verification — Mizuauto Standalone 3.7.2.2 test

Base repository commit: `9f3321571cf276ef54d43477fd9323d8af4401a1`.

## Built successfully

- C# frontend: all project Compile entries, Roslyn 4.11 from .NET SDK 8.0.428, .NET Framework 4.7.2 reference assemblies 1.0.3, x64 Windows GUI executable. No compiler errors; inherited assembly-binding, async and unused-field warnings remain.
- Rust engine: Rust 1.100.0, MinGW GCC 14, target x86_64-pc-windows-gnu, separate optimizer-only Cargo package with a committed lockfile. No linker errors; unused helpers in the shared platform module produce warnings.
- C# Windows diagnostics executable: x64, .NET Framework, built from the shipped test sources and the actual AppIdentity/ApiAuthentication helper sources.
- Native dependency package: own CEF x64 109.1.11 / CefSharp 109.1.110, the existing managed dependencies, and Microsoft VC++ x64 app-local runtime. Native engine imports only Windows system DLLs, not GCC DLLs.

UI .resources and managed dependencies were extracted from the repository's original executable; the libsodium resource and distributed native DLL were corrected to the official x64 DLL from libsodium-net 0.10.0; all application C# code was recompiled from the modified source. The native Rust engine was compiled from the modified source. No original executable was patched into the new application.

## Executed checks

The Windows diagnostics executable ran under Wine 10 / Wine Mono 9.4.0 and passed:

- Distinct manager identity, profile and default ports.
- Original-manager folder protection using synthetic marker files only.
- Missing, unowned, held and released mutex lifecycle, plus acquisition of a distinct application mutex.
- API password cases: missing, empty, incorrect, unconfigured, too short and case-mismatched passwords denied; exact valid password accepted.
- Native cryptography and its VC++ runtime load, plus the actual account encryption/decryption code exercised with synthetic data.
- Rust DLL load, ABI version, 32-byte session layout, null guards, invalid-config normalization, bounded snapshot and clean engine shutdown. Auto was never enabled and account data was never read.

PowerShell 7.4.6 on Linux parsed the install helper, Windows build script and package-manifest script without syntax errors. Project XML and all Compile paths were checked. The original Git checkout was unchanged. ZIP and manifest files were checked against their source files and by decompression/CRC.

## Limits

The GUI smoke attempt on Wine/Mono stopped at `System.Drawing.Bitmap.MakeTransparent` / `ToolStripScrollButton` with GDI+ InvalidParameter. It was not counted as successful GUI testing. No Windows-native UI, account sign-in, real Roblox launch, memory/CPU optimization during gameplay or simultaneous use of the actual upstream manager has been verified.

The Inno Setup installer was not compiled. This test release provides a runnable portable ZIP plus `Cai-Mizuauto.cmd` / `Install-Mizuauto.ps1` for per-user installation and a Start Menu shortcut. The copy/shortcut flow has not been executed on a real Windows machine. This is a test build for the user to exercise, not a certified stable release. No real account data or credentials are included.

## Native Windows packaging check

The first GitHub Windows diagnostics run detected that libsodium-net requests `libsodium-64.dll` on Microsoft .NET Framework. Wine had resolved the library under its other name. The package and Windows build script now include both `libsodium.dll` and `libsodium-64.dll`, with identical official x64 bytes. A rerun is required before publication.
