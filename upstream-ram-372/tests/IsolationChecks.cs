namespace RBX_Alt_Manager
{
    public static class IsolationChecks
    {
        private static void Assert(bool value, string message)
        {
            if (!value) throw new System.Exception(message);
        }

        private static bool Owned(string name)
        {
            var method = typeof(AppIdentity).GetMethod("MutexIsOwned",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            return (bool)method.Invoke(null, new object[] { name });
        }

        public static string Run()
        {
            Assert(AppIdentity.ManagerMutex != AppIdentity.LegacyManagerMutex, "Manager mutex is shared with RAM");
            Assert(AppIdentity.RobloxMutex == "ROBLOX_singletonMutex", "Roblox mutex was renamed");
            Assert(AppIdentity.WebPort != "7963" && AppIdentity.NexusPort != "5242", "Default ports collide with RAM");
            string expected = System.IO.Path.Combine(System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.LocalApplicationData), "Mizuauto", "Manager");
            Assert(AppIdentity.DataDirectory == expected, "Profile is not isolated");

            string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Mizuauto-Test-" + System.Guid.NewGuid());
            System.IO.Directory.CreateDirectory(temp);
            try
            {
                Assert(!AppIdentity.OriginalManagerInFolder(temp), "Empty install directory rejected");
                foreach (string filename in new[] { "Roblox Account Manager.exe", "RBX Alt Manager.exe" })
                {
                    string path = System.IO.Path.Combine(temp, filename);
                    System.IO.File.WriteAllText(path, "synthetic marker");
                    Assert(AppIdentity.OriginalManagerInFolder(temp), "Original install directory not detected");
                    Assert(System.IO.File.ReadAllText(path) == "synthetic marker", "Original file was modified");
                    System.IO.File.Delete(path);
                }
            }
            finally { System.IO.Directory.Delete(temp, true); }

            string testName = @"Local\Mizuauto.IsolationTest." + System.Guid.NewGuid();
            Assert(!Owned(testName), "Nonexistent mutex reported owned");
            using (var keepAlive = new System.Threading.Mutex(false, testName))
            using (var ready = new System.Threading.ManualResetEventSlim())
            using (var release = new System.Threading.ManualResetEventSlim())
            {
                Assert(!Owned(testName), "Unowned mutex reported owned");
                var holder = new System.Threading.Thread(() =>
                {
                    using (var mutex = System.Threading.Mutex.OpenExisting(testName))
                    {
                        mutex.WaitOne();
                        ready.Set();
                        try { release.Wait(); }
                        finally { mutex.ReleaseMutex(); }
                    }
                });
                holder.IsBackground = true;
                holder.Start();
                try
                {
                    Assert(ready.Wait(5000), "Holder did not start");
                    Assert(Owned(testName), "Owned mutex was not detected");
                    // A distinct application identity can acquire its own mutex while this is held.
                    using (var other = new System.Threading.Mutex(false, testName + ".other"))
                    {
                        Assert(other.WaitOne(0), "Independent application mutex was blocked");
                        other.ReleaseMutex();
                    }
                }
                finally
                {
                    release.Set();
                    Assert(holder.Join(5000), "Holder did not stop");
                }
                Assert(!Owned(testName), "Released mutex still reported owned");
            }
            return "PASS: independent identity/profile/ports, original-folder protection, and mutex ownership lifecycle.";
        }
    }
}