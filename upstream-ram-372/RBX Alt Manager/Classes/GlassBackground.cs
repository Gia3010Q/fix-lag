using RBX_Alt_Manager.Forms;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace RBX_Alt_Manager.Classes
{
    // A light, static glass treatment; painting only on theme changes and resize.
    internal static class GlassBackground
    {
        private static string cachedPath;
        private static Bitmap cachedPhoto;

        internal static void Invalidate()
        {
            cachedPhoto?.Dispose();
            cachedPhoto = null;
            cachedPath = null;
        }

        private static Bitmap Photo()
        {
            string path = ThemeEditor.BackgroundPath;
            if (path == cachedPath) return cachedPhoto;
            cachedPhoto?.Dispose();
            cachedPhoto = null;
            cachedPath = path;
            if (string.IsNullOrEmpty(path) || !File.Exists(path) || ThemeMedia.IsVideo(path)) return null;
            try
            {
                using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (Image image = Image.FromStream(stream))
                {
                    float shrink = Math.Min(1f, Math.Min(2048f / image.Width, 2048f / image.Height));
                    cachedPhoto = new Bitmap(Math.Max(1, (int)(image.Width * shrink)), Math.Max(1, (int)(image.Height * shrink)));
                    using (Graphics graphics = Graphics.FromImage(cachedPhoto))
                    {
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.DrawImage(image, new Rectangle(Point.Empty, cachedPhoto.Size));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is OutOfMemoryException)
            {
                cachedPhoto = null;
            }
            return cachedPhoto;
        }

        internal static void DrawMedia(Graphics graphics, Image photo, Size size, string mode, int scale)
        {
            float canvasW = size.Width, canvasH = size.Height;
            float factor = scale / 100f;
            if (mode == "Tile")
            {
                float w = Math.Max(32f, photo.Width * factor), h = Math.Max(32f, photo.Height * factor);
                for (float y = 0; y < canvasH; y += h)
                    for (float x = 0; x < canvasW; x += w)
                        graphics.DrawImage(photo, x, y, w, h);
                return;
            }
            float fit = Math.Min(canvasW / photo.Width, canvasH / photo.Height);
            float fill = Math.Max(canvasW / photo.Width, canvasH / photo.Height);
            float ratio = (mode == "Fit" ? fit : mode == "Stretch" ? 1f : mode == "Center" ? 1f : fill) * factor;
            float width = mode == "Stretch" ? canvasW * factor : photo.Width * ratio;
            float height = mode == "Stretch" ? canvasH * factor : photo.Height * ratio;
            graphics.DrawImage(photo, (canvasW - width) / 2, (canvasH - height) / 2, width, height);
        }

        internal static void DrawTint(Graphics graphics, Size size)
        {
            using (Brush tint = new SolidBrush(Color.FromArgb(ThemeEditor.BackgroundTint * 255 / 100, 17, 14, 29)))
                graphics.FillRectangle(tint, new Rectangle(Point.Empty, size));
        }

        internal static Bitmap Render(Size size, bool includePhoto = true)
        {
            if (size.Width < 1 || size.Height < 1) return null;
            Bitmap output = new Bitmap(size.Width, size.Height);
            using (Graphics graphics = Graphics.FromImage(output))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                using (LinearGradientBrush gradient = new LinearGradientBrush(
                    new Rectangle(Point.Empty, size),
                    Color.FromArgb(44, 34, 69), ThemeEditor.FormsBackground, 115f))
                    graphics.FillRectangle(gradient, new Rectangle(Point.Empty, size));

                Bitmap photo = includePhoto ? Photo() : null;
                if (photo != null)
                {
                    DrawMedia(graphics, photo, size, ThemeEditor.BackgroundMode, ThemeEditor.BackgroundScale);
                    DrawTint(graphics, size);
                }

                // Soft specular highlights evoke liquid glass without a busy animation timer.
                DrawDroplet(graphics, new RectangleF(size.Width * .65f, -size.Height * .45f, size.Width * .7f, size.Height * 1.1f), 43);
                DrawDroplet(graphics, new RectangleF(-size.Width * .28f, size.Height * .37f, size.Width * .65f, size.Height * .95f), 24);
            }
            return output;
        }

        private static void DrawDroplet(Graphics graphics, RectangleF bounds, int alpha)
        {
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddEllipse(bounds);
                using (PathGradientBrush glow = new PathGradientBrush(path))
                {
                    glow.CenterColor = Color.FromArgb(alpha, 255, 209, 246);
                    glow.SurroundColors = new[] { Color.FromArgb(0, 255, 209, 246) };
                    graphics.FillPath(glow, path);
                }
                using (Pen edge = new Pen(Color.FromArgb(alpha / 2, Color.White), 1f))
                    graphics.DrawPath(edge, path);
            }
        }

        internal static void Apply(Control control)
        {
            Bitmap previous = control.BackgroundImage as Bitmap;
            string path = ThemeEditor.BackgroundPath;
            bool animatedMainWindow = control is AccountManager && !string.IsNullOrEmpty(path) &&
                File.Exists(path) && (ThemeMedia.IsGif(path) || ThemeMedia.IsVideo(path));
            // Animated themes paint their own full-size layer. A second form bitmap
            // can leave stale tiles behind layered child controls during a resize.
            control.BackgroundImage = animatedMainWindow ? null : Render(control.ClientSize);
            // Reuse the last frame while the window is being resized; render a sharp frame afterwards.
            control.BackgroundImageLayout = ImageLayout.Stretch;
            previous?.Dispose();
            if (control is AccountManager manager) ThemeMedia.Apply(manager);
        }
    }
}
