using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SimpleCap
{
    // 色見本ポップアップ (4列×3段=12色 + 透明 + その他の色)
    // 色見本を右クリックすると、その枠の色を好きな色に変更できます(settings.ini に保存)
    public class ColorPalette : Control
    {
        public static readonly Color[] DefaultColors = {
            Color.FromArgb(255, 69, 69),   Color.FromArgb(99, 33, 138),  Color.FromArgb(0, 176, 145),  Color.FromArgb(252, 172, 15),
            Color.FromArgb(255, 255, 255), Color.FromArgb(164, 164, 164), Color.FromArgb(69, 69, 69),   Color.FromArgb(0, 0, 0),
            Color.FromArgb(0, 120, 215),   Color.FromArgb(76, 175, 80),  Color.FromArgb(255, 235, 59), Color.FromArgb(240, 98, 146)
        };
        static Color[] colors;

        const int Cols = 4;
        int cell, gap, pad, footer;
        Color current;
        bool allowTransparent;
        int hoverIdx = -1;   // 0..11 = 色見本, 100 = 透明, 101 = その他
        Action<Color> onPick;
        ToolStripDropDown dropDown;
        Control anchor;

        public static Color[] Colors
        {
            get
            {
                if (colors == null) colors = LoadColors();
                return colors;
            }
        }

        static Color[] LoadColors()
        {
            Color[] c = (Color[])DefaultColors.Clone();
            string v;
            if (Program.LoadSettings().TryGetValue("palette", out v))
            {
                string[] parts = v.Split(',');
                for (int i = 0; i < parts.Length && i < c.Length; i++)
                {
                    try { c[i] = ColorTranslator.FromHtml(parts[i].Trim()); } catch { }
                }
            }
            return c;
        }

        static void SaveColors()
        {
            List<string> s = new List<string>();
            foreach (Color c in Colors) s.Add(string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B));
            Dictionary<string, string> st = Program.LoadSettings();
            st["palette"] = string.Join(",", s.ToArray());
            Program.SaveSettings(st);
        }

        ColorPalette(Color current, bool allowTransparent, Action<Color> onPick)
        {
            this.current = current; this.allowTransparent = allowTransparent; this.onPick = onPick;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            cell = Program.D(44); gap = Program.D(8); pad = Program.D(10); footer = Program.D(34);
            int rows = (Colors.Length + Cols - 1) / Cols;
            Size = new Size(pad * 2 + Cols * cell + (Cols - 1) * gap,
                            pad * 2 + rows * cell + (rows - 1) * gap + gap + footer);
            BackColor = Color.FromArgb(236, 243, 251);
            Font = Program.UiFont;
            Cursor = Cursors.Hand;
        }

        // 呼び出し口
        public static void Show(Control anchor, Color current, bool allowTransparent, Action<Color> onPick)
        {
            ColorPalette p = new ColorPalette(current, allowTransparent, onPick);
            ToolStripControlHost h = new ToolStripControlHost(p);
            h.Margin = Padding.Empty; h.Padding = Padding.Empty; h.AutoSize = false; h.Size = p.Size;
            ToolStripDropDown dd = new ToolStripDropDown();
            dd.Padding = Padding.Empty;
            dd.Items.Add(h);
            // 閉じたあとに破棄する(イベント処理中に破棄すると落ちるため、次のメッセージ処理で破棄)
            dd.Closed += delegate
            {
                if (anchor.IsHandleCreated && !anchor.IsDisposed)
                    anchor.BeginInvoke((MethodInvoker)delegate { if (!dd.IsDisposed) dd.Dispose(); });
            };
            p.dropDown = dd;
            p.anchor = anchor;
            dd.Show(anchor, new Point(0, anchor.Height));
            p.Focus();
        }

        Rectangle CellRect(int i)
        {
            int r = i / Cols, c = i % Cols;
            return new Rectangle(pad + c * (cell + gap), pad + r * (cell + gap), cell, cell);
        }
        Rectangle FooterRect(int which) // 0=透明, 1=その他
        {
            int rows = (Colors.Length + Cols - 1) / Cols;
            int y = pad + rows * cell + (rows - 1) * gap + gap;
            int w = (Width - pad * 2 - gap) / 2;
            if (!allowTransparent) return which == 1 ? new Rectangle(pad, y, Width - pad * 2, footer) : Rectangle.Empty;
            return new Rectangle(pad + which * (w + gap), y, w, footer);
        }

        int HitIndex(Point p)
        {
            for (int i = 0; i < Colors.Length; i++)
            {
                Rectangle r = CellRect(i); r.Inflate(gap / 2, gap / 2);
                if (r.Contains(p)) return i;
            }
            if (FooterRect(0).Contains(p)) return 100;
            if (FooterRect(1).Contains(p)) return 101;
            return -1;
        }

        static bool SameRgb(Color a, Color b) { return a.R == b.R && a.G == b.G && a.B == b.B; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int sel = Program.D(5);
            for (int i = 0; i < Colors.Length; i++)
            {
                Rectangle r = CellRect(i);
                bool selected = current.A > 0 && SameRgb(current, Colors[i]);
                if (selected)
                {
                    Rectangle o = r; o.Inflate(sel, sel);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(74, 158, 230))) g.FillRectangle(b, o);
                }
                else if (hoverIdx == i)
                {
                    Rectangle o = r; o.Inflate(sel - 1, sel - 1);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(200, 222, 245))) g.FillRectangle(b, o);
                }
                using (SolidBrush b = new SolidBrush(Colors[i])) g.FillRectangle(b, r);
                if (Colors[i].GetBrightness() > 0.92f)
                    using (Pen p = new Pen(Color.FromArgb(205, 210, 218))) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
                if (selected) DrawStar(g, new PointF(r.Right + sel - Program.D(6), r.Top - sel + Program.D(6)), Program.D(9));
            }

            TextFormatFlags tf = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
            if (allowTransparent)
            {
                Rectangle t = FooterRect(0);
                bool tSel = current.A == 0;
                using (SolidBrush b = new SolidBrush(tSel ? Color.FromArgb(74, 158, 230) : (hoverIdx == 100 ? Color.FromArgb(200, 222, 245) : Color.White)))
                    g.FillRectangle(b, t);
                TextRenderer.DrawText(g, "⊘ 透明", Font, t, tSel ? Color.White : Color.Black, tf);
            }
            Rectangle m = FooterRect(1);
            using (SolidBrush b = new SolidBrush(hoverIdx == 101 ? Color.FromArgb(200, 222, 245) : Color.White)) g.FillRectangle(b, m);
            TextRenderer.DrawText(g, "🎨 その他の色…", Font, m, Color.Black, tf);
        }

        static void DrawStar(Graphics g, PointF c, float r)
        {
            PointF[] pts = new PointF[10];
            for (int i = 0; i < 10; i++)
            {
                double a = -Math.PI / 2 + i * Math.PI / 5;
                float rr = (i % 2 == 0) ? r : r * 0.45f;
                pts[i] = new PointF(c.X + (float)(Math.Cos(a) * rr), c.Y + (float)(Math.Sin(a) * rr));
            }
            using (SolidBrush b = new SolidBrush(Color.FromArgb(225, 232, 240))) g.FillPolygon(b, pts);
            using (Pen p = new Pen(Color.FromArgb(110, 120, 135), 1.2f)) g.DrawPolygon(p, pts);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitIndex(e.Location);
            if (h != hoverIdx) { hoverIdx = h; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hoverIdx = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int h = HitIndex(e.Location);
            if (h < 0) return;
            if (e.Button == MouseButtons.Right && h < Colors.Length)
            {
                // 右クリック: この色見本を変更
                Color? c = AskColor(Colors[h]);
                if (c.HasValue) { Colors[h] = c.Value; SaveColors(); Invalidate(); }
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            Color pick;
            if (h < Colors.Length) pick = Colors[h];
            else if (h == 100) pick = Color.Transparent;
            else
            {
                Color? c = AskColor(current.A == 0 ? Color.White : current);
                if (!c.HasValue) return;
                pick = c.Value;
            }
            // マウスイベント処理が終わってから閉じる・反映する
            ToolStripDropDown dd = dropDown;
            Action<Color> cb = onPick;
            Control a = anchor;
            if (a == null || a.IsDisposed) return;
            a.BeginInvoke((MethodInvoker)delegate
            {
                if (dd != null && !dd.IsDisposed && dd.Visible) dd.Close();
                if (cb != null) cb(pick);
            });
        }

        Color? AskColor(Color init)
        {
            if (dropDown != null) dropDown.AutoClose = false;
            try
            {
                using (ColorDialog cd = new ColorDialog())
                {
                    cd.FullOpen = true;
                    cd.Color = Color.FromArgb(255, init);
                    List<int> custom = new List<int>();
                    foreach (Color c in Colors) custom.Add(c.R | (c.G << 8) | (c.B << 16));
                    cd.CustomColors = custom.ToArray();
                    if (cd.ShowDialog(FindForm()) == DialogResult.OK) return cd.Color;
                }
                return null;
            }
            finally { if (dropDown != null) dropDown.AutoClose = true; }
        }
    }
}
