using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace KDriveProgress
{
    /// <summary>Liste dessinée à la main, avec défilement, survol, infobulle et menu contextuel.</summary>
    abstract class RowsView : Panel
    {
        protected const TextFormatFlags Flags =
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

        protected List<TransferView> Items = new List<TransferView>();
        readonly ToolTip _tip = new ToolTip { InitialDelay = 600, ReshowDelay = 200, AutoPopDelay = 15000 };
        readonly ContextMenuStrip _menu = new ContextMenuStrip();
        int _hover = -1;
        int _menuIndex = -1;

        protected readonly Font Normal = new Font("Segoe UI", 9f);
        protected readonly Font Bold = new Font("Segoe UI Semibold", 9f);
        protected readonly Font Small = new Font("Segoe UI", 8.25f);

        public string EmptyText { get; set; } = "";

        protected RowsView()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            AutoScroll = true;
            BackColor = Theme.Surface;
            _menu.Items.Add(L.T("menu.openLocation"), null, (s, e) => OpenLocation(_menuIndex));
            _menu.Items.Add(L.T("menu.copyPath"), null, (s, e) =>
            {
                var p = PathAt(_menuIndex);
                if (p != null) Clipboard.SetText(p);
            });
        }

        protected abstract int RowHeightLogical { get; }
        protected int RowHeight => LogicalToDeviceUnits(RowHeightLogical);
        protected int D(int logical) => LogicalToDeviceUnits(logical);

        protected abstract void DrawRow(Graphics g, Rectangle r, TransferView item, bool hover);

        public void SetItems(List<TransferView> items)
        {
            Items = items ?? new List<TransferView>();
            var min = new Size(0, Items.Count * RowHeight);
            if (AutoScrollMinSize != min) AutoScrollMinSize = min;
            if (_hover >= Items.Count) _hover = -1;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (Items.Count == 0)
            {
                TextRenderer.DrawText(g, EmptyText, Normal, ClientRectangle, Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                return;
            }
            int rh = RowHeight, y0 = AutoScrollPosition.Y;
            int first = Math.Max(0, -y0 / rh);
            for (int i = first; i < Items.Count; i++)
            {
                var r = new Rectangle(0, y0 + i * rh, ClientSize.Width, rh);
                if (r.Top > ClientSize.Height) break;
                if (i == _hover) using (var b = new SolidBrush(Theme.Hover)) g.FillRectangle(b, r);
                DrawRow(g, r, Items[i], i == _hover);
                using (var p = new Pen(Theme.Border)) g.DrawLine(p, r.Left + D(12), r.Bottom - 1, r.Right - D(12), r.Bottom - 1);
            }
        }

        int IndexAt(Point p)
        {
            int i = (p.Y - AutoScrollPosition.Y) / RowHeight;
            return i >= 0 && i < Items.Count ? i : -1;
        }

        string PathAt(int i) => i >= 0 && i < Items.Count ? Items[i].Path : null;

        protected virtual string TooltipFor(TransferView v)
        {
            var text = v.Path ?? v.Name;
            if (v.Status != null && v.Status != Statuses.Ok) text += "\n" + L.Status(v.Status);
            return text;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = IndexAt(e.Location);
            if (i == _hover) return;
            _hover = i;
            Invalidate();
            _tip.SetToolTip(this, i >= 0 ? TooltipFor(Items[i]) : null);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus(); // pour la molette
            if (e.Button != MouseButtons.Right) return;
            _menuIndex = IndexAt(e.Location);
            if (PathAt(_menuIndex) == null) return;
            _menu.Show(this, e.Location);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            OpenLocation(IndexAt(e.Location));
        }

        protected override void OnScroll(ScrollEventArgs se)
        {
            base.OnScroll(se);
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            Invalidate();
        }

        /// <summary>Ouvre l'Explorateur sur le fichier (simple affichage : aucun accès au contenu).</summary>
        void OpenLocation(int i)
        {
            var p = PathAt(i);
            if (p == null) return;
            try
            {
                if (File.Exists(p)) Process.Start("explorer.exe", "/select,\"" + p + "\"");
                else if (Directory.Exists(Path.GetDirectoryName(p))) Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(p) + "\"");
            }
            catch (Exception) { }
        }

        protected static void FillRounded(Graphics g, Color c, RectangleF r)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            float d = Math.Min(r.Height, r.Width);
            using (var path = new GraphicsPath())
            using (var b = new SolidBrush(c))
            {
                path.AddArc(r.X, r.Y, d, d, 90, 180);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
                path.CloseFigure();
                var mode = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(b, path);
                g.SmoothingMode = mode;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _tip.Dispose();
                _menu.Dispose();
                Normal.Dispose();
                Bold.Dispose();
                Small.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>Transferts en cours : nom, barre, octets, débit, temps restant.</summary>
    sealed class ActiveView : RowsView
    {
        protected override int RowHeightLogical => 62;

        protected override void DrawRow(Graphics g, Rectangle r, TransferView v, bool hover)
        {
            int pad = D(14), left = r.Left + pad, width = r.Width - 2 * pad;
            var color = Theme.DirColor(v.Dir);

            string pct = v.Fraction.HasValue ? Format.Percent(v.Fraction.Value) : "";
            int pctW = pct.Length > 0 ? TextRenderer.MeasureText(g, pct, Bold, Size.Empty, Flags).Width : 0;
            var line1 = new Rectangle(left, r.Top + D(8), width, D(20));
            TextRenderer.DrawText(g, v.Dir == Direction.Up ? "↑" : "↓", Bold, new Rectangle(left, line1.Top, D(16), line1.Height), color, Flags);
            TextRenderer.DrawText(g, v.Name, Bold, new Rectangle(left + D(18), line1.Top, width - D(18) - pctW - D(10), line1.Height),
                Theme.Text, Flags | TextFormatFlags.EndEllipsis);
            if (pctW > 0)
                TextRenderer.DrawText(g, pct, Bold, new Rectangle(r.Right - pad - pctW, line1.Top, pctW, line1.Height), Theme.Text, Flags);

            var bar = new RectangleF(left, r.Top + D(32), width, D(6));
            FillRounded(g, Theme.Track, bar);
            if (v.StalledSeconds.HasValue) color = Theme.Warning;
            if (v.Fraction.HasValue && v.Fraction > 0)
                FillRounded(g, color, new RectangleF(bar.X, bar.Y, Math.Max(bar.Height, (float)(bar.Width * v.Fraction.Value)), bar.Height));

            TextRenderer.DrawText(g, DetailText(v), Small, new Rectangle(left, r.Top + D(41), width, D(16)),
                v.StalledSeconds.HasValue ? Theme.Warning : Theme.Muted, Flags | TextFormatFlags.EndEllipsis);
        }

        static string DetailText(TransferView v)
        {
            var parts = new List<string>();
            parts.Add(v.Total > 0 ? Format.Size(v.Done) + " / " + Format.Size(v.Total) : Format.Size(v.Done));
            if (v.StalledSeconds.HasValue)
                parts.Add(L.T("row.stalled", Format.Duration(v.StalledSeconds.Value)));
            else if (v.Rate > 0)
            {
                parts.Add(Format.Rate(v.Rate.Value));
                if (v.EtaSeconds.HasValue) parts.Add(L.T("row.left", Format.Duration(v.EtaSeconds.Value)));
            }
            else if (v.State == null) parts.Add(L.T("row.waiting"));
            if (v.Detail != null) parts.Add(v.Detail);
            if (v.State != null) parts.Add(L.T("state." + v.State));
            return string.Join("  ·  ", parts);
        }

        protected override string TooltipFor(TransferView v)
        {
            return L.T("tip.since", L.T(v.Dir == Direction.Up ? "tip.upload" : "tip.download"), v.Start.ToString("HH:mm:ss"))
                   + "\n" + (v.Path ?? v.Name);
        }
    }

    /// <summary>Transferts terminés : statut, heure, taille, durée, débit moyen.</summary>
    sealed class HistoryView : RowsView
    {
        protected override int RowHeightLogical => 28;

        protected override void DrawRow(Graphics g, Rectangle r, TransferView v, bool hover)
        {
            int pad = D(14), x = r.Left + pad;
            bool ok = v.Status == "OK";
            TextRenderer.DrawText(g, ok ? "✓" : "✗", Bold, new Rectangle(x, r.Top, D(14), r.Height), ok ? Theme.Ok : Theme.Warning, Flags);
            x += D(18);
            var time = v.End.Date == DateTime.Today ? v.End.ToString("HH:mm") : v.End.ToString(L.T("fmt.dayMonth"));
            TextRenderer.DrawText(g, time, Small, new Rectangle(x, r.Top, D(38), r.Height), Theme.Muted, Flags);
            x += D(40);
            TextRenderer.DrawText(g, v.Dir == Direction.Up ? "↑" : "↓", Bold, new Rectangle(x, r.Top, D(14), r.Height), Theme.DirColor(v.Dir), Flags);
            x += D(16);

            string detail;
            if (ok)
            {
                double dur = (v.End - v.Start).TotalSeconds;
                detail = Format.Size(v.Total) + "  ·  " + Format.Duration(dur);
                if (v.Rate > 0) detail += "  ·  " + Format.Rate(v.Rate.Value);
            }
            else detail = L.Status(v.Status);
            int dw = Math.Min(TextRenderer.MeasureText(g, detail, Small, Size.Empty, Flags).Width, r.Width / 2);
            TextRenderer.DrawText(g, detail, Small, new Rectangle(r.Right - pad - dw, r.Top, dw, r.Height), ok ? Theme.Muted : Theme.Warning,
                Flags | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, v.Name, Normal, new Rectangle(x, r.Top, r.Right - pad - dw - D(10) - x, r.Height), Theme.Text,
                Flags | TextFormatFlags.EndEllipsis);
        }

        protected override string TooltipFor(TransferView v)
        {
            var text = (v.Path ?? v.Name) + "\n" + L.T("tip.history", L.T(v.Dir == Direction.Up ? "tip.upload" : "tip.download"),
                v.Start.ToString("g", L.Culture), v.End.ToString("T", L.Culture));
            if (v.Status != Statuses.Ok) text += "\n" + L.Status(v.Status);
            return text;
        }
    }
}
