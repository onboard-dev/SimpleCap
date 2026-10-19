using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace SimpleCap
{
    // 色の指定欄: [最近よく使う3色] [スポイト] [ラベル(クリックで色見本)]
    public class ColorRow : Control
    {
        public string Label;
        public string Key;                 // 使用履歴の保存名 ("stroke" / "fill")
        public bool AllowTransparent;
        public Color Current = Color.Black;
        public event Action<Color> Picked; // 色が選ばれた(履歴への記録はこのクラスで行う)

        const int HistoryMax = 30;
        List<Color> history;               // 直近の使用履歴(新しいものが先頭)
        int hover = -1;                    // 0..2=色, 3=スポイト, 4=ラベル
        ToolTip tip = new ToolTip();

        static readonly Color[] Defaults = { Color.FromArgb(255, 69, 69), Color.FromArgb(0, 176, 145), Color.FromArgb(70, 90, 240) };

        public ColorRow(string label, string key, bool allowTransparent)
        {
            Label = label; Key = key; AllowTransparent = allowTransparent;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = Program.UiFont;
            Cursor = Cursors.Hand;
            LoadHistory();
        }

        // ---------- 使用履歴 ----------
        void LoadHistory()
        {
            history = new List<Color>();
            string v;
            if (Program.LoadSettings().TryGetValue("recent." + Key, out v))
                foreach (string s in v.Split(','))
                {
                    try { if (s.Trim() != "") history.Add(ColorTranslator.FromHtml(s.Trim())); } catch { }
                }
        }

        void SaveHistory()
        {
            List<string> a = new List<string>();
            foreach (Color c in history) a.Add(string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B));
            Dictionary<string, string> st = Program.LoadSettings();
            st["recent." + Key] = string.Join(",", a.ToArray());
            Program.SaveSettings(st);
        }

        public void RecordUse(Color c)
        {
            if (c.A == 0) return;
            history.Insert(0, Color.FromArgb(255, c));
            if (history.Count > HistoryMax) history.RemoveRange(HistoryMax, history.Count - HistoryMax);
            SaveHistory();
            Invalidate();
        }

        // 直近の履歴の中で使用回数が多い順(同数なら新しい順)に3色。足りなければ既定色で補う
        public Color[] TopColors()
        {
            Dictionary<int, int> count = new Dictionary<int, int>();
            Dictionary<int, int> first = new Dictionary<int, int>();
            for (int i = 0; i < history.Count; i++)
            {
                int k = history[i].ToArgb();
                int n; count.TryGetValue(k, out n); count[k] = n + 1;
                if (!first.ContainsKey(k)) first[k] = i;
            }
            List<int> keys = new List<int>(count.Keys);
            keys.Sort(delegate (int a, int b)
            {
                int c = count[b].CompareTo(count[a]);
                return c != 0 ? c : first[a].CompareTo(first[b]);
            });
            List<Color> res = new List<Color>();
            foreach (int k in keys) { if (res.Count < 3) res.Add(Color.FromArgb(k)); }
            foreach (Color d in Defaults)
            {
                if (res.Count >= 3) break;
                bool dup = false;
                foreach (Color r in res) if (r.ToArgb() == d.ToArgb()) dup = true;
                if (!dup) res.Add(d);
            }
            return res.ToArray();
        }

        // ---------- 配置 ----------
        Rectangle SwatchRect(int i)
        {
            int s = Height - Program.D(12);
            return new Rectangle(Program.D(6) + i * (s + Program.D(4)), Program.D(6), s, s);
        }
        Rectangle DropperRect()
        {
            Rectangle r = SwatchRect(2);
            return new Rectangle(r.Right + Program.D(4), Program.D(3), Height - Program.D(6), Height - Program.D(6));
        }
        Rectangle LabelRect()
        {
            Rectangle d = DropperRect();
            return new Rectangle(d.Right + Program.D(2), 0, Width - d.Right - Program.D(2), Height);
        }
        int HitPart(Point p)
        {
            for (int i = 0; i < 3; i++) if (SwatchRect(i).Contains(p)) return i;
            if (DropperRect().Contains(p)) return 3;
            if (p.X >= LabelRect().X) return 4;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitPart(e.Location);
            if (h != hover)
            {
                hover = h; Invalidate();
                string t = h >= 0 && h <= 2 ? "この色を使う" : h == 3 ? "スポイト:画面上の色を取る(Esc で中止)" : h == 4 ? "クリックで色見本・その他の色" : "";
                tip.SetToolTip(this, t);
            }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            int h = HitPart(e.Location);
            if (h >= 0 && h <= 2) Pick(TopColors()[h]);
            else if (h == 3)
            {
                Color? c = ScreenColorPicker.Pick();
                if (c.HasValue) Pick(c.Value);
            }
            else if (h == 4)
            {
                ColorPalette.Show(this, Current, AllowTransparent, delegate (Color c) { Pick(c); });
            }
        }

        void Pick(Color c)
        {
            RecordUse(c);
            if (Picked != null) Picked(c);
        }

        // ---------- 描画 ----------
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle box = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Tile.RoundRect(box, Program.D(5)))
            {
                g.FillPath(Brushes.White, p);
                using (Pen pen = new Pen(Color.FromArgb(0, 120, 215), 1.2f)) g.DrawPath(pen, p);
            }
            Color[] top = TopColors();
            for (int i = 0; i < 3; i++)
            {
                Rectangle r = SwatchRect(i);
                bool cur = Current.A > 0 && top[i].R == Current.R && top[i].G == Current.G && top[i].B == Current.B;
                if (cur || hover == i)
                {
                    Rectangle o = r; o.Inflate(Program.D(2), Program.D(2));
                    using (SolidBrush hb = new SolidBrush(cur ? Color.FromArgb(74, 158, 230) : Color.FromArgb(200, 222, 245))) g.FillRectangle(hb, o);
                }
                g.SmoothingMode = SmoothingMode.None;
                using (SolidBrush b = new SolidBrush(top[i])) g.FillRectangle(b, r);
                if (top[i].GetBrightness() > 0.9f) g.DrawRectangle(Pens.Silver, r.X, r.Y, r.Width - 1, r.Height - 1);
                g.SmoothingMode = SmoothingMode.AntiAlias;
            }
            Rectangle d = DropperRect();
            if (hover == 3) using (SolidBrush hb = new SolidBrush(Color.FromArgb(220, 234, 250))) g.FillRectangle(hb, d);
            DrawDropper(g, d);
            Rectangle l = LabelRect();
            if (hover == 4) using (SolidBrush hb = new SolidBrush(Color.FromArgb(232, 240, 250))) g.FillRectangle(hb, Rectangle.Inflate(l, -2, -3));
            // 現在の色を小さな丸で示す(透明なら斜線)
            int dot = Program.D(10);
            Rectangle dr = new Rectangle(l.X + Program.D(4), l.Y + (l.Height - dot) / 2, dot, dot);
            if (Current.A > 0) using (SolidBrush cb = new SolidBrush(Color.FromArgb(255, Current))) g.FillEllipse(cb, dr);
            g.DrawEllipse(Pens.Gray, dr);
            if (Current.A == 0) g.DrawLine(Pens.Red, dr.Left + 1, dr.Bottom - 1, dr.Right - 1, dr.Top + 1);
            TextRenderer.DrawText(g, Label, Font, new Rectangle(dr.Right + Program.D(4), l.Y, l.Right - dr.Right - Program.D(4), l.Height),
                Color.FromArgb(40, 40, 40), TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }

        // スポイトの絵
        static void DrawDropper(Graphics g, Rectangle r)
        {
            float s = Math.Min(r.Width, r.Height);
            float x0 = r.X + (r.Width - s) / 2f, y0 = r.Y + (r.Height - s) / 2f;
            PointF tip = new PointF(x0 + s * 0.18f, y0 + s * 0.82f);
            PointF mid = new PointF(x0 + s * 0.58f, y0 + s * 0.42f);
            using (Pen body = new Pen(Color.FromArgb(70, 80, 95), s * 0.13f))
            {
                body.StartCap = LineCap.Round; body.EndCap = LineCap.Round;
                g.DrawLine(body, tip, mid);
            }
            using (Pen ring = new Pen(Color.FromArgb(70, 80, 95), s * 0.1f))
                g.DrawLine(ring, x0 + s * 0.48f, y0 + s * 0.32f, x0 + s * 0.68f, y0 + s * 0.52f);
            using (SolidBrush bulb = new SolidBrush(Color.FromArgb(70, 80, 95)))
                g.FillEllipse(bulb, x0 + s * 0.6f, y0 + s * 0.12f, s * 0.28f, s * 0.28f);
        }
    }

    // スポイト:画面全体から色を1つ選ぶ (ルーペ付き、クリックで決定、Esc / 右クリックで中止)
    public class ScreenColorPicker : Form
    {
        Bitmap shot;
        Rectangle vs;
        Point mouse;
        Color picked;
        const int Cells = 11;

        public static Color? Pick()
        {
            Rectangle vs = SystemInformation.VirtualScreen;
            Bitmap shot = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppRgb);
            using (Graphics g = Graphics.FromImage(shot))
                g.CopyFromScreen(vs.Location, Point.Empty, vs.Size, CopyPixelOperation.SourceCopy);
            using (ScreenColorPicker f = new ScreenColorPicker(shot, vs))
            {
                Color? r = null;
                if (f.ShowDialog() == DialogResult.OK) r = f.picked;
                shot.Dispose();
                return r;
            }
        }

        ScreenColorPicker(Bitmap shot, Rectangle vs)
        {
            this.shot = shot; this.vs = vs;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = vs;
            TopMost = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            Cursor = Cursors.Cross;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.Opaque, true);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Bounds = vs;
            Activate();
            mouse = PointToClient(Cursor.Position);
            Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x02E0) return; // WM_DPICHANGED を無視
            base.WndProc(ref m);
        }

        Color ColorAt(Point p)
        {
            int x = Math.Max(0, Math.Min(shot.Width - 1, p.X)), y = Math.Max(0, Math.Min(shot.Height - 1, p.Y));
            return shot.GetPixel(x, y);
        }

        Rectangle LoupeRect(Point p)
        {
            int cell = Program.D(10);
            int w = Cells * cell, h = w + Program.D(26);
            int x = p.X + Program.D(20), y = p.Y + Program.D(20);
            if (x + w > Width) x = p.X - Program.D(20) - w;
            if (y + h > Height) y = p.Y - Program.D(20) - h;
            return new Rectangle(x, y, w, h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            Rectangle c = e.ClipRectangle;
            g.DrawImage(shot, c, c, GraphicsUnit.Pixel);
            g.CompositingMode = CompositingMode.SourceOver;

            // ルーペ
            Rectangle lr = LoupeRect(mouse);
            int cell = Program.D(10);
            int half = Cells / 2;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(shot, new Rectangle(lr.X, lr.Y, Cells * cell, Cells * cell),
                new Rectangle(mouse.X - half, mouse.Y - half, Cells, Cells), GraphicsUnit.Pixel);
            g.PixelOffsetMode = PixelOffsetMode.Default;
            using (Pen p = new Pen(Color.White, 2)) g.DrawRectangle(p, lr.X + half * cell, lr.Y + half * cell, cell, cell);
            using (Pen p = new Pen(Color.Black, 1)) g.DrawRectangle(p, lr.X + half * cell - 1, lr.Y + half * cell - 1, cell + 2, cell + 2);
            g.DrawRectangle(Pens.Black, lr.X, lr.Y, Cells * cell, Cells * cell);
            Color col = ColorAt(mouse);
            Rectangle info = new Rectangle(lr.X, lr.Y + Cells * cell, Cells * cell + 1, Program.D(26));
            g.FillRectangle(Brushes.White, info);
            g.DrawRectangle(Pens.Black, info);
            Rectangle sw = new Rectangle(info.X + Program.D(4), info.Y + Program.D(4), Program.D(18), info.Height - Program.D(8));
            using (SolidBrush b = new SolidBrush(col)) g.FillRectangle(b, sw);
            g.DrawRectangle(Pens.Gray, sw);
            TextRenderer.DrawText(g, string.Format("#{0:X2}{1:X2}{2:X2}", col.R, col.G, col.B), Program.UiFont,
                new Rectangle(sw.Right + Program.D(4), info.Y, info.Right - sw.Right - Program.D(4), info.Height), Color.Black,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Rectangle old = LoupeRect(mouse);
            mouse = e.Location;
            Rectangle now = LoupeRect(mouse);
            old.Inflate(4, 4); now.Inflate(4, 4);
            Invalidate(old); Invalidate(now);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { DialogResult = DialogResult.Cancel; return; }
            if (e.Button != MouseButtons.Left) return;
            picked = ColorAt(e.Location);
            DialogResult = DialogResult.OK;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
            base.OnKeyDown(e);
        }
    }
}
