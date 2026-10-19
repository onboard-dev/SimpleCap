using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace SimpleCap
{
    public enum ObjType : byte { Rect = 1, Ellipse = 2, Line = 3, Arrow = 4, Text = 5, Highlight = 6, Mosaic = 7, Image = 8,
        BalloonPointer = 9, BalloonArrow = 10, BalloonShadow = 11 /*旧データ互換*/,
        Number = 12, NumberPin = 13 }

    // 注釈オブジェクト = 1レイヤ
    public class Obj
    {
        public ObjType Type;
        public PointF P1, P2;
        public PointF P3;    // 吹き出しの指し先
        public Color Stroke = Color.Red;
        public Color Fill = Color.Transparent;
        public float Width = 4f;
        public float FontSize = 28f;
        public bool BothEnds;  // 矢印: 始点にも矢印
        public int Dash;     // 線の種類 0=実線 1=細かなドット 2=短い破線 3=長い破線
        public string Text = "";
        public bool Visible = true;
        public Bitmap Img;   // Image レイヤ用 (共有・変更しない)
        // ビデオの注釈:表示するコマの範囲(TEnd < 0 = 最後まで)。TimeHidden = 表示中のコマでは範囲外(編集中の一時的な非表示)
        public int TStart, TEnd = -1;
        public bool TimeHidden;

        Bitmap mosaicCache; RectangleF mosaicKey; Bitmap mosaicBase; float mosaicBlock;

        public const string FontName = "Yu Gothic UI";

        public Obj Clone()
        {
            Obj o = (Obj)MemberwiseClone();
            o.mosaicCache = null; o.mosaicBase = null;
            return o;
        }

        public bool IsBoxType
        {
            get { return Type == ObjType.Rect || Type == ObjType.Ellipse || Type == ObjType.Highlight || Type == ObjType.Mosaic || Type == ObjType.Image || IsBalloon; }
        }
        public bool IsBalloon { get { return Type == ObjType.BalloonPointer || Type == ObjType.BalloonArrow || Type == ObjType.BalloonShadow; } }
        public bool HasText { get { return Type == ObjType.Text || IsBalloon || IsNumber; } }
        public bool IsNumber { get { return Type == ObjType.Number || Type == ObjType.NumberPin; } }
        public bool IsLineType { get { return Type == ObjType.Line || Type == ObjType.Arrow; } }

        public static RectangleF Norm(PointF a, PointF b)
        {
            return RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        public string Name
        {
            get
            {
                switch (Type)
                {
                    case ObjType.Rect: return "⬜ 四角形";
                    case ObjType.Ellipse: return "⭕ 楕円";
                    case ObjType.Line: return "➖ 線";
                    case ObjType.Arrow: return "➡️ 矢印";
                    case ObjType.Highlight: return "🖍️ 蛍光ペン";
                    case ObjType.Mosaic: return "▦ モザイク";
                    case ObjType.Image: return "🖼️ 画像";
                    case ObjType.BalloonPointer: return "💬 " + Short(Text);
                    case ObjType.BalloonArrow: return "👉 " + Short(Text);
                    case ObjType.BalloonShadow: return "🟥 " + Short(Text);
                    case ObjType.Number: return "🔢 番号スタンプ " + Short(Text);
                    case ObjType.NumberPin: return "📍 ピン " + Short(Text);
                    case ObjType.Text:
                        string t = (Text ?? "").Replace("\r", "").Replace("\n", " ");
                        if (t.Length > 12) t = t.Substring(0, 12) + "…";
                        return "🔤 " + t;
                }
                return "?";
            }
        }

        static string Short(string s)
        {
            string t = (s ?? "").Replace("\r", "").Replace("\n", " ");
            return t.Length > 10 ? t.Substring(0, 10) + "…" : t;
        }

        // ---- テキスト ----
        GraphicsPath TextPath()
        {
            GraphicsPath gp = new GraphicsPath();
            using (FontFamily ff = new FontFamily(FontName))
                gp.AddString(string.IsNullOrEmpty(Text) ? " " : Text, ff, (int)FontStyle.Bold, FontSize,
                             new PointF(P1.X + Pad, P1.Y + Pad), StringFormat.GenericTypographic);
            return gp;
        }
        float Pad { get { return Math.Max(4f, FontSize * 0.25f); } }

        // テキストの大きさに合わせて P2 を更新
        public void FitText()
        {
            if (Type != ObjType.Text) return;
            using (GraphicsPath gp = new GraphicsPath())
            using (FontFamily ff = new FontFamily(FontName))
            using (StringFormat sf = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                string t = string.IsNullOrEmpty(Text) ? " " : Text;
                gp.AddString(t, ff, (int)FontStyle.Bold, FontSize, PointF.Empty, sf);
                RectangleF b = gp.GetBounds();
                int lines = t.Split('\n').Length;
                float h = Math.Max(b.Bottom, FontSize * 1.3f * lines);
                float w = Math.Max(b.Right, FontSize * 0.5f);
                P2 = new PointF(P1.X + w + Pad * 2, P1.Y + h + Pad * 2);
            }
        }

        public RectangleF Bounds
        {
            get
            {
                if (IsNumber)
                {
                    float r = NumberRadius;
                    RectangleF c = new RectangleF(P1.X - r, P1.Y - r, r * 2, r * 2);
                    if (Type == ObjType.NumberPin)
                    {
                        PointF t = NumberTip;
                        c = RectangleF.Union(c, new RectangleF(t.X, t.Y, 0.01f, 0.01f));
                    }
                    return c;
                }
                return Norm(P1, P2);
            }
        }

        // ---- 番号スタンプ ----  P1=円の中心、P2=ピンの向き
        public float NumberRadius { get { return Math.Max(8f, FontSize * 0.75f); } }
        PointF NumberDir
        {
            get
            {
                float dx = P2.X - P1.X, dy = P2.Y - P1.Y;
                float l = (float)Math.Sqrt(dx * dx + dy * dy);
                if (l < 0.5f) return new PointF(1, 0);
                return new PointF(dx / l, dy / l);
            }
        }
        public PointF NumberTip
        {
            get { PointF d = NumberDir; float L = NumberRadius * 2.0f; return new PointF(P1.X + d.X * L, P1.Y + d.Y * L); }
        }

        void DrawNumber(Graphics g)
        {
            float r = NumberRadius;
            Color bc = BoxColor;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(255, bc)))
            {
                // 円とピン先は別々に塗る(1つのパスにすると向きの組み合わせで扇形に抜けるため)
                g.FillEllipse(b, P1.X - r, P1.Y - r, r * 2, r * 2);
                if (Type == ObjType.NumberPin)
                {
                    // 先端から円への接線で涙型にする
                    PointF d = NumberDir;
                    float L = r * 2.0f;
                    double th = Math.Acos(r / L);
                    double a0 = Math.Atan2(d.Y, d.X);
                    PointF t1 = new PointF(P1.X + (float)(Math.Cos(a0 + th) * r), P1.Y + (float)(Math.Sin(a0 + th) * r));
                    PointF t2 = new PointF(P1.X + (float)(Math.Cos(a0 - th) * r), P1.Y + (float)(Math.Sin(a0 - th) * r));
                    g.FillPolygon(b, new PointF[] { P1, t1, NumberTip, t2 });
                }
            }
            if (string.IsNullOrEmpty(Text)) return;
            using (GraphicsPath tp = new GraphicsPath())
            using (FontFamily ff = new FontFamily(FontName))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                float em = Text.Length >= 3 ? r * 0.85f : (Text.Length == 2 ? r * 1.05f : r * 1.25f);
                RectangleF box = new RectangleF(P1.X - r * 1.5f, P1.Y - r, r * 3f, r * 2f);
                tp.AddString(Text, ff, (int)FontStyle.Bold, em, box, sf);
                // 文字の見た目の中心を円の中心へ合わせる
                RectangleF tb = tp.GetBounds();
                using (Matrix m = new Matrix())
                {
                    m.Translate(P1.X - (tb.X + tb.Width / 2f), P1.Y - (tb.Y + tb.Height / 2f));
                    tp.Transform(m);
                }
                using (SolidBrush b = new SolidBrush(Stroke)) g.FillPath(b, tp);
            }
        }

        // ---- 描画 (画像座標系) ----
        public void Draw(Graphics g, Bitmap baseImg)
        {
            if (!Visible || TimeHidden) return;
            RectangleF r = Bounds;
            switch (Type)
            {
                case ObjType.Rect:
                    if (Fill.A > 0) using (SolidBrush b = new SolidBrush(Fill)) g.FillRectangle(b, r);
                    if (Width > 0) using (Pen p = new Pen(Stroke, Width)) { p.LineJoin = LineJoin.Miter; ApplyDash(p, Dash); g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height); }
                    break;
                case ObjType.Ellipse:
                    if (Fill.A > 0) using (SolidBrush b = new SolidBrush(Fill)) g.FillEllipse(b, r);
                    if (Width > 0) using (Pen p = new Pen(Stroke, Width)) { ApplyDash(p, Dash); g.DrawEllipse(p, r); }
                    break;
                case ObjType.Line:
                    using (Pen p = new Pen(Stroke, Math.Max(1f, Width)))
                    {
                        p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                        ApplyDash(p, Dash);
                        g.DrawLine(p, P1, P2);
                    }
                    break;
                case ObjType.Arrow:
                    DrawArrow(g);
                    break;
                case ObjType.Highlight:
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(Math.Min((int)Stroke.A, 110), Stroke))) g.FillRectangle(b, r);
                    break;
                case ObjType.Mosaic:
                    DrawMosaic(g, baseImg, r);
                    break;
                case ObjType.Image:
                    if (Img != null)
                    {
                        InterpolationMode im = g.InterpolationMode;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(Img, r, new RectangleF(0, 0, Img.Width, Img.Height), GraphicsUnit.Pixel);
                        g.InterpolationMode = im;
                    }
                    break;
                case ObjType.Text:
                    DrawText(g);
                    break;
                case ObjType.BalloonPointer:
                case ObjType.BalloonArrow:
                case ObjType.BalloonShadow:
                    DrawBalloon(g);
                    break;
                case ObjType.Number:
                case ObjType.NumberPin:
                    DrawNumber(g);
                    break;
            }
        }

        void DrawArrow(Graphics g) { DrawArrow(g, P1, P2, Stroke, Width, Dash, BothEnds); }

        // 線の種類を設定 (パターンは線の太さに対する比率)
        public static void ApplyDash(Pen p, int dash)
        {
            switch (dash)
            {
                case 1: p.DashCap = DashCap.Round; p.DashPattern = new float[] { 1f, 1.8f }; break;   // 細かなドット
                case 2: p.DashCap = DashCap.Flat; p.DashPattern = new float[] { 2.5f, 2f }; break;    // 短い破線
                case 3: p.DashCap = DashCap.Flat; p.DashPattern = new float[] { 6f, 2.5f }; break;    // 長い破線
                default: p.DashStyle = DashStyle.Solid; break;
            }
        }

        static void DrawArrow(Graphics g, PointF P1, PointF P2, Color Stroke, float Width, int dash, bool both)
        {
            float w = Math.Max(1f, Width);
            float dx = P2.X - P1.X, dy = P2.Y - P1.Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 1f) return;
            float ux = dx / len, uy = dy / len;
            float head = Math.Max(w * 3.5f, 12f);
            float maxHead = both ? len / 2f : len;
            if (head > maxHead) head = maxHead;
            float hw = head * 0.6f;
            PointF bp = new PointF(P2.X - ux * head, P2.Y - uy * head);   // 終点側の矢じりの付け根
            PointF bs = both ? new PointF(P1.X + ux * head, P1.Y + uy * head) : P1;   // 始点側
            using (SolidBrush b = new SolidBrush(Stroke))
            {
                if (len > head * (both ? 2 : 1))
                    using (Pen p = new Pen(Stroke, w))
                    {
                        p.StartCap = both ? LineCap.Flat : LineCap.Round; p.EndCap = LineCap.Flat;
                        ApplyDash(p, dash);
                        PointF s0 = both ? new PointF(bs.X - ux * 1f, bs.Y - uy * 1f) : P1;
                        g.DrawLine(p, s0, new PointF(bp.X + ux * 1f, bp.Y + uy * 1f));
                    }
                g.FillPolygon(b, new PointF[] {
                    P2, new PointF(bp.X - uy * hw, bp.Y + ux * hw), new PointF(bp.X + uy * hw, bp.Y - ux * hw) });
                if (both)
                    g.FillPolygon(b, new PointF[] {
                        P1, new PointF(bs.X - uy * hw, bs.Y + ux * hw), new PointF(bs.X + uy * hw, bs.Y - ux * hw) });
            }
        }

        void DrawText(Graphics g)
        {
            RectangleF r = Bounds;
            if (Fill.A > 0)
            {
                using (SolidBrush b = new SolidBrush(Fill))
                using (GraphicsPath bg = RoundRectF(r, Pad))
                    g.FillPath(b, bg);
            }
            using (GraphicsPath gp = TextPath())
            {
                if (Fill.A == 0)
                {
                    // 背景なしのときは白フチで読みやすく
                    using (Pen halo = new Pen(Color.FromArgb(Math.Min(255, (int)Stroke.A), Color.White), Math.Max(2f, FontSize / 7f)))
                    {
                        halo.LineJoin = LineJoin.Round;
                        g.DrawPath(halo, gp);
                    }
                }
                using (SolidBrush b = new SolidBrush(Stroke)) g.FillPath(b, gp);
            }
            if (Dash > 0) DrawDashedFrame(g, r, Pad);
        }

        // ---- 吹き出し ----
        // 吹き出しの色: 塗り=背景、線/文字の色=文字色
        Color BoxColor { get { return Fill.A > 0 ? Fill : Color.FromArgb(255, 69, 69); } }

        static PointF Center(RectangleF r) { return new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f); }

        // 矢印型吹き出しの外形 (P2 が P1 より左なら左向き)
        GraphicsPath BlockArrowPath(RectangleF r, out RectangleF textArea)
        {
            bool left = P2.X < P1.X;
            float h = r.Height, w = r.Width;
            float head = Math.Min(h * 0.75f, w * 0.45f);
            float sy1 = r.Y + h * 0.2f, sy2 = r.Bottom - h * 0.2f, cy = r.Y + h / 2f;
            GraphicsPath gp = new GraphicsPath();
            if (!left)
            {
                float hx = r.Right - head;
                gp.AddPolygon(new PointF[] {
                    new PointF(r.X, sy1), new PointF(hx, sy1), new PointF(hx, r.Y), new PointF(r.Right, cy),
                    new PointF(hx, r.Bottom), new PointF(hx, sy2), new PointF(r.X, sy2) });
                textArea = RectangleF.FromLTRB(r.X, sy1, hx + head * 0.25f, sy2);
            }
            else
            {
                float hx = r.X + head;
                gp.AddPolygon(new PointF[] {
                    new PointF(r.Right, sy1), new PointF(hx, sy1), new PointF(hx, r.Y), new PointF(r.X, cy),
                    new PointF(hx, r.Bottom), new PointF(hx, sy2), new PointF(r.Right, sy2) });
                textArea = RectangleF.FromLTRB(hx - head * 0.25f, sy1, r.Right, sy2);
            }
            return gp;
        }

        void DrawBalloon(Graphics g)
        {
            RectangleF r = Bounds;
            if (r.Width < 1 || r.Height < 1) return;
            Color bc = BoxColor;
            RectangleF textArea = r;
            float rad = Math.Max(3f, Math.Min(10f, Math.Min(r.Width, r.Height) * 0.08f));
            using (SolidBrush b = new SolidBrush(bc))
            {
                if (Type == ObjType.BalloonPointer)
                {
                    // 箱の中心から指し先へ矢印 (箱の下に描くので箱の縁から出て見える)
                    DrawArrow(g, Center(r), P3, bc, Math.Max(5f, Width * 1.5f), Dash, false);
                    using (GraphicsPath gp = RoundRectF(r, rad)) g.FillPath(b, gp);
                }
                else if (Type == ObjType.BalloonArrow)
                {
                    using (GraphicsPath gp = BlockArrowPath(r, out textArea))
                    using (Pen soft = new Pen(bc, Math.Max(2f, rad)))
                    {
                        soft.LineJoin = LineJoin.Round;
                        g.FillPath(b, gp);
                        g.DrawPath(soft, gp);   // 角を少し丸める
                    }
                }
                else
                {
                    // (旧データの影付き吹き出しは影なしの箱として表示)
                    using (GraphicsPath gp = RoundRectF(r, rad)) g.FillPath(b, gp);
                }
            }
            // 点線を選んだときは、箱の内側に文字色の枠を描く
            if (Dash > 0)
            {
                if (Type == ObjType.BalloonArrow)
                {
                    RectangleF ta2;
                    using (GraphicsPath gp = BlockArrowPath(RectangleF.Inflate(r, -Math.Max(3f, Width), -Math.Max(3f, Width)), out ta2))
                    using (Pen p = new Pen(Stroke, Math.Max(1.5f, Width * 0.6f)))
                    {
                        p.LineJoin = LineJoin.Round; ApplyDash(p, Dash);
                        g.DrawPath(p, gp);
                    }
                }
                else DrawDashedFrame(g, RectangleF.Inflate(r, -Math.Max(3f, Width), -Math.Max(3f, Width)), rad);
            }
            if (string.IsNullOrEmpty(Text)) return;
            using (GraphicsPath tp = new GraphicsPath())
            using (FontFamily ff = new FontFamily(FontName))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                RectangleF ta = textArea; ta.Inflate(-Pad * 0.5f, 0);
                tp.AddString(Text, ff, (int)FontStyle.Bold, FontSize, ta, sf);
                using (SolidBrush tb = new SolidBrush(Stroke)) g.FillPath(tb, tp);
            }
        }

        void DrawDashedFrame(Graphics g, RectangleF r, float rad)
        {
            if (r.Width < 2 || r.Height < 2) return;
            using (GraphicsPath gp = RoundRectF(r, rad))
            using (Pen p = new Pen(Stroke, Math.Max(1.5f, Width * 0.6f)))
            {
                ApplyDash(p, Dash);
                g.DrawPath(p, gp);
            }
        }

        static GraphicsPath RoundRectF(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d < 1) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        void DrawMosaic(Graphics g, Bitmap baseImg, RectangleF r)
        {
            if (baseImg == null) return;
            Rectangle ri = Rectangle.Round(r);
            ri.Intersect(new Rectangle(0, 0, baseImg.Width, baseImg.Height));
            if (ri.Width < 1 || ri.Height < 1) return;
            float block = Math.Max(4f, Width * 3f);
            if (mosaicCache == null || mosaicKey != (RectangleF)ri || mosaicBase != baseImg || mosaicBlock != block)
            {
                if (mosaicCache != null) mosaicCache.Dispose();
                int sw = Math.Max(1, (int)Math.Ceiling(ri.Width / block));
                int sh = Math.Max(1, (int)Math.Ceiling(ri.Height / block));
                mosaicCache = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
                using (Graphics mg = Graphics.FromImage(mosaicCache))
                {
                    mg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    mg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    mg.DrawImage(baseImg, new Rectangle(0, 0, sw, sh), ri, GraphicsUnit.Pixel);
                }
                mosaicKey = ri; mosaicBase = baseImg; mosaicBlock = block;
            }
            InterpolationMode im = g.InterpolationMode;
            PixelOffsetMode pm = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            float bw = mosaicCache.Width * block, bh = mosaicCache.Height * block;
            GraphicsState gs = g.Save();
            g.SetClip(ri, CombineMode.Intersect);
            g.DrawImage(mosaicCache, new RectangleF(ri.X, ri.Y, bw, bh), new RectangleF(0, 0, mosaicCache.Width, mosaicCache.Height), GraphicsUnit.Pixel);
            g.Restore(gs);
            g.InterpolationMode = im; g.PixelOffsetMode = pm;
        }

        // ---- ヒットテスト ----
        public bool HitTest(PointF p, float tol)
        {
            if (!Visible || TimeHidden) return false;
            RectangleF r = Bounds;
            float hw = Width / 2f + tol;
            switch (Type)
            {
                case ObjType.Image:
                    {
                        // 画像レイヤは透明な部分では選ばない
                        if (!r.Contains(p) || Img == null || r.Width < 1 || r.Height < 1) return false;
                        int ix = (int)((p.X - r.X) / r.Width * Img.Width), iy = (int)((p.Y - r.Y) / r.Height * Img.Height);
                        ix = Math.Max(0, Math.Min(Img.Width - 1, ix)); iy = Math.Max(0, Math.Min(Img.Height - 1, iy));
                        try { return Img.GetPixel(ix, iy).A > 0; } catch { return true; }
                    }
                case ObjType.Line:
                case ObjType.Arrow:
                    return SegDist(p, P1, P2) <= Math.Max(hw, Type == ObjType.Arrow ? Width * 1.8f : 0);
                case ObjType.Rect:
                    if (Fill.A > 0) { RectangleF a = r; a.Inflate(hw, hw); return a.Contains(p); }
                    {
                        RectangleF o = r; o.Inflate(hw, hw);
                        RectangleF i = r; i.Inflate(-hw, -hw);
                        return o.Contains(p) && !(i.Width > 0 && i.Height > 0 && i.Contains(p));
                    }
                case ObjType.Ellipse:
                    {
                        float rx = r.Width / 2f, ry = r.Height / 2f;
                        if (rx < 1 || ry < 1) { RectangleF a = r; a.Inflate(hw, hw); return a.Contains(p); }
                        float cx = r.X + rx, cy = r.Y + ry;
                        float nx = (p.X - cx) / rx, ny = (p.Y - cy) / ry;
                        float d = (float)Math.Sqrt(nx * nx + ny * ny);
                        if (Fill.A > 0) return d <= 1f + hw / Math.Min(rx, ry);
                        return Math.Abs(d - 1f) * Math.Min(rx, ry) <= hw;
                    }
                case ObjType.Number:
                case ObjType.NumberPin:
                    {
                        float dx = p.X - P1.X, dy = p.Y - P1.Y;
                        if (Math.Sqrt(dx * dx + dy * dy) <= NumberRadius + tol) return true;
                        return Type == ObjType.NumberPin && SegDist(p, P1, NumberTip) <= NumberRadius * 0.5f + tol;
                    }
                case ObjType.BalloonPointer:
                    {
                        RectangleF a = r; a.Inflate(tol, tol);
                        return a.Contains(p) || SegDist(p, Center(r), P3) <= Math.Max(3f, Width) / 2f + tol + 4f;
                    }
                default:
                    { RectangleF a = r; a.Inflate(tol, tol); return a.Contains(p); }
            }
        }

        static float SegDist(PointF p, PointF a, PointF b)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y;
            float l2 = dx * dx + dy * dy;
            float t = l2 > 0 ? ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2 : 0;
            t = Math.Max(0, Math.Min(1, t));
            float x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
            return (float)Math.Sqrt(x * x + y * y);
        }

        // ハンドル位置 (画像座標)
        public PointF[] Handles()
        {
            if (IsLineType) return new PointF[] { P1, P2 };
            if (IsNumber)
            {
                // 円の四隅=大きさ変更、ピンは先端=向き変更
                float r = NumberRadius;
                PointF[] c = { new PointF(P1.X - r, P1.Y - r), new PointF(P1.X + r, P1.Y - r),
                               new PointF(P1.X + r, P1.Y + r), new PointF(P1.X - r, P1.Y + r) };
                if (Type == ObjType.Number) return c;
                return new PointF[] { c[0], c[1], c[2], c[3], NumberTip };
            }
            if (Type == ObjType.BalloonPointer) return new PointF[] { P1, new PointF(P2.X, P1.Y), P2, new PointF(P1.X, P2.Y), P3 };
            if (IsBoxType) return new PointF[] { P1, new PointF(P2.X, P1.Y), P2, new PointF(P1.X, P2.Y) };
            return new PointF[0];
        }
        public void MoveHandle(int i, PointF p)
        {
            if (IsNumber)
            {
                if (i == 4) { P2 = p; return; }
                float r = Math.Max(Math.Abs(p.X - P1.X), Math.Abs(p.Y - P1.Y));
                FontSize = Math.Max(11f, Math.Min(200f, r / 0.75f));
                return;
            }
            if (IsLineType) { if (i == 0) P1 = p; else P2 = p; return; }
            if (i == 4) { P3 = p; return; }
            switch (i)
            {
                case 0: P1 = p; break;
                case 1: P2 = new PointF(p.X, P2.Y); P1 = new PointF(P1.X, p.Y); break;
                case 2: P2 = p; break;
                case 3: P1 = new PointF(p.X, P1.Y); P2 = new PointF(P2.X, p.Y); break;
            }
        }
        public void Offset(float dx, float dy)
        {
            P1 = new PointF(P1.X + dx, P1.Y + dy);
            P2 = new PointF(P2.X + dx, P2.Y + dy);
            P3 = new PointF(P3.X + dx, P3.Y + dy);
        }
    }

    // ドキュメント = 背景画像 + レイヤ(注釈)
    public class Doc
    {
        public Bitmap Base;
        public List<Obj> Objs = new List<Obj>();
        public string PngPath;
        public bool Dirty;

        public string ScapPath { get { return Path.ChangeExtension(PngPath, ".scap"); } }

        // レイヤ合成した画像を生成
        public Bitmap Render()
        {
            Bitmap b = new Bitmap(Base.Width, Base.Height, PixelFormat.Format32bppArgb);
            b.SetResolution(96, 96);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(Base, new Rectangle(0, 0, Base.Width, Base.Height), 0, 0, Base.Width, Base.Height, GraphicsUnit.Pixel);
                g.CompositingMode = CompositingMode.SourceOver;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                foreach (Obj o in Objs) if (o.Visible) o.Draw(g, Base);
            }
            return b;
        }

        public void Save()
        {
            using (Bitmap flat = Render()) flat.Save(PngPath, ImageFormat.Png);
            using (FileStream fs = new FileStream(ScapPath, FileMode.Create, FileAccess.Write))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                w.Write(new byte[] { (byte)'S', (byte)'C', (byte)'A', (byte)'P' });
                w.Write(4);
                WriteImg(w, Base);
                w.Write(Objs.Count);
                foreach (Obj o in Objs)
                {
                    w.Write((byte)o.Type);
                    w.Write(o.P1.X); w.Write(o.P1.Y); w.Write(o.P2.X); w.Write(o.P2.Y);
                    w.Write(o.P3.X); w.Write(o.P3.Y);
                    w.Write((byte)o.Dash);
                    w.Write(o.BothEnds);
                    w.Write(o.Stroke.ToArgb()); w.Write(o.Fill.ToArgb());
                    w.Write(o.Width); w.Write(o.FontSize);
                    w.Write(o.Text ?? "");
                    w.Write(o.Visible);
                    if (o.Img != null) { w.Write(true); WriteImg(w, o.Img); } else w.Write(false);
                }
            }
            Dirty = false;
        }

        static void WriteImg(BinaryWriter w, Bitmap b)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                b.Save(ms, ImageFormat.Png);
                w.Write((int)ms.Length);
                w.Write(ms.GetBuffer(), 0, (int)ms.Length);
            }
        }
        static Bitmap ReadImg(BinaryReader r)
        {
            int n = r.ReadInt32();
            return BytesToBitmap(r.ReadBytes(n));
        }

        // ファイルをロックしないで読み込み、32bppARGB に正規化
        public static Bitmap BytesToBitmap(byte[] data)
        {
            using (MemoryStream ms = new MemoryStream(data))
            using (Image img = Image.FromStream(ms))
                return ToArgb(img);
        }
        public static Bitmap ToArgb(Image img)
        {
            Bitmap b = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
            b.SetResolution(96, 96);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(img, new Rectangle(0, 0, img.Width, img.Height), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel);
            }
            return b;
        }

        public static Doc Load(string pngPath)
        {
            Doc d = new Doc();
            d.PngPath = pngPath;
            string sp = d.ScapPath;
            if (File.Exists(sp))
            {
                try
                {
                    using (FileStream fs = new FileStream(sp, FileMode.Open, FileAccess.Read))
                    using (BinaryReader r = new BinaryReader(fs))
                    {
                        byte[] magic = r.ReadBytes(4);
                        if (magic.Length == 4 && magic[0] == 'S' && magic[1] == 'C' && magic[2] == 'A' && magic[3] == 'P')
                        {
                            int ver = r.ReadInt32();
                            d.Base = ReadImg(r);
                            int n = r.ReadInt32();
                            for (int i = 0; i < n; i++)
                            {
                                Obj o = new Obj();
                                o.Type = (ObjType)r.ReadByte();
                                o.P1 = new PointF(r.ReadSingle(), r.ReadSingle());
                                o.P2 = new PointF(r.ReadSingle(), r.ReadSingle());
                                if (ver >= 2) o.P3 = new PointF(r.ReadSingle(), r.ReadSingle());
                                if (ver >= 3) o.Dash = r.ReadByte();
                                if (ver >= 4) o.BothEnds = r.ReadBoolean();
                                o.Stroke = Color.FromArgb(r.ReadInt32());
                                o.Fill = Color.FromArgb(r.ReadInt32());
                                o.Width = r.ReadSingle(); o.FontSize = r.ReadSingle();
                                o.Text = r.ReadString();
                                o.Visible = r.ReadBoolean();
                                if (r.ReadBoolean()) o.Img = ReadImg(r);
                                d.Objs.Add(o);
                            }
                            return d;
                        }
                    }
                }
                catch { d.Objs.Clear(); if (d.Base != null) { d.Base.Dispose(); d.Base = null; } }
            }
            d.Base = BytesToBitmap(File.ReadAllBytes(pngPath));
            return d;
        }

        // 塗りつぶし。クリック位置から色が近い連続領域を指定色で塗る
        // thr: 許容範囲 0..255 (各チャンネルの差の最大値で判定)
        public static Bitmap FloodFill(Bitmap src, int sx, int sy, Color col, int thr)
        {
            Bitmap b = (Bitmap)src.Clone(new Rectangle(0, 0, src.Width, src.Height), PixelFormat.Format32bppArgb);
            b.SetResolution(96, 96);
            int w = b.Width, h = b.Height;
            if (sx < 0 || sy < 0 || sx >= w || sy >= h) return b;
            BitmapData bd = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int stride = bd.Stride, len = stride * h;
                byte[] px = new byte[len];
                Marshal.Copy(bd.Scan0, px, 0, len);
                byte[] org = (byte[])px.Clone();
                int s0 = sy * stride + sx * 4;
                int kb = org[s0], kg = org[s0 + 1], kr = org[s0 + 2], ka = org[s0 + 3];
                bool[] done = new bool[w * h];
                Stack<int> st = new Stack<int>();
                st.Push(sy * w + sx);
                byte cb = col.B, cg = col.G, cr = col.R, ca = col.A;
                while (st.Count > 0)
                {
                    int idx = st.Pop();
                    int y = idx / w, x = idx % w;
                    if (done[idx]) continue;
                    int xl = x, xr = x;
                    while (xl - 1 >= 0 && !done[y * w + xl - 1] && Near(org, y * stride + (xl - 1) * 4, kb, kg, kr, ka, thr)) xl--;
                    while (xr + 1 < w && !done[y * w + xr + 1] && Near(org, y * stride + (xr + 1) * 4, kb, kg, kr, ka, thr)) xr++;
                    for (int i = xl; i <= xr; i++)
                    {
                        done[y * w + i] = true;
                        int o = y * stride + i * 4;
                        px[o] = cb; px[o + 1] = cg; px[o + 2] = cr; px[o + 3] = ca;
                    }
                    for (int pass = 0; pass < 2; pass++)
                    {
                        int ny = pass == 0 ? y - 1 : y + 1;
                        if (ny < 0 || ny >= h) continue;
                        bool prev = false;
                        for (int i = xl; i <= xr; i++)
                        {
                            bool m = !done[ny * w + i] && Near(org, ny * stride + i * 4, kb, kg, kr, ka, thr);
                            if (m && !prev) st.Push(ny * w + i);
                            prev = m;
                        }
                    }
                }
                Marshal.Copy(px, 0, bd.Scan0, len);
            }
            finally { b.UnlockBits(bd); }
            return b;
        }

        static bool Near(byte[] p, int i, int b, int g, int r, int a, int thr)
        {
            return Math.Abs(p[i] - b) <= thr && Math.Abs(p[i + 1] - g) <= thr &&
                   Math.Abs(p[i + 2] - r) <= thr && Math.Abs(p[i + 3] - a) <= thr;
        }

        // 指定色を透明にした新しい画像を返す (透明化)
        public static Bitmap MakeColorTransparent(Bitmap src, Color key, int tol)
        {
            Bitmap b = (Bitmap)src.Clone(new Rectangle(0, 0, src.Width, src.Height), PixelFormat.Format32bppArgb);
            BitmapData bd = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int len = bd.Stride * b.Height;
                byte[] px = new byte[len];
                Marshal.Copy(bd.Scan0, px, 0, len);
                for (int y = 0; y < b.Height; y++)
                {
                    int row = y * bd.Stride;
                    for (int x = 0; x < b.Width; x++)
                    {
                        int i = row + x * 4;
                        if (Math.Abs(px[i] - key.B) <= tol && Math.Abs(px[i + 1] - key.G) <= tol && Math.Abs(px[i + 2] - key.R) <= tol)
                        { px[i] = 0; px[i + 1] = 0; px[i + 2] = 0; px[i + 3] = 0; }
                    }
                }
                Marshal.Copy(px, 0, bd.Scan0, len);
            }
            finally { b.UnlockBits(bd); }
            return b;
        }
    }
}
