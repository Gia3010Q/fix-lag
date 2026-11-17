using System;
using System.IO;
using System.Runtime.InteropServices;

namespace RBX_Alt_Manager.Classes
{
    // Blittable bridge to the Rust working-set and CPU optimizer. The WinForms UI stays unchanged.
    internal sealed class RustOptimizerBackend : IDisposable
    {
        private const uint LoadLibrarySearchDllLoadDir = 0x00000100;
        private const uint LoadLibrarySearchDefaultDirs = 0x00001000;

        [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Session
        {
            public uint Pid;
            public uint Reserved;
            public ulong WorkingSetBytes;
            public double CpuPercent;
            public ulong StartedFiletime;
        }

        [DllImport("mizuauto_engine.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        private static extern uint mizu_engine_abi_version();

        [DllImport("mizuauto_engine.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        private static extern IntPtr mizu_engine_create();

        [DllImport("mizuauto_engine.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        private static extern void mizu_engine_destroy(IntPtr engine);

        [DllImport("mizuauto_engine.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        private static extern int mizu_engine_set_config(IntPtr engine, double intervalSeconds, uint cpuThreads, int autoEnabled, uint ramLimitMb);

        [DllImport("mizuauto_engine.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        private static extern uint mizu_engine_sessions(IntPtr engine, [Out] Session[] rows, uint capacity);

        private IntPtr engine;
        private IntPtr module;

        private RustOptimizerBackend(IntPtr value, IntPtr library) { engine = value; module = library; }

        internal static RustOptimizerBackend TryCreate(out string error)
        {
            error = null;
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mizuauto_engine.dll");
            if (!File.Exists(path))
            {
                error = "Thiếu mizuauto_engine.dll cạnh RobloxManager.exe";
                return null;
            }

            // Load the exact companion DLL, independent of the working directory.
            IntPtr library = LoadLibraryEx(path, IntPtr.Zero,
                LoadLibrarySearchDllLoadDir | LoadLibrarySearchDefaultDirs);
            if (library == IntPtr.Zero)
            {
                error = "Không nạp được Rust DLL (lỗi Windows " + Marshal.GetLastWin32Error() + ")";
                return null;
            }
            try
            {
                if (mizu_engine_abi_version() != 1)
                {
                    error = "Rust DLL không đúng phiên bản";
                    return null;
                }
                IntPtr value = mizu_engine_create();
                if (value == IntPtr.Zero)
                {
                    error = "Rust engine không khởi tạo được";
                    return null;
                }
                RustOptimizerBackend backend = new RustOptimizerBackend(value, library);
                library = IntPtr.Zero;
                return backend;
            }
            catch (DllNotFoundException) { error = "Thiếu Rust DLL hoặc thư viện phụ thuộc"; return null; }
            catch (BadImageFormatException) { error = "Rust DLL không đúng kiến trúc x64"; return null; }
            catch (EntryPointNotFoundException) { error = "Rust DLL thiếu hàm cần thiết"; return null; }
            finally { if (library != IntPtr.Zero) FreeLibrary(library); }
        }

        internal bool Configure(double seconds, int threads, bool enabled, int ramLimitMb)
        {
            return engine != IntPtr.Zero && mizu_engine_set_config(
                engine,
                seconds,
                (uint)Math.Max(0, threads),
                enabled ? 1 : 0,
                (uint)Math.Max(0, ramLimitMb)) != 0;
        }

        internal Session[] Sessions()
        {
            if (engine == IntPtr.Zero) return Array.Empty<Session>();
            Session[] rows = new Session[512];
            int count = (int)Math.Min((uint)rows.Length, mizu_engine_sessions(engine, rows, (uint)rows.Length));
            Array.Resize(ref rows, count);
            return rows;
        }

        public void Dispose()
        {
            if (engine != IntPtr.Zero)
            {
                mizu_engine_destroy(engine);
                engine = IntPtr.Zero;
            }
            if (module != IntPtr.Zero)
            {
                FreeLibrary(module);
                module = IntPtr.Zero;
            }
        }
    }
}
