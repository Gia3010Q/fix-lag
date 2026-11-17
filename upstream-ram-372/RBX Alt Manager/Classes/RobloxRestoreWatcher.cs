using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Classes
{
    // Receives restore events on its own message loop so a busy WinForms UI cannot
    // delay the first below-minimum-size correction.
    internal sealed class RobloxRestoreWatcher : IDisposable
    {
        internal sealed class Target
        {
            internal readonly RobloxWindowManager.Window Window;
            internal readonly Size Size;

            internal Target(RobloxWindowManager.Window window, Size size)
            {
                Window = window;
                Size = size;
            }
        }

        private sealed class RestoreSession
        {
            internal long LastEventTick;
            internal int BurstCount;
            internal bool Running;
            internal readonly SemaphoreSlim Wake = new SemaphoreSlim(0, 1);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            public IntPtr Window;
            public uint Message;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int PointX;
            public int PointY;
            public uint Private;
        }

        private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr window,
            int objectId, int childId, uint threadId, uint eventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
            WinEventDelegate callback, uint processId, uint threadId, uint flags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out NativeMessage message, IntPtr window,
            uint filterMin, uint filterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref NativeMessage message);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref NativeMessage message);

        [DllImport("user32.dll")]
        private static extern bool PeekMessage(out NativeMessage message, IntPtr window,
            uint filterMin, uint filterMax, uint remove);

        [DllImport("user32.dll")]
        private static extern bool PostThreadMessage(uint threadId, uint message,
            IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private readonly Func<IntPtr, Target> findTarget;
        private readonly Func<IntPtr, bool> canResize;
        private readonly Action<IntPtr> releaseDrag;
        private readonly ConcurrentDictionary<IntPtr, RestoreSession> sessions =
            new ConcurrentDictionary<IntPtr, RestoreSession>();
        private Thread thread;
        private WinEventDelegate callback;
        private IntPtr hook;
        private volatile uint threadId;
        private volatile bool disposed;
        private volatile bool active;

        internal bool IsActive => active;
        internal bool IsRestoring(IntPtr handle)
        {
            if (!sessions.TryGetValue(handle, out RestoreSession session)) return false;
            lock (session)
                return session.Running && MillisecondsSince(session.LastEventTick) < 600;
        }

        private static long MillisecondsSince(long tick) =>
            (Stopwatch.GetTimestamp() - tick) * 1000 / Stopwatch.Frequency;

        internal RobloxRestoreWatcher(Func<IntPtr, Target> findTarget,
            Func<IntPtr, bool> canResize, Action<IntPtr> releaseDrag)
        {
            this.findTarget = findTarget;
            this.canResize = canResize;
            this.releaseDrag = releaseDrag;
        }

        internal void Start()
        {
            if (disposed || thread != null) return;
            thread = new Thread(EventLoop) { IsBackground = true, Name = "Roblox Manager Roblox restore" };
            thread.Start();
        }

        private void EventLoop()
        {
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // Create this thread's message queue.
            threadId = GetCurrentThreadId();
            callback = OnRestore;
            hook = SetWinEventHook(0x0017, 0x0017, IntPtr.Zero, callback,
                0, 0, 0x0002); // EVENT_SYSTEM_MINIMIZEEND, skip this process.
            active = hook != IntPtr.Zero;
            try
            {
                while (!disposed && GetMessage(out NativeMessage message, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
            }
            finally
            {
                active = false;
                if (hook != IntPtr.Zero) UnhookWinEvent(hook);
                hook = IntPtr.Zero;
                callback = null;
            }
        }

        private void OnRestore(IntPtr ignoredHook, uint eventType, IntPtr handle,
            int objectId, int childId, uint threadId, uint eventTime)
        {
            if (disposed || objectId != 0 || childId != 0) return;
            Target target = findTarget(handle);
            if (target == null || !RobloxWindowManager.IsCurrent(target.Window)) return;
            try { releaseDrag?.Invoke(handle); }
            catch { /* A closing manager must not disable the restore hook. */ }
            RestoreSession session = sessions.GetOrAdd(handle, _ => new RestoreSession());
            bool start;
            lock (session)
            {
                session.BurstCount = MillisecondsSince(session.LastEventTick) < 200
                    ? Math.Min(session.BurstCount + 1, 1000) : 1;
                session.LastEventTick = Stopwatch.GetTimestamp();
                start = !session.Running;
                if (start) session.Running = true;
                else if (session.Wake.CurrentCount == 0)
                {
                    try { session.Wake.Release(); }
                    catch (SemaphoreFullException) { }
                }
            }
            if (start) _ = Task.Run(() => WatchRestore(handle, session));
        }

        private async Task WatchRestore(IntPtr handle, RestoreSession session)
        {
            try
            {
                while (!disposed)
                {
                    long eventAge;
                    int burstCount;
                    lock (session)
                    {
                        eventAge = MillisecondsSince(session.LastEventTick);
                        burstCount = session.BurstCount;
                    }
                    if (eventAge >= 8000) break;
                    Target target = findTarget(handle);
                    if (target == null || !RobloxWindowManager.IsCurrent(target.Window)) break;
                    if (RobloxWindowManager.IsMinimized(target.Window) || !canResize(handle) ||
                        (burstCount >= 3 && eventAge < 45))
                    {
                        await session.Wake.WaitAsync(eventAge < 500 ? 12 : 60)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (RobloxWindowManager.TryGetSize(target.Window, out int width, out int height) &&
                        (width != target.Size.Width || height != target.Size.Height))
                    {
                        if (RobloxWindowManager.IsResponsive(target.Window))
                            RobloxWindowManager.ApplySize(target.Window,
                                target.Size.Width, target.Size.Height);
                        else
                        {
                            await session.Wake.WaitAsync(60).ConfigureAwait(false);
                            continue;
                        }
                        await session.Wake.WaitAsync(15).ConfigureAwait(false);
                        continue;
                    }
                    await session.Wake.WaitAsync(eventAge < 1000 ? 40 : 100)
                        .ConfigureAwait(false);
                }
            }
            catch { /* Closing Roblox must not fault the event thread. */ }
            finally
            {
                lock (session)
                {
                    // A new restore can arrive while this worker is winding down.
                    // Keep exactly one worker per window, even during an event storm.
                    if (!disposed && MillisecondsSince(session.LastEventTick) < 100)
                        _ = Task.Run(() => WatchRestore(handle, session));
                    else
                        session.Running = false;
                }
            }
        }

        public void Dispose()
        {
            disposed = true;
            uint id = threadId;
            if (id != 0) PostThreadMessage(id, 0x0012, IntPtr.Zero, IntPtr.Zero); // WM_QUIT
            if (thread != null && thread != Thread.CurrentThread) thread.Join(500);
            sessions.Clear();
        }
    }
}
