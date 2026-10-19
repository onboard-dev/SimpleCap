using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace SimpleCap
{
    // ================= ビデオ(録画データ) =================
    //  <保存先>\yyyyMMdd_HHmmss_fff.scvid\  フォルダ
    //      frames\000000.jpg …   15fps の連番 JPEG
    //      video.ini              fps / 大きさ / IN-OUT 区間 / 最後に作った GIF
    public class VideoInfo
    {
        public const string Ext = ".scvid";
        public string Dir;
        public int Fps = 15, Width, Height;
        public List<int[]> Segments = new List<int[]>();   // {IN, OUT}(フレーム番号、両端を含む)
        public string GifPath = "";

        public string FramesDir { get { return Path.Combine(Dir, "frames"); } }
        // 注釈:anno.png / anno.scap(キャプチャと同じ形式)+ anno_time.txt(各注釈の表示範囲)
        public string AnnoPng { get { return Path.Combine(Dir, "anno.png"); } }
        public string AnnoTimes { get { return Path.Combine(Dir, "anno_time.txt"); } }

        public static bool ShowAt(Obj o, int frame) { return frame >= o.TStart && (o.TEnd < 0 || frame <= o.TEnd); }

        public List<Obj> LoadAnnotations()
        {
            List<Obj> l = new List<Obj>();
            if (!File.Exists(Path.ChangeExtension(AnnoPng, ".scap"))) return l;
            try
            {
                Doc d = Doc.Load(AnnoPng);
                l = d.Objs;
                if (d.Base != null) d.Base.Dispose();
                if (File.Exists(AnnoTimes))
                {
                    string[] lines = File.ReadAllLines(AnnoTimes);
                    for (int i = 0; i < lines.Length && i < l.Count; i++)
                    {
                        string[] p = lines[i].Split(',');
                        int a, b;
                        if (p.Length == 2 && int.TryParse(p[0], out a) && int.TryParse(p[1], out b)) { l[i].TStart = a; l[i].TEnd = b; }
                    }
                }
            }
            catch { }
            return l;
        }

        public void SaveAnnotationTimes(List<Obj> objs)
        {
            List<string> lines = new List<string>();
            foreach (Obj o in objs) lines.Add(o.TStart + "," + o.TEnd);
            File.WriteAllLines(AnnoTimes, lines.ToArray());
        }

        public void DeleteAnnotations()
        {
            foreach (string f in new string[] { AnnoPng, Path.ChangeExtension(AnnoPng, ".scap"), AnnoTimes })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
        }

        // 注釈を描き込む(コマ番号 f で表示するものだけ)
        public static void DrawAnnotations(Graphics g, Bitmap frame, List<Obj> objs, int f)
        {
            if (objs == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            foreach (Obj o in objs)
            {
                if (!o.Visible || !ShowAt(o, f)) continue;
                bool th = o.TimeHidden; o.TimeHidden = false;
                o.Draw(g, frame);
                o.TimeHidden = th;
            }
        }
        public string IniPath { get { return Path.Combine(Dir, "video.ini"); } }

        public static bool IsVideo(string path)
        {
            return path != null && path.EndsWith(Ext, StringComparison.OrdinalIgnoreCase) && Directory.Exists(path);
        }

        public static VideoInfo Load(string dir)
        {
            VideoInfo v = new VideoInfo();
            v.Dir = dir;
            try
            {
                if (File.Exists(v.IniPath))
                    foreach (string line in File.ReadAllLines(v.IniPath, Encoding.UTF8))
                    {
                        int i = line.IndexOf('=');
                        if (i <= 0) continue;
                        string k = line.Substring(0, i).Trim(), val = line.Substring(i + 1).Trim();
                        int n;
                        if (k == "fps" && int.TryParse(val, out n) && n > 0) v.Fps = n;
                        else if (k == "width" && int.TryParse(val, out n)) v.Width = n;
                        else if (k == "height" && int.TryParse(val, out n)) v.Height = n;
                        else if (k == "gif") v.GifPath = val;
                        else if (k == "segments")
                            foreach (string s in val.Split(','))
                            {
                                string[] p = s.Split('-');
                                int a, b;
                                if (p.Length == 2 && int.TryParse(p[0], out a) && int.TryParse(p[1], out b)) v.Segments.Add(new int[] { a, b });
                            }
                    }
            }
            catch { }
            return v;
        }

        public void Save()
        {
            List<string> seg = new List<string>();
            foreach (int[] s in Segments) seg.Add(s[0] + "-" + s[1]);
            File.WriteAllLines(IniPath, new string[] {
                "fps=" + Fps, "width=" + Width, "height=" + Height,
                "segments=" + string.Join(",", seg.ToArray()), "gif=" + GifPath }, Encoding.UTF8);
        }

        public List<string> Frames()
        {
            List<string> l = new List<string>();
            if (Directory.Exists(FramesDir)) l.AddRange(Directory.GetFiles(FramesDir, "*.jpg"));
            l.Sort(StringComparer.OrdinalIgnoreCase);
            return l;
        }

        public string FirstFrame()
        {
            string f = Path.Combine(FramesDir, "000000.jpg");
            if (File.Exists(f)) return f;
            List<string> l = Frames();
            return l.Count > 0 ? l[0] : null;
        }

        // 区間を並べ替えて重なりをまとめる
        public void Normalize()
        {
            Segments.Sort(delegate (int[] a, int[] b) { return a[0].CompareTo(b[0]); });
            List<int[]> r = new List<int[]>();
            foreach (int[] s in Segments)
            {
                if (r.Count > 0 && s[0] <= r[r.Count - 1][1] + 1) r[r.Count - 1][1] = Math.Max(r[r.Count - 1][1], s[1]);
                else r.Add(new int[] { s[0], s[1] });
            }
            Segments = r;
        }
    }

    // ================= ffmpeg(必要になったときに自動ダウンロード) =================
    public static class FFmpeg
    {
        static readonly string[] Urls = {
            "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
            "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip" };

        public static string Dir
        {
            get { return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SimpleCap"), "ffmpeg"); }
        }

        public static string Find()
        {
            string p = Path.Combine(Dir, "ffmpeg.exe");
            if (File.Exists(p)) return p;
            p = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "ffmpeg.exe");
            if (File.Exists(p)) return p;
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string d in path.Split(';'))
            {
                try
                {
                    if (d.Trim() == "") continue;
                    p = Path.Combine(d.Trim().Trim('"'), "ffmpeg.exe");
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return null;
        }

        // ffmpeg.exe の場所を返す。無ければ確認してダウンロード。使えなければ null
        public static string Ensure(IWin32Window owner)
        {
            string p = Find();
            if (p != null) return p;
            if (MessageBox.Show(owner,
                "動画(MP4 / GIF)を作るには ffmpeg が必要です。\nインターネットからダウンロードしますか?(約 100MB、初回のみ)\n\n" +
                "配布元: gyan.dev(つながらないときは GitHub の BtbN 版)\n保存先: " + Dir,
                "SimpleCap", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return null;
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
            Directory.CreateDirectory(Dir);
            string zip = Path.Combine(Dir, "download.zip");
            string lastErr = "";
            foreach (string url in Urls)
            {
                bool ok = false;
                using (ProgressForm pf = new ProgressForm("ffmpeg をダウンロードしています…"))
                using (WebClient wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "SimpleCap");
                    wc.DownloadProgressChanged += delegate (object s, DownloadProgressChangedEventArgs e)
                    {
                        pf.SetProgress(e.ProgressPercentage, string.Format("{0:0.0} / {1:0.0} MB", e.BytesReceived / 1048576.0,
                            e.TotalBytesToReceive > 0 ? e.TotalBytesToReceive / 1048576.0 : 0));
                    };
                    wc.DownloadFileCompleted += delegate (object s, System.ComponentModel.AsyncCompletedEventArgs e)
                    {
                        if (e.Cancelled) lastErr = "キャンセルしました";
                        else if (e.Error != null) lastErr = e.Error.Message;
                        else ok = true;
                        pf.Done();
                    };
                    pf.Cancel += delegate { try { wc.CancelAsync(); } catch { } };
                    wc.DownloadFileAsync(new Uri(url), zip);
                    pf.ShowDialog(owner);
                }
                if (!ok)
                {
                    try { File.Delete(zip); } catch { }
                    if (lastErr == "キャンセルしました") return null;
                    continue;
                }
                try
                {
                    Cursor.Current = Cursors.WaitCursor;
                    using (ZipArchive za = ZipFile(zip))
                        foreach (ZipArchiveEntry en in za.Entries)
                            if (en.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                            {
                                string dst = Path.Combine(Dir, "ffmpeg.exe");
                                using (Stream s = en.Open())
                                using (FileStream fs = File.Create(dst)) s.CopyTo(fs);
                                break;
                            }
                }
                catch (Exception ex) { lastErr = ex.Message; }
                finally { Cursor.Current = Cursors.Default; try { File.Delete(zip); } catch { } }
                p = Path.Combine(Dir, "ffmpeg.exe");
                if (File.Exists(p)) return p;
            }
            MessageBox.Show(owner, "ffmpeg をダウンロードできませんでした: " + lastErr +
                "\n\n手動で入れる場合は ffmpeg.exe を次のフォルダに置いてください:\n" + Dir, "SimpleCap");
            return null;
        }

        static ZipArchive ZipFile(string path)
        {
            return new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read, false);
        }

        // ffmpeg を実行(進捗ダイアログ付き)。totalFrames は進捗表示用。成功で true
        public static bool Run(IWin32Window owner, string exe, string args, int totalFrames, string title)
        {
            bool ok = false;
            string err = "";
            using (ProgressForm pf = new ProgressForm(title))
            {
                Process proc = null;
                pf.Cancel += delegate { try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { } };
                Thread th = new Thread(delegate ()
                {
                    try
                    {
                        ProcessStartInfo si = new ProcessStartInfo(exe, args);
                        si.UseShellExecute = false; si.CreateNoWindow = true;
                        si.RedirectStandardError = true; si.RedirectStandardOutput = true;
                        proc = Process.Start(si);
                        proc.OutputDataReceived += delegate { };
                        proc.BeginOutputReadLine();
                        StringBuilder tail = new StringBuilder();
                        Regex rx = new Regex(@"frame=\s*(\d+)");
                        string line;
                        while ((line = proc.StandardError.ReadLine()) != null)
                        {
                            Match m = rx.Match(line);
                            if (m.Success && totalFrames > 0)
                            {
                                int f = int.Parse(m.Groups[1].Value);
                                pf.SetProgress(Math.Min(100, f * 100 / totalFrames), f + " / " + totalFrames + " コマ");
                            }
                            tail.AppendLine(line);
                            if (tail.Length > 4000) tail.Remove(0, tail.Length - 4000);
                        }
                        proc.WaitForExit();
                        ok = proc.ExitCode == 0;
                        if (!ok) err = tail.ToString();
                    }
                    catch (Exception ex) { err = ex.Message; }
                    pf.Done();
                });
                th.IsBackground = true;
                pf.Shown += delegate { th.Start(); };
                pf.ShowDialog(owner);
                th.Join(3000);
            }
            if (!ok && err != "")
            {
                string[] lines = err.Trim().Split('\n');
                string last = string.Join("\n", lines, Math.Max(0, lines.Length - 6), Math.Min(6, lines.Length));
                MessageBox.Show(owner, "動画を作れませんでした。\n\n" + last, "SimpleCap");
            }
            return ok;
        }
    }

    // 進捗ダイアログ
    public class ProgressForm : Form
    {
        ProgressBar bar;
        Label lbl;
        bool done;
        public event EventHandler Cancel;

        public ProgressForm(string title)
        {
            Text = "SimpleCap";
            AutoScaleMode = AutoScaleMode.None;
            Font = Program.UiFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Program.D(380), Program.D(118));
            Label t = new Label(); t.Text = title; t.SetBounds(Program.D(14), Program.D(12), Program.D(352), Program.D(22));
            Controls.Add(t);
            bar = new ProgressBar(); bar.SetBounds(Program.D(14), Program.D(38), Program.D(352), Program.D(20));
            Controls.Add(bar);
            lbl = new Label(); lbl.ForeColor = Color.FromArgb(90, 90, 90);
            lbl.SetBounds(Program.D(14), Program.D(64), Program.D(250), Program.D(20));
            Controls.Add(lbl);
            Button c = new Button(); c.Text = "キャンセル";
            c.SetBounds(Program.D(276), Program.D(80), Program.D(90), Program.D(28));
            c.Click += delegate { if (Cancel != null) Cancel(this, EventArgs.Empty); };
            Controls.Add(c);
        }

        public void SetProgress(int pct, string text)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke((MethodInvoker)delegate { SetProgress(pct, text); }); } catch { } return; }
            bar.Value = Math.Max(0, Math.Min(100, pct));
            lbl.Text = text;
        }

        public void Done()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke((MethodInvoker)Done); } catch { } return; }
            done = true;
            if (IsHandleCreated) Close();
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); if (done) Close(); }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!done && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                if (Cancel != null) Cancel(this, EventArgs.Empty);
                return;
            }
            base.OnFormClosing(e);
        }
    }

    // ================= クリックのエフェクト(マウスを写し込むときだけ) =================
    //  左クリック / 右クリック / ダブルクリックを、色の違う広がる輪で表示
    public class ClickFx
    {
        public bool Enabled = true, Label = true;
        public int Size = 1;   // 0=小 1=中 2=大
        public Color Left = Color.FromArgb(255, 180, 0), Right = Color.FromArgb(40, 140, 255), Double = Color.FromArgb(240, 50, 50);
        // 輪の広がるスピード(1=遅い … 5=速い)。広がりきるまでの時間。広がりきったら FadeMs で薄くなって消える
        public int Speed = 3;
        static readonly int[] SpeedMs = { 3000, 2000, 1500, 1000, 600 };
        public int ExpandMs { get { return SpeedMs[Math.Max(1, Math.Min(5, Speed)) - 1]; } }
        public const int FadeMs = 400;
        public int RingMs { get { return ExpandMs + FadeMs; } }
        // 静止(クリックから HoldAtMs 後の画面を止めて、指定秒数ぶんビデオに足す。止めている間も輪は同じ速さで広がり続ける)
        public bool Hold = false;
        public int HoldSec = 2;          // 1〜5 秒
        public const int HoldAtMs = 360; // 静止を始める時刻(拡大はこの時刻までに終える)
        int HoldMs { get { return HoldSec * 1000; } }
        // クリック位置を拡大(クリックの瞬間から拡大し、HoldAtMs までに拡大しきる → 輪が消えたら元に戻す)
        public bool Zoom = false;
        public int ZoomPct = 20;         // 10〜50 %
        public const int ZoomOutMs = 360;

        public static ClickFx Load()
        {
            Dictionary<string, string> st = Program.LoadSettings();
            ClickFx c = new ClickFx();
            c.Enabled = Program.GetInt(st, "clickfx", 1) == 1;
            c.Label = Program.GetInt(st, "clickfx.label", 1) == 1;
            c.Size = Math.Max(0, Math.Min(2, Program.GetInt(st, "clickfx.size", 1)));
            string v; int n;
            c.Hold = Program.GetInt(st, "clickfx.hold", 0) == 1;
            c.HoldSec = Math.Max(1, Math.Min(5, Program.GetInt(st, "clickfx.holdsec", 2)));
            c.Speed = Math.Max(1, Math.Min(5, Program.GetInt(st, "clickfx.speed", 3)));
            c.Zoom = Program.GetInt(st, "clickfx.zoom", 0) == 1;
            c.ZoomPct = Math.Max(10, Math.Min(50, Program.GetInt(st, "clickfx.zoompct", 20) / 10 * 10));
            if (st.TryGetValue("clickfx.left", out v) && int.TryParse(v, out n)) c.Left = Color.FromArgb(n);
            if (st.TryGetValue("clickfx.right", out v) && int.TryParse(v, out n)) c.Right = Color.FromArgb(n);
            if (st.TryGetValue("clickfx.double", out v) && int.TryParse(v, out n)) c.Double = Color.FromArgb(n);
            return c;
        }
        public void Save(Dictionary<string, string> st)
        {
            st["clickfx"] = Enabled ? "1" : "0";
            st["clickfx.label"] = Label ? "1" : "0";
            st["clickfx.size"] = Size.ToString();
            st["clickfx.left"] = Left.ToArgb().ToString();
            st["clickfx.right"] = Right.ToArgb().ToString();
            st["clickfx.double"] = Double.ToArgb().ToString();
            st["clickfx.hold"] = Hold ? "1" : "0";
            st["clickfx.holdsec"] = HoldSec.ToString();
            st["clickfx.speed"] = Speed.ToString();
            st["clickfx.zoom"] = Zoom ? "1" : "0";
            st["clickfx.zoompct"] = ZoomPct.ToString();
        }

        public enum Kind { Left, Right, Double }
        class Ev { public long T, UpT = -1; public Point P; public Kind K; public bool Hidden; public bool IsLeft; public long HoldT = -1; }
        readonly List<Ev> evs = new List<Ev>();

        // 描き込みを遅らせる時間:ダブルクリックかどうかが決まるまで待ってから描く
        // (1 回目の左クリックを「左」と描いてしまわないように)
        public int DelayMs { get { return SystemInformation.DoubleClickTime + 60; } }

        // フックから呼ぶ(t = 録画開始からの ms、p = 画面座標)
        public void AddDown(bool left, long t, Point p)
        {
            lock (evs)
            {
                Ev e = new Ev(); e.T = t; e.P = p; e.IsLeft = left; e.K = left ? Kind.Left : Kind.Right;
                if (left)
                {
                    // 直前の左クリック(まだダブルクリックの 2 回目でないもの)と近ければダブルクリック
                    Ev prev = null;
                    for (int i = evs.Count - 1; i >= 0; i--) if (evs[i].IsLeft) { prev = evs[i]; break; }
                    Size ds = SystemInformation.DoubleClickSize;
                    if (prev != null && prev.K == Kind.Left && !prev.Hidden && t - prev.T <= SystemInformation.DoubleClickTime
                        && Math.Abs(p.X - prev.P.X) <= ds.Width && Math.Abs(p.Y - prev.P.Y) <= ds.Height)
                    {
                        prev.Hidden = true;    // 1 回目は輪を出さない
                        e.K = Kind.Double;     // 2 回目をダブルクリックとして描く
                    }
                }
                evs.Add(e);
                if (evs.Count > 80) evs.RemoveAt(0);
            }
        }

        public void AddUp(bool left, long t)
        {
            lock (evs)
                for (int i = evs.Count - 1; i >= 0; i--)
                    if (evs[i].IsLeft == left && evs[i].UpT < 0) { evs[i].UpT = t; break; }
        }

        // クリックからの経過時間(輪・拡大のアニメーション用)。静止を過ぎたクリックは静止の長さを足す
        // (画面は止まっていても、輪は静止中も進んでいるため)。extra = 静止中のコマを描くときの、静止の始まりからの時間
        long Age(Ev e, long t, long extra)
        {
            long a = t - e.T;
            if (e.HoldT >= 0 && t > e.HoldT) a += HoldMs;
            return a + extra;
        }

        // 静止させるクリックを 1 つ取り出す:時刻 t までにクリックから HoldAtMs を過ぎた、まだ静止していないクリック。
        // 戻り値 = 静止する時刻(なければ -1)。ダブルクリックの 1 回目(Hidden)は対象外。
        public long TakeHold(long t)
        {
            lock (evs)
                foreach (Ev e in evs)
                    if (!e.Hidden && e.HoldT < 0 && e.T + HoldAtMs <= t) { e.HoldT = e.T + HoldAtMs; return e.HoldT; }
            return -1;
        }

        // 時刻 t の拡大率(1 = 等倍)と中心(画面座標)。重なったときは拡大率の大きい方(同じなら新しい方)
        public float ZoomAt(long t, out Point center, long extra = 0)
        {
            center = Point.Empty;
            float best = 1f;
            if (!Zoom) return best;
            lock (evs)
                foreach (Ev e in evs)
                {
                    if (e.Hidden || t < e.T) continue;
                    long d = Age(e, t, extra);
                    // 元に戻し始める時刻:輪が消えたとき。静止するときは静止が終わるまでは拡大したまま
                    long outAt = RingMs;
                    if (Hold && Enabled) outAt = Math.Max(outAt, HoldAtMs + HoldMs);
                    float k;
                    if (d < HoldAtMs) k = d / (float)HoldAtMs;                              // 拡大していく
                    else if (d < outAt) k = 1f;                                             // 拡大したまま
                    else if (d < outAt + ZoomOutMs) k = 1f - (d - outAt) / (float)ZoomOutMs; // 元に戻す
                    else continue;
                    k = k * k * (3 - 2 * k);   // なめらかに(ゆっくり始まりゆっくり終わる)
                    float z = 1f + ZoomPct / 100f * k;
                    if (z >= best) { best = z; center = e.P; }
                }
            return best;
        }

        // 拡大した絵を返す(拡大しないときは src をそのまま返す。新しく作ったときは呼び出し側で Dispose)
        public Bitmap Zoomed(Bitmap src, Point origin, long t, long extra = 0)
        {
            Point c;
            float z = ZoomAt(t, out c, extra);
            if (z <= 1.001f) return src;
            int w = src.Width, h = src.Height;
            float cw = w / z, ch = h / z;
            float left = Math.Max(0, Math.Min(w - cw, c.X - origin.X - cw / 2));
            float top = Math.Max(0, Math.Min(h - ch, c.Y - origin.Y - ch / 2));
            Bitmap dst = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using (ImageAttributes ia = new ImageAttributes())
                {
                    ia.SetWrapMode(WrapMode.TileFlipXY);   // 端がにじまないように
                    g.DrawImage(src, new Rectangle(0, 0, w, h), left, top, cw, ch, GraphicsUnit.Pixel, ia);
                }
            }
            return dst;
        }

        // コマ(時刻 t)に描き込む。origin = 録画範囲の左上の画面座標、cursor = そのコマのカーソル位置
        // extra = 静止中のコマを描くときの、静止の始まりからの時間(このときは押している丸は出さない)
        public void Draw(Graphics g, Point origin, long t, Point cursor, long extra = 0)
        {
            if (!Enabled) return;   // 輪の表示が OFF(拡大だけ使うとき)
            float baseR = Program.D(new int[] { 10, 16, 24 }[Size]);
            float ringR = baseR * 1.5f;   // 輪の大きさ(小・中・大とも 1.5 倍)
            g.SmoothingMode = SmoothingMode.AntiAlias;
            List<Ev> live = new List<Ev>();
            List<long> ages = new List<long>();
            bool pressL = false, pressR = false;
            lock (evs)
                foreach (Ev e in evs)
                {
                    if (e.T <= t && (e.UpT < 0 || t < e.UpT)) { if (e.IsLeft) pressL = true; else pressR = true; }
                    if (e.Hidden || t < e.T) continue;
                    long age = Age(e, t, extra);
                    if (age >= RingMs) continue;
                    live.Add(e); ages.Add(age);
                }
            // 押している間は、カーソルの位置に半透明の丸
            if (extra == 0 && (pressL || pressR))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(110, pressL ? Left : Right)))
                    g.FillEllipse(b, cursor.X - origin.X - baseR * 0.7f, cursor.Y - origin.Y - baseR * 0.7f, baseR * 1.4f, baseR * 1.4f);
            for (int li = 0; li < live.Count; li++)
            {
                Ev e = live[li];
                long age = ages[li];
                // 広がる間は濃いまま(だんだんゆっくり)→ 広がりきったら薄くなって消える
                float pe = Math.Min(1f, age / (float)ExpandMs);
                float pr = 1f - (1f - pe) * (1f - pe);
                int a = age < ExpandMs ? 255 : (int)(255 * (1 - (age - ExpandMs) / (float)FadeMs));
                a = Math.Max(0, Math.Min(255, a));
                Color c = e.K == Kind.Left ? Left : e.K == Kind.Right ? Right : Double;
                float x = e.P.X - origin.X, y = e.P.Y - origin.Y;
                float r = ringR * (0.6f + 3.4f * pr);   // 最大は ringR の 4 倍
                // 輪の内側:縁は色付き、中心にいくほど透明になるグラデーション
                if (r > 1f)
                    using (GraphicsPath gp = new GraphicsPath())
                    {
                        gp.AddEllipse(x - r, y - r, r * 2, r * 2);
                        using (PathGradientBrush pg = new PathGradientBrush(gp))
                        {
                            pg.CenterPoint = new PointF(x, y);
                            pg.CenterColor = Color.FromArgb(0, c);
                            pg.SurroundColors = new Color[] { Color.FromArgb(a * 55 / 100, c) };
                            g.FillPath(pg, gp);
                        }
                    }
                using (Pen pen = new Pen(Color.FromArgb(a, c), Math.Max(2f, ringR / 5f)))
                {
                    g.DrawEllipse(pen, x - r, y - r, r * 2, r * 2);
                    if (e.K == Kind.Double) { float r2 = r * 0.775f; g.DrawEllipse(pen, x - r2, y - r2, r2 * 2, r2 * 2); }   // 二重の輪
                }
                if (Label)
                {
                    string s = e.K == Kind.Left ? "左" : e.K == Kind.Right ? "右" : "ダブル";
                    using (Font f = new Font("Yu Gothic UI", Math.Max(8f, baseR * 0.55f), FontStyle.Bold, GraphicsUnit.Pixel))
                    {
                        SizeF sz = g.MeasureString(s, f);
                        RectangleF lr = new RectangleF(x + baseR * 0.9f, y + baseR * 0.9f, sz.Width + 4, sz.Height);
                        using (SolidBrush bg = new SolidBrush(Color.FromArgb(Math.Min(a, 200), c))) g.FillRectangle(bg, lr);
                        using (SolidBrush fg = new SolidBrush(Color.FromArgb(a, Color.White))) g.DrawString(s, f, fg, lr.X + 2, lr.Y);
                    }
                }
            }
        }

        // ---- 低レベルのマウスフック(録画中だけ) ----
        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
        [StructLayout(LayoutKind.Sequential)] struct MSLL { public int X, Y; public uint data, flags, time; public IntPtr extra; }
        IntPtr hook;
        HookProc proc;
        Func<long> clock;

        public void Hook(Func<long> clock)
        {
            this.clock = clock;
            proc = OnHook;
            try { hook = SetWindowsHookEx(14, proc, GetModuleHandle(null), 0); } catch { hook = IntPtr.Zero; }
        }
        public void Unhook()
        {
            if (hook != IntPtr.Zero) { try { UnhookWindowsHookEx(hook); } catch { } hook = IntPtr.Zero; }
        }
        IntPtr OnHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                int m = wParam.ToInt32();
                MSLL s = (MSLL)Marshal.PtrToStructure(lParam, typeof(MSLL));
                Point p = new Point(s.X, s.Y);
                if (m == 0x201) AddDown(true, clock(), p);
                else if (m == 0x204) AddDown(false, clock(), p);
                else if (m == 0x202) AddUp(true, clock());
                else if (m == 0x205) AddUp(false, clock());
            }
            return CallNextHookEx(hook, code, wParam, lParam);
        }
    }

    // 撮った瞬間のマウスカーソル(あとで描き込むために、形と位置を覚えておく)
    public class CursorSnap
    {
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT pt; }
        [StructLayout(LayoutKind.Sequential)] struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
        [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
        [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr h, out ICONINFO ii);
        [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr h, int w, int hh, int step, IntPtr br, int flags);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

        IntPtr handle; int hx, hy;
        public Point Pos;

        public static CursorSnap Take()
        {
            try
            {
                CURSORINFO ci = new CURSORINFO();
                ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
                if (!GetCursorInfo(ref ci)) return null;
                CursorSnap c = new CursorSnap();
                c.Pos = new Point(ci.pt.X, ci.pt.Y);
                if ((ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero) return c;   // 非表示:位置だけ
                c.handle = ci.hCursor;
                ICONINFO ii;
                if (GetIconInfo(ci.hCursor, out ii))
                {
                    c.hx = ii.xHotspot; c.hy = ii.yHotspot;
                    if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                    if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                }
                return c;
            }
            catch { return null; }
        }

        public void Draw(Bitmap bmp, Point origin)
        {
            if (handle == IntPtr.Zero) return;
            try
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    try { DrawIconEx(hdc, Pos.X - hx - origin.X, Pos.Y - hy - origin.Y, handle, 0, 0, 0, IntPtr.Zero, 3); }
                    finally { g.ReleaseHdc(hdc); }
                }
            }
            catch { }
        }
    }

    // ================= 録画 =================
    public class VideoRecorder
    {
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
        // 録画に写り込まないようにする(Windows 10 2004 以降)
        public static void ExcludeFromCapture(Form f)
        {
            try { SetWindowDisplayAffinity(f.Handle, 0x11); } catch { }
        }

        public readonly VideoInfo Info;
        readonly Rectangle area;
        readonly bool withCursor;
        ClickFx clickFx;   // マウスを写し込むときのクリック表示(設定で ON のとき)
        Thread th;
        volatile bool stop;
        public int FrameCount;
        public readonly Stopwatch Clock = new Stopwatch();
        public string Error;

        public VideoRecorder(Rectangle area, bool withCursor)
        {
            this.area = area;
            this.withCursor = withCursor;
            Info = new VideoInfo();
            Info.Dir = Path.Combine(Program.LibDir, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + VideoInfo.Ext);
            Info.Width = area.Width; Info.Height = area.Height; Info.Fps = 15;
            Directory.CreateDirectory(Info.FramesDir);
        }

        public void Start()
        {
            if (withCursor)
            {
                ClickFx c = ClickFx.Load();
                if (c.Enabled || c.Zoom) { clickFx = c; clickFx.Hook(delegate { return Clock.ElapsedMilliseconds; }); }   // フックは UI スレッドで
            }
            th = new Thread(Loop);
            th.IsBackground = true;
            th.Priority = ThreadPriority.AboveNormal;
            th.Start();
        }

        public void Stop()
        {
            stop = true;
            if (th != null) th.Join(5000);
            // 静止のスローモーションを作り終えるのを待つ
            Thread[] ws;
            lock (holdWorkers) ws = holdWorkers.ToArray();
            foreach (Thread w in ws) w.Join(60000);
            if (clickFx != null) clickFx.Unhook();
            Clock.Stop();
            try { Info.Save(); } catch { }
        }

        // 撮った 1 コマ(書き出し待ち)
        class Shot { public Bitmap Bmp; public long T; public int Count; public CursorSnap Cur; }
        readonly List<Thread> holdWorkers = new List<Thread>();   // 静止のスローモーションを作っているスレッド
        int outIndex;   // 次に書き出すファイルの番号(静止のコマを足すので、撮ったコマ数 FrameCount より多くなる)

        void Loop()
        {
            ImageCodecInfo jpg = null;
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders()) if (c.FormatID == ImageFormat.Jpeg.Guid) jpg = c;
            EncoderParameters ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
            double frameMs = 1000.0 / Info.Fps;
            // クリック表示があるときは、ダブルクリックかどうか決まるまでコマを手元に置いてから描き込む
            int delay = clickFx != null ? clickFx.DelayMs : 0;
            Queue<Shot> pending = new Queue<Shot>();
            try
            {
                Clock.Start();
                while (!stop)
                {
                    Shot sh = new Shot();
                    sh.Bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
                    using (Graphics g = Graphics.FromImage(sh.Bmp))
                        g.CopyFromScreen(area.Location, Point.Empty, area.Size, CopyPixelOperation.SourceCopy);
                    sh.T = Clock.ElapsedMilliseconds;
                    if (withCursor) sh.Cur = CursorSnap.Take();
                    // 遅れたぶんは同じコマを重ねて実時間に合わせる
                    int due = (int)(Clock.ElapsedMilliseconds / frameMs) + 1;
                    sh.Count = Math.Max(1, Math.Min(Info.Fps, due - FrameCount));
                    FrameCount += sh.Count;
                    pending.Enqueue(sh);
                    while (pending.Count > 0 && pending.Peek().T <= Clock.ElapsedMilliseconds - delay)
                        WriteShot(pending.Dequeue(), jpg, ep);
                    double wait = FrameCount * frameMs - Clock.ElapsedMilliseconds;
                    while (wait > 1 && !stop) { Thread.Sleep((int)Math.Min(wait, 15)); wait = FrameCount * frameMs - Clock.ElapsedMilliseconds; }
                }
                while (pending.Count > 0) WriteShot(pending.Dequeue(), jpg, ep);   // 残りを書き出す
            }
            catch (Exception ex) { Error = ex.Message; }
            finally { while (pending.Count > 0) pending.Dequeue().Bmp.Dispose(); }
        }

        void WriteShot(Shot sh, ImageCodecInfo jpg, EncoderParameters ep)
        {
            try
            {
                Point cp = sh.Cur != null ? sh.Cur.Pos : Cursor.Position;
                // 静止:クリックから HoldAtMs 後の画面を止めて、指定秒数ぶんのコマを先に足す(録画そのものは止めない)
                if (clickFx != null && clickFx.Hold && clickFx.Enabled)
                {
                    long ht;
                    while ((ht = clickFx.TakeHold(sh.T)) >= 0)
                    {
                        // 画面は止めて、輪(と拡大)は静止中も進める。コマ数が多く時間がかかるので、
                        // 番号だけ先に確保して別スレッドで作る(録画を止めない)。
                        int n = clickFx.HoldSec * Info.Fps;
                        Bitmap raw = sh.Bmp.Clone(new Rectangle(0, 0, sh.Bmp.Width, sh.Bmp.Height), sh.Bmp.PixelFormat);
                        CursorSnap cur = sh.Cur; Point hcp = cp; long hht = ht; int first = outIndex;
                        outIndex += n;
                        Thread w = new Thread(delegate () { RenderHold(raw, cur, hcp, hht, n, first); });
                        w.IsBackground = true;
                        w.Priority = ThreadPriority.BelowNormal;
                        lock (holdWorkers) holdWorkers.Add(w);
                        w.Start();
                    }
                }
                using (Graphics g = Graphics.FromImage(sh.Bmp))
                {
                    if (clickFx != null) clickFx.Draw(g, area.Location, sh.T, cp);   // クリックの輪(カーソルの下に)
                }
                if (sh.Cur != null) sh.Cur.Draw(sh.Bmp, area.Location);
                WriteZoomed(sh.Bmp, sh.T, sh.Count, jpg, ep);
            }
            finally { sh.Bmp.Dispose(); }
        }

        // クリック位置の拡大をかけてから書き出す
        void WriteZoomed(Bitmap bmp, long t, int count, ImageCodecInfo jpg, EncoderParameters ep)
        {
            Bitmap z = clickFx != null ? clickFx.Zoomed(bmp, area.Location, t) : bmp;
            try { WriteFrames(z, count, jpg, ep); }
            finally { if (z != bmp) z.Dispose(); }
        }

        // 静止中のコマ:first 番から n コマ(= 静止の秒数)。画面は静止の時刻 ht のまま止め、
        // 輪と拡大は静止の始まりからの時間 extra だけ進めて描く(最後のコマが静止のあとの最初のコマにつながる)。
        void RenderHold(Bitmap raw, CursorSnap cur, Point cp, long ht, int n, int first)
        {
            try
            {
                ImageCodecInfo jpg = null;
                foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders()) if (c.FormatID == ImageFormat.Jpeg.Guid) jpg = c;
                EncoderParameters ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                long holdMs = clickFx.HoldSec * 1000L;
                for (int i = 1; i <= n; i++)
                {
                    using (Bitmap hb = raw.Clone(new Rectangle(0, 0, raw.Width, raw.Height), raw.PixelFormat))
                    {
                        long extra = holdMs * i / n;
                        using (Graphics g = Graphics.FromImage(hb)) clickFx.Draw(g, area.Location, ht, cp, extra);
                        if (cur != null) cur.Draw(hb, area.Location);
                        Bitmap z = clickFx.Zoomed(hb, area.Location, ht, extra);
                        try { File.WriteAllBytes(Path.Combine(Info.FramesDir, (first + i - 1).ToString("000000") + ".jpg"), Encode(z, jpg, ep)); }
                        finally { if (z != hb) z.Dispose(); }
                    }
                }
            }
            catch (Exception ex) { Error = ex.Message; }
            finally { raw.Dispose(); }
        }

        // 同じ絵を count コマぶん、連番の JPEG で書き出す
        void WriteFrames(Bitmap bmp, int count, ImageCodecInfo jpg, EncoderParameters ep)
        {
            byte[] data = Encode(bmp, jpg, ep);
            for (int i = 0; i < count; i++) WriteBytes(data);
        }
        static byte[] Encode(Bitmap bmp, ImageCodecInfo jpg, EncoderParameters ep)
        {
            using (MemoryStream ms = new MemoryStream()) { bmp.Save(ms, jpg, ep); return ms.ToArray(); }
        }
        void WriteBytes(byte[] data)
        {
            File.WriteAllBytes(Path.Combine(Info.FramesDir, (outIndex++).ToString("000000") + ".jpg"), data);
        }
    }

    // 録画範囲を示す赤い枠(クリックは通す、録画には写らない)
    class RecFrame : Form
    {
        public RecFrame(Rectangle r)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(230, 40, 40);
            Bounds = r;
            Opacity = 0.85;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x20 | 0x80 | 0x08000000;   // TRANSPARENT | TOOLWINDOW | NOACTIVATE
                return cp;
            }
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            VideoRecorder.ExcludeFromCapture(this);
        }
    }

    // 録画開始までのカウントダウン(範囲の中央に大きな数字。クリックは下に通すので、数えている間もふつうに操作できる)
    class CountdownForm : Form
    {
        int number;
        public CountdownForm(Rectangle area)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(30, 30, 34);
            Opacity = 0.78;
            int s = Program.D(130);
            Bounds = new Rectangle(area.X + area.Width / 2 - s / 2, area.Y + area.Height / 2 - s / 2, s, s);
            using (GraphicsPath p = new GraphicsPath()) { p.AddEllipse(0, 0, s, s); Region = new Region(p); }
        }
        public int Number { set { number = value; Invalidate(); } }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x20 | 0x80 | 0x08000000;   // TRANSPARENT | TOOLWINDOW | NOACTIVATE
                return cp;
            }
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            VideoRecorder.ExcludeFromCapture(this);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            int s = ClientSize.Width;
            using (Font big = new Font("Segoe UI", s * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (Font small = new Font("Yu Gothic UI", Math.Max(9f, s * 0.1f), FontStyle.Bold, GraphicsUnit.Pixel))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                g.DrawString(number.ToString(), big, Brushes.White, new RectangleF(0, -s * 0.06f, s, s), sf);
                g.DrawString("録画開始まで", small, Brushes.Gainsboro, new RectangleF(0, s * 0.62f, s, s * 0.2f), sf);
            }
        }
    }

    // 録画中の操作バー(停止ボタンと経過時間)
    public class RecordBar : Form
    {
        long maxMs;          // 最大録画時間
        bool autoStopped;
        Label dot, lbl;   // ● は文字と分けて、色だけを切り替える(文字の幅が変わらないように)
        Button stopBtn;
        int countdown;    // 録画開始までの残り秒数(0 = 録画中)
        System.Windows.Forms.Timer timer;
        VideoRecorder rec;
        List<RecFrame> frames = new List<RecFrame>();
        public event EventHandler StopClicked;

        public RecordBar(VideoRecorder rec, Rectangle area, string hotkey)
        {
            this.rec = rec;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Font = Program.UiFont;
            BackColor = Color.FromArgb(40, 40, 44);
            ClientSize = new Size(Program.D(300), Program.D(36));
            maxMs = Math.Max(1, Math.Min(600, Program.GetInt(Program.LoadSettings(), "video.maxmin", 10))) * 60000L;
            dot = new Label();
            dot.Text = "●";
            dot.Font = new Font("Yu Gothic UI", 10f, FontStyle.Bold);
            dot.TextAlign = ContentAlignment.MiddleCenter;
            dot.SetBounds(Program.D(6), 0, Program.D(20), Program.D(36));
            Controls.Add(dot);
            lbl = new Label();
            lbl.ForeColor = Color.White;
            lbl.Font = new Font("Yu Gothic UI", 10f, FontStyle.Bold);
            lbl.TextAlign = ContentAlignment.MiddleLeft;
            lbl.SetBounds(Program.D(26), 0, Program.D(164), Program.D(36));
            Controls.Add(lbl);
            Button b = stopBtn = new Button();
            b.Text = "■ 停止";
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Color.FromArgb(243, 173, 173);
            b.SetBounds(Program.D(194), Program.D(4), Program.D(98), Program.D(28));
            b.Click += delegate { if (StopClicked != null) StopClicked(this, EventArgs.Empty); };
            Controls.Add(b);
            new ToolTip().SetToolTip(b, "録画を止める" + (hotkey != "" ? "(" + hotkey + " でも止まります)" : ""));

            // バーの位置:範囲の下 → 上 → 範囲の中(右下)
            Rectangle wa = Screen.FromRectangle(area).WorkingArea;
            int x = Math.Max(wa.Left, Math.Min(wa.Right - Width, area.Right - Width));
            int y = area.Bottom + Program.D(6);
            if (y + Height > wa.Bottom) y = area.Top - Height - Program.D(6);
            if (y < wa.Top) y = area.Bottom - Height - Program.D(8);
            Location = new Point(x, y);

            // 赤い枠(範囲の外側 3px)
            int t = Program.D(3);
            Rectangle[] rs = {
                new Rectangle(area.Left - t, area.Top - t, area.Width + t * 2, t),
                new Rectangle(area.Left - t, area.Bottom, area.Width + t * 2, t),
                new Rectangle(area.Left - t, area.Top, t, area.Height),
                new Rectangle(area.Right, area.Top, t, area.Height) };
            foreach (Rectangle r in rs) frames.Add(new RecFrame(r));

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 250;
            timer.Tick += delegate
            {
                UpdateText();
                // 最大録画時間(設定)に達したら自動で止める
                if (!autoStopped && countdown == 0 && rec.Clock.ElapsedMilliseconds >= maxMs)
                {
                    autoStopped = true;
                    if (StopClicked != null) StopClicked(this, EventArgs.Empty);
                }
            };
            UpdateText();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        // カウントダウン中は「録画開始まで N 秒」と「✖ 中止」を出す
        public int Countdown
        {
            set
            {
                countdown = value;
                stopBtn.Text = value > 0 ? "✖ 中止" : "■ 停止";
                UpdateText();
            }
        }

        void UpdateText()
        {
            if (countdown > 0)
            {
                dot.ForeColor = Color.FromArgb(160, 160, 160);
                string c = "録画開始まで " + countdown + " 秒";
                if (lbl.Text != c) lbl.Text = c;
                return;
            }
            TimeSpan t = rec.Clock.Elapsed;
            bool blink = (t.Milliseconds / 500) == 0;
            dot.ForeColor = blink ? Color.FromArgb(255, 80, 80) : Color.White;   // ● だけ赤 ⇔ 白
            string s = string.Format("録画中 {0:00}:{1:00} / {2}分", (int)t.TotalMinutes, t.Seconds, maxMs / 60000);
            if (lbl.Text != s) lbl.Text = s;
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
            timer.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop(); timer.Dispose();
            foreach (RecFrame f in frames) { f.Close(); f.Dispose(); }
            base.OnFormClosed(e);
        }
    }
}
