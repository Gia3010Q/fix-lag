# Roblox Manager — Windows x64 test build

Phiên bản Roblox Manager chạy độc lập, có thư mục dữ liệu và khóa ứng dụng riêng. Không cần cài hoặc mở Roblox Account Manager để sử dụng.

## Tải bản test

**[Tải gói Windows x64 — ZIP khoảng 123 MB](https://github.com/Gia3010Q/fix-lag/releases/download/v3.7.2.3-roblox-manager-test/Roblox-Manager-3.7.2.3-win-x64.zip)**

[Trang Pre-release và mã nguồn tương ứng](https://github.com/Gia3010Q/fix-lag/releases/tag/v3.7.2.3-roblox-manager-test)

1. Giải nén **toàn bộ** ZIP vào một thư mục riêng.
2. Mở `RobloxManager.exe` để thử ngay, hoặc `Cai-Roblox-Manager.cmd` để copy vào `%LOCALAPPDATA%\Programs\RobloxManager` và tạo shortcut Start Menu.
3. Nếu gặp lỗi, chạy `Kiem-tra-Roblox-Manager.cmd`; kết quả ở `diagnostics-result.txt`.

Cần Windows 10/11 x64 và Microsoft .NET Framework 4.7.2 trở lên. Giữ toàn bộ DLL và thư mục `locales` cạnh EXE. Tài khoản/cài đặt riêng nằm tại `%LOCALAPPDATA%\Mizuauto\Manager`. Gói không chứa dữ liệu tài khoản thật và không tự nhập tài khoản từ phần mềm khác. EXE chưa được ký số.

## Tình trạng kiểm tra

Đây là bản **test**, chưa phải bản ổn định. Đã build EXE C# và DLL Rust x64; kiểm tra chẩn đoán của bản trước trên Windows đã qua, gồm khóa/hồ sơ riêng, xác thực API, mã hóa/giải mã với dữ liệu giả và vòng đời Rust engine. **Chưa xác nhận giao diện, đăng nhập hoặc Join Game trên Windows thật.** Lần kiểm tra GUI trên Wine dừng ở lỗi GDI+ của Mono.

Xem [hướng dẫn chi tiết](upstream-ram-372/STANDALONE.vi.md) và [báo cáo kiểm chứng](VERIFICATION.md).

## Mã nguồn và bản quyền

Dựa trên [mizuharasup/Mizuauto](https://github.com/mizuharasup/Mizuauto) tại commit `9f3321571cf276ef54d43477fd9323d8af4401a1`, phát triển từ [Roblox Account Manager](https://github.com/ic3w0lf22/Roblox-Account-Manager). Giữ giấy phép GPL-3.0 và phần ghi công upstream. Đây là bản tùy chỉnh, không phải toàn bộ phần mềm viết mới từ đầu.

Frontend: `upstream-ram-372/RBX Alt Manager`. Backend tối ưu: `upstream-ram-372/mizuauto-engine`, dùng chung mã trong `mizuauto-rust`. Các bản GUI Rust và Python cũ được giữ làm tham khảo.

Nguồn tương ứng cũng được đính kèm trong Assets của Pre-release, cùng các tệp SHA-256. Xem `SOURCE_EXPORT.md` để biết nguồn gốc snapshot.

Tên mới là **Roblox Manager**, phiên bản 3.7.2.3. Giữ hồ sơ `%LOCALAPPDATA%\Mizuauto\Manager` để dùng lại tài khoản/cài đặt của bản standalone trước; không tự nhập từ Roblox Account Manager gốc. Tên DLL và khóa nội bộ được giữ để tương thích.
