using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace RBX_Alt_Manager.Classes
{
    internal static class RobloxWindowManager
    {
        private sealed class PendingLaunch
        {
            public Account Account;
            public HashSet<int> ExistingProcessIds;
            public int LauncherProcessId;
            public DateTime RegisteredUtc;
        }

        private sealed class ProcessAssignment
        {
            public Account Account;
            public long StartTicks;
        }

        private sealed class WindowCandidate
        {
            public int ProcessId;
            public IntPtr Handle;
            public string Title;
            public long StartTicks;
        }

        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
        private static readonly object launchLock = new object();
        private static readonly List<PendingLaunch> pendingLaunches = new List<PendingLaunch>();
        private static readonly Dictionary<int, ProcessAssignment> processAssignments =
            new Dictionary<int, ProcessAssignment>();

        internal sealed class Window
        {
            public int ProcessId;
            public IntPtr Handle;
            public bool Responsive;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowPlacement
        {
            public int Length, Flags, ShowCommand;
            public NativePoint MinPosition, MaxPosition;
            public Rect NormalPosition;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GuiThreadInfo
        {
            public uint Size, Flags;
            public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            public Rect CaretRect;
        }

        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoSendChanging = 0x0400;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowTextW", SetLastError = true)]
        private static extern bool SetWindowText(IntPtr handle, string title);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
        private static extern int GetWindowText(IntPtr handle, System.Text.StringBuilder title, int capacity);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr handle, uint command);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern bool IsZoomed(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr handle, int command);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr handle);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr handle, out Rect rect);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowPlacement(IntPtr handle, ref WindowPlacement placement);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPlacement(IntPtr handle, ref WindowPlacement placement);

        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr handle, uint message,
            IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out Rect rect, int size);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        internal static bool IsCurrent(Window window)
        {
            if (window == null || window.Handle == IntPtr.Zero || !IsWindow(window.Handle)) return false;
            GetWindowThreadProcessId(window.Handle, out uint pid);
            return pid == window.ProcessId;
        }

        // This probe runs in the background scan, never on the manager's UI thread.
        // A loading or hung Roblox window must not receive resize/title messages.
        internal static bool IsResponsive(Window window) => IsCurrent(window) &&
            SendMessageTimeout(window.Handle, 0, IntPtr.Zero, IntPtr.Zero,
                0x0001 | 0x0002 | 0x0020, 30, out _) != IntPtr.Zero;

        internal static void Focus(Window window)
        {
            if (!IsCurrent(window)) return;
            if (IsIconic(window.Handle)) ShowWindow(window.Handle, 9); // SW_RESTORE
            SetForegroundWindow(window.Handle);
        }

        internal static void Restore(Window window)
        {
            if (IsCurrent(window) && (IsIconic(window.Handle) || IsZoomed(window.Handle)))
                ShowWindow(window.Handle, 9); // SW_RESTORE
        }

        internal static bool IsMinimized(Window window) => IsCurrent(window) && IsIconic(window.Handle);

        internal static bool TryGetNativeMoveState(Window window, out bool moving)
        {
            moving = false;
            if (!IsCurrent(window)) return false;
            uint threadId = GetWindowThreadProcessId(window.Handle, out _);
            var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf(typeof(GuiThreadInfo)) };
            if (threadId == 0 || !GetGUIThreadInfo(threadId, ref info)) return false;
            moving = (info.Flags & 0x0002) != 0;
            return true;
        }

        internal static bool IsTitleMoveStart(Window window)
        {
            if (!TryGetRect(window, out Rect rect) || !GetCursorPos(out NativePoint cursor))
                return false;
            int x = cursor.X - rect.Left;
            int y = cursor.Y - rect.Top;
            return y >= 8 && y < 32 && x >= 16 &&
                x < rect.Right - rect.Left - 138;
        }

        internal static void PreserveRestoreSize(Window window, int width, int height)
        {
            if (!IsMinimized(window)) return;
            var placement = new WindowPlacement { Length = Marshal.SizeOf(typeof(WindowPlacement)) };
            if (!GetWindowPlacement(window.Handle, ref placement) ||
                (placement.Flags & 2) != 0) return; // Keep restore-to-maximized windows intact.
            if (placement.NormalPosition.Right - placement.NormalPosition.Left == width &&
                placement.NormalPosition.Bottom - placement.NormalPosition.Top == height) return;
            placement.NormalPosition.Right = placement.NormalPosition.Left + width;
            placement.NormalPosition.Bottom = placement.NormalPosition.Top + height;
            SetWindowPlacement(window.Handle, ref placement);
        }

        internal static void BringToFront(Window window)
        {
            if (IsCurrent(window))
                SetWindowPos(window.Handle, IntPtr.Zero, 0, 0, 0, 0,
                    SwpNoMove | 0x0001 | SwpNoActivate); // SWP_NOSIZE, HWND_TOP
        }

        internal static bool TryGetSize(Window window, out int width, out int height)
        {
            width = height = 0;
            if (!IsCurrent(window) || !GetWindowRect(window.Handle, out Rect rect)) return false;
            width = rect.Right - rect.Left;
            height = rect.Bottom - rect.Top;
            return width > 0 && height > 0;
        }

        internal static void ApplySize(Window window, int width, int height)
        {
            if (!IsCurrent(window) || IsIconic(window.Handle) || IsZoomed(window.Handle)) return;
            if (TryGetSize(window, out int currentWidth, out int currentHeight) &&
                currentWidth == width && currentHeight == height) return;

            // Skip WM_WINDOWPOSCHANGING, where the default procedure applies Roblox's
            // minimum tracking size. Keep the existing position so title-bar dragging works.
            SetWindowPos(window.Handle, IntPtr.Zero, 0, 0, width, height,
                SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpNoSendChanging);
        }

        internal static bool TrySetNativeMinimum(Window window, out int width, out int height)
        {
            width = height = 0;
            if (!IsCurrent(window)) return false;
            Restore(window);
            if (!IsResponsive(window) || !TryGetSize(window, out int oldWidth, out int oldHeight))
                return false;

            // Let Roblox handle WM_WINDOWPOSCHANGING so its own minimum size wins.
            if (!SetWindowPos(window.Handle, IntPtr.Zero, 0, 0, 160, 120,
                SwpNoMove | SwpNoZOrder | SwpNoActivate)) return false;
            if (TryGetSize(window, out width, out height) && width > 160 && height > 120)
                return true;

            // A window without a native limit should keep its previous size.
            ApplySize(window, oldWidth, oldHeight);
            return false;
        }

        internal static bool ApplyBounds(Window window, int x, int y, int width, int height)
        {
            if (!IsCurrent(window) || IsIconic(window.Handle) || IsZoomed(window.Handle)) return false;
            if (TryGetRect(window, out Rect current) && current.Left == x && current.Top == y &&
                current.Right - current.Left == width && current.Bottom - current.Top == height) return true;
            return SetWindowPos(window.Handle, IntPtr.Zero, x, y, width, height,
                SwpNoZOrder | SwpNoActivate | SwpNoSendChanging);
        }

        internal static bool Move(Window window, int x, int y)
        {
            if (!IsCurrent(window) || IsIconic(window.Handle) || IsZoomed(window.Handle)) return false;
            if (TryGetRect(window, out Rect current) && current.Left == x && current.Top == y) return true;
            return SetWindowPos(window.Handle, IntPtr.Zero, x, y, 0, 0,
                0x0001 | SwpNoZOrder | SwpNoActivate | SwpNoSendChanging); // SWP_NOSIZE
        }

        internal static bool MoveDuringDrag(Window window, int x, int y)
        {
            if (!IsCurrent(window) || IsIconic(window.Handle) || IsZoomed(window.Handle))
                return false;
            return SetWindowPos(window.Handle, IntPtr.Zero, x, y, 0, 0,
                0x0001 | SwpNoZOrder | SwpNoActivate | SwpNoSendChanging);
        }

        internal static void ApplyTitle(Window window, string username, bool showUsername, bool showSize)
        {
            if (!IsCurrent(window)) return;
            string title = showUsername && !string.IsNullOrWhiteSpace(username)
                ? username.Trim() : "Roblox";
            if (title.Length > 40) title = title.Substring(0, 40);
            if (showSize && TryGetSize(window, out int width, out int height))
                title += " " + width + "x" + height;
            var current = new System.Text.StringBuilder(64);
            GetWindowText(window.Handle, current, current.Capacity);
            if (!string.Equals(current.ToString(), title, StringComparison.Ordinal))
                SetWindowText(window.Handle, title);
        }

        internal static void ResetManagedTitle(Window window, string username)
        {
            ApplyTitle(window, username, false, false);
        }

        internal static bool TryGetRect(Window window, out Rect rect)
        {
            rect = default;
            return IsCurrent(window) && GetWindowRect(window.Handle, out rect);
        }

        internal static bool TryGetVisibleRect(Window window, out Rect rect)
        {
            rect = default;
            if (!IsCurrent(window)) return false;
            if (DwmGetWindowAttribute(window.Handle, 9, out rect, Marshal.SizeOf(typeof(Rect))) == 0 &&
                rect.Right > rect.Left && rect.Bottom > rect.Top) return true;
            return GetWindowRect(window.Handle, out rect);
        }

        internal static HashSet<int> CaptureProcessIds()
        {
            var ids = new HashSet<int>();
            foreach (Process process in Process.GetProcessesByName("RobloxPlayerBeta"))
            {
                try { ids.Add(process.Id); }
                finally { process.Dispose(); }
            }
            return ids;
        }

        internal static void RegisterLaunch(Account account, HashSet<int> existingProcessIds,
            int launcherProcessId, bool directPlayerProcess)
        {
            if (account == null) return;
            lock (launchLock)
            {
                pendingLaunches.RemoveAll(p => p.Account == account ||
                    (DateTime.UtcNow - p.RegisteredUtc).TotalMinutes > 3);
                pendingLaunches.Add(new PendingLaunch
                {
                    Account = account,
                    ExistingProcessIds = existingProcessIds ?? new HashSet<int>(),
                    LauncherProcessId = launcherProcessId,
                    RegisteredUtc = DateTime.UtcNow
                });
                if (directPlayerProcess && launcherProcessId > 0)
                    processAssignments[launcherProcessId] = new ProcessAssignment
                    {
                        Account = account,
                        StartTicks = 0
                    };
            }
        }

        private static long GetStartTicks(Process process)
        {
            try { return process.StartTime.ToUniversalTime().Ticks; }
            catch { return 0; }
        }

        private static Account MatchRecentLog(long processStartTicks, Dictionary<string, Account> byTracker)
        {
            if (processStartTicks == 0 || byTracker.Count == 0) return null;
            string logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "logs");
            if (!Directory.Exists(logDirectory)) return null;

            Account matched = null;
            foreach (string path in Directory.EnumerateFiles(logDirectory, "*_Player_*_last.log"))
            {
                string name = Path.GetFileName(path);
                if (name.IndexOf("CrashHandler", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                Match stamp = Regex.Match(name, @"_(\d{8}T\d{6}Z)_Player_");
                if (!stamp.Success || !DateTime.TryParseExact(stamp.Groups[1].Value,
                    "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTime createdUtc)) continue;
                double seconds = (createdUtc.Ticks - processStartTicks) / (double)TimeSpan.TicksPerSecond;
                if (seconds < -3 || seconds > 15) continue;

                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    {
                        byte[] buffer = new byte[(int)Math.Min(stream.Length, 65536L)];
                        int read = stream.Read(buffer, 0, buffer.Length);
                        Match tracker = Regex.Match(Encoding.UTF8.GetString(buffer, 0, read),
                            @"browsertrackerid[:=](\d+)", RegexOptions.IgnoreCase);
                        if (!tracker.Success || !byTracker.TryGetValue(tracker.Groups[1].Value,
                            out Account account)) continue;
                        if (matched != null && matched != account) return null;
                        matched = account;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return matched;
        }

        internal static bool IsManagedTitle(int processId, string title)
        {
            if (string.IsNullOrEmpty(title)) return false;
            lock (launchLock)
                return processAssignments.TryGetValue(processId, out ProcessAssignment assigned) &&
                    (string.Equals(assigned.Account?.Username, title, StringComparison.Ordinal) ||
                     Regex.IsMatch(title, @"^(?:" + Regex.Escape(assigned.Account?.Username ?? "") +
                         @"|Roblox) \d{2,4}x\d{2,4}$", RegexOptions.IgnoreCase));
        }

        internal static Dictionary<Account, List<Window>> Scan(IReadOnlyCollection<Account> accounts)
        {
            var results = new Dictionary<Account, List<Window>>();
            if (accounts == null || accounts.Count == 0) return results;

            var byTracker = accounts
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.BrowserTrackerID))
                .GroupBy(a => a.BrowserTrackerID)
                .Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var byTitle = accounts
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.Username))
                .GroupBy(a => a.Username, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var processes = new Dictionary<int, Process>();
            foreach (Process process in Process.GetProcessesByName("RobloxPlayerBeta"))
                processes[process.Id] = process;

            try
            {
                var commandLines = new Dictionary<int, string>();
                var parentIds = new Dictionary<int, int>();
                try
                {
                    using (var searcher = new ManagementObjectSearcher(
                        "SELECT ProcessId, ParentProcessId, CommandLine FROM Win32_Process WHERE Name = 'RobloxPlayerBeta.exe'"))
                    using (ManagementObjectCollection rows = searcher.Get())
                        foreach (ManagementObject row in rows)
                        {
                            int pid = Convert.ToInt32(row["ProcessId"]);
                            commandLines[pid] = row["CommandLine"] as string;
                            parentIds[pid] = Convert.ToInt32(row["ParentProcessId"]);
                        }
                }
                catch { /* Roblox may hide command lines; window discovery still works. */ }

                var candidates = new List<WindowCandidate>();
                EnumWindows((handle, parameter) =>
                {
                    try
                    {
                        GetWindowThreadProcessId(handle, out uint rawPid);
                        int pid = (int)rawPid;
                        if (!processes.TryGetValue(pid, out Process process) ||
                            !IsWindowVisible(handle) || GetWindow(handle, 4) != IntPtr.Zero ||
                            !GetWindowRect(handle, out Rect rect) ||
                            rect.Right - rect.Left < 160 || rect.Bottom - rect.Top < 120) return true;
                        var title = new System.Text.StringBuilder(128);
                        GetWindowText(handle, title, title.Capacity);
                        if (title.Length == 0) return true;
                        candidates.Add(new WindowCandidate
                        {
                            ProcessId = pid,
                            Handle = handle,
                            Title = title.ToString(),
                            StartTicks = GetStartTicks(process)
                        });
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);

                lock (launchLock)
                {
                    DateTime now = DateTime.UtcNow;
                    pendingLaunches.RemoveAll(p => (now - p.RegisteredUtc).TotalMinutes > 3);
                    foreach (int stale in processAssignments.Keys.Where(pid => !processes.ContainsKey(pid)).ToArray())
                        processAssignments.Remove(stale);

                    foreach (WindowCandidate candidate in candidates.OrderBy(c => c.StartTicks))
                    {
                        Account account = null;
                        if (commandLines.TryGetValue(candidate.ProcessId, out string commandLine) &&
                            !string.IsNullOrWhiteSpace(commandLine))
                        {
                            Match match = Regex.Match(commandLine,
                                @"(?:\-b\s+|browsertrackerid[:=])(\d+)", RegexOptions.IgnoreCase);
                            if (match.Success) byTracker.TryGetValue(match.Groups[1].Value, out account);
                        }

                        if (account == null && processAssignments.TryGetValue(candidate.ProcessId,
                            out ProcessAssignment assigned) &&
                            (assigned.StartTicks == 0 || candidate.StartTicks == 0 ||
                             assigned.StartTicks == candidate.StartTicks) &&
                            accounts.Contains(assigned.Account))
                            account = assigned.Account;

                        if (account == null)
                        {
                            string usernameTitle = Regex.Replace(candidate.Title,
                                @" \d{2,4}x\d{2,4}$", "");
                            byTitle.TryGetValue(usernameTitle, out account);
                        }

                        bool genericTitle = string.Equals(candidate.Title, "Roblox", StringComparison.OrdinalIgnoreCase) ||
                            Regex.IsMatch(candidate.Title, @"^Roblox \d{2,4}x\d{2,4}$", RegexOptions.IgnoreCase);
                        if (account == null && genericTitle)
                        {
                            parentIds.TryGetValue(candidate.ProcessId, out int parentId);
                            PendingLaunch pending = pendingLaunches
                                .Where(p => accounts.Contains(p.Account) &&
                                    !p.ExistingProcessIds.Contains(candidate.ProcessId) &&
                                    (candidate.StartTicks == 0 ||
                                     candidate.StartTicks >= p.RegisteredUtc.AddSeconds(-10).Ticks))
                                .OrderByDescending(p => p.LauncherProcessId > 0 &&
                                    (p.LauncherProcessId == candidate.ProcessId || p.LauncherProcessId == parentId))
                                .ThenBy(p => Math.Abs((candidate.StartTicks == 0
                                    ? now.Ticks : candidate.StartTicks) - p.RegisteredUtc.Ticks))
                                .FirstOrDefault();
                            if (pending != null)
                            {
                                account = pending.Account;
                                pendingLaunches.Remove(pending);
                            }
                        }

                        if (account == null && genericTitle)
                            account = MatchRecentLog(candidate.StartTicks, byTracker);

                        if (account == null) continue;
                        processAssignments[candidate.ProcessId] = new ProcessAssignment
                        {
                            Account = account,
                            StartTicks = candidate.StartTicks
                        };
                        var window = new Window
                        {
                            ProcessId = candidate.ProcessId,
                            Handle = candidate.Handle
                        };
                        if (!results.TryGetValue(account, out List<Window> windows))
                            results[account] = windows = new List<Window>();
                        windows.Add(window);
                    }
                }
                foreach (Window window in results.Values.SelectMany(windows => windows))
                    window.Responsive = IsResponsive(window);
            }
            catch (Exception exception) { Program.Logger.Error($"Roblox window scan failed: {exception.Message}"); }
            finally
            {
                foreach (Process process in processes.Values) process.Dispose();
            }

            return results;
        }
    }
}
