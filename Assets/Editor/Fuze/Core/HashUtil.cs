using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace ProjectMerger
{
    public static class HashUtil
    {
        public static string Md5File(string absolutePath)
        {
            using (var md5 = MD5.Create())
            using (var fs = new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var bytes = md5.ComputeHash(fs);
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// MD5 of a text file after normalization: UTF-8 BOM stripped, CRLF/CR→LF,
        /// trailing whitespace per line removed, trailing blank lines collapsed.
        /// Returns null if the file can't be read.
        /// </summary>
        public static string Md5NormalizedText(string absolutePath)
        {
            string text;
            try { text = File.ReadAllText(absolutePath, Encoding.UTF8); }
            catch { return null; }

            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');

            var lines = text.Split('\n');
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < lines.Length; i++) sb.Append(lines[i].TrimEnd()).Append('\n');
            while (sb.Length > 1 && sb[sb.Length - 1] == '\n' && sb[sb.Length - 2] == '\n') sb.Length--;

            using (var md5 = MD5.Create())
            {
                var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) hex.Append(b.ToString("x2"));
                return hex.ToString();
            }
        }

        /// <summary>
        /// Difference hash (dHash) for textures. Produces a 64-bit perceptual hash. Returns 0 on failure.
        /// </summary>
        public static ulong DHashImage(string absolutePath)
        {
            byte[] raw;
            try { raw = File.ReadAllBytes(absolutePath); }
            catch { return 0UL; }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!tex.LoadImage(raw)) return 0UL;
                return ComputeDHash(tex);
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }
        }

        static ulong ComputeDHash(Texture2D src)
        {
            const int W = 9, H = 8;
            var rt = RenderTexture.GetTemporary(W, H, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tmp = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tmp.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tmp.Apply(false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            var px = tmp.GetPixels32();
            Object.DestroyImmediate(tmp);

            ulong hash = 0UL;
            int bit = 0;
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W - 1; x++)
                {
                    var a = px[y * W + x];
                    var b = px[y * W + x + 1];
                    float la = 0.299f * a.r + 0.587f * a.g + 0.114f * a.b;
                    float lb = 0.299f * b.r + 0.587f * b.g + 0.114f * b.b;
                    if (la > lb) hash |= (1UL << bit);
                    bit++;
                }
            }
            return hash;
        }

        public static int HammingDistance(ulong a, ulong b)
        {
            ulong x = a ^ b;
            int count = 0;
            while (x != 0) { count += (int)(x & 1UL); x >>= 1; }
            return count;
        }
    }
}
