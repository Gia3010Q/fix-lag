using CefSharp;
using RBX_Alt_Manager;
using RBX_Alt_Manager.Classes;
using RestSharp;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class BrowserChecks
{
    private sealed class AuthenticationFixture : HttpMessageHandler
    {
        internal HttpStatusCode Status = HttpStatusCode.OK;
        internal string Body = "{\"id\":12345,\"name\":\"FixtureUser\"}";
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.RequestUri.Host == "users.roblox.com" && request.RequestUri.AbsolutePath == "/v1/users/authenticated", "Wrong authentication endpoint");
            Calls++;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void CheckAccountImport()
    {
        const string fixture = "synthetic-cookie-value-only";
        Check(CookieAuthentication.TryNormalize(" Cookie: other=value; .ROBLOSECURITY=\"" + fixture + "\"; Path=/ ", out string normalized) && normalized == fixture, "Named cookie normalization failed");
        Check(CookieAuthentication.TryNormalize("  " + fixture + "\r\n", out normalized) && normalized == fixture, "Pasted token whitespace failed");
        Check(!CookieAuthentication.TryNormalize("", out _) && !CookieAuthentication.TryNormalize("other=value; path=/", out _) && !CookieAuthentication.TryNormalize("a\nb", out _), "Malformed cookie accepted");
        Check(!CookieAuthentication.TryReadIdentity("{}", out _, out _) && !CookieAuthentication.TryReadIdentity("<html>error</html>", out _, out _) && !CookieAuthentication.TryReadIdentity("{\"id\":0,\"name\":\"invalid\"}", out _, out _), "Invalid account identity accepted");
        var legacy = new Account(fixture, "{\"UserId\":44,\"Name\":\"LegacyFixture\"}");
        Check(legacy.Valid && legacy.UserID == 44, "Legacy fixture compatibility failed");
        var handler = new AuthenticationFixture();
        using (var client = new RestClient(new RestClientOptions("https://users.roblox.com") { ConfigureMessageHandler = _ => handler }))
        {
            AccountManager.UsersClient = client;
            var imported = new Account(".ROBLOSECURITY=" + fixture + "; Path=/");
            Check(imported.Valid && imported.UserID == 12345 && imported.Username == "FixtureUser" && imported.SecurityToken == fixture, "Actual account import failed with current identity JSON");
            foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, (HttpStatusCode)429, HttpStatusCode.ServiceUnavailable })
            {
                handler.Status = status;
                var rejected = new Account(fixture);
                Check(!rejected.Valid && !string.IsNullOrEmpty(rejected.LoginError) && !rejected.LoginError.Contains(fixture), "Missing or unsafe import error");
            }
            handler.Status = HttpStatusCode.OK;
            handler.Body = "{\"errors\":[{\"code\":0}]}";
            Check(!new Account(fixture).Valid, "API error payload accepted as account");
            Check(handler.Calls == 6, "Authentication cases did not execute");
        }
        Console.WriteLine("PASS: actual cookie account import, current endpoint, legacy fixture, normalization, HTTP failures and invalid JSON (synthetic only).");
    }

    private static async Task ServePage(TcpListener listener)
    {
        try
        {
            while (true)
            {
                using (var client = await listener.AcceptTcpClientAsync())
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                {
                    await reader.ReadLineAsync();
                    string header;
                    do { header = await reader.ReadLineAsync(); } while (!string.IsNullOrEmpty(header));
                    byte[] body = Encoding.UTF8.GetBytes("<!doctype html><html><head><title>RobloxManagerBrowserFixture</title></head><body id='fixture'>Synthetic browser fixture</body></html>");
                    byte[] headers = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers, 0, headers.Length);
                    await stream.WriteAsync(body, 0, body.Length);
                }
            }
        }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
        catch (IOException) { }
    }

    private static async Task CheckBrowserAsync(string temp)
    {
        // The app's current directory is deliberately different from its runtime directory.
        BrowserRuntime.Initialize(temp);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Task serving = ServePage(listener);
        try
        {
            string url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
            var window = CefBrowser.Instance;
            foreach (string token in new[] { "synthetic-account-one", "synthetic-account-two" })
            {
                await window.EnterBrowserModeAsync(new Account { SecurityToken = token, Username = "FixtureUser", Valid = true }, url);
                DateTime deadline = DateTime.UtcNow.AddSeconds(25);
                bool rendered = false;
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(100);
                    if (window.browser.IsLoading || window.browser.Address != url) continue;
                    var result = await window.browser.EvaluateScriptAsync("document.getElementById('fixture') && document.getElementById('fixture').textContent");
                    if (result.Success && (string)result.Result == "Synthetic browser fixture") { rendered = true; break; }
                }
                Check(rendered && window.Visible && window.browser.Width > 0 && window.browser.Height > 0, "Browser window did not render the fixture page");
                var cookies = await Cef.GetGlobalCookieManager().VisitAllCookiesAsync();
                var accountCookies = cookies.Where(c => c.Name == ".ROBLOSECURITY").ToList();
                Check(accountCookies.Count == 1 && accountCookies[0].Value == token && accountCookies[0].HttpOnly && accountCookies[0].Secure && accountCookies[0].Path == "/", "Cookie was not set before navigation or old account leaked");
            }
            // Exercise the actual login action too: it must clear the selected account.
            await window.LoginAsync();
            var cleared = await Cef.GetGlobalCookieManager().VisitAllCookiesAsync();
            Check(!cleared.Any(c => c.Name == ".ROBLOSECURITY"), "Login action kept a prior account cookie");
            Console.WriteLine("PASS: actual browser window renders a local page from a separate working directory; cookie injection/account switching and login reset work.");
        }
        finally
        {
            listener.Stop();
            await serving;
            CefBrowser.Instance.Dispose();
        }
    }

    [STAThread]
    private static int Main()
    {
        int exit = 1;
        string temp = Path.Combine(Path.GetTempPath(), "RobloxManager-BrowserChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        Directory.SetCurrentDirectory(temp);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var host = new Form { Text = "Roblox Manager synthetic browser checks", Width = 350, Height = 100 };
        host.Shown += async (sender, args) =>
        {
            try
            {
                CheckAccountImport();
                await CheckBrowserAsync(temp);
                exit = 0;
            }
            catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); }
            finally { host.Close(); }
        };
        try { Application.Run(host); }
        finally
        {
            if (Cef.IsInitialized == true) Cef.Shutdown();
            Console.WriteLine("Browser diagnostic output: " + temp);
        }
        return exit;
    }
}
