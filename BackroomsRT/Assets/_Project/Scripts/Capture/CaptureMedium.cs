using UnityEngine;

namespace Backrooms.Capture
{
    /// <summary>
    /// 参照画像を作った「レンズと記録系」を、書き出しの直前に掛ける。
    ///
    /// レンダは理想的なピンホールで、針のように鮮鋭で、黒がどこまでも沈む。
    /// 一方で参照画像は実在のレンズと記録系を通っていて、そこには必ず
    ///     * 樽型の歪み        直線がわずかに膨らむ
    ///     * 有限の MTF        細部が眠い
    ///     * ベーリンググレア  画面全体の光がにじんで黒が浮き、明部の頭が寝る
    /// が入っている。これは演出ではなく、参照画像がそう写っている理由そのもの。
    /// 値は tools/fit-capture-medium.py が参照画像から実測したもの。
    ///
    /// HDRP の LensDistortion を使わないのは、その intensity と実際の歪み量の
    /// 対応が分からず、実測した k1 に合わせられないため。ここでは
    ///     rd = ru (1 + k1 ru^2)
    /// をそのまま解いて引く。歪みで画面の外から引くことになる分は、
    /// カメラ側の画角を Overscan だけ広げて確保する。
    /// </summary>
    public static class CaptureMedium
    {
        /// <summary>k1 の歪みを掛けたとき、画面の隅が元画像のどこまで外側を要求するか。</summary>
        public static float RequiredOverscan(float k1, int w, int h, float fovV)
        {
            if (Mathf.Abs(k1) < 1e-6f) return 1f;
            float f = (h * 0.5f) / Mathf.Tan(fovV * 0.5f * Mathf.Deg2Rad);
            float rd = Mathf.Sqrt(w * w + h * h) * 0.5f / f;
            float ru = SolveRu(rd, k1);
            return Mathf.Max(1f, ru / rd) * 1.02f;
        }

        static float SolveRu(float rd, float k1)
        {
            float ru = rd;
            for (int i = 0; i < 24; i++)
            {
                float fv = ru * (1f + k1 * ru * ru) - rd;
                float fp = 1f + 3f * k1 * ru * ru;
                if (Mathf.Abs(fp) < 1e-5f) break;
                ru -= fv / fp;
            }
            return ru;
        }

        static float ToLinear(float c)
            => c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);

        static float ToSrgb(float c)
        {
            c = Mathf.Max(c, 0f);
            return c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
        }

        /// <param name="k1">放射歪み。負が樽型</param>
        /// <param name="overscan">カメラ側で広げてある倍率</param>
        /// <param name="blurSigma">MTF に相当するガウス幅 (px)</param>
        /// <param name="glare">グレアの混合率</param>
        /// <param name="glareSigma">グレアの広がり (px)</param>
        /// <param name="gain">
        /// チャンネルごとの倍率。参照と比べたときの色温度の差をここで合わせる。
        /// グレアを足したぶんの明るさもここで戻す。
        /// </param>
        public static void Apply(Texture2D tex, float k1, float overscan,
                                 float blurSigma, float glare, float glareSigma, Color gain)
        {
            int w = tex.width, h = tex.height;
            var src = tex.GetPixels();
            var lin = new float[w * h * 3];
            for (int i = 0; i < w * h; i++)
            {
                lin[i * 3 + 0] = ToLinear(src[i].r);
                lin[i * 3 + 1] = ToLinear(src[i].g);
                lin[i * 3 + 2] = ToLinear(src[i].b);
            }

            if (Mathf.Abs(k1) > 1e-6f || Mathf.Abs(overscan - 1f) > 1e-6f)
                lin = Warp(lin, w, h, k1, overscan);
            if (blurSigma > 0.05f)
                lin = Blur(lin, w, h, blurSigma);
            if (glare > 1e-4f && glareSigma > 1f)
            {
                // ベーリンググレアは「足される」光。混合として書くと元の画を
                // (1-g) 倍に縮めてしまい、細部のコントラストまで落ちる。
                var wide = Blur(lin, w, h, glareSigma, 8);
                for (int i = 0; i < lin.Length; i++)
                    lin[i] += glare * wide[i];
            }

            var dst = new Color[w * h];
            for (int i = 0; i < w * h; i++)
                dst[i] = new Color(ToSrgb(lin[i * 3 + 0] * gain.r),
                                   ToSrgb(lin[i * 3 + 1] * gain.g),
                                   ToSrgb(lin[i * 3 + 2] * gain.b), 1f);
            tex.SetPixels(dst);
            tex.Apply();
        }

