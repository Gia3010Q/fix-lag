using log4net;
using RBX_Alt_Manager.Properties;
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace RBX_Alt_Manager
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>

        public static ILog Logger => LogManager.GetLogger("Mizuauto");
        public static bool Closed = false; // RobloxProcess.cs would cause the program to chill in the background as long roblox was also running
        public static bool Elevated;
        public static float Scale
        {
            get
            {
                float _Scale = (AccountManager.General != null && AccountManager.General.Exists("WindowScale")) ? AccountManager.General.Get<float>("WindowScale") : 0f;

                if (_Scale > 3f) AccountManager.General.RemoveProperty("WindowScale");

                if (_Scale > 0 && _Scale <= 3)
                    return AccountManager.General.Get<float>("WindowScale");

                return 1f;
            }
        }

        public static bool ScaleFonts
        {
            get
            {
                if (AccountManager.General != null && AccountManager.General.Exists("ScaleFonts"))
                    return AccountManager.General.Get<bool>("ScaleFonts");

                return true;
            }
        }

        [STAThread]
        static void Main(params string[] Arguments)
        {
            // Never run the upstream updater: it installs a different application.
            if (Array.Exists(Arguments, argument => argument == "-update"))
            {
                MessageBox.Show("Cập nhật Mizuauto bằng bộ cài Mizuauto riêng.", "Mizuauto");
                return;
            }
            if (AppIdentity.OriginalManagerInFolder(AppIdentity.InstallDirectory))
            {
                MessageBox.Show("Hãy cài Mizuauto vào thư mục riêng, không đặt cạnh Roblox Account Manager.exe hoặc RBX Alt Manager.exe.", "Mizuauto");
                return;
            }

            bool restart = false;
            using (var mutex = new Mutex(false, AppIdentity.ManagerMutex))
            {
                bool acquired;
                try { acquired = mutex.WaitOne(TimeSpan.Zero, false); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired)
                {
                    MessageBox.Show("Mizuauto đang chạy. Bạn vẫn có thể mở RAM gốc riêng.", "Mizuauto");
                    return;
                }
                try
                {
                    Directory.CreateDirectory(AppIdentity.DataDirectory);
                    // Existing relative data/cache paths now resolve inside Mizuauto's profile.
                    // Managed and native dependencies still load from the executable directory.
                    Directory.SetCurrentDirectory(AppIdentity.DataDirectory);
                    Elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
                    restart = PrepareRuntimeFiles();
                    if (!restart) RunManager();
                }
                catch (Exception exception)
                {
                    try
                    {
                        Directory.CreateDirectory(AppIdentity.DataDirectory);
                        File.WriteAllText(Path.Combine(AppIdentity.DataDirectory, "startup-error.log"), exception.ToString());
                    }
                    catch { }
                    MessageBox.Show("Không khởi động được Mizuauto: " + exception.Message + "\nChi tiết: " + Path.Combine(AppIdentity.DataDirectory, "startup-error.log"), "Mizuauto", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally { mutex.ReleaseMutex(); }
            }
            // Release our singleton before launching the child to avoid a restart race.
            if (restart) Process.Start(Application.ExecutablePath, "-restart");
        }

        private static bool PrepareRuntimeFiles()
        {
            string appConfigPath = Application.ExecutablePath + ".config";
            string logConfigPath = Path.Combine(AppIdentity.InstallDirectory, "log4.config");
            bool restart = false;
            if (Utilities.FileSHA256(appConfigPath) != Resources.AppConfigHash)
            {
                File.WriteAllBytes(appConfigPath, Resources.App);
                restart = true;
            }
            if (Utilities.FileSHA256(logConfigPath) != Resources.Log4ConfigHash)
            {
                File.WriteAllBytes(logConfigPath, Resources.log4);
                restart = true;
            }
            string sodiumPath = Path.Combine(AppIdentity.InstallDirectory, "libsodium.dll");
            if (!File.Exists(sodiumPath)) File.WriteAllBytes(sodiumPath, Resources.libsodium);
            string themePath = Path.Combine(AppIdentity.DataDirectory, "RAMTheme.ini");
            if (!File.Exists(themePath)) File.WriteAllText(themePath, Resources.DefaultTheme);
            return restart;
        }

        private static void RunManager()
        {
            Application.ApplicationExit += (sender, args) => Closed = true;
            // RAM's legacy 773 workaround exclusively locks RobloxCookies.dat for its lifetime.
            // Do not take that shared lock here: it interferes with another account manager.
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new AccountManager());
        }
    }
}
