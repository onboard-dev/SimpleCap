using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SimpleCap
{
    // ================= 結合 =================
    //  ブラウズ画面の一覧で選んだビデオ(.scvid)/ GIF / MP4 を、一覧の並び順に 1 本につなぐ。
    //  ・ビデオは IN / OUT の区間と注釈を反映する(「🎬 生成」と同じ)
    //  ・大きさは最初のものに合わせる(違う大きさは縦横比を保って縮小し、余白は黒)
    //  ・すべて GIF なら GIF、それ以外は MP4。保存先フォルダに「日時_結合.mp4 / .gif」で作る
    public static class Merger
    {
        public const int Fps = 15;

        public static bool CanMerge(string path) { return VideoInfo.IsVideo(path) || MediaFile.IsMedia(path); }

        // 結合して、できたファイルのパスを返す(取りやめ・失敗は null)
        public static string Run(IWin32Window owner, List<string> paths)
        {
            List<string> items = new List<string>();
            foreach (string p in paths) if (CanMerge(p)) items.Add(p);
            if (items.Count < 2) return null;

            string exe = FFmpeg.Ensure(owner);
            if (exe == null) return null;

            bool allGif = true;
            foreach (string p in items) if (!MediaFile.IsGif(p)) allGif = false;
            string outPath = Path.Combine(Program.LibDir, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_結合" + (allGif ? ".gif" : ".mp4"));

            List<string> tmps = new List<string>();
            try
            {
                Cursor.Current = Cursors.WaitCursor;
                List<string> inputs = new List<string>();
                int total = 0;
                Size size = Size.Empty;
                foreach (string p in items)
                {
                    Size s = Size.Empty;
                    if (VideoInfo.IsVideo(p))
                    {
                        VideoInfo info = VideoInfo.Load(p);
                        string tmp = Path.Combine(Path.GetTempPath(), "SimpleCap_" + Guid.NewGuid().ToString("N"));
                        tmps.Add(tmp);
                        int n = PrepareFrames(info, tmp, out s);
                        if (n == 0) continue;   // コマがないビデオは飛ばす
                        total += (int)Math.Ceiling(n * (double)Fps / Math.Max(1, info.Fps));
                        inputs.Add(string.Format("-framerate {0} -i \"{1}\"", info.Fps, Path.Combine(tmp, "%06d.jpg")));
                    }
                    else
                    {
                        double sec;
                        if (MediaFile.IsGif(p)) s = GifInfo(p, out sec);
                        else s = Probe(exe, p, out sec);
                        total += (int)Math.Ceiling(sec * Fps);
                        inputs.Add("-i \"" + p + "\"");
                    }
                    if (size.IsEmpty && !s.IsEmpty) size = s;
                }
                Cursor.Current = Cursors.Default;
                if (inputs.Count < 2)
                {
                    MessageBox.Show(owner, "結合できるものが 2 つ以上ありません。", "SimpleCap");
                    return null;
                }
                if (size.IsEmpty) size = new Size(1280, 720);
                int W = Math.Max(2, size.Width / 2 * 2), H = Math.Max(2, size.Height / 2 * 2);   // MP4 は偶数の大きさが必要

                // 大きさとコマの速さをそろえてから、順につなぐ
                string fmt = allGif ? "rgb24" : "yuv420p";
                System.Text.StringBuilder f = new System.Text.StringBuilder();
                for (int i = 0; i < inputs.Count; i++)
                    f.AppendFormat("[{0}:v]fps={1},scale={2}:{3}:force_original_aspect_ratio=decrease,pad={2}:{3}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,format={4}[v{0}];",
                        i, Fps, W, H, fmt);
                for (int i = 0; i < inputs.Count; i++) f.AppendFormat("[v{0}]", i);
                f.AppendFormat("concat=n={0}:v=1:a=0", inputs.Count);
                if (allGif) f.Append(",split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle[out]");
                else f.Append("[out]");

                string args = "-y " + string.Join(" ", inputs.ToArray()) + " -filter_complex \"" + f + "\" -map \"[out]\" -an "
                    + (allGif ? "-loop 0 " : "-c:v libx264 -pix_fmt yuv420p -crf 18 -preset veryfast -movflags +faststart ")
                    + "\"" + outPath + "\"";
                bool ok = FFmpeg.Run(owner, exe, args, total, "🔗 " + inputs.Count + " 本を結合しています…");
                if (!ok) { try { if (File.Exists(outPath)) File.Delete(outPath); } catch { } return null; }
                return outPath;
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "結合できませんでした: " + ex.Message, "SimpleCap");
                return null;
            }
            finally
            {
                Cursor.Current = Cursors.Default;
                foreach (string t in tmps) try { if (Directory.Exists(t)) Directory.Delete(t, true); } catch { }
            }
        }

        // ビデオの使うコマ(IN〜OUT の区間、なければ全体)を、注釈を描き込んで連番で tmp へ。戻り値 = コマ数
        static int PrepareFrames(VideoInfo info, string tmp, out Size size)
        {
            size = new Size(info.Width, info.Height);
            List<string> frames = info.Frames();
            List<int> sel = new List<int>();
            if (info.Segments.Count == 0) { for (int i = 0; i < frames.Count; i++) sel.Add(i); }
            else
                foreach (int[] s in info.Segments)
                    for (int i = Math.Max(0, s[0]); i <= Math.Min(frames.Count - 1, s[1]); i++) sel.Add(i);
            if (sel.Count == 0) return 0;
            Directory.CreateDirectory(tmp);
            List<Obj> annos = info.LoadAnnotations();
            ImageCodecInfo jpg = null;
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders()) if (c.FormatID == ImageFormat.Jpeg.Guid) jpg = c;
            EncoderParameters ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 95L);
            for (int i = 0; i < sel.Count; i++)
            {
                string dst = Path.Combine(tmp, i.ToString("000000") + ".jpg");
                bool any = false;
                foreach (Obj o in annos) if (o.Visible && VideoInfo.ShowAt(o, sel[i])) { any = true; break; }
                if (!any) { File.Copy(frames[sel[i]], dst); continue; }
                using (Bitmap fb = new Bitmap(new MemoryStream(File.ReadAllBytes(frames[sel[i]]))))
                {
                    using (Graphics g = Graphics.FromImage(fb)) VideoInfo.DrawAnnotations(g, fb, annos, sel[i]);
                    fb.Save(dst, jpg, ep);
                }
            }
            if (size.Width <= 0 || size.Height <= 0)
                using (Bitmap b = new Bitmap(new MemoryStream(File.ReadAllBytes(frames[sel[0]])))) size = b.Size;
            return sel.Count;
        }

        // GIF の大きさと長さ(秒)
        static Size GifInfo(string path, out double sec)
        {
            sec = 1;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (Image img = Image.FromStream(fs))
                {
                    int n = 1;
                    try { n = img.GetFrameCount(FrameDimension.Time); } catch { }
                    int ms = 0;
                    try
                    {
                        byte[] d = img.GetPropertyItem(0x5100).Value;   // 各コマの表示時間(1/100 秒)
                        for (int i = 0; i + 3 < d.Length && i / 4 < n; i += 4) ms += Math.Max(2, BitConverter.ToInt32(d, i)) * 10;
                    }
                    catch { ms = n * 100; }
                    sec = Math.Max(0.1, ms / 1000.0);
                    return img.Size;
                }
            }
            catch { return Size.Empty; }
        }

        // MP4 の大きさと長さ(秒)を ffmpeg の表示から読む
        static Size Probe(string exe, string path, out double sec)
        {
            sec = 1;
            try
            {
                ProcessStartInfo si = new ProcessStartInfo(exe, "-hide_banner -i \"" + path + "\"");
                si.UseShellExecute = false; si.CreateNoWindow = true; si.RedirectStandardError = true;
                string err;
                using (Process p = Process.Start(si)) { err = p.StandardError.ReadToEnd(); p.WaitForExit(5000); }
                Match d = Regex.Match(err, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
                if (d.Success)
                    sec = int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60
                        + double.Parse(d.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                Match m = Regex.Match(err, @"Video:.*?\s(\d{2,5})x(\d{2,5})");
                if (m.Success) return new Size(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
            }
            catch { }
            return Size.Empty;
        }
    }
}
