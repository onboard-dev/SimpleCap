// SimpleCap - シンプルな画面キャプチャ&注釈ツール
// 外部ライブラリなし / .NET Framework 4.x (Windows標準) の csc.exe でビルド
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SimpleCap
{
    static class Program
    {
        public static float S = 1f;              // DPI倍率
        public static string LibDir;              // キャプチャ保存先
        public static Font UiFont;

        // バージョン表示用(version.bat が src\AssemblyInfo.cs に書いた番号)。番号がなければ空文字。
        public static string VersionText
        {
            get
            {
                string v = Application.ProductVersion;
                if (string.IsNullOrEmpty(v) || v == "0.0.0.0") return "";
                return "v" + v;
            }
        }
        public static Font EmojiFont;
        public static Font SmallFont;
        public static Icon AppIcon;
        public static CaptureWindow CapWin;
        static BrowseWindow browser;
        public static bool Exiting;

        public static int D(int v) { return (int)Math.Round(v * S); }

        [STAThread]
        static void Main()
        {
            bool created;
            using (Mutex m = new Mutex(true, "SimpleCap_SingleInstance_8f3a", out created))
            {
                // 設定のインポート後の再起動:前の SimpleCap が終わるのを待ってから起動する
                if (!created && Array.IndexOf(Environment.GetCommandLineArgs(), "/restart") > 0)
                {
                    try { created = m.WaitOne(15000); } catch (AbandonedMutexException) { created = true; }
                }
                if (!created)
                {
                    IntPtr h = Native.FindWindow(null, "SimpleCap");
                    if (h != IntPtr.Zero) { Native.ShowWindow(h, 9); Native.SetForegroundWindow(h); }
                    return;
                }
                Native.SetDpiAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) S = g.DpiX / 96f;

                UiFont = new Font("Yu Gothic UI", 9f);
                SmallFont = new Font("Yu Gothic UI", 8f);
                EmojiFont = new Font("Segoe UI Emoji", 15f);
                try { AppIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

                // 設定ファイルは %APPDATA%\SimpleCap に置く(保存先フォルダを変えても設定が残るように)
                MigrateOldSettings();
                string lib;
                if (!LoadSettings().TryGetValue("libdir", out lib) || string.IsNullOrEmpty(lib)) lib = DefaultLibDir;
                try { Directory.CreateDirectory(lib); } catch { lib = DefaultLibDir; Directory.CreateDirectory(lib); }
                LibDir = lib;

                CapWin = new CaptureWindow();
                Application.Run(CapWin);
            }
        }

        public static BrowseWindow Browser
        {
            get
            {
                if (browser == null || browser.IsDisposed) browser = new BrowseWindow();
                return browser;
            }
        }
        public static bool BrowserExists { get { return browser != null && !browser.IsDisposed; } }

        public static string DefaultLibDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SimpleCap"); }
        }
        public static string SettingsFile { get { return SettingsPath; } }

        // 設定を書き出す(AI のシークレットキーは含めない)
        public static void ExportSettings(string path)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# SimpleCap の設定 (" + DateTime.Now.ToString("yyyy/MM/dd HH:mm") + " 書き出し)");
            sb.AppendLine("# AI のシークレットキーは含みません。読み込んだあと ⚙️ 設定で入力してください。");
            foreach (KeyValuePair<string, string> kv in LoadSettings())
            {
                if (kv.Key.StartsWith("ai.") && kv.Key.EndsWith(".key")) continue;
                sb.AppendLine(kv.Key + "=" + kv.Value);
            }
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        // 設定を読み込む(今の設定に上書き)。読み込んだ項目数を返す
        public static int ImportSettings(string path)
        {
            Dictionary<string, string> cur = LoadSettings();
            int n = 0;
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                string t = line.Trim();
                if (t == "" || t.StartsWith("#")) continue;
                int i = t.IndexOf('=');
                if (i <= 0) continue;
                string k = t.Substring(0, i).Trim(), v = t.Substring(i + 1).Trim();
                if (k.StartsWith("ai.") && k.EndsWith(".key")) continue;          // キーは別のパソコンでは使えないので読まない
                if (k == "libdir" && !Directory.Exists(v)) continue;              // 保存先フォルダが無いときは今のまま
                cur[k] = v;
                n++;
            }
            if (n == 0) throw new Exception("SimpleCap の設定が見つかりませんでした。");
            SaveSettings(cur);
            return n;
        }

        // 再起動(設定のインポート後)
        public static void Restart()
        {
            FreezeSettings = true;   // 終了時の書き込みで、読み込んだ設定を上書きしない
            try { System.Diagnostics.Process.Start(Application.ExecutablePath, "/restart"); } catch { FreezeSettings = false; return; }
            CapWin.Quit();
        }

        static string SettingsPath
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SimpleCap");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "settings.ini");
            }
        }
        // 以前の版(保存先フォルダ内の settings.ini)からの引き継ぎ
        static void MigrateOldSettings()
        {
            try
            {
                string oldP = Path.Combine(DefaultLibDir, "settings.ini");
                if (File.Exists(oldP) && !File.Exists(SettingsPath))
                {
                    File.Copy(oldP, SettingsPath);
                    Dictionary<string, string> d = LoadSettings();
                    if (!d.ContainsKey("libdir")) { d["libdir"] = DefaultLibDir; SaveSettings(d); }   // 既存ユーザーは今の場所のまま
                }
            }
            catch { }
        }

        // 保存先がまだ決まっていなければ(初回キャプチャ時)フォルダを選んでもらう
        public static void EnsureLibDirChosen(IWin32Window owner)
        {
            Dictionary<string, string> d = LoadSettings();
            if (d.ContainsKey("libdir")) return;
            string chosen = AskFolder(owner, "キャプチャの保存先フォルダを選んでください。\n(あとから ⚙️ 設定 で変更できます)", LibDir);
            if (chosen == null) chosen = LibDir;
            d = LoadSettings();
            d["libdir"] = chosen;
            SaveSettings(d);
            if (!string.Equals(chosen, LibDir, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(chosen);
                LibDir = chosen;
                if (BrowserExists) Browser.ReloadLibrary();
            }
        }

        public static string AskFolder(IWin32Window owner, string description, string initial)
        {
            using (FolderBrowserDialog fd = new FolderBrowserDialog())
            {
                fd.Description = description;
                fd.ShowNewFolderButton = true;
                try { Directory.CreateDirectory(initial); fd.SelectedPath = initial; } catch { }
                if (fd.ShowDialog(owner) == DialogResult.OK && !string.IsNullOrEmpty(fd.SelectedPath)) return fd.SelectedPath;
            }
            return null;
        }

        // 簡易設定ファイル (key=value)
        public static Dictionary<string, string> LoadSettings()
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            try
            {
                string p = SettingsPath;
                if (File.Exists(p))
                    foreach (string line in File.ReadAllLines(p, Encoding.UTF8))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                    }
            }
            catch { }
            return d;
        }
        public static bool FreezeSettings;   // true の間は設定を書き込まない(インポート直後の再起動で上書きしないように)

        public static void SaveSettings(Dictionary<string, string> d)
        {
            if (FreezeSettings) return;
            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, string> kv in d) sb.AppendLine(kv.Key + "=" + kv.Value);
                File.WriteAllText(SettingsPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
        public static int GetInt(Dictionary<string, string> d, string k, int def)
        {
            string v; int r;
            if (d.TryGetValue(k, out v) && int.TryParse(v, out r)) return r;
            return def;
        }
    }

    // ---- Win32 ----
    public static class Native
    {
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);
        [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int v);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        public static void SetDpiAware()
        {
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch { } // PerMonitorV2
            try { if (SetProcessDpiAwareness(2) == 0) return; } catch { }
            try { SetProcessDPIAware(); } catch { }
        }

        public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string n);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);

        // マウスカーソルの取得と描画
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        public struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT pt; }
        [StructLayout(LayoutKind.Sequential)]
        public struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }
        [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO ci);
        [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr h, out ICONINFO ii);
        [DllImport("user32.dll")] public static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, int step, IntPtr brush, int flags);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);

        // 画像にマウスカーソルを描き込む (origin = 画像左上のスクリーン座標)
        public static void DrawCursor(Bitmap bmp, Point origin)
        {
            try
            {
                CURSORINFO ci = new CURSORINFO();
                ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
                if (!GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero) return;
                int hx = 0, hy = 0;
                ICONINFO ii;
                if (GetIconInfo(ci.hCursor, out ii))
                {
                    hx = ii.xHotspot; hy = ii.yHotspot;
                    if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                    if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                }
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    try { DrawIconEx(hdc, ci.pt.X - hx - origin.X, ci.pt.Y - hy - origin.Y, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3); }
                    finally { g.ReleaseHdc(hdc); }
                }
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int L, T, R, B; }

        public class WinInfo { public IntPtr Hwnd; public Rectangle Rect; }

        // Zオーダー順(手前→奥)に可視トップレベルウィンドウを列挙
        public static List<WinInfo> GetWindows()
        {
            List<WinInfo> list = new List<WinInfo>();
            uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            StringBuilder sb = new StringBuilder(256);
            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                if (!IsWindowVisible(h) || IsIconic(h)) return true;
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (pid == myPid) return true;
                int ex = GetWindowLong(h, -20);
                if ((ex & 0x20) != 0) return true; // WS_EX_TRANSPARENT (クリック透過オーバーレイ)
                int cloaked = 0;
                try { DwmGetWindowAttribute(h, 14, out cloaked, 4); } catch { }
                if (cloaked != 0) return true;
                sb.Length = 0; GetClassName(h, sb, 256);
                string cls = sb.ToString();
                if (cls == "Progman" || cls == "WorkerW") return true;
                RECT r;
                bool ok = false;
                try { ok = DwmGetWindowAttribute(h, 9, out r, 16) == 0; } catch { r = new RECT(); }
                if (!ok) GetWindowRect(h, out r);
                Rectangle rc = Rectangle.FromLTRB(r.L, r.T, r.R, r.B);
                if (rc.Width < 2 || rc.Height < 2) return true;
                WinInfo w = new WinInfo(); w.Hwnd = h; w.Rect = rc;
                list.Add(w);
                return true;
            }, IntPtr.Zero);
            return list;
        }
    }
}
