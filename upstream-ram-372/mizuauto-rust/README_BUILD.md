# Mizuauto Rust (Windows x64)

Đây là bản chuyển Mizuauto sang Rust/eframe đang phát triển. Mã nguồn được tách khỏi bản C# hiện có; build không thay thế bản C# hoặc sửa `AccountData.json`.

## Build

Cần Rust stable MSVC x64, Visual Studio C++ Build Tools và NASM (do thư viện giải mã video cần lắp ráp AV1). Trên máy dùng ứng dụng cần WebView2 Runtime để mở trình duyệt tích hợp. Từ thư mục này:

```powershell
$env:PATH = "$env:LOCALAPPDATA\MizuautoBuildTools\nasm-2.16.03;$env:PATH" # nếu NASM đã có trong PATH thì bỏ dòng này
cargo check --target x86_64-pc-windows-msvc
cargo build --release --target x86_64-pc-windows-msvc
```

File tạo ra ở `target\x86_64-pc-windows-msvc\release\mizuauto.exe`. Manifest UAC `requireAdministrator` và icon được nhúng qua `build.rs`. Không dùng `cargo run` để build. Bản Rust tìm dữ liệu cạnh chính file exe, nên nếu muốn dùng dữ liệu C# cũ thì cần tự sao lưu rồi đặt bản sao vào thư mục này.

## Dữ liệu

- `AccountData.json`, `RAMSettings.ini`, `RAMTheme.ini` nằm cạnh exe.
- `%LOCALAPPDATA%\Mizuauto\state.json` lưu chu kỳ và trạng thái tối ưu.
- Logo UI tìm `D:\Downloads\mizulogo.png`, rồi `assets\mizulogo.png` cạnh exe, cuối cùng là bản nhúng.
- `AccountData.json` cũ được đọc theo DPAPI hoặc Argon2i + XSalsa20-Poly1305 thực tế của RAM 3.7.2. Khi ghi, cập nhật `AccountData.json.backup`, sau đó thay file qua tệp tạm.

## Chức năng hiện có

- UI quản lý tài khoản, chọn nhiều, thêm bằng cookie hoặc đăng nhập qua WebView2, sửa alias/description/group, lưu Place/Job, tìm server còn chỗ, mở profile, copy thông tin, hiển thị presence, mở nhiều Roblox bằng ticket.
- Theo dõi các tiến trình `RobloxPlayerBeta.exe`; chọn thu hồi working set trên 384 MiB như trước hoặc đặt mức tối đa 100–3000 MB theo bước 100 MB. Tùy chọn chu kỳ từ 0,1 giây và giới hạn 1/2 CPU. Mức working set và CPU được khôi phục khi tắt Auto hoặc thoát ứng dụng.
- Giữ mutex `ROBLOX_singletonMutex` khi bật Multi Roblox, tự thử lại với backoff.
- Theme ảnh tĩnh, GIF và video MP4/WebM tùy codec được Windows hỗ trợ. Decoder chỉ được tạo khi chọn nền. Video phát im lặng và tạm dừng khi cửa sổ thu nhỏ.
- Web server tắt mặc định. Nếu bật, yêu cầu mật khẩu ít nhất 6 ký tự. Header xác thực `X-Mizuauto-Password`; API `/api?Method=GetAccounts`, `GetCookie`, `LaunchAccount`, `FollowUser`, `SetAlias`, `SetDescription` chịu các quyền tương ứng trong Settings. Mặc định chỉ bind `127.0.0.1`.
- Tùy chọn FPS và file `ClientAppSettings.json` riêng được áp dụng trước khi mở Roblox; bản cũ trong thư mục Roblox được sao lưu một lần.

## Giới hạn hiện tại

Bản Rust chưa đạt đủ mọi chức năng của Roblox Account Manager 3.7.2. Chưa có danh sách server đầy đủ, điều khiển tài khoản nâng cao và cập nhật tự động. Các cài đặt `CheckForUpdates`, `AutoCookieRefresh` và `SavePasswords` mới được lưu, chưa có tác dụng tương ứng. Tương thích dữ liệu được đối chiếu theo mã nguồn cũ nhưng chưa thử với dữ liệu thật; không mở dữ liệu thật trong quá trình build.

`EmptyWorkingSet` chỉ loại bớt các trang khỏi working set. Chế độ giới hạn dùng `SetProcessWorkingSetSizeEx` với `QUOTA_LIMITS_HARDWS_MAX_ENABLE`: nó giới hạn working set đang nằm trong RAM vật lý, không giới hạn tổng bộ nhớ Roblox cấp phát. Mức thấp như 100 MB có thể khiến game giật do nạp lại trang bộ nhớ.

Không chạy thử ứng dụng hoặc Roblox trong quá trình phát triển trên máy này theo yêu cầu của chủ máy. Chỉ `cargo check` và `cargo build` được dùng để kiểm tra.
