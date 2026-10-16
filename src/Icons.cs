using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BatteryBrightness
{
    /// <summary>Draws the tray glyph and the app icon, so the exe carries no image files.</summary>
    static class Icons
    {
        /// <summary>A monochrome sun sized for the notification area: white on a dark taskbar, black on a light one.</summary>
        public static Icon CreateTrayIcon(bool darkTaskbar)
        {
            int size = SystemInformation.SmallIconSize.Width;
            using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                DrawSun(g, new RectangleF(0, 0, size, size), darkTaskbar ? Color.White : Color.Black);

            IntPtr handle = bmp.GetHicon();
            try
            {
                using var temp = Icon.FromHandle(handle);
                return (Icon)temp.Clone();
            }
            finally { DestroyIcon(handle); }
        }

        /// <summary>Writes the multi-size app icon used for the exe (run with --write-icon app.ico).</summary>
        public static void WriteAppIcon(string path)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
            var images = new List<byte[]>();
            foreach (int size in sizes)
            {
                using var bmp = RenderAppIcon(size);
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                images.Add(ms.ToArray());
            }

            using var w = new BinaryWriter(File.Create(path));
            w.Write((short)0);            // reserved
            w.Write((short)1);            // type: icon
            w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                w.Write(dim);
                w.Write(dim);
                w.Write((byte)0);         // palette size
                w.Write((byte)0);         // reserved
                w.Write((short)1);        // planes
                w.Write((short)32);       // bits per pixel
                w.Write(images[i].Length);
                w.Write(offset);
                offset += images[i].Length;
            }
            foreach (var image in images) w.Write(image);
        }

        static Bitmap RenderAppIcon(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            float inset = size * 0.04f, side = size - 2 * inset;
            var tile = new RectangleF(inset, inset, side, side);
            using (var path = RoundedRect(tile, side * 0.24f))
            using (var brush = new LinearGradientBrush(tile, Color.FromArgb(0xFF, 0xC1, 0x4D), Color.FromArgb(0xF5, 0x6B, 0x1E), 45f))
                g.FillPath(brush, path);

            float sun = side * 0.7f;
            DrawSun(g, new RectangleF((size - sun) / 2, (size - sun) / 2, sun, sun), Color.White);
            return bmp;
        }

        static void DrawSun(Graphics g, RectangleF r, Color color)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            float s = r.Width, cx = r.X + s / 2, cy = r.Y + s / 2;
            float stroke = Math.Max(1f, s * 0.09f);
            float core = s * 0.19f, rayIn = s * 0.31f, rayOut = s * 0.5f - stroke / 2;

            using (var brush = new SolidBrush(color))
                g.FillEllipse(brush, cx - core, cy - core, 2 * core, 2 * core);

            using var pen = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4;
                float dx = (float)Math.Cos(a), dy = (float)Math.Sin(a);
                g.DrawLine(pen, cx + dx * rayIn, cy + dy * rayIn, cx + dx * rayOut, cy + dy * rayOut);
            }
        }

        internal static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);
    }
}
