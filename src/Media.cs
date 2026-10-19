using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SimpleCap
{
    // ================= 書き出した GIF / MP4 =================
    public static class MediaFile
    {
        public static bool IsGif(string p) { return p != null && p.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) && File.Exists(p); }
        public static bool IsMp4(string p) { return p != null && p.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) && File.Exists(p); }
        public static bool IsMedia(string p) { return IsGif(p) || IsMp4(p); }
    }

    // Windows のサムネイル(MP4 の 1 コマ目など)を取得
    public static class ShellThumb
    {
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

        public static Bitmap Get(string path, int size)
        {
            try
            {
                IShellItemImageFactory f;
                SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out f);
                SIZE s = new SIZE(); s.cx = size; s.cy = size;
                IntPtr hb;
                if (f.GetImage(s, 0x08, out hb) != 0 || hb == IntPtr.Zero)   // THUMBNAILONLY
                    if (f.GetImage(s, 0x00, out hb) != 0 || hb == IntPtr.Zero) return null;
                try
                {
                    using (Bitmap b = Image.FromHbitmap(hb))
                        return new Bitmap(b);
                }
                finally { DeleteObject(hb); Marshal.ReleaseComObject(f); }
            }
            catch { return null; }
        }
    }

    // ================= 拡大・縮小して表示する入れ物(ビデオ / GIF / MP4 用) =================
    //  中身(Content)を「元の大きさ × 倍率」で置き、はみ出すときはスクロール。Ctrl+ホイールで拡大・縮小
    public class ZoomBox : Panel
    {
        public readonly Control Content;
        Size natural;
        float zoom = 1f;
        bool fit = true, fitEnlarge;
        public event EventHandler ZoomChanged;

        public ZoomBox(Control content)
        {
            AutoScroll = true;
            BackColor = Color.FromArgb(64, 64, 68);
            DoubleBuffered = true;
            Content = content;
            Controls.Add(content);
            content.MouseWheel += delegate (object s, MouseEventArgs e) { if (Wheel(e)) { HandledMouseEventArgs he = e as HandledMouseEventArgs; if (he != null) he.Handled = true; } };
        }

        public float Zoom { get { return zoom; } }
        public Size Natural
        {
            get { return natural; }
            set { natural = value; Relayout(); }
        }

        // 開いたとき:大きいものだけ縮小(小さいものは 100%)
        public void Reset() { fit = true; fitEnlarge = false; Relayout(); }
        public void SetZoom(float z) { fit = false; zoom = Math.Max(0.05f, Math.Min(16f, z)); Relayout(); }
        public void FitAll(bool enlarge) { fit = true; fitEnlarge = enlarge; Relayout(); }

        bool Wheel(MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) == 0) return false;
            SetZoom(e.Delta > 0 ? zoom * 1.25f : zoom / 1.25f);
            return true;
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (Wheel(e)) { HandledMouseEventArgs he = e as HandledMouseEventArgs; if (he != null) he.Handled = true; return; }
            base.OnMouseWheel(e);
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }
        protected override Point ScrollToControl(Control activeControl) { return DisplayRectangle.Location; }

        public void Relayout()
        {
            if (natural.Width <= 0 || natural.Height <= 0)
            {
                Content.Bounds = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
                return;
            }
            float old = zoom;
            if (fit)
            {
                float z = Math.Min((ClientSize.Width - 20f) / natural.Width, (ClientSize.Height - 20f) / natural.Height);
                if (!fitEnlarge) z = Math.Min(1f, z);
                zoom = Math.Max(0.05f, z);
            }
            int w = Math.Max(1, (int)Math.Round(natural.Width * zoom)), h = Math.Max(1, (int)Math.Round(natural.Height * zoom));
            int x = w < ClientSize.Width ? (ClientSize.Width - w) / 2 : AutoScrollPosition.X;
            int y = h < ClientSize.Height ? (ClientSize.Height - h) / 2 : AutoScrollPosition.Y;
            Content.Bounds = new Rectangle(x, y, w, h);
            Content.Invalidate();
            if (Math.Abs(old - zoom) > 0.0001f && ZoomChanged != null) ZoomChanged(this, EventArgs.Empty);
            else if (ZoomChanged != null && !fit) ZoomChanged(this, EventArgs.Empty);
        }
    }

    // Windows Media Player を埋め込む(MP4 の再生用。参照 DLL なしで遅延バインド)
    class WmpHost : AxHost
    {
        public WmpHost() : base("6bf52a52-394a-11d3-b153-00c04f79faa6") { }
        public object Ocx { get { return GetOcx(); } }
        static object Get(object o, string name) { return o.GetType().InvokeMember(name, System.Reflection.BindingFlags.GetProperty, null, o, null); }
        static void Set(object o, string name, object v) { o.GetType().InvokeMember(name, System.Reflection.BindingFlags.SetProperty, null, o, new object[] { v }); }
        static void Call(object o, string name) { o.GetType().InvokeMember(name, System.Reflection.BindingFlags.InvokeMethod, null, o, null); }

        public void Init()
        {
            Set(Ocx, "uiMode", "none");
            Set(Ocx, "stretchToFit", true);
            Set(Ocx, "enableContextMenu", false);
            Set(Get(Ocx, "settings"), "autoStart", false);
        }
        public string Url { set { Set(Ocx, "URL", value); } }
        public void Play() { Call(Get(Ocx, "controls"), "play"); }
        public void Pause() { Call(Get(Ocx, "controls"), "pause"); }
        public void StopPlay() { Call(Get(Ocx, "controls"), "stop"); }
        public int PlayState { get { try { return Convert.ToInt32(Get(Ocx, "playState")); } catch { return 0; } } }   // 3=再生中 2=一時停止 1=停止 8=終了
        public double Position
        {
            get { try { return Convert.ToDouble(Get(Get(Ocx, "controls"), "currentPosition")); } catch { return 0; } }
        }
        public Size VideoSize
        {
            get
            {
                try
                {
                    object m = Get(Ocx, "currentMedia");
                    if (m == null) return Size.Empty;
                    return new Size(Convert.ToInt32(Get(m, "imageSourceWidth")), Convert.ToInt32(Get(m, "imageSourceHeight")));
                }
                catch { return Size.Empty; }
            }
        }
        public double Duration
        {
            get { try { object m = Get(Ocx, "currentMedia"); return m == null ? 0 : Convert.ToDouble(Get(m, "duration")); } catch { return 0; } }
        }
    }

    // ブラウズ画面で GIF(アニメーション再生)/ MP4(再生・停止ボタン付き)を表示
    public class MediaView : Control
    {
        PictureBox pb;
        Panel stage;          // pb と Media Player を重ねる板(拡大・縮小はこの板の大きさで)
        public ZoomBox Zoomer;
        MemoryStream ms;
        public string PathOpen;
        string info = "";
        // MP4 用
        Panel bar;
        Button btnPlay, btnStop;
        Label lblTime;
        WmpHost wmp;
        bool wmpFailed, wmpLoaded;
        System.Windows.Forms.Timer timer;

        public MediaView()
        {
            BackColor = Color.FromArgb(64, 64, 68);
            pb = new PictureBox();
            pb.BackColor = BackColor;
            pb.Dock = DockStyle.Fill;
            pb.SizeMode = PictureBoxSizeMode.StretchImage;   // 板の大きさ(=倍率)に合わせて表示。GIF もアニメーションする
            stage = new Panel();
            stage.BackColor = BackColor;
            stage.Controls.Add(pb);
            Zoomer = new ZoomBox(stage);
            pb.MouseWheel += delegate (object s, MouseEventArgs e) { if ((ModifierKeys & Keys.Control) != 0) { Zoomer.SetZoom(e.Delta > 0 ? Zoomer.Zoom * 1.25f : Zoomer.Zoom / 1.25f); HandledMouseEventArgs he = e as HandledMouseEventArgs; if (he != null) he.Handled = true; } };
            pb.DoubleClick += delegate { if (MediaFile.IsMp4(PathOpen)) TogglePlay(); };
            Controls.Add(Zoomer);
            ContextMenuStrip m = new ContextMenuStrip();
            m.Items.Add("▶ 既定のアプリで開く", null, delegate { try { Process.Start(PathOpen); } catch { } });
            m.Items.Add("📂 エクスプローラーで表示", null, delegate { try { Process.Start("explorer.exe", "/select,\"" + PathOpen + "\""); } catch { } });
            pb.ContextMenuStrip = m;

            bar = new Panel();
            bar.BackColor = Color.FromArgb(245, 246, 248);
            bar.Visible = false;
            btnPlay = new Button(); btnPlay.Text = "▶ 再生";
            btnPlay.Click += delegate { TogglePlay(); };
            btnStop = new Button(); btnStop.Text = "■ 停止";
            btnStop.Click += delegate { StopMp4(); };
            lblTime = new Label(); lblTime.TextAlign = ContentAlignment.MiddleLeft; lblTime.Font = new Font("Consolas", 9.5f);
            bar.Controls.Add(btnPlay); bar.Controls.Add(btnStop); bar.Controls.Add(lblTime);
            Controls.Add(bar);

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 200;
            timer.Tick += delegate { UpdatePlayState(); };
            DoLayout();
        }

        public bool IsOpen { get { return PathOpen != null; } }

        public void Open(string path)
        {
            CloseMedia();
            PathOpen = path;
            Image img;
            bool mp4 = MediaFile.IsMp4(path);
            if (!mp4)
            {
                ms = new MemoryStream(File.ReadAllBytes(path));   // 再生中はストリームを開いたままにする(ファイルはロックしない)
                img = Image.FromStream(ms);
                int n = 1;
                try { n = img.GetFrameCount(System.Drawing.Imaging.FrameDimension.Time); } catch { }
                info = string.Format("🎞️ GIF アニメーション {0} × {1}  {2} コマ", img.Width, img.Height, n);
            }
            else
            {
                img = ShellThumb.Get(path, Program.D(1280));
                info = "🎬 MP4 ムービー   Space / ▶ で再生・一時停止、■ で停止";
                if (img == null)
                {
                    Bitmap b = new Bitmap(Program.D(320), Program.D(180));
                    using (Graphics g = Graphics.FromImage(b))
                    {
                        g.Clear(Color.FromArgb(40, 40, 44));
                        TextRenderer.DrawText(g, "MP4", new Font("Yu Gothic UI", 24f, FontStyle.Bold), new Rectangle(0, 0, b.Width, b.Height), Color.White,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    }
                    img = b;
                }
            }
            info += "   " + Path.GetFileName(path);
            pb.Image = img;
            pb.Visible = true;
            Zoomer.Natural = img.Size;
            Zoomer.Reset();
            bar.Visible = mp4;
            btnPlay.Text = "▶ 再生";
            lblTime.Text = "";
            DoLayout();
        }

        public void CloseMedia()
        {
            timer.Stop();
            if (wmp != null && wmpLoaded)
            {
                try { wmp.StopPlay(); wmp.Url = ""; } catch { }
                wmp.Visible = false;
                wmpLoaded = false;
            }
            Image old = pb.Image;
            pb.Image = null;
            if (old != null) old.Dispose();
            if (ms != null) { ms.Dispose(); ms = null; }
            PathOpen = null;
            bar.Visible = false;
        }

        public string StatusText { get { return info; } }

        // ---------- MP4 の再生 ----------
        bool EnsureWmp()
        {
            if (wmp != null) return true;
            if (wmpFailed) return false;
            try
            {
                wmp = new WmpHost();
                wmp.Dock = DockStyle.Fill;
                stage.Controls.Add(wmp);
                wmp.CreateControl();
                wmp.Init();
                wmp.Visible = false;
                DoLayout();
                return true;
            }
            catch
            {
                wmpFailed = true;
                if (wmp != null) { try { stage.Controls.Remove(wmp); wmp.Dispose(); } catch { } wmp = null; }
                return false;
            }
        }

        public bool HandleKey(Keys k)
        {
            if (!MediaFile.IsMp4(PathOpen)) return false;
            if (k == Keys.Space) { TogglePlay(); return true; }
            if (k == Keys.Escape && wmpLoaded && wmp.PlayState == 3) { StopMp4(); return true; }
            return false;
        }

        void TogglePlay()
        {
            if (!MediaFile.IsMp4(PathOpen)) return;
            if (!EnsureWmp())
            {
                // Media Player が使えない環境では既定のアプリで再生
                try { Process.Start(PathOpen); } catch { }
                return;
            }
            try
            {
                if (!wmpLoaded) { wmp.Url = PathOpen; wmpLoaded = true; }
                if (wmp.PlayState == 3) { wmp.Pause(); btnPlay.Text = "▶ 再生"; }
                else
                {
                    wmp.Visible = true; wmp.BringToFront();
                    pb.Visible = false;
                    wmp.Play();
                    btnPlay.Text = "⏸ 一時停止";
                    timer.Start();
                }
            }
            catch (Exception ex) { MessageBox.Show(FindForm(), "再生できませんでした: " + ex.Message, "SimpleCap"); }
        }

        void StopMp4()
        {
            if (wmp != null && wmpLoaded) try { wmp.StopPlay(); } catch { }
            timer.Stop();
            ShowPoster();
        }

        void ShowPoster()
        {
            if (wmp != null) wmp.Visible = false;
            pb.Visible = true;
            btnPlay.Text = "▶ 再生";
            lblTime.Text = "";
        }

        void UpdatePlayState()
        {
            if (wmp == null || !wmpLoaded) { timer.Stop(); return; }
            int st = wmp.PlayState;
            if (st == 8 || st == 1) { timer.Stop(); ShowPoster(); return; }   // 最後まで再生 / 停止 → 最初のコマに戻す
            // 動画の本当の大きさが分かったら、倍率の基準にする
            Size vs = wmp.VideoSize;
            if (vs.Width > 0 && vs.Height > 0 && vs != Zoomer.Natural) Zoomer.Natural = vs;
            double p = wmp.Position, d = wmp.Duration;
            lblTime.Text = string.Format("{0:00}:{1:00.0} / {2:00}:{3:00.0}", (int)(p / 60), p % 60, (int)(d / 60), d % 60);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); DoLayout(); }

        void DoLayout()
        {
            if (bar == null) return;
            int W = ClientSize.Width, H = ClientSize.Height;
            int bh = bar.Visible ? Program.D(44) : 0;
            Rectangle view = new Rectangle(0, 0, W, Math.Max(0, H - bh));
            Zoomer.Bounds = view;
            bar.SetBounds(0, Math.Max(0, H - bh), W, bh);
            int m = Program.D(8), h = Program.D(30), y = Program.D(7);
            btnPlay.SetBounds(m, y, Program.D(110), h);
            btnStop.SetBounds(m + Program.D(114), y, Program.D(80), h);
            lblTime.SetBounds(m + Program.D(204), y, Math.Max(10, W - m * 2 - Program.D(204)), h);
        }

    }
}
