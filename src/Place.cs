using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SimpleCap
{
    // ウィンドウの位置を「どのディスプレイの、どこか」で覚える(マルチディスプレイ対応)
    public static class Place
    {
        public static void Save(Dictionary<string, string> st, string key, Form f, bool withSize)
        {
            Rectangle b = f.WindowState == FormWindowState.Normal ? f.Bounds : f.RestoreBounds;
            Screen s = Screen.FromRectangle(b);
            Rectangle wa = s.WorkingArea;
            st[key + ".screen"] = s.DeviceName;
            st[key + ".screenidx"] = Array.IndexOf(Screen.AllScreens, s).ToString();
            st[key + ".rx"] = (b.X - wa.X).ToString();
            st[key + ".ry"] = (b.Y - wa.Y).ToString();
            if (withSize)
            {
                st[key + ".w"] = b.Width.ToString();
                st[key + ".h"] = b.Height.ToString();
                st[key + ".max"] = f.WindowState == FormWindowState.Maximized ? "1" : "0";
            }
        }

        // 覚えたディスプレイ(なければ同じ番号のディスプレイ)に置く。覚えていなければ false
        public static bool Restore(Dictionary<string, string> st, string key, Form f, bool withSize)
        {
            string name;
            if (!st.TryGetValue(key + ".screen", out name)) return false;
            Screen[] all = Screen.AllScreens;
            Screen s = null;
            foreach (Screen sc in all) if (sc.DeviceName == name) s = sc;
            if (s == null)
            {
                int idx = Program.GetInt(st, key + ".screenidx", -1);
                s = idx >= 0 && idx < all.Length ? all[idx] : Screen.PrimaryScreen;
            }
            Rectangle wa = s.WorkingArea;
            Size sz = f.Size;
            if (withSize)
            {
                int w = Program.GetInt(st, key + ".w", sz.Width), h = Program.GetInt(st, key + ".h", sz.Height);
                sz = new Size(Math.Max(Program.D(400), Math.Min(wa.Width, w)), Math.Max(Program.D(300), Math.Min(wa.Height, h)));
            }
            int rx = Program.GetInt(st, key + ".rx", 0), ry = Program.GetInt(st, key + ".ry", 0);
            // 画面からはみ出さないように
            int x = Math.Max(wa.Left, Math.Min(wa.Right - Math.Min(sz.Width, wa.Width), wa.X + rx));
            int y = Math.Max(wa.Top, Math.Min(wa.Bottom - Math.Min(sz.Height, wa.Height), wa.Y + ry));
            f.StartPosition = FormStartPosition.Manual;
            f.Bounds = new Rectangle(x, y, sz.Width, sz.Height);
            if (withSize && Program.GetInt(st, key + ".max", 0) == 1) f.WindowState = FormWindowState.Maximized;
            return true;
        }
    }
}
