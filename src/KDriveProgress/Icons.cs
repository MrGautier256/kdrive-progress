using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace KDriveProgress
{
    enum IconState { Idle, Active, Warning }

    /// <summary>Icône dessinée à la volée : un nuage dans un disque, entouré d'un anneau de progression.</summary>
    static class Icons
    {
        static readonly Color Blue = Color.FromArgb(0x1E, 0x7B, 0xE6);
        static readonly Color Gray = Color.FromArgb(0x8A, 0x8F, 0x98);
        static readonly Color Ring = Color.FromArgb(0x4A, 0xDE, 0x80);

        public static Bitmap Draw(int size, IconState state, double fraction)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float s = size;
                float ring = Math.Max(2f, s / 7f);
                var disc = state == IconState.Active
                    ? new RectangleF(ring * 0.9f, ring * 0.9f, s - ring * 1.8f, s - ring * 1.8f)
                    : new RectangleF(0.5f, 0.5f, s - 1f, s - 1f);
                using (var b = new SolidBrush(state == IconState.Warning ? Gray : Blue)) g.FillEllipse(b, disc);

                // nuage
                float cx = disc.X + disc.Width / 2, cy = disc.Y + disc.Height / 2, w = disc.Width * 0.66f, h = w * 0.5f;
                float x0 = cx - w / 2, y0 = cy - h / 2 + disc.Height * 0.04f;
                using (var path = new GraphicsPath(FillMode.Winding)) // sinon les chevauchements font des trous
                {
                    path.AddEllipse(x0, y0 + h * 0.30f, w * 0.42f, h * 0.70f);
                    path.AddEllipse(x0 + w * 0.20f, y0 - h * 0.10f, w * 0.52f, h * 0.95f);
                    path.AddEllipse(x0 + w * 0.52f, y0 + h * 0.18f, w * 0.48f, h * 0.82f);
                    path.AddRectangle(new RectangleF(x0 + w * 0.2f, y0 + h * 0.55f, w * 0.62f, h * 0.45f));
                    using (var region = new Region(path))
                    using (var white = new SolidBrush(Color.White))
                        g.FillRegion(white, region);
                }

                if (state == IconState.Active)
                {
                    var arc = new RectangleF(ring / 2, ring / 2, s - ring, s - ring);
                    using (var track = new Pen(Color.FromArgb(90, Blue), ring)) g.DrawEllipse(track, arc);
                    float sweep = (float)(Math.Max(0.04, Math.Min(1.0, fraction)) * 360);
                    using (var pen = new Pen(Ring, ring) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawArc(pen, arc, -90, sweep);
                }
            }
            return bmp;
        }

        /// <summary>Icône Windows à partir du dessin. L'appelant libère le handle avec <see cref="Native.DestroyIcon"/>.</summary>
        public static Icon ToIcon(Bitmap bmp, out IntPtr handle)
        {
            handle = bmp.GetHicon();
            return Icon.FromHandle(handle);
        }

        /// <summary>Fichier .ico multi-tailles (images PNG), utilisé comme icône de l'exécutable.</summary>
        public static void WriteIco(string path)
        {
            var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 256 };
            var images = new List<byte[]>();
            foreach (var size in sizes)
                using (var bmp = Draw(size, IconState.Idle, 0))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    images.Add(ms.ToArray());
                }
            using (var fs = File.Create(path))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(images[i].Length);
                    w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (var img in images) w.Write(img);
            }
        }
    }
}
