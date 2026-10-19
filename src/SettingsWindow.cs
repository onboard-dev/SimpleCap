using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SimpleCap
{
    // ⚙️ 設定ウィンドウ (ショートカットキーの変更)
    public class SettingsWindow : Form
    {
        class Row { public string Name; public Keys Key; public Keys Default; public TextBox Box; }
        List<Row> rows = new List<Row>();
        TextBox txtLib;
        CheckBox chkDelay, chkCursor, chkCompact, chkClip, chkTray;
        public int ImportedCount;
        // ビデオのクリック表示
        CheckBox chkFx, chkFxLabel, chkFxHold, chkFxZoom;
        ComboBox cmbFxSize, cmbFxHold, cmbFxZoom, cmbFxSpeed;
        NumericUpDown numMaxMin;
        ComboBox cmbVideoCd;   // 録画開始までのカウントダウン
        ClickFx clickFx;
        // AI で名前付け(プロバイダーごとの URL / キー / モデル)
        ComboBox cmbAi;
        TextBox txtAiUrl, txtAiKey, txtAiModel;
        Dictionary<string, string[]> aiVals = new Dictionary<string, string[]>();   // provider → {url, key, model}
        string aiShown;
        public string LibDirChoice;

        public SettingsWindow()
        {
            Text = "⚙️ 設定";
            AutoScaleMode = AutoScaleMode.None;
            Font = Program.UiFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            if (Program.AppIcon != null) Icon = Program.AppIcon;

            int x = Program.D(16), y = Program.D(14), W = Program.D(460);
            Label head = new Label();
            head.Text = "⌨️ ショートカットキー(どのアプリを使っているときでも有効)";
            head.Font = new Font("Yu Gothic UI", 10f, FontStyle.Bold);
            head.SetBounds(x, y, W - x * 2, Program.D(24));
            Controls.Add(head); y += Program.D(30);

            Label how = new Label();
            how.Text = "枠をクリックしてから、使いたいキーを押してください(Ctrl / Shift / Alt との組み合わせも可)。";
            how.ForeColor = Color.FromArgb(90, 90, 90);
            how.SetBounds(x, y, W - x * 2, Program.D(36));
            Controls.Add(how); y += Program.D(40);

            AddRow("📷 範囲 / ウィンドウ キャプチャ", "capture", CaptureWindow.DefaultCaptureKey, x, ref y, W);
            AddRow("🗂️ ブラウズ画面を表示", "browse", CaptureWindow.DefaultBrowseKey, x, ref y, W);
            AddRow("🎥 ビデオ録画 開始/停止", "video", CaptureWindow.DefaultVideoKey, x, ref y, W);

            // キャプチャの動作(以前はキャプチャウィンドウのボタンだったもの)
            y += Program.D(6);
            Label lc = new Label();
            lc.Text = "📷 キャプチャの動作";
            lc.Font = new Font("Yu Gothic UI", 10f, FontStyle.Bold);
            lc.SetBounds(x, y, W - x * 2, Program.D(24));
            Controls.Add(lc); y += Program.D(28);
            CaptureWindow cw = Program.CapWin;
            ToolTip tt = new ToolTip();
            chkDelay = Check("⏱️ 3秒後にキャプチャ", cw != null && cw.OptDelay, x, y, W);
            tt.SetToolTip(chkDelay, "メニューを開いた状態の撮影などに");
            chkCursor = Check("🖱️ マウスカーソルも写し込む", cw != null && cw.OptCursor, x + (W - x * 2) / 2, y, W);
            y += Program.D(28);
            chkClip = Check("📋 キャプチャと同時にクリップボードへコピー", cw != null && cw.OptClip, x, y, W);
            chkCompact = Check("🔹 キャプチャウィンドウをアイコンのみで表示", cw != null && cw.IsCompact, x + (W - x * 2) / 2, y, W);
            tt.SetToolTip(chkCompact, "キャプチャウィンドウの左端のつまみをダブルクリックしても切り替えられます");
            y += Program.D(28);
            chkTray = Check("📥 キャプチャウィンドウをシステムトレイに入れる(ショートカットキーだけで使う)", cw != null && cw.OptTray, x, y, W);
            chkTray.Width = W - x * 2;
            tt.SetToolTip(chkTray, "ON にするとキャプチャウィンドウは表示されません。\nキャプチャとブラウズ画面はショートカットキーで呼び出します(トレイのアイコンをクリックするとブラウズ画面)");
            y += Program.D(32);

            // ---- 🎥 ビデオのクリック表示 ----
            Label lv = new Label();
            lv.Text = "🎥 ビデオのクリック表示(「マウスカーソルも写し込む」が ON のとき)";
            lv.Font = new Font("Yu Gothic UI", 10f, FontStyle.Bold);
            lv.SetBounds(x, y, W - x * 2, Program.D(24));
            Controls.Add(lv); y += Program.D(28);
            clickFx = ClickFx.Load();
            chkFx = Check("🖱️ クリックを輪で表示する", clickFx.Enabled, x, y, W);
            tt.SetToolTip(chkFx, "左クリック・右クリック・ダブルクリックを、色の違う広がる輪で録画に描き込みます\n(ボタンを押している間はカーソルの下に丸を表示)");
            chkFxLabel = Check("🔤 「左 / 右 / ダブル」の文字も出す", clickFx.Label, x + (W - x * 2) / 2, y, W);
            y += Program.D(30);
            Label ls = new Label(); ls.Text = "大きさ";
            ls.SetBounds(x, y + Program.D(4), Program.D(50), Program.D(22)); Controls.Add(ls);
            cmbFxSize = new ComboBox(); cmbFxSize.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbFxSize.Items.AddRange(new object[] { "小", "中", "大" });
            cmbFxSize.SelectedIndex = clickFx.Size;
            cmbFxSize.SetBounds(x + Program.D(52), y, Program.D(60), Program.D(26)); Controls.Add(cmbFxSize);
            int cx = x + Program.D(124);
            foreach (int k in new int[] { 0, 1, 2 })
            {
                int kk = k;
                Button cb = new Button();
                cb.Text = k == 0 ? "左クリック" : k == 1 ? "右クリック" : "ダブル";
                cb.SetBounds(cx, y - 1, Program.D(100), Program.D(28));
                cb.BackColor = k == 0 ? clickFx.Left : k == 1 ? clickFx.Right : clickFx.Double;
                cb.Click += delegate
                {
                    using (ColorDialog cd = new ColorDialog())
                    {
                        cd.Color = cb.BackColor; cd.FullOpen = true;
                        if (cd.ShowDialog(this) != DialogResult.OK) return;
                        cb.BackColor = cd.Color;
                        if (kk == 0) clickFx.Left = cd.Color; else if (kk == 1) clickFx.Right = cd.Color; else clickFx.Double = cd.Color;
                    }
                };
                tt.SetToolTip(cb, "クリックして輪の色を変更");
                Controls.Add(cb);
                cx += Program.D(104);
            }
            y += Program.D(36);
            Label lsp = new Label(); lsp.Text = "輪の広がるスピード";
            lsp.SetBounds(x, y + Program.D(4), Program.D(130), Program.D(22)); Controls.Add(lsp);
            cmbFxSpeed = new ComboBox(); cmbFxSpeed.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbFxSpeed.Items.AddRange(new object[] { "1(遅い)", "2", "3(ふつう)", "4", "5(速い)" });
            cmbFxSpeed.SelectedIndex = Math.Max(0, Math.Min(4, clickFx.Speed - 1));
            cmbFxSpeed.SetBounds(x + Program.D(132), y, Program.D(100), Program.D(26)); Controls.Add(cmbFxSpeed);
            tt.SetToolTip(cmbFxSpeed, "輪が広がりきるまでの時間:1=3秒、2=2秒、3=1.5秒、4=1秒、5=0.6秒");
            y += Program.D(34);
            chkFxHold = Check("⏸️ マウスクリック時に静止する", clickFx.Hold, x, y, W);
            tt.SetToolTip(chkFxHold, "クリックした所の画面を、選んだ秒数だけ止めてビデオに足します(止めている間も輪は広がり続けます)\n(録画は止まりません。そのぶんビデオが長くなります)");
            Label lhs = new Label(); lhs.Text = "静止する秒数";
            lhs.SetBounds(x + (W - x * 2) / 2, y + Program.D(4), Program.D(90), Program.D(22)); Controls.Add(lhs);
            cmbFxHold = new ComboBox(); cmbFxHold.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbFxHold.Items.AddRange(new object[] { "1 秒", "2 秒", "3 秒", "4 秒", "5 秒" });
            cmbFxHold.SelectedIndex = Math.Max(0, Math.Min(4, clickFx.HoldSec - 1));
            cmbFxHold.SetBounds(x + (W - x * 2) / 2 + Program.D(92), y, Program.D(70), Program.D(26)); Controls.Add(cmbFxHold);
            cmbFxHold.Enabled = lhs.Enabled = chkFxHold.Checked;
            chkFxHold.CheckedChanged += delegate { cmbFxHold.Enabled = lhs.Enabled = chkFxHold.Checked; };
            y += Program.D(34);
            chkFxZoom = Check("🔍 クリック位置を拡大", clickFx.Zoom, x, y, W);
            tt.SetToolTip(chkFxZoom, "クリックした所を中心に画面を拡大します\n(クリックの瞬間から拡大し、静止する時点までに拡大しきって、輪が消えると元に戻ります)");
            Label lzs = new Label(); lzs.Text = "拡大率";
            lzs.SetBounds(x + (W - x * 2) / 2, y + Program.D(4), Program.D(90), Program.D(22)); Controls.Add(lzs);
            cmbFxZoom = new ComboBox(); cmbFxZoom.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbFxZoom.Items.AddRange(new object[] { "10%", "20%", "30%", "40%", "50%" });
            cmbFxZoom.SelectedIndex = Math.Max(0, Math.Min(4, clickFx.ZoomPct / 10 - 1));
            cmbFxZoom.SetBounds(x + (W - x * 2) / 2 + Program.D(92), y, Program.D(70), Program.D(26)); Controls.Add(cmbFxZoom);
            cmbFxZoom.Enabled = lzs.Enabled = chkFxZoom.Checked;
            chkFxZoom.CheckedChanged += delegate { cmbFxZoom.Enabled = lzs.Enabled = chkFxZoom.Checked; };
            y += Program.D(34);
            Label lm = new Label(); lm.Text = "⏱️ 最大録画時間";
            lm.SetBounds(x, y + Program.D(4), Program.D(110), Program.D(22)); Controls.Add(lm);
            numMaxMin = new NumericUpDown(); numMaxMin.Minimum = 1; numMaxMin.Maximum = 600;
            numMaxMin.Value = Math.Max(1, Math.Min(600, Program.GetInt(Program.LoadSettings(), "video.maxmin", 10)));
            numMaxMin.SetBounds(x + Program.D(112), y, Program.D(64), Program.D(26)); Controls.Add(numMaxMin);
            Label lm2 = new Label(); lm2.Text = "分(この時間になると録画を自動で止めます)";
            lm2.ForeColor = Color.FromArgb(90, 90, 90);
            lm2.SetBounds(x + Program.D(182), y + Program.D(4), W - x * 2 - Program.D(182), Program.D(22)); Controls.Add(lm2);
            y += Program.D(34);
            Label lcd = new Label(); lcd.Text = "⏳ ビデオキャプチャのときのカウントダウン";
            lcd.SetBounds(x, y + Program.D(4), Program.D(250), Program.D(22)); Controls.Add(lcd);
            cmbVideoCd = new ComboBox(); cmbVideoCd.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbVideoCd.Items.AddRange(new object[] { "なし", "3秒", "5秒", "10秒" });
            int vcd = Program.GetInt(Program.LoadSettings(), "video.countdown", 0);
            cmbVideoCd.SelectedIndex = vcd >= 10 ? 3 : vcd >= 5 ? 2 : vcd >= 3 ? 1 : 0;
            cmbVideoCd.SetBounds(x + Program.D(252), y, Program.D(80), Program.D(26)); Controls.Add(cmbVideoCd);
            tt.SetToolTip(cmbVideoCd, "録画する範囲を選んだあと、指定した秒数を数えてから録画を始めます\n(数えている間もパソコンはふつうに操作できます。「✖ 中止」で取りやめ)");
            y += Program.D(34);

            // 保存先フォルダ
            y += Program.D(6);
            Label lh = new Label();
            lh.Text = "📁 キャプチャの保存先フォルダ";
            lh.Font = new Font("Yu Gothic UI", 10f, FontStyle.Bold);
            lh.SetBounds(x, y, W - x * 2, Program.D(24));
            Controls.Add(lh); y += Program.D(28);
            LibDirChoice = Program.LibDir;
            txtLib = new TextBox();
            txtLib.ReadOnly = true; txtLib.BackColor = Color.White;
            txtLib.Text = LibDirChoice;
            txtLib.SetBounds(x, y, W - x * 2 - Program.D(140), Program.D(26));
            Controls.Add(txtLib);
            Button chg = new Button(); chg.Text = "変更…";
            chg.SetBounds(W - x - Program.D(134), y - 1, Program.D(64), Program.D(28));
            chg.Click += delegate
            {
                string f = Program.AskFolder(this, "キャプチャの保存先フォルダを選んでください", LibDirChoice);
                if (f != null) { LibDirChoice = f; txtLib.Text = f; }
            };
            Controls.Add(chg);
            Button open = new Button(); open.Text = "開く";
            open.SetBounds(W - x - Program.D(66), y - 1, Program.D(66), Program.D(28));
            open.Click += delegate { try { System.Diagnostics.Process.Start("explorer.exe", "\"" + LibDirChoice + "\""); } catch { } };
            Controls.Add(open);
            y += Program.D(32);
            Label ln = new Label();
            ln.Text = "※ 変更後の新しいキャプチャから、このフォルダに保存されます(今までのファイルは移動しません)。";
            ln.ForeColor = Color.FromArgb(110, 110, 110);
            ln.SetBounds(x, y, W - x * 2, Program.D(20));
            Controls.Add(ln); y += Program.D(26);

            // ---- 🤖 AI で名前付け ----
            y += Program.D(4);
            Label la = new Label();
            la.Text = "🤖 AI で名前付け(ブラウズ画面の「🏷️ 名前付け」で使います)";
            la.Font = new Font("Yu Gothic UI", 10f, FontStyle.Bold);
            la.SetBounds(x, y, W - x * 2, Program.D(24));
            Controls.Add(la); y += Program.D(28);
            Dictionary<string, string> stAi = Program.LoadSettings();
            foreach (string pv in AiNamer.Providers)
                aiVals[pv] = new string[] { AiNamer.GetUrl(stAi, pv), AiNamer.GetKey(stAi, pv), AiNamer.GetModel(stAi, pv) };
            int lw = Program.D(110), fx = x + lw, fw = W - x * 2 - lw;
            cmbAi = new ComboBox();
            cmbAi.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (string n in AiNamer.ProviderNames) cmbAi.Items.Add(n);
            AiRow("使う AI", cmbAi, x, ref y, lw, fw);
            txtAiUrl = new TextBox();
            AiRow("API の URL", txtAiUrl, x, ref y, lw, fw);
            txtAiKey = new TextBox(); txtAiKey.UseSystemPasswordChar = true;
            AiRow("シークレットキー", txtAiKey, x, ref y, lw, fw - Program.D(58));
            CheckBox showKey = new CheckBox(); showKey.Text = "表示";
            showKey.SetBounds(fx + fw - Program.D(52), y - Program.D(32) + Program.D(3), Program.D(52), Program.D(22));
            showKey.CheckedChanged += delegate { txtAiKey.UseSystemPasswordChar = !showKey.Checked; };
            Controls.Add(showKey);
            txtAiModel = new TextBox();
            AiRow("モデル", txtAiModel, x, ref y, lw, fw - Program.D(100));
            Button aiDef = new Button(); aiDef.Text = "既定に戻す";
            aiDef.SetBounds(fx + fw - Program.D(94), y - Program.D(32) - 1, Program.D(94), Program.D(28));
            aiDef.Click += delegate
            {
                string pv = AiNamer.Providers[Math.Max(0, cmbAi.SelectedIndex)];
                txtAiUrl.Text = AiNamer.DefaultUrl(pv); txtAiModel.Text = AiNamer.DefaultModel(pv);
            };
            Controls.Add(aiDef);
            cmbAi.SelectedIndexChanged += delegate { ShowAi(AiNamer.Providers[Math.Max(0, cmbAi.SelectedIndex)]); };
            cmbAi.SelectedIndex = Math.Max(0, Array.IndexOf(AiNamer.Providers, AiNamer.ActiveProvider));
            Label lk = new Label();
            lk.Text = "※ シークレットキーは、このパソコンのこのユーザーだけが読める形に暗号化して保存します。";
            lk.ForeColor = Color.FromArgb(110, 110, 110);
            lk.SetBounds(x, y, W - x * 2, Program.D(20));
            Controls.Add(lk); y += Program.D(24);

            Label note = new Label();
            note.Text = "※ PrintScreen で Snipping Tool が起動してしまう場合は、Windows の「設定 → アクセシビリティ → キーボード →\n   Print Screen キーを使用して画面キャプチャを開く」をオフにしてください。";
            note.ForeColor = Color.FromArgb(110, 110, 110);
            note.SetBounds(x, y + Program.D(4), W - x * 2, Program.D(40));
            Controls.Add(note); y += Program.D(52);

            Button ok = new Button(); ok.Text = "✔ 保存"; ok.DialogResult = DialogResult.OK;
            Button cancel = new Button(); cancel.Text = "キャンセル"; cancel.DialogResult = DialogResult.Cancel;
            ok.SetBounds(W - Program.D(200), y, Program.D(88), Program.D(30));
            cancel.SetBounds(W - Program.D(104), y, Program.D(88), Program.D(30));
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
            // 設定のエクスポート / インポート
            Button exp = new Button(); exp.Text = "📤 エクスポート…";
            Button imp = new Button(); imp.Text = "📥 インポート…";
            exp.SetBounds(x, y, Program.D(118), Program.D(30));
            imp.SetBounds(x + Program.D(122), y, Program.D(118), Program.D(30));
            ToolTip tx = new ToolTip();
            tx.SetToolTip(exp, "保存済みの設定をファイルに書き出す(別のパソコンへの引っ越しやバックアップ用)\nAI のシークレットキーは含めません");
            tx.SetToolTip(imp, "書き出した設定ファイルを読み込む(読み込んだあと SimpleCap を再起動して反映)");
            exp.Click += delegate
            {
                using (SaveFileDialog sd = new SaveFileDialog())
                {
                    sd.Filter = "SimpleCap の設定 (*.ini)|*.ini|すべてのファイル|*.*";
                    sd.FileName = "SimpleCap設定_" + DateTime.Now.ToString("yyyyMMdd") + ".ini";
                    if (sd.ShowDialog(this) != DialogResult.OK) return;
                    try
                    {
                        Program.ExportSettings(sd.FileName);
                        MessageBox.Show(this, "設定を書き出しました。\n" + sd.FileName + "\n\n※ この画面で変更してまだ「保存」していない内容は含まれません。\n※ AI のシークレットキーは含みません。", "SimpleCap");
                    }
                    catch (Exception ex) { MessageBox.Show(this, "書き出せませんでした: " + ex.Message, "SimpleCap"); }
                }
            };
            imp.Click += delegate
            {
                using (OpenFileDialog od = new OpenFileDialog())
                {
                    od.Filter = "SimpleCap の設定 (*.ini;*.txt)|*.ini;*.txt|すべてのファイル|*.*";
                    if (od.ShowDialog(this) != DialogResult.OK) return;
                    if (MessageBox.Show(this, "今の設定を、このファイルの内容で置き換えますか?\n(AI のシークレットキーは今のまま残ります)", "SimpleCap",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                    try
                    {
                        int n = Program.ImportSettings(od.FileName);
                        ImportedCount = n;
                        DialogResult = DialogResult.Abort;   // この画面の内容では上書きしない
                        Close();
                    }
                    catch (Exception ex) { MessageBox.Show(this, "読み込めませんでした: " + ex.Message, "SimpleCap"); }
                }
            };
            Controls.Add(exp); Controls.Add(imp);
            // 画面に収まらないときはスクロールできるようにする
            int needH = y + Program.D(44);
            int maxH = Screen.PrimaryScreen.WorkingArea.Height - Program.D(60);
            if (needH > maxH)
            {
                AutoScroll = true;
                ClientSize = new Size(W + SystemInformation.VerticalScrollBarWidth, maxH);
            }
            else ClientSize = new Size(W, needH);
        }

        void AiRow(string label, Control c, int x, ref int y, int lw, int fw)
        {
            Label l = new Label(); l.Text = label;
            l.SetBounds(x, y + Program.D(4), lw, Program.D(22));
            Controls.Add(l);
            c.SetBounds(x + lw, y, fw, Program.D(26));
            Controls.Add(c);
            y += Program.D(32);
        }

        // 表示中のプロバイダーの入力を覚えてから、別のプロバイダーを表示
        void ShowAi(string pv)
        {
            KeepAi();
            string[] v = aiVals[pv];
            txtAiUrl.Text = v[0]; txtAiKey.Text = v[1]; txtAiModel.Text = v[2];
            aiShown = pv;
        }
        void KeepAi()
        {
            if (aiShown != null) aiVals[aiShown] = new string[] { txtAiUrl.Text.Trim(), txtAiKey.Text.Trim(), txtAiModel.Text.Trim() };
        }

        CheckBox Check(string text, bool on, int x, int y, int W)
        {
            CheckBox c = new CheckBox();
            c.Text = text; c.Checked = on; c.AutoSize = false;
            c.SetBounds(x, y, (W - Program.D(16) * 2) / 2, Program.D(24));
            Controls.Add(c);
            return c;
        }

        void AddRow(string label, string name, Keys def, int x, ref int y, int W)
        {
            Row r = new Row();
            r.Name = name; r.Default = def; r.Key = CaptureWindow.LoadKey(name, def);
            Label l = new Label();
            l.Text = label;
            l.SetBounds(x, y + Program.D(4), Program.D(190), Program.D(22));
            Controls.Add(l);

            TextBox tb = new TextBox();
            tb.ReadOnly = true;
            tb.BackColor = Color.White;
            tb.Cursor = Cursors.Hand;
            tb.TextAlign = HorizontalAlignment.Center;
            tb.Text = CaptureWindow.KeyText(r.Key);
            tb.SetBounds(x + Program.D(194), y, Program.D(140), Program.D(26));
            tb.ShortcutsEnabled = false;
            tb.KeyDown += delegate (object s, KeyEventArgs e)
            {
                e.SuppressKeyPress = true; e.Handled = true;
                if (IsModifierOnly(e.KeyCode)) return;
                if (e.KeyCode == Keys.Escape) return;
                r.Key = e.KeyCode | e.Modifiers;
                tb.Text = CaptureWindow.KeyText(r.Key);
            };
            // PrintScreen は KeyDown が来ないので KeyUp で受け取る
            tb.KeyUp += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.PrintScreen)
                {
                    r.Key = Keys.PrintScreen | e.Modifiers;
                    tb.Text = CaptureWindow.KeyText(r.Key);
                }
            };
            tb.Enter += delegate { tb.BackColor = Color.FromArgb(230, 242, 255); };
            tb.Leave += delegate { tb.BackColor = Color.White; };
            Controls.Add(tb);

            Button clr = new Button(); clr.Text = "なし";
            clr.SetBounds(x + Program.D(340), y, Program.D(44), Program.D(26));
            clr.Click += delegate { r.Key = Keys.None; tb.Text = CaptureWindow.KeyText(r.Key); };
            Controls.Add(clr);
            Button def2 = new Button(); def2.Text = "既定";
            def2.SetBounds(x + Program.D(388), y, Program.D(44), Program.D(26));
            def2.Click += delegate { r.Key = r.Default; tb.Text = CaptureWindow.KeyText(r.Key); };
            Controls.Add(def2);

            r.Box = tb;
            rows.Add(r);
            y += Program.D(34);
        }

        static bool IsModifierOnly(Keys k)
        {
            return k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu || k == Keys.LWin || k == Keys.RWin
                || k == Keys.LControlKey || k == Keys.RControlKey || k == Keys.LShiftKey || k == Keys.RShiftKey;
        }

        // Tab や矢印キーも枠で受け取れるように
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            foreach (Row r in rows)
            {
                if (r.Box.Focused && (keyData & Keys.KeyCode) != Keys.Tab && (keyData & Keys.KeyCode) != Keys.Escape
                    && (keyData & Keys.KeyCode) != Keys.Enter)
                {
                    Keys code = keyData & Keys.KeyCode;
                    if (!IsModifierOnly(code))
                    {
                        r.Key = keyData;
                        r.Box.Text = CaptureWindow.KeyText(r.Key);
                        return true;
                    }
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // 設定画面を開く。開いている間はホットキーを止める(押したキーで撮影が始まらないように)
        public static void Open(IWin32Window owner)
        {
            CaptureWindow cw = Program.CapWin;
            cw.UnregisterHotkeys();
            using (SettingsWindow f = new SettingsWindow())
            {
                DialogResult dr = f.ShowDialog(owner);
                if (dr == DialogResult.Abort)
                {
                    // インポートした → 再起動して反映
                    if (MessageBox.Show(owner, f.ImportedCount + " 個の設定を読み込みました。\nSimpleCap を再起動すると反映されます。今すぐ再起動しますか?", "SimpleCap",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        Program.Restart();
                        return;
                    }
                }
                if (dr == DialogResult.OK)
                {
                    Dictionary<string, string> st = Program.LoadSettings();
                    foreach (Row r in f.rows) st["hotkey." + r.Name] = ((int)r.Key).ToString();
                    bool libChanged = !string.Equals(f.LibDirChoice, Program.LibDir, StringComparison.OrdinalIgnoreCase);
                    st["libdir"] = f.LibDirChoice;
                    // AI で名前付け
                    f.KeepAi();
                    st["ai.provider"] = AiNamer.Providers[Math.Max(0, f.cmbAi.SelectedIndex)];
                    foreach (KeyValuePair<string, string[]> kv in f.aiVals)
                    {
                        st["ai." + kv.Key + ".url"] = kv.Value[0];
                        st["ai." + kv.Key + ".model"] = kv.Value[2];
                        try { AiNamer.SetKey(st, kv.Key, kv.Value[1]); } catch { }
                    }
                    cw.OptDelay = f.chkDelay.Checked;
                    cw.OptCursor = f.chkCursor.Checked;
                    cw.OptClip = f.chkClip.Checked;
                    if (cw.IsCompact != f.chkCompact.Checked) cw.SetCompact(f.chkCompact.Checked);
                    st["delay"] = cw.OptDelay ? "1" : "0";
                    st["cursor"] = cw.OptCursor ? "1" : "0";
                    st["clipboard"] = cw.OptClip ? "1" : "0";
                    st["compact"] = cw.IsCompact ? "1" : "0";
                    bool trayChanged = cw.OptTray != f.chkTray.Checked;
                    cw.OptTray = f.chkTray.Checked;
                    st["trayonly"] = cw.OptTray ? "1" : "0";
                    f.clickFx.Enabled = f.chkFx.Checked;
                    f.clickFx.Label = f.chkFxLabel.Checked;
                    f.clickFx.Size = Math.Max(0, f.cmbFxSize.SelectedIndex);
                    f.clickFx.Hold = f.chkFxHold.Checked;
                    f.clickFx.HoldSec = Math.Max(0, f.cmbFxHold.SelectedIndex) + 1;
                    f.clickFx.Speed = Math.Max(0, f.cmbFxSpeed.SelectedIndex) + 1;
                    f.clickFx.Zoom = f.chkFxZoom.Checked;
                    f.clickFx.ZoomPct = (Math.Max(0, f.cmbFxZoom.SelectedIndex) + 1) * 10;
                    f.clickFx.Save(st);
                    st["video.maxmin"] = ((int)f.numMaxMin.Value).ToString();
                    st["video.countdown"] = new int[] { 0, 3, 5, 10 }[Math.Max(0, f.cmbVideoCd.SelectedIndex)].ToString();
                    if (trayChanged) cw.ApplyTrayMode();
                    Program.SaveSettings(st);
                    if (libChanged)
                    {
                        try
                        {
                            System.IO.Directory.CreateDirectory(f.LibDirChoice);
                            Program.LibDir = f.LibDirChoice;
                            if (Program.BrowserExists) Program.Browser.ReloadLibrary();
                        }
                        catch (Exception ex) { MessageBox.Show(owner, "保存先フォルダを使えませんでした: " + ex.Message, "SimpleCap"); }
                    }
                }
            }
            List<string> fails = cw.RegisterHotkeys();
            if (fails.Count > 0)
                MessageBox.Show(owner, "次のショートカットキーは、ほかのアプリが使用中のため登録できませんでした。\n別のキーを設定してください。\n\n"
                    + string.Join("\n", fails.ToArray()), "SimpleCap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
