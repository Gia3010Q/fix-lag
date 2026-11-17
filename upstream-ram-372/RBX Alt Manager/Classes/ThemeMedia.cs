using RBX_Alt_Manager.Forms;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using WpfMedia = System.Windows.Media;

namespace RBX_Alt_Manager.Classes
{
    // Animated backgrounds are created only for the account manager, and only
    // while an animated file is selected. Other windows keep the static theme.
    internal static class ThemeMedia
    {
        private sealed class Entry
        {
            internal string Path;
            internal Control Layer;
            internal bool PausedForMove;
        }

        private static readonly Dictionary<Form, Entry> layers = new Dictionary<Form, Entry>();
        private static readonly HashSet<Form> watchedForms = new HashSet<Form>();

        internal static bool IsVideo(string path)
        {
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return ext == ".mp4" || ext == ".wmv" || ext == ".avi" || ext == ".m4v" || ext == ".mov";
        }

        internal static bool IsGif(string path) =>
            string.Equals(System.IO.Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase);

        internal static void Apply(Form form)
        {
            string path = ThemeEditor.BackgroundPath;
            bool animated = !string.IsNullOrEmpty(path) && File.Exists(path) && (IsGif(path) || IsVideo(path));
            if (!animated)
            {
                Remove(form);
                return;
            }

            if (layers.TryGetValue(form, out Entry existing) && !string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                Remove(form);
                existing = null;
            }

            if (existing == null)
            {
                try
                {
                    Control layer = IsGif(path) ? (Control)new GifLayer(path) : new VideoLayer(path);
                    layer.Dock = DockStyle.Fill;
                    form.Controls.Add(layer);
                    existing = new Entry { Path = path, Layer = layer };
                    layers.Add(form, existing);
                    if (watchedForms.Add(form))
                    {
                        form.VisibleChanged += (sender, args) => Apply(form);
                        form.Disposed += (sender, args) => { Remove(form); watchedForms.Remove(form); };
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger.Error($"Could not load theme media {path}: {ex}");
                    return;
                }
            }

            existing.Layer.SendToBack();
            bool active = !existing.PausedForMove && form.Visible && existing.Layer.Visible && form.WindowState != FormWindowState.Minimized;
            if (existing.Layer is GifLayer gif)
            {
                gif.Configure();
                gif.SetActive(active);
            }
            else if (existing.Layer is VideoLayer video)
            {
                video.Configure();
                video.SetActive(active);
            }
        }

        internal static void SetInteractiveMove(Form form, bool moving)
        {
            if (!layers.TryGetValue(form, out Entry entry)) return;
            entry.PausedForMove = moving;
            bool active = !moving && form.Visible && entry.Layer.Visible && form.WindowState != FormWindowState.Minimized;
            if (entry.Layer is GifLayer gif)
            {
                gif.SetMoving(moving);
                gif.SetActive(active);
            }
            else if (entry.Layer is VideoLayer video)
            {
                video.SetMoving(moving);
                video.SetActive(active);
            }
        }

        private static void Remove(Form form)
        {
            if (!layers.TryGetValue(form, out Entry entry)) return;
            layers.Remove(form);
            if (!form.IsDisposed) form.Controls.Remove(entry.Layer);
            entry.Layer.Dispose();
        }

        private sealed class GifLayer : Control
        {
            private readonly FileStream stream;
            private readonly Image animation;
            private Bitmap backdrop;
            private Bitmap frozenFrame;
            private bool playing;
            private bool moving;
            private int frameQueued;
            private long lastFrameRequest;
            private readonly System.Windows.Forms.Timer backdropTimer = new System.Windows.Forms.Timer { Interval = 150 };

            internal GifLayer(string path)
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                try { animation = Image.FromStream(stream); }
                catch { stream.Dispose(); throw; }
                BackColor = ThemeEditor.FormsBackground;
                VisibleChanged += (sender, args) => SetActive(Visible && FindForm()?.WindowState != FormWindowState.Minimized);
                SizeChanged += (sender, args) =>
                {
                    if (IsDisposed || Disposing) return;
                    backdropTimer.Stop();
                    if (!moving) backdropTimer.Start();
                };
                backdropTimer.Tick += (sender, args) =>
                {
                    backdropTimer.Stop();
                    if (IsDisposed || Disposing) return;
                    backdrop?.Dispose();
                    backdrop = GlassBackground.Render(ClientSize, false);
                    Invalidate();
                };
            }

            internal void Configure()
            {
                backdrop?.Dispose();
                backdrop = null;
                Invalidate();
            }

            internal void SetMoving(bool value)
            {
                if (moving == value) return;
                moving = value;
                if (moving)
                {
                    backdropTimer.Stop();
                    if (ClientSize.Width > 0 && ClientSize.Height > 0)
                    {
                        frozenFrame = new Bitmap(ClientSize.Width, ClientSize.Height);
                        using (Graphics graphics = Graphics.FromImage(frozenFrame))
                            DrawFrame(graphics, ClientSize);
                    }
                }
                else
                {
                    frozenFrame?.Dispose();
                    frozenFrame = null;
                    Invalidate();
                }
            }

            internal void SetActive(bool active)
            {
                if (playing == active) return;
                playing = active;
                if (active) ImageAnimator.Animate(animation, FrameChanged);
                else ImageAnimator.StopAnimate(animation, FrameChanged);
            }

            private void FrameChanged(object sender, EventArgs args)
            {
                if (!playing || IsDisposed || !IsHandleCreated) return;
                long now = Stopwatch.GetTimestamp();
                if (now - Interlocked.Read(ref lastFrameRequest) < Stopwatch.Frequency / 30) return;
                if (Interlocked.Exchange(ref frameQueued, 1) != 0) return;
                Interlocked.Exchange(ref lastFrameRequest, now);
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        Interlocked.Exchange(ref frameQueued, 0);
                        if (!IsDisposed && !moving) Invalidate();
                    }));
                }
                catch (InvalidOperationException) { Interlocked.Exchange(ref frameQueued, 0); }
            }

            protected override void OnPaintBackground(PaintEventArgs args) { }

            protected override void OnPaint(PaintEventArgs args)
            {
                if (ClientSize.Width < 1 || ClientSize.Height < 1) return;
                if (moving && frozenFrame != null)
                {
                    args.Graphics.InterpolationMode = InterpolationMode.Bilinear;
                    args.Graphics.DrawImage(frozenFrame, ClientRectangle);
                    return;
                }
                DrawFrame(args.Graphics, ClientSize);
            }

            private void DrawFrame(Graphics graphics, Size size)
            {
                if (backdrop == null)
                {
                    backdrop = GlassBackground.Render(size, false);
                }
                if (backdrop != null)
                {
                    if (backdrop.Size == size) graphics.DrawImageUnscaled(backdrop, Point.Empty);
                    else graphics.DrawImage(backdrop, new Rectangle(Point.Empty, size));
                }
                ImageAnimator.UpdateFrames(animation);
                graphics.InterpolationMode = InterpolationMode.Bilinear;
                GlassBackground.DrawMedia(graphics, animation, size, ThemeEditor.BackgroundMode, ThemeEditor.BackgroundScale);
                GlassBackground.DrawTint(graphics, size);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    SetActive(false);
                    backdropTimer.Stop();
                    backdropTimer.Dispose();
                    backdrop?.Dispose();
                    frozenFrame?.Dispose();
                    animation.Dispose();
                    stream.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        private sealed class VideoLayer : ElementHost
        {
            private readonly WpfControls.Grid canvas;
            private readonly WpfControls.MediaElement player;
            private readonly WpfControls.Border shade;
            private bool active;
            private bool moving;

            internal VideoLayer(string path)
            {
                canvas = new WpfControls.Grid { ClipToBounds = true,
                    Background = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(23, 20, 33)) };
                player = new WpfControls.MediaElement
                {
                    LoadedBehavior = WpfControls.MediaState.Manual,
                    UnloadedBehavior = WpfControls.MediaState.Manual,
                    IsMuted = true,
                    Stretch = WpfMedia.Stretch.Fill,
                    HorizontalAlignment = Wpf.HorizontalAlignment.Center,
                    VerticalAlignment = Wpf.VerticalAlignment.Center
                };
                shade = new WpfControls.Border { IsHitTestVisible = false };
                canvas.Children.Add(player);
                canvas.Children.Add(shade);
                player.Loaded += (sender, args) => { if (active) player.Play(); };
                player.MediaOpened += (sender, args) => UpdateLayout();
                player.MediaEnded += (sender, args) => { player.Position = TimeSpan.Zero; if (active) player.Play(); };
                player.MediaFailed += (sender, args) =>
                {
                    Program.Logger.Error($"Theme video could not play: {args.ErrorException}");
                    player.Stop();
                    Visible = false;
                };
                SizeChanged += (sender, args) => { if (!moving) UpdateLayout(); };
                VisibleChanged += (sender, args) => SetActive(Visible && FindForm()?.WindowState != FormWindowState.Minimized);
                Child = canvas;
                player.Source = new Uri(System.IO.Path.GetFullPath(path), UriKind.Absolute);
            }

            internal void Configure()
            {
                shade.Background = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(
                    (byte)(ThemeEditor.BackgroundTint * 255 / 100), 17, 14, 29));
                UpdateLayout();
            }

            internal void SetMoving(bool value)
            {
                moving = value;
                if (!moving) UpdateLayout();
            }

            private void UpdateLayout()
            {
                if (Width < 1 || Height < 1) return;
                double naturalW = player.NaturalVideoWidth > 0 ? player.NaturalVideoWidth : 16d;
                double naturalH = player.NaturalVideoHeight > 0 ? player.NaturalVideoHeight : 9d;
                double fit = Math.Min(Width / naturalW, Height / naturalH);
                double fill = Math.Max(Width / naturalW, Height / naturalH);
                string mode = ThemeEditor.BackgroundMode;
                double factor = ThemeEditor.BackgroundScale / 100d;
                double ratio = (mode == "Fit" ? fit : mode == "Center" ? 1d : fill) * factor;
                player.Width = mode == "Stretch" ? Width * factor : naturalW * ratio;
                player.Height = mode == "Stretch" ? Height * factor : naturalH * ratio;
            }

            internal void SetActive(bool shouldPlay)
            {
                if (active == shouldPlay) return;
                active = shouldPlay;
                if (player.IsLoaded)
                {
                    if (active) player.Play();
                    else player.Pause();
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    player.Close();
                    Child = null;
                }
                base.Dispose(disposing);
            }
        }
    }
}
