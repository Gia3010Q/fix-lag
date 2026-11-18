using CefSharp;
using CefSharp.WinForms;
using System;
using System.IO;
using System.Windows.Forms;

namespace RBX_Alt_Manager.Classes
{
    internal static class BrowserRuntime
    {
        internal static void Initialize(string diagnosticsDirectory = null)
        {
            if (Cef.IsInitialized == true) return;
            string root = AppIdentity.InstallDirectory;
            foreach (string name in new[] { "libcef.dll", "CefSharp.Core.Runtime.dll", "CefSharp.BrowserSubprocess.exe", "CefSharp.BrowserSubprocess.Core.dll", "icudtl.dat", "resources.pak" })
                if (!File.Exists(Path.Combine(root, name))) throw new FileNotFoundException("Missing browser runtime file", name);
            if (!Directory.Exists(Path.Combine(root, "locales"))) throw new DirectoryNotFoundException("Missing browser locales");
            string diagnostics = diagnosticsDirectory ?? AppIdentity.DataDirectory;
            Directory.CreateDirectory(diagnostics);
            var settings = new CefSettings
            {
                BrowserSubprocessPath = Path.Combine(root, "CefSharp.BrowserSubprocess.exe"),
                ResourcesDirPath = root,
                LocalesDirPath = Path.Combine(root, "locales"),
                // Keep browser sessions in memory; stored account data remains separate.
                CachePath = string.Empty,
                PersistSessionCookies = false,
                LogFile = Path.Combine(diagnostics, "cef-browser.log"),
                LogSeverity = LogSeverity.Error
            };
            if (!Cef.Initialize(settings, performDependencyCheck: true, browserProcessHandler: null))
                throw new InvalidOperationException("CEF initialization failed");
        }

        internal static void ReportError(IWin32Window owner, Exception error)
        {
            string path = Path.Combine(AppIdentity.DataDirectory, "browser-error.log");
            // CDP errors may contain cookie parameters. Record type/stack only.
            try { File.WriteAllText(path, DateTime.UtcNow.ToString("O") + "\n" + error.GetType().FullName + "\n" + error.StackTrace); } catch { }
            string missing = error is FileNotFoundException file ? "\nThiếu: " + Path.GetFileName(file.FileName) : string.Empty;
            MessageBox.Show(owner, "Không mở được trình duyệt. Giải nén đầy đủ gói mới vào thư mục riêng và thử lại.\nLoại lỗi: " + error.GetType().Name + missing + "\nChi tiết: " + path,
                "Roblox Manager · Browser", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
