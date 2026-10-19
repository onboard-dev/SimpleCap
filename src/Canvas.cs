using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace SimpleCap
{
    public enum Tool { Select, Arrow, Line, Rect, Ellipse, Highlight, Text, Mosaic, Crop, ClearColor,
        BalloonPointer, BalloonArrow, Number, NumberPin, Fill, CutV, CutH }

    // 編集キャンバス
    public partial class Canvas : Control
    {
        public Doc Doc;
        public float Zoom = 1f;
        public Tool Tool = Tool.Select;
        public Obj Selected;

        // 新規オブジェクトの既定値
        public Color CurStroke = Color.FromArgb(230, 30, 50);
        public Color CurFill = Color.Transparent;
        public float CurWidth = 4f;
        public int CurDash = 0;              // 線の種類
        public bool CurBothEnds = false;     // 矢印: 始点にも矢印
        public float CurFontSize = 28f;
        public int Tolerance = 15;           // 塗り・透明化の許容範囲 (%)

        // ツールごとに最後に使ったサイズを記憶 {線の太さ, 文字サイズ, 箱の幅, 箱の高さ}
        Dictionary<Tool, float[]> toolStyle;

        // 背景(キャンバス)のサイズ変更
        public bool BgSelected;
        bool bgResizing;
        int bgHandle;
        RectangleF bgRect;

        public event EventHandler DocChanged;        // レイヤ構成/内容が変わった
        public event EventHandler SelectionChanged;
        public event EventHandler ZoomChanged;

        class State { public Bitmap Base; public List<Obj> Objs; }
        List<State> undo = new List<State>(), redo = new List<State>();

        enum DragMode { None, Create, Move, Handle, Crop, Region, RegionHandle, RegionMove, Cut }

        // 縦カット / 横カット:マウスの位置に点線、ドラッグした範囲を切り取って詰める
        bool IsCutTool { get { return Tool == Tool.CutV || Tool == Tool.CutH; } }
        bool cutHover;        // 点線を出しているか(マウスが画像の上にあるとき)
        float cutHoverPos;    // 点線の位置(画像座標。縦カットは Y、横カットは X)

        // 背景の範囲選択 (点線+四隅のアンカー)
        bool hasRegion;
        Rectangle region;           // 画像座標
        Bitmap floatImg;            // 移動中に持ち上げた画素
        Point floatPos;
        PointF floatGrab;
        public bool HasRegion { get { return hasRegion && Doc != null; } }
        public Rectangle RegionRect { get { return region; } }
        public event EventHandler RegionMenu;   // 範囲の上で右クリック
        public event EventHandler RightCancel;  // 右クリック(範囲の上以外):選択を解除して選択ツールへ戻す
        DragMode drag = DragMode.None;
        int handleIdx;
        PointF last, start, cur;
        bool pendingUndo;

        Bitmap view;          // 市松模様+背景画像を表示倍率で描いたキャッシュ
        float viewZoom; Bitmap viewBase;
        static TextureBrush checker;

        public Canvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.Opaque | ControlStyles.Selectable, true);
            if (checker == null)
            {
                // 透明部分の市松模様。白っぽいウィンドウの角と見分けがつくよう、濃いめ・細かめにする
                int c = Math.Max(4, Program.D(5));
                Bitmap cb = new Bitmap(c * 2, c * 2);
                using (Graphics g = Graphics.FromImage(cb))
                {
                    g.Clear(Color.FromArgb(200, 200, 200));
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(150, 150, 150)))
                    { g.FillRectangle(b, 0, 0, c, c); g.FillRectangle(b, c, c, c, c); }
                }
                checker = new TextureBrush(cb);
            }
        }

        // ---------- ドキュメント ----------
        public void SetDoc(Doc d)
        {
            hasRegion = false; floatImg = null;
            Doc = d;
            Selected = null;
            BgSelected = false; bgResizing = false;
            undo.Clear(); redo.Clear();
            drag = DragMode.None;
            InvalidateView();
            UpdateSize();
            Fire(DocChanged); Fire(SelectionChanged);
        }

        void Fire(EventHandler h) { if (h != null) h(this, EventArgs.Empty); }

        public void UpdateSize()
        {
            if (Doc == null) { Size = new Size(1, 1); }
            else Size = new Size(Math.Max(1, (int)Math.Round(Doc.Base.Width * Zoom)), Math.Max(1, (int)Math.Round(Doc.Base.Height * Zoom)));
            CanvasHost h = Parent as CanvasHost;
            if (h != null) h.Recenter();
            Invalidate();
        }

        public void SetZoom(float z)
        {
            z = Math.Max(0.05f, Math.Min(16f, z));
            if (Math.Abs(z - Zoom) < 0.0001f) return;
            Zoom = z;
            InvalidateView();
            UpdateSize();
            Fire(ZoomChanged);
        }

        public void ZoomFit(Size area) { ZoomFit(area, false); }
        // enlarge = true:小さい画像もウィンドウいっぱいまで拡大(「🔲 全体」ボタン)
        //          false:大きい画像だけ縮小し、小さい画像は 100% のまま(開いたとき)
        public void ZoomFit(Size area, bool enlarge)
        {
            if (Doc == null) return;
            float z = Math.Min((area.Width - 20f) / Doc.Base.Width, (area.Height - 20f) / Doc.Base.Height);
            if (!enlarge) z = Math.Min(1f, z);
            SetZoom(Math.Max(0.05f, z));
        }

        public void InvalidateView() { viewBase = null; Invalidate(); }

        void EnsureView()
        {
            if (Doc == null) return;
            if (view != null && viewBase == Doc.Base && viewZoom == Zoom) return;
            if (view != null) view.Dispose();
            int w = Math.Max(1, (int)Math.Round(Doc.Base.Width * Zoom));
            int h = Math.Max(1, (int)Math.Round(Doc.Base.Height * Zoom));
            view = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(view))
            {
                g.FillRectangle(checker, 0, 0, w, h);
                g.InterpolationMode = Zoom >= 1f ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                using (ImageAttributes ia = new ImageAttributes())
                {
                    ia.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(Doc.Base, new Rectangle(0, 0, w, h), 0, 0, Doc.Base.Width, Doc.Base.Height, GraphicsUnit.Pixel, ia);
                }
            }
            viewBase = Doc.Base; viewZoom = Zoom;
        }

        // ---------- Undo / Redo ----------
        State Snapshot()
        {
            State s = new State();
            s.Base = Doc.Base;
            s.Objs = new List<Obj>();
            foreach (Obj o in Doc.Objs) s.Objs.Add(o.Clone());
            return s;
        }
        public void PushUndo()
        {
            if (Doc == null) return;
            undo.Add(Snapshot());
            if (undo.Count > 40) undo.RemoveAt(0);
            redo.Clear();
            Doc.Dirty = true;
        }
        void Restore(State s)
        {
            hasRegion = false; floatImg = null;
            int selIdx = Selected != null ? Doc.Objs.IndexOf(Selected) : -1;
            Doc.Base = s.Base;
            Doc.Objs = s.Objs;
            Selected = (selIdx >= 0 && selIdx < Doc.Objs.Count) ? Doc.Objs[selIdx] : null;
            Doc.Dirty = true;
            InvalidateView();
            UpdateSize();
            Fire(DocChanged); Fire(SelectionChanged);
        }
        public void Undo()
        {
            if (Doc == null || undo.Count == 0) return;
            redo.Add(Snapshot());
            State s = undo[undo.Count - 1]; undo.RemoveAt(undo.Count - 1);
            Restore(s);
        }
        public void Redo()
        {
            if (Doc == null || redo.Count == 0) return;
            undo.Add(Snapshot());
            State s = redo[redo.Count - 1]; redo.RemoveAt(redo.Count - 1);
            Restore(s);
        }

        // ---------- 操作 ----------
        public void SetSelected(Obj o)
        {
            if (o != null && BgSelected) { BgSelected = false; InvalidateHost(); }
            if (Selected == o) return;
            Selected = o;
            Invalidate();
            Fire(SelectionChanged);
        }

        // ---------- 背景のサイズ変更 (四隅+四辺のアンカー) ----------
        // ビデオの注釈編集中は背景(コマの画像)を変えさせない
        public bool LockBase;

        public void SetBgSelected(bool on)
        {
            if (Doc == null || LockBase) on = false;
            if (on && Selected != null) { Selected = null; }
            if (BgSelected == on) { Invalidate(); return; }
            BgSelected = on;
            Invalidate(); InvalidateHost();
            Fire(SelectionChanged);
        }
        public bool IsBgResizing { get { return bgResizing; } }

        void InvalidateHost() { if (Parent != null) Parent.Invalidate(); }

        RectangleF CurrentBgRect
        {
            get { return bgResizing ? bgRect : new RectangleF(0, 0, Doc.Base.Width, Doc.Base.Height); }
        }

        static PointF[] RectHandles(RectangleF r)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            return new PointF[] {
                new PointF(r.Left, r.Top), new PointF(cx, r.Top), new PointF(r.Right, r.Top), new PointF(r.Right, cy),
                new PointF(r.Right, r.Bottom), new PointF(cx, r.Bottom), new PointF(r.Left, r.Bottom), new PointF(r.Left, cy) };
        }

        // viewPt はキャンバス座標(キャンバス外=負の値もあり)
        public int BgHandleAt(Point viewPt)
        {
            if (Doc == null || !BgSelected) return -1;
            PointF[] hs = RectHandles(CurrentBgRect);
            int r = Program.D(7);
            for (int i = 0; i < hs.Length; i++)
            {
                Point q = ToView(hs[i]);
                if (Math.Abs(q.X - viewPt.X) <= r && Math.Abs(q.Y - viewPt.Y) <= r) return i;
            }
            return -1;
        }

        public static Cursor BgCursor(int h)
        {
            switch (h)
            {
                case 0: case 4: return Cursors.SizeNWSE;
                case 2: case 6: return Cursors.SizeNESW;
                case 1: case 5: return Cursors.SizeNS;
                case 3: case 7: return Cursors.SizeWE;
            }
            return Cursors.Default;
        }

        public void BeginBgResize(int h)
        {
            if (LockBase) return;
            bgResizing = true; bgHandle = h;
            bgRect = new RectangleF(0, 0, Doc.Base.Width, Doc.Base.Height);
        }

        public void MoveBgResize(Point viewPt)
        {
            if (!bgResizing) return;
            PointF p = ToImg(viewPt);
            float L = bgRect.Left, T = bgRect.Top, R = bgRect.Right, B = bgRect.Bottom;
            float px = (float)Math.Round(p.X), py = (float)Math.Round(p.Y);
            if (bgHandle == 0 || bgHandle == 6 || bgHandle == 7) L = Math.Min(px, R - 1);
            if (bgHandle == 2 || bgHandle == 3 || bgHandle == 4) R = Math.Max(px, L + 1);
            if (bgHandle == 0 || bgHandle == 1 || bgHandle == 2) T = Math.Min(py, B - 1);
            if (bgHandle == 4 || bgHandle == 5 || bgHandle == 6) B = Math.Max(py, T + 1);
            bgRect = RectangleF.FromLTRB(L, T, R, B);
            Invalidate(); InvalidateHost();
            Fire(ZoomChanged);   // ステータス更新用
        }

        public void EndBgResize()
        {
            hasRegion = false;
            if (!bgResizing) return;
            bgResizing = false;
            Rectangle nr = Rectangle.Round(bgRect);
            int W = Doc.Base.Width, H = Doc.Base.Height;
            if (nr.Width < 1 || nr.Height < 1 || nr == new Rectangle(0, 0, W, H)) { Invalidate(); InvalidateHost(); return; }
            PushUndo();
            Bitmap nb = new Bitmap(nr.Width, nr.Height, PixelFormat.Format32bppArgb);
            nb.SetResolution(96, 96);
            using (Graphics g = Graphics.FromImage(nb))
            {
                g.Clear(Color.Transparent);   // 広げた部分は透明
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(Doc.Base, new Rectangle(-nr.X, -nr.Y, W, H), 0, 0, W, H, GraphicsUnit.Pixel);
            }
            Doc.Base = nb;
            foreach (Obj o in Doc.Objs) o.Offset(-nr.X, -nr.Y);
            InvalidateView();
            UpdateSize();
            InvalidateHost();
            Fire(DocChanged);
        }

        public Size BgPreviewSize
        {
            get { Rectangle r = Rectangle.Round(CurrentBgRect); return r.Size; }
        }

        // 背景の枠とアンカーを描く (origin = 画像左上が描画面のどこにあるか)
        public void PaintBgFrame(Graphics g, Point origin)
        {
            if (Doc == null || !BgSelected) return;
            RectangleF r = CurrentBgRect;
            Func<PointF, Point> V = delegate (PointF ip)
            {
                Point q = ToView(ip); return new Point(q.X + origin.X, q.Y + origin.Y);
            };
            Point a = V(r.Location), b = V(new PointF(r.Right, r.Bottom));
            Rectangle vr = Rectangle.FromLTRB(a.X, a.Y, b.X, b.Y);
            g.SmoothingMode = SmoothingMode.None;
            using (Pen p = new Pen(Color.FromArgb(0, 120, 215), 1.5f))
            {
                p.DashStyle = DashStyle.Dash;
                g.DrawRectangle(p, vr);
            }
            int hr = Program.D(5);
            foreach (PointF hp in RectHandles(r))
            {
                Point q = V(hp);
                Rectangle rc = new Rectangle(q.X - hr, q.Y - hr, hr * 2, hr * 2);
                g.FillRectangle(Brushes.White, rc);
                using (Pen p = new Pen(Color.FromArgb(0, 120, 215), 1.5f)) g.DrawRectangle(p, rc);
            }
            if (bgResizing)
            {
                string s = Rectangle.Round(r).Width + " × " + Rectangle.Round(r).Height;
                Size sz = TextRenderer.MeasureText(s, Program.UiFont);
                Rectangle lr = new Rectangle(b.X + Program.D(8), b.Y + Program.D(8), sz.Width + Program.D(8), sz.Height + Program.D(4));
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(220, 0, 120, 215))) g.FillRectangle(sb, lr);
                TextRenderer.DrawText(g, s, Program.UiFont, lr, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        public void DeleteSelected()
        {
            if (Doc == null || Selected == null) return;
            PushUndo();
            Doc.Objs.Remove(Selected);
            Selected = null;
            Invalidate();
            Fire(DocChanged); Fire(SelectionChanged);
        }

        public void MoveLayer(int dir)
        {
            if (Doc == null || Selected == null) return;
            int i = Doc.Objs.IndexOf(Selected);
            int j = i + dir;
            if (i < 0 || j < 0 || j >= Doc.Objs.Count) return;
            PushUndo();
            Obj sel = Doc.Objs[i];  // PushUndo は複製を保存するので実体はそのまま
            Doc.Objs.RemoveAt(i);
            Doc.Objs.Insert(j, sel);
            Invalidate();
            Fire(DocChanged);
        }

        // レイヤ統合: 表示中のレイヤをすべて背景画像レイヤに統合
        public void MergeAllLayers()
        {
            hasRegion = false;
            if (Doc == null || Doc.Objs.Count == 0) return;
            PushUndo();
            Bitmap flat = Doc.Render();
            Doc.Base = flat;
            Doc.Objs = new List<Obj>();
            Selected = null;
            InvalidateView();
            Fire(DocChanged); Fire(SelectionChanged);
        }

        public void AddImageLayer(Bitmap img) { AddImageLayer(img, null, true); }

        // スタンプ: 長い辺を size px にして、表示中の範囲の中央に新しいレイヤとして置く
        public void AddStamp(Bitmap img, int size)
        {
            if (Doc == null) return;
            float z = (float)size / Math.Max(img.Width, img.Height);
            float w = img.Width * z, h = img.Height * z;
            float cx = Doc.Base.Width / 2f, cy = Doc.Base.Height / 2f;
            CanvasHost host = Parent as CanvasHost;
            if (host != null)
            {
                // 画面に見えている部分の中央
                cx = (host.ClientSize.Width / 2f - Left) / Zoom;
                cy = (host.ClientSize.Height / 2f - Top) / Zoom;
                cx = Math.Max(0, Math.Min(Doc.Base.Width, cx));
                cy = Math.Max(0, Math.Min(Doc.Base.Height, cy));
            }
            AddImageLayer(img, new Point((int)(cx - w / 2), (int)(cy - h / 2)), true);
            if (Selected != null && Selected.Type == ObjType.Image)
            {
                Selected.P2 = new PointF(Selected.P1.X + w, Selected.P1.Y + h);
                Invalidate();
            }
        }

        public void AddImageLayer(Bitmap img, Point? at, bool pushUndo)
        {
            if (Doc == null) return;
            if (pushUndo) PushUndo();
            Obj o = new Obj();
            o.Type = ObjType.Image;
            o.Img = img;
            // 指定がなければ表示中の左上付近に配置
            CanvasHost h = Parent as CanvasHost;
            float x = 10, y = 10;
            if (at.HasValue) { x = at.Value.X; y = at.Value.Y; }
            else if (h != null) { x = Math.Max(0, -Left) / Zoom + 10; y = Math.Max(0, -Top) / Zoom + 10; }
            hasRegion = false;
            o.P1 = new PointF(x, y);
            o.P2 = new PointF(x + img.Width, y + img.Height);
            Doc.Objs.Add(o);
            Selected = o;
            Invalidate();
            Fire(DocChanged); Fire(SelectionChanged);
        }

        // ---------- 注釈オブジェクトのコピー&貼り付け ----------
        // 選択中の注釈だけを画像にする(透明背景、余白は自動で切り詰め)
        public Bitmap GetObjectImage(Obj o)
        {
            if (Doc == null || o == null) return null;
            RectangleF b = o.Bounds;
            if (o.Type == ObjType.BalloonPointer) b = RectangleF.Union(b, new RectangleF(o.P3.X, o.P3.Y, 0.01f, 0.01f));
            float pad = Math.Max(8f, o.Width * 4f + o.FontSize * 0.3f);
            b.Inflate(pad, pad);
            int w = Math.Max(1, (int)Math.Ceiling(b.Width)), h = Math.Max(1, (int)Math.Ceiling(b.Height));
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            bmp.SetResolution(96, 96);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.TranslateTransform(-(float)Math.Floor(b.X), -(float)Math.Floor(b.Y));
                bool vis = o.Visible; o.Visible = true;
                o.Draw(g, Doc.Base);
                o.Visible = vis;
            }
            Rectangle c = OpaqueBounds(bmp);
            if (c.Width <= 0 || c.Height <= 0 || c.Size == bmp.Size) return bmp;
            Bitmap t = bmp.Clone(c, PixelFormat.Format32bppArgb);
            t.SetResolution(96, 96);
            bmp.Dispose();
            return t;
        }

        static Rectangle OpaqueBounds(Bitmap b)
        {
            BitmapData bd = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = bd.Stride;
                byte[] px = new byte[stride * b.Height];
                System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, px, 0, px.Length);
                int x0 = b.Width, y0 = b.Height, x1 = -1, y1 = -1;
                for (int y = 0; y < b.Height; y++)
                    for (int x = 0; x < b.Width; x++)
                        if (px[y * stride + x * 4 + 3] != 0)
                        {
                            if (x < x0) x0 = x; if (x > x1) x1 = x;
                            if (y < y0) y0 = y; if (y > y1) y1 = y;
                        }
                if (x1 < 0) return Rectangle.Empty;
                return Rectangle.FromLTRB(x0, y0, x1 + 1, y1 + 1);
            }
            finally { b.UnlockBits(bd); }
        }

        // 注釈を複製して新しいレイヤとして貼り付け(編集できる状態のまま)
        public void PasteObjects(List<Obj> objs, float offset)
        {
            if (Doc == null || objs == null || objs.Count == 0) return;
            PushUndo();
            hasRegion = false;
            Obj last = null;
            foreach (Obj src in objs)
            {
                Obj o = src.Clone();
                o.Visible = true;
                o.Offset(offset, offset);
                if (o.IsNumber) o.Text = NextNumber().ToString();   // 番号スタンプは続きの番号に
                Doc.Objs.Add(o);
                last = o;
            }
            if (BgSelected) { BgSelected = false; InvalidateHost(); }
            Selected = last;
            Invalidate();
            Fire(DocChanged); Fire(SelectionChanged);
        }

        // ---------- 背景の範囲選択の操作 ----------
        public void ClearRegionSelection()
        {
            if (!hasRegion && floatImg == null) return;
            hasRegion = false; floatImg = null;
            Invalidate();
            Fire(SelectionChanged);
        }

        // 範囲の画素を取り出す(背景画像のみ)
        public Bitmap GetRegionImage()
        {
            if (!HasRegion) return null;
            Bitmap b = Doc.Base.Clone(region, PixelFormat.Format32bppArgb);
            b.SetResolution(96, 96);
            return b;
        }

        static Bitmap ClearRect(Bitmap src, Rectangle r)
        {
            Bitmap b = (Bitmap)src.Clone(new Rectangle(0, 0, src.Width, src.Height), PixelFormat.Format32bppArgb);
            b.SetResolution(96, 96);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                using (SolidBrush t = new SolidBrush(Color.Transparent)) g.FillRectangle(t, r);
            }
            return b;
        }

        // 範囲を透明にする(削除 / 切り取り)
        public void ClearRegion()
        {
            if (!HasRegion) return;
            PushUndo();
            Doc.Base = ClearRect(Doc.Base, region);
            InvalidateView();
            Fire(DocChanged);
        }

        // 範囲を新しい画像レイヤにする (cut=true なら元の場所は透明に)
        public void RegionToLayer(bool cut)
        {
            if (!HasRegion) return;
            Rectangle r = region;
            Bitmap img = GetRegionImage();
            PushUndo();
            if (cut) { Doc.Base = ClearRect(Doc.Base, r); InvalidateView(); }
            AddImageLayer(img, r.Location, false);
        }

        Rectangle RegionView
        {
            get
            {
                Point a = ToView(new PointF(region.X, region.Y)), b = ToView(new PointF(region.Right, region.Bottom));
                return Rectangle.FromLTRB(a.X, a.Y, b.X, b.Y);
            }
        }

        int RegionHandleAt(Point vp)
        {
            if (!HasRegion) return -1;
            Rectangle v = RegionView;
            Point[] hs = { new Point(v.Left, v.Top), new Point(v.Right, v.Top), new Point(v.Right, v.Bottom), new Point(v.Left, v.Bottom) };
            int r = Program.D(6);
            for (int i = 0; i < 4; i++)
                if (Math.Abs(hs[i].X - vp.X) <= r && Math.Abs(hs[i].Y - vp.Y) <= r) return i;
            return -1;
        }

        Rectangle ClampToImage(RectangleF rf)
        {
            Rectangle r = Rectangle.Round(rf);
            r.Intersect(new Rectangle(0, 0, Doc.Base.Width, Doc.Base.Height));
            return r;
        }

        // 選択中オブジェクトのプロパティ変更
        // ---------- ツールごとのサイズ記憶 ----------
        static Tool? ToolOf(ObjType t)
        {
            switch (t)
            {
                case ObjType.Arrow: return Tool.Arrow;
                case ObjType.Line: return Tool.Line;
                case ObjType.Rect: return Tool.Rect;
                case ObjType.Ellipse: return Tool.Ellipse;
                case ObjType.Text: return Tool.Text;
                case ObjType.Highlight: return Tool.Highlight;
                case ObjType.Mosaic: return Tool.Mosaic;
                case ObjType.BalloonPointer: return Tool.BalloonPointer;
                case ObjType.BalloonArrow: return Tool.BalloonArrow;
                case ObjType.Number: return Tool.Number;
                case ObjType.NumberPin: return Tool.NumberPin;
            }
            return null;
        }

        Dictionary<Tool, float[]> Styles
        {
            get
            {
                if (toolStyle != null) return toolStyle;
                toolStyle = new Dictionary<Tool, float[]>();
                foreach (KeyValuePair<string, string> kv in Program.LoadSettings())
                {
                    if (!kv.Key.StartsWith("style.")) continue;
                    try
                    {
                        Tool t = (Tool)Enum.Parse(typeof(Tool), kv.Key.Substring(6));
                        string[] a = kv.Value.Split(',');
                        float[] v = new float[6];
                        for (int i = 0; i < 6 && i < a.Length; i++)
                            v[i] = float.Parse(a[i], System.Globalization.CultureInfo.InvariantCulture);
                        toolStyle[t] = v;
                    }
                    catch { }
                }
                return toolStyle;
            }
        }

        void SaveStyles()
        {
            Dictionary<string, string> st = Program.LoadSettings();
            foreach (KeyValuePair<Tool, float[]> kv in Styles)
            {
                List<string> a = new List<string>();
                foreach (float f in kv.Value) a.Add(f.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                st["style." + kv.Key] = string.Join(",", a.ToArray());
            }
            Program.SaveSettings(st);
        }

        // オブジェクトのサイズを、そのツールの次回の既定値として覚える
        void Remember(Obj o)
        {
            if (o == null) return;
            Tool? t = ToolOf(o.Type);
            if (!t.HasValue) return;
            float[] v;
            if (!Styles.TryGetValue(t.Value, out v)) { v = new float[6]; Styles[t.Value] = v; }
            if (v.Length < 6) { float[] nv = new float[6]; Array.Copy(v, nv, v.Length); v = nv; Styles[t.Value] = v; }
            v[0] = o.Width; v[1] = o.FontSize; v[4] = o.Dash;
            if (o.Type == ObjType.Arrow) v[5] = o.BothEnds ? 1 : 0;
            if (o.IsBalloon) { RectangleF b = o.Bounds; v[2] = b.Width; v[3] = b.Height; }
            if (Tool == t.Value) { CurWidth = o.Width; CurFontSize = o.FontSize; CurDash = o.Dash; if (o.Type == ObjType.Arrow) CurBothEnds = o.BothEnds; }
            SaveStyles();
        }

        // ツールを選んだとき、記憶したサイズを既定値にする
        public void LoadToolStyle(Tool t)
        {
            float[] v;
            if (t == Tool.Select || t == Tool.Crop || t == Tool.Fill || t == Tool.ClearColor || t == Tool.CutV || t == Tool.CutH) return;
            if (!Styles.TryGetValue(t, out v)) { CurWidth = 4f; CurFontSize = 28f; CurDash = 0; CurBothEnds = false; return; }  // 未使用のツールは既定値
            if (v[0] > 0 || t == Tool.Rect || t == Tool.Ellipse) CurWidth = v[0];
            if (v[1] >= 8) CurFontSize = v[1];
            CurDash = v.Length > 4 ? (int)v[4] : 0;
            CurBothEnds = v.Length > 5 && v[5] > 0.5f;
        }

        SizeF RememberedBox(Tool t, SizeF def)
        {
            float[] v;
            if (Styles.TryGetValue(t, out v) && v[2] >= 10 && v[3] >= 10) return new SizeF(v[2], v[3]);
            return def;
        }

        public void ApplyProps(Color? stroke, Color? fill, float? width, float? fontSize)
        {
            if (stroke.HasValue) CurStroke = stroke.Value;
            if (fill.HasValue) CurFill = fill.Value;
            if (width.HasValue) CurWidth = width.Value;
            if (fontSize.HasValue) CurFontSize = fontSize.Value;
            if ((Selected == null || Doc == null) && (width.HasValue || fontSize.HasValue) && Tool != Tool.Select)
            {
                // 何も選ばずにサイズを変えた → 今のツールの既定として覚える
                float[] v;
                if (!Styles.TryGetValue(Tool, out v)) { v = new float[6]; Styles[Tool] = v; }
                v[0] = CurWidth; v[1] = CurFontSize;
                SaveStyles();
            }
            if (Selected == null || Doc == null) return;
            PushUndo();
            if (stroke.HasValue) Selected.Stroke = stroke.Value;
            if (fill.HasValue) Selected.Fill = fill.Value;
            if (width.HasValue) Selected.Width = width.Value;
            if (fontSize.HasValue) { Selected.FontSize = fontSize.Value; Selected.FitText(); }
            if (width.HasValue || fontSize.HasValue) Remember(Selected);
            Invalidate();
            Fire(DocChanged);
        }

        // 線の種類を変更(選択中の注釈と、今のツールの既定)
        public void ApplyDash(int dash)
        {
            CurDash = dash;
            if (Selected == null || Doc == null)
            {
                if (Tool != Tool.Select)
                {
                    float[] v;
                    if (!Styles.TryGetValue(Tool, out v)) { v = new float[6]; v[0] = CurWidth; v[1] = CurFontSize; Styles[Tool] = v; }
                    if (v.Length < 6) { float[] nv = new float[6]; Array.Copy(v, nv, v.Length); v = nv; Styles[Tool] = v; }
                    v[4] = dash;
                    SaveStyles();
                }
                return;
            }
            if (Selected.Dash == dash) return;
            PushUndo();
            Selected.Dash = dash;
            Remember(Selected);
            Invalidate();
            Fire(DocChanged);
        }

        // 矢印の始点にも矢印を付ける/外す
        public void ApplyBothEnds(bool on)
        {
            CurBothEnds = on;
            if (Selected == null || Doc == null || Selected.Type != ObjType.Arrow)
            {
                if (Tool == Tool.Arrow)
                {
                    float[] v;
                    if (!Styles.TryGetValue(Tool, out v)) { v = new float[6]; v[0] = CurWidth; v[1] = CurFontSize; v[4] = CurDash; Styles[Tool] = v; }
                    if (v.Length < 6) { float[] nv = new float[6]; Array.Copy(v, nv, v.Length); v = nv; Styles[Tool] = v; }
                    v[5] = on ? 1 : 0;
                    SaveStyles();
                }
                return;
            }
            if (Selected.BothEnds == on) return;
            PushUndo();
            Selected.BothEnds = on;
            Remember(Selected);
            Invalidate();
            Fire(DocChanged);
        }

        public void EditText(Obj o)
        {
            string t = TextInputDialog.Ask(FindForm(), o.Text);
            if (t == null || t == o.Text) return;
            PushUndo();
            if (t.Trim().Length == 0) { Doc.Objs.Remove(o); Selected = null; Fire(SelectionChanged); }
            else { o.Text = t; o.FitText(); }
            Invalidate();
            Fire(DocChanged);
        }

        int NextNumber()
        {
            int max = 0;
            foreach (Obj o in Doc.Objs)
            {
                int n;
                if (o.IsNumber && int.TryParse(o.Text, out n) && n > max) max = n;
            }
            return max + 1;
        }

        // 吹き出しの指し先の初期位置 (箱の左上の外側)
        static PointF BalloonTail(RectangleF r)
        {
            float dx = Math.Max(30f, Math.Min(80f, r.Width * 0.5f));
            float dy = Math.Max(24f, Math.Min(60f, r.Height * 0.8f));
            return new PointF(r.X - dx, r.Y - dy);
        }

        // ---------- 座標 ----------
        PointF ToImg(Point p) { return new PointF(p.X / Zoom, p.Y / Zoom); }
        Point ToView(PointF p) { return new Point((int)Math.Round(p.X * Zoom), (int)Math.Round(p.Y * Zoom)); }

        Obj HitTest(PointF p)
        {
            float tol = 5f / Zoom;
            // 矢印・吹き出しなどの注釈を優先(大きな画像レイヤの下にあっても選べるように)
            for (int i = Doc.Objs.Count - 1; i >= 0; i--)
                if (Doc.Objs[i].Type != ObjType.Image && Doc.Objs[i].HitTest(p, tol)) return Doc.Objs[i];
            for (int i = Doc.Objs.Count - 1; i >= 0; i--)
                if (Doc.Objs[i].Type == ObjType.Image && Doc.Objs[i].HitTest(p, tol)) return Doc.Objs[i];
            return null;
        }

        int HandleAt(Obj o, Point vp)
        {
            PointF[] hs = o.Handles();
            int r = Program.D(6);
            for (int i = 0; i < hs.Length; i++)
            {
                Point q = ToView(hs[i]);
                if (Math.Abs(q.X - vp.X) <= r && Math.Abs(q.Y - vp.Y) <= r) return i;
            }
            return -1;
        }

        // Shift で 45度/正方形スナップ
        PointF Constrain(PointF a, PointF b, bool line)
        {
            if ((ModifierKeys & Keys.Shift) == 0) return b;
            float dx = b.X - a.X, dy = b.Y - a.Y;
            if (line)
            {
                double ang = Math.Atan2(dy, dx);
                double snap = Math.Round(ang / (Math.PI / 4)) * (Math.PI / 4);
                double len = Math.Sqrt(dx * dx + dy * dy);
                return new PointF(a.X + (float)(Math.Cos(snap) * len), a.Y + (float)(Math.Sin(snap) * len));
            }
            float m = Math.Max(Math.Abs(dx), Math.Abs(dy));
            return new PointF(a.X + (dx < 0 ? -m : m), a.Y + (dy < 0 ? -m : m));
        }

        // ---------- マウス ----------
        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            base.OnMouseDown(e);
            if (Doc == null || e.Button != MouseButtons.Left) return;
            PointF p = ToImg(e.Location);
            start = cur = last = p;

            switch (Tool)
            {
                case Tool.Select:
                    if (HasRegion)
                    {
                        int rh = RegionHandleAt(e.Location);
                        if (rh >= 0)
                        {
                            // 対角の点を起点に範囲を調整
                            Point[] opp = { new Point(region.Right, region.Bottom), new Point(region.Left, region.Bottom),
                                            new Point(region.Left, region.Top), new Point(region.Right, region.Top) };
                            start = opp[rh]; cur = p;
                            drag = DragMode.RegionHandle;
                            return;
                        }
                        if (RegionView.Contains(e.Location))
                        {
                            drag = DragMode.RegionMove; pendingUndo = true;
                            floatGrab = p;
                            return;
                        }
                        ClearRegionSelection();
                    }
                    {
                        int bh = BgHandleAt(e.Location);
                        if (bh >= 0) { BeginBgResize(bh); return; }
                    }
                    if (Selected != null)
                    {
                        int h = HandleAt(Selected, e.Location);
                        if (h >= 0) { PushUndo(); drag = DragMode.Handle; handleIdx = h; return; }
                    }
                    Obj hit = HitTest(p);
                    SetSelected(hit);
                    if (hit != null) { drag = DragMode.Move; pendingUndo = true; moveStartBounds = hit.Bounds; }
                    else
                    {
                        // 何もない所: クリック=背景を選択(四隅でサイズ変更)、ドラッグ=範囲選択
                        SetBgSelected(true);
                        if (!LockBase) drag = DragMode.Region;
                    }
                    return;

                case Tool.Text:
                    {
                        Obj t = HitTest(p);
                        if (t != null && t.HasText) { SetSelected(t); EditText(t); return; }
                        string s = TextInputDialog.Ask(FindForm(), "");
                        if (string.IsNullOrEmpty(s) || s.Trim().Length == 0) return;
                        PushUndo();
                        Obj o = new Obj();
                        o.Type = ObjType.Text; o.Text = s; o.P1 = p;
                        o.Stroke = CurStroke; o.Fill = CurFill; o.Width = CurWidth; o.FontSize = CurFontSize; o.Dash = CurDash;
                        o.FitText();
                        Doc.Objs.Add(o);
                        Selected = o;
                        Invalidate();
                        Fire(DocChanged); Fire(SelectionChanged);
                        return;
                    }

                case Tool.Crop:
                    drag = DragMode.Crop;
                    return;

                case Tool.CutV:
                case Tool.CutH:
                    drag = DragMode.Cut;
                    cutHover = false;
                    Invalidate();
                    return;

                case Tool.Fill:
                    {
                        int x = (int)Math.Floor(p.X), y = (int)Math.Floor(p.Y);
                        if (x < 0 || y < 0 || x >= Doc.Base.Width || y >= Doc.Base.Height) return;
                        Color fc = CurFill.A > 0 ? CurFill : CurStroke;
                        PushUndo();
                        Doc.Base = Doc.FloodFill(Doc.Base, x, y, fc, Tolerance * 255 / 100);
                        InvalidateView();
                        Fire(DocChanged);
                        return;
                    }

                case Tool.ClearColor:
                    {
                        int x = (int)p.X, y = (int)p.Y;
                        if (x < 0 || y < 0 || x >= Doc.Base.Width || y >= Doc.Base.Height) return;
                        Color c = Doc.Base.GetPixel(x, y);
                        if (c.A == 0) return;
                        PushUndo();
                        Doc.Base = Doc.MakeColorTransparent(Doc.Base, c, Tolerance * 255 / 100);
                        InvalidateView();
                        Fire(DocChanged);
                        return;
                    }

                default:
                    {
                        PushUndo();
                        Obj o = new Obj();
                        switch (Tool)
                        {
                            case Tool.Arrow: o.Type = ObjType.Arrow; break;
                            case Tool.Line: o.Type = ObjType.Line; break;
                            case Tool.Rect: o.Type = ObjType.Rect; break;
                            case Tool.Ellipse: o.Type = ObjType.Ellipse; break;
                            case Tool.Highlight: o.Type = ObjType.Highlight; break;
                            case Tool.Mosaic: o.Type = ObjType.Mosaic; break;
                            case Tool.BalloonPointer: o.Type = ObjType.BalloonPointer; break;
                            case Tool.BalloonArrow: o.Type = ObjType.BalloonArrow; break;
                            case Tool.Number: o.Type = ObjType.Number; break;
                            case Tool.NumberPin: o.Type = ObjType.NumberPin; break;
                        }
                        o.P1 = o.P2 = p;
                        o.Stroke = o.Type == ObjType.Highlight ? Color.FromArgb(255, 240, 0) : CurStroke;
                        o.Fill = (o.Type == ObjType.Rect || o.Type == ObjType.Ellipse) ? CurFill : Color.Transparent;
                        o.Width = CurWidth;
                        o.Dash = CurDash;
                        if (o.Type == ObjType.Arrow) o.BothEnds = CurBothEnds;
                        o.FontSize = CurFontSize;
                        if (o.IsBalloon)
                        {
                            // 吹き出し: 塗り=背景色(透明なら線の色)、文字=白
                            o.Fill = CurFill.A > 0 ? Color.FromArgb(255, CurFill) : CurStroke;
                            o.Stroke = Color.White;
                            o.P3 = BalloonTail(o.Bounds);   // 指し先も最初から箱に合わせて置く
                        }
                        if (o.IsNumber)
                        {
                            // 番号スタンプ: クリックごとに 1,2,3... (既存の最大番号+1)
                            o.Fill = CurFill.A > 0 ? Color.FromArgb(255, CurFill) : CurStroke;
                            o.Stroke = Color.White;
                            o.Text = NextNumber().ToString();
                            o.P2 = new PointF(p.X + 10, p.Y);   // ピンの向き(既定は右)
                        }
                        Doc.Objs.Add(o);
                        Selected = o;
                        drag = DragMode.Create;
                        Fire(SelectionChanged);
                        return;
                    }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (Doc == null) return;
            if (bgResizing) { MoveBgResize(e.Location); return; }
            PointF p = ToImg(e.Location);
            switch (drag)
            {
                case DragMode.Region:
                case DragMode.RegionHandle:
                    cur = p;
                    if (drag == DragMode.Region && Math.Abs(p.X - start.X) * Zoom < 3 && Math.Abs(p.Y - start.Y) * Zoom < 3) return;
                    if (BgSelected) { BgSelected = false; InvalidateHost(); }
                    region = ClampToImage(Obj.Norm(start, cur));
                    hasRegion = region.Width > 0 && region.Height > 0;
                    Invalidate();
                    return;
                case DragMode.RegionMove:
                    if (pendingUndo)
                    {
                        if (Math.Abs(p.X - floatGrab.X) * Zoom < 2 && Math.Abs(p.Y - floatGrab.Y) * Zoom < 2) return;
                        // 画素を持ち上げて、元の場所は透明に
                        PushUndo(); pendingUndo = false;
                        floatImg = GetRegionImage();
                        floatPos = region.Location;
                        Doc.Base = ClearRect(Doc.Base, region);
                        InvalidateView();
                    }
                    floatPos = new Point(region.X + (int)Math.Round(p.X - floatGrab.X), region.Y + (int)Math.Round(p.Y - floatGrab.Y));
                    Invalidate();
                    return;
                case DragMode.Cut:
                    cur = p;
                    Invalidate();
                    return;
                case DragMode.None:
                    if (IsCutTool)
                    {
                        Cursor = Tool == Tool.CutV ? Cursors.HSplit : Cursors.VSplit;
                        float pos = Tool == Tool.CutV ? p.Y : p.X;
                        float max = Tool == Tool.CutV ? Doc.Base.Height : Doc.Base.Width;
                        cutHoverPos = (float)Math.Round(Math.Max(0, Math.Min(max, pos)));
                        cutHover = true;
                        Invalidate();
                        break;
                    }
                    if (Tool == Tool.Select && BgHandleAt(e.Location) >= 0) { Cursor = BgCursor(BgHandleAt(e.Location)); break; }
                    if (Tool == Tool.Select && HasRegion)
                    {
                        int rh = RegionHandleAt(e.Location);
                        if (rh >= 0) { Cursor = (rh == 0 || rh == 2) ? Cursors.SizeNWSE : Cursors.SizeNESW; break; }
                        if (RegionView.Contains(e.Location)) { Cursor = Cursors.SizeAll; break; }
                    }
                    if (Tool == Tool.Select)
                    {
                        if (Selected != null && HandleAt(Selected, e.Location) >= 0) Cursor = Cursors.SizeAll;
                        else if (HitTest(p) != null) Cursor = Cursors.Hand;
                        else Cursor = Cursors.Default;
                    }
                    else if (Tool == Tool.Text) Cursor = Cursors.IBeam;
                    else Cursor = Cursors.Cross;
                    break;
                case DragMode.Move:
                    if (pendingUndo)
                    {
                        if (Math.Abs(p.X - start.X) * Zoom < 2 && Math.Abs(p.Y - start.Y) * Zoom < 2) return;
                        PushUndo(); pendingUndo = false;
                    }
                    {
                        // ドラッグ開始時の位置 + 移動量 からスマートガイドで吸着(Alt で無効)
                        RectangleF raw = moveStartBounds;
                        raw.Offset(p.X - start.X, p.Y - start.Y);
                        bool snap = (ModifierKeys & Keys.Alt) == 0;
                        PointF adj = snap ? SmartSnap(raw) : PointF.Empty;
                        RectangleF now = Selected.Bounds;
                        Selected.Offset(raw.X + adj.X - now.X, raw.Y + adj.Y - now.Y);
                        if (snap) BuildGuides(Selected.Bounds); else { guideLines.Clear(); guideGaps.Clear(); }
                    }
                    last = p;
                    Invalidate();
                    break;
                case DragMode.Handle:
                    Selected.MoveHandle(handleIdx, p);
                    Invalidate();
                    break;
                case DragMode.Create:
                    if (Selected.IsNumber)
                    {
                        // ピン型はドラッグした方向へ先端を向ける
                        if (Selected.Type == ObjType.NumberPin)
                        {
                            float dx = p.X - Selected.P1.X, dy = p.Y - Selected.P1.Y;
                            if (Math.Sqrt(dx * dx + dy * dy) > Selected.NumberRadius * 0.5f) Selected.P2 = p;
                        }
                        Invalidate();
                        break;
                    }
                    Selected.P2 = Constrain(Selected.P1, p, Selected.IsLineType);
                    // 吹き出しの指し先は描画中ずっと箱の左上の外側について動く
                    if (Selected.Type == ObjType.BalloonPointer) Selected.P3 = BalloonTail(Selected.Bounds);
                    Invalidate();
                    break;
                case DragMode.Crop:
                    cur = p;
                    Invalidate();
                    break;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (Doc == null) return;
            if (bgResizing) { EndBgResize(); return; }
            if (e.Button == MouseButtons.Right && HasRegion && RegionView.Contains(e.Location) && drag == DragMode.None)
            {
                Fire(RegionMenu);
                return;
            }
            if (e.Button == MouseButtons.Right && drag == DragMode.None && (Tool != Tool.Select || Selected != null))
            {
                Fire(RightCancel);
                return;
            }
            if (drag == DragMode.Region || drag == DragMode.RegionHandle)
            {
                drag = DragMode.None;
                if (hasRegion && (region.Width < 2 || region.Height < 2)) hasRegion = false;
                Invalidate();
                Fire(SelectionChanged);
                return;
            }
            if (drag == DragMode.RegionMove)
            {
                drag = DragMode.None;
                if (floatImg != null)
                {
                    // 同じレイヤ(背景)に置く
                    Bitmap nb = (Bitmap)Doc.Base.Clone(new Rectangle(0, 0, Doc.Base.Width, Doc.Base.Height), PixelFormat.Format32bppArgb);
                    nb.SetResolution(96, 96);
                    using (Graphics g = Graphics.FromImage(nb))
                    {
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.DrawImage(floatImg, new Rectangle(floatPos, floatImg.Size), 0, 0, floatImg.Width, floatImg.Height, GraphicsUnit.Pixel);
                    }
                    Doc.Base = nb;
                    region = ClampToImage(new Rectangle(floatPos, floatImg.Size));
                    hasRegion = region.Width > 0 && region.Height > 0;
                    floatImg = null;
                    InvalidateView();
                    Fire(DocChanged);
                }
                pendingUndo = false;
                return;
            }
            DragMode d = drag;
            drag = DragMode.None;
            pendingUndo = false;
            ClearGuides();
            if (d == DragMode.Create && Selected != null && Selected.IsNumber)
            {
                Remember(Selected);
                Invalidate();
                Fire(DocChanged);
                return;
            }
            if (d == DragMode.Create && Selected != null)
            {
                RectangleF r = Selected.Bounds;
                if (Selected.IsBalloon)
                {
                    // クリックだけなら既定サイズで作成
                    if (r.Width * Zoom < 8 || r.Height * Zoom < 8)
                    {
                        SizeF box = RememberedBox(Selected.Type == ObjType.BalloonArrow ? Tool.BalloonArrow : Tool.BalloonPointer,
                            Selected.Type == ObjType.BalloonArrow ? new SizeF(200, 90) : new SizeF(160, 80));
                        Selected.P2 = new PointF(Selected.P1.X + box.Width, Selected.P1.Y + box.Height);
                        r = Selected.Bounds;
                    }
                    if (Selected.Type == ObjType.BalloonPointer)
                        Selected.P3 = BalloonTail(r);
                    Invalidate();
                    string s = TextInputDialog.Ask(FindForm(), "");
                    if (s == null)
                    {
                        Doc.Objs.Remove(Selected);
                        Selected = null;
                        if (undo.Count > 0) undo.RemoveAt(undo.Count - 1);
                        Invalidate();
                        Fire(SelectionChanged);
                        return;
                    }
                    Selected.Text = s;
                    Remember(Selected);
                    Invalidate();
                    Fire(DocChanged);
                    return;
                }
                if (r.Width * Zoom < 3 && r.Height * Zoom < 3)
                {
                    // 小さすぎる → 取り消し
                    Doc.Objs.Remove(Selected);
                    Selected = null;
                    if (undo.Count > 0) undo.RemoveAt(undo.Count - 1);
                    Invalidate();
                    Fire(SelectionChanged);
                    return;
                }
            }
            if (d == DragMode.Cut)
            {
                Invalidate();
                CutRange(Tool == Tool.CutV, Tool == Tool.CutV ? start.Y : start.X, Tool == Tool.CutV ? cur.Y : cur.X);
                return;
            }
            if (d == DragMode.Crop)
            {
                Rectangle r = Rectangle.Round(Obj.Norm(start, cur));
                r.Intersect(new Rectangle(0, 0, Doc.Base.Width, Doc.Base.Height));
                Invalidate();
                if (r.Width < 3 || r.Height < 3) return;
                PushUndo();
                Doc.Base = Doc.Base.Clone(r, PixelFormat.Format32bppArgb);
                foreach (Obj o in Doc.Objs) o.Offset(-r.X, -r.Y);
                InvalidateView();
                UpdateSize();
                Fire(DocChanged);
                return;
            }
            if ((d == DragMode.Handle || d == DragMode.Create) && Selected != null) Remember(Selected);
            if (d != DragMode.None) Fire(DocChanged);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (Doc != null && Tool == Tool.Select && Selected != null && Selected.HasText)
                EditText(Selected);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) != 0)
            {
                SetZoom(e.Delta > 0 ? Zoom * 1.25f : Zoom / 1.25f);
                HandledMouseEventArgs he = e as HandledMouseEventArgs;
                if (he != null) he.Handled = true;
                return;
            }
            if (WheelWidth(e.Delta))
            {
                HandledMouseEventArgs he = e as HandledMouseEventArgs;
                if (he != null) he.Handled = true;
                return;
            }
            base.OnMouseWheel(e);
        }

        // オブジェクト選択中のホイール → 線の太さを変更(上で太く、下で細く)
        Obj wheelObj; DateTime wheelTime;
        int wheelRest;
        public bool WheelWidth(int delta)
        {
            if (Doc == null || Selected == null || delta == 0) return false;
            if (drag != DragMode.None) return false;
            wheelRest += delta;
            int steps = wheelRest / 120;            // 高精度ホイール/タッチパッド対策
            if (steps == 0) return true;
            wheelRest -= steps * 120;
            ObjType t = Selected.Type;
            float min = (t == ObjType.Rect || t == ObjType.Ellipse || t == ObjType.Image) ? 0f : 1f;
            float w = (float)Math.Round(Selected.Width) + steps;
            w = Math.Max(min, Math.Min(40f, w));
            if (w == Selected.Width) return true;
            // 連続したホイール操作は 1 回の「元に戻す」にまとめる
            DateTime now = DateTime.Now;
            if (wheelObj != Selected || (now - wheelTime).TotalMilliseconds > 800) PushUndo();
            wheelObj = Selected; wheelTime = now;
            Selected.Width = w;
            CurWidth = w;
            Remember(Selected);
            Doc.Dirty = true;
            Invalidate();
            Fire(DocChanged);
            return true;
        }

        // ================= スマートガイド =================
        // 移動中の注釈の 端・中央 を他の注釈やキャンバスに合わせて吸着し、補助線を表示する。
        // 左右・上下の間隔が他の注釈同士の間隔と同じになる位置にも吸着し、矢印で示す。
        // Alt を押しながらドラッグすると吸着しない。
        struct GuideLine { public bool Vert; public float Pos, A, B; }          // Vert: x=Pos, y=A..B / 横: y=Pos, x=A..B
        struct GuideGap { public bool Horiz; public float A, B, At; }          // Horiz: x=A..B (y=At) / 縦: y=A..B (x=At)
        readonly List<GuideLine> guideLines = new List<GuideLine>();
        readonly List<GuideGap> guideGaps = new List<GuideGap>();
        RectangleF moveStartBounds;
        static readonly Color GuideColor = Color.FromArgb(235, 70, 50);

        List<RectangleF> GuideTargets()
        {
            List<RectangleF> l = new List<RectangleF>();
            foreach (Obj o in Doc.Objs)
            {
                if (o == Selected || !o.Visible) continue;
                RectangleF b = o.Bounds;
                if (b.Width <= 0 && b.Height <= 0) continue;
                l.Add(b);
            }
            return l;
        }
        RectangleF PageRect { get { return new RectangleF(0, 0, Doc.Base.Width, Doc.Base.Height); } }

        static float[] Xs(RectangleF r) { return new float[] { r.Left, r.Left + r.Width / 2f, r.Right }; }
        static float[] Ys(RectangleF r) { return new float[] { r.Top, r.Top + r.Height / 2f, r.Bottom }; }
        static bool VOverlap(RectangleF a, RectangleF b) { return a.Top < b.Bottom && b.Top < a.Bottom; }
        static bool HOverlap(RectangleF a, RectangleF b) { return a.Left < b.Right && b.Left < a.Right; }

        // 他の注釈同士の間隔 (horiz=true なら左右の間隔)
        static List<float> PairGaps(List<RectangleF> objs, bool horiz)
        {
            List<float> g = new List<float>();
            for (int i = 0; i < objs.Count; i++)
                for (int j = 0; j < objs.Count; j++)
                {
                    if (i == j) continue;
                    RectangleF a = objs[i], b = objs[j];
                    if (horiz) { if (VOverlap(a, b) && a.Right <= b.Left) g.Add(b.Left - a.Right); }
                    else { if (HOverlap(a, b) && a.Bottom <= b.Top) g.Add(b.Top - a.Bottom); }
                }
            return g;
        }

        // 吸着のための移動量を返す
        PointF SmartSnap(RectangleF m)
        {
            float thr = Program.D(6) / Zoom;
            List<RectangleF> objs = GuideTargets();
            List<RectangleF> all = new List<RectangleF>(objs);
            all.Add(PageRect);
            return new PointF(SnapAxis(m, objs, all, true, thr), SnapAxis(m, objs, all, false, thr));
        }

        float SnapAxis(RectangleF m, List<RectangleF> objs, List<RectangleF> all, bool horiz, float thr)
        {
            float best = 0, bestAbs = thr;
            Action<float> consider = delegate (float d) { if (Math.Abs(d) < bestAbs) { bestAbs = Math.Abs(d); best = d; } };
            float[] ms = horiz ? Xs(m) : Ys(m);
            // 端・中央の位置合わせ
            foreach (RectangleF t in all)
            {
                float[] ts = horiz ? Xs(t) : Ys(t);
                foreach (float mv in ms) foreach (float tv in ts) consider(tv - mv);
            }
            // 等間隔
            RectangleF? nl, nr;
            Neighbors(m, objs, horiz, thr, out nl, out nr);
            List<float> gaps = PairGaps(objs, horiz);
            float mStart = horiz ? m.Left : m.Top, mEnd = horiz ? m.Right : m.Bottom, mSize = horiz ? m.Width : m.Height;
            if (nl.HasValue)
            {
                float nEnd = horiz ? nl.Value.Right : nl.Value.Bottom;
                foreach (float g in gaps) consider(nEnd + g - mStart);
            }
            if (nr.HasValue)
            {
                float nStart = horiz ? nr.Value.Left : nr.Value.Top;
                foreach (float g in gaps) consider(nStart - g - mEnd);
            }
            if (nl.HasValue && nr.HasValue)
            {
                float a = horiz ? nl.Value.Right : nl.Value.Bottom, b = horiz ? nr.Value.Left : nr.Value.Top;
                consider((a + b - mSize) / 2f - mStart);    // 両隣のちょうど真ん中
            }
            return best;
        }

        // m の左(上)隣と右(下)隣の注釈
        static void Neighbors(RectangleF m, List<RectangleF> objs, bool horiz, float tol, out RectangleF? nl, out RectangleF? nr)
        {
            nl = null; nr = null;
            foreach (RectangleF o in objs)
            {
                if (horiz ? !VOverlap(o, m) : !HOverlap(o, m)) continue;
                float oS = horiz ? o.Left : o.Top, oE = horiz ? o.Right : o.Bottom;
                float mS = horiz ? m.Left : m.Top, mE = horiz ? m.Right : m.Bottom;
                if (oE <= mS + tol && (!nl.HasValue || oE > (horiz ? nl.Value.Right : nl.Value.Bottom))) nl = o;
                if (oS >= mE - tol && (!nr.HasValue || oS < (horiz ? nr.Value.Left : nr.Value.Top))) nr = o;
            }
        }

        // 吸着後の位置で、一致している補助線と等間隔の矢印を集める
        void BuildGuides(RectangleF m)
        {
            guideLines.Clear(); guideGaps.Clear();
            const float tol = 0.5f;
            List<RectangleF> objs = GuideTargets();
            List<RectangleF> all = new List<RectangleF>(objs);
            all.Add(PageRect);
            foreach (RectangleF t in all)
            {
                foreach (float mv in Xs(m)) foreach (float tv in Xs(t))
                    if (Math.Abs(mv - tv) < tol) AddLine(true, tv, Math.Min(m.Top, t.Top), Math.Max(m.Bottom, t.Bottom));
                foreach (float mv in Ys(m)) foreach (float tv in Ys(t))
                    if (Math.Abs(mv - tv) < tol) AddLine(false, tv, Math.Min(m.Left, t.Left), Math.Max(m.Right, t.Right));
            }
            GapGuides(m, objs, true, tol);
            GapGuides(m, objs, false, tol);
        }

        void AddLine(bool vert, float pos, float a, float b)
        {
            for (int i = 0; i < guideLines.Count; i++)
            {
                GuideLine g = guideLines[i];
                if (g.Vert == vert && Math.Abs(g.Pos - pos) < 0.01f)
                { g.A = Math.Min(g.A, a); g.B = Math.Max(g.B, b); guideLines[i] = g; return; }
            }
            GuideLine n = new GuideLine(); n.Vert = vert; n.Pos = pos; n.A = a; n.B = b;
            guideLines.Add(n);
        }

        void AddGap(bool horiz, RectangleF a, RectangleF b)
        {
            GuideGap g = new GuideGap(); g.Horiz = horiz;
            if (horiz)
            {
                g.A = a.Right; g.B = b.Left;
                g.At = (Math.Max(a.Top, b.Top) + Math.Min(a.Bottom, b.Bottom)) / 2f;
            }
            else
            {
                g.A = a.Bottom; g.B = b.Top;
                g.At = (Math.Max(a.Left, b.Left) + Math.Min(a.Right, b.Right)) / 2f;
            }
            foreach (GuideGap x in guideGaps)
                if (x.Horiz == horiz && Math.Abs(x.A - g.A) < 0.01f && Math.Abs(x.B - g.B) < 0.01f) return;
            guideGaps.Add(g);
        }

        void GapGuides(RectangleF m, List<RectangleF> objs, bool horiz, float tol)
        {
            RectangleF? nl, nr;
            Neighbors(m, objs, horiz, tol, out nl, out nr);
            float gl = nl.HasValue ? (horiz ? m.Left - nl.Value.Right : m.Top - nl.Value.Bottom) : -1;
            float gr = nr.HasValue ? (horiz ? nr.Value.Left - m.Right : nr.Value.Top - m.Bottom) : -1;
            if (gl < 0.5f) gl = -1;
            if (gr < 0.5f) gr = -1;
            if (gl > 0 && gr > 0 && Math.Abs(gl - gr) < tol) { AddGap(horiz, nl.Value, m); AddGap(horiz, m, nr.Value); }
            for (int i = 0; i < objs.Count; i++)
                for (int j = 0; j < objs.Count; j++)
                {
                    if (i == j) continue;
                    RectangleF a = objs[i], b = objs[j];
                    float g;
                    if (horiz) { if (!VOverlap(a, b) || a.Right > b.Left) continue; g = b.Left - a.Right; }
                    else { if (!HOverlap(a, b) || a.Bottom > b.Top) continue; g = b.Top - a.Bottom; }
                    bool hit = false;
                    if (gl > 0 && Math.Abs(gl - g) < tol) { AddGap(horiz, nl.Value, m); hit = true; }
                    if (gr > 0 && Math.Abs(gr - g) < tol) { AddGap(horiz, m, nr.Value); hit = true; }
                    if (hit) AddGap(horiz, a, b);
                }
        }

        void ClearGuides()
        {
            if (guideLines.Count == 0 && guideGaps.Count == 0) return;
            guideLines.Clear(); guideGaps.Clear();
            Invalidate();
        }

        void PaintGuides(Graphics g)
        {
            if (guideLines.Count == 0 && guideGaps.Count == 0) return;
            g.SmoothingMode = SmoothingMode.None;
            using (Pen p = new Pen(GuideColor, 1f))
            {
                p.DashStyle = DashStyle.Dash;
                float ext = Program.D(10);
                foreach (GuideLine l in guideLines)
                {
                    float pos = (float)Math.Round(l.Pos * Zoom) - 0.5f;
                    if (l.Vert) g.DrawLine(p, pos, l.A * Zoom - ext, pos, l.B * Zoom + ext);
                    else g.DrawLine(p, l.A * Zoom - ext, pos, l.B * Zoom + ext, pos);
                }
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen p = new Pen(GuideColor, 1.5f))
            using (AdjustableArrowCap cap = new AdjustableArrowCap(4, 4, true))
            {
                p.CustomStartCap = cap; p.CustomEndCap = cap;
                foreach (GuideGap gg in guideGaps)
                {
                    float a = gg.A * Zoom, b = gg.B * Zoom, at = gg.At * Zoom;
                    if (b - a < 4) continue;
                    if (gg.Horiz) g.DrawLine(p, a + 1, at, b - 1, at);
                    else g.DrawLine(p, at, a + 1, at, b - 1);
                }
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (cutHover) { cutHover = false; Invalidate(); }
        }

        // 縦カット(vertical = true):Y が a〜b の行を切り取り、下を上に詰める
        // 横カット(vertical = false):X が a〜b の列を切り取り、右を左に詰める
        public void CutRange(bool vertical, float a, float b)
        {
            if (Doc == null) return;
            int size = vertical ? Doc.Base.Height : Doc.Base.Width;
            int lo = (int)Math.Round(Math.Max(0, Math.Min(a, b)));
            int hi = (int)Math.Round(Math.Min(size, Math.Max(a, b)));
            int cut = hi - lo;
            if (cut < 1 || cut >= size) return;   // 範囲なし / 全部はカットしない
            PushUndo();
            Bitmap src = Doc.Base;
            int W = src.Width, H = src.Height;
            Bitmap dst = vertical ? new Bitmap(W, H - cut, PixelFormat.Format32bppArgb) : new Bitmap(W - cut, H, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.CompositingMode = CompositingMode.SourceCopy;   // 透明部分もそのまま
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                if (vertical)
                {
                    if (lo > 0) g.DrawImage(src, new Rectangle(0, 0, W, lo), new Rectangle(0, 0, W, lo), GraphicsUnit.Pixel);
                    if (hi < H) g.DrawImage(src, new Rectangle(0, lo, W, H - hi), new Rectangle(0, hi, W, H - hi), GraphicsUnit.Pixel);
                }
                else
                {
                    if (lo > 0) g.DrawImage(src, new Rectangle(0, 0, lo, H), new Rectangle(0, 0, lo, H), GraphicsUnit.Pixel);
                    if (hi < W) g.DrawImage(src, new Rectangle(lo, 0, W - hi, H), new Rectangle(hi, 0, W - hi, H), GraphicsUnit.Pixel);
                }
            }
            Doc.Base = dst;
            // 注釈も合わせて詰める(切り取った範囲の後ろは cut だけ戻し、範囲の中は切り口へ寄せる)
            foreach (Obj o in Doc.Objs)
            {
                o.P1 = CutPoint(o.P1, vertical, lo, hi);
                o.P2 = CutPoint(o.P2, vertical, lo, hi);
                o.P3 = CutPoint(o.P3, vertical, lo, hi);
            }
            hasRegion = false; floatImg = null;
            InvalidateView();
            UpdateSize();
            Fire(DocChanged);
        }

        static PointF CutPoint(PointF p, bool vertical, int lo, int hi)
        {
            float v = vertical ? p.Y : p.X;
            if (v >= hi) v -= hi - lo;
            else if (v > lo) v = lo;
            return vertical ? new PointF(p.X, v) : new PointF(v, p.Y);
        }

        // 縦カット / 横カットの点線とドラッグ範囲を描く
        void PaintCut(Graphics g)
        {
            if (!IsCutTool) return;
            bool vertical = Tool == Tool.CutV;
            int W = ClientSize.Width, H = ClientSize.Height;
            if (drag == DragMode.Cut)
            {
                float a = vertical ? Math.Min(start.Y, cur.Y) : Math.Min(start.X, cur.X);
                float b = vertical ? Math.Max(start.Y, cur.Y) : Math.Max(start.X, cur.X);
                float max = vertical ? Doc.Base.Height : Doc.Base.Width;
                int va = (int)Math.Round(Math.Max(0, Math.Round(a)) * Zoom), vb = (int)Math.Round(Math.Min(max, Math.Round(b)) * Zoom);
                Rectangle band = vertical ? new Rectangle(0, va, W, Math.Max(0, vb - va)) : new Rectangle(va, 0, Math.Max(0, vb - va), H);
                // 半透明のグレー(下の画像が透けて見える)
                using (SolidBrush br = new SolidBrush(Color.FromArgb(110, 90, 90, 90))) g.FillRectangle(br, band);
                CutLine(g, vertical, va, W, H);
                CutLine(g, vertical, vb, W, H);
            }
            else if (cutHover)
                CutLine(g, vertical, (int)Math.Round(cutHoverPos * Zoom), W, H);
        }

        static void CutLine(Graphics g, bool vertical, int at, int W, int H)
        {
            g.SmoothingMode = SmoothingMode.None;
            using (Pen w = new Pen(Color.White, 1f))
            using (Pen k = new Pen(Color.Black, 1f))
            {
                k.DashStyle = DashStyle.Dash;
                if (vertical) { g.DrawLine(w, 0, at, W, at); g.DrawLine(k, 0, at, W, at); }
                else { g.DrawLine(w, at, 0, at, H); g.DrawLine(k, at, 0, at, H); }
            }
        }

        // カーソルキーで選択中の注釈を移動(押しっぱなし・連打は 1 回の「元に戻す」にまとめる)
        Obj nudgeObj; DateTime nudgeTime;
        public void Nudge(int dx, int dy)
        {
            if (Selected == null || Doc == null) return;
            if (drag != DragMode.None) return;
            DateTime now = DateTime.Now;
            if (nudgeObj != Selected || (now - nudgeTime).TotalMilliseconds > 800) PushUndo();
            nudgeObj = Selected; nudgeTime = now;
            Selected.Offset(dx, dy);
            Doc.Dirty = true;
            Invalidate();
            InvalidateHost();
        }

        // ---------- 描画 ----------
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            if (Doc == null) { g.Clear(Color.FromArgb(90, 90, 90)); return; }
            EnsureView();
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            Rectangle c = e.ClipRectangle;
            g.DrawImage(view, c, c, GraphicsUnit.Pixel);
            g.CompositingMode = CompositingMode.SourceOver;

            GraphicsState gs = g.Save();
            g.ScaleTransform(Zoom, Zoom);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            foreach (Obj o in Doc.Objs) o.Draw(g, Doc.Base);
            g.Restore(gs);

            // 選択表示
            if (Selected != null && Selected.Visible)
            {
                g.SmoothingMode = SmoothingMode.None;
                if (!Selected.IsLineType)
                {
                    RectangleF b = Selected.Bounds;
                    Rectangle vb = Rectangle.FromLTRB(ToView(b.Location).X - 2, ToView(b.Location).Y - 2,
                        ToView(new PointF(b.Right, b.Bottom)).X + 2, ToView(new PointF(b.Right, b.Bottom)).Y + 2);
                    using (Pen p = new Pen(Color.FromArgb(0, 120, 215)))
                    {
                        p.DashStyle = DashStyle.Dash;
                        g.DrawRectangle(p, vb);
                    }
                }
                int r = Program.D(4);
                foreach (PointF hp in Selected.Handles())
                {
                    Point q = ToView(hp);
                    Rectangle hr = new Rectangle(q.X - r, q.Y - r, r * 2, r * 2);
                    g.FillRectangle(Brushes.White, hr);
                    using (Pen p = new Pen(Color.FromArgb(0, 120, 215), 1.5f)) g.DrawRectangle(p, hr);
                }
            }

            // スマートガイド(移動中のみ)
            PaintGuides(g);
            g.SmoothingMode = SmoothingMode.None;

            // 背景の枠とアンカー(はみ出す部分はホスト側でも描く)
            PaintBgFrame(g, Point.Empty);

            // 範囲選択 (移動中の画素 + 点線 + 四隅のアンカー)
            if (floatImg != null)
            {
                Point a = ToView(new PointF(floatPos.X, floatPos.Y));
                Rectangle fr = new Rectangle(a.X, a.Y, (int)Math.Round(floatImg.Width * Zoom), (int)Math.Round(floatImg.Height * Zoom));
                g.InterpolationMode = Zoom >= 1f ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(floatImg, fr, 0, 0, floatImg.Width, floatImg.Height, GraphicsUnit.Pixel);
                DrawMarquee(g, fr, false);
            }
            else if (HasRegion) DrawMarquee(g, RegionView, drag != DragMode.Region);

            // 縦カット / 横カット
            PaintCut(g);

            // トリミング範囲
            if (drag == DragMode.Crop)
            {
                RectangleF rr = Obj.Norm(start, cur);
                Rectangle vr = Rectangle.FromLTRB(ToView(rr.Location).X, ToView(rr.Location).Y,
                    ToView(new PointF(rr.Right, rr.Bottom)).X, ToView(new PointF(rr.Right, rr.Bottom)).Y);
                using (Region outside = new Region(ClientRectangle))
                {
                    outside.Exclude(vr);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.FillRegion(b, outside);
                }
                using (Pen p = new Pen(Color.White)) { p.DashStyle = DashStyle.Dash; g.DrawRectangle(p, vr); }
            }
        }
    }

    // 点線の選択枠
    public partial class Canvas
    {
        void DrawMarquee(Graphics g, Rectangle r, bool handles)
        {
            g.SmoothingMode = SmoothingMode.None;
            using (Pen w = new Pen(Color.White, 1f)) g.DrawRectangle(w, r);
            using (Pen b = new Pen(Color.Black, 1f)) { b.DashStyle = DashStyle.Dash; g.DrawRectangle(b, r); }
            if (!handles) return;
            int hr = Program.D(4);
            Point[] hs = { new Point(r.Left, r.Top), new Point(r.Right, r.Top), new Point(r.Right, r.Bottom), new Point(r.Left, r.Bottom) };
            foreach (Point q in hs)
            {
                Rectangle rc = new Rectangle(q.X - hr, q.Y - hr, hr * 2, hr * 2);
                g.FillRectangle(Brushes.White, rc);
                using (Pen p = new Pen(Color.FromArgb(0, 120, 215), 1.5f)) g.DrawRectangle(p, rc);
            }
        }
    }

    // キャンバスを中央寄せ+スクロールさせる入れ物
    public class CanvasHost : Panel
    {
        public Canvas C;
        public CanvasHost(Canvas c)
        {
            C = c;
            AutoScroll = true;
            BackColor = Color.FromArgb(64, 64, 68);
            DoubleBuffered = true;
            Controls.Add(c);
        }
        public void Recenter()
        {
            int x = C.Width < ClientSize.Width ? (ClientSize.Width - C.Width) / 2 : AutoScrollPosition.X;
            int y = C.Height < ClientSize.Height ? (ClientSize.Height - C.Height) / 2 : AutoScrollPosition.Y;
            if (C.Location != new Point(x, y)) C.Location = new Point(x, y);
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Recenter(); Invalidate(); }

        // キャンバスの外にはみ出したアンカーを描画・操作する
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            C.PaintBgFrame(e.Graphics, C.Location);
        }
        Point ToCanvas(Point p) { return new Point(p.X - C.Left, p.Y - C.Top); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int h = C.BgHandleAt(ToCanvas(e.Location));
            if (h >= 0) C.BeginBgResize(h);
            else C.SetBgSelected(false);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (C.IsBgResizing) { C.MoveBgResize(ToCanvas(e.Location)); return; }
            int h = C.BgHandleAt(ToCanvas(e.Location));
            Cursor = h >= 0 ? Canvas.BgCursor(h) : Cursors.Default;
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (C.IsBgResizing) C.EndBgResize();
        }
        protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); }
        protected override Point ScrollToControl(Control activeControl) { return DisplayRectangle.Location; }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) != 0) { C.SetZoom(e.Delta > 0 ? C.Zoom * 1.25f : C.Zoom / 1.25f); return; }
            if (C.WheelWidth(e.Delta)) { HandledMouseEventArgs he = e as HandledMouseEventArgs; if (he != null) he.Handled = true; return; }
            base.OnMouseWheel(e);
        }
    }
}
