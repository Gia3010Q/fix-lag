using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RBX_Alt_Manager.Classes
{
    // A hollow overlay handles small-window edges and title dragging without Roblox's minimum size.
    internal sealed class RobloxResizeGrip : Form
    {
        private const int Edge = 16;
        private const int TopEdge = 8;
        private const int TitleHeight = 32;
        private const int TitleButtons = 138;
        private readonly Account account;
        private readonly RobloxWindowManager.Window window;
        private readonly Action<Size> sizeCommitted;
        private readonly Action<bool> dragStateChanged;
        private readonly Func<Size?> desiredSize;
        private readonly System.Windows.Forms.Timer followTimer =
            new System.Windows.Forms.Timer { Interval = 40 };
        private readonly object moveSync = new object();
        private AutoResetEvent moveWake;
        private Point queuedCursor;
        private int queuedVersion;
        private bool moveFinishRequested;
        private bool moveCancelRequested;
        private bool moveFocusOnFinish;
        private RobloxWindowManager.Rect original;
        private Point dragStart;
        private long lastMouseStamp;
        private Size lastAppliedSize;
        private bool left, right, top, bottom;
        private bool moving;
        private bool dragging;
        private bool finishing;
        private bool changed;
        private bool regionInitialized;
        private bool nativeMoveSuspended;

        internal IntPtr TargetHandle => window.Handle;
        internal bool IsDragging => dragging;

        internal void CancelForRestore()
        {
            if (dragging) FinishDrag(false, true);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= 0x00000080 | 0x08000000; // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                return parameters;
            }
        }

        internal RobloxResizeGrip(Account account, RobloxWindowManager.Window window,
            Action<Size> sizeCommitted, Action<bool> dragStateChanged,
            Func<Size?> desiredSize)
        {
            this.account = account;
            this.window = window;
            this.sizeCommitted = sizeCommitted;
            this.dragStateChanged = dragStateChanged;
            this.desiredSize = desiredSize;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            Opacity = 0.02;
            BackColor = Color.FromArgb(118, 85, 164);
            followTimer.Tick += (sender, args) => FollowTarget();
            followTimer.Start();
            Shown += (sender, args) => FollowTarget();
        }

        private void FollowTarget()
        {
            if (IsDisposed || Disposing) return;
            if (!RobloxWindowManager.TryGetRect(window, out RobloxWindowManager.Rect rect))
            {
                Close();
                return;
            }
            if (RobloxWindowManager.IsMinimized(window) ||
                rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            {
                FinishDrag(false, true);
                if (Visible) Hide();
                return;
            }
            if (nativeMoveSuspended)
            {
                if (Visible) Hide();
                return;
            }
            // Keep capture during an ordinary drag, but never across a minimize.
            if (dragging)
            {
                if (moving) QueueMove(System.Windows.Forms.Cursor.Position);
                return;
            }
            Size? expected = desiredSize?.Invoke();
            if (expected.HasValue &&
                (rect.Right - rect.Left != expected.Value.Width ||
                 rect.Bottom - rect.Top != expected.Value.Height))
            {
                if (Visible) Hide();
                return;
            }
            if (!dragging && RobloxWindowManager.GetForegroundWindow() != window.Handle)
            {
                if (Visible) Hide();
                return;
            }

            Rectangle display = Screen.FromHandle(window.Handle).Bounds;
            if (rect.Right - rect.Left >= display.Width && rect.Bottom - rect.Top >= display.Height)
            {
                FinishDrag(false, true);
                if (Visible) Hide();
                return;
            }

            var bounds = new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            bool sizeChanged = Size != bounds.Size;
            if (Bounds != bounds) Bounds = bounds;
            if (sizeChanged || !regionInitialized)
            {
                int titleHeight = Math.Min(TitleHeight, Height);
                using (var region = new Region(new Rectangle(0, 0, Edge, Height)))
                {
                    region.Union(new Rectangle(Math.Max(0, Width - Edge), titleHeight,
                        Edge, Height - titleHeight));
                    region.Union(new Rectangle(0, 0, Width, TopEdge));
                    region.Union(new Rectangle(0, Math.Max(0, Height - Edge), Width, Edge));
                    region.Union(new Rectangle(Edge, TopEdge,
                        Math.Max(0, Width - TitleButtons - Edge), Math.Max(0, titleHeight - TopEdge)));
                    Region previous = Region;
                    Region = region.Clone();
                    previous?.Dispose();
                }
                regionInitialized = true;
            }
            if (!Visible) Show();
        }

        internal void SuspendForNativeMove(bool suspended)
        {
            nativeMoveSuspended = suspended;
            if (suspended)
            {
                if (Visible) Hide();
            }
            else FollowTarget();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging)
            {
                bool nearLeft = e.X < Edge;
                bool nearRight = e.X >= Width - Edge;
                bool nearTop = e.Y < TopEdge;
                bool nearBottom = e.Y >= Height - Edge;
                Cursor = (nearLeft && nearTop) || (nearRight && nearBottom) ? Cursors.SizeNWSE :
                    (nearRight && nearTop) || (nearLeft && nearBottom) ? Cursors.SizeNESW :
                    nearLeft || nearRight ? Cursors.SizeWE :
                    nearTop || nearBottom ? Cursors.SizeNS : Cursors.Default;
                return;
            }
            if (moving)
            {
                QueueMove(System.Windows.Forms.Cursor.Position);
                return;
            }
            long now = Stopwatch.GetTimestamp();
            long interval = Stopwatch.Frequency / 60;
            if (now - lastMouseStamp < interval) return;
            lastMouseStamp = now;
            ApplyDragPosition(System.Windows.Forms.Cursor.Position);
        }

        private void ApplyDragPosition(Point cursor)
        {
            Point delta = new Point(cursor.X - dragStart.X, cursor.Y - dragStart.Y);
            int x = original.Left;
            int y = original.Top;
            int width = original.Right - original.Left;
            int height = original.Bottom - original.Top;
            if (left) { x += delta.X; width -= delta.X; }
            if (right) width += delta.X;
            if (top) { y += delta.Y; height -= delta.Y; }
            if (bottom) height += delta.Y;
            if (width < 160) { if (left) x = original.Right - 160; width = 160; }
            if (height < 120) { if (top) y = original.Bottom - 120; height = 120; }
            width = Math.Min(4096, width);
            height = Math.Min(4096, height);
            if (RobloxWindowManager.ApplyBounds(window, x, y, width, height))
            {
                lastAppliedSize = new Size(width, height);
                changed = true;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left ||
                !RobloxWindowManager.TryGetRect(window, out original)) return;
            Size? expected = desiredSize?.Invoke();
            if (expected.HasValue &&
                (original.Right - original.Left != expected.Value.Width ||
                 original.Bottom - original.Top != expected.Value.Height))
            {
                Hide();
                return;
            }
            left = e.X < Edge;
            right = e.X >= Width - Edge;
            top = e.Y < TopEdge;
            bottom = e.Y >= Height - Edge;
            moving = !left && !right && !top && !bottom &&
                e.Y < TitleHeight && e.X < Width - TitleButtons;
            if (!left && !right && !top && !bottom && !moving) return;
            dragStart = System.Windows.Forms.Cursor.Position;
            dragging = true;
            lastMouseStamp = 0;
            dragStateChanged?.Invoke(true);
            if (moving) StartMoveWorker();
            Capture = true;
        }

        private void StartMoveWorker()
        {
            var wake = new AutoResetEvent(false);
            lock (moveSync)
            {
                moveWake = wake;
                queuedCursor = dragStart;
                queuedVersion = 0;
                moveFinishRequested = false;
                moveCancelRequested = false;
                moveFocusOnFinish = false;
            }
            RobloxWindowManager.Rect start = original;
            Point pointer = dragStart;
            _ = Task.Run(() => RunMoveWorker(wake, start, pointer));
        }

        private void QueueMove(Point cursor)
        {
            lock (moveSync)
            {
                if (moveWake == null || moveFinishRequested || queuedCursor == cursor)
                    return;
                queuedCursor = cursor;
                queuedVersion++;
                moveWake.Set();
            }
        }

        private void RunMoveWorker(AutoResetEvent wake,
            RobloxWindowManager.Rect start, Point pointer)
        {
            int appliedVersion = 0;
            try
            {
                while (true)
                {
                    wake.WaitOne();
                    Point cursor;
                    int version;
                    bool finish;
                    bool cancel;
                    lock (moveSync)
                    {
                        if (!ReferenceEquals(moveWake, wake)) break;
                        cursor = queuedCursor;
                        version = queuedVersion;
                        finish = moveFinishRequested;
                        cancel = moveCancelRequested;
                    }
                    if (cancel) break;
                    if (version != appliedVersion)
                    {
                        // The signal already coalesces mouse events. Read the live
                        // cursor just before moving, unless this is the final point.
                        if (!finish) cursor = System.Windows.Forms.Cursor.Position;
                        int width = start.Right - start.Left;
                        int height = start.Bottom - start.Top;
                        int x = start.Left + cursor.X - pointer.X;
                        int y = start.Top + cursor.Y - pointer.Y;
                        if (RobloxWindowManager.MoveDuringDrag(window, x, y) &&
                            RobloxWindowManager.TryGetSize(window,
                                out int actualWidth, out int actualHeight) &&
                            (actualWidth != width || actualHeight != height))
                            RobloxWindowManager.ApplySize(window, width, height);
                        appliedVersion = version;
                    }
                    if (finish && appliedVersion == version) break;
                }
            }
            catch { /* A closing Roblox window must not stall the manager UI. */ }
            finally
            {
                try
                {
                    if (!IsDisposed && IsHandleCreated)
                        BeginInvoke((Action)(() => CompleteMoveDrag(wake)));
                }
                catch { /* The overlay may close while a move is finishing. */ }
            }
        }

        private void RequestMoveFinish(bool focusWindow, bool cancel)
        {
            lock (moveSync)
            {
                if (moveWake == null) return;
                moveFocusOnFinish |= focusWindow;
                moveCancelRequested |= cancel;
                if (!moveFinishRequested)
                {
                    queuedCursor = System.Windows.Forms.Cursor.Position;
                    queuedVersion++;
                    moveFinishRequested = true;
                }
                moveWake.Set();
            }
            if (Capture) Capture = false;
        }

        private void CompleteMoveDrag(AutoResetEvent wake)
        {
            bool focus;
            lock (moveSync)
            {
                if (!ReferenceEquals(moveWake, wake)) return;
                focus = moveFocusOnFinish && !moveCancelRequested;
                moveWake = null;
                moveFinishRequested = false;
            }
            wake.Dispose();
            FinishDrag(focus);
        }

        private void FinishDrag(bool focusWindow, bool cancelMove = false)
        {
            if (!dragging || finishing) return;
            if (moving && moveWake != null)
            {
                RequestMoveFinish(focusWindow, cancelMove);
                return;
            }
            finishing = true;
            try
            {
                if (moving && RobloxWindowManager.TryGetSize(window,
                    out int width, out int height) &&
                    (width != original.Right - original.Left ||
                     height != original.Bottom - original.Top))
                    RobloxWindowManager.ApplySize(window,
                        original.Right - original.Left, original.Bottom - original.Top);
                if (changed)
                {
                    account.Fields["Mizu_Window_Width"] = lastAppliedSize.Width.ToString();
                    account.Fields["Mizu_Window_Height"] = lastAppliedSize.Height.ToString();
                    sizeCommitted?.Invoke(lastAppliedSize);
                }
            }
            finally
            {
                changed = false;
                moving = false;
                dragging = false;
                Capture = false;
                dragStateChanged?.Invoke(false);
                finishing = false;
            }
            if (focusWindow)
            {
                if (RobloxWindowManager.GetForegroundWindow() != window.Handle)
                    RobloxWindowManager.Focus(window);
                FollowTarget();
            }
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (dragging && !Capture && !moveFinishRequested)
                FinishDrag(false, moving);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!dragging)
            {
                RobloxWindowManager.Focus(window);
                return;
            }
            if (!moving) ApplyDragPosition(System.Windows.Forms.Cursor.Position);
            FinishDrag(true);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            followTimer.Stop();
            followTimer.Dispose();
            FinishDrag(false, true);
            base.OnFormClosed(e);
        }
    }
}
