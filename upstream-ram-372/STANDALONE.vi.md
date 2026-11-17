# Mizuauto Standalone 3.7.2.2 — bản test Windows x64

Đã build EXE C# và DLL Rust mới. Gói test chứa chương trình và thư viện chạy, không phải chỉ có mã nguồn. Cần Windows 10/11 x64 với Microsoft .NET Framework 4.7.2 trở lên (Windows 11 thường đã có 4.8). Không cần Roblox Account Manager, Visual Studio, Rust hoặc Python để dùng gói test.

## Cách chạy và cài

1. Giải nén **toàn bộ** ZIP vào một thư mục riêng.
2. Để thử ngay, mở `mizuauto.exe` trong thư mục đã giải nén. Giữ các DLL và thư mục `locales` cạnh nó.
3. Nếu muốn cài vào thư mục riêng và tạo shortcut Start Menu, bấm `Cai-Mizuauto.cmd`. Tệp này dùng Windows PowerShell có sẵn, kiểm tra SHA-256 các tệp rồi copy vào `%LOCALAPPDATA%\Programs\Mizuauto`.
4. Windows có thể hỏi quyền quản trị khi mở ứng dụng theo manifest kế thừa. Phần mềm chưa được ký số.

Danh sách tài khoản mới mặc định trống. Thêm tài khoản qua giao diện của Mizuauto. Trình duyệt CEF x64 cùng VC++ runtime đã được kèm sẵn và bật mặc định cho hồ sơ mới. Nếu đổi sang trình duyệt Chromium trong Settings thì ứng dụng có thể cần tải Chromium qua mạng.

Không đặt ứng dụng trong thư mục Roblox Account Manager cũ. Chương trình và tệp cài sẽ từ chối nếu thư mục đích chứa EXE của ứng dụng cũ. Không chia sẻ hồ sơ bằng junction/symlink.

| Thành phần | Vị trí |
| --- | --- |
| Tệp chương trình sau khi bấm Cai-Mizuauto | `%LOCALAPPDATA%\Programs\Mizuauto` |
| Tài khoản, cài đặt, theme và cache riêng | `%LOCALAPPDATA%\Mizuauto\Manager` |
| Trạng thái bộ tối ưu | `%LOCALAPPDATA%\Mizuauto\state.json` |
| Dữ liệu Roblox Account Manager khác | Giữ nguyên, Mizuauto không tự đọc hoặc đồng bộ |

Hồ sơ tính theo tài khoản Windows đang chạy ứng dụng. Muốn bỏ bản test, đóng Mizuauto, xóa thư mục chương trình và shortcut Mizuauto trong Start Menu. Hồ sơ tài khoản riêng vẫn còn; không cần xóa hồ sơ để bỏ chương trình.

## Chạy đồng thời với ứng dụng cũ (tùy chọn)

Mizuauto tự chạy độc lập và mặc định tự bật Multi Roblox cho hồ sơ mới. Không cần mở phần mềm khác trước.

Nếu muốn cả hai manager cùng mở nhiều Roblox, chỉ một manager nên giữ mutex Multi Roblox thực tế. Có thể bật Multi Roblox ở Roblox Account Manager trước, rồi trong Mizuauto chọn **Settings → Mizuauto → Use Multi Roblox from RAM**. Đây là chế độ phối hợp tùy chọn; không phải điều kiện sử dụng Mizuauto. Giữ ứng dụng đang quản lý mutex mở trong lúc chơi.

Không đổi tên `ROBLOX_singletonMutex`: đây là tên Roblox dùng. Mizuauto đã đổi khóa chống mở trùng của chính manager sang tên riêng. Việc tách ứng dụng không bỏ qua giới hạn đăng nhập cùng một tài khoản do máy chủ Roblox áp dụng.

Hai manager vẫn điều khiển Roblox trên cùng máy. Bộ tối ưu Mizuauto có thể áp dụng cho mọi tiến trình Roblox, kể cả phiên do ứng dụng cũ mở. Tránh để cả hai công cụ cùng sửa FPS/ClientSettings hoặc cùng điều chỉnh cửa sổ.

