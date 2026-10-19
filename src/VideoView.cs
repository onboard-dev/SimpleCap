using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace SimpleCap
{
    // ================= ブラウズ画面のビデオ編集 =================
    //  再生 / コマ送り / IN・OUT 点(複数区間)/ 生成(MP4・GIF)
    public class VideoView : Control
    {
        public VideoInfo Info;
        List<string> frames = new List<string>();
        int cur;              // 表示中のコマ
        int pendingIn = -1;   // IN を押して OUT 待ちのコマ
        Bitmap curBmp;
        int curBmpIdx = -1;

        Pic pic;
        public ZoomBox Zoomer;   // 映像を拡大・縮小して表示
        Timeline line;
        Panel bar;
        Button btnPlay, btnIn, btnOut, btnGen;
        Button btnMerge;   // 一覧で 2 つ以上選んだときだけ表示(押すとブラウズ画面が結合する)
        public event EventHandler MergeClicked;
        bool mergeAvail;
        public bool MergeAvailable
        {
            get { return mergeAvail; }
            set { if (mergeAvail == value) return; mergeAvail = value; if (btnMerge != null) { btnMerge.Visible = value; DoLayout(); } }
        }
        CheckBox chkSegOnly;
        Label lblTime;
        ListBox lstSeg;
        Button btnDelSeg, btnClear;
        ToolTip tip = new ToolTip();
        System.Windows.Forms.Timer playTimer;
        Stopwatch playClock = new Stopwatch();
        int playBase;

        public event EventHandler Changed;           // 表示 / 区間が変わった(ステータス更新用)
        public event Action<string> FileCreated;     // MP4 / GIF を書き出した(一覧に追加する用)
        // ---- 注釈 ----
        public List<Obj> Annos = new List<Obj>();
        public event EventHandler AnnotateToggled;   // 「✏️ 注釈」ボタン(編集の開始 / 終了はブラウズ画面が行う)
        public event Action<int> FrameShown;         // 表示するコマが変わった
        public event EventHandler AnnoTimeChanged;   // 注釈の表示範囲を変えた
        public Func<Obj> SelectedAnno;               // 選択中の注釈(編集中のみ)
        public bool Annotating;
        Control editor;                              // 注釈編集中はキャンバスをここに置く
        Button btnAnno, btnFrom, btnTo, btnAll;
        Button btnPrev, btnNext;   // コマ送り(並べ方は DoLayout で。Controls の順番に頼らない)
        Label lblRange;
        public int Current { get { return cur; } }
        public Bitmap CurrentFrame { get { return curBmp; } }
        public event Action<string> Message;         // ステータスバーへのメッセージ

        public bool IsOpen { get { return Info != null; } }
        public string PathOpen { get { return Info != null ? Info.Dir : null; } }

        public VideoView()
        {
            BackColor = Color.FromArgb(64, 64, 68);
            pic = new Pic(this);
            Zoomer = new ZoomBox(pic);
            Controls.Add(Zoomer);
            bar = new Panel();
            bar.BackColor = Color.FromArgb(245, 246, 248);
            Controls.Add(bar);

            line = new Timeline(this);
            bar.Controls.Add(line);

            btnPlay = Btn("▶ 再生", "再生 / 一時停止 (Space)", delegate { TogglePlay(); });
            Button prev = btnPrev = Btn("◀", "1コマ戻る (←)", delegate { Step(-1); });
            Button next = btnNext = Btn("▶", "1コマ進む (→)", delegate { Step(1); });
            btnIn = Btn("[ IN", "IN 点:ここから使う (I)", delegate { MarkIn(); });
            btnOut = Btn("OUT ]", "OUT 点:ここまで使う (O)\nIN を押さずに OUT だけ押すと、前の区間の続き(または先頭)から", delegate { MarkOut(); });
            chkSegOnly = new CheckBox();
            chkSegOnly.Text = "区間だけ再生";
            chkSegOnly.AutoSize = true;
            tip.SetToolTip(chkSegOnly, "IN〜OUT の区間だけをつないで再生(生成されるムービーの確認用)");
            lblTime = new Label();
            lblTime.AutoSize = false;
            lblTime.TextAlign = ContentAlignment.MiddleLeft;
            lblTime.Font = new Font("Consolas", 9.5f);
            btnGen = Btn("🎬 生成…", "IN〜OUT の区間をつないだムービーを作る(MP4 / GIF)\n区間がなければ全体", delegate { ShowExportMenu(btnGen, new Point(0, btnGen.Height)); });
            btnGen.BackColor = Color.FromArgb(0x94, 0xD0, 0xFF);
            btnMerge = Btn("🔗 結合", "一覧で選んだビデオ / GIF / MP4 を、並び順に 1 本につなぐ\n(大きさは最初のものに合わせます。すべて GIF なら GIF、それ以外は MP4)",
                delegate { if (MergeClicked != null) MergeClicked(this, EventArgs.Empty); });
            btnMerge.BackColor = Color.FromArgb(0xB9, 0xE4, 0xB0);
            btnMerge.Visible = false;
            bar.Controls.Add(btnMerge);
            btnAnno = Btn("✏️ 注釈", "矢印・文字・枠などの注釈を書き込む(もう一度押すと編集を終了)\n注釈は書き出す MP4 / GIF に焼き込まれます", delegate { if (AnnotateToggled != null) AnnotateToggled(this, EventArgs.Empty); });
            btnAnno.BackColor = Color.FromArgb(0xF6, 0xC9, 0xA1);
            bar.Controls.Add(btnAnno);
            lblRange = new Label(); lblRange.TextAlign = ContentAlignment.MiddleLeft; lblRange.ForeColor = Color.FromArgb(30, 90, 180);
            btnFrom = Btn("ここから", "選んだ注釈を、今のコマから表示する", delegate { SetAnnoRange(0); });
            btnTo = Btn("ここまで", "選んだ注釈を、今のコマまで表示する", delegate { SetAnnoRange(1); });
            btnAll = Btn("全体", "選んだ注釈をビデオ全体で表示する", delegate { SetAnnoRange(2); });
            foreach (Control c in new Control[] { lblRange, btnFrom, btnTo, btnAll }) { c.Visible = false; bar.Controls.Add(c); }
            foreach (Control c in new Control[] { btnPlay, prev, next, btnIn, btnOut, chkSegOnly, lblTime, btnGen }) bar.Controls.Add(c);
            btnPlay.Tag = "w74"; prev.Tag = "w32"; next.Tag = "w32"; btnIn.Tag = "w50"; btnOut.Tag = "w56"; btnGen.Tag = "w96";

            lstSeg = new ListBox();
            lstSeg.IntegralHeight = false;
            lstSeg.SelectedIndexChanged += delegate
            {
                if (lstSeg.SelectedIndex >= 0 && Info != null && lstSeg.SelectedIndex < Info.Segments.Count)
                    Seek(Info.Segments[lstSeg.SelectedIndex][0]);
            };
            bar.Controls.Add(lstSeg);
            btnDelSeg = Btn("🗑 区間を削除", "一覧で選んだ区間を削除 (Delete)", delegate { DeleteSegment(); });
            btnClear = Btn("すべてクリア", "区間をすべて消す", delegate
            {
                if (Info == null || Info.Segments.Count == 0) return;
                Info.Segments.Clear(); pendingIn = -1; SaveInfo();
            });
            bar.Controls.Add(btnDelSeg); bar.Controls.Add(btnClear);

            playTimer = new System.Windows.Forms.Timer();
            playTimer.Interval = 15;
            playTimer.Tick += delegate { PlayTick(); };

            ContextMenuStrip m = new ContextMenuStrip();
            m.Items.Add("🎬 MP4 で書き出し…", null, delegate { Export("mp4"); });
            m.Items.Add("🎞️ GIF で書き出し…", null, delegate { Export("gif"); });
            pic.ContextMenuStrip = m;
            DoLayout();
        }

        Button Btn(string text, string tipText, EventHandler click)
        {
            Button b = new Button();
            b.Text = text;
            b.Click += click;
            tip.SetToolTip(b, tipText);
            return b;
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); DoLayout(); }

        void DoLayout()
        {
            if (btnClear == null) return;
            int W = ClientSize.Width, H = ClientSize.Height;
            int bh = Program.D(170);
            bar.SetBounds(0, Math.Max(0, H - bh), W, bh);
            Zoomer.SetBounds(0, 0, W, Math.Max(0, H - bh));
            int m = Program.D(8);
            line.SetBounds(m, Program.D(6), Math.Max(10, W - m * 2), Program.D(46));
            int x = m, y = Program.D(58), h = Program.D(30);
            foreach (Control c in new Control[] { btnPlay, btnPrev, btnNext, btnIn, btnOut })
            {
                int w = Program.D(int.Parse(((string)c.Tag).Substring(1)));
                c.SetBounds(x, y, w, h); x += w + Program.D(4);
            }
            x += Program.D(4);
            chkSegOnly.Location = new Point(x, y + Program.D(5)); x += chkSegOnly.PreferredSize.Width + Program.D(10);
            int gw = Program.D(96);
            // 「✔ 注釈を終了」でも切れないよう、文字の幅に合わせる
            int aw = Math.Max(Program.D(86), TextRenderer.MeasureText(btnAnno.Text, btnAnno.Font).Width + Program.D(20));
            // 右端から:🔗 結合(選んでいるときだけ)→ 🎬 生成 → ✏️ 注釈
            int right = W - m;
            if (btnMerge.Visible)
            {
                int mw = Program.D(80);
                btnMerge.SetBounds(right - mw, y, mw, h);
                right -= mw + Program.D(4);
            }
            btnGen.SetBounds(right - gw, y, gw, h);
            btnAnno.SetBounds(right - gw - Program.D(4) - aw, y, aw, h);
            lblTime.SetBounds(x, y, Math.Max(10, right - gw - aw - Program.D(4) - x - Program.D(6)), h);
            int ly = y + h + Program.D(6);
            int bw = Program.D(110);
            int annoW = Annotating ? Program.D(330) : 0;   // 注釈編集中は「表示: ここから / ここまで / 全体」
            lstSeg.SetBounds(m, ly, Math.Max(10, W - m * 3 - bw - annoW), Math.Max(10, bh - ly - Program.D(8)));
            if (Annotating)
            {
                int ax = W - m * 2 - bw - annoW + Program.D(6);
                lblRange.SetBounds(ax, ly, annoW - Program.D(6), h);
                int bx = ax, bwid = Program.D(96);
                btnFrom.SetBounds(bx, ly + h + Program.D(4), bwid, h);
                btnTo.SetBounds(bx + bwid + Program.D(4), ly + h + Program.D(4), bwid, h);
                btnAll.SetBounds(bx + (bwid + Program.D(4)) * 2, ly + h + Program.D(4), Program.D(80), h);
            }
            if (editor != null) editor.Bounds = Zoomer.Bounds;
            btnDelSeg.SetBounds(W - m - bw, ly, bw, h);
            btnClear.SetBounds(W - m - bw, ly + h + Program.D(4), bw, h);
        }

        // ---------- 開く / 閉じる ----------
        public void Open(string dir)
        {
            CloseVideo();
            Info = VideoInfo.Load(dir);
            Annos = Info.LoadAnnotations();
            frames = Info.Frames();
            cur = 0; pendingIn = -1;
            if (frames.Count > 0 && (Info.Width <= 0 || Info.Height <= 0))
                using (Bitmap b = LoadFrame(0)) if (b != null) { Info.Width = b.Width; Info.Height = b.Height; }
            Zoomer.Natural = new Size(Info.Width, Info.Height);
            Zoomer.Reset();
            RefreshSegments();
            ShowFrame(0);
        }

        public void CloseVideo()
        {
            StopPlay();
            Info = null;
            frames.Clear();
            if (curBmp != null) { curBmp.Dispose(); curBmp = null; }
            curBmpIdx = -1;
            lstSeg.Items.Clear();
            pic.Invalidate(); line.Invalidate();
        }

        Bitmap LoadFrame(int i)
        {
            if (i < 0 || i >= frames.Count) return null;
            try { return Doc.BytesToBitmap(File.ReadAllBytes(frames[i])); } catch { return null; }
        }

        void ShowFrame(int i)
        {
            if (frames.Count == 0) { cur = 0; pic.Invalidate(); line.Invalidate(); UpdateTime(); return; }
            cur = Math.Max(0, Math.Min(frames.Count - 1, i));
            if (curBmpIdx != cur)
            {
                Bitmap b = LoadFrame(cur);
                if (b != null) { if (curBmp != null) curBmp.Dispose(); curBmp = b; curBmpIdx = cur; }
            }
            pic.Invalidate(); line.Invalidate();
            UpdateTime();
            if (FrameShown != null) FrameShown(cur);
        }

        string T(int frame) { double s = frame / (double)(Info != null ? Info.Fps : 15); return string.Format("{0:00}:{1:00.00}", (int)(s / 60), s % 60); }

        void UpdateTime()
        {
            if (Info == null) { lblTime.Text = ""; return; }
            int sel = SelectedFrameCount();
            lblTime.Text = string.Format("{0} / {1}  ({2}/{3}コマ)  出力 {4}", T(cur), T(Math.Max(0, frames.Count - 1)), cur + 1, frames.Count,
                T(sel) + (Info.Segments.Count > 0 ? "" : " 全体"));
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        public string StatusText
        {
            get
            {
                if (Info == null) return "";
                return string.Format("🎥 ビデオ {0} × {1}  {2}fps  {3}   |   Space=再生  ←→=コマ送り  I=IN  O=OUT  Ctrl+S=書き出し(MP4 / GIF)  右クリック=書き出し",
                    Info.Width, Info.Height, Info.Fps, T(frames.Count));
            }
        }

        // ---------- 再生 ----------
        void TogglePlay() { if (playTimer.Enabled) StopPlay(); else StartPlay(); }

        void StartPlay()
        {
            if (frames.Count == 0) return;
            if (chkSegOnly.Checked && Info.Segments.Count > 0 && !InSegment(cur)) cur = Info.Segments[0][0];
            else if (cur >= frames.Count - 1) cur = 0;
            playBase = cur;
            playClock.Reset(); playClock.Start();
            playTimer.Start();
            btnPlay.Text = "⏸ 停止";
        }

        public void StopPlayback() { StopPlay(); }

        // ---------- 注釈の編集(キャンバスを映像の場所に置く) ----------
        public void SetEditor(Control c)
        {
            editor = c;
            Annotating = c != null;
            if (c != null) { Controls.Add(c); c.Bounds = Zoomer.Bounds; c.BringToFront(); Zoomer.Visible = false; }
            else Zoomer.Visible = true;
            btnAnno.Text = Annotating ? "✔ 注釈を終了" : "✏️ 注釈";
            foreach (Control x in new Control[] { lblRange, btnFrom, btnTo, btnAll }) x.Visible = Annotating;
            DoLayout();
            RefreshAnnoUi();
            pic.Invalidate();
        }

        public void RefreshAnnoUi()
        {
            line.Invalidate();
            if (!Annotating) return;
            Obj o = SelectedAnno != null ? SelectedAnno() : null;
            bool on = o != null;
            btnFrom.Enabled = btnTo.Enabled = btnAll.Enabled = on;
            if (!on) lblRange.Text = "注釈を選ぶと、表示する範囲を決められます(新しい注釈は全体に表示)";
            else lblRange.Text = "選んだ注釈の表示: " + T(o.TStart) + " 〜 " + (o.TEnd < 0 ? "最後" : T(o.TEnd));
        }

        // 0=ここから 1=ここまで 2=全体
        void SetAnnoRange(int mode)
        {
            Obj o = SelectedAnno != null ? SelectedAnno() : null;
            if (o == null) return;
            if (mode == 0) { o.TStart = cur; if (o.TEnd >= 0 && o.TEnd < cur) o.TEnd = -1; }
            else if (mode == 1) { o.TEnd = cur; if (o.TStart > cur) o.TStart = 0; }
            else { o.TStart = 0; o.TEnd = -1; }
            if (AnnoTimeChanged != null) AnnoTimeChanged(this, EventArgs.Empty);
            RefreshAnnoUi();
        }

        public void StepFrame(int d) { Step(d); }
        public void TogglePlayback() { TogglePlay(); }

        void StopPlay()
        {
            playTimer.Stop(); playClock.Stop();
            btnPlay.Text = "▶ 再生";
        }

        bool InSegment(int f)
        {
            foreach (int[] s in Info.Segments) if (f >= s[0] && f <= s[1]) return true;
            return false;
        }

        void PlayTick()
        {
            if (Info == null) { StopPlay(); return; }
            int f = playBase + (int)(playClock.ElapsedMilliseconds * Info.Fps / 1000);
            if (chkSegOnly.Checked && Info.Segments.Count > 0 && !InSegment(f))
            {
                // 次の区間へ飛ぶ
                int next = -1;
                foreach (int[] s in Info.Segments) if (s[0] > cur) { next = s[0]; break; }
                if (next < 0) { ShowFrame(Info.Segments[Info.Segments.Count - 1][1]); StopPlay(); return; }
                playBase = next; playClock.Reset(); playClock.Start(); f = next;
            }
            if (f >= frames.Count) { ShowFrame(frames.Count - 1); StopPlay(); return; }
            if (f != cur) ShowFrame(f);
        }

        void Step(int d) { StopPlay(); ShowFrame(cur + d); }
        public void Seek(int f) { StopPlay(); ShowFrame(f); }

        // ---------- IN / OUT ----------
        void MarkIn()
        {
            if (Info == null) return;
            pendingIn = cur;
            line.Invalidate();
            if (Message != null) Message("[ IN を " + T(cur) + " に置きました。OUT の位置で O を押してください");
        }

        void MarkOut()
        {
            if (Info == null || frames.Count == 0) return;
            int a = pendingIn;
            if (a < 0)
            {
                // IN なし → 直前の区間の続き(なければ先頭)から
                a = 0;
                foreach (int[] s in Info.Segments) if (s[1] < cur) a = Math.Max(a, s[1] + 1);
            }
            int b = cur;
            if (b < a) { int t = a; a = b; b = t; }
            Info.Segments.Add(new int[] { a, b });
            pendingIn = -1;
            SaveInfo();
            if (Message != null) Message(string.Format("区間 {0} 〜 {1} を追加しました(区間 {2} 個)", T(a), T(b), Info.Segments.Count));
        }

        void DeleteSegment()
        {
            if (Info == null) return;
            int i = lstSeg.SelectedIndex;
            if (i < 0 || i >= Info.Segments.Count) return;
            Info.Segments.RemoveAt(i);
            SaveInfo();
            if (lstSeg.Items.Count > 0) lstSeg.SelectedIndex = Math.Min(i, lstSeg.Items.Count - 1);
        }

        void SaveInfo()
        {
            Info.Normalize();
            try { Info.Save(); } catch { }
            RefreshSegments();
            line.Invalidate();
            UpdateTime();
        }

        void RefreshSegments()
        {
            lstSeg.BeginUpdate();
            lstSeg.Items.Clear();
            if (Info != null)
            {
                int n = 1;
                foreach (int[] s in Info.Segments)
                    lstSeg.Items.Add(string.Format("{0}.  {1}  〜  {2}   ({3:0.00} 秒)", n++, T(s[0]), T(s[1]), (s[1] - s[0] + 1) / (double)Info.Fps));
                if (Info.Segments.Count == 0) lstSeg.Items.Add("(区間なし:IN / OUT を置くと、その部分だけをつないだムービーを作れます。なければ全体)");
            }
            lstSeg.EndUpdate();
        }

        int SelectedFrameCount()
        {
            if (Info == null) return 0;
            if (Info.Segments.Count == 0) return frames.Count;
            int n = 0;
            foreach (int[] s in Info.Segments) n += Math.Min(frames.Count - 1, s[1]) - s[0] + 1;
            return n;
        }

        List<int> SelectedFrames()
        {
            List<int> l = new List<int>();
            if (Info.Segments.Count == 0) { for (int i = 0; i < frames.Count; i++) l.Add(i); return l; }
            foreach (int[] s in Info.Segments)
                for (int i = Math.Max(0, s[0]); i <= Math.Min(frames.Count - 1, s[1]); i++) l.Add(i);
            return l;
        }

        // ---------- キー操作(ブラウズ画面から呼ばれる) ----------
        public bool HandleKey(Keys k)
        {
            if (Info == null) return false;
            switch (k)
            {
                case Keys.Space: TogglePlay(); return true;
                case Keys.Left: Step(-1); return true;
                case Keys.Right: Step(1); return true;
                case Keys.Shift | Keys.Left: Step(-Info.Fps); return true;
                case Keys.Shift | Keys.Right: Step(Info.Fps); return true;
                case Keys.Home: Seek(0); return true;
                case Keys.End: Seek(frames.Count - 1); return true;
                case Keys.I: MarkIn(); return true;
                case Keys.O: MarkOut(); return true;
                case Keys.Delete: DeleteSegment(); return true;
                case Keys.Escape:
                    if (playTimer.Enabled) { StopPlay(); return true; }
                    if (pendingIn >= 0) { pendingIn = -1; line.Invalidate(); return true; }
                    return false;
            }
            return false;
        }

        // ---------- 書き出し ----------
        public void ShowExportMenu(Control c, Point p)
        {
            if (Info == null) return;
            pic.ContextMenuStrip.Show(c, p);
        }

        public void ExportDialog()
        {
            if (Info == null) return;
            Export(null);
        }

        // fmt = "mp4" / "gif" / null(保存ダイアログで選ぶ)
        public void Export(string fmt)
        {
            if (Info == null || frames.Count == 0) return;
            StopPlay();
            IWin32Window owner = FindForm();
            string name = Path.GetFileNameWithoutExtension(Info.Dir);
            string outPath;
            using (SaveFileDialog sd = new SaveFileDialog())
            {
                if (fmt == "gif") sd.Filter = "GIF アニメーション|*.gif";
                else if (fmt == "mp4") sd.Filter = "MP4 ムービー|*.mp4";
                else sd.Filter = "MP4 ムービー|*.mp4|GIF アニメーション|*.gif";
                sd.InitialDirectory = Program.LibDir;
                sd.FileName = name + (fmt == "gif" ? ".gif" : ".mp4");
                if (sd.ShowDialog(owner) != DialogResult.OK) return;
                outPath = sd.FileName;
            }
            bool gif = Path.GetExtension(outPath).Equals(".gif", StringComparison.OrdinalIgnoreCase);
            if (!gif && !Path.GetExtension(outPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase)) outPath += ".mp4";

            string exe = FFmpeg.Ensure(owner);
            if (exe == null) return;

            // 使うコマを連番で一時フォルダへ(IN〜OUT の区間だけをつなぐ)
            List<int> sel = SelectedFrames();
            if (sel.Count == 0) return;
            string tmp = Path.Combine(Path.GetTempPath(), "SimpleCap_" + Guid.NewGuid().ToString("N"));
            try
            {
                Cursor.Current = Cursors.WaitCursor;
                Directory.CreateDirectory(tmp);
                // 注釈があるコマは描き込んでから、ないコマはそのままコピー
                System.Drawing.Imaging.ImageCodecInfo jpgc = null;
                foreach (System.Drawing.Imaging.ImageCodecInfo c in System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders())
                    if (c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid) jpgc = c;
                System.Drawing.Imaging.EncoderParameters ep = new System.Drawing.Imaging.EncoderParameters(1);
                ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 95L);
                for (int i = 0; i < sel.Count; i++)
                {
                    string dst = Path.Combine(tmp, i.ToString("000000") + ".jpg");
                    bool any = false;
                    foreach (Obj o in Annos) if (o.Visible && VideoInfo.ShowAt(o, sel[i])) { any = true; break; }
                    if (!any) { File.Copy(frames[sel[i]], dst); continue; }
                    using (Bitmap fb = LoadFrame(sel[i]))
                    {
                        if (fb == null) { File.Copy(frames[sel[i]], dst); continue; }
                        using (Graphics g = Graphics.FromImage(fb)) VideoInfo.DrawAnnotations(g, fb, Annos, sel[i]);
                        fb.Save(dst, jpgc, ep);
                    }
                }
                Cursor.Current = Cursors.Default;

                string input = string.Format("-y -framerate {0} -i \"{1}\"", Info.Fps, Path.Combine(tmp, "%06d.jpg"));
                string args = gif
                    ? input + " -vf \"split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle\" -loop 0 \"" + outPath + "\""
                    : input + " -vf \"pad=ceil(iw/2)*2:ceil(ih/2)*2\" -c:v libx264 -pix_fmt yuv420p -crf 18 -preset veryfast -movflags +faststart \"" + outPath + "\"";
                bool ok = FFmpeg.Run(owner, exe, args, sel.Count, (gif ? "GIF" : "MP4") + " を作っています…(" + sel.Count + " コマ)");
                if (!ok) return;
                if (FileCreated != null) FileCreated(outPath);
                if (Message != null) Message("🎬 書き出しました: " + outPath);
            }
            catch (Exception ex) { MessageBox.Show(owner, "書き出せませんでした: " + ex.Message, "SimpleCap"); }
            finally
            {
                Cursor.Current = Cursors.Default;
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        // ---------- 映像の表示 ----------
        class Pic : Control
        {
            VideoView v;
            public Pic(VideoView v)
            {
                this.v = v;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                BackColor = Color.FromArgb(64, 64, 68);
            }
            protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); if (e.Button == MouseButtons.Left) v.TogglePlay(); }
            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(BackColor);
                Bitmap b = v.curBmp;
                if (v.Info == null) return;
                if (b == null)
                {
                    TextRenderer.DrawText(g, "コマがありません", Font, ClientRectangle, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    return;
                }
                // この部品の大きさ = 元の大きさ × 倍率(ZoomBox が決める)
                float z = Width / (float)b.Width;
                if (z <= 0) return;
                Rectangle r = ClientRectangle;
                g.InterpolationMode = z >= 2 ? InterpolationMode.NearestNeighbor : z >= 1 ? InterpolationMode.Bilinear : InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(b, r);
                // 注釈(このコマで表示するもの)
                if (v.Annos.Count > 0)
                {
                    System.Drawing.Drawing2D.GraphicsState gs = g.Save();
                    g.TranslateTransform(r.X, r.Y);
                    g.ScaleTransform(z, z);
                    VideoInfo.DrawAnnotations(g, b, v.Annos, v.cur);
                    g.Restore(gs);
                }
                // 区間の外なら暗く表示
                if (v.Info.Segments.Count > 0 && !v.InSegment(v.cur))
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(110, 0, 0, 0))) g.FillRectangle(sb, r);
            }
        }

        // ---------- タイムライン ----------
        class Timeline : Control
        {
            VideoView v;
            bool drag;
            public Timeline(VideoView v)
            {
                this.v = v;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                Cursor = Cursors.Hand;
            }
            int N { get { return Math.Max(1, v.frames.Count); } }
            float X(int f) { return Program.D(6) + (Width - Program.D(12)) * (N <= 1 ? 0 : f / (float)(N - 1)); }
            int F(int x) { return N <= 1 ? 0 : (int)Math.Round((x - Program.D(6)) / (float)(Width - Program.D(12)) * (N - 1)); }

            protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { drag = true; v.Seek(F(e.X)); } }
            protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (drag) v.Seek(F(e.X)); }
            protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); drag = false; }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(Color.FromArgb(245, 246, 248));
                if (v.Info == null) return;
                int top = Program.D(16), h = Height - top - Program.D(4);
                RectangleF track = new RectangleF(X(0), top, X(N - 1) - X(0) + 1, h);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(210, 214, 220))) g.FillRectangle(b, track);
                // 区間
                using (SolidBrush b = new SolidBrush(Color.FromArgb(150, 46, 170, 90)))
                    foreach (int[] s in v.Info.Segments)
                        g.FillRectangle(b, X(s[0]), top, Math.Max(2, X(s[1]) - X(s[0])), h);
                // 1秒ごとの目盛り
                using (Pen p = new Pen(Color.FromArgb(150, 150, 150)))
                {
                    int step = v.Info.Fps;
                    float pxPerSec = (Width - Program.D(12)) / (float)Math.Max(1, N - 1) * step;
                    if (pxPerSec < 6) step *= 10;
                    for (int f = 0; f < N; f += step) g.DrawLine(p, X(f), top, X(f), top + Program.D(4));
                }
                // 保留中の IN
                if (v.pendingIn >= 0)
                    using (Pen p = new Pen(Color.FromArgb(230, 130, 0), 2))
                    {
                        g.DrawLine(p, X(v.pendingIn), top - Program.D(4), X(v.pendingIn), top + h);
                        TextRenderer.DrawText(g, "IN", Font, new Point((int)X(v.pendingIn) + 2, 0), Color.FromArgb(230, 130, 0));
                    }
                // 選んでいる注釈の表示範囲(注釈の編集中)
                Obj sa = v.Annotating && v.SelectedAnno != null ? v.SelectedAnno() : null;
                if (sa != null)
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(200, 40, 110, 220)))
                    {
                        int eF = sa.TEnd < 0 ? N - 1 : Math.Min(N - 1, sa.TEnd);
                        g.FillRectangle(b, X(Math.Max(0, sa.TStart)), top + h - Program.D(6), Math.Max(3, X(eF) - X(Math.Max(0, sa.TStart))), Program.D(6));
                    }
                // 再生位置
                float x = X(v.cur);
                using (Pen p = new Pen(Color.FromArgb(220, 40, 40), 2)) g.DrawLine(p, x, 0, x, Height);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(220, 40, 40)))
                    g.FillPolygon(b, new PointF[] { new PointF(x - 5, 0), new PointF(x + 5, 0), new PointF(x, 7) });
            }
        }
    }
}
