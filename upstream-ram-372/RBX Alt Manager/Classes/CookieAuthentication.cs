using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace RBX_Alt_Manager.Classes
{
    internal static class CookieAuthentication
    {
        internal const string AuthenticatedPath = "/v1/users/authenticated";

        internal static bool TryNormalize(string input, out string token)
        {
            token = null;
            if (string.IsNullOrWhiteSpace(input)) return false;
            string value = input.Trim();
            if (value.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) value = value.Substring(7).Trim();
            var match = Regex.Match(value, @"(?:^|;)\s*\.ROBLOSECURITY\s*=\s*([^;]*)", RegexOptions.IgnoreCase);
            if (match.Success) value = match.Groups[1].Value.Trim();
            if (value.Length >= 2 && ((value[0] == '"' && value[value.Length - 1] == '"') ||
                (value[0] == '\'' && value[value.Length - 1] == '\''))) value = value.Substring(1, value.Length - 2);
            // Never accept an entire Cookie header or multiple lines as one token.
            if (value.Length == 0 || value.Any(c => char.IsWhiteSpace(c) || c == ';' || c == '\r' || c == '\n')) return false;
            token = value;
            return true;
        }

        internal static bool TryReadIdentity(string json, out long id, out string name)
        {
            id = 0;
            name = null;
            try
            {
                var data = JObject.Parse(json);
                var identifier = data.GetValue("id", StringComparison.OrdinalIgnoreCase) ?? data.GetValue("UserId", StringComparison.OrdinalIgnoreCase);
                id = identifier?.Value<long>() ?? 0;
                name = data.GetValue("name", StringComparison.OrdinalIgnoreCase)?.Value<string>();
                return id > 0 && !string.IsNullOrWhiteSpace(name) && data["errors"] == null;
            }
            catch { return false; }
        }

        internal static string ResponseError(int status)
        {
            switch (status)
            {
                case 401: return "Cookie không hợp lệ hoặc đã hết hạn. Lấy lại cookie từ phiên đăng nhập của bạn.";
                case 403: return "Roblox từ chối phiên đăng nhập (HTTP 403). Kiểm tra tài khoản trong trình duyệt rồi thử lại.";
                case 429: return "Roblox giới hạn số lần thử (HTTP 429). Chờ một lúc rồi thử lại.";
                case 0: return "Không kết nối được API Roblox. Kiểm tra mạng, proxy và ngày giờ Windows.";
                default: return "API Roblox trả về HTTP " + status + ". Thử lại sau.";
            }
        }
    }
}