        /// <summary>歪みを掛ける。出力画素が元のどこから来るかを解いて引く。</summary>
        static float[] Warp(float[] s, int w, int h, float k1, float overscan)
        {
            var o = new float[s.Length];
            float cx = w * 0.5f, cy = h * 0.5f;
            float rmax = Mathf.Sqrt(cx * cx + cy * cy);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float qx = x + 0.5f - cx, qy = y + 0.5f - cy;
                    float r = Mathf.Sqrt(qx * qx + qy * qy);
                    float scale = 1f / overscan;
                    if (r > 1e-4f)
                    {
                        float rd = r / rmax;
                        float ru = SolveRu(rd, k1);
                        scale = (ru / rd) / overscan;
                    }
                    Sample(s, w, h, cx + qx * scale - 0.5f, cy + qy * scale - 0.5f,
                           o, (y * w + x) * 3);
                }
            }
            return o;
        }

        static void Sample(float[] s, int w, int h, float fx, float fy, float[] o, int oi)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, w - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, h - 1);
            int x1 = Mathf.Min(x0 + 1, w - 1), y1 = Mathf.Min(y0 + 1, h - 1);
            float tx = Mathf.Clamp01(fx - x0), ty = Mathf.Clamp01(fy - y0);
            for (int c = 0; c < 3; c++)
            {
                float a = Mathf.Lerp(s[(y0 * w + x0) * 3 + c], s[(y0 * w + x1) * 3 + c], tx);
                float b = Mathf.Lerp(s[(y1 * w + x0) * 3 + c], s[(y1 * w + x1) * 3 + c], tx);
                o[oi + c] = Mathf.Lerp(a, b, ty);
            }
        }

        /// <summary>
        /// 分離型のガウスぼかし。広いグレアは 1 画素ずつ回すと遅いので、
        /// down だけ縮めてから掛けて戻す。
        /// </summary>
        static float[] Blur(float[] s, int w, int h, float sigma, int down = 1)
        {
            if (down > 1)
            {
                int dw = Mathf.Max(1, w / down), dh = Mathf.Max(1, h / down);
                var small = new float[dw * dh * 3];
                var cnt = new float[dw * dh];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int di = Mathf.Min(y / down, dh - 1) * dw + Mathf.Min(x / down, dw - 1);
                        for (int c = 0; c < 3; c++) small[di * 3 + c] += s[(y * w + x) * 3 + c];
                        cnt[di] += 1f;
                    }
                for (int i = 0; i < dw * dh; i++)
                    for (int c = 0; c < 3; c++) small[i * 3 + c] /= Mathf.Max(cnt[i], 1f);
                small = Blur(small, dw, dh, sigma / down);
                var big = new float[s.Length];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        Sample(small, dw, dh, (x + 0.5f) / down - 0.5f, (y + 0.5f) / down - 0.5f,
                               big, (y * w + x) * 3);
                return big;
            }

            int rad = Mathf.Max(1, Mathf.CeilToInt(sigma * 3f));
            var k = new float[rad * 2 + 1];
            float sum = 0f;
            for (int i = -rad; i <= rad; i++)
            {
                k[i + rad] = Mathf.Exp(-(i * i) / (2f * sigma * sigma));
                sum += k[i + rad];
            }
            for (int i = 0; i < k.Length; i++) k[i] /= sum;

            var tmp = new float[s.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < 3; c++)
                    {
                        float a = 0f;
                        for (int i = -rad; i <= rad; i++)
                            a += k[i + rad] * s[(y * w + Mathf.Clamp(x + i, 0, w - 1)) * 3 + c];
                        tmp[(y * w + x) * 3 + c] = a;
                    }
            var o = new float[s.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < 3; c++)
                    {
                        float a = 0f;
                        for (int i = -rad; i <= rad; i++)
                            a += k[i + rad] * tmp[(Mathf.Clamp(y + i, 0, h - 1) * w + x) * 3 + c];
                        o[(y * w + x) * 3 + c] = a;
                    }
            return o;
        }
    }
}
