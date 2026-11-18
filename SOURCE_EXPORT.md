# Standalone test source export

Based on mizuharasup/Mizuauto commit `9f3321571cf276ef54d43477fd9323d8af4401a1`. C# WinForms has been rebuilt as a separate Mizuauto application with its own profile, manager mutex, ports, updates policy and installation helper. Rust optimizer is built separately through `mizuauto-engine` with shared engine sources. Known C# API authentication flaws are fixed.

A runnable Windows x64 test package is delivered separately from this source archive. Windows UI/login/gameplay testing remains outstanding. Diagnostics passed under Wine/Mono; the GUI smoke attempt stopped at a Mono GDI+ ToolStrip error. See `upstream-ram-372/STANDALONE.vi.md` and `VERIFICATION.md`.

Original GPL-3.0 license is preserved. No real account data, cookies or personal settings are included. Build caches, target directories and staged binaries are excluded from this source ZIP.

The 3.7.2.3 export renames the executable and visible application to Roblox Manager while retaining the standalone profile and internal identity for compatibility. See VERIFICATION.md for the required native Windows release checks.

The 3.7.2.4 source includes explicit CEF startup/session ordering, the current cookie identity endpoint, sanitized asynchronous cookie import, and native Windows browser regression checks. See VERIFICATION.md.
