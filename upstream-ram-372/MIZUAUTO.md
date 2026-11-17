# Bản nguồn tách riêng Mizuauto

Bản sửa WinForms hiện tại dùng hồ sơ riêng và có thể phối hợp Multi Roblox với RAM gốc. Xem hướng dẫn cài/build và giới hạn kiểm chứng tại [STANDALONE.vi.md](STANDALONE.vi.md). Các hướng dẫn bên dưới về đặt EXE cạnh RAM và dùng dữ liệu chung thuộc bản cũ, không áp dụng cho bản tách riêng.

---

# Mizuauto full manager

This branch starts from [Roblox Account Manager 3.7.2](https://github.com/ic3w0lf22/Roblox-Account-Manager/tree/3.7.2) at commit `79f61f3351df61fb3774dfa854ab868954da5389`. The original source and its GPL-3.0 license are preserved in this repository. The RAM account, multi-instance, server, browser, grouping, and utility features remain in the original WinForms application.

Mizuauto adds a RAM/CPU optimizer window, visible Roblox session tracking, automatic working-set trim above 384 MB, a configurable 0.1-3600 second interval, and optional per-process CPU limits of one or two logical processors. CPU affinity and priority are restored when the optimizer window closes. The new executable is named `mizuauto.exe` and requests administrator rights at startup. The built-in upstream auto-update is disabled for Mizuauto so it cannot replace this build with the original RAM executable.

The Mizuauto logo is based on the user's `mizulogo.png`. The top-right button beside Settings opens the optimizer. The former Donate icon is removed from that position. The manager theme uses a plum and pink palette and adds a glass-like highlight treatment while keeping rectangular buttons. Edit Theme now supports a custom background image, Fill/Fit/Stretch/Center/Tile placement, 25-300% image scale, and 0-90% dark overlay. Selected still images are copied to `%LOCALAPPDATA%\Mizuauto`; the original image remains untouched. Mizuauto theme settings are stored in the `[mizuauto]` section of `RAMTheme.ini`, leaving the original RAM theme section intact.

Edit Theme also accepts animated GIF and local video files (`.mp4`, `.wmv`, `.avi`, `.m4v`, `.mov`). GIFs animate in the account manager. Videos use Windows' WPF media player, play silently in a loop, and pause while the manager is minimized. Animated media is read from its original path to avoid copying large files; moving or deleting that file removes the background. Video playback depends on the codecs installed in Windows. Tile mode behaves as Fill for video. Other windows and the theme preview use a static backdrop to avoid running several decoders at once.

Multi Roblox mutex ownership is checked explicitly and retried every two seconds when unavailable. Launching several accounts takes a stable snapshot of the selection. A failed account is reported at the end of the batch while the remaining accounts continue. The legacy direct-launch branch now actually starts Roblox, and the protocol branch no longer waits up to 15 seconds for each launcher process. Roblox itself may still reject a launch due to an expired account, protocol registration, or a changed client behavior; the account manager reports launch errors rather than silently skipping them.

Place `mizuauto.exe` and `mizuauto.exe.config` beside the existing `Roblox Account Manager.exe` and its data files. Close the original RAM and older Mizuauto processes before opening it; the original RAM singleton and Mizuauto trim guard prevent simultaneous copies. Account data and settings are read from that directory. This project does not copy saved account files into the build.

In the account manager window, select multiple accounts, enter a Place ID, and use RAM's Join Server action. The existing `EnableMultiRbx=true` setting on this machine enables RAM's multi-instance mutex. The logo button next to Settings opens the optimizer panel only when clicked. The optimizer has no separate taskbar entry, and its in-window heading is **Roblox**. Its **Auto** button controls RAM trimming; the CPU menu offers Off, two logical processors, or one logical processor per Roblox session. The Discord button opens `https://discord.com/users/598301893877432341`.

Build requires MSBuild, the .NET Framework 4.7.2 reference assemblies, and packages restored from `packages.config`. The `MizuOptimizerForm.cs` file contains the optimizer logic. The full application has not been launched in this workspace because the user requested that only they open it.
