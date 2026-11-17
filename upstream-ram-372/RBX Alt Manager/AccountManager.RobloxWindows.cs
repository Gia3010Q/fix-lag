using BrightIdeasSoftware;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Forms;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RBX_Alt_Manager
{
    public partial class AccountManager
    {
        private sealed class RobloxSizeRepair
        {
            public bool Running;
            public bool PendingImmediate;
            public int Failures;
            public DateTime NextAttemptUtc;
        }

        private sealed class ManagedRobloxWindow
        {
            public Account Account;
            public RobloxWindowManager.Window Window;
        }

        private sealed class NativeRobloxMove
        {
            public long StartedTick;
            public bool TitleMove;
        }

        private const string RobloxWidthField = "Mizu_Window_Width";
        private const string RobloxHeightField = "Mizu_Window_Height";
        private const uint EventObjectLocationChange = 0x800B;
        private const uint EventMoveSizeStart = 0x000A;
        private const uint EventMoveSizeEnd = 0x000B;
        private const uint WineventOutOfContext = 0;
        private const uint WineventSkipOwnProcess = 2;

        private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr window,
            int objectId, int childId, uint threadId, uint eventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
            WinEventDelegate callback, uint processId, uint threadId, uint flags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hook);

        private readonly Timer robloxWindowsTimer = new Timer { Interval = 3000 };
        private readonly Timer robloxBorderTimer = new Timer { Interval = 100 };
        private readonly Dictionary<Account, List<RobloxWindowManager.Window>> robloxWindows =
            new Dictionary<Account, List<RobloxWindowManager.Window>>();
        private readonly Dictionary<IntPtr, ManagedRobloxWindow> robloxWindowByHandle =
            new Dictionary<IntPtr, ManagedRobloxWindow>();
        private readonly HashSet<IntPtr> pendingRobloxSizeChecks = new HashSet<IntPtr>();
        private readonly Dictionary<IntPtr, Size> desiredRobloxSizes = new Dictionary<IntPtr, Size>();
        private readonly Dictionary<IntPtr, int> desiredRobloxProcessIds = new Dictionary<IntPtr, int>();
        private readonly ConcurrentDictionary<IntPtr, int> desiredRobloxSizeVersions =
            new ConcurrentDictionary<IntPtr, int>();
        private readonly ConcurrentDictionary<IntPtr, RobloxSizeRepair> robloxSizeRepairs =
            new ConcurrentDictionary<IntPtr, RobloxSizeRepair>();
        private readonly ConcurrentDictionary<IntPtr, RobloxRestoreWatcher.Target> restoreTargets =
            new ConcurrentDictionary<IntPtr, RobloxRestoreWatcher.Target>();
        private readonly ConcurrentDictionary<IntPtr, byte> activeRobloxDrags =
            new ConcurrentDictionary<IntPtr, byte>();
        private readonly ConcurrentDictionary<IntPtr, byte> readyRobloxWindows =
            new ConcurrentDictionary<IntPtr, byte>();
        private readonly ConcurrentDictionary<IntPtr, NativeRobloxMove> nativeRobloxMoves =
            new ConcurrentDictionary<IntPtr, NativeRobloxMove>();
        private OLVColumn openRobloxColumn;
        private ToolStripMenuItem robloxSizeMenu;
        private ToolStripMenuItem robloxSizeResetMenu;
        private RobloxResizeGrip robloxResizeGrip;
        private RobloxRestoreWatcher restoreWatcher;
        private WinEventDelegate robloxMoveCallback;
        private WinEventDelegate robloxSystemCallback;
        private IntPtr robloxMoveHook;
        private IntPtr robloxNativeMoveHook;
        private bool robloxScanBusy;
        private volatile bool robloxSizeBusy;
        private int nextRobloxSizeVersion;

        private void InitializeRobloxWindowControls()
        {
            openRobloxColumn = new OLVColumn
            {
                Text = "Status",
                Width = 48,
                IsEditable = false,
                Sortable = false,
                TextAlign = HorizontalAlignment.Center,
                AspectGetter = row => General.Get<bool>("ShowRobloxWindowStatus") &&
                    row is Account account && HasRobloxWindow(account) ? "▶" : ""
            };
            AccountsView.AllColumns.Insert(0, openRobloxColumn);
            AccountsView.Columns.Insert(0, openRobloxColumn);
            AccountsView.CellClick += (sender, args) =>
            {
                if (args.Column != openRobloxColumn || !General.Get<bool>("ShowRobloxWindowStatus") ||
                    !(args.Model is Account account)) return;
                if (TryGetRobloxWindow(account, out RobloxWindowManager.Window window))
                    RobloxWindowManager.Focus(window);
            };

            robloxSizeMenu = new ToolStripMenuItem("Roblox window size...");
            robloxSizeMenu.Click += (sender, args) => ShowRobloxSizeDialog(SelectedAccount);
            robloxSizeResetMenu = new ToolStripMenuItem("Reset custom window size");
            robloxSizeResetMenu.Click += (sender, args) =>
            {
                if (SelectedAccount == null) return;
                SelectedAccount.Fields.Remove(RobloxWidthField);
                SelectedAccount.Fields.Remove(RobloxHeightField);
                if (robloxWindows.TryGetValue(SelectedAccount, out List<RobloxWindowManager.Window> activeWindows))
                    foreach (RobloxWindowManager.Window active in activeWindows)
                    {
                        desiredRobloxSizeVersions.TryRemove(active.Handle, out _);
                        desiredRobloxSizes.Remove(active.Handle);
                        desiredRobloxProcessIds.Remove(active.Handle);
                        robloxSizeRepairs.TryRemove(active.Handle, out _);
                        restoreTargets.TryRemove(active.Handle, out _);
                    }
                SaveAccounts();
            };
            AccountsStrip.Items.Insert(0, new ToolStripSeparator());
            AccountsStrip.Items.Insert(0, robloxSizeResetMenu);
            AccountsStrip.Items.Insert(0, robloxSizeMenu);
            AccountsStrip.Opening += (sender, args) =>
            {
                Account account = AccountsView.SelectedObject as Account;
                bool canResize = General.Get<bool>("EnableSmallRobloxWindows") &&
                    account != null && HasRobloxWindow(account);
                robloxSizeMenu.Enabled = canResize;
                robloxSizeResetMenu.Enabled = General.Get<bool>("EnableSmallRobloxWindows") &&
                    account != null && TryGetRobloxSize(account, out _, out _);
            };
            AccountsView.MouseDown += (sender, args) =>
            {
                if (args.Button != MouseButtons.Right) return;
                OLVListItem item = AccountsView.GetItemAt(args.X, args.Y) as OLVListItem;
                if (item != null) AccountsView.SelectedObject = item.RowObject;
            };

            robloxWindowsTimer.Tick += async (sender, args) => await ScanRobloxWindows();
            robloxBorderTimer.Tick += (sender, args) =>
            {
                UpdateRobloxBorder();
                KeepRobloxWindowSize();
                FlushRobloxSizeChecks();
            };
            Shown += (sender, args) =>
            {
                restoreWatcher = new RobloxRestoreWatcher(
                    handle => restoreTargets.TryGetValue(handle, out RobloxRestoreWatcher.Target target)
                        ? target : null,
                    handle => readyRobloxWindows.ContainsKey(handle) && !robloxSizeBusy &&
                        !activeRobloxDrags.ContainsKey(handle),
                    handle =>
                    {
                        if (!activeRobloxDrags.TryRemove(handle, out _) ||
                            IsDisposed || !IsHandleCreated) return;
                        BeginInvoke((Action)(() =>
                        {
                            if (robloxResizeGrip?.TargetHandle == handle)
                                robloxResizeGrip.CancelForRestore();
                        }));
                    });
                restoreWatcher.Start();
                robloxMoveCallback = OnRobloxWindowMoved;
                robloxMoveHook = SetWinEventHook(EventObjectLocationChange, EventObjectLocationChange,
                    IntPtr.Zero, robloxMoveCallback, 0, 0, WineventOutOfContext | WineventSkipOwnProcess);
                robloxSystemCallback = OnRobloxSystemEvent;
                robloxNativeMoveHook = SetWinEventHook(EventMoveSizeStart, EventMoveSizeEnd,
                    IntPtr.Zero, robloxSystemCallback, 0, 0, WineventOutOfContext | WineventSkipOwnProcess);
                robloxWindowsTimer.Start();
                robloxBorderTimer.Start();
                _ = ScanRobloxWindows();
            };
            FormClosed += (sender, args) =>
            {
                robloxWindowsTimer.Stop();
                robloxWindowsTimer.Dispose();
                robloxBorderTimer.Stop();
                robloxBorderTimer.Dispose();
                robloxResizeGrip?.Close();
                restoreWatcher?.Dispose();
                restoreWatcher = null;
                if (robloxMoveHook != IntPtr.Zero) UnhookWinEvent(robloxMoveHook);
                if (robloxNativeMoveHook != IntPtr.Zero) UnhookWinEvent(robloxNativeMoveHook);
                robloxMoveCallback = null;
                robloxSystemCallback = null;
                pendingRobloxSizeChecks.Clear();
                desiredRobloxSizeVersions.Clear();
                readyRobloxWindows.Clear();
                restoreTargets.Clear();
                nativeRobloxMoves.Clear();
            };
            ApplyRobloxFeatureSettings();
        }

        private bool HasRobloxWindow(Account account) => TryGetRobloxWindow(account, out _);

        public void ApplyRobloxFeatureSettings()
        {
            if (IsDisposed || openRobloxColumn == null) return;
            openRobloxColumn.Width = General.Get<bool>("ShowRobloxWindowStatus") ? 48 : 0;
            if (!General.Get<bool>("EnableSmallRobloxWindows"))
            {
                robloxResizeGrip?.Close();
                robloxResizeGrip = null;
            }
            else UpdateRobloxBorder();
            if (!General.Get<bool>("EnableSmallRobloxWindows") && !General.Get<bool>("AutoSizeRobloxWindows"))
            {
                desiredRobloxSizeVersions.Clear();
                desiredRobloxSizes.Clear();
                desiredRobloxProcessIds.Clear();
                robloxSizeRepairs.Clear();
                restoreTargets.Clear();
            }
            foreach (var pair in robloxWindows)
                foreach (RobloxWindowManager.Window window in pair.Value)
                    ApplyRobloxTitle(pair.Key, window);
            AccountsView.Refresh();
        }

        private void ApplyRobloxTitle(Account account, RobloxWindowManager.Window window)
        {
            if (readyRobloxWindows.ContainsKey(window.Handle) && window.Responsive &&
                restoreWatcher?.IsRestoring(window.Handle) != true)
                RobloxWindowManager.ApplyTitle(window, account.Username,
                    General.Get<bool>("RenameRobloxWindows"),
                    General.Get<bool>("ShowRobloxWindowSizeInTitle"));
        }

        private bool TryGetRobloxWindow(Account account, out RobloxWindowManager.Window window)
        {
            window = null;
            if (account == null || !robloxWindows.TryGetValue(account, out List<RobloxWindowManager.Window> windows))
                return false;
            window = windows.LastOrDefault(RobloxWindowManager.IsCurrent);
            return window != null;
        }

        private static bool TryGetRobloxSize(Account account, out int width, out int height)
        {
            width = height = 0;
            return account?.Fields != null &&
                int.TryParse(account.GetField(RobloxWidthField), out width) &&
                int.TryParse(account.GetField(RobloxHeightField), out height) &&
                width >= 160 && height >= 120 && width <= 4096 && height <= 4096;
        }

        private bool TryGetDesiredRobloxSize(Account account, RobloxWindowManager.Window window, out Size size)
        {
            if (desiredRobloxSizes.TryGetValue(window.Handle, out size))
            {
                if (desiredRobloxProcessIds.TryGetValue(window.Handle, out int processId) &&
                    processId == window.ProcessId) return true;
                desiredRobloxSizeVersions.TryRemove(window.Handle, out _);
                desiredRobloxSizes.Remove(window.Handle);
                desiredRobloxProcessIds.Remove(window.Handle);
                robloxSizeRepairs.TryRemove(window.Handle, out _);
                restoreTargets.TryRemove(window.Handle, out _);
            }
            if (General.Get<bool>("AutoSizeRobloxWindows"))
                size = new Size(
                    Math.Max(160, Math.Min(4096, General.Get<int>("AutoRobloxWindowWidth"))),
                    Math.Max(120, Math.Min(4096, General.Get<int>("AutoRobloxWindowHeight"))));
            else if (General.Get<bool>("EnableSmallRobloxWindows") &&
                TryGetRobloxSize(account, out int width, out int height))
                size = new Size(width, height);
            else return false;
            SetDesiredRobloxSize(window, size);
            return true;
        }

        private void SetDesiredRobloxSize(RobloxWindowManager.Window window, Size size)
        {
            if (!RobloxWindowManager.IsCurrent(window)) return;
            if (desiredRobloxSizes.TryGetValue(window.Handle, out Size previous) && previous == size &&
                desiredRobloxProcessIds.TryGetValue(window.Handle, out int processId) &&
                processId == window.ProcessId)
                return;
            desiredRobloxSizeVersions[window.Handle] =
                System.Threading.Interlocked.Increment(ref nextRobloxSizeVersion);
            desiredRobloxSizes[window.Handle] = size;
            desiredRobloxProcessIds[window.Handle] = window.ProcessId;
            restoreTargets[window.Handle] = new RobloxRestoreWatcher.Target(window, size);
            if (robloxSizeRepairs.TryGetValue(window.Handle, out RobloxSizeRepair repair))
                lock (repair)
                {
                    repair.Failures = 0;
                    repair.NextAttemptUtc = DateTime.MinValue;
                }
        }

        private async Task ScanRobloxWindows()
        {
            if (robloxScanBusy || IsDisposed || AccountsList == null) return;
            robloxScanBusy = true;
            try
            {
                Account[] accounts = AccountsList.ToArray();
                var found = await Task.Run(() => RobloxWindowManager.Scan(accounts));
                if (IsDisposed || !IsHandleCreated) return;

                Account[] changed = accounts.Where(account =>
                {
                    robloxWindows.TryGetValue(account, out List<RobloxWindowManager.Window> oldWindows);
                    found.TryGetValue(account, out List<RobloxWindowManager.Window> newWindows);
                    return !SameWindows(oldWindows, newWindows);
                }).ToArray();

                var previousWindows = robloxWindowByHandle.ToDictionary(
                    pair => pair.Key, pair => pair.Value.Window);
                robloxWindows.Clear();
                robloxWindowByHandle.Clear();
                foreach (var pair in found)
                {
                    robloxWindows[pair.Key] = pair.Value;
                    foreach (RobloxWindowManager.Window window in pair.Value)
                    {
                        robloxWindowByHandle[window.Handle] = new ManagedRobloxWindow
                        {
                            Account = pair.Key,
                            Window = window
                        };
                        bool sameWindow = previousWindows.TryGetValue(window.Handle,
                            out RobloxWindowManager.Window previous) &&
                            previous.ProcessId == window.ProcessId;
                        if (!sameWindow) readyRobloxWindows.TryRemove(window.Handle, out _);
                        else if (window.Responsive && previous.Responsive)
                            readyRobloxWindows.TryAdd(window.Handle, 0);
                    }
                }
                var activeHandles = new HashSet<IntPtr>(found.Values.SelectMany(list => list).Select(window => window.Handle));
                pendingRobloxSizeChecks.RemoveWhere(handle => !activeHandles.Contains(handle));
                foreach (IntPtr stale in readyRobloxWindows.Keys.Where(handle => !activeHandles.Contains(handle)))
                    readyRobloxWindows.TryRemove(stale, out _);
                foreach (IntPtr stale in desiredRobloxSizes.Keys.Where(handle => !activeHandles.Contains(handle)).ToArray())
                {
                    desiredRobloxSizeVersions.TryRemove(stale, out _);
                    desiredRobloxSizes.Remove(stale);
                    desiredRobloxProcessIds.Remove(stale);
                    robloxSizeRepairs.TryRemove(stale, out _);
                    restoreTargets.TryRemove(stale, out _);
                }
                foreach (IntPtr stale in nativeRobloxMoves.Keys.Where(handle => !activeHandles.Contains(handle)))
                    nativeRobloxMoves.TryRemove(stale, out _);
                foreach (var pair in robloxWindows)
                    foreach (RobloxWindowManager.Window window in pair.Value)
                    {
                        if (readyRobloxWindows.ContainsKey(window.Handle) &&
                            !activeRobloxDrags.ContainsKey(window.Handle) &&
                            !nativeRobloxMoves.ContainsKey(window.Handle) &&
                            TryGetDesiredRobloxSize(pair.Key, window, out Size desired))
                        {
                            if (!RobloxWindowManager.IsMinimized(window))
                                QueueRobloxSizeFix(window, desired);
                        }
                        if (!nativeRobloxMoves.ContainsKey(window.Handle) &&
                            !activeRobloxDrags.ContainsKey(window.Handle))
                            ApplyRobloxTitle(pair.Key, window);
                    }

                foreach (Account account in changed)
                    AccountsView.RefreshObject(account);
                UpdateRobloxBorder();
            }
            catch (Exception exception)
            {
                Program.Logger.Error($"Roblox window display failed: {exception.Message}");
            }
            finally { robloxScanBusy = false; }
        }

        private static bool SameWindows(List<RobloxWindowManager.Window> first,
            List<RobloxWindowManager.Window> second)
        {
            if (first == null || first.Count == 0) return second == null || second.Count == 0;
            if (second == null || first.Count != second.Count) return false;
            return first.All(a => second.Any(b => a.ProcessId == b.ProcessId && a.Handle == b.Handle));
        }

        private void OnRobloxWindowMoved(IntPtr hook, uint eventType, IntPtr window,
            int objectId, int childId, uint threadId, uint eventTime)
        {
            if (robloxSizeBusy || objectId != 0 || childId != 0 || IsDisposed ||
                !readyRobloxWindows.ContainsKey(window) ||
                activeRobloxDrags.ContainsKey(window) || nativeRobloxMoves.ContainsKey(window)) return;
            if (robloxWindowByHandle.ContainsKey(window))
                pendingRobloxSizeChecks.Add(window);
        }

        private void FlushRobloxSizeChecks()
        {
            if (pendingRobloxSizeChecks.Count == 0 || robloxSizeBusy) return;
            IntPtr[] pending = pendingRobloxSizeChecks.ToArray();
            pendingRobloxSizeChecks.Clear();
            foreach (IntPtr handle in pending)
                if (!nativeRobloxMoves.ContainsKey(handle) &&
                    !activeRobloxDrags.ContainsKey(handle) &&
                    robloxWindowByHandle.TryGetValue(handle, out ManagedRobloxWindow tracked) &&
                    TryGetDesiredRobloxSize(tracked.Account, tracked.Window, out Size desired))
                    QueueRobloxSizeFix(tracked.Window, desired);
        }

        private void OnRobloxSystemEvent(IntPtr hook, uint eventType, IntPtr window,
            int objectId, int childId, uint threadId, uint eventTime)
        {
            if (IsDisposed || objectId != 0 || childId != 0 ||
                !readyRobloxWindows.ContainsKey(window)) return;
            if (!robloxWindowByHandle.TryGetValue(window, out ManagedRobloxWindow tracked) ||
                !TryGetDesiredRobloxSize(tracked.Account, tracked.Window, out Size desired)) return;
            RobloxWindowManager.Window target = tracked.Window;
            if (eventType == EventMoveSizeStart)
            {
                var move = new NativeRobloxMove
                {
                    StartedTick = Stopwatch.GetTimestamp(),
                    TitleMove = RobloxWindowManager.IsTitleMoveStart(target)
                };
                nativeRobloxMoves[window] = move;
                if (robloxResizeGrip?.TargetHandle == window && !robloxResizeGrip.IsDragging)
                    robloxResizeGrip.SuspendForNativeMove(true);
                if (move.TitleMove)
                    _ = Task.Run(() => KeepNativeTitleMoveSize(target, move));
            }
            else if (eventType == EventMoveSizeEnd)
            {
                nativeRobloxMoves.TryRemove(window, out _);
                if (robloxResizeGrip?.TargetHandle == window)
                    robloxResizeGrip.SuspendForNativeMove(false);
                QueueRobloxSizeFix(target, desired, true);
            }
        }

        private void KeepNativeTitleMoveSize(RobloxWindowManager.Window window,
            NativeRobloxMove move)
        {
            var elapsed = Stopwatch.StartNew();
            try
            {
                while (!IsDisposed && elapsed.ElapsedMilliseconds < 15000 &&
                    nativeRobloxMoves.TryGetValue(window.Handle, out NativeRobloxMove current) &&
                    ReferenceEquals(current, move) && RobloxWindowManager.IsCurrent(window))
                {
                    if (!RobloxWindowManager.TryGetNativeMoveState(window, out bool moving))
                        break;
                    if (!moving)
                    {
                        if (elapsed.ElapsedMilliseconds >= 200) break;
                        System.Threading.Thread.Sleep(10);
                        continue;
                    }
                    if (!robloxSizeBusy && !activeRobloxDrags.ContainsKey(window.Handle) &&
                        restoreTargets.TryGetValue(window.Handle,
                            out RobloxRestoreWatcher.Target desired) &&
                        desired.Window.ProcessId == window.ProcessId &&
                        RobloxWindowManager.TryGetSize(window, out int width, out int height) &&
                        (width != desired.Size.Width || height != desired.Size.Height) &&
                        RobloxWindowManager.IsResponsive(window))
                        RobloxWindowManager.ApplySize(window,
                            desired.Size.Width, desired.Size.Height);
                    System.Threading.Thread.Sleep(20);
                }
            }
            catch { /* Roblox may close while the native move loop is active. */ }
        }

        private void KeepRobloxWindowSize()
        {
            if (IsDisposed) return;
            bool gripDragging = robloxResizeGrip?.IsDragging == true;
            IntPtr focused = gripDragging ? robloxResizeGrip.TargetHandle :
                RobloxWindowManager.GetForegroundWindow();
            if (!robloxWindowByHandle.TryGetValue(focused, out ManagedRobloxWindow tracked)) return;
            RobloxWindowManager.Window window = tracked.Window;
            if (nativeRobloxMoves.TryGetValue(window.Handle, out NativeRobloxMove nativeMove))
            {
                bool expired = (Stopwatch.GetTimestamp() - nativeMove.StartedTick) * 1000 /
                    Stopwatch.Frequency > 500;
                if (!expired || !RobloxWindowManager.TryGetNativeMoveState(window,
                    out bool moving) || moving) return;
                nativeRobloxMoves.TryRemove(window.Handle, out _);
                if (robloxResizeGrip?.TargetHandle == window.Handle)
                    robloxResizeGrip.SuspendForNativeMove(false);
            }
            if (activeRobloxDrags.ContainsKey(window.Handle)) return;
            ApplyRobloxTitle(tracked.Account, window);
            if (!robloxSizeBusy && TryGetDesiredRobloxSize(tracked.Account, window, out Size desired))
                QueueRobloxSizeFix(window, desired);
        }

        private void QueueRobloxSizeFix(RobloxWindowManager.Window window, Size desired,
            bool immediate = false)
        {
            if (IsDisposed || Disposing || !RobloxWindowManager.IsCurrent(window) ||
                !readyRobloxWindows.ContainsKey(window.Handle) ||
                restoreWatcher?.IsRestoring(window.Handle) == true ||
                nativeRobloxMoves.ContainsKey(window.Handle) ||
                activeRobloxDrags.ContainsKey(window.Handle)) return;
            if (RobloxWindowManager.IsMinimized(window))
                return;
            if (!desiredRobloxSizeVersions.TryGetValue(window.Handle, out int version)) return;
            if (RobloxWindowManager.TryGetSize(window, out int currentWidth, out int currentHeight) &&
                currentWidth == desired.Width && currentHeight == desired.Height)
            {
                if (robloxSizeRepairs.TryGetValue(window.Handle, out RobloxSizeRepair settled))
                    lock (settled)
                    {
                        settled.Failures = 0;
                        settled.NextAttemptUtc = DateTime.MinValue;
                    }
                return;
            }

            RobloxSizeRepair repair = robloxSizeRepairs.GetOrAdd(window.Handle,
                handle => new RobloxSizeRepair());
            lock (repair)
            {
                if (immediate)
                {
                    repair.NextAttemptUtc = DateTime.MinValue;
                    if (repair.Running) repair.PendingImmediate = true;
                }
                if (repair.Running || DateTime.UtcNow < repair.NextAttemptUtc) return;
                repair.Running = true;
            }
            Task.Run(() =>
            {
                bool attempted = false;
                bool matched = false;
                try
                {
                    if (desiredRobloxSizeVersions.TryGetValue(window.Handle, out int currentVersion) &&
                        currentVersion == version &&
                        readyRobloxWindows.ContainsKey(window.Handle) &&
                        !nativeRobloxMoves.ContainsKey(window.Handle) &&
                        !activeRobloxDrags.ContainsKey(window.Handle))
                    {
                        attempted = true;
                        if (RobloxWindowManager.IsResponsive(window))
                        {
                            RobloxWindowManager.ApplySize(window, desired.Width, desired.Height);
                            matched = RobloxWindowManager.TryGetSize(window, out int width, out int height) &&
                                width == desired.Width && height == desired.Height;
                        }
                    }
                }
                catch { /* A closing or hung Roblox window must not fault the UI. */ }
                finally
                {
                    lock (repair)
                    {
                        repair.Running = false;
                        if (attempted && desiredRobloxSizeVersions.TryGetValue(window.Handle,
                            out int latestVersion) && latestVersion == version)
                        {
                            repair.Failures = matched ? 0 : Math.Min(repair.Failures + 1, 5);
                            int pause = matched ? 80 : Math.Min(1000, 80 << repair.Failures);
                            repair.NextAttemptUtc = DateTime.UtcNow.AddMilliseconds(pause);
                        }
                        if (repair.PendingImmediate)
                        {
                            repair.PendingImmediate = false;
                            repair.NextAttemptUtc = DateTime.MinValue;
                        }
                    }
                }
            });
        }

        private void ShowRobloxSizeDialog(Account account)
        {
            if (!General.Get<bool>("EnableSmallRobloxWindows")) return;
            if (!TryGetRobloxWindow(account, out RobloxWindowManager.Window window)) return;
            RobloxWindowManager.TryGetSize(window, out int currentWidth, out int currentHeight);
            if (currentWidth <= 0) currentWidth = 800;
            if (currentHeight <= 0) currentHeight = 600;
            if (TryGetRobloxSize(account, out int savedWidth, out int savedHeight))
            {
                currentWidth = savedWidth;
                currentHeight = savedHeight;
            }

            using (var dialog = new Form
            {
                Text = "Roblox window size · " + account.Username,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(286, 126),
                BackColor = ThemeEditor.FormsBackground,
                ForeColor = ThemeEditor.FormsForeground
            })
            {
                var widthLabel = new Label { Text = "Width", Location = new Point(15, 18), AutoSize = true };
                var heightLabel = new Label { Text = "Height", Location = new Point(151, 18), AutoSize = true };
                var widthBox = new NumericUpDown { Minimum = 160, Maximum = 4096, Value = Math.Max(160, Math.Min(4096, currentWidth)), Location = new Point(15, 42), Width = 119 };
                var heightBox = new NumericUpDown { Minimum = 120, Maximum = 4096, Value = Math.Max(120, Math.Min(4096, currentHeight)), Location = new Point(151, 42), Width = 119 };
                var apply = new Button { Text = "Apply", DialogResult = DialogResult.OK, Location = new Point(169, 83), Size = new Size(101, 27) };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(57, 83), Size = new Size(101, 27) };
                dialog.Controls.AddRange(new Control[] { widthLabel, heightLabel, widthBox, heightBox, apply, cancel });
                dialog.AcceptButton = apply;
                dialog.CancelButton = cancel;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                account.Fields[RobloxWidthField] = widthBox.Value.ToString();
                account.Fields[RobloxHeightField] = heightBox.Value.ToString();
                SaveAccounts();
                if (robloxWindows.TryGetValue(account, out List<RobloxWindowManager.Window> windows))
                    foreach (RobloxWindowManager.Window active in windows)
                    {
                        var selectedSize = new Size((int)widthBox.Value, (int)heightBox.Value);
                        SetDesiredRobloxSize(active, selectedSize);
                        QueueRobloxSizeFix(active, selectedSize, true);
                    }
            }
        }

        public async Task ArrangeRobloxWindows(bool cascade)
        {
            if (IsDisposed) return;
            await ScanRobloxWindows();
            if (IsDisposed) return;

            var windows = robloxWindows.Values.SelectMany(list => list)
                .Where(RobloxWindowManager.IsCurrent)
                .GroupBy(window => window.Handle)
                .Select(group => group.First())
                .OrderBy(window => window.ProcessId)
                .ToList();
            if (windows.Count == 0) return;

            Rectangle area = Screen.FromControl(this).WorkingArea;
            robloxSizeBusy = true;
            try
            {
                int nextX = area.Left + 6;
                int nextY = area.Top + 6;
                int rowHeight = 0;
                for (int index = 0; index < windows.Count; index++)
                {
                    RobloxWindowManager.Window window = windows[index];
                    RobloxWindowManager.Restore(window);
                    if (!RobloxWindowManager.TryGetSize(window, out int width, out int height)) continue;
                    int x, y;
                    if (cascade)
                    {
                        x = Math.Min(area.Left + 6 + index * 32,
                            Math.Max(area.Left + 6, area.Right - width - 6));
                        y = Math.Min(area.Top + 6 + index * 30,
                            Math.Max(area.Top + 6, area.Bottom - height - 6));
                    }
                    else
                    {
                        if (nextX > area.Left + 6 && nextX + width > area.Right - 6)
                        {
                            nextX = area.Left + 6;
                            nextY += rowHeight + 6;
                            rowHeight = 0;
                        }
                        if (nextY + height > area.Bottom - 6)
                        {
                            // Preserve size even when all windows cannot fit without overlap.
                            nextX = area.Left + 6 + Math.Min(index * 24,
                                Math.Max(0, area.Width - width - 12));
                            nextY = area.Top + 6 + Math.Min(index * 24,
                                Math.Max(0, area.Height - height - 12));
                            rowHeight = 0;
                        }
                        x = nextX;
                        y = nextY;
                        nextX += width + 6;
                        rowHeight = Math.Max(rowHeight, height);
                    }
                    RobloxWindowManager.Move(window, x, y);
                    if (cascade) RobloxWindowManager.BringToFront(window);
                }
            }
            finally { robloxSizeBusy = false; }
            UpdateRobloxBorder();
        }

        public async Task ResetAllRobloxWindowsToNativeMinimum()
        {
            if (IsDisposed || robloxSizeBusy) return;
            await ScanRobloxWindows();
            if (IsDisposed || robloxSizeBusy) return;

            var windows = robloxWindows.Values.SelectMany(list => list)
                .Where(RobloxWindowManager.IsCurrent)
                .GroupBy(window => window.Handle)
                .Select(group => group.First())
                .ToList();
            robloxSizeBusy = true;
            try
            {
                var nativeSizes = await Task.Run(() =>
                {
                    var result = new List<KeyValuePair<RobloxWindowManager.Window, Size>>();
                    foreach (RobloxWindowManager.Window window in windows)
                        if (RobloxWindowManager.TrySetNativeMinimum(window,
                            out int width, out int height))
                            result.Add(new KeyValuePair<RobloxWindowManager.Window, Size>(
                                window, new Size(width, height)));
                    return result;
                });
                if (IsDisposed) return;
                foreach (var pair in nativeSizes)
                    SetDesiredRobloxSize(pair.Key, pair.Value);
            }
            finally { robloxSizeBusy = false; }
            if (!IsDisposed) UpdateRobloxBorder();
        }

        private void UpdateRobloxBorder()
        {
            if (IsDisposed || robloxResizeGrip?.IsDragging == true) return;
            if (robloxResizeGrip?.IsDisposed == true) robloxResizeGrip = null;
            IntPtr focused = General.Get<bool>("EnableSmallRobloxWindows")
                ? RobloxWindowManager.GetForegroundWindow() : IntPtr.Zero;
            if (robloxResizeGrip != null && robloxResizeGrip.TargetHandle == focused &&
                readyRobloxWindows.ContainsKey(focused)) return;
            robloxResizeGrip?.Close();
            robloxResizeGrip = null;
            if (focused == IntPtr.Zero) return;
            if (!robloxWindowByHandle.TryGetValue(focused, out ManagedRobloxWindow tracked) ||
                !readyRobloxWindows.ContainsKey(focused) ||
                !RobloxWindowManager.IsCurrent(tracked.Window)) return;
            RobloxWindowManager.Window window = tracked.Window;
            if (TryGetDesiredRobloxSize(tracked.Account, window, out Size desired) &&
                RobloxWindowManager.TryGetSize(window, out int actualWidth, out int actualHeight) &&
                (actualWidth != desired.Width || actualHeight != desired.Height)) return;
            robloxResizeGrip = new RobloxResizeGrip(tracked.Account, window,
                newSize =>
                {
                    SetDesiredRobloxSize(window, newSize);
                    ApplyRobloxTitle(tracked.Account, window);
                    SaveAccounts();
                },
                isDragging =>
                {
                    if (isDragging) activeRobloxDrags.TryAdd(window.Handle, 0);
                    else
                    {
                        activeRobloxDrags.TryRemove(window.Handle, out _);
                        if (desiredRobloxSizes.TryGetValue(window.Handle, out Size current))
                            QueueRobloxSizeFix(window, current, true);
                    }
                },
                () => restoreTargets.TryGetValue(window.Handle,
                    out RobloxRestoreWatcher.Target target) &&
                    target.Window.ProcessId == window.ProcessId
                        ? target.Size : (Size?)null);
            robloxResizeGrip.Show();
        }
    }
}
