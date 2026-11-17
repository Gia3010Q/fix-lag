using System;
using System.Runtime.InteropServices;
using RBX_Alt_Manager;

internal static class Diagnostics
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Session
    {
        public uint Pid, Reserved;
        public ulong WorkingSet;
        public double Cpu;
        public ulong Started;
    }

    [DllImport("mizuauto_engine.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern uint mizu_engine_abi_version();
    [DllImport("mizuauto_engine.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr mizu_engine_create();
    [DllImport("mizuauto_engine.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int mizu_engine_set_config(IntPtr engine, double interval, uint threads, int enabled, uint ram);
    [DllImport("mizuauto_engine.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern uint mizu_engine_sessions(IntPtr engine, [Out] Session[] rows, uint capacity);
    [DllImport("mizuauto_engine.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern void mizu_engine_destroy(IntPtr engine);
    [DllImport("libsodium.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sodium_init();

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException(message);
    }

    private static int Main()
    {
        try
        {
            Console.WriteLine(IsolationChecks.Run());
            const string password = "synthetic-password";
            Check(!ApiAuthentication.ValidPassword(password, null), "Missing API password accepted");
            Check(!ApiAuthentication.ValidPassword(password, ""), "Empty API password accepted");
            Check(!ApiAuthentication.ValidPassword(password, "wrong"), "Wrong API password accepted");
            Check(!ApiAuthentication.ValidPassword(null, password), "Unconfigured API password accepted");
            Check(!ApiAuthentication.ValidPassword("short", "short"), "Weak API password accepted");
            Check(!ApiAuthentication.ValidPassword(password, "Synthetic-password"), "API password comparison is not exact");
            Check(ApiAuthentication.ValidPassword(password, password), "Valid API password rejected");
            Console.WriteLine("PASS: API password regression cases.");
            Check(sodium_init() >= 0, "Native cryptography library initialization failed");
            Console.WriteLine("PASS: VC++ runtime and native cryptography library load.");
            byte[] fixturePassword = System.Text.Encoding.UTF8.GetBytes("synthetic password only");
            byte[] encrypted = RBX_Alt_Manager.Classes.Cryptography.Encrypt("synthetic account fixture", fixturePassword);
            byte[] decrypted = RBX_Alt_Manager.Classes.Cryptography.Decrypt(encrypted, fixturePassword);
            Check(System.Text.Encoding.UTF8.GetString(decrypted) == "synthetic account fixture", "Account encryption round trip failed");
            Console.WriteLine("PASS: actual account encryption/decryption code with synthetic data.");
            Check(Marshal.SizeOf(typeof(Session)) == 32, "Rust session ABI size mismatch");
            Check(mizu_engine_abi_version() == 1, "Rust ABI version mismatch");
            Check(mizu_engine_set_config(IntPtr.Zero, 1, 0, 0, 0) == 0, "Rust null engine guard failed");
            Check(mizu_engine_sessions(IntPtr.Zero, null, 0) == 0, "Rust null session guard failed");
            IntPtr engine = mizu_engine_create();
            Check(engine != IntPtr.Zero, "Rust engine did not start");
            try
            {
                // Auto remains OFF: this check never changes any Roblox process.
                Check(mizu_engine_set_config(engine, double.NaN, 99, 0, 99) == 1, "Rust config validation failed");
                var rows = new Session[512];
                Check(mizu_engine_sessions(engine, rows, 512) <= 512, "Rust snapshot exceeded capacity");
            }
            finally { mizu_engine_destroy(engine); }
            Console.WriteLine("PASS: Rust DLL load, ABI, null guards, configuration, snapshot and shutdown.");
            Console.WriteLine("No account data was opened. Auto optimizer was not enabled.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception);
            return 1;
        }
    }
}
