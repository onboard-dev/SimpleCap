using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SimpleCap
{
    // ブラウズ画面(一覧トレイ + 編集)
    public class BrowseWindow : Form
    {
        Canvas canvas;
        CanvasHost host;
        ListView tray;
        ImageList thumbs;
        CheckedListBox layers;
        ColorRow btnStroke, btnFill;
        NumericUpDown numFillAlpha, numWidth, numFont;
        ComboBox cmbDash;
        StampPanel stampPanel;
        ToolStripButton stampBtn;
        Action layoutLeftAction;
        bool stampOpen;   // Visible は親が非表示の間 false を返すので自前で持つ
        ToolStripComboBox cmbZoom;
        ToolStripLabel lblSize;
        CheckBox chkBoth;
        ToolStripStatusLabel status;
        Dictionary<Tool, ToolStripButton> toolButtons = new Dictionary<Tool, ToolStripButton>();
        bool syncing;
        Doc current;
        VideoView videoView;          // ビデオを開いているときはキャンバスの上に重ねて表示
        MediaView mediaView;          // 書き出した GIF(アニメーション再生)/ MP4(静止画)を表示
        class ThumbAnim { public List<Bitmap> Frames = new List<Bitmap>(); public List<int> Delays = new List<int>(); public int Idx, Acc; }
        Dictionary<string, ThumbAnim> anims = new Dictionary<string, ThumbAnim>();
        System.Windows.Forms.Timer animTimer;

        public BrowseWindow()
        {
            Text = (Program.VersionText == "" ? "SimpleCap" : "SimpleCap " + Program.VersionText) + " - ブラウズ";
            AutoScaleMode = AutoScaleMode.None;
            Font = Program.UiFont;
            StartPosition = FormStartPosition.CenterScreen;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(wa.Width - 40, Program.D(1200)), Math.Min(wa.Height - 40, Program.D(800)));
            Place.Restore(Program.LoadSettings(), "browser", this, true);   // 前回終了したディスプレイ・大きさで出す
            KeyPreview = true;
            AllowDrop = true;
            if (Program.AppIcon != null) Icon = Program.AppIcon;

            // --- 中央キャンバス ---
            canvas = new Canvas();
            host = new CanvasHost(canvas);
            host.Dock = DockStyle.Fill;
            canvas.DocChanged += delegate { if (videoAnno) AnnoDocChanged(); RefreshLayers(); SyncProps(); UpdateStatus(); };
            canvas.SelectionChanged += delegate { SyncProps(); RefreshLayers(); if (videoAnno) videoView.RefreshAnnoUi(); };
            canvas.ZoomChanged += delegate { UpdateStatus(); };
            canvas.RegionMenu += delegate { ShowRegionMenu(); };
            // 右クリック:描いた注釈の選択を外して、選択ツール(「選択」ボタンを押したのと同じ)へ
            canvas.RightCancel += delegate { canvas.SetSelected(null); SelectTool(Tool.Select); };
            Controls.Add(host);

            // --- ビデオ編集(キャンバスと同じ場所に重ねる) ---
            videoView = new VideoView();
            videoView.Visible = false;
            Controls.Add(videoView);
            videoView.BringToFront();
            mediaView = new MediaView();
            mediaView.Visible = false;
            Controls.Add(mediaView);
            mediaView.BringToFront();
            EventHandler syncVideo = delegate
            {
                if (host.Parent != this) return;   // 注釈編集中はキャンバスがビデオ画面の中にある
                if (videoView.Visible) videoView.Bounds = host.Bounds;
                if (mediaView.Visible) mediaView.Bounds = host.Bounds;
            };
            host.SizeChanged += syncVideo;
            host.LocationChanged += syncVideo;
            videoView.Changed += delegate { UpdateStatus(); };
            videoView.Message += delegate (string s) { status.Text = s; };
            videoView.FileCreated += delegate (string p) { AddLibraryFile(p); };
            videoView.MergeClicked += delegate { MergeSelected(); };
            videoView.Zoomer.ZoomChanged += delegate { UpdateZoomBox(); };
            mediaView.Zoomer.ZoomChanged += delegate { UpdateZoomBox(); };
            videoView.AnnotateToggled += delegate { if (videoAnno) ExitVideoAnnotate(); else EnterVideoAnnotate(); };
            videoView.FrameShown += delegate (int f) { if (videoAnno) SetAnnoFrame(); };
            videoView.SelectedAnno = delegate { return videoAnno ? canvas.Selected : null; };
            videoView.AnnoTimeChanged += delegate { if (videoAnno) { annoDoc.Dirty = true; UpdateTimeHidden(); canvas.Invalidate(); } };
            animTimer = new System.Windows.Forms.Timer();
            animTimer.Interval = 50;
            animTimer.Tick += delegate { AnimTick(); };

            // 編集画面と右パネルの間の仕切り(左右にドラッグして幅を変更)
            Splitter vs = new Splitter();
            vs.Dock = DockStyle.Right;
            vs.Width = Program.D(6);
            vs.MinSize = Program.D(190);
            vs.MinExtra = Program.D(200);
            vs.BackColor = Color.FromArgb(225, 228, 232);
            Controls.Add(vs);

            // --- 右パネル(プロパティ + レイヤ) ---
            Controls.Add(BuildRightPanel());

            // --- 左ツールバー + スタンプウィンドウ (ツールバーのすぐ右) ---
            stampPanel = new StampPanel();
            stampPanel.Dock = DockStyle.None;   // 位置は下の leftWrap 内で明示的に決める
            stampOpen = Program.GetInt(Program.LoadSettings(), "stampopen", 0) == 1;
            stampPanel.Visible = stampOpen;
            stampPanel.StampClicked += delegate (string path)
            {
                if (current == null) { status.Text = "先にキャプチャを開いてください"; return; }
                try
                {
                    Bitmap b = Doc.BytesToBitmap(File.ReadAllBytes(path));
                    SelectTool(Tool.Select);
                    // 元の大きさで貼る。ただし画像の短い辺の半分を超えるときは縮める(貼ったあとハンドルで調整)
                    int natural = Math.Max(b.Width, b.Height);
                    int limit = Math.Max(32, Math.Min(current.Base.Width, current.Base.Height) / 2);
                    canvas.AddStamp(b, Math.Min(natural, limit));
                    status.Text = "🌟 スタンプを貼り付けました(新しいレイヤ)";
                }
                catch (Exception ex) { MessageBox.Show(this, "スタンプを読み込めませんでした: " + ex.Message, "SimpleCap"); }
            };
            // ツールバーとスタンプを 1 つの左パネルにまとめ、[ツールバー][スタンプ] の順に並べる
            // (Dock の重なり順に頼らず、スタンプは必ずツールバーの右に置く)
            Panel leftWrap = new Panel();
            leftWrap.Dock = DockStyle.Left;
            ToolStrip toolBar = BuildToolBar();
            leftWrap.Controls.Add(toolBar);       // toolBar は leftWrap 内で Dock=Left
            leftWrap.Controls.Add(stampPanel);
            // スタンプウィンドウ右端のハンドル(ドラッグで幅を変える)
            Panel grip = new Panel();
            grip.Cursor = Cursors.SizeWE;
            grip.BackColor = Color.FromArgb(225, 228, 232);
            grip.Visible = stampOpen;
            leftWrap.Controls.Add(grip);
            int gripW = Program.D(6);
            int stampW = Math.Max(Program.D(150), Math.Min(Program.D(900), Program.GetInt(Program.LoadSettings(), "stampw", stampPanel.Width)));
            Action layoutLeft = delegate
            {
                int tw = toolBar.Width;
                int w = tw + (stampOpen ? stampW + gripW : 0);
                if (leftWrap.Width != w) leftWrap.Width = w;
                stampPanel.SetBounds(tw, 0, stampW, leftWrap.ClientSize.Height);
                grip.SetBounds(tw + stampW, 0, gripW, leftWrap.ClientSize.Height);
                grip.Visible = stampOpen;
            };
            bool gripDrag = false;
            grip.MouseDown += delegate (object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { gripDrag = true; grip.Capture = true; } };
            grip.MouseMove += delegate (object s, MouseEventArgs e)
            {
                if (!gripDrag) return;
                int x = leftWrap.PointToClient(grip.PointToScreen(e.Location)).X - toolBar.Width;
                int nw = Math.Max(Program.D(150), Math.Min(Program.D(900), Math.Min(ClientSize.Width - toolBar.Width - Program.D(400), x)));
                if (nw != stampW) { stampW = nw; layoutLeft(); }
            };
            grip.MouseUp += delegate
            {
                if (!gripDrag) return;
                gripDrag = false; grip.Capture = false;
                Dictionary<string, string> st = Program.LoadSettings();
                st["stampw"] = stampW.ToString();
                Program.SaveSettings(st);
            };
            leftWrap.Resize += delegate { layoutLeft(); };
            toolBar.SizeChanged += delegate { layoutLeft(); };
            layoutLeftAction = layoutLeft;
            Controls.Add(leftWrap);
            layoutLeft();

            // --- 下トレイ ---
            tray = new ListView();
            tray.Dock = DockStyle.None;   // 下の trayWrap の中に置く(上端のハンドルで高さを変える)
            tray.View = View.LargeIcon;
            tray.MultiSelect = true;    // Ctrl / Shift で複数選択
            tray.HideSelection = false;
            tray.BackColor = Color.FromArgb(245, 246, 248);
            thumbs = new ImageList();
            thumbs.ColorDepth = ColorDepth.Depth32Bit;
            thumbs.ImageSize = new Size(Program.D(112), Program.D(72));
            tray.LargeImageList = thumbs;
            tray.SelectedIndexChanged += delegate
            {
                UpdateMergeButton();
                // 1つだけ選んだときに開く(複数選択中は開かない)
                if (syncing || tray.SelectedItems.Count != 1) return;
                OpenCapture((string)tray.SelectedItems[0].Tag);
            };
            tray.ItemDrag += delegate (object s, ItemDragEventArgs e)
            {
                ListViewItem it = e.Item as ListViewItem;
                if (it == null) return;
                SaveCurrent();
                DataObject d = new DataObject();
                StringCollection sc = new StringCollection();
                if (it.Selected) foreach (ListViewItem s2 in tray.SelectedItems) sc.Add((string)s2.Tag);
                else sc.Add((string)it.Tag);
                d.SetFileDropList(sc);
                tray.DoDragDrop(d, DragDropEffects.Copy);
            };
            tray.ContextMenuStrip = BuildTrayMenu();
            // 一覧(サムネイル)の上端にハンドルを付けて高さを変えられるようにする
            Panel trayWrap = new Panel();
            trayWrap.Dock = DockStyle.Bottom;
            int gripH = Program.D(6);
            int minTrayH = Program.D(70), saveTrayH = Program.GetInt(Program.LoadSettings(), "trayh", Program.D(132));
            trayWrap.Height = Math.Max(minTrayH, saveTrayH) + gripH;
            Panel tgrip = new Panel();
            tgrip.Cursor = Cursors.SizeNS;
            tgrip.BackColor = Color.FromArgb(225, 228, 232);
            trayWrap.Controls.Add(tgrip);
            trayWrap.Controls.Add(tray);
            trayWrap.Resize += delegate
            {
                tgrip.SetBounds(0, 0, trayWrap.ClientSize.Width, gripH);
                tray.SetBounds(0, gripH, trayWrap.ClientSize.Width, Math.Max(0, trayWrap.ClientSize.Height - gripH));
            };
            bool tdrag = false; int tdragY = 0, tdragH = 0;
            tgrip.MouseDown += delegate (object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                tdrag = true; tgrip.Capture = true;
                tdragY = Cursor.Position.Y; tdragH = trayWrap.Height;
            };
            tgrip.MouseMove += delegate
            {
                if (!tdrag) return;
                int h = tdragH + (tdragY - Cursor.Position.Y);   // 上へドラッグで高く
                int maxH = Math.Max(minTrayH + gripH, ClientSize.Height - Program.D(260));
                h = Math.Max(minTrayH + gripH, Math.Min(maxH, h));
                if (h != trayWrap.Height) trayWrap.Height = h;
            };
            tgrip.MouseUp += delegate
            {
                if (!tdrag) return;
                tdrag = false; tgrip.Capture = false;
                Dictionary<string, string> st = Program.LoadSettings();
                st["trayh"] = (trayWrap.Height - gripH).ToString();
                Program.SaveSettings(st);
            };
            Controls.Add(trayWrap);

            // --- ステータス ---
            StatusStrip ss = new StatusStrip();
            status = new ToolStripStatusLabel("");
            status.Spring = true; status.TextAlign = ContentAlignment.MiddleLeft;
            ss.Items.Add(status);
            Controls.Add(ss);

            // --- 上ツールバー(最後に追加=最も外側) ---
            Controls.Add(BuildMainBar());

            SelectTool(Tool.Select);
            LoadLibrary();
            UpdateStatus();
        }

        // ================= UI 構築 =================
        ToolStripButton Btn(string text, string tipText, EventHandler click)
        {
            ToolStripButton b = new ToolStripButton(text);
            b.ToolTipText = tipText;
            b.DisplayStyle = ToolStripItemDisplayStyle.Text;
            b.Click += click;
            return b;
        }

        ToolStrip BuildMainBar()
        {
            ToolStrip ts = new ToolStrip();
            ts.GripStyle = ToolStripGripStyle.Hidden;
            ts.Font = new Font("Yu Gothic UI", 10f);
            ts.Padding = new Padding(Program.D(4), Program.D(2), 0, Program.D(2));
            ts.Items.Add(Btn("📷 キャプチャ", "キャプチャする(キャプチャウィンドウ表示中はそちらに切り替え)", delegate
            {
                if (Program.CapWin.OptTray) Program.CapWin.StartCapture(false);   // トレイ常駐モードはそのまま撮影
                else Program.CapWin.ShowCaptureOnly();
            }));
            ts.Items.Add(Btn("🎥 ビデオ", "画面をビデオで録画(範囲を選んで開始)", delegate { Program.CapWin.ToggleVideo(); }));
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(Btn("💾 保存", "保存 (Ctrl+S)", delegate { SaveCurrent(true); }));
            ts.Items.Add(Btn("📤 書き出し", "名前を付けて PNG / JPG / BMP に書き出し", delegate { ExportAs(); }));
            ts.Items.Add(Btn("📋 コピー", "合成した画像をクリップボードへ (Ctrl+C)", delegate { CopyToClipboard(); }));
            ts.Items.Add(Btn("📥 貼り付け", "クリップボードの画像をレイヤとして貼り付け (Ctrl+V)", delegate { Paste(); }));
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(Btn("↩️ 元に戻す", "元に戻す (Ctrl+Z)", delegate { canvas.Undo(); }));
            ts.Items.Add(Btn("↪️ やり直し", "やり直し (Ctrl+Y)", delegate { canvas.Redo(); }));
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(Btn("＋", "拡大 (Ctrl+ホイール)", delegate { SetViewZoom(ViewZoom * 1.25f); }));
            ts.Items.Add(Btn("－", "縮小 (Ctrl+ホイール)", delegate { SetViewZoom(ViewZoom / 1.25f); }));
            ts.Items.Add(Btn("1:1", "等倍表示", delegate { SetViewZoom(1f); }));
            ts.Items.Add(Btn("🔲 全体", "ウィンドウに合わせる(小さいものは拡大、大きいものは縮小)", delegate { ZoomFitAll(); }));
            // 倍率を数値で指定 (一覧から選ぶか、数字を入力して Enter)
            cmbZoom = new ToolStripComboBox();
            cmbZoom.AutoSize = false;
            cmbZoom.Width = Program.D(72);
            cmbZoom.ToolTipText = "表示倍率(%)。一覧から選ぶか、数字を入力して Enter";
            foreach (string z in new string[] { "25%", "50%", "75%", "100%", "150%", "200%", "300%", "400%", "800%" }) cmbZoom.Items.Add(z);
            cmbZoom.SelectedIndexChanged += delegate { if (!syncing && cmbZoom.SelectedIndex >= 0) ApplyZoomText(cmbZoom.Items[cmbZoom.SelectedIndex].ToString()); };
            cmbZoom.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ApplyZoomText(cmbZoom.Text); canvas.Focus(); }
            };
            cmbZoom.Leave += delegate { UpdateZoomBox(); };
            ts.Items.Add(cmbZoom);
            ts.Items.Add(new ToolStripSeparator());
            // 背景(画像)の縦横ピクセル数
            lblSize = new ToolStripLabel("");
            lblSize.ToolTipText = "背景画像の大きさ(幅 × 高さ)";
            ts.Items.Add(lblSize);
            ts.Items.Add(new ToolStripSeparator());
            ts.Items.Add(Btn("📂 フォルダ", "保存フォルダを開く", delegate { Process.Start("explorer.exe", "\"" + Program.LibDir + "\""); }));
            ToolStripButton setBtn = Btn("⚙️ 設定", "ショートカットキーなどの設定", delegate { SettingsWindow.Open(this); });
            setBtn.Alignment = ToolStripItemAlignment.Right;
            ts.Items.Add(setBtn);
            return ts;
        }

        ToolStrip BuildToolBar()
        {
            ToolStrip ts = new ToolStrip();
            ts.Dock = DockStyle.Left;
            ts.LayoutStyle = ToolStripLayoutStyle.VerticalStackWithOverflow;
            ts.GripStyle = ToolStripGripStyle.Hidden;
            ts.Font = new Font("Yu Gothic UI", 10f);
            ts.Padding = new Padding(Program.D(2), Program.D(6), Program.D(2), 0);
            AddTool(ts, Tool.Select, "選択", "選択・移動 (V)\nダブルクリックで文字を編集");
            // 選択ボタンのアイコンは点線の四角形(絵で描く)
            ToolStripButton selBtn = toolButtons[Tool.Select];
            selBtn.Image = DashedSquareIcon(Program.D(16));
            selBtn.ImageScaling = ToolStripItemImageScaling.None;
            selBtn.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            selBtn.ImageAlign = ContentAlignment.MiddleLeft;
            selBtn.TextImageRelation = TextImageRelation.ImageBeforeText;
            ts.Items.Add(new ToolStripSeparator());
            AddTool(ts, Tool.Arrow, "➡️ 矢印", "矢印 (A)  Shiftで45°");
            AddTool(ts, Tool.Line, "➖ 線", "直線 (L)  Shiftで45°");
            AddTool(ts, Tool.Rect, "⬜ 四角", "四角形 (R)  Shiftで正方形");
            AddTool(ts, Tool.Ellipse, "⭕ 楕円", "楕円 (E)  Shiftで正円");
            AddTool(ts, Tool.Text, "🔤 文字", "テキスト (T)  クリックした位置に入力");
            AddTool(ts, Tool.BalloonPointer, "💬 吹出し", "引き出し線つき吹き出し (B)\n矢印の先のハンドルで指し先を移動");
            AddTool(ts, Tool.BalloonArrow, "👉 矢印型", "矢印型の吹き出し (W)\n右→左にドラッグすると左向き");
            AddTool(ts, Tool.Number, "🔢 番号", "番号スタンプ (N)\nクリックするたびに 1, 2, 3 … と増えます");
            AddTool(ts, Tool.NumberPin, "📍 ピン", "ピン型の番号スタンプ (P)\nドラッグした方向に先端が向きます");
            AddTool(ts, Tool.Highlight, "🖍️ 蛍光", "蛍光ペン (H)");
            AddTool(ts, Tool.Mosaic, "▦ ぼかし", "モザイク (M)  個人情報を隠す\n太さでブロックの大きさを変更");
            ts.Items.Add(new ToolStripSeparator());
            AddTool(ts, Tool.Crop, "✂️ 切抜き", "トリミング (C)  ドラッグした範囲を残す");
            AddTool(ts, Tool.CutV, "↕️ 縦カット", "縦カット:上下にドラッグした範囲を切り取って、上下を詰める\n(横の点線がマウスの位置に出ます)");
            AddTool(ts, Tool.CutH, "↔️ 横カット", "横カット:左右にドラッグした範囲を切り取って、左右を詰める\n(縦の点線がマウスの位置に出ます)");
            AddTool(ts, Tool.Fill, "🪣 塗り", "塗りつぶし (F)  クリックした所と似た色の範囲を塗る\n色=塗りの色(透明なら線の色)、許容範囲で曖昧な色もまとめて塗る");
            AddTool(ts, Tool.ClearColor, "🪄 透明化", "クリックした色を透明にする (背景画像)\n許容範囲で近い色もまとめて透明に");
            ts.Items.Add(new ToolStripSeparator());
            // スタンプウィンドウの表示/非表示
            stampBtn = new ToolStripButton("🌟 スタンプ");
            stampBtn.DisplayStyle = ToolStripItemDisplayStyle.Text;
            stampBtn.TextAlign = ContentAlignment.MiddleLeft;
            stampBtn.ToolTipText = "スタンプウィンドウを開く/閉じる\nスタンプをクリックすると新しいレイヤとして貼り付け";
            stampBtn.Padding = new Padding(Program.D(2), Program.D(3), Program.D(2), Program.D(3));
            stampBtn.Checked = stampOpen;
            stampBtn.Click += delegate
            {
                stampOpen = !stampOpen;
                stampPanel.Visible = stampOpen;
                stampBtn.Checked = stampOpen;
                if (layoutLeftAction != null) layoutLeftAction();
                Dictionary<string, string> st = Program.LoadSettings();
                st["stampopen"] = stampOpen ? "1" : "0";
                Program.SaveSettings(st);
            };
            ts.Items.Add(stampBtn);
            // AI で名前付け(スタンプの下)
            ToolStripButton nameBtn = new ToolStripButton("🏷️ 名前付け");
            nameBtn.DisplayStyle = ToolStripItemDisplayStyle.Text;
            nameBtn.TextAlign = ContentAlignment.MiddleLeft;
            nameBtn.ToolTipText = "AI で画像・ビデオ・GIF・MP4 に名前を付けてファイル名にする\n(下の一覧で複数選ぶとまとめて。AI の設定は ⚙️ 設定)";
            nameBtn.Padding = new Padding(Program.D(2), Program.D(3), Program.D(2), Program.D(3));
            nameBtn.Click += delegate { NameWithAi(); };
            ts.Items.Add(nameBtn);
            return ts;
        }

        // 点線の四角形のアイコン(選択ツール用)
        static Bitmap DashedSquareIcon(int s)
        {
            Bitmap b = new Bitmap(s, s);
            using (Graphics g = Graphics.FromImage(b))
            using (Pen p = new Pen(Color.FromArgb(40, 40, 40), Math.Max(1f, s / 12f)))
            {
                g.Clear(Color.Transparent);
                p.DashPattern = new float[] { 2f, 1.5f };
                int m = Math.Max(1, s / 8);
                g.DrawRectangle(p, m, m, s - m * 2 - 1, s - m * 2 - 1);
            }
            return b;
        }

        void AddTool(ToolStrip ts, Tool t, string text, string tipText)
        {
            ToolStripButton b = new ToolStripButton(text);
            b.DisplayStyle = ToolStripItemDisplayStyle.Text;
            b.TextAlign = ContentAlignment.MiddleLeft;
            b.ToolTipText = tipText;
            b.Padding = new Padding(Program.D(2), Program.D(3), Program.D(2), Program.D(3));
            b.Click += delegate { SelectTool(t); };
            toolButtons[t] = b;
            ts.Items.Add(b);
        }

        void SelectTool(Tool t)
        {
            if (videoAnno && (t == Tool.Crop || t == Tool.Fill || t == Tool.ClearColor || t == Tool.CutV || t == Tool.CutH))
            {
                status.Text = "✏️ ビデオの注釈では、切抜き・縦カット・横カット・塗り・透明化は使えません";
                return;
            }
            canvas.Tool = t;
            foreach (KeyValuePair<Tool, ToolStripButton> kv in toolButtons) kv.Value.Checked = kv.Key == t;
            if (t != Tool.Select) { canvas.SetSelected(null); canvas.SetBgSelected(false); canvas.ClearRegionSelection(); }
            canvas.LoadToolStyle(t);     // そのツールで前回使ったサイズを既定にする
            if (numFont != null) SyncProps();
            UpdateStatus();
        }

        Panel rightPanel, layerPanel;

        Panel BuildRightPanel()
        {
            Dictionary<string, string> st0 = Program.LoadSettings();
            int W = Math.Max(Program.D(190), Program.GetInt(st0, "panelw", Program.D(230)));
            // 外枠 (幅は左の仕切りでドラッグして変更)
            Panel outer = new Panel();
            outer.Dock = DockStyle.Right;
            outer.Width = W;
            outer.BackColor = Color.FromArgb(250, 250, 251);
            rightPanel = outer;

            // プロパティ部分 (スクロールできる)
            Panel p = new Panel();
            p.Dock = DockStyle.Fill;
            p.AutoScroll = true;
            p.BackColor = outer.BackColor;

            int y = Program.D(8), x = Program.D(8), cw = W - Program.D(16) - SystemInformation.VerticalScrollBarWidth;
            int rowH = Program.D(28);

            Label head = new Label();
            head.Text = "🎨 プロパティ"; head.Font = new Font("Yu Gothic UI", 10f, FontStyle.Bold);
            head.SetBounds(x, y, cw, Program.D(22)); p.Controls.Add(head); y += Program.D(26);

            // 色の指定: 最近よく使う3色 / スポイト / ラベル(クリックで色見本)
            btnStroke = new ColorRow("線/文字の色", "stroke", false);
            btnStroke.SetBounds(x, y, cw, Program.D(32));
            p.Controls.Add(btnStroke); y += Program.D(36);
            btnStroke.Picked += delegate (Color c) { canvas.ApplyProps(Color.FromArgb(255, c), null, null, null); SyncProps(); };
            btnFill = new ColorRow("塗りの色", "fill", true);
            btnFill.SetBounds(x, y, cw, Program.D(32));
            p.Controls.Add(btnFill); y += Program.D(36);
            btnFill.Picked += delegate (Color c)
            {
                if (c.A == 0) canvas.ApplyProps(null, Color.Transparent, null, null);
                else
                {
                    int a = (int)numFillAlpha.Value == 0 ? 100 : (int)numFillAlpha.Value;
                    canvas.ApplyProps(null, Color.FromArgb(a * 255 / 100, c), null, null);
                }
                SyncProps();
            };

            numFillAlpha = Numeric(p, "塗り 不透明度 %  (0=透明)", x, ref y, cw, 0, 100, 0);
            numFillAlpha.ValueChanged += delegate
            {
                if (syncing) return;
                Color f = canvas.Selected != null ? canvas.Selected.Fill : canvas.CurFill;
                if (f.A == 0 && f.R == 0 && f.G == 0 && f.B == 0) f = Color.White;
                canvas.ApplyProps(null, Color.FromArgb((int)numFillAlpha.Value * 255 / 100, f), null, null);
                SyncProps();
            };
            numWidth = Numeric(p, "線の太さ / モザイクの粗さ", x, ref y, cw, 0, 40, 4);
            numWidth.ValueChanged += delegate { if (!syncing) canvas.ApplyProps(null, null, (float)numWidth.Value, null); };
            cmbDash = DashCombo(p, x, ref y, cw);
            cmbDash.SelectedIndexChanged += delegate { if (!syncing && cmbDash.SelectedIndex >= 0) canvas.ApplyDash(cmbDash.SelectedIndex); };
            chkBoth = new CheckBox();
            chkBoth.Text = "↔️ 始点にも矢印";
            chkBoth.SetBounds(x, y - Program.D(4), cw, Program.D(24));
            chkBoth.CheckedChanged += delegate { if (!syncing) canvas.ApplyBothEnds(chkBoth.Checked); };
            new ToolTip().SetToolTip(chkBoth, "矢印の両端に矢じりを付けます(矢印ツール・矢印を選択中に有効)");
            p.Controls.Add(chkBoth); y += Program.D(24);
            numFont = Numeric(p, "文字サイズ / 番号の大きさ", x, ref y, cw, 8, 200, 28);
            numFont.ValueChanged += delegate { if (!syncing) canvas.ApplyProps(null, null, null, (float)numFont.Value); };
            NumericUpDown numTol = Numeric(p, "🪣塗り・🪄透明化の許容範囲 %", x, ref y, cw, 0, 100, 15);
            numTol.ValueChanged += delegate { canvas.Tolerance = (int)numTol.Value; };
            new ToolTip().SetToolTip(numTol, "0 = 同じ色だけ。大きくするほど、似た色(グラデーションやにじみ)もまとめて処理します");

            // 幅を変えたら各項目も伸び縮みするように
            foreach (Control c in p.Controls) c.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Label spacer = new Label(); spacer.SetBounds(x, y, 1, Program.D(8)); p.Controls.Add(spacer);

            // レイヤ部分 (スクロールしない、下に固定。上の仕切りで高さを変更)
            Panel lp = new Panel();
            lp.Dock = DockStyle.Bottom;
            lp.Height = Math.Max(Program.D(120), Program.GetInt(st0, "layersh", Program.D(230)));
            lp.BackColor = outer.BackColor;
            layerPanel = lp;
            Label lh = new Label();
            lh.Text = "📚 レイヤ (上が手前)"; lh.Font = head.Font;
            lp.Controls.Add(lh);

            // 見出しの右に並べる小さなボタン (⬆ ⬇ 🗑 統合)
            ToolTip tt = new ToolTip();
            Button up = SmallBtn("⬆", "前面へ"); up.Click += delegate { canvas.MoveLayer(1); }; tt.SetToolTip(up, "前面へ");
            Button dn = SmallBtn("⬇", "背面へ"); dn.Click += delegate { canvas.MoveLayer(-1); }; tt.SetToolTip(dn, "背面へ");
            Button del = SmallBtn("🗑", "削除"); del.Click += delegate { canvas.DeleteSelected(); }; tt.SetToolTip(del, "レイヤを削除 (Delete)");
            Button flat = SmallBtn("🧩統合", "統合"); flat.Click += delegate { canvas.MergeAllLayers(); };
            flat.Font = new Font("Yu Gothic UI", 8f);
            tt.SetToolTip(flat, "レイヤ統合:表示中のレイヤをすべて背景画像レイヤに統合します(非表示レイヤは破棄)。Ctrl+Zで戻せます");
            Button[] hbtn = { up, dn, del, flat };

            layers = new CheckedListBox();
            layers.CheckOnClick = false;
            layers.IntegralHeight = false;
            layers.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom;
            layers.Font = new Font("Yu Gothic UI", 9.5f);
            layers.ItemCheck += Layers_ItemCheck;
            layers.SelectedIndexChanged += delegate
            {
                if (syncing || current == null) return;
                int i = layers.SelectedIndex;
                int n = current.Objs.Count;
                if (i >= 0 && i < n) { SelectTool(Tool.Select); canvas.SetSelected(current.Objs[n - 1 - i]); }
                else if (i == n) { SelectTool(Tool.Select); canvas.SetSelected(null); canvas.SetBgSelected(true); }
                else canvas.SetSelected(null);
            };

            lp.Controls.Add(layers);
            foreach (Button b in hbtn) lp.Controls.Add(b);
            lh.AutoEllipsis = true;
            EventHandler layout = delegate
            {
                int lw = lp.ClientSize.Width - Program.D(16);
                int bh = Program.D(26), top0 = Program.D(6);
                // ボタンを右詰めで並べ、残りを見出しに
                int bx = x + lw;
                for (int k = hbtn.Length - 1; k >= 0; k--)
                {
                    int bw = hbtn[k] == flat ? Program.D(50) : Program.D(30);
                    bx -= bw;
                    hbtn[k].SetBounds(bx, top0, bw, bh);
                    bx -= Program.D(3);
                }
                lh.SetBounds(x, top0 + Program.D(2), Math.Max(Program.D(20), bx - x - Program.D(2)), Program.D(22));
                int top = top0 + bh + Program.D(4);
                layers.SetBounds(x, top, lw, Math.Max(Program.D(40), lp.ClientSize.Height - Program.D(6) - top));
            };
            lp.Resize += layout;
            layout(lp, EventArgs.Empty);

            // プロパティとレイヤの間の仕切り(上下にドラッグ)
            Splitter hs = new Splitter();
            hs.Dock = DockStyle.Bottom;
            hs.Height = Program.D(6);
            hs.MinSize = Program.D(120);
            hs.MinExtra = Program.D(80);
            hs.BackColor = Color.FromArgb(225, 228, 232);

            outer.Controls.Add(p); outer.Controls.Add(hs); outer.Controls.Add(lp);
            return outer;
        }

        Button SmallBtn(string t, string name)
        {
            Button b = new Button();
            b.Text = t; b.Name = name;
            b.Size = new Size(Program.D(30), Program.D(26));
            b.Margin = Padding.Empty;
            b.Padding = Padding.Empty;
            return b;
        }

        Button ColorButton(string text, int x, int y, int w)
        {
            Button b = new Button();
            b.Text = text;
            b.TextAlign = ContentAlignment.MiddleRight;
            b.SetBounds(x, y, w, Program.D(28));
            b.Paint += delegate (object s, PaintEventArgs e)
            {
                Button bb = (Button)s;
                Color c = (Color)(bb.Tag ?? Color.Black);
                Rectangle sw = new Rectangle(Program.D(6), Program.D(5), Program.D(30), bb.Height - Program.D(10));
                using (HatchBrush hb = new HatchBrush(HatchStyle.LargeCheckerBoard, Color.LightGray, Color.White)) e.Graphics.FillRectangle(hb, sw);
                using (SolidBrush sb = new SolidBrush(c)) e.Graphics.FillRectangle(sb, sw);
                e.Graphics.DrawRectangle(Pens.Gray, sw);
                if (c.A == 0) e.Graphics.DrawLine(Pens.Red, sw.Left, sw.Bottom, sw.Right, sw.Top);
            };
            return b;
        }

        // 線の種類 (見本の線を描いた選択リスト)
        static readonly string[] DashNames = { "実線", "細かなドット", "短い破線", "長い破線" };
        ComboBox DashCombo(Panel p, int x, ref int y, int w)
        {
            Label l = new Label();
            l.Text = "線の種類"; l.SetBounds(x, y, w, Program.D(18));
            l.ForeColor = Color.FromArgb(80, 80, 80);
            p.Controls.Add(l); y += Program.D(19);
            ComboBox cb = new ComboBox();
            cb.DropDownStyle = ComboBoxStyle.DropDownList;
            cb.DrawMode = DrawMode.OwnerDrawFixed;
            cb.ItemHeight = Program.D(20);
            foreach (string n in DashNames) cb.Items.Add(n);
            cb.SelectedIndex = 0;
            cb.SetBounds(x, y, w, Program.D(26));
            cb.DrawItem += delegate (object s, DrawItemEventArgs e)
            {
                e.DrawBackground();
                if (e.Index < 0) return;
                Rectangle r = e.Bounds;
                int lineW = Program.D(70);
                bool sel = (e.State & DrawItemState.Selected) != 0;
                Color fg = sel ? SystemColors.HighlightText : Color.Black;
                using (Pen pen = new Pen(fg, Program.D(3)))
                {
                    Obj.ApplyDash(pen, e.Index);
                    int cy = r.Top + r.Height / 2;
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    e.Graphics.DrawLine(pen, r.Left + Program.D(6), cy, r.Left + Program.D(6) + lineW, cy);
                }
                TextRenderer.DrawText(e.Graphics, DashNames[e.Index], cb.Font,
                    new Rectangle(r.Left + lineW + Program.D(14), r.Top, r.Width - lineW - Program.D(14), r.Height), fg,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            };
            p.Controls.Add(cb); y += Program.D(32);
            return cb;
        }

        NumericUpDown Numeric(Panel p, string label, int x, ref int y, int w, int min, int max, int val)
        {
            Label l = new Label();
            l.Text = label; l.SetBounds(x, y, w, Program.D(18));
            l.ForeColor = Color.FromArgb(80, 80, 80);
            p.Controls.Add(l); y += Program.D(19);
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min; n.Maximum = max; n.Value = val;
            n.SetBounds(x, y, w, Program.D(24));
            p.Controls.Add(n); y += Program.D(30);
            return n;
        }

        ContextMenuStrip BuildTrayMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Items.Add("📋 コピー (Ctrl+C)", null, delegate { CopyTrayItem(); });
            m.Items.Add("🏷️ AI で名前付け", null, delegate { NameWithAi(); });
            m.Items.Add("📂 エクスプローラーで表示", null, delegate
            {
                if (tray.SelectedItems.Count == 0) return;
                Process.Start("explorer.exe", "/select,\"" + (string)tray.SelectedItems[0].Tag + "\"");
            });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("🗑️ 削除(ごみ箱へ) (Delete)", null, delegate { DeleteCapture(); });
            // ビデオのとき:書き出し
            ToolStripSeparator vsep = new ToolStripSeparator();
            ToolStripItem vmp4 = new ToolStripMenuItem("🎬 MP4 で書き出し…", null, delegate { ExportVideo("mp4"); });
            ToolStripItem vgif = new ToolStripMenuItem("🎞️ GIF で書き出し…", null, delegate { ExportVideo("gif"); });
            m.Items.Insert(0, vsep); m.Items.Insert(0, vgif); m.Items.Insert(0, vmp4);
            // 2 つ以上選んだとき:結合
            ToolStripSeparator msep = new ToolStripSeparator();
            ToolStripItem vmerge = new ToolStripMenuItem("🔗 結合(選んだビデオ / GIF / MP4 を 1 本に)", null, delegate { MergeSelected(); });
            m.Items.Insert(0, msep); m.Items.Insert(0, vmerge);
            m.Opening += delegate
            {
                bool v = tray.SelectedItems.Count == 1 && VideoInfo.IsVideo((string)tray.SelectedItems[0].Tag);
                vsep.Visible = vmp4.Visible = vgif.Visible = v;
                msep.Visible = vmerge.Visible = MergeCount() >= 2;
            };
            return m;
        }

        // 一覧で選んだキャプチャを「ファイル」と「画像」の両方でクリップボードへ
        //  → エクスプローラーではファイルとして、Word / PowerPoint などでは画像として貼り付けられる
        void CopyTrayItem()
        {
            if (tray.SelectedItems.Count == 0) return;
            SaveCurrent();   // 編集中なら最新の内容を保存してから
            List<string> paths = new List<string>();
            int videos = 0;
            foreach (string sp in SelectedPaths())
            {
                if (VideoInfo.IsVideo(sp)) videos++;   // 録画データ(編集用)はコピーしない
                else paths.Add(sp);
            }
            if (paths.Count == 0)
            {
                if (videos > 0) status.Text = "🎥 ビデオは「🎬 生成」で MP4 / GIF を作ると、一覧に追加されてコピーできます";
                return;
            }
            try
            {
                // 1枚なら画像データも入れる(Word などに貼れる)。複数ならファイルのみ
                DataObject d;
                if (paths.Count == 1 && paths[0].EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    using (Bitmap b = Doc.BytesToBitmap(File.ReadAllBytes(paths[0]))) d = MakeImageData(b);
                else d = new DataObject();
                StringCollection sc = new StringCollection();
                foreach (string p in paths) sc.Add(p);
                d.SetFileDropList(sc);
                d.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));   // 1 = コピー
                Clipboard.SetDataObject(d, true);
                status.Text = paths.Count == 1
                    ? "📋 コピーしました(エクスプローラーにはファイル、Word などには画像として貼り付けできます): " + Path.GetFileName(paths[0])
                    : string.Format("📋 {0} 件のファイルをコピーしました(エクスプローラーに貼り付けできます)", paths.Count);
            }
            catch (Exception ex) { MessageBox.Show(this, "コピーできませんでした: " + ex.Message, "SimpleCap"); }
        }

        // ================= 同期 =================
        void SyncProps()
        {
            syncing = true;
            try
            {
                Obj o = canvas.Selected;
                Color st = o != null ? o.Stroke : canvas.CurStroke;
                Color fi = o != null ? o.Fill : canvas.CurFill;
                float w = o != null ? o.Width : canvas.CurWidth;
                float fs = o != null ? o.FontSize : canvas.CurFontSize;
                btnStroke.Current = st; btnStroke.Invalidate();
                btnFill.Current = fi; btnFill.Invalidate();
                numFillAlpha.Value = Math.Max(0, Math.Min(100, (int)Math.Round(fi.A * 100 / 255.0)));
                numWidth.Value = (decimal)Math.Max(0, Math.Min(40, w));
                numFont.Value = (decimal)Math.Max(8, Math.Min(200, fs));
                cmbDash.SelectedIndex = Math.Max(0, Math.Min(3, o != null ? o.Dash : canvas.CurDash));
                chkBoth.Checked = (o != null && o.Type == ObjType.Arrow) ? o.BothEnds : canvas.CurBothEnds;
                chkBoth.Enabled = (o != null && o.Type == ObjType.Arrow) || (o == null && canvas.Tool == Tool.Arrow);
            }
            finally { syncing = false; }
        }

        void RefreshLayers()
        {
            syncing = true;
            try
            {
                layers.BeginUpdate();
                layers.Items.Clear();
                if (current != null)
                {
                    int n = current.Objs.Count;
                    for (int i = n - 1; i >= 0; i--)
                        layers.Items.Add(current.Objs[i].Name, current.Objs[i].Visible);
                    layers.Items.Add("🖼️ 背景 (キャプチャ)", true);
                    if (canvas.Selected != null)
                    {
                        int idx = current.Objs.IndexOf(canvas.Selected);
                        if (idx >= 0) layers.SelectedIndex = n - 1 - idx;
                    }
                    else if (canvas.BgSelected) layers.SelectedIndex = n;
                }
                layers.EndUpdate();
            }
            finally { syncing = false; }
        }

        void Layers_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (syncing || current == null) return;
            int n = current.Objs.Count;
            if (e.Index >= n) { e.NewValue = CheckState.Checked; return; } // 背景は常に表示
            canvas.PushUndo();
            Obj o = current.Objs[n - 1 - e.Index];
            o.Visible = e.NewValue == CheckState.Checked;
            canvas.Invalidate();
        }

        void ApplyZoomText(string text)
        {
            string t = (text ?? "").Replace("%", "").Replace("％", "").Trim();
            float v;
            if (float.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) && v > 0)
                SetViewZoom(Math.Max(5f, Math.Min(1600f, v)) / 100f);
            UpdateZoomBox();
        }

        // 「🔲 全体」:ウィンドウいっぱいに合わせる
        void ZoomFitAll()
        {
            ZoomBox zb = ZoomTarget;
            if (zb != null) zb.FitAll(true); else canvas.ZoomFit(host.ClientSize, true);
            UpdateZoomBox();
        }

        // 最大化したら「全体」と同じく、ウィンドウに合わせて拡大
        FormWindowState lastState = FormWindowState.Normal;
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (WindowState == FormWindowState.Maximized && lastState != FormWindowState.Maximized && IsHandleCreated)
                BeginInvoke((MethodInvoker)delegate { ZoomFitAll(); });   // レイアウトが終わってから
            lastState = WindowState;
        }

        // 表示倍率の対象:ビデオ / GIF / MP4 を表示中はその表示、それ以外(注釈の編集中を含む)はキャンバス
        ZoomBox ZoomTarget
        {
            get
            {
                if (videoAnno) return null;
                if (videoView != null && videoView.Visible) return videoView.Zoomer;
                if (mediaView != null && mediaView.Visible) return mediaView.Zoomer;
                return null;
            }
        }
        float ViewZoom { get { ZoomBox zb = ZoomTarget; return zb != null ? zb.Zoom : canvas.Zoom; } }
        void SetViewZoom(float z)
        {
            ZoomBox zb = ZoomTarget;
            if (zb != null) zb.SetZoom(z); else canvas.SetZoom(z);
            UpdateZoomBox();
        }

        void UpdateZoomBox()
        {
            if (cmbZoom == null) return;
            bool old = syncing; syncing = true;
            try
            {
                cmbZoom.SelectedIndex = -1;
            cmbZoom.Text = string.Format("{0:0}%", ViewZoom * 100);
                ZoomBox zb = ZoomTarget;
                if (zb != null) lblSize.Text = zb.Natural.Width > 0 ? string.Format("📐 {0} × {1} px", zb.Natural.Width, zb.Natural.Height) : "";
                else lblSize.Text = current != null ? string.Format("📐 {0} × {1} px", current.Base.Width, current.Base.Height) : "";
            }
            finally { syncing = old; }
        }

        void UpdateStatus()
        {
            UpdateZoomBox();
            if (status == null) return;
            string hint = "";
            switch (canvas.Tool)
            {
                case Tool.Select: hint = "クリックで選択、ドラッグで移動、ハンドルで変形。何もない所をクリック→画像サイズ変更、ドラッグ→範囲選択(中をドラッグで移動、右クリックで切り取り/新しいレイヤ)"; break;
                case Tool.Fill: hint = string.Format("クリックした所と似た色の範囲を塗ります(許容範囲 {0}%)。色=塗りの色、透明なら線の色", canvas.Tolerance); break;
                case Tool.Text: hint = "クリックした位置に文字を入れます"; break;
                case Tool.BalloonPointer:
                case Tool.BalloonArrow:
                    hint = "ドラッグ(またはクリック)で吹き出しを作成。塗り=背景色、線/文字の色=文字色"; break;
                case Tool.Number:
                case Tool.NumberPin: hint = "クリックで番号を配置(1,2,3…)。大きさ=文字サイズ、色=塗り(なしなら線の色)、ダブルクリックで番号変更"; break;
                case Tool.Crop: hint = "残したい範囲をドラッグ"; break;
                case Tool.CutV: hint = "切り取りたい範囲を上下にドラッグ → 離すと切り取って上下を詰めます(Ctrl+Z で元に戻す)"; break;
                case Tool.CutH: hint = "切り取りたい範囲を左右にドラッグ → 離すと切り取って左右を詰めます(Ctrl+Z で元に戻す)"; break;
                case Tool.ClearColor: hint = string.Format("透明にしたい色をクリック(背景画像のみ、許容範囲 {0}%)", canvas.Tolerance); break;
                default: hint = "ドラッグで描画(Shift で角度/縦横比を固定)"; break;
            }
            if (videoView != null && videoView.Visible) { status.Text = videoView.StatusText; return; }
            if (mediaView != null && mediaView.Visible) { status.Text = mediaView.StatusText; return; }
            if (current != null)
                status.Text = string.Format("{0} × {1}   {2:0}%   レイヤ {3}   |   {4}",
                    current.Base.Width, current.Base.Height, canvas.Zoom * 100, current.Objs.Count, hint);
            else status.Text = "📷 キャプチャするか、下の一覧から画像を選んでください";
        }

        // ================= ライブラリ =================
        void LoadLibrary()
        {
            // 画像(.png)・ビデオ(.scvid フォルダ)・GIF / MP4 を日時の順に
            List<string> all = new List<string>(Directory.GetFiles(Program.LibDir, "*.png"));
            try { all.AddRange(Directory.GetDirectories(Program.LibDir, "*" + VideoInfo.Ext)); } catch { }
            all.AddRange(Directory.GetFiles(Program.LibDir, "*.gif"));
            all.AddRange(Directory.GetFiles(Program.LibDir, "*.mp4"));
            all.Sort(delegate (string a, string b) { return string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase); });
            // 古い順(新しいものが最後)。キャプチャ・ビデオ・書き出した GIF / MP4 とも追加すると最後に並ぶ
            string[] files = all.ToArray();
            tray.BeginUpdate();
            foreach (string f in files)
            {
                ListViewItem it = new ListViewItem(Caption(f));
                it.Tag = f;
                tray.Items.Add(it);
            }
            tray.EndUpdate();
            if (tray.Items.Count > 0) tray.Items[tray.Items.Count - 1].EnsureVisible();   // 最新(最後)が見えるように
            // サムネイルはバックグラウンドで生成
            List<string> list = new List<string>(files);
            Size ts = thumbs.ImageSize;
            Thread th = new Thread(delegate ()
            {
                foreach (string f in list)
                {
                    Bitmap t = null;
                    try { t = MakeThumb(f, ts); } catch { }
                    if (t == null) continue;
                    try
                    {
                        string ff = f; Bitmap tt = t;
                        BeginInvoke((MethodInvoker)delegate { SetThumb(ff, tt); });
                        if (MediaFile.IsGif(f)) BeginInvoke((MethodInvoker)delegate { StartThumbAnim(ff); });
                    }
                    catch { return; }
                }
            });
            th.IsBackground = true;
            th.Priority = ThreadPriority.BelowNormal;
            th.SetApartmentState(ApartmentState.STA);   // MP4 のサムネイル(シェル)用
            bool started = false;
            EventHandler go = delegate { if (!started) { started = true; th.Start(); } };
            HandleCreated += go;
            if (IsHandleCreated) go(this, EventArgs.Empty);
        }

        static string Caption(string path)
        {
            if (path.EndsWith(VideoInfo.Ext, StringComparison.OrdinalIgnoreCase)) return "🎥 " + CaptionCore(path);
            if (path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) return "🎞️ " + CaptionCore(path);
            if (path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return "🎬 " + CaptionCore(path);
            return CaptionCore(path);
        }
        static string CaptionCore(string path)
        {
            string n = Path.GetFileNameWithoutExtension(path);
            DateTime dt;
            if (n.Length >= 15 && DateTime.TryParseExact(n.Substring(0, 15), "yyyyMMdd_HHmmss", null,
                System.Globalization.DateTimeStyles.None, out dt))
            {
                // AI で付けた名前があれば「日付 + 名前」、なければ日時
                string rest = n.Substring(15).TrimStart('_');
                bool msOnly = rest.Length > 0;
                foreach (char c in rest) if (!char.IsDigit(c)) msOnly = false;
                if (rest.Length > 0 && !msOnly) return dt.ToString("MM/dd ") + rest;
                return dt.ToString("MM/dd HH:mm:ss");
            }
            return n;
        }

        static Bitmap MakeThumb(string path, Size ts)
        {
            if (VideoInfo.IsVideo(path))
            {
                // ビデオ:最初のコマ + 再生マーク
                VideoInfo v = VideoInfo.Load(path);
                string f0 = v.FirstFrame();
                Bitmap t;
                if (f0 != null)
                    using (Bitmap b = Doc.BytesToBitmap(File.ReadAllBytes(f0)))
                    {
                        List<Obj> annos = v.LoadAnnotations();   // 注釈があれば 1 コマ目に描き込んで表示
                        if (annos.Count > 0) using (Graphics g = Graphics.FromImage(b)) VideoInfo.DrawAnnotations(g, b, annos, 0);
                        t = MakeThumb(b, ts);
                    }
                else t = new Bitmap(ts.Width, ts.Height);
                VideoBadge(t, "▶ ビデオ");
                return t;
            }
            if (MediaFile.IsMp4(path))
            {
                Bitmap t;
                using (Bitmap b = ShellThumb.Get(path, Math.Max(ts.Width, ts.Height) * 2))
                    t = b != null ? MakeThumb(b, ts) : new Bitmap(ts.Width, ts.Height);
                VideoBadge(t, "MP4");
                return t;
            }
            if (MediaFile.IsGif(path))
            {
                Bitmap t = MakeThumb(path, ts, true);
                VideoBadge(t, "GIF");
                return t;
            }
            using (MemoryStream ms = new MemoryStream(File.ReadAllBytes(path)))
            using (Image img = Image.FromStream(ms, false, false))
                return MakeThumb(img, ts);
        }
        static Bitmap MakeThumb(string path, Size ts, bool raw)
        {
            using (MemoryStream ms = new MemoryStream(File.ReadAllBytes(path)))
            using (Image img = Image.FromStream(ms, false, false))
                return MakeThumb(img, ts);
        }
        static Bitmap MakeThumb(Image img, Size ts)
        {
            Bitmap t = new Bitmap(ts.Width, ts.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(t))
            {
                g.Clear(Color.Transparent);
                float z = Math.Min((float)ts.Width / img.Width, (float)ts.Height / img.Height);
                if (z > 1) z = 1;
                int w = Math.Max(1, (int)(img.Width * z)), h = Math.Max(1, (int)(img.Height * z));
                Rectangle r = new Rectangle((ts.Width - w) / 2, (ts.Height - h) / 2, w, h);
                using (HatchBrush hb = new HatchBrush(HatchStyle.SmallCheckerBoard, Color.FromArgb(150, 150, 150), Color.FromArgb(200, 200, 200)))
                    g.FillRectangle(hb, r);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(img, r, 0, 0, img.Width, img.Height, GraphicsUnit.Pixel);
                g.DrawRectangle(Pens.Silver, r.X, r.Y, r.Width - 1, r.Height - 1);
            }
            return t;
        }

        // サムネイルに「▶ ビデオ」の印を付ける
        static void VideoBadge(Bitmap t, string s)
        {
            using (Graphics g = Graphics.FromImage(t))
            using (Font f = new Font("Yu Gothic UI", 7.5f, FontStyle.Bold))
            {
                SizeF sz = g.MeasureString(s, f);
                RectangleF r = new RectangleF(2, t.Height - sz.Height - 3, sz.Width + 4, sz.Height + 1);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(190, 30, 30, 34))) g.FillRectangle(b, r);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.DrawString(s, f, Brushes.White, r.X + 2, r.Y);
            }
        }

        // GIF のサムネイルをアニメーション
        void StartThumbAnim(string gifPath)
        {
            if (!MediaFile.IsGif(gifPath)) return;
            string dir = gifPath;   // 一覧のキー
            Size ts = thumbs.ImageSize;
            Thread th = new Thread(delegate ()
            {
                ThumbAnim a = new ThumbAnim();
                try
                {
                    using (MemoryStream ms = new MemoryStream(File.ReadAllBytes(gifPath)))
                    using (Image img = Image.FromStream(ms))
                    {
                        FrameDimension fd = new FrameDimension(img.FrameDimensionsList[0]);
                        int n = img.GetFrameCount(fd);
                        byte[] delays = null;
                        try { delays = img.GetPropertyItem(0x5100).Value; } catch { }
                        int step = Math.Max(1, (n + 149) / 150);   // 多すぎるときは間引く(最大 150 コマ)
                        for (int i = 0; i < n; i += step)
                        {
                            img.SelectActiveFrame(fd, i);
                            Bitmap t = MakeThumb(img, ts);
                            VideoBadge(t, "GIF");
                            int d = 0;
                            for (int k = i; k < Math.Min(n, i + step); k++)
                                d += delays != null && delays.Length >= (k + 1) * 4 ? BitConverter.ToInt32(delays, k * 4) * 10 : 67;
                            a.Frames.Add(t); a.Delays.Add(Math.Max(40, d));
                        }
                    }
                }
                catch { return; }
                if (a.Frames.Count < 2) return;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        ThumbAnim old;
                        if (anims.TryGetValue(dir, out old)) foreach (Bitmap b in old.Frames) b.Dispose();
                        anims[dir] = a;
                        animTimer.Start();
                    });
                }
                catch { }
            });
            th.IsBackground = true;
            th.Start();
        }

        void StopThumbAnim(string dir)
        {
            ThumbAnim a;
            if (!anims.TryGetValue(dir, out a)) return;
            anims.Remove(dir);
            foreach (Bitmap b in a.Frames) b.Dispose();
            if (anims.Count == 0) animTimer.Stop();
        }

        void AnimTick()
        {
            if (anims.Count == 0 || !Visible || WindowState == FormWindowState.Minimized) return;
            foreach (KeyValuePair<string, ThumbAnim> kv in anims)
            {
                ThumbAnim a = kv.Value;
                a.Acc += animTimer.Interval;
                if (a.Acc < a.Delays[a.Idx]) continue;
                a.Acc = 0;
                a.Idx = (a.Idx + 1) % a.Frames.Count;
                int i = thumbs.Images.IndexOfKey(kv.Key);
                if (i < 0) continue;
                thumbs.Images[i] = a.Frames[a.Idx];
                ListViewItem it = FindItem(kv.Key);
                if (it != null) tray.Invalidate(it.Bounds);
            }
        }

        void SetThumb(string path, Bitmap t)
        {
            if (IsDisposed) return;
            // ImageList からは削除しない(インデックスがずれるため)。既存は差し替え
            int i = thumbs.Images.IndexOfKey(path);
            if (i >= 0) { thumbs.Images[i] = t; tray.Invalidate(); return; }
            thumbs.Images.Add(path, t);
            foreach (ListViewItem it in tray.Items)
                if ((string)it.Tag == path) it.ImageKey = path;
        }

        ListViewItem FindItem(string path)
        {
            foreach (ListViewItem it in tray.Items) if ((string)it.Tag == path) return it;
            return null;
        }

        void SelectItem(string path)
        {
            syncing = true;
            try
            {
                tray.SelectedItems.Clear();
                ListViewItem it = FindItem(path);
                if (it != null) { it.Selected = true; it.Focused = true; it.EnsureVisible(); }
            }
            finally { syncing = false; }
        }

        // 新しいキャプチャを追加
        public void AddCapture(Bitmap bmp)
        {
            Program.EnsureLibDirChosen(Visible ? this : null);   // 初回だけ保存先フォルダを選んでもらう
            SaveCurrent();
            string name = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png";
            Doc d = new Doc();
            d.Base = bmp;
            d.PngPath = Path.Combine(Program.LibDir, name);
            try { d.Save(); }
            catch (Exception ex) { MessageBox.Show(this, "保存に失敗しました: " + ex.Message, "SimpleCap"); }
            ListViewItem it = new ListViewItem(Caption(d.PngPath));
            it.Tag = d.PngPath;
            tray.Items.Add(it);   // 新しいものは一覧の最後に
            SetThumb(d.PngPath, MakeThumb(bmp, thumbs.ImageSize));
            ShowDoc(d);
            SelectItem(d.PngPath);
        }

        void OpenCapture(string path)
        {
            if (VideoInfo.IsVideo(path))
            {
                if (videoView.Visible && videoView.PathOpen == path) return;
                ExitVideoAnnotate();
                SaveCurrent();
                current = null;
                canvas.SetDoc(null);
                ShowMediaMode(false);
                try { videoView.Open(path); }
                catch (Exception ex) { MessageBox.Show(this, "開けませんでした: " + ex.Message, "SimpleCap"); return; }
                ShowVideoMode(true);
                UpdateStatus();
                return;
            }
            if (MediaFile.IsMedia(path))
            {
                if (mediaView.Visible && mediaView.PathOpen == path) return;
                SaveCurrent();
                current = null;
                canvas.SetDoc(null);
                ShowVideoMode(false);
                try { mediaView.Open(path); }
                catch (Exception ex) { MessageBox.Show(this, "開けませんでした: " + ex.Message, "SimpleCap"); return; }
                ShowMediaMode(true);
                UpdateStatus();
                return;
            }
            if (current != null && current.PngPath == path) return;
            SaveCurrent();
            try
            {
                Cursor = Cursors.WaitCursor;
                ShowDoc(Doc.Load(path));
            }
            catch (Exception ex) { MessageBox.Show(this, "開けませんでした: " + ex.Message, "SimpleCap"); }
            finally { Cursor = Cursors.Default; }
        }

        // ================= ビデオの注釈 =================
        bool videoAnno;      // 注釈の編集中(キャンバスをビデオ画面の中に置いて、表示中のコマを背景にする)
        Doc annoDoc;
        Bitmap annoFrame;
        int hostIndex;

        void EnterVideoAnnotate()
        {
            if (videoAnno || !videoView.IsOpen || videoView.CurrentFrame == null) return;
            videoView.StopPlayback();
            annoFrame = (Bitmap)videoView.CurrentFrame.Clone();
            annoDoc = new Doc();
            annoDoc.Base = annoFrame;
            annoDoc.Objs = videoView.Annos;
            annoDoc.PngPath = videoView.Info.AnnoPng;
            hostIndex = Controls.GetChildIndex(host);
            Controls.Remove(host);
            host.Dock = DockStyle.None;
            videoAnno = true;
            canvas.LockBase = true;
            current = annoDoc;
            videoView.SetEditor(host);
            canvas.Zoom = 1f;
            canvas.SetDoc(annoDoc);
            canvas.ZoomFit(host.ClientSize);
            if (rightPanel != null) rightPanel.Enabled = true;
            UpdateTimeHidden();
            SelectTool(Tool.Arrow);
            SyncProps(); RefreshLayers();
            status.Text = "✏️ 注釈の編集中:左のツールで書き込み → 選んだ注釈は下の「ここから / ここまで / 全体」で表示する範囲を指定。PageUp / PageDown=コマ送り、Space=再生";
        }

        void ExitVideoAnnotate()
        {
            if (!videoAnno) return;
            SaveVideoAnno(false);
            List<Obj> objs = annoDoc.Objs;
            foreach (Obj o in objs) o.TimeHidden = false;
            videoAnno = false;
            canvas.LockBase = false;
            current = null;
            canvas.SetDoc(null);
            videoView.SetEditor(null);
            Controls.Add(host);
            Controls.SetChildIndex(host, hostIndex);
            host.Dock = DockStyle.Fill;
            videoView.Annos = objs;
            annoDoc = null;
            if (annoFrame != null) { annoFrame.Dispose(); annoFrame = null; }
            if (rightPanel != null) rightPanel.Enabled = !videoView.Visible;
            SelectTool(Tool.Select);
            videoView.Invalidate(true);
            UpdateStatus();
        }

        // 注釈を保存(ビデオのフォルダに anno.png / anno.scap / anno_time.txt)
        void SaveVideoAnno(bool force)
        {
            if (annoDoc == null || (!annoDoc.Dirty && !force)) return;
            VideoInfo v = videoView.Info;
            try
            {
                if (annoDoc.Objs.Count == 0) { v.DeleteAnnotations(); annoDoc.Dirty = false; }
                else { annoDoc.Save(); v.SaveAnnotationTimes(annoDoc.Objs); }
                SetThumb(v.Dir, MakeThumb(v.Dir, thumbs.ImageSize));
            }
            catch (Exception ex) { MessageBox.Show(this, "注釈を保存できませんでした: " + ex.Message, "SimpleCap"); }
        }

        // 表示するコマが変わったら、キャンバスの背景をそのコマにする
        void SetAnnoFrame()
        {
            Bitmap f = videoView.CurrentFrame;
            if (f == null || annoDoc == null) return;
            Bitmap old = annoFrame;
            annoFrame = (Bitmap)f.Clone();
            annoDoc.Base = annoFrame;
            if (old != null) old.Dispose();
            UpdateTimeHidden();
            canvas.InvalidateView();
        }

        // 範囲外の注釈は編集中も隠す
        void UpdateTimeHidden()
        {
            if (annoDoc == null) return;
            int f = videoView.Current;
            foreach (Obj o in annoDoc.Objs) o.TimeHidden = !VideoInfo.ShowAt(o, f);
        }

        // 元に戻す等でキャンバスの内容が入れ替わったとき:背景は表示中のコマのまま、注釈の一覧をビデオ側にも渡す
        void AnnoDocChanged()
        {
            if (annoDoc == null || current != annoDoc) return;
            if (annoDoc.Base != annoFrame) { annoDoc.Base = annoFrame; canvas.InvalidateView(); }   // 切り抜き・塗りなどで背景は変えない
            videoView.Annos = annoDoc.Objs;
            UpdateTimeHidden();
            videoView.RefreshAnnoUi();
        }

        void ShowVideoMode(bool on)
        {
            if (!on) ExitVideoAnnotate();
            if (on) { videoView.Bounds = host.Bounds; videoView.Visible = true; videoView.BringToFront(); }
            else if (videoView.Visible) { videoView.Visible = false; videoView.CloseVideo(); }
            if (rightPanel != null) rightPanel.Enabled = !on;
        }

        void ShowMediaMode(bool on)
        {
            if (on) { mediaView.Bounds = host.Bounds; mediaView.Visible = true; mediaView.BringToFront(); }
            else if (mediaView.Visible) { mediaView.Visible = false; mediaView.CloseMedia(); }
            if (rightPanel != null) rightPanel.Enabled = !on && !videoView.Visible;
        }

        // ================= AI で名前付け =================
        bool naming;

        async void NameWithAi()
        {
            if (naming) return;
            List<string> paths = SelectedPaths();
            if (paths.Count == 0)
            {
                if (current != null && !videoAnno) paths.Add(current.PngPath);
                else if (videoView.Visible && videoView.IsOpen) paths.Add(videoView.PathOpen);
                else if (mediaView.Visible && mediaView.IsOpen) paths.Add(mediaView.PathOpen);
            }
            if (paths.Count == 0) { status.Text = "🏷️ 名前を付けるキャプチャを下の一覧で選んでください"; return; }
            if (AiNamer.GetKey(Program.LoadSettings(), AiNamer.ActiveProvider) == "")
            {
                if (MessageBox.Show(this, "AI の設定(API の URL・シークレットキー・モデル)がまだです。\n⚙️ 設定を開きますか?", "SimpleCap",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK) SettingsWindow.Open(this);
                return;
            }
            SaveCurrent();
            naming = true;
            int done = 0;
            try
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    string path = paths[i];
                    status.Text = string.Format("🏷️ AI に問い合わせています… ({0}/{1}) {2}", i + 1, paths.Count, Path.GetFileName(path));
                    byte[] jpg;
                    try { jpg = ImageForAi(path); }
                    catch (Exception ex) { MessageBox.Show(this, "画像を用意できませんでした: " + ex.Message, "SimpleCap"); continue; }
                    if (jpg == null) continue;
                    string name;
                    try { name = await Task.Run(() => AiNamer.Ask(jpg)); }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "AI で名前を付けられませんでした。\n\n" + ex.Message, "SimpleCap");
                        break;
                    }
                    string np = RenameItem(path, name);
                    if (np != null) done++;
                }
            }
            finally { naming = false; }
            if (done > 0) status.Text = string.Format("🏷️ {0} 件に名前を付けました", done) + (done == 1 && paths.Count == 1 ? "" : "");
        }

        // AI に送る画像(画像=そのもの、ビデオ / GIF / MP4 = 1 コマ目)
        byte[] ImageForAi(string path)
        {
            if (VideoInfo.IsVideo(path))
            {
                string f0 = VideoInfo.Load(path).FirstFrame();
                if (f0 == null) return null;
                using (Bitmap b = Doc.BytesToBitmap(File.ReadAllBytes(f0))) return AiNamer.ToJpeg(b);
            }
            if (MediaFile.IsMp4(path))
            {
                string ff = FFmpeg.Find();
                if (ff != null)
                {
                    string tmp = Path.Combine(Path.GetTempPath(), "SimpleCap_" + Guid.NewGuid().ToString("N") + ".png");
                    try
                    {
                        ProcessStartInfo si = new ProcessStartInfo(ff, "-y -i \"" + path + "\" -frames:v 1 \"" + tmp + "\"");
                        si.UseShellExecute = false; si.CreateNoWindow = true;
                        using (Process pr = Process.Start(si)) pr.WaitForExit(20000);
                        if (File.Exists(tmp)) using (Bitmap b = Doc.BytesToBitmap(File.ReadAllBytes(tmp))) return AiNamer.ToJpeg(b);
                    }
                    catch { }
                    finally { try { File.Delete(tmp); } catch { } }
                }
                using (Bitmap b = ShellThumb.Get(path, 1568)) return b != null ? AiNamer.ToJpeg(b) : null;
            }
            using (MemoryStream ms = new MemoryStream(File.ReadAllBytes(path)))
            using (Image img = Image.FromStream(ms))   // GIF は 1 コマ目
                return AiNamer.ToJpeg(img);
        }

        // ファイル(ビデオはフォルダ)の名前を変える。先頭の日時は残して並び順を保つ
        string RenameItem(string oldPath, string aiName)
        {
            string dir = Path.GetDirectoryName(oldPath);
            string ext = Path.GetExtension(oldPath);
            string baseName = Path.GetFileNameWithoutExtension(oldPath);
            DateTime dt;
            string prefix = baseName.Length >= 15 && DateTime.TryParseExact(baseName.Substring(0, 15), "yyyyMMdd_HHmmss", null,
                System.Globalization.DateTimeStyles.None, out dt) ? baseName.Substring(0, 15) + "_" : "";
            string nb = prefix + aiName;
            string np = Path.Combine(dir, nb + ext);
            if (string.Equals(np, oldPath, StringComparison.OrdinalIgnoreCase)) return oldPath;
            for (int k = 2; File.Exists(np) || Directory.Exists(np); k++) np = Path.Combine(dir, nb + "(" + k + ")" + ext);

            bool wasDoc = current != null && current.PngPath == oldPath;
            bool wasVideo = videoView.Visible && videoView.PathOpen == oldPath;
            bool wasMedia = mediaView.Visible && mediaView.PathOpen == oldPath;
            if (wasVideo) ShowVideoMode(false);
            if (wasMedia) ShowMediaMode(false);
            StopThumbAnim(oldPath);
            try
            {
                if (Directory.Exists(oldPath)) Directory.Move(oldPath, np);
                else
                {
                    File.Move(oldPath, np);
                    string sc = Path.ChangeExtension(oldPath, ".scap");
                    if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(sc)) File.Move(sc, Path.ChangeExtension(np, ".scap"));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "名前を変えられませんでした: " + ex.Message, "SimpleCap");
                if (wasVideo || wasMedia) OpenCapture(oldPath);
                return null;
            }
            ListViewItem it = FindItem(oldPath);
            if (it != null)
            {
                it.Tag = np;
                it.Text = Caption(np);
                try { SetThumb(np, MakeThumb(np, thumbs.ImageSize)); } catch { }
            }
            if (wasDoc) current.PngPath = np;
            if (wasVideo || wasMedia) OpenCapture(np);
            if (MediaFile.IsGif(np)) StartThumbAnim(np);
            return np;
        }

        // 書き出した MP4 / GIF を一覧に追加(保存先フォルダに保存したときだけ)
        void AddLibraryFile(string path)
        {
            try
            {
                if (!string.Equals(Path.GetFullPath(Path.GetDirectoryName(path)).TrimEnd('\\'), Path.GetFullPath(Program.LibDir).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase)) return;
            }
            catch { return; }
            if (mediaView.PathOpen == path) ShowMediaMode(false);   // 上書きしたときは開き直す
            StopThumbAnim(path);
            if (FindItem(path) == null)
            {
                ListViewItem it = new ListViewItem(Caption(path));
                it.Tag = path;
                tray.Items.Add(it);   // 新しいものは一覧の最後に
                it.EnsureVisible();
            }
            try { SetThumb(path, MakeThumb(path, thumbs.ImageSize)); } catch { }
            if (MediaFile.IsGif(path)) StartThumbAnim(path);
        }

        void ExportVideo(string fmt)
        {
            if (tray.SelectedItems.Count != 1) return;
            string path = (string)tray.SelectedItems[0].Tag;
            if (!VideoInfo.IsVideo(path)) return;
            OpenCapture(path);
            videoView.Export(fmt);
        }

        // 録画したビデオを一覧に追加して開く
        public void AddVideo(string dir)
        {
            SaveCurrent();
            if (FindItem(dir) == null)   // ブラウズ画面を開いたときの読み込みで、もう一覧にあることがある
            {
                ListViewItem it = new ListViewItem(Caption(dir));
                it.Tag = dir;
                tray.Items.Add(it);   // 新しいものは一覧の最後に
            }
            try { SetThumb(dir, MakeThumb(dir, thumbs.ImageSize)); } catch { }
            SelectItem(dir);
            OpenCapture(dir);
        }

        void ShowDoc(Doc d)
        {
            ShowVideoMode(false);
            ShowMediaMode(false);
            current = d;
            canvas.Zoom = 1f;
            canvas.SetDoc(d);
            canvas.ZoomFit(host.ClientSize);
            SyncProps();
            UpdateStatus();
        }

        public void SaveCurrent() { SaveCurrent(false); }
        void SaveCurrent(bool force)
        {
            if (videoAnno) SaveVideoAnno(force);   // 注釈の編集中は注釈を保存
            if (force && videoView.Visible && videoView.IsOpen) { videoView.ExportDialog(); return; }
            if (videoAnno) return;   // ビデオ:Ctrl+S で MP4 / GIF に書き出し
            if (force && mediaView.Visible) return;
            if (current == null) return;
            if (!current.Dirty && !force) return;
            try
            {
                current.Save();
                using (Bitmap flat = current.Render())
                    SetThumb(current.PngPath, MakeThumb(flat, thumbs.ImageSize));
                if (force) status.Text = "💾 保存しました: " + current.PngPath;
            }
            catch (Exception ex) { MessageBox.Show(this, "保存に失敗しました: " + ex.Message, "SimpleCap"); }
        }

        // ---- 結合 ----
        int MergeCount()
        {
            int n = 0;
            foreach (string p in SelectedPaths()) if (Merger.CanMerge(p)) n++;
            return n;
        }

        void UpdateMergeButton()
        {
            if (videoView != null) videoView.MergeAvailable = MergeCount() >= 2;
        }

        // 選んだビデオ / GIF / MP4 を一覧の並び順につないで、新しいファイルとして一覧に追加
        void MergeSelected()
        {
            List<string> paths = new List<string>();
            foreach (ListViewItem it in tray.Items) if (it.Selected && Merger.CanMerge((string)it.Tag)) paths.Add((string)it.Tag);
            if (paths.Count < 2) { status.Text = "🔗 結合するビデオ / GIF / MP4 を、下の一覧で 2 つ以上選んでください"; return; }
            SaveCurrent();   // 開いているビデオの注釈・区間を保存してから
            if (videoView.IsOpen) videoView.StopPlayback();
            string outPath = Merger.Run(this, paths);
            if (outPath == null) return;
            AddLibraryFile(outPath);
            status.Text = "🔗 " + paths.Count + " 本を結合しました: " + outPath;
        }

        List<string> SelectedPaths()
        {
            List<string> l = new List<string>();
            foreach (ListViewItem it in tray.SelectedItems) l.Add((string)it.Tag);
            return l;
        }

        void SelectAllTray()
        {
            syncing = true;
            try { tray.BeginUpdate(); foreach (ListViewItem it in tray.Items) it.Selected = true; }
            finally { tray.EndUpdate(); syncing = false; }
            status.Text = string.Format("{0} 件を選択しました(Ctrl+C でコピー、Delete で削除)", tray.Items.Count);
        }

        void DeleteCapture()
        {
            if (tray.SelectedItems.Count == 0) return;
            List<ListViewItem> items = new List<ListViewItem>();
            foreach (ListViewItem it in tray.SelectedItems) items.Add(it);
            string msg = items.Count == 1 ? "このキャプチャをごみ箱へ移動しますか?" : string.Format("選択した {0} 件のキャプチャをごみ箱へ移動しますか?", items.Count);
            if (MessageBox.Show(this, msg, "SimpleCap", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            int idx = items[0].Index;
            syncing = true;
            try
            {
                foreach (ListViewItem it in items)
                {
                    string path = (string)it.Tag;
                    if (current != null && current.PngPath == path) { current = null; canvas.SetDoc(null); }
                    if (videoView.PathOpen == path) ShowVideoMode(false);
                    if (mediaView.PathOpen == path) ShowMediaMode(false);
                    StopThumbAnim(path);
                    try
                    {
                        Recycle(path);
                        string sp = Path.ChangeExtension(path, ".scap");
                        if (File.Exists(sp)) Recycle(sp);
                    }
                    catch (Exception ex) { MessageBox.Show(this, "削除できませんでした: " + ex.Message, "SimpleCap"); break; }
                    tray.Items.Remove(it);
                }
            }
            finally { syncing = false; }
            if (tray.Items.Count > 0) tray.Items[Math.Max(0, Math.Min(idx, tray.Items.Count - 1))].Selected = true;
            UpdateStatus();
        }

        // 保存先フォルダを変えたときに一覧を読み直す
        public void ReloadLibrary()
        {
            SaveCurrent();
            current = null;
            canvas.SetDoc(null);
            syncing = true;
            ShowVideoMode(false);
            ShowMediaMode(false);
            foreach (string k in new List<string>(anims.Keys)) StopThumbAnim(k);
            try { tray.Items.Clear(); thumbs.Images.Clear(); }
            finally { syncing = false; }
            LoadLibrary();
            UpdateStatus();
        }

        static void Recycle(string path)
        {
            if (Directory.Exists(path))   // ビデオ(フォルダ)
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                return;
            }
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }

        // ================= 入出力 =================
        void ExportAs()
        {
            if (videoView.Visible && videoView.IsOpen) { videoView.ExportDialog(); return; }
            if (current == null) return;
            using (SaveFileDialog sd = new SaveFileDialog())
            {
                sd.Filter = "PNG (透明対応)|*.png|JPEG|*.jpg|BMP|*.bmp";
                sd.FileName = Path.GetFileNameWithoutExtension(current.PngPath);
                if (sd.ShowDialog(this) != DialogResult.OK) return;
                using (Bitmap flat = current.Render())
                {
                    string ext = Path.GetExtension(sd.FileName).ToLowerInvariant();
                    if (ext == ".jpg" || ext == ".jpeg" || ext == ".bmp")
                    {
                        using (Bitmap op = new Bitmap(flat.Width, flat.Height, PixelFormat.Format24bppRgb))
                        {
                            using (Graphics g = Graphics.FromImage(op))
                            {
                                g.Clear(Color.White);
                                g.DrawImage(flat, new Rectangle(0, 0, flat.Width, flat.Height), 0, 0, flat.Width, flat.Height, GraphicsUnit.Pixel);
                            }
                            op.Save(sd.FileName, ext == ".bmp" ? ImageFormat.Bmp : ImageFormat.Jpeg);
                        }
                    }
                    else flat.Save(sd.FileName, ImageFormat.Png);
                }
                status.Text = "📤 書き出しました: " + sd.FileName;
            }
        }

        // 範囲選択の右クリックメニュー
        void ShowRegionMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Font = Program.UiFont;
            m.Items.Add("✂️ 切り取り (Ctrl+X)", null, delegate { CutRegion(); });
            m.Items.Add("📋 コピー (Ctrl+C)", null, delegate { CopyToClipboard(); });
            m.Items.Add("📥 貼り付け - 新しいレイヤ (Ctrl+V)", null, delegate { Paste(); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("🧩 新しいレイヤにコピー", null, delegate { canvas.RegionToLayer(false); });
            m.Items.Add("🧩 切り取って新しいレイヤへ", null, delegate { canvas.RegionToLayer(true); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("🗑️ 削除 (透明にする)", null, delegate { canvas.ClearRegion(); });
            m.Items.Add("✖ 選択を解除 (Esc)", null, delegate { canvas.ClearRegionSelection(); });
            m.Closed += delegate { BeginInvoke((MethodInvoker)delegate { m.Dispose(); }); };
            m.Show(Cursor.Position);
        }

        void CutRegion()
        {
            if (!canvas.HasRegion) return;
            using (Bitmap b = canvas.GetRegionImage()) SetClipboardImage(b);
            canvas.ClearRegion();
            status.Text = "✂️ 切り取りました(Ctrl+V で新しいレイヤとして貼り付け)";
        }

        internal static void SetClipboardImage(Bitmap flat)
        {
            Clipboard.SetDataObject(MakeImageData(flat), true);
        }

        // PNG(透明あり)+ 白背景ビットマップの両方を入れたクリップボードデータ
        static DataObject MakeImageData(Bitmap flat)
        {
            DataObject d = new DataObject();
            MemoryStream ms = new MemoryStream();
            flat.Save(ms, ImageFormat.Png);
            d.SetData("PNG", false, ms);       // 透明を保持したいアプリ向け
            // 透明を扱えないアプリ向けに白背景版も入れる(クリップボードへ渡すまで破棄しない)
            Bitmap op = new Bitmap(flat.Width, flat.Height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(op))
            {
                g.Clear(Color.White);
                g.DrawImage(flat, new Rectangle(0, 0, flat.Width, flat.Height), 0, 0, flat.Width, flat.Height, GraphicsUnit.Pixel);
            }
            d.SetData(DataFormats.Bitmap, true, op);
            return d;
        }

        // アプリ内でコピーした注釈(貼り付けると編集できる注釈として復元)
        static List<Obj> objClip;
        static string objClipToken;
        int pasteCount;

        void CopySelectedObject()
        {
            Obj o = canvas.Selected;
            using (Bitmap b = canvas.GetObjectImage(o))
            {
                DataObject d = MakeImageData(b);
                objClipToken = Guid.NewGuid().ToString();
                d.SetData("SimpleCapObject", false, objClipToken);   // 自分のコピーかどうかの目印
                Clipboard.SetDataObject(d, true);
            }
            objClip = new List<Obj>();
            objClip.Add(o.Clone());
            pasteCount = 0;
            status.Text = "📋 選択した注釈をコピーしました(Ctrl+V で貼り付け)";
        }

        void CutSelectedObject()
        {
            if (canvas.Selected == null) return;
            CopySelectedObject();
            canvas.DeleteSelected();
            pasteCount = -1;   // 切り取りは同じ位置に貼り付け
            status.Text = "✂️ 選択した注釈を切り取りました(Ctrl+V で貼り付け)";
        }

        void CopyToClipboard()
        {
            if (current == null) return;
            if (canvas.HasRegion)
            {
                using (Bitmap b = canvas.GetRegionImage()) SetClipboardImage(b);
                status.Text = "📋 選択範囲をコピーしました";
                return;
            }
            if (canvas.Selected != null) { CopySelectedObject(); return; }
            using (Bitmap flat = current.Render())
            {
                DataObject d = new DataObject();
                MemoryStream ms = new MemoryStream();
                flat.Save(ms, ImageFormat.Png);
                d.SetData("PNG", false, ms);       // 透明を保持したいアプリ向け
                using (Bitmap op = new Bitmap(flat.Width, flat.Height, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(op))
                    {
                        g.Clear(Color.White);
                        g.DrawImage(flat, new Rectangle(0, 0, flat.Width, flat.Height), 0, 0, flat.Width, flat.Height, GraphicsUnit.Pixel);
                    }
                    d.SetData(DataFormats.Bitmap, true, op);
                    Clipboard.SetDataObject(d, true);
                }
            }
            status.Text = "📋 クリップボードにコピーしました";
        }

        void Paste()
        {
            Bitmap img = null;
            try
            {
                IDataObject d = Clipboard.GetDataObject();
                // このアプリでコピーした注釈なら、編集できる注釈として貼り付け
                if (current != null && objClip != null && d != null && d.GetDataPresent("SimpleCapObject")
                    && (d.GetData("SimpleCapObject") as string) == objClipToken)
                {
                    pasteCount++;
                    SelectTool(Tool.Select);
                    canvas.PasteObjects(objClip, Math.Max(0, pasteCount) * 16f);
                    status.Text = "📥 注釈を貼り付けました";
                    return;
                }
                if (d != null && d.GetDataPresent("PNG"))
                {
                    MemoryStream ms = d.GetData("PNG") as MemoryStream;
                    if (ms != null) img = Doc.BytesToBitmap(ms.ToArray());
                }
                if (img == null && Clipboard.ContainsImage())
                    using (Image i = Clipboard.GetImage()) img = Doc.ToArgb(i);
                if (img == null && Clipboard.ContainsFileDropList())
                    foreach (string f in Clipboard.GetFileDropList())
                    {
                        try { img = Doc.BytesToBitmap(File.ReadAllBytes(f)); break; } catch { }
                    }
            }
            catch { }
            if (img == null) { status.Text = "クリップボードに画像がありません"; return; }
            if (current == null) AddCapture(img);
            else if (canvas.HasRegion)
            {
                // 範囲選択中はその位置に新しいレイヤとして貼り付け
                Point at = canvas.RegionRect.Location;
                SelectTool(Tool.Select);
                canvas.AddImageLayer(img, at, true);
            }
            else { SelectTool(Tool.Select); canvas.AddImageLayer(img); }
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            base.OnDragEnter(e);
        }
        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null) return;
            foreach (string f in files)
            {
                if (f.StartsWith(Program.LibDir, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    Bitmap b = Doc.BytesToBitmap(File.ReadAllBytes(f));
                    if (current == null) AddCapture(b); else canvas.AddImageLayer(b);
                }
                catch { }
            }
        }

        // ================= キー操作 =================
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            bool inText = ActiveControl is TextBoxBase || ActiveControl is NumericUpDown || ActiveControl is UpDownBase || (cmbZoom != null && cmbZoom.Focused);
            switch (keyData)
            {
                case Keys.Control | Keys.Z: canvas.Undo(); return true;
                case Keys.Control | Keys.Y: canvas.Redo(); return true;
                case Keys.Control | Keys.S: SaveCurrent(true); return true;
                case Keys.Control | Keys.C:
                    if (inText) break;
                    if (tray.Focused) { CopyTrayItem(); return true; }
                    CopyToClipboard(); return true;
                case Keys.Control | Keys.X:
                    if (inText) break;
                    if (canvas.HasRegion) { CutRegion(); return true; }
                    if (canvas.Selected != null) { CutSelectedObject(); return true; }
                    break;
                case Keys.Control | Keys.V: if (inText) break; Paste(); return true;
                case Keys.Control | Keys.Shift | Keys.S: ExportAs(); return true;
            }
            if (tray.Focused && keyData == Keys.Delete) { DeleteCapture(); return true; }   // 一覧で Delete → 削除
            if (tray.Focused && keyData == (Keys.Control | Keys.A)) { SelectAllTray(); return true; }   // 一覧で Ctrl+A → すべて選択
            if (tray.Focused && keyData == Keys.Escape) { LeaveByEscape(); return true; }   // 一覧で Esc → キャプチャウィンドウへ(トレイ常駐なら最小化)
            if (!inText && !tray.Focused && videoAnno)
            {
                // 注釈の編集中:キャンバスの操作を優先。コマ送りは PageUp / PageDown、再生は Space
                if (keyData == Keys.Space) { videoView.TogglePlayback(); return true; }
                if (keyData == Keys.PageUp) { videoView.StepFrame(-1); return true; }
                if (keyData == Keys.PageDown) { videoView.StepFrame(1); return true; }
            }
            else if (!inText && !tray.Focused && videoView.Visible && videoView.HandleKey(keyData)) return true;   // ビデオ編集のキー
            if (!inText && !tray.Focused && mediaView.Visible && mediaView.HandleKey(keyData)) return true;   // MP4 の再生・停止
            if (!inText && !tray.Focused)
            {
                switch (keyData)
                {
                    case Keys.Delete: if (canvas.HasRegion) canvas.ClearRegion(); else canvas.DeleteSelected(); return true;
                    case Keys.Escape:
                        // 選択中・ツール使用中ならまず解除、何もなければキャプチャ画面へ
                        if (canvas.HasRegion || canvas.Selected != null || canvas.BgSelected || canvas.Tool != Tool.Select)
                        {
                            canvas.ClearRegionSelection(); canvas.SetSelected(null); canvas.SetBgSelected(false); SelectTool(Tool.Select);
                        }
                        else LeaveByEscape();   // 何もなければキャプチャウィンドウへ(トレイ常駐なら最小化してタスクバーに残す)
                        return true;
                    case Keys.V: SelectTool(Tool.Select); return true;
                    case Keys.A: SelectTool(Tool.Arrow); return true;
                    case Keys.L: SelectTool(Tool.Line); return true;
                    case Keys.R: SelectTool(Tool.Rect); return true;
                    case Keys.E: SelectTool(Tool.Ellipse); return true;
                    case Keys.T: SelectTool(Tool.Text); return true;
                    case Keys.H: SelectTool(Tool.Highlight); return true;
                    case Keys.M: SelectTool(Tool.Mosaic); return true;
                    case Keys.C: SelectTool(Tool.Crop); return true;
                    case Keys.F: SelectTool(Tool.Fill); return true;
                    case Keys.B: SelectTool(Tool.BalloonPointer); return true;
                    case Keys.W: SelectTool(Tool.BalloonArrow); return true;
                    case Keys.N: SelectTool(Tool.Number); return true;
                    case Keys.P: SelectTool(Tool.NumberPin); return true;
                    // カーソルキーで 5px、Ctrl または Shift+カーソルキーで 1px 移動
                    case Keys.Left: if (canvas.Selected != null) { canvas.Nudge(-5, 0); return true; } break;
                    case Keys.Right: if (canvas.Selected != null) { canvas.Nudge(5, 0); return true; } break;
                    case Keys.Up: if (canvas.Selected != null) { canvas.Nudge(0, -5); return true; } break;
                    case Keys.Down: if (canvas.Selected != null) { canvas.Nudge(0, 5); return true; } break;
                    case Keys.Shift | Keys.Left: case Keys.Control | Keys.Left: if (canvas.Selected != null) { canvas.Nudge(-1, 0); return true; } break;
                    case Keys.Shift | Keys.Right: case Keys.Control | Keys.Right: if (canvas.Selected != null) { canvas.Nudge(1, 0); return true; } break;
                    case Keys.Shift | Keys.Up: case Keys.Control | Keys.Up: if (canvas.Selected != null) { canvas.Nudge(0, -1); return true; } break;
                    case Keys.Shift | Keys.Down: case Keys.Control | Keys.Down: if (canvas.Selected != null) { canvas.Nudge(0, 1); return true; } break;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // Esc:最小化してタスクバーに残す(タスクバー / ホットキー / トレイからいつでも呼び出せる)
        // Esc でブラウズ画面を離れる:
        //  ・「システムトレイに入れる」が OFF → × と同じく、ブラウズ画面を隠してキャプチャウィンドウを表示
        //  ・ON(キャプチャウィンドウを出さない設定)→ 最小化してタスクバーに残す
        void LeaveByEscape()
        {
            if (Program.CapWin != null && !Program.CapWin.OptTray)
            {
                SaveCurrent();
                Program.CapWin.ShowCaptureOnly();
            }
            else MinimizeToTaskbar();
        }

        void MinimizeToTaskbar()
        {
            SaveCurrent();
            WindowState = FormWindowState.Minimized;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (current != null) canvas.ZoomFit(host.ClientSize);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveCurrent();
            if (rightPanel != null)
            {
                // 右パネルの幅とレイヤ部分の高さを覚える
                Dictionary<string, string> st = Program.LoadSettings();
                st["panelw"] = rightPanel.Width.ToString();
                st["layersh"] = layerPanel.Height.ToString();
                if (WindowState != FormWindowState.Minimized || RestoreBounds.Width > 0) Place.Save(st, "browser", this, true);
                Program.SaveSettings(st);
            }
            if (!Program.Exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;   // ×では隠すだけ(キャプチャウィンドウが本体)
                Program.CapWin.ShowCaptureOnly();
                return;
            }
            base.OnFormClosing(e);
        }
    }

    // テキスト入力ダイアログ
    public static class TextInputDialog
    {
        public static string Ask(IWin32Window owner, string init)
        {
            using (Form f = new Form())
            {
                f.Text = "🔤 テキスト (Ctrl+Enter で確定)";
                f.AutoScaleMode = AutoScaleMode.None;
                f.Font = Program.UiFont;
                f.FormBorderStyle = FormBorderStyle.SizableToolWindow;
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(Cursor.Position.X + Program.D(10), Cursor.Position.Y + Program.D(10));
                f.ClientSize = new Size(Program.D(360), Program.D(150));
                f.ShowInTaskbar = false;
                f.MinimizeBox = false; f.MaximizeBox = false;

                TextBox tb = new TextBox();
                tb.Multiline = true; tb.AcceptsReturn = true;
                tb.ScrollBars = ScrollBars.Vertical;
                tb.Font = new Font("Yu Gothic UI", 12f);
                tb.Text = (init ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
                tb.SetBounds(Program.D(8), Program.D(8), f.ClientSize.Width - Program.D(16), f.ClientSize.Height - Program.D(52));
                tb.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom;

                Button ok = new Button(); ok.Text = "✔ OK"; ok.DialogResult = DialogResult.OK;
                Button cancel = new Button(); cancel.Text = "キャンセル"; cancel.DialogResult = DialogResult.Cancel;
                ok.SetBounds(f.ClientSize.Width - Program.D(176), f.ClientSize.Height - Program.D(38), Program.D(80), Program.D(30));
                cancel.SetBounds(f.ClientSize.Width - Program.D(88), f.ClientSize.Height - Program.D(38), Program.D(80), Program.D(30));
                ok.Anchor = cancel.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
                f.CancelButton = cancel;
                tb.KeyDown += delegate (object s, KeyEventArgs e)
                {
                    if (e.KeyCode == Keys.Enter && e.Control) { e.SuppressKeyPress = true; f.DialogResult = DialogResult.OK; }
                };
                f.Controls.Add(tb); f.Controls.Add(ok); f.Controls.Add(cancel);

                // 画面外にはみ出さない
                Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
                f.Location = new Point(Math.Min(f.Location.X, wa.Right - f.Width), Math.Min(f.Location.Y, wa.Bottom - f.Height));

                if (f.ShowDialog(owner) == DialogResult.OK) return tb.Text.Replace("\r\n", "\n");
                return null;
            }
        }
    }
}
