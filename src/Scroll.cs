using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SimpleCap
{
    // ================= スクロールキャプチャ =================
    //  選んだ範囲を一定間隔で撮影し、前の画像との「ずれ(スクロール量)」を見つけて、新しく見えた部分を下につなげていく。
    //  上下に動かない部分(固定ヘッダー・フッター)は自動で見分けて、重ねずに 1 回だけ使う。
    public class ScrollStitcher
    {
        const int MaxHeight = 30000;   // つなげる高さの上限(px)
        readonly Rectangle area;
        readonly List<Bitmap> strips = new List<Bitmap>();   // つなげる帯(上から順)
        Bitmap prev;                                          // 最後に「つなげた」画像
        ulong[] prevRows;
        int cut;                                              // 最後の画像のこの行まではつなげ済み(この下は最後に足す)
        public int Height { get; private set; }
        public int Count { get; private set; }               // つなげた回数
        public bool Full { get { return Height >= MaxHeight; } }

        public ScrollStitcher(Rectangle area) { this.area = area; }

        Bitmap Grab()
        {
            Bitmap b = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppRgb);
            using (Graphics g = Graphics.FromImage(b))
                g.CopyFromScreen(area.Location, Point.Empty, area.Size, CopyPixelOperation.SourceCopy);
            return b;
        }

        // 行ごとのハッシュ(左右の端は除く:スクロールバーが動くため)
        static ulong[] RowHashes(Bitmap b)
        {
            int w = b.Width, h = b.Height;
            int x0 = w * 3 / 100, x1 = Math.Max(x0 + 1, w - Math.Max(24, w * 4 / 100));
            ulong[] r = new ulong[h];
            BitmapData bd = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                int[] row = new int[w];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), row, 0, w);
                    ulong hs = 14695981039346656037UL;
                    for (int x = x0; x < x1; x++) { hs ^= (uint)(row[x] & 0xFFFFFF); hs *= 1099511628211UL; }
                    r[y] = hs;
                }
            }
            finally { b.UnlockBits(bd); }
            return r;
        }

        // 1 回分の処理。新しくつなげたら true
        public bool Tick()
        {
            if (Full) return false;
            return TickWith(Grab());
        }

        // cur は呼び出し後この中で管理する(破棄または保持)
        public bool TickWith(Bitmap cur)
        {
            if (Full) { cur.Dispose(); return false; }
            ulong[] rows = RowHashes(cur);
            int h = rows.Length;
            if (prev == null)
            {
                prev = cur; prevRows = rows;
                strips.Add(new Bitmap(cur));
                Height = h; Count = 1; cut = h;
                return true;
            }
            // 上下の固定部分(同じ位置で同じ行)
            int top = 0; while (top < h && rows[top] == prevRows[top]) top++;
            if (top >= h) { cur.Dispose(); return false; }   // 何も変わっていない
            int bot = 0; while (bot < h - top && rows[h - 1 - bot] == prevRows[h - 1 - bot]) bot++;
            int len = h - top - bot;
            // ずれ dy を探す:前の画像の行 i+dy と今の画像の行 i が一致する割合がいちばん高いもの。
            // 背景だけの行(いちばん多い行)は手がかりにならないので数えない
            Dictionary<ulong, int> freq = new Dictionary<ulong, int>();
            for (int i = top; i < h - bot; i++) { int c; freq.TryGetValue(rows[i], out c); freq[rows[i]] = c + 1; }
            ulong bg = 0; int bgN = 0;
            foreach (KeyValuePair<ulong, int> kv in freq) if (kv.Value > bgN) { bgN = kv.Value; bg = kv.Key; }
            bool skipBg = bgN > len / 10;
            int bestDy = -1, bestMatch = 0; double bestScore = 0;
            int minOverlap = Math.Max(12, len / 8);
            for (int dy = 1; dy <= len - minOverlap; dy++)
            {
                int n = len - dy, match = 0, inf = 0;
                for (int i = 0; i < n; i++)
                {
                    ulong r = rows[top + i];
                    if (skipBg && r == bg) continue;
                    inf++;
                    if (prevRows[top + i + dy] == r) match++;
                }
                if (inf < 8) continue;
                double sc = match / (double)inf;
                // 一致率が高いもの、同じなら一致した行が多いもの(=ずれが小さいもの)
                if (sc > bestScore + 1e-9 || (Math.Abs(sc - bestScore) <= 1e-9 && match > bestMatch)) { bestScore = sc; bestDy = dy; bestMatch = match; }
            }
            if (bestDy < 0 || bestScore < 0.85) { cur.Dispose(); return false; }   // 上へ戻した・大きく飛んだなど → 使わない

            // つなげ済みの部分が下の固定部分にかかっていたら、その分を外す(固定部分は最後に 1 回だけ付ける)
            int limit = h - bot;
            if (cut > limit) { TrimLast(cut - limit); cut = limit; }
            // 前の画像の行 cut 以降が新しい部分。今の画像では cut - dy 行目から
            int y0 = Math.Max(0, cut - bestDy);
            int add = Math.Min(limit - y0, MaxHeight - Height);
            if (add > 0)
            {
                Bitmap strip = cur.Clone(new Rectangle(0, y0, cur.Width, add), PixelFormat.Format32bppRgb);
                strips.Add(strip);
                Height += add;
            }
            cut = limit;
            prev.Dispose();
            prev = cur; prevRows = rows;
            Count++;
            return true;
        }

        // つなげた帯の下から n 行を外す(短い帯が続くときは複数の帯にまたがって)
        void TrimLast(int n)
        {
            while (n > 0 && strips.Count > 1)
            {
                Bitmap last = strips[strips.Count - 1];
                if (last.Height <= n)
                {
                    n -= last.Height; Height -= last.Height;
                    last.Dispose(); strips.RemoveAt(strips.Count - 1);
                    continue;
                }
                Bitmap t = last.Clone(new Rectangle(0, 0, last.Width, last.Height - n), PixelFormat.Format32bppRgb);
                last.Dispose();
                strips[strips.Count - 1] = t;
                Height -= n;
                n = 0;
            }
            if (n > 0 && strips.Count == 1 && strips[0].Height > n)
            {
                Bitmap first = strips[0];
                Bitmap t = first.Clone(new Rectangle(0, 0, first.Width, first.Height - n), PixelFormat.Format32bppRgb);
                first.Dispose();
                strips[0] = t;
                Height -= n;
            }
        }

        // つなげた 1 枚の画像を作る
        public Bitmap Build()
        {
            if (strips.Count == 0) return null;
            int w = strips[0].Width;
            int band = prev != null ? Math.Max(0, prev.Height - cut) : 0;   // まだ付けていない下の部分(固定フッターなど)
            Bitmap b = new Bitmap(w, Height + band, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                int y = 0;
                foreach (Bitmap s in strips) { g.DrawImageUnscaled(s, 0, y); y += s.Height; }
                if (band > 0) g.DrawImage(prev, new Rectangle(0, y, w, band), new Rectangle(0, prev.Height - band, w, band), GraphicsUnit.Pixel);
            }
            return b;
        }

        public void Dispose()
        {
            foreach (Bitmap s in strips) s.Dispose();
            strips.Clear();
            if (prev != null) { prev.Dispose(); prev = null; }
        }
    }

    // スクロールキャプチャ中の操作バー(終了 / 中止)
    public class ScrollBarForm : Form
    {
        Label lbl;
        Timer timer;
        ScrollStitcher st;
        List<RecFrame> frames = new List<RecFrame>();
        public bool Cancelled;
        public event EventHandler Finished;

        public ScrollBarForm(Rectangle area)
        {
            st = new ScrollStitcher(area);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Font = Program.UiFont;
            BackColor = Color.FromArgb(40, 40, 44);
            ClientSize = new Size(Program.D(420), Program.D(58));
            lbl = new Label();
            lbl.ForeColor = Color.White;
            lbl.SetBounds(Program.D(10), Program.D(2), Program.D(200), Program.D(54));
            Controls.Add(lbl);
            Button ok = new Button();
            ok.Text = "■ キャプチャ終了";
            ok.FlatStyle = FlatStyle.Flat;
            ok.BackColor = Color.FromArgb(0xCD, 0xB9, 0xF1);
            ok.SetBounds(Program.D(214), Program.D(12), Program.D(126), Program.D(34));
            ok.Click += delegate { Finish(false); };
            Controls.Add(ok);
            Button cancel = new Button();
            cancel.Text = "✖ 中止";
            cancel.FlatStyle = FlatStyle.Flat;
            cancel.ForeColor = Color.White;
            cancel.SetBounds(Program.D(346), Program.D(12), Program.D(66), Program.D(34));
            cancel.Click += delegate { Finish(true); };
            Controls.Add(cancel);

            // バーの位置:範囲の下 → 上 → 範囲の中(右下)
            Rectangle wa = Screen.FromRectangle(area).WorkingArea;
            int x = Math.Max(wa.Left, Math.Min(wa.Right - Width, area.Right - Width));
            int y = area.Bottom + Program.D(6);
            if (y + Height > wa.Bottom) y = area.Top - Height - Program.D(6);
            if (y < wa.Top) y = area.Bottom - Height - Program.D(8);
            Location = new Point(x, y);

            int t = Program.D(3);
            Rectangle[] rs = {
                new Rectangle(area.Left - t, area.Top - t, area.Width + t * 2, t),
                new Rectangle(area.Left - t, area.Bottom, area.Width + t * 2, t),
                new Rectangle(area.Left - t, area.Top, t, area.Height),
                new Rectangle(area.Right, area.Top, t, area.Height) };
            foreach (Rectangle r in rs) { RecFrame f = new RecFrame(r); f.BackColor = Color.FromArgb(130, 80, 220); frames.Add(f); }

            timer = new Timer();
            timer.Interval = 80;
            timer.Tick += delegate
            {
                try { st.Tick(); } catch { }
                UpdateText();
                if (st.Full) { timer.Stop(); lbl.Text += "\n(高さの上限に達しました)"; }
            };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        void UpdateText()
        {
            lbl.Text = string.Format("📜 ゆっくり下へスクロールしてください\n{0} 回つなげました(高さ {1} px)", Math.Max(0, st.Count - 1), st.Height);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            VideoRecorder.ExcludeFromCapture(this);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            foreach (RecFrame f in frames) f.Show();
            try { st.Tick(); } catch { }   // 最初の 1 枚
            UpdateText();
            timer.Start();
        }

        void Finish(bool cancel)
        {
            timer.Stop();
            if (!cancel) { try { st.Tick(); } catch { } }   // 終了直前の状態も取り込む
            Cancelled = cancel;
            if (Finished != null) Finished(this, EventArgs.Empty);
            Close();
        }

        // 結果の画像(中止なら null)
        public Bitmap TakeResult()
        {
            if (Cancelled) return null;
            return st.Build();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop(); timer.Dispose();
            foreach (RecFrame f in frames) { f.Close(); f.Dispose(); }
            st.Dispose();
            base.OnFormClosed(e);
        }
    }
}
