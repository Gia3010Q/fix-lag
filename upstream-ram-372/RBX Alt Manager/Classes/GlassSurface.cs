using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RBX_Alt_Manager.Forms;

namespace RBX_Alt_Manager.Classes
{
    // Make the native control itself translucent. A layered parent around a
    // ListView or RichEdit retains an old child bitmap after window moves.
    internal sealed class GlassSurface
    {
        private const int GwlExStyle = -20;
        private const int WsExLayered = 0x00080000;
        private const uint LwaAlpha = 0x00000002;
        private const uint RdwInvalidate = 0x0001;
        private const uint RdwErase = 0x0004;
        private const uint RdwUpdateNow = 0x0100;
        private const uint RdwFrame = 0x0400;
        private const int EmSetBackgroundColor = 0x0400 + 67;

        private readonly Control control;
        private readonly byte alpha;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr window, int index, int style);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetLayeredWindowAttributes(IntPtr window, uint colorKey, byte alpha, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RedrawWindow(IntPtr window, IntPtr updateRect, IntPtr updateRegion, uint flags);

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        internal GlassSurface(Control control, byte alpha)
        {
            this.control = control;
            this.alpha = alpha;
            control.HandleCreated += (sender, args) =>
            {
                if (control.IsDisposed) return;
                control.BeginInvoke(new Action(() => { if (!control.IsDisposed) RefreshSurface(); }));
            };
            control.VisibleChanged += (sender, args) =>
            {
                if (control.Visible) RefreshSurface();
            };
            ApplyGlass();
        }

        internal bool ApplyGlass()
        {
            if (control.IsDisposed || !control.IsHandleCreated) return false;
            IntPtr handle = control.Handle;
            int style = GetWindowLong(handle, GwlExStyle);
            if ((style & WsExLayered) == 0)
                SetWindowLong(handle, GwlExStyle, style | WsExLayered);
            bool applied = (GetWindowLong(handle, GwlExStyle) & WsExLayered) != 0 &&
                SetLayeredWindowAttributes(handle, 0, alpha, LwaAlpha);
            if (applied && control is RichTextBox)
            {
                Color color = ThemeEditor.TextBoxesBackground;
                int colorRef = color.R | (color.G << 8) | (color.B << 16);
                SendMessage(handle, EmSetBackgroundColor, IntPtr.Zero, new IntPtr(colorRef));
            }
            return applied;
        }

        internal void RefreshSurface()
        {
            if (!control.Visible || !ApplyGlass()) return;
            RedrawWindow(control.Handle, IntPtr.Zero, IntPtr.Zero,
                RdwInvalidate | RdwErase | RdwUpdateNow | RdwFrame);
        }
    }
}
