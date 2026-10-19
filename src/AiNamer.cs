using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace SimpleCap
{
    // ================= AI で名前付け(OpenAI / Claude / OpenRouter) =================
    public static class AiNamer
    {
        public const string Prompt = "この画像に合ったファイル名を20文字以内で付けてください";
        // 返答を扱いやすくするための補足(ファイル名だけを返してもらう)
        const string PromptFormat = "\n\nファイル名だけを1行で答えてください(拡張子・説明・かぎかっこは不要)。";

        public static readonly string[] Providers = { "openai", "claude", "openrouter" };
        public static readonly string[] ProviderNames = { "OpenAI", "Claude (Anthropic)", "OpenRouter" };

        public static string DefaultUrl(string p)
        {
            if (p == "claude") return "https://api.anthropic.com/v1/messages";
            if (p == "openrouter") return "https://openrouter.ai/api/v1/chat/completions";
            return "https://api.openai.com/v1/chat/completions";
        }
        public static string DefaultModel(string p)
        {
            if (p == "claude") return "claude-haiku-4-5";
            if (p == "openrouter") return "google/gemini-2.5-flash-lite";
            return "gpt-4o-mini";
        }

        // ---- 設定(シークレットキーは Windows のユーザーごとの暗号化 DPAPI で保存) ----
        public static string ActiveProvider
        {
            get
            {
                string v;
                if (Program.LoadSettings().TryGetValue("ai.provider", out v) && Array.IndexOf(Providers, v) >= 0) return v;
                return "openai";
            }
        }
        public static string GetUrl(Dictionary<string, string> st, string p)
        {
            string v; return st.TryGetValue("ai." + p + ".url", out v) && v != "" ? v : DefaultUrl(p);
        }
        public static string GetModel(Dictionary<string, string> st, string p)
        {
            string v;
            if (!st.TryGetValue("ai." + p + ".model", out v) || v == "") return DefaultModel(p);
            if (p == "openrouter" && v == "openai/gpt-4o-mini") return DefaultModel(p);   // 以前の既定値は新しい既定値に
            return v;
        }
        public static string GetKey(Dictionary<string, string> st, string p)
        {
            string v;
            if (!st.TryGetValue("ai." + p + ".key", out v) || v == "") return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(v), null, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }
        public static void SetKey(Dictionary<string, string> st, string p, string key)
        {
            st["ai." + p + ".key"] = key == "" ? "" :
                Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
        }

        // ---- AI に送る画像(長い辺 1568px までに縮小した JPEG) ----
        public static byte[] ToJpeg(Image img)
        {
            int max = 1568;
            float z = Math.Min(1f, (float)max / Math.Max(img.Width, img.Height));
            int w = Math.Max(1, (int)(img.Width * z)), h = Math.Max(1, (int)(img.Height * z));
            using (Bitmap b = new Bitmap(w, h, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.Clear(Color.White);   // 透明部分は白に
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(img, 0, 0, w, h);
                }
                ImageCodecInfo jpg = null;
                foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders()) if (c.FormatID == ImageFormat.Jpeg.Guid) jpg = c;
                EncoderParameters ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
                using (MemoryStream ms = new MemoryStream()) { b.Save(ms, jpg, ep); return ms.ToArray(); }
            }
        }

        // ---- 問い合わせ(別スレッドで呼ぶ)。戻り値=ファイル名に使える名前 ----
        public static string Ask(byte[] jpeg)
        {
            Dictionary<string, string> st = Program.LoadSettings();
            string p = ActiveProvider;
            string url = GetUrl(st, p), model = GetModel(st, p), key = GetKey(st, p);
            if (key == "") throw new Exception("API のシークレットキーが設定されていません。⚙️ 設定の「🤖 AI で名前付け」で入力してください。");
            string b64 = Convert.ToBase64String(jpeg);
            string text = Prompt + PromptFormat;
            object body;
            if (p == "claude")
                body = new Dictionary<string, object> {
                    { "model", model }, { "max_tokens", 200 },
                    { "messages", new object[] { new Dictionary<string, object> {
                        { "role", "user" },
                        { "content", new object[] {
                            new Dictionary<string, object> { { "type", "image" }, { "source", new Dictionary<string, object> {
                                { "type", "base64" }, { "media_type", "image/jpeg" }, { "data", b64 } } } },
                            new Dictionary<string, object> { { "type", "text" }, { "text", text } } } } } } } };
            else
                body = new Dictionary<string, object> {
                    { "model", model },
                    { "messages", new object[] { new Dictionary<string, object> {
                        { "role", "user" },
                        { "content", new object[] {
                            new Dictionary<string, object> { { "type", "text" }, { "text", text } },
                            new Dictionary<string, object> { { "type", "image_url" }, { "image_url", new Dictionary<string, object> {
                                { "url", "data:image/jpeg;base64," + b64 } } } } } } } } } };

            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            byte[] data = Encoding.UTF8.GetBytes(js.Serialize(body));

            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = 60000;
            if (p == "claude")
            {
                req.Headers.Add("x-api-key", key);
                req.Headers.Add("anthropic-version", "2023-06-01");
            }
            else
            {
                req.Headers.Add("Authorization", "Bearer " + key);
                if (p == "openrouter") { req.Headers.Add("X-Title", "SimpleCap"); }
            }
            using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            string resp;
            try
            {
                using (WebResponse r = req.GetResponse())
                using (StreamReader sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8)) resp = sr.ReadToEnd();
            }
            catch (WebException ex)
            {
                string detail = "";
                if (ex.Response != null)
                    using (StreamReader sr = new StreamReader(ex.Response.GetResponseStream(), Encoding.UTF8)) detail = sr.ReadToEnd();
                if (detail.Length > 400) detail = detail.Substring(0, 400);
                throw new Exception(ex.Message + (detail != "" ? "\n" + detail : ""));
            }

            // 返答から文章を取り出す
            Dictionary<string, object> root = js.DeserializeObject(resp) as Dictionary<string, object>;
            string answer = null;
            try
            {
                if (p == "claude")
                {
                    foreach (object o in (IEnumerable)root["content"])
                    {
                        Dictionary<string, object> c = o as Dictionary<string, object>;
                        if (c != null && c.ContainsKey("text")) { answer = (string)c["text"]; break; }
                    }
                }
                else
                {
                    object[] choices = (object[])root["choices"];
                    Dictionary<string, object> msg = (Dictionary<string, object>)((Dictionary<string, object>)choices[0])["message"];
                    answer = msg["content"] as string;
                }
            }
            catch { }
            if (string.IsNullOrEmpty(answer)) throw new Exception("AI の返答を読み取れませんでした:\n" + (resp.Length > 300 ? resp.Substring(0, 300) : resp));
            string name = CleanName(answer);
            if (name == "") throw new Exception("ファイル名に使える名前が返ってきませんでした: " + answer);
            return name;
        }

        // 返答をファイル名に使える 20 文字以内の名前に整える
        public static string CleanName(string s)
        {
            s = s.Trim();
            foreach (string line in s.Split('\n')) { if (line.Trim() != "") { s = line.Trim(); break; } }
            s = s.Trim('"', '\'', '「', '」', '『', '』', '`', '*', ' ', '　');
            if (s.StartsWith("ファイル名:") || s.StartsWith("ファイル名:")) s = s.Substring(6).Trim();
            string ext = Path.GetExtension(s).ToLowerInvariant();
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".mp4") s = s.Substring(0, s.Length - ext.Length);
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            foreach (char c in "\\/:*?\"<>|") s = s.Replace(c, '_');   // Windows で使えない文字
            s = s.Replace(' ', '_').Replace('　', '_').Trim('.', '_');
            // 20 文字(見た目の文字数)まで
            StringInfo si = new StringInfo(s);
            if (si.LengthInTextElements > 20) s = si.SubstringByTextElements(0, 20);
            return s.Trim('.', '_');
        }
    }
}
