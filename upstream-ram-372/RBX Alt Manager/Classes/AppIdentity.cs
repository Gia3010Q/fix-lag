using System;
using System.IO;
using System.Threading;

namespace RBX_Alt_Manager
{
    internal static class AppIdentity
    {
        internal const string ManagerMutex = @"Local\Mizuauto.Manager.66f908fc-b7d0-45da-9050-c98e1095a3d1";
        internal const string LegacyManagerMutex = "{93b3858f-3dac-4dc0-99cb-0476efc5adce}";
        // This name belongs to Roblox. It must stay shared with the original manager.
        internal const string RobloxMutex = "ROBLOX_singletonMutex";
        internal const string DisplayName = "Roblox Manager";
        internal const string ExecutableName = "RobloxManager.exe";
        internal const string ProcessName = "RobloxManager";
        internal const string StartupName = "Roblox Manager";
        internal const string ExternalMultiSetting = "UseOriginalRamMultiRoblox";
        internal const string WebPort = "7965";
        internal const string NexusPort = "5243";

        internal static string InstallDirectory => AppDomain.CurrentDomain.BaseDirectory;
        // Keep the existing profile directory so renaming never moves account data.
        internal static string DataDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mizuauto", "Manager");

        internal static bool OriginalManagerInFolder(string folder)
        {
            return File.Exists(Path.Combine(folder, "Roblox Account Manager.exe")) ||
                File.Exists(Path.Combine(folder, "RBX Alt Manager.exe"));
        }

        internal static bool ExternalMultiRobloxReady()
        {
            // Existence alone is insufficient: an unowned mutex does not keep Roblox multi-instance.
            // The user must enable Multi Roblox in RAM first; these checks only detect its loss.
            return MutexIsOwned(LegacyManagerMutex) && MutexIsOwned(RobloxMutex);
        }

        private static bool MutexIsOwned(string name)
        {
            Mutex mutex;
            if (!Mutex.TryOpenExisting(name, out mutex)) return false;
            using (mutex)
            {
                bool acquired;
                try { acquired = mutex.WaitOne(TimeSpan.Zero, false); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) return true;
                mutex.ReleaseMutex();
                return false;
            }
        }
    }
}
