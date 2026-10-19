using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SimpleCap
{
    // 絵文字+キャプションのタイルボタン
    public class Tile : Control
    {
        public string Emoji, Caption;
        public bool Toggle, Checked;
        public bool Compact;        // アイコンのみ表示
        public string Badge = "";   // アイコンのみ表示のとき絵文字の代わりに出す文字(カウントダウン用)
        public Color Accent = Color.Empty;   // 指定するとカラーのボタンとして描く
        public bool Split;          // 右端に ⌄ の部分があるスプリットボタン(⌄ を押すと SplitClick)
        public event EventHandler SplitClick;
        bool hover;
        int hoverPart;              // スプリットボタンでマウスが乗っている部分:1=本体 2=⌄
        int lastUpX; MouseButtons lastUpButton = MouseButtons.Left;
        public int SplitWidth { get { return Program.D(Compact ? 13 : 18); } }

        public Tile(string emoji, string caption, bool toggle)
        {
            Emoji = emoji; Caption = caption; Toggle = toggle;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Size = new Size(Program.D(56), Program.D(54));
            Cursor = Cursors.Hand;
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; hoverPart = 0; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (Split)
            {
                int part = e.X >= Width - SplitWidth ? 2 : 1;
                if (part != hoverPart) { hoverPart = part; Invalidate(); }
            }
            base.OnMouseMove(e);
        }
        protected override void OnMouseUp(MouseEventArgs e) { lastUpX = e.X; lastUpButton = e.Button; base.OnMouseUp(e); }
        protected override void OnClick(EventArgs e)
        {
            if (lastUpButton != MouseButtons.Left) return;   // 右クリックはメニュー用
            if (Split && lastUpX >= Width - SplitWidth)
            {
                if (SplitClick != null) SplitClick(this, EventArgs.Empty);
                return;
            }
            if (Toggle) { Checked = !Checked; Invalidate(); }
            base.OnClick(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Color.White);
            Color bg = Checked ? Color.FromArgb(204, 228, 255) : (hover ? Color.FromArgb(232, 240, 250) : Color.Transparent);
            bool color = Accent != Color.Empty;
            if (color)   // カラーのボタン:マウスを乗せると少し濃く(スプリットボタンは本体に乗っているときだけ)
                bg = hover && (!Split || hoverPart == 1) ? Shade(Accent, 90) : Accent;
            Rectangle box = new Rectangle(1, 1, Width - 3, Height - 3);
            if (bg.A > 0)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath p = RoundRect(box, Program.D(7)))
                using (SolidBrush b = new SolidBrush(bg))
                {
                    g.FillPath(b, p);
                    if (Split)
                    {
                        // 右端の ⌄ の部分:同じ色の少し濃い帯(乗っているときはさらに濃く)
                        int sx = Width - SplitWidth;
                        Region old = g.Clip;
                        g.SetClip(new Rectangle(sx, 0, SplitWidth, Height));
                        using (SolidBrush sb = new SolidBrush(Shade(Accent, hover && hoverPart == 2 ? 74 : 84))) g.FillPath(sb, p);
                        g.Clip = old;
                        using (Pen sep = new Pen(Color.FromArgb(140, Color.White))) g.DrawLine(sep, sx, box.Top + Program.D(6), sx, box.Bottom - Program.D(6));
                        float cx = sx + SplitWidth / 2f - 0.5f, cy0 = Height / 2f, a = Program.D(Compact ? 3 : 4);
                        using (Pen cp = new Pen(Color.FromArgb(45, 45, 45), Math.Max(1.5f, Program.D(2) * 0.8f)))
                        {
                            cp.StartCap = LineCap.Round; cp.EndCap = LineCap.Round; cp.LineJoin = LineJoin.Round;
                            g.DrawLines(cp, new PointF[] { new PointF(cx - a, cy0 - a * 0.45f), new PointF(cx, cy0 + a * 0.55f), new PointF(cx + a, cy0 - a * 0.45f) });
                        }
                    }
                    if (Checked && !color) using (Pen pen = new Pen(Color.FromArgb(0, 120, 215))) g.DrawPath(pen, p);
                }
            }
            TextFormatFlags f = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            int cw = Split ? Width - SplitWidth : Width;   // 文字とアイコンを置く幅
            if (Compact)
            {
                if (Badge != "")
                    using (Font bf = new Font("Yu Gothic UI", 13f, FontStyle.Bold))
                        TextRenderer.DrawText(g, Badge, bf, new Rectangle(0, 0, cw, Height), Color.FromArgb(0, 120, 215), f);
                else TextRenderer.DrawText(g, Emoji, Program.EmojiFont, new Rectangle(0, 0, cw, Height), Color.Black, f);
                return;
            }
            int eh = Height * 30 / 54, cy = Height * 31 / 54;
            TextRenderer.DrawText(g, Emoji, Program.EmojiFont, new Rectangle(0, Program.D(2), cw, eh), Color.Black, f);
            TextRenderer.DrawText(g, Caption, Program.SmallFont, new Rectangle(0, cy, cw, Height - cy - Program.D(2)), Color.FromArgb(50, 50, 50), f);
        }
        static Color Shade(Color c, int pct) { return Color.FromArgb(c.R * pct / 100, c.G * pct / 100, c.B * pct / 100); }
        public static GraphicsPath RoundRect(Rectangle r, int rad)
        {
            GraphicsPath p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // 小さなキャプチャウィンドウ
    public class CaptureWindow : Form
    {
        Tile tCap, tBrowse, tSet;   // tCap は右端の ⌄ でスクロール / ビデオを選ぶスプリットボタン
        // tCap の撮り方(最後に選んだものを覚えて、次回もボタン本体で使う):0=キャプチャ 1=スクロール 2=ビデオ
        int capMode;
        static readonly string[] ModeEmoji = { "📷", "📜", "🎥" };
        static readonly string[] ModeCaption = { "キャプチャ", "スクロール", "ビデオ" };
        static readonly Color[] ModeColor = { Color.FromArgb(0x94, 0xD0, 0xFF), Color.FromArgb(0xCD, 0xB9, 0xF1), Color.FromArgb(0xF3, 0xAD, 0xAD) };   // 青 / 紫 / 赤
        // 「3秒後」「カーソル」「クリップボード」は ⚙️ 設定ウィンドウで切り替える
        public bool OptDelay, OptCursor, OptClip;
        // ON のときキャプチャウィンドウは表示せず、システムトレイ + ショートカットキーだけで使う
        public bool OptTray;
        public bool IsCompact { get { return compact; } }
        Tile[] tiles;
        bool compact;
        ToolTip tip = new ToolTip();
        bool busy;
        string hotkeyText = "";
        System.Windows.Forms.Timer delayTimer;
        int countdown;
        bool pendingFull;

        public CaptureWindow()
        {
            Text = "SimpleCap";
            OptTray = Program.GetInt(Program.LoadSettings(), "trayonly", 0) == 1;
            // タイトルバーのない、ボタンだけのウィンドウ。左端のつまみ(と余白)をドラッグして移動、右クリックでメニュー
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false; MinimizeBox = false;
            KeyPreview = true;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            Font = Program.UiFont;
            BackColor = Color.FromArgb(250, 250, 251);
            DoubleBuffered = true;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;   // タスクバーには出さず、システムトレイに常駐
            if (Program.AppIcon != null) Icon = Program.AppIcon;

            tCap = new Tile("📷", "キャプチャ", false);
            tBrowse = new Tile("🗂️", "ブラウズ", false);
            tSet = new Tile("⚙️", "設定", false);
            // キャプチャだけ色付きのスプリットボタン。ブラウズ・設定はウィンドウの背景と同じ(マウスを乗せると薄く色が付く)
            tCap.Accent = Color.FromArgb(0x94, 0xD0, 0xFF);     // 青   #94D0FF
            tCap.Split = true;
            tCap.SplitClick += delegate { ShowDropMenu(); };
            tiles = new Tile[] { tCap, tBrowse, tSet };
            foreach (Tile t in tiles) Controls.Add(t);
            // 右クリックメニュー(タイトルバーと × の代わり)
            winMenu = BuildWindowMenu();
            ContextMenuStrip = winMenu;
            foreach (Tile t in tiles) t.ContextMenuStrip = winMenu;
            tSet.Click += delegate { SettingsWindow.Open(this); };

            tCap.Click += delegate { RunMode(); };
            tBrowse.Click += delegate { ShowBrowser(); };

            Dictionary<string, string> st = Program.LoadSettings();
            capMode = Math.Max(0, Math.Min(2, Program.GetInt(st, "capmode", 0)));
            ApplyMode();
            OptDelay = Program.GetInt(st, "delay", 0) == 1;
            OptClip = Program.GetInt(st, "clipboard", 0) == 1;
            OptCursor = Program.GetInt(st, "cursor", 0) == 1;
            SetCompact(Program.GetInt(st, "compact", 0) == 1);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Point pos = new Point(Program.GetInt(st, "x", wa.Right - Width - Program.D(24)),
                                  Program.GetInt(st, "y", wa.Top + Program.D(24)));
            bool visible = false;
            foreach (Screen s in Screen.AllScreens)
                if (s.WorkingArea.IntersectsWith(new Rectangle(pos, new Size(Program.D(40), Program.D(40))))) visible = true;
            if (!visible) pos = new Point(wa.Right - Width - Program.D(24), wa.Top + Program.D(24));
            Location = pos;
            Place.Restore(st, "capwin", this, false);   // 前回終了したディスプレイに出す

            tip.SetToolTip(tSet, "設定(3秒後・カーソル・アイコンのみ・クリップボード、ショートカットキー、保存先)");
            tip.SetToolTip(tBrowse, "キャプチャ一覧と編集画面(ブラウズ画面)を開く");
            tip.SetToolTip(this, "左端のつまみをドラッグで移動、ダブルクリックでアイコンのみ表示に切り替え\n右クリックでメニュー(システムトレイにしまう・終了など)。Esc でもシステムトレイへ");
            BuildTray();
        }

        // ---------- システムトレイ ----------
        NotifyIcon tray;
        bool trayTipShown;

        void BuildTray()
        {
            tray = new NotifyIcon();
            tray.Icon = Program.AppIcon ?? SystemIcons.Application;
            tray.Text = "SimpleCap(クリックで表示)";
            ContextMenuStrip m = new ContextMenuStrip();
            m.Font = Program.UiFont;
            // 左の縦帯を青くして、アプリ名とバージョンを縦書きで出す
            m.Renderer = new BannerMenuRenderer("SimpleCap " + Program.VersionText);
            m.Items.Add("📷 キャプチャ", null, delegate { StartCapture(false); });
            m.Items.Add("📜 スクロールキャプチャ", null, delegate { StartScroll(); });
            ToolStripItem miVideo = m.Items.Add("🎥 ビデオ録画", null, delegate { ToggleVideo(); });
            m.Opening += delegate { miVideo.Text = recorder != null ? "■ ビデオ録画を停止" : "🎥 ビデオ録画"; };
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("🎯 キャプチャ画面を表示", null, delegate { ShowCaptureFromTray(); });
            m.Items.Add("🗂️ ブラウズ画面を表示", null, delegate { ShowBrowser(); });
            m.Items.Add("⚙️ 設定", null, delegate { SettingsWindow.Open(null); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("✖ 終了", null, delegate { quitting = true; Close(); });
            tray.ContextMenuStrip = m;
            tray.MouseDoubleClick += delegate (object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowCaptureFromTray(); };   // ダブルクリック → キャプチャ画面
            tray.MouseClick += delegate (object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                if (OptTray) ShowBrowser(); else ShowCaptureOnly();   // トレイ常駐モードではブラウズ画面を開く
            };
            tray.Visible = true;
        }

        // キャプチャウィンドウをシステムトレイにしまう
        public void HideToTray()
        {
            Hide();
            if (!trayTipShown)
            {
                trayTipShown = true;
                string hk = hotkeyText != "" ? hotkeyText + " で撮影、" : "";
                tray.ShowBalloonTip(3000, "SimpleCap", "システムトレイに入りました。" + hk + "アイコンをクリックで表示します。", ToolTipIcon.Info);
            }
        }

        // 最小化ボタンでもシステムトレイへ
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
                HideToTray();
            }
        }

        // アイコンのみ(ラベル非表示)の小さな表示に切り替え
        public void SetCompact(bool on)
        {
            compact = on;
            Point keep = Location;
            int w = on ? Program.D(34) : Program.D(50), h = on ? Program.D(32) : Program.D(46);
            int x = GripWidth;   // 左端はドラッグ用のつまみ
            foreach (Tile t in tiles)
            {
                t.Compact = on;
                int tw = w;
                if (!on)
                    foreach (string cap in t == tCap ? ModeCaption : new string[] { t.Caption })   // キャプチャは撮り方が変わっても幅が変わらないように
                        tw = Math.Max(tw, TextRenderer.MeasureText(cap, Program.SmallFont).Width + Program.D(8));
                if (t.Split) tw += t.SplitWidth;
                t.Size = new Size(tw, h);
                t.Location = new Point(x, Program.D(on ? 3 : 5));
                t.Invalidate();
                x += tw + Program.D(2);
            }
            ClientSize = new Size(x + Program.D(on ? 2 : 3), h + Program.D(on ? 6 : 10));
            Location = keep;
            FitWindow();
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                if (delayTimer != null && delayTimer.Enabled) CancelDelay();   // カウントダウン中なら中止
                else HideToTray();                                             // Esc でシステムトレイへ
                return;
            }
            base.OnKeyDown(e);
        }

        void CancelDelay()
        {
            delayTimer.Stop(); delayTimer.Dispose(); delayTimer = null;
            tCap.Caption = ModeCaption[capMode]; tCap.Badge = ""; tCap.Invalidate();
            busy = false;
        }

        // ---------- 枠のないウィンドウの見た目と操作 ----------
        ContextMenuStrip winMenu;
        int GripWidth { get { return Program.D(compact ? 10 : 13); } }   // 左端のドラッグ用つまみの幅

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; }   // CS_DROPSHADOW:影を付ける
        }

        // Windows 11:角を丸くし、細い枠線を付ける(Windows 10 では自分で枠を描く)
        bool dwmRound;
        internal static bool RoundCorners(IntPtr h)
        {
            try
            {
                int round = 2;   // DWMWCP_ROUND
                if (Native.DwmSetWindowAttribute(h, 33, ref round, 4) != 0) return false;
                int border = 0x00CFCAC8;   // 枠の色(COLORREF = 0x00BBGGRR)
                Native.DwmSetWindowAttribute(h, 34, ref border, 4);
                return true;
            }
            catch { return false; }
        }

        // 枠なしでも Windows の最小幅に広げられないよう、大きさを直接指定する
        void FitWindow()
        {
            if (!IsHandleCreated) return;
            MoveWindow(Handle, Left, Top, ClientSize.Width, ClientSize.Height, true);
        }
        protected override void OnShown(EventArgs e) { base.OnShown(e); FitWindow(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 左端のつまみ(点 2 列 × 3 段)
            float d = Math.Max(2f, Program.D(2)), gx = GripWidth / 2f - d - 0.5f, gy = ClientSize.Height / 2f - d * 3.5f;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(170, 170, 176)))
                for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 2; c++)
                        g.FillEllipse(b, gx + c * d * 2.2f, gy + r * d * 2.5f, d, d);
            if (!dwmRound)
                using (Pen p = new Pen(Color.FromArgb(200, 200, 205))) g.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        ContextMenuStrip BuildWindowMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Font = Program.UiFont;
            ToolStripMenuItem miCompact = new ToolStripMenuItem("🔹 アイコンのみで表示", null, delegate
            {
                SetCompact(!compact);
                Dictionary<string, string> st = Program.LoadSettings();
                st["compact"] = compact ? "1" : "0";
                Program.SaveSettings(st);
            });
            m.Items.Add(miCompact);
            m.Items.Add("📥 システムトレイにしまう (Esc)", null, delegate { HideToTray(); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("🗂️ ブラウズ画面を表示", null, delegate { ShowBrowser(); });
            m.Items.Add("⚙️ 設定", null, delegate { SettingsWindow.Open(this); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("✖ 終了", null, delegate { Quit(); });
            m.Opening += delegate { miCompact.Checked = compact; };
            return m;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            dwmRound = RoundCorners(Handle);
            RegisterHotkeys();
            if (OptTray) ShowTrayModeTip();
        }

        // トレイ常駐モードではキャプチャウィンドウを表示しない(起動時も)
        // トレイから「キャプチャ画面を表示」:トレイ常駐モードでも、このときだけは表示する
        bool allowShow;
        public void ShowCaptureFromTray()
        {
            allowShow = true;
            ShowCaptureOnly();
        }

        protected override void SetVisibleCore(bool value)
        {
            if (!value) allowShow = false;   // 隠したら、また常駐モードに戻る
            if (value && OptTray && !allowShow)
            {
                if (!IsHandleCreated) CreateHandle();   // ホットキーを受けるためハンドルだけ作る
                value = false;
            }
            base.SetVisibleCore(value);
        }

        void ShowTrayModeTip()
        {
            if (tray == null) return;
            string msg = "システムトレイで待機中です。";
            if (hotkeyText != "") msg += "\n" + hotkeyText + " でキャプチャ";
            if (browseHotkeyText != "") msg += "\n" + browseHotkeyText + " でブラウズ画面";
            msg += "\nアイコンをクリックでブラウズ画面を開きます。";
            tray.ShowBalloonTip(3000, "SimpleCap", msg, ToolTipIcon.Info);
        }

        // 設定で「システムトレイに入れる」を切り替えたとき
        public void ApplyTrayMode()
        {
            if (OptTray)
            {
                Hide();
                ShowTrayModeTip();
            }
            else if (!(Program.BrowserExists && Program.Browser.Visible && Program.Browser.WindowState != FormWindowState.Minimized))
                ShowCaptureOnly();
        }

        // ---------- ホットキー ----------
        public const Keys DefaultCaptureKey = Keys.PrintScreen;
        public const Keys DefaultBrowseKey = Keys.Control | Keys.PrintScreen;
        string browseHotkeyText = "";
        public const Keys DefaultVideoKey = Keys.Control | Keys.Shift | Keys.PrintScreen;
        string videoHotkeyText = "";

        public static Keys LoadKey(string name, Keys def)
        {
            string v;
            int k;
            if (Program.LoadSettings().TryGetValue("hotkey." + name, out v) && int.TryParse(v, out k)) return (Keys)k;
            return def;
        }

        public static string KeyText(Keys k)
        {
            if (k == Keys.None) return "(なし)";
            List<string> parts = new List<string>();
            if ((k & Keys.Control) != 0) parts.Add("Ctrl");
            if ((k & Keys.Shift) != 0) parts.Add("Shift");
            if ((k & Keys.Alt) != 0) parts.Add("Alt");
            Keys code = k & Keys.KeyCode;
            string n = code.ToString();
            if (code >= Keys.D0 && code <= Keys.D9) n = n.Substring(1);
            if (code == Keys.PrintScreen) n = "PrintScreen";
            parts.Add(n);
            return string.Join("+", parts.ToArray());
        }

        static bool Reg(IntPtr h, int id, Keys k)
        {
            if (k == Keys.None) return true;
            uint mod = 0x4000; // MOD_NOREPEAT
            if ((k & Keys.Alt) != 0) mod |= 1;
            if ((k & Keys.Control) != 0) mod |= 2;
            if ((k & Keys.Shift) != 0) mod |= 4;
            try { return Native.RegisterHotKey(h, id, mod, (uint)(k & Keys.KeyCode)); } catch { return false; }
        }

        public void UnregisterHotkeys()
        {
            try { Native.UnregisterHotKey(Handle, 1); Native.UnregisterHotKey(Handle, 2); Native.UnregisterHotKey(Handle, 3); Native.UnregisterHotKey(Handle, 4); } catch { }
        }

        // 設定のホットキーを登録。失敗したものの説明を返す
        public List<string> RegisterHotkeys()
        {
            UnregisterHotkeys();
            List<string> fails = new List<string>();
            Keys cap = LoadKey("capture", DefaultCaptureKey);
            hotkeyText = "";
            if (Reg(Handle, 1, cap)) hotkeyText = cap == Keys.None ? "" : KeyText(cap);
            else if (cap == DefaultCaptureKey && Reg(Handle, 1, Keys.Control | Keys.Shift | Keys.X)) hotkeyText = "Ctrl+Shift+X";
            else fails.Add("キャプチャ: " + KeyText(cap));
            Keys br = LoadKey("browse", DefaultBrowseKey);
            browseHotkeyText = "";
            if (Reg(Handle, 3, br)) browseHotkeyText = br == Keys.None ? "" : KeyText(br);
            else fails.Add("ブラウズ画面: " + KeyText(br));
            Keys vk = LoadKey("video", DefaultVideoKey);
            videoHotkeyText = "";
            if (Reg(Handle, 4, vk)) videoHotkeyText = vk == Keys.None ? "" : KeyText(vk);
            else fails.Add("ビデオ録画: " + KeyText(vk));
            tip.SetToolTip(tBrowse, "キャプチャ一覧と編集画面(ブラウズ画面)を開く" + (browseHotkeyText != "" ? "\nホットキー: " + browseHotkeyText : ""));
            UpdateCapTip();
            return fails;
        }
        public string CaptureHotkeyText { get { return hotkeyText; } }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0084)   // WM_NCHITTEST:ボタン以外の所(左端のつまみと余白)はタイトルバー扱い → ドラッグで移動できる
            {
                base.WndProc(ref m);
                if (m.Result.ToInt32() == 1) m.Result = (IntPtr)2;   // HTCLIENT → HTCAPTION
                return;
            }
            if (m.Msg == 0x00A5) { winMenu.Show(Cursor.Position); return; }   // WM_NCRBUTTONUP:つまみ・余白の右クリック → メニュー
            if (m.Msg == 0x00A3 && m.WParam.ToInt32() == 2) { SetCompact(!compact); return; }   // つまみのダブルクリック
            if (m.Msg == 0x0112 && (m.WParam.ToInt32() & 0xFFF0) == 0xF020) { HideToTray(); return; }   // 最小化ボタン → システムトレイ
            if (m.Msg == 0x0312) // WM_HOTKEY
            {
                int id = m.WParam.ToInt32();
                if (id == 1) { StartCapture(false); return; }
                if (id == 3) { ShowBrowser(); return; }
                if (id == 4) { ToggleVideo(); return; }
            }
            base.WndProc(ref m);
        }

        bool quitting;   // トレイの「✖ 終了」から終了する
        public void Quit() { quitting = true; Close(); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 「システムトレイに入れる」設定なら × では終了せず、トレイにしまう(終了はトレイの右クリック →「✖ 終了」)
            if (OptTray && !quitting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            if (recorder != null) { recorder.Stop(); recorder = null; if (recBar != null) recBar.Close(); }   // 録画中なら止めて保存
            if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }   // トレイにアイコンが残らないように
            Program.Exiting = true;
            UnregisterHotkeys();
            Dictionary<string, string> st = Program.LoadSettings();
            st["x"] = Location.X.ToString(); st["y"] = Location.Y.ToString();
            Place.Save(st, "capwin", this, false);
            st["delay"] = OptDelay ? "1" : "0";
            st["clipboard"] = OptClip ? "1" : "0";
            st["cursor"] = OptCursor ? "1" : "0";
            st["compact"] = compact ? "1" : "0";
            Program.SaveSettings(st);
            if (Program.BrowserExists) { Program.Browser.SaveCurrent(); Program.Browser.Close(); }
            base.OnFormClosing(e);
        }

        // ブラウズ画面だけを表示(キャプチャウィンドウは隠す)
        public void ShowBrowser()
        {
            BrowseWindow b = Program.Browser;
            if (!b.Visible) b.Show();
            if (b.WindowState == FormWindowState.Minimized) b.WindowState = FormWindowState.Normal;
            b.Activate();
            Hide();
        }

        // キャプチャウィンドウだけを表示(ブラウズ画面は隠す)
        public void ShowCaptureOnly()
        {
            if (Program.BrowserExists && Program.Browser.Visible) { Program.Browser.SaveCurrent(); Program.Browser.Hide(); }
            Opacity = 1;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        public void StartCapture(bool full)
        {
            if (busy) return;
            busy = true;
            pendingFull = full;
            if (OptDelay)
            {
                countdown = 3;
                tCap.Caption = "あと 3"; tCap.Badge = "3"; tCap.Invalidate();
                delayTimer = new System.Windows.Forms.Timer();
                delayTimer.Interval = 1000;
                delayTimer.Tick += delegate
                {
                    countdown--;
                    if (countdown <= 0)
                    {
                        delayTimer.Stop(); delayTimer.Dispose(); delayTimer = null;
                        tCap.Caption = ModeCaption[capMode]; tCap.Badge = ""; tCap.Invalidate();
                        DoCapture(pendingFull);
                    }
                    else { tCap.Caption = "あと " + countdown; tCap.Badge = countdown.ToString(); tCap.Invalidate(); }
                };
                delayTimer.Start();
            }
            else DoCapture(full);
        }

        void DoCapture(bool full)
        {
            Bitmap result = null;
            bool browserWasVisible = Program.BrowserExists && Program.Browser.Visible;
            try
            {
                // 自分のウィンドウを一瞬で隠す(アニメーション無し)
                Opacity = 0;
                if (browserWasVisible) Program.Browser.Opacity = 0;
                Application.DoEvents();
                Thread.Sleep(60);

                Rectangle vs = SystemInformation.VirtualScreen;
                using (Bitmap shot = Grab(vs, OptCursor))
                {
                    if (full)
                    {
                        Rectangle m = Screen.FromPoint(Cursor.Position).Bounds;
                        m.Intersect(vs);
                        m.Offset(-vs.X, -vs.Y);
                        result = shot.Clone(m, PixelFormat.Format32bppArgb);
                    }
                    else
                    {
                        List<Native.WinInfo> wins = Native.GetWindows();
                        using (Overlay ov = new Overlay(shot, vs, wins))
                        {
                            if (ov.ShowDialog() == DialogResult.OK)
                            {
                                Rectangle r = ov.ResultRect;
                                r.Intersect(vs);
                                if (r.Width > 0 && r.Height > 0)
                                {
                                    Rectangle rr = r; rr.Offset(-vs.X, -vs.Y);
                                    result = shot.Clone(rr, PixelFormat.Format32bppArgb);
                                    if (ov.ResultHwnd != IntPtr.Zero && !Native.IsZoomed(ov.ResultHwnd)
                                        && r == ov.ResultRect)
                                    {
                                        int dpi = 96;
                                        try { dpi = (int)Native.GetDpiForWindow(ov.ResultHwnd); } catch { }
                                        if (dpi <= 0) dpi = 96;
                                        RoundCorners(result, 8f * dpi / 96f);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("キャプチャに失敗しました: " + ex.Message, "SimpleCap");
            }
            finally
            {
                Opacity = 1;
                if (browserWasVisible) Program.Browser.Opacity = 1;
                busy = false;
            }
            if (result != null)
            {
                // キャプチャ後はブラウズ画面だけにする
                result.SetResolution(96, 96);
                if (OptClip)
                {
                    try { BrowseWindow.SetClipboardImage(result); }   // 透明(角)は PNG 形式で保持、他アプリ向けに白背景のビットマップも
                    catch (Exception ex) { MessageBox.Show("クリップボードにコピーできませんでした: " + ex.Message, "SimpleCap"); }
                }
                BrowseWindow b = Program.Browser;
                b.AddCapture(result);
                ShowBrowser();
            }
        }

        // ---------- ビデオ録画 ----------
        VideoRecorder recorder;
        RecordBar recBar;
        public bool IsRecording { get { return recorder != null; } }

        public void ToggleVideo()
        {
            if (recorder != null) StopVideo(); else StartVideo();
        }

        void StartVideo()
        {
            if (busy) return;
            busy = true;
            bool browserWasVisible = Program.BrowserExists && Program.Browser.Visible;
            Rectangle area = Rectangle.Empty;
            try
            {
                Opacity = 0;
                if (browserWasVisible) Program.Browser.Opacity = 0;
                Application.DoEvents();
                Thread.Sleep(60);
                Rectangle vs = SystemInformation.VirtualScreen;
                using (Bitmap shot = Grab(vs, false))
                using (Overlay ov = new Overlay(shot, vs, Native.GetWindows()))
                {
                    if (ov.ShowDialog() == DialogResult.OK)
                    {
                        Rectangle r = ov.ResultRect;
                        r.Intersect(vs);
                        area = r;
                    }
                }
            }
            catch (Exception ex) { MessageBox.Show("録画を開始できませんでした: " + ex.Message, "SimpleCap"); }
            if (area.Width < 8 || area.Height < 8)
            {
                Opacity = 1;
                if (browserWasVisible) Program.Browser.Opacity = 1;
                busy = false;
                return;
            }
            // 録画中は自分とブラウズ画面を隠す
            Hide(); Opacity = 1;
            if (browserWasVisible) { Program.Browser.Hide(); Program.Browser.Opacity = 1; }
            try
            {
                Program.EnsureLibDirChosen(null);
                recorder = new VideoRecorder(area, OptCursor);
                recBar = new RecordBar(recorder, area, videoHotkeyText);
                recBar.StopClicked += delegate { StopVideo(); };
                recBar.Show();
                int cd = Program.GetInt(Program.LoadSettings(), "video.countdown", 0);
                if (cd > 0) StartVideoCountdown(cd, area);
                else recorder.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show("録画を開始できませんでした: " + ex.Message, "SimpleCap");
                recorder = null;
                if (recBar != null) { recBar.Close(); recBar = null; }
                busy = false;
                if (OptTray) ShowBrowser(); else ShowCaptureOnly();
            }
        }

        [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);

        // ---------- ▼ のドロップダウン(スクロール / ビデオ) ----------
        ModeMenu dropMenu;

        // キャプチャの ⌄:撮り方を選ぶメニュー
        void ShowDropMenu()
        {
            if (dropMenu != null) { dropMenu.Close(); return; }
            List<ModeMenu.Item> items = new List<ModeMenu.Item>();
            // 選んだ撮り方を覚えて(次回もボタン本体で使う)、すぐに撮る
            items.Add(new ModeMenu.Item("📷", "画面・範囲", hotkeyText, delegate { SetMode(0); StartCapture(false); }));
            items.Add(new ModeMenu.Item("📜", "スクロール", "", delegate { SetMode(1); StartScroll(); }));
            if (recorder != null) items.Add(new ModeMenu.Item("⏹️", "録画を停止", videoHotkeyText, delegate { SetMode(2); ToggleVideo(); }));
            else items.Add(new ModeMenu.Item("🎥", "ビデオ録画", videoHotkeyText, delegate { SetMode(2); ToggleVideo(); }));
            ModeMenu f = new ModeMenu(items, capMode);
            f.Picked += delegate (Action act) { DropPick(act); };
            Point pt = PointToScreen(new Point(tCap.Left, tCap.Bottom + Program.D(4)));
            Rectangle wa = Screen.FromPoint(pt).WorkingArea;
            if (pt.Y + f.Height > wa.Bottom) pt.Y = PointToScreen(new Point(0, tCap.Top)).Y - f.Height - Program.D(4);   // 下に入らなければ上に
            if (pt.X + f.Width > wa.Right) pt.X = wa.Right - f.Width;
            f.Location = pt;
            f.FormClosed += delegate { dropMenu = null; };
            dropMenu = f;
            // Windows のウィンドウの最小幅で右側に余白が出ないよう、表示後に大きさを直接指定する
            Size want = f.Size;
            Point loc = f.Location;
            f.Shown += delegate { MoveWindow(f.Handle, loc.X, loc.Y, want.Width, want.Height, true); };
            f.Show(this);
            f.Activate();
        }

        // ボタン本体:いま選ばれている撮り方で撮る
        void RunMode()
        {
            if (capMode == 1) StartScroll();
            else if (capMode == 2) ToggleVideo();
            else StartCapture(false);
        }

        // 撮り方を切り替えて覚える
        void SetMode(int m)
        {
            if (m == capMode) return;
            capMode = m;
            ApplyMode();
            Dictionary<string, string> st = Program.LoadSettings();
            st["capmode"] = m.ToString();
            Program.SaveSettings(st);
        }

        // ボタンの絵・名前・色を撮り方に合わせる
        void ApplyMode()
        {
            tCap.Emoji = ModeEmoji[capMode];
            if (tCap.Badge == "") tCap.Caption = ModeCaption[capMode];
            tCap.Accent = ModeColor[capMode];
            tCap.Invalidate();
            UpdateCapTip();
        }

        void UpdateCapTip()
        {
            string t = capMode == 1 ? "スクロールしながら縦に長い範囲をキャプチャ\nクリック=ウィンドウ、ドラッグ=範囲を選ぶ → ゆっくり下へスクロール →「■ キャプチャ終了」"
                     : capMode == 2 ? "画面をビデオで録画(15fps)\nクリック=ウィンドウ、ドラッグ=範囲を選んで開始。もう一度押すか「■ 停止」で終了"
                     : "範囲 / ウィンドウをキャプチャ\nクリック=ウィンドウ、ドラッグ=範囲";
            string hk = capMode == 2 ? videoHotkeyText : capMode == 0 ? hotkeyText : "";
            if (hk != "") t += "\nホットキー: " + hk;
            t += "\n右端の ⌄ で撮り方を切り替え(選んだものを次回も使います)";
            tip.SetToolTip(tCap, t);
        }

        void DropPick(Action a)
        {
            if (dropMenu != null) dropMenu.Close();
            BeginInvoke(a);   // メニューが消えてから開始(撮影に写らないように)
        }

        // ---------- スクロールキャプチャ ----------
        ScrollBarForm scrollBar;

        public void StartScroll()
        {
            if (busy || recorder != null || scrollBar != null) return;
            busy = true;
            bool browserWasVisible = Program.BrowserExists && Program.Browser.Visible;
            Rectangle area = Rectangle.Empty;
            try
            {
                Opacity = 0;
                if (browserWasVisible) Program.Browser.Opacity = 0;
                Application.DoEvents();
                Thread.Sleep(60);
                Rectangle vs = SystemInformation.VirtualScreen;
                using (Bitmap shot = Grab(vs, false))
                using (Overlay ov = new Overlay(shot, vs, Native.GetWindows()))
                {
                    if (ov.ShowDialog() == DialogResult.OK)
                    {
                        Rectangle r = ov.ResultRect;
                        r.Intersect(vs);
                        area = r;
                    }
                }
            }
            catch (Exception ex) { MessageBox.Show("スクロールキャプチャを開始できませんでした: " + ex.Message, "SimpleCap"); }
            if (area.Width < 16 || area.Height < 32)
            {
                Opacity = 1;
                if (browserWasVisible) Program.Browser.Opacity = 1;
                busy = false;
                return;
            }
            // キャプチャ中は自分とブラウズ画面を隠す(スクロールする画面を操作できるように)
            Hide(); Opacity = 1;
            if (browserWasVisible) { Program.Browser.Hide(); Program.Browser.Opacity = 1; }
            scrollBar = new ScrollBarForm(area);
            scrollBar.Finished += delegate
            {
                Bitmap result = null;
                try { result = scrollBar.TakeResult(); }
                catch (Exception ex) { MessageBox.Show("画像をつなげられませんでした: " + ex.Message, "SimpleCap"); }
                scrollBar = null;
                busy = false;
                if (result == null) { if (OptTray) ShowBrowser(); else ShowCaptureOnly(); return; }
                result.SetResolution(96, 96);
                if (OptClip)
                {
                    try { BrowseWindow.SetClipboardImage(result); }
                    catch (Exception ex) { MessageBox.Show("クリップボードにコピーできませんでした: " + ex.Message, "SimpleCap"); }
                }
                Program.Browser.AddCapture(result);
                ShowBrowser();
            };
            scrollBar.Show();
        }

        // ---------- 録画開始までのカウントダウン(数えている間もパソコンはふつうに操作できる) ----------
        System.Windows.Forms.Timer vcdTimer;
        CountdownForm vcdForm;
        int vcdLeft;

        void StartVideoCountdown(int sec, Rectangle area)
        {
            vcdLeft = sec;
            vcdForm = new CountdownForm(area);
            vcdForm.Number = vcdLeft;
            vcdForm.Show();
            recBar.Countdown = vcdLeft;
            vcdTimer = new System.Windows.Forms.Timer();
            vcdTimer.Interval = 1000;
            vcdTimer.Tick += delegate
            {
                vcdLeft--;
                if (vcdLeft > 0)
                {
                    vcdForm.Number = vcdLeft;
                    recBar.Countdown = vcdLeft;
                    return;
                }
                EndVideoCountdown();
                if (recorder == null) return;
                recBar.Countdown = 0;
                recorder.Start();   // 0 になったら録画開始
            };
            vcdTimer.Start();
        }

        void EndVideoCountdown()
        {
            if (vcdTimer != null) { vcdTimer.Stop(); vcdTimer.Dispose(); vcdTimer = null; }
            if (vcdForm != null) { vcdForm.Close(); vcdForm.Dispose(); vcdForm = null; }
        }

        void StopVideo()
        {
            EndVideoCountdown();   // カウントダウン中に「✖ 中止」/ ホットキー → 録画せずに取りやめ
            if (recorder == null) return;
            VideoRecorder rec = recorder;
            recorder = null;
            rec.Stop();
            if (recBar != null) { recBar.Close(); recBar.Dispose(); recBar = null; }
            busy = false;
            if (rec.Error != null) MessageBox.Show("録画中にエラーが起きました: " + rec.Error, "SimpleCap");
            if (rec.FrameCount == 0)
            {
                try { System.IO.Directory.Delete(rec.Info.Dir, true); } catch { }
                if (OptTray) ShowBrowser(); else ShowCaptureOnly();
                return;
            }
            Program.Browser.AddVideo(rec.Info.Dir);
            ShowBrowser();
        }

        static Bitmap Grab(Rectangle vs, bool withCursor)
        {
            Bitmap bmp = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppRgb);
            using (Graphics g = Graphics.FromImage(bmp))
                g.CopyFromScreen(vs.Location, Point.Empty, vs.Size, CopyPixelOperation.SourceCopy);
            if (withCursor) Native.DrawCursor(bmp, vs.Location);   // 撮影した瞬間のカーソルを描き込む
            return bmp;
        }

        // Windows 11 の丸角の外側を透明化(アンチエイリアス付き)
        public static void RoundCorners(Bitmap bmp, float rad)
        {
            int w = bmp.Width, h = bmp.Height;
            int r = (int)Math.Ceiling(rad);
            if (w < r * 2 || h < r * 2 || r < 1) return;
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int stride = bd.Stride;
                byte[] row = new byte[stride];
                for (int y = 0; y < r; y++)
                {
                    for (int pass = 0; pass < 2; pass++)
                    {
                        int yy = pass == 0 ? y : h - 1 - y;
                        IntPtr p = new IntPtr(bd.Scan0.ToInt64() + (long)yy * stride);
                        Marshal.Copy(p, row, 0, stride);
                        for (int x = 0; x < r; x++)
                        {
                            float dx = rad - (x + 0.5f), dy = rad - (y + 0.5f);
                            if (dx <= 0 || dy <= 0) continue;
                            float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                            float a = rad - dist + 0.5f;
                            if (a >= 1) continue;
                            if (a < 0) a = 0;
                            int i1 = x * 4 + 3, i2 = (w - 1 - x) * 4 + 3;
                            row[i1] = (byte)(row[i1] * a);
                            row[i2] = (byte)(row[i2] * a);
                        }
                        Marshal.Copy(row, 0, p, stride);
                    }
                }
            }
            finally { bmp.UnlockBits(bd); }
        }
    }

    // 全画面オーバーレイ:ホバーでウィンドウ強調、クリックでウィンドウ、ドラッグで範囲
    public class Overlay : Form
    {
        Bitmap shot, dim;
        Rectangle vs;
        List<Native.WinInfo> wins;
        Rectangle hover;       // クライアント座標
        IntPtr hoverHwnd;
        bool dragging;
        Point dragStart;
        Rectangle sel;
        public Rectangle ResultRect;   // スクリーン座標
        public IntPtr ResultHwnd;
        Font labelFont = new Font("Yu Gothic UI", 10f, FontStyle.Bold);
        string hint = "クリック: ウィンドウ   ドラッグ: 範囲   Esc / 右クリック: キャンセル";

        public Overlay(Bitmap shot, Rectangle vs, List<Native.WinInfo> wins)
        {
            this.shot = shot; this.vs = vs; this.wins = wins;
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

            dim = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(dim))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(shot, new Rectangle(0, 0, vs.Width, vs.Height), 0, 0, vs.Width, vs.Height, GraphicsUnit.Pixel);
                g.CompositingMode = CompositingMode.SourceOver;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(100, 0, 0, 0)))
                    g.FillRectangle(b, 0, 0, vs.Width, vs.Height);
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Bounds = vs;
            Activate();
            Point p = PointToClient(Cursor.Position);
            UpdateHover(p);
            Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x02E0) return; // WM_DPICHANGED を無視(サイズ変更させない)
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { if (dim != null) dim.Dispose(); labelFont.Dispose(); }
            base.Dispose(disposing);
        }

        Rectangle Current { get { return dragging && (sel.Width > 3 || sel.Height > 3) ? sel : hover; } }

        void UpdateHover(Point client)
        {
            Point sp = new Point(client.X + vs.X, client.Y + vs.Y);
            hoverHwnd = IntPtr.Zero;
            Rectangle r = Rectangle.Empty;
            foreach (Native.WinInfo w in wins)
                if (w.Rect.Contains(sp)) { r = w.Rect; hoverHwnd = w.Hwnd; break; }
            if (r.IsEmpty) r = Screen.FromPoint(sp).Bounds;
            r.Intersect(vs);
            r.Offset(-vs.X, -vs.Y);
            hover = r;
        }

        Rectangle LabelRect(Rectangle r)
        {
            Size sz = TextRenderer.MeasureText(r.Width + " × " + r.Height, labelFont);
            int w = sz.Width + Program.D(12), h = sz.Height + Program.D(6);
            int x = r.X, y = r.Y - h - Program.D(4);
            if (y < 0) y = r.Y + Program.D(4);
            if (x + w > vs.Width) x = vs.Width - w;
            if (x < 0) x = 0;
            return new Rectangle(x, y, w, h);
        }
        Rectangle HintRect()
        {
            Rectangle pb = Screen.PrimaryScreen.Bounds; pb.Offset(-vs.X, -vs.Y);
            Size sz = TextRenderer.MeasureText(hint, labelFont);
            int w = sz.Width + Program.D(24), h = sz.Height + Program.D(12);
            return new Rectangle(pb.X + (pb.Width - w) / 2, pb.Y + Program.D(16), w, h);
        }

        void Damage(Rectangle r)
        {
            Rectangle a = r; a.Inflate(Program.D(4), Program.D(4));
            Invalidate(a);
            Invalidate(LabelRect(r));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.None;
            Rectangle c = e.ClipRectangle;
            g.DrawImage(dim, c, c, GraphicsUnit.Pixel);
            Rectangle r = Current;
            if (!r.IsEmpty)
            {
                Rectangle bright = Rectangle.Intersect(r, c);
                if (!bright.IsEmpty) g.DrawImage(shot, bright, bright, GraphicsUnit.Pixel);
                g.CompositingMode = CompositingMode.SourceOver;
                using (Pen p = new Pen(Color.FromArgb(0, 160, 255), Program.D(2)))
                {
                    p.Alignment = PenAlignment.Inset;
                    g.DrawRectangle(p, r);
                }
                Rectangle lr = LabelRect(r);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(220, 0, 120, 215))) g.FillRectangle(b, lr);
                TextRenderer.DrawText(g, r.Width + " × " + r.Height, labelFont, lr, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            g.CompositingMode = CompositingMode.SourceOver;
            Rectangle hr = HintRect();
            if (hr.IntersectsWith(c))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(200, 30, 30, 30))) g.FillRectangle(b, hr);
                TextRenderer.DrawText(g, hint, labelFont, hr, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Rectangle old = Current;
            if (dragging)
            {
                int x1 = Math.Min(dragStart.X, e.X), y1 = Math.Min(dragStart.Y, e.Y);
                int x2 = Math.Max(dragStart.X, e.X), y2 = Math.Max(dragStart.Y, e.Y);
                sel = new Rectangle(x1, y1, x2 - x1 + 1, y2 - y1 + 1);
            }
            else UpdateHover(e.Location);
            Rectangle now = Current;
            if (now != old) { Damage(old); Damage(now); Invalidate(HintRect()); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { DialogResult = DialogResult.Cancel; return; }
            if (e.Button != MouseButtons.Left) return;
            dragging = true;
            dragStart = e.Location;
            sel = new Rectangle(e.X, e.Y, 1, 1);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!dragging || e.Button != MouseButtons.Left) return;
            dragging = false;
            if (sel.Width > 3 || sel.Height > 3)
            {
                ResultRect = new Rectangle(sel.X + vs.X, sel.Y + vs.Y, sel.Width, sel.Height);
                ResultHwnd = IntPtr.Zero;
            }
            else
            {
                ResultRect = new Rectangle(hover.X + vs.X, hover.Y + vs.Y, hover.Width, hover.Height);
                ResultHwnd = hoverHwnd;
            }
            DialogResult = DialogResult.OK;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
            base.OnKeyDown(e);
        }
    }
    // キャプチャの ⌄ で出る、撮り方を選ぶメニュー(角丸・影つき、アイコン+名前+ショートカットキー)
    public class ModeMenu : Form
    {
        public class Item
        {
            public string Icon, Text, Key; public Action Act;
            public Item(string icon, string text, string key, Action act) { Icon = icon; Text = text; Key = key; Act = act; }
        }
        readonly List<Item> items;
        int hover = -1;
        readonly int selected;   // いま選ばれている撮り方(左に青い印)
        readonly int pad, rowH;
        public event Action<Action> Picked;

        public ModeMenu(List<Item> list, int sel)
        {
            items = list;
            selected = sel;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.White;
            DoubleBuffered = true;
            KeyPreview = true;
            Font = Program.UiFont;
            Cursor = Cursors.Hand;
            pad = Program.D(6); rowH = Program.D(34);
            int tw = 0, kw = 0;
            foreach (Item it in items)
            {
                tw = Math.Max(tw, TextRenderer.MeasureText(it.Text, Font).Width);
                if (it.Key != "") kw = Math.Max(kw, TextRenderer.MeasureText(it.Key, Program.SmallFont).Width);
            }
            int w = pad * 2 + Program.D(38) + tw + (kw > 0 ? Program.D(18) + kw : 0) + Program.D(12);
            ClientSize = new Size(Math.Max(Program.D(170), w), pad * 2 + rowH * items.Count);
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; }   // 影
        }
        bool dwmRound;
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); dwmRound = CaptureWindow.RoundCorners(Handle); }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); Close(); }

        int RowAt(Point p)
        {
            if (p.X < 0 || p.X >= ClientSize.Width || p.Y < pad) return -1;
            int i = (p.Y - pad) / rowH;
            return i >= 0 && i < items.Count ? i : -1;
        }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int i = RowAt(e.Location); if (i != hover) { hover = i; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hover >= 0) { hover = -1; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            int i = RowAt(e.Location);
            if (i >= 0) Pick(i);
        }
        void Pick(int i)
        {
            Action a = items[i].Act;
            Close();
            if (Picked != null) Picked(a);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; return; }
            if (e.KeyCode == Keys.Down) { hover = (hover + 1) % items.Count; Invalidate(); e.Handled = true; return; }
            if (e.KeyCode == Keys.Up) { hover = hover <= 0 ? items.Count - 1 : hover - 1; Invalidate(); e.Handled = true; return; }
            if (e.KeyCode == Keys.Enter && hover >= 0) { Pick(hover); e.Handled = true; return; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            TextFormatFlags lf = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            TextFormatFlags rf = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            for (int i = 0; i < items.Count; i++)
            {
                Rectangle r = new Rectangle(pad, pad + i * rowH, ClientSize.Width - pad * 2, rowH);
                if (i == hover)
                    using (GraphicsPath p = Tile.RoundRect(new Rectangle(r.X, r.Y + 1, r.Width - 1, r.Height - 2), Program.D(6)))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(227, 240, 252))) g.FillPath(b, p);
                if (i == selected)
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(0, 120, 215)))
                        g.FillRectangle(b, r.X + Program.D(2), r.Y + Program.D(9), Math.Max(2, Program.D(3)), r.Height - Program.D(18));
                Item it = items[i];
                TextRenderer.DrawText(g, it.Icon, Program.EmojiFont, new Rectangle(r.X + Program.D(8), r.Y, Program.D(26), r.Height), Color.Black,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, it.Text, Font, new Rectangle(r.X + Program.D(40), r.Y, r.Width - Program.D(40), r.Height), Color.FromArgb(30, 30, 30), lf);
                if (it.Key != "")
                    TextRenderer.DrawText(g, it.Key, Program.SmallFont, new Rectangle(r.X, r.Y, r.Width - Program.D(10), r.Height), Color.FromArgb(130, 130, 136), rf);
            }
            if (!dwmRound)
                using (Pen p = new Pen(Color.FromArgb(200, 200, 205))) g.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }
    }
    // トレイの右クリックメニュー用:左の縦帯(アイコンの欄)を #71ADFF にして、下から上へアプリ名とバージョンを書く
    public class BannerMenuRenderer : ToolStripProfessionalRenderer
    {
        static readonly Color BandTop = Color.FromArgb(0xA4, 0xA4, 0xA4);      // 上:グレー
        static readonly Color BandBottom = Color.FromArgb(0x71, 0xAD, 0xFF);   // 下:青
        readonly string text;
        int bandW = 24;   // 縦帯の幅(描くときに実際の幅を覚える)

        public BannerMenuRenderer(string text) { this.text = text.Trim(); }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            Rectangle r = e.AffectedBounds;
            bandW = r.Right;
            Graphics g = e.Graphics;
            using (LinearGradientBrush b = new LinearGradientBrush(new Rectangle(r.X, r.Y - 1, r.Width, r.Height + 2), BandTop, BandBottom, LinearGradientMode.Vertical))
                g.FillRectangle(b, r);
            if (text == "" || r.Width < 6 || r.Height < 20) return;
            // 文字を図形にして、実際の外形の大きさから倍率を決める(縦帯の幅いっぱい、長さもはみ出さない)
            using (GraphicsPath p = new GraphicsPath())
            using (FontFamily ff = new FontFamily("Segoe UI"))
            {
                p.AddString(text, ff, (int)FontStyle.Bold, 100f, PointF.Empty, StringFormat.GenericTypographic);
                RectangleF tb = p.GetBounds();
                if (tb.Width <= 0 || tb.Height <= 0) return;
                float pad = Math.Max(2f, r.Width * 0.14f);           // 帯の左右の余白
                float endPad = Program.D(8);                          // 帯の下端からの余白
                float s = Math.Min((r.Width - pad * 2) / tb.Height, (r.Height - endPad * 2) / tb.Width);
                using (Matrix m = new Matrix())
                {
                    // 下から上へ読む縦書き:左下に置き、90° 左に回す
                    m.Translate(r.Left + (r.Width - tb.Height * s) / 2f, r.Bottom - endPad);
                    m.Rotate(-90);
                    m.Scale(s, s);
                    m.Translate(-tb.X, -tb.Y);
                    p.Transform(m);
                }
                SmoothingMode old = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(Brushes.White, p);
                g.SmoothingMode = old;
            }
        }

        // 選んだ項目の色付けは縦帯の右側だけ(縦帯と文字を消さない)
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            Rectangle r = new Rectangle(bandW + 1, 1, Math.Max(1, e.Item.Width - bandW - 3), e.Item.Height - 2);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(220, 235, 252))) e.Graphics.FillRectangle(b, r);
            using (Pen p = new Pen(Color.FromArgb(0x71, 0xAD, 0xFF))) e.Graphics.DrawRectangle(p, r);
        }
    }
}
