using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace SimpleCap
{
    // ================= スタンプの保存場所 =================
    //  %APPDATA%\SimpleCap\stamps\<グループ名>\*.png / *.jpg
    //  グループのダウンロード元 URL は <グループ>\source.txt
    public static class StampStore
    {
        public static string Root
        {
            get
            {
                string r = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SimpleCap"), "stamps");
                Directory.CreateDirectory(r);
                return r;
            }
        }

        public static List<string> Groups()
        {
            List<string> g = new List<string>();
            foreach (string d in Directory.GetDirectories(Root)) g.Add(Path.GetFileName(d));
            if (g.Count == 0) { Directory.CreateDirectory(Path.Combine(Root, "スタンプ")); g.Add("スタンプ"); }
            g.Sort(StringComparer.CurrentCulture);
            return g;
        }

        public static string Dir(string group) { return Path.Combine(Root, group); }

        public static string SafeName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            s = s.Trim().Trim('.');
            return s.Length == 0 ? "グループ" : s;
        }

        public static string CreateGroup(string name)
        {
            name = SafeName(name);
            string n = name; int i = 2;
            while (Directory.Exists(Dir(n))) n = name + " (" + (i++) + ")";
            Directory.CreateDirectory(Dir(n));
            return n;
        }

        public static string RenameGroup(string oldName, string newName)
        {
            newName = SafeName(newName);
            if (newName == oldName) return oldName;
            if (Directory.Exists(Dir(newName))) throw new IOException("同じ名前のグループがあります。");
            Directory.Move(Dir(oldName), Dir(newName));
            return newName;
        }

        public static void DeleteGroup(string name)
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(Dir(name),
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }

        public static List<string> Images(string group)
        {
            List<string> l = new List<string>();
            if (!Directory.Exists(Dir(group))) return l;
            foreach (string f in Directory.GetFiles(Dir(group)))
            {
                string e = Path.GetExtension(f).ToLowerInvariant();
                if (e == ".png" || e == ".jpg" || e == ".jpeg" || e == ".gif" || e == ".bmp") l.Add(f);
            }
            l.Sort(StringComparer.OrdinalIgnoreCase);
            return l;
        }

        public static string GetSource(string group)
        {
            try { string p = Path.Combine(Dir(group), "source.txt"); return File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8).Trim() : ""; }
            catch { return ""; }
        }
        public static void SetSource(string group, string url)
        {
            try { File.WriteAllText(Path.Combine(Dir(group), "source.txt"), url ?? "", Encoding.UTF8); } catch { }
        }

        // 画像を登録 (同名があれば番号を付ける)
        public static string AddImage(string group, byte[] data, string nameHint)
        {
            string ext = Path.GetExtension(nameHint).ToLowerInvariant();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".gif" && ext != ".bmp") ext = ".png";
            string baseName = SafeName(Path.GetFileNameWithoutExtension(nameHint));
            if (baseName.Length > 60) baseName = baseName.Substring(0, 60);
            string p = Path.Combine(Dir(group), baseName + ext);
            int i = 2;
            while (File.Exists(p)) p = Path.Combine(Dir(group), baseName + "_" + (i++) + ext);
            File.WriteAllBytes(p, data);
            return p;
        }

        // ---- フォルダ / zip から取り込み (PNG と JPEG だけ) ----
        const long MaxImport = 50L * 1024 * 1024;   // 1 ファイル 50MB まで

        static bool IsPngOrJpegName(string name)
        {
            string n = Path.GetFileName(name);
            if (n.StartsWith("._")) return false;    // macOS のゴミファイル
            string e = Path.GetExtension(n).ToLowerInvariant();
            return e == ".png" || e == ".jpg" || e == ".jpeg";
        }
        // 中身が本当に PNG / JPEG か(拡張子だけ偽装されたものを除く)
        static bool IsPngOrJpegData(byte[] d)
        {
            if (d == null || d.Length < 8) return false;
            if (d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return true;   // PNG
            if (d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return true;                    // JPEG
            return false;
        }

        // フォルダ内(サブフォルダも含む)の PNG / JPEG をグループにコピー。戻り値=登録数
        public static int ImportFolder(string group, string folder, out int skipped)
        {
            int n = 0; skipped = 0;
            Stack<string> dirs = new Stack<string>();
            dirs.Push(folder);
            while (dirs.Count > 0)
            {
                string d = dirs.Pop();
                string[] files, subs;
                try { files = Directory.GetFiles(d); subs = Directory.GetDirectories(d); }
                catch { continue; }    // アクセスできないフォルダは飛ばす
                foreach (string s in subs) dirs.Push(s);
                foreach (string f in files)
                {
                    if (!IsPngOrJpegName(f)) { skipped++; continue; }
                    try
                    {
                        if (new FileInfo(f).Length > MaxImport) { skipped++; continue; }
                        byte[] data = File.ReadAllBytes(f);
                        if (!IsPngOrJpegData(data)) { skipped++; continue; }
                        AddImage(group, data, Path.GetFileName(f));
                        n++;
                    }
                    catch { skipped++; }
                }
            }
            return n;
        }

        // zip 内の PNG / JPEG をグループにコピー。戻り値=登録数
        public static int ImportZip(string group, string zipPath, out int skipped)
        {
            int n = 0; skipped = 0;
            Encoding sjis;
            try { sjis = Encoding.GetEncoding(932); } catch { sjis = Encoding.UTF8; }   // Windows で作った日本語名の zip 対策
            using (FileStream fs = File.OpenRead(zipPath))
            using (ZipArchive za = new ZipArchive(fs, ZipArchiveMode.Read, false, sjis))
            {
                foreach (ZipArchiveEntry en in za.Entries)
                {
                    if (en.FullName.EndsWith("/") || en.FullName.EndsWith("\\")) continue;   // フォルダ
                    if (en.FullName.StartsWith("__MACOSX/") || !IsPngOrJpegName(en.Name)) { skipped++; continue; }
                    if (en.Length > MaxImport) { skipped++; continue; }
                    try
                    {
                        byte[] data;
                        using (Stream s = en.Open())
                        using (MemoryStream ms = new MemoryStream())
                        {
                            s.CopyTo(ms);
                            data = ms.ToArray();
                        }
                        if (!IsPngOrJpegData(data)) { skipped++; continue; }
                        AddImage(group, data, en.Name);
                        n++;
                    }
                    catch { skipped++; }
                }
            }
            return n;
        }

        public static void DeleteImage(string path)
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }

        // サムネイル (ファイルをロックしない)
        public static Bitmap Thumb(string path, int size)
        {
            using (Bitmap src = Doc.BytesToBitmap(File.ReadAllBytes(path))) return Fit(src, size);
        }
        public static Bitmap Fit(Image src, int size)
        {
            Bitmap t = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(t))
            {
                g.Clear(Color.Transparent);
                float z = Math.Min((float)size / src.Width, (float)size / src.Height);
                if (z > 1) z = 1;
                int w = Math.Max(1, (int)(src.Width * z)), h = Math.Max(1, (int)(src.Height * z));
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle((size - w) / 2, (size - h) / 2, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
            }
            return t;
        }
    }

    // ================= Webページから画像を集める =================
    public static class StampWeb
    {
        static StampWeb()
        {
            // 古い .NET Framework 既定だと TLS1.2 が無効なことがあるため有効化
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072 | (SecurityProtocolType)768; } catch { }
        }

        static WebClient Client()
        {
            WebClient wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SimpleCap";
            wc.Encoding = Encoding.UTF8;
            return wc;
        }

        public static byte[] Download(string url)
        {
            using (WebClient wc = Client()) return wc.DownloadData(url);
        }

        // ページ内の .png / .jpg / .jpeg の URL を集める (img の src・data-src・srcset、a の href)
        public static List<string> FindImageUrls(string pageUrl)
        {
            string html;
            using (WebClient wc = Client()) html = wc.DownloadString(pageUrl);
            return ExtractImageUrls(html, new Uri(pageUrl));
        }

        public static List<string> ExtractImageUrls(string html, Uri baseUri)
        {
            List<string> list = new List<string>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            Regex attr = new Regex(@"(?:src|data-src|data-original|href|srcset|content)\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            foreach (Match m in attr.Matches(html))
            {
                foreach (string part0 in m.Groups[1].Value.Split(','))
                {
                    string part = part0.Trim().Split(' ')[0].Replace("&amp;", "&");
                    if (!Regex.IsMatch(part, @"\.(png|jpe?g)(\?|#|$)", RegexOptions.IgnoreCase)) continue;
                    Uri u;
                    if (!Uri.TryCreate(baseUri, part, out u)) continue;
                    if (u.Scheme != "http" && u.Scheme != "https") continue;
                    string full = u.AbsoluteUri;
                    // Drupal などの縮小版 (…/styles/170x170/public/…) は元画像の URL に戻す
                    string orig = Regex.Replace(full, @"/styles/[^/]+/public/", "/");
                    if (orig != full) { int q = orig.IndexOf('?'); if (q >= 0) orig = orig.Substring(0, q); full = orig; }
                    if (!seen.ContainsKey(full)) { seen[full] = true; list.Add(full); }
                    if (list.Count >= 200) return list;
                }
            }
            return list;
        }

        public static string FileNameOf(string url)
        {
            try
            {
                string n = Uri.UnescapeDataString(new Uri(url).Segments[new Uri(url).Segments.Length - 1]);
                n = n.TrimStart(':');
                return n.Length > 0 ? n : "stamp.png";
            }
            catch { return "stamp.png"; }
        }
    }

    // ================= スタンプウィンドウ (左のツールバーの右に出る) =================
    public class StampPanel : Panel
    {
        ComboBox cmbGroup;
        ComboBox cmbThumb;
        static readonly int[] ThumbPercents = { 25, 50, 75, 100, 125, 150, 175, 200, 250, 300 };
        FlowLayoutPanel flow;
        ToolTip tip = new ToolTip();
        public event Action<string> StampClicked;   // 画像パス(貼り付ける大きさは貼り付け側で決める)
        bool loading;

        public StampPanel()
        {
            Width = Program.D(190);
            BackColor = Color.FromArgb(250, 250, 251);
            Font = Program.UiFont;

            Label head = new Label();
            head.Text = "🌟 スタンプ";
            head.Font = new Font("Yu Gothic UI", 10f, FontStyle.Bold);
            head.SetBounds(Program.D(6), Program.D(6), Program.D(120), Program.D(24));
            Controls.Add(head);

            Button set = new Button();
            set.Text = "⚙";
            set.SetBounds(Width - Program.D(36), Program.D(4), Program.D(30), Program.D(26));
            set.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            set.Click += delegate { OpenSettings(); };
            tip.SetToolTip(set, "スタンプの設定(グループの追加・ダウンロード・削除)");
            Controls.Add(set);

            cmbGroup = new ComboBox();
            cmbGroup.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbGroup.SetBounds(Program.D(6), Program.D(34), Width - Program.D(12), Program.D(24));
            cmbGroup.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            cmbGroup.SelectedIndexChanged += delegate
            {
                if (loading) return;
                Dictionary<string, string> st = Program.LoadSettings();
                st["stampgroup"] = (string)cmbGroup.SelectedItem;
                Program.SaveSettings(st);
                LoadStamps();
            };
            Controls.Add(cmbGroup);

            // サムネイルの大きさ(元の大きさに対する %)
            Label ls = new Label();
            ls.Text = "サムネイル";
            ls.SetBounds(Program.D(6), Program.D(66), Program.D(80), Program.D(20));
            Controls.Add(ls);
            cmbThumb = new ComboBox();
            cmbThumb.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (int pc in ThumbPercents) cmbThumb.Items.Add(pc + "%");
            int savedPc = Program.GetInt(Program.LoadSettings(), "stampthumb", 100);
            int idxPc = Array.IndexOf(ThumbPercents, savedPc);
            cmbThumb.SelectedIndex = idxPc >= 0 ? idxPc : Array.IndexOf(ThumbPercents, 100);
            cmbThumb.SetBounds(Program.D(88), Program.D(63), Width - Program.D(94), Program.D(24));
            cmbThumb.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            cmbThumb.SelectedIndexChanged += delegate
            {
                Dictionary<string, string> st = Program.LoadSettings();
                st["stampthumb"] = ThumbPercent.ToString();
                Program.SaveSettings(st);
                LoadStamps();
            };
            tip.SetToolTip(cmbThumb, "一覧のサムネイルの大きさ。貼り付けたスタンプの大きさはハンドルで変えてください");
            Controls.Add(cmbThumb);

            flow = new FlowLayoutPanel();
            flow.SetBounds(Program.D(4), Program.D(94), Width - Program.D(8), Height - Program.D(98));
            flow.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            flow.AutoScroll = true;
            flow.BackColor = Color.White;
            Controls.Add(flow);

            ReloadGroups();
        }

        public void ReloadGroups()
        {
            loading = true;
            try
            {
                string want;
                if (!Program.LoadSettings().TryGetValue("stampgroup", out want)) want = null;
                cmbGroup.Items.Clear();
                foreach (string g in StampStore.Groups()) cmbGroup.Items.Add(g);
                int idx = want != null ? cmbGroup.Items.IndexOf(want) : -1;
                cmbGroup.SelectedIndex = idx >= 0 ? idx : (cmbGroup.Items.Count > 0 ? 0 : -1);
            }
            finally { loading = false; }
            LoadStamps();
        }

        int ThumbPercent
        {
            get { int i = cmbThumb.SelectedIndex; return i >= 0 && i < ThumbPercents.Length ? ThumbPercents[i] : 100; }
        }

        void LoadStamps()
        {
            flow.SuspendLayout();
            // 古いサムネイルを片付ける。先に PictureBox から画像を外してから Dispose しないと、
            // 取り除いた PictureBox が破棄済みの画像を描こうとして ArgumentException で落ちる
            List<Control> old = new List<Control>();
            foreach (Control c in flow.Controls) old.Add(c);
            flow.Controls.Clear();
            foreach (Control c in old)
            {
                PictureBox pb = c as PictureBox;
                if (pb != null) { Image im = pb.Image; pb.Image = null; if (im != null) im.Dispose(); }
                c.Dispose();
            }
            string g = cmbGroup.SelectedItem as string;
            if (g != null)
            {
                List<string> files = StampStore.Images(g);
                if (files.Count == 0)
                {
                    Label l = new Label();
                    l.Text = "スタンプがありません。\n右上の ⚙ から追加してください。";
                    l.AutoSize = true; l.ForeColor = Color.Gray; l.Margin = new Padding(Program.D(6));
                    flow.Controls.Add(l);
                }
                int sz = Math.Max(Program.D(16), Program.D(52) * ThumbPercent / 100);
                foreach (string f in files)
                {
                    PictureBox pb = new PictureBox();
                    pb.Size = new Size(sz, sz);
                    pb.SizeMode = PictureBoxSizeMode.CenterImage;
                    pb.Margin = new Padding(Program.D(3));
                    pb.Cursor = Cursors.Hand;
                    pb.BackColor = Color.White;
                    try { pb.Image = StampStore.Thumb(f, sz - Program.D(4)); } catch { continue; }
                    string path = f;
                    pb.Click += delegate { if (StampClicked != null) StampClicked(path); };
                    pb.MouseEnter += delegate { pb.BackColor = Color.FromArgb(220, 234, 250); };
                    pb.MouseLeave += delegate { pb.BackColor = Color.White; };
                    tip.SetToolTip(pb, Path.GetFileName(f) + "\nクリックで貼り付け(新しいレイヤ)");
                    flow.Controls.Add(pb);
                }
            }
            flow.ResumeLayout();
        }

        void OpenSettings()
        {
            using (StampSettings f = new StampSettings(cmbGroup.SelectedItem as string))
            {
                f.ShowDialog(FindForm());
                if (f.SelectedGroup != null)
                {
                    Dictionary<string, string> st = Program.LoadSettings();
                    st["stampgroup"] = f.SelectedGroup;
                    Program.SaveSettings(st);
                }
            }
            ReloadGroups();
        }
    }

    // ================= スタンプの設定ウィンドウ =================
    public class StampSettings : Form
    {
        ListBox lstGroups;
        TextBox txtUrl;
        ListView lv;
        ImageList il;
        Label lblStatus;
        public string SelectedGroup;

        public StampSettings(string current)
        {
            Text = "🌟 スタンプの設定";
            AutoScaleMode = AutoScaleMode.None;
            Font = Program.UiFont;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            if (Program.AppIcon != null) Icon = Program.AppIcon;
            ClientSize = new Size(Program.D(800), Program.D(520));
            MinimumSize = new Size(Program.D(600), Program.D(400));
            int m = Program.D(10);

            // ---- 左:グループ ----
            Label lg = new Label(); lg.Text = "📁 スタンプグループ"; lg.Font = new Font("Yu Gothic UI", 10f, FontStyle.Bold);
            lg.SetBounds(m, m, Program.D(224), Program.D(22)); Controls.Add(lg);
            lstGroups = new ListBox();
            lstGroups.SetBounds(m, m + Program.D(26), Program.D(224), ClientSize.Height - Program.D(26) - m * 2 - Program.D(108));
            lstGroups.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Bottom;
            lstGroups.IntegralHeight = false;
            lstGroups.SelectedIndexChanged += delegate { ShowGroup(); };
            Controls.Add(lstGroups);
            Button add = new Button(); add.Text = "＋ 追加";
            Button ren = new Button(); ren.Text = "✏️ 名前変更";
            Button delg = new Button(); delg.Text = "🗑 削除";
            int by = ClientSize.Height - m - Program.D(30);
            // 左下のボタン: 上段=追加・名前変更・削除、中段=フォルダ / zip から、下段=保存フォルダを開く
            int byMid = by - Program.D(72);
            add.SetBounds(m, byMid, Program.D(66), Program.D(30));
            ren.SetBounds(m + Program.D(70), byMid, Program.D(90), Program.D(30));
            delg.SetBounds(m + Program.D(164), byMid, Program.D(60), Program.D(30));
            foreach (Button b in new Button[] { add, ren, delg }) { b.Anchor = AnchorStyles.Left | AnchorStyles.Bottom; Controls.Add(b); }
            add.Click += delegate
            {
                string n = TextInputDialog.Ask(this, "新しいグループ");
                if (string.IsNullOrEmpty(n) || n.Trim() == "") return;
                string g = StampStore.CreateGroup(n.Replace("\n", " "));
                ReloadGroups(g);
            };
            // フォルダ / zip から新しいグループを作る(中の PNG / JPEG だけをコピー)
            Button addDir = new Button(); addDir.Text = "📁 フォルダから…";
            Button addZip = new Button(); addZip.Text = "🗜 zip から…";
            int by2 = by - Program.D(36);
            addDir.SetBounds(m, by2, Program.D(110), Program.D(30));
            addZip.SetBounds(m + Program.D(114), by2, Program.D(110), Program.D(30));
            foreach (Button b in new Button[] { addDir, addZip }) { b.Anchor = AnchorStyles.Left | AnchorStyles.Bottom; Controls.Add(b); }
            addDir.Click += delegate { AddGroupFromFolder(); };
            addZip.Click += delegate { AddGroupFromZip(); };
            ToolTip tip0 = new ToolTip();
            tip0.SetToolTip(add, "空のグループを追加");
            tip0.SetToolTip(addDir, "フォルダを選んで新しいグループを作ります\n中の PNG / JPEG だけをコピー(サブフォルダも対象)");
            tip0.SetToolTip(addZip, "zip ファイルを選んで新しいグループを作ります\n中の PNG / JPEG だけをコピー");
            ren.Click += delegate
            {
                string g = lstGroups.SelectedItem as string;
                if (g == null) return;
                string n = TextInputDialog.Ask(this, g);
                if (string.IsNullOrEmpty(n) || n.Trim() == "") return;
                try { ReloadGroups(StampStore.RenameGroup(g, n.Replace("\n", " "))); }
                catch (Exception ex) { MessageBox.Show(this, "名前を変更できませんでした: " + ex.Message, "SimpleCap"); }
            };
            delg.Click += delegate
            {
                string g = lstGroups.SelectedItem as string;
                if (g == null) return;
                if (MessageBox.Show(this, "グループ「" + g + "」と中のスタンプをごみ箱へ移動しますか?", "SimpleCap",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                try { StampStore.DeleteGroup(g); } catch (Exception ex) { MessageBox.Show(this, "削除できませんでした: " + ex.Message, "SimpleCap"); }
                ReloadGroups(null);
            };

            // ---- 右:ダウンロード先とスタンプ一覧 ----
            int rx = m + Program.D(238), rw = ClientSize.Width - rx - m;
            Label lu = new Label(); lu.Text = "🌐 スタンプのダウンロード先(Webページの URL)";
            lu.SetBounds(rx, m, rw, Program.D(22)); lu.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; Controls.Add(lu);
            txtUrl = new TextBox();
            txtUrl.SetBounds(rx, m + Program.D(26), rw - Program.D(120), Program.D(26));
            txtUrl.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(txtUrl);
            Button dl = new Button(); dl.Text = "⬇ ダウンロード";
            dl.SetBounds(rx + rw - Program.D(114), m + Program.D(25), Program.D(114), Program.D(28));
            dl.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            dl.Click += delegate { StartDownload(); };
            Controls.Add(dl);
            Label hint = new Label();
            hint.Text = "ページ内の PNG / JPEG 画像を探して、選んだものをこのグループに登録します。素材サイトの利用規約を確認してから使ってください。";
            hint.ForeColor = Color.FromArgb(100, 100, 100);
            hint.SetBounds(rx, m + Program.D(56), rw, Program.D(34)); hint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(hint);

            Label ls = new Label(); ls.Text = "🖼️ 登録済みのスタンプ(選んで削除できます)";
            ls.SetBounds(rx, m + Program.D(94), rw, Program.D(22)); ls.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; Controls.Add(ls);
            il = new ImageList(); il.ColorDepth = ColorDepth.Depth32Bit; il.ImageSize = new Size(Program.D(64), Program.D(64));
            lv = new ListView();
            lv.View = View.LargeIcon; lv.LargeImageList = il; lv.MultiSelect = true; lv.HideSelection = false;
            lv.SetBounds(rx, m + Program.D(118), rw, ClientSize.Height - m * 2 - Program.D(118) - Program.D(36));
            lv.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            lv.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Delete) DeleteSelectedStamps();
                if (e.KeyCode == Keys.A && e.Control) foreach (ListViewItem it in lv.Items) it.Selected = true;
            };
            Controls.Add(lv);
            Button dels = new Button(); dels.Text = "🗑 選んだスタンプを削除";
            Button addf = new Button(); addf.Text = "📂 ファイルから追加…";
            Button close = new Button(); close.Text = "閉じる"; close.DialogResult = DialogResult.OK;
            dels.SetBounds(rx, by, Program.D(170), Program.D(30));
            addf.SetBounds(rx + Program.D(176), by, Program.D(150), Program.D(30));
            close.SetBounds(ClientSize.Width - m - Program.D(90), by, Program.D(90), Program.D(30));
            dels.Anchor = addf.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            close.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            dels.Click += delegate { DeleteSelectedStamps(); };
            addf.Click += delegate { AddFromFiles(); };
            Controls.Add(dels); Controls.Add(addf); Controls.Add(close);
            // スタンプの保存フォルダを開く(フォルダごとコピーすればバックアップになる)
            Button openDir = new Button(); openDir.Text = "📂 保存フォルダを開く";
            openDir.SetBounds(m, by, Program.D(224), Program.D(30));   // 左下の一番下
            openDir.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            openDir.Click += delegate
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "\"" + StampStore.Root + "\""); }
                catch (Exception ex) { MessageBox.Show(this, "フォルダを開けませんでした: " + ex.Message, "SimpleCap"); }
            };
            new ToolTip().SetToolTip(openDir, "スタンプ画像の保存フォルダをエクスプローラーで開きます\n" + StampStore.Root
                + "\n\nこのフォルダごと別の場所にコピーするとバックアップになります。\n戻すときは、SimpleCap を終了してからこのフォルダに戻してください(フォルダ名=グループ名)。");
            Controls.Add(openDir);
            CancelButton = close;
            lblStatus = new Label();
            lblStatus.ForeColor = Color.FromArgb(0, 100, 180);
            lblStatus.SetBounds(rx + Program.D(332), by + Program.D(6), Math.Max(10, rw - Program.D(332) - Program.D(96)), Program.D(22));
            lblStatus.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            lblStatus.AutoEllipsis = true;
            Controls.Add(lblStatus);

            ReloadGroups(current);
        }

        void ReloadGroups(string select)
        {
            lstGroups.Items.Clear();
            foreach (string g in StampStore.Groups()) lstGroups.Items.Add(g);
            int i = select != null ? lstGroups.Items.IndexOf(select) : -1;
            lstGroups.SelectedIndex = i >= 0 ? i : (lstGroups.Items.Count > 0 ? 0 : -1);
        }

        string lastGroup;
        void ShowGroup()
        {
            if (lastGroup != null && Directory.Exists(StampStore.Dir(lastGroup))) StampStore.SetSource(lastGroup, txtUrl.Text.Trim());
            string g = lstGroups.SelectedItem as string;
            SelectedGroup = g;
            lastGroup = g;
            txtUrl.Text = g != null ? StampStore.GetSource(g) : "";
            LoadStampList();
        }

        void LoadStampList()
        {
            lv.BeginUpdate();
            lv.Items.Clear();
            il.Images.Clear();
            string g = lstGroups.SelectedItem as string;
            if (g != null)
                foreach (string f in StampStore.Images(g))
                {
                    try { il.Images.Add(f, StampStore.Thumb(f, il.ImageSize.Width)); } catch { continue; }
                    ListViewItem it = new ListViewItem(Path.GetFileNameWithoutExtension(f), f);
                    it.Tag = f;
                    lv.Items.Add(it);
                }
            lv.EndUpdate();
        }

        void DeleteSelectedStamps()
        {
            if (lv.SelectedItems.Count == 0) return;
            if (MessageBox.Show(this, lv.SelectedItems.Count + " 個のスタンプをごみ箱へ移動しますか?", "SimpleCap",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            foreach (ListViewItem it in lv.SelectedItems)
            {
                try { StampStore.DeleteImage((string)it.Tag); } catch (Exception ex) { MessageBox.Show(this, "削除できませんでした: " + ex.Message, "SimpleCap"); break; }
            }
            LoadStampList();
        }

        void AddFromFiles()
        {
            string g = lstGroups.SelectedItem as string;
            if (g == null) return;
            using (OpenFileDialog od = new OpenFileDialog())
            {
                od.Filter = "画像 (*.png;*.jpg;*.jpeg;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp";
                od.Multiselect = true;
                if (od.ShowDialog(this) != DialogResult.OK) return;
                foreach (string f in od.FileNames)
                {
                    try { StampStore.AddImage(g, File.ReadAllBytes(f), Path.GetFileName(f)); } catch { }
                }
            }
            LoadStampList();
        }

        // ---- フォルダ / zip から新しいグループを作る ----
        void AddGroupFromFolder()
        {
            using (FolderBrowserDialog fd = new FolderBrowserDialog())
            {
                fd.Description = "スタンプにする画像(PNG / JPEG)が入っているフォルダを選んでください。サブフォルダの画像も取り込みます。";
                fd.ShowNewFolderButton = false;
                if (fd.ShowDialog(this) != DialogResult.OK) return;
                ImportAsGroup(fd.SelectedPath, false);
            }
        }

        void AddGroupFromZip()
        {
            using (OpenFileDialog od = new OpenFileDialog())
            {
                od.Filter = "zip ファイル (*.zip)|*.zip";
                if (od.ShowDialog(this) != DialogResult.OK) return;
                ImportAsGroup(od.FileName, true);
            }
        }

        void ImportAsGroup(string src, bool zip)
        {
            string def = zip ? Path.GetFileNameWithoutExtension(src) : Path.GetFileName(src.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(def)) def = "新しいグループ";
            string n = TextInputDialog.Ask(this, def);
            if (string.IsNullOrEmpty(n) || n.Trim() == "") return;
            string g = StampStore.CreateGroup(n.Replace("\n", " "));
            int count = 0, skipped = 0;
            UseWaitCursor = true; Cursor.Current = Cursors.WaitCursor;
            try
            {
                count = zip ? StampStore.ImportZip(g, src, out skipped) : StampStore.ImportFolder(g, src, out skipped);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "取り込めませんでした: " + ex.Message, "SimpleCap");
            }
            finally { UseWaitCursor = false; Cursor.Current = Cursors.Default; }
            if (count == 0)
            {
                // 1 枚も無ければ空のグループは作らない
                try { Directory.Delete(StampStore.Dir(g), true); } catch { }
                ReloadGroups(null);
                MessageBox.Show(this, "PNG / JPEG 画像が見つかりませんでした。", "SimpleCap");
                return;
            }
            ReloadGroups(g);
            lblStatus.Text = string.Format("✅ {0} 個のスタンプを取り込みました{1}", count, skipped > 0 ? string.Format("(対象外 {0} 個はスキップ)", skipped) : "");
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            string g = lstGroups.SelectedItem as string;
            if (g != null && Directory.Exists(StampStore.Dir(g))) StampStore.SetSource(g, txtUrl.Text.Trim());
            base.OnFormClosing(e);
        }

        // ---- ダウンロード ----
        void StartDownload()
        {
            string g = lstGroups.SelectedItem as string;
            string url = txtUrl.Text.Trim();
            if (g == null) { MessageBox.Show(this, "先にグループを選ぶか追加してください。", "SimpleCap"); return; }
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            { MessageBox.Show(this, "http:// または https:// で始まる URL を入力してください。", "SimpleCap"); return; }
            StampStore.SetSource(g, url);
            lblStatus.Text = "⏳ ページを読み込んでいます…";
            UseWaitCursor = true;
            Enabled = false;
            Thread th = new Thread(delegate ()
            {
                List<KeyValuePair<string, byte[]>> got = new List<KeyValuePair<string, byte[]>>();
                string error = null;
                try
                {
                    List<string> urls = StampWeb.FindImageUrls(url);
                    int i = 0;
                    foreach (string u in urls)
                    {
                        i++;
                        string msg = string.Format("⏳ 画像をダウンロード中… {0}/{1}", i, urls.Count);
                        try { BeginInvoke((MethodInvoker)delegate { lblStatus.Text = msg; }); } catch { }
                        try { got.Add(new KeyValuePair<string, byte[]>(u, StampWeb.Download(u))); } catch { }
                    }
                }
                catch (Exception ex) { error = ex.Message; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        Enabled = true; UseWaitCursor = false;
                        if (error != null) { lblStatus.Text = ""; MessageBox.Show(this, "ページを読み込めませんでした: " + error, "SimpleCap"); return; }
                        if (got.Count == 0) { lblStatus.Text = ""; MessageBox.Show(this, "このページから PNG / JPEG 画像が見つかりませんでした。", "SimpleCap"); return; }
                        int n = DownloadSelectDialog.Choose(this, got, g);
                        lblStatus.Text = n > 0 ? "✔ " + n + " 個のスタンプを登録しました" : "";
                        LoadStampList();
                    });
                }
                catch { }
            });
            th.IsBackground = true;
            th.Start();
        }
    }

    // ================= ダウンロードした画像から登録するものを選ぶ =================
    public static class DownloadSelectDialog
    {
        public static int Choose(IWin32Window owner, List<KeyValuePair<string, byte[]>> items, string group)
        {
            using (Form f = new Form())
            {
                f.Text = "登録する画像を選んでください";
                f.AutoScaleMode = AutoScaleMode.None;
                f.Font = Program.UiFont;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ShowInTaskbar = false; f.MinimizeBox = false;
                f.ClientSize = new Size(Program.D(640), Program.D(460));
                ImageList il = new ImageList(); il.ColorDepth = ColorDepth.Depth32Bit; il.ImageSize = new Size(Program.D(80), Program.D(80));
                ListView lv = new ListView();
                lv.View = View.LargeIcon; lv.LargeImageList = il; lv.CheckBoxes = true;
                lv.SetBounds(Program.D(10), Program.D(34), f.ClientSize.Width - Program.D(20), f.ClientSize.Height - Program.D(84));
                lv.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
                Label l = new Label();
                l.Text = "☑ を付けた画像がスタンプとして登録されます(小さすぎる画像は最初から外しています)。";
                l.SetBounds(Program.D(10), Program.D(8), f.ClientSize.Width - Program.D(20), Program.D(22));
                f.Controls.Add(l); f.Controls.Add(lv);
                List<byte[]> data = new List<byte[]>();
                List<string> dnames = new List<string>();
                foreach (KeyValuePair<string, byte[]> kv in items)
                {
                    Bitmap b;
                    try { b = Doc.BytesToBitmap(kv.Value); } catch { continue; }
                    il.Images.Add(StampStore.Fit(b, il.ImageSize.Width));
                    ListViewItem it = new ListViewItem(string.Format("{0}\n{1}×{2}", StampWeb.FileNameOf(kv.Key), b.Width, b.Height), il.Images.Count - 1);
                    it.Checked = b.Width >= 48 && b.Height >= 48;
                    it.Tag = data.Count;
                    data.Add(kv.Value);
                    dnames.Add(StampWeb.FileNameOf(kv.Key));
                    lv.Items.Add(it);
                    b.Dispose();
                }
                Button all = new Button(); all.Text = "すべて選択";
                Button none = new Button(); none.Text = "すべて外す";
                Button ok = new Button(); ok.Text = "✔ 登録"; ok.DialogResult = DialogResult.OK;
                Button cancel = new Button(); cancel.Text = "キャンセル"; cancel.DialogResult = DialogResult.Cancel;
                int by = f.ClientSize.Height - Program.D(42);
                all.SetBounds(Program.D(10), by, Program.D(96), Program.D(30));
                none.SetBounds(Program.D(110), by, Program.D(96), Program.D(30));
                ok.SetBounds(f.ClientSize.Width - Program.D(196), by, Program.D(90), Program.D(30));
                cancel.SetBounds(f.ClientSize.Width - Program.D(100), by, Program.D(90), Program.D(30));
                all.Anchor = none.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
                ok.Anchor = cancel.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
                all.Click += delegate { foreach (ListViewItem it in lv.Items) it.Checked = true; };
                none.Click += delegate { foreach (ListViewItem it in lv.Items) it.Checked = false; };
                f.Controls.Add(all); f.Controls.Add(none); f.Controls.Add(ok); f.Controls.Add(cancel);
                f.AcceptButton = ok; f.CancelButton = cancel;
                int count = 0;
                if (f.ShowDialog(owner) == DialogResult.OK)
                {
                    foreach (ListViewItem it in lv.Items)
                    {
                        if (!it.Checked) continue;
                        int di = (int)it.Tag;
                        try { StampStore.AddImage(group, data[di], dnames[di]); count++; } catch { }
                    }
                }
                il.Dispose();
                return count;
            }
        }
    }
}