## Kiểm tra và báo lỗi

Bấm `Kiem-tra-Mizuauto.cmd`. Công cụ kiểm tra khóa/hồ sơ/cổng riêng, xác thực API và khả năng nạp/gọi/đóng Rust DLL. Nó ghi `diagnostics-result.txt` trong thư mục chương trình, không đọc tài khoản và không bật tối ưu các phiên Roblox.

Nếu không mở được giao diện, xem `%LOCALAPPDATA%\Mizuauto\Manager\startup-error.log`. Có thể gửi thông báo lỗi hoặc kết quả chẩn đoán để kiểm tra tiếp. Không cần gửi `AccountData.json`, cookie hoặc mật khẩu.

Bản test đã biên dịch và chạy kiểm tra chẩn đoán qua Wine/Mono. **Chưa xác nhận giao diện, đăng nhập hay Join Game trên Windows thật.** Kiểm tra giao diện trên Wine dừng ở lỗi GDI+ của Mono trong ToolStrip; không tính lần đó là một lần mở giao diện thành công. Mục đích gói này là để thử trên Windows, chưa phải bản phát hành ổn định đã được chứng nhận.

## API

Web Server và quyền API mặc định tắt. Cổng Mizuauto là 7965; Nexus là 5243. Mọi API tài khoản giờ yêu cầu mật khẩu đúng, dài tối thiểu 6 ký tự. Có thể dùng header `X-Mizuauto-Password` hoặc tham số Password tương thích cũ. `/Running` là health check không chứa tài khoản. Điều kiện trả cookie sai và lỗi bỏ qua mật khẩu thiếu đã được sửa. Không dùng nguyên script tham khảo `RAMAccount.lua` vốn hướng tới cổng 7963 của ứng dụng cũ.

## Chuyển tài khoản cũ (không bắt buộc)

Mizuauto không tự động nhập. Có thể thêm lại qua giao diện. Nếu muốn giữ dữ liệu cũ, đóng cả hai ứng dụng, sao lưu, rồi chỉ **copy** `AccountData.json` vào hồ sơ Mizuauto khi hồ sơ mới chưa có tài khoản. Không di chuyển bản gốc và không ghi đè hồ sơ đã có dữ liệu. DPAPI cần đúng máy/tài khoản Windows; mã hóa bằng mật khẩu cần mật khẩu cũ. Bản sao không đồng bộ. Không copy toàn bộ RAMSettings.ini vì có thể mang theo cổng hoặc đường dẫn xuất dùng chung.

## Build từ nguồn

Frontend nằm trong `RBX Alt Manager`, backend tối ưu nhẹ nằm trong `mizuauto-engine` và dùng chung mã với `mizuauto-rust`. Bản Rust GUI độc lập và nguyên mẫu Python vẫn là mã tham khảo, không phải EXE trong gói test.

Trên Windows, cần VS Build Tools/MSBuild, targeting pack .NET Framework 4.7.2, NuGet CLI và Rust stable MSVC x64. Dùng Developer PowerShell chạy `build-standalone.ps1`. Với bộ cài Inno Setup riêng, xem `installer/mizuauto.iss`; bộ cài Inno chưa được biên dịch trong gói test này. Tệp cài dùng được kèm gói test là `Cai-Mizuauto.cmd` và `Install-Mizuauto.ps1`.

Lần build trên Linux dùng Roslyn 4.11 từ .NET SDK 8.0.428, reference assemblies net472 1.0.3, Rust 1.100.0 và MinGW GCC 14. Rust DLL được build x86_64-pc-windows-gnu và không cần DLL GCC phụ. UI .resources và các managed DLL được lấy từ binary snapshot gốc; toàn bộ mã chương trình frontend được biên dịch lại từ nguồn đã sửa, không vá EXE cũ. CEF x64 và VC++ runtime lấy từ gói phát hành chính thức. Xem `VERIFICATION.md` và log build đi kèm mã nguồn để biết giới hạn kiểm chứng.

Giữ giấy phép GPL-3.0 của upstream khi phân phối và cung cấp mã nguồn tương ứng. Gói không chứa dữ liệu tài khoản thật.
