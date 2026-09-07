using System;
using UnityEngine;

namespace Backrooms.Audio
{
    /// <summary>
    /// 音を合成する。ファイルは持たない。
    ///
    /// 素材を落としてこないのは手抜きではない。Backrooms の音は
    /// 「何の音か」がはっきりしている。磁気安定器は電源 60Hz の 2 倍、
    /// つまり 120Hz で鉄心が鳴り、その倍音が並ぶ。分かっているものを
    /// 拾い物で近似するより、そのまま書いたほうが正確で、
    /// 帰属表記も要らず、seed を変えれば何通りでも出る。
    ///
    /// ループの継ぎ目が鳴らないようにしてある。
    ///   * 音程のあるものは周期の整数倍の周波数しか使わない
    ///   * 雑音系は余分に作っておいて、末尾を頭に重ねて段差を消す
    /// これをしないと 1 秒ごとにプツッと入り、それが一番安っぽく聞こえる。
    /// </summary>
    public static class SoundBank
    {
        public const int StepPairs = 3;     // 靴の種類
        public const int StepVariants = 3;  // 同じ足でも 1 歩ずつ違う
        public const int Steps = StepPairs * 2 * StepVariants;

        /// <summary>Footsteps の添字。</summary>
        public static int StepIndex(int pair, bool right, int variant)
            => ((pair * 2 + (right ? 1 : 0)) * StepVariants + variant) % Steps;

        public static AudioClip Ballast { get; private set; }
        public static AudioClip BallastFail { get; private set; }
        public static AudioClip Hvac { get; private set; }
        public static AudioClip RoomTone { get; private set; }
        public static AudioClip[] Footsteps { get; private set; }
        public static AudioClip[] Clicks { get; private set; }
        public static string Report { get; private set; }

        static int _fs;

        public static void Build(int seed = 1996)
        {
            if (Ballast != null) return;
            _fs = AudioSettings.outputSampleRate;
            if (_fs <= 0) _fs = 48000;

            Ballast = Clip("Snd_Ballast", BallastHum(1.0f, seed, false));
            BallastFail = Clip("Snd_BallastFail", BallastHum(0.5f, seed + 7, true));
            Hvac = Clip("Snd_Hvac", HvacBed(4.0f, seed + 11));
            RoomTone = Clip("Snd_RoomTone", Tone(4.0f, seed + 23));

            // 靴 1 足につき左右、さらに 1 歩ずつ違う波形を持たせる。
            // 左足がいつも同じ波形だと、残響が付いていても機械に聞こえる。
            Footsteps = new AudioClip[Steps];
            for (int p = 0; p < StepPairs; p++)
                for (int f = 0; f < 2; f++)
                    for (int v = 0; v < StepVariants; v++)
                        Footsteps[StepIndex(p, f == 1, v)] =
                            Clip(string.Format("Snd_Step{0}{1}{2}", p, f == 1 ? "R" : "L", v),
                                 Footstep(seed + 100 + p * 37, f == 1, v));

            Clicks = new AudioClip[3];
            for (int i = 0; i < 3; i++)
                Clicks[i] = Clip("Snd_Click" + i, Click(seed + 200 + i * 17));

            Report = string.Format(
                "音源を合成: {0} Hz / 安定器 {1}s ループ / 空調 {2}s / 足音 {3} 種 / クリック {4} 種",
                _fs, 1.0f, 4.0f, Steps, 3);
        }

        static AudioClip Clip(string name, float[] data)
        {
            var c = AudioClip.Create(name, data.Length, 1, _fs, false);
            c.SetData(data, 0);
            return c;
        }

        // ------------------------------------------------------------ 乱数

        sealed class Rng
        {
            uint _s;
            public Rng(int seed) { _s = (uint)(seed * 2654435761u) | 1u; }
            public float Unit() { _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5; return (_s & 0xFFFFFF) / 16777215f; }
            public float Bi() { return Unit() * 2f - 1f; }
        }

        // ---------------------------------------------------------- フィルタ

        /// <summary>
        /// 繋がる雑音を作る。
        ///
        /// フィルタの状態だけを周期的にしても足りない。元の白色雑音は
        /// 末尾と先頭が無関係なので、そこで必ず段差ができる。実測で
        /// 部屋の空気は実効値の 145% の段差があり、4 秒ごとに鳴っていた。
        /// 余分に作っておいて、末尾を頭に重ねて消す。
        /// </summary>
        static float[] NoiseLoop(int n, float lo, float hi, int passes, int seed)
        {
            int warm = 4096;
            int fade = Mathf.Min(n / 4, Mathf.RoundToInt(0.25f * _fs));
            var src = new float[warm + n + fade];
            var rng = new Rng(seed);
            for (int i = 0; i < src.Length; i++) src[i] = rng.Bi();
            for (int p = 0; p < passes; p++) { if (hi > 0f) Lp(src, hi); }
            if (lo > 0f) { for (int p = 0; p < passes; p++) Hp(src, lo); }

            var b = new float[n];
            for (int i = 0; i < n; i++) b[i] = src[warm + i];
            // out[i] (i<fade) は「頭」と「一周ぶん先の同じ位置」を混ぜたもの。
            // out[n-1] と src[warm+n] は隣り合う標本なので、輪にしても繋がる。
            for (int i = 0; i < fade; i++)
            {
                float w = i / (float)fade;
                b[i] = src[warm + i] * w + src[warm + n + i] * (1f - w);
            }
            return b;
        }

        /// <summary>
        /// 周期を保ったままのローパス。1 周ぶん空回しして状態を作ってから書く。
        /// 音程のある成分にだけ使う。雑音には NoiseLoop を使うこと。
        /// </summary>
        static void LoopLp(float[] b, float fc, int passes)
        {
            float a = 1f - Mathf.Exp(-2f * Mathf.PI * fc / _fs);
            for (int p = 0; p < passes; p++)
            {
                float y = 0f;
                for (int i = 0; i < b.Length; i++) y += a * (b[i] - y);      // 助走
                for (int i = 0; i < b.Length; i++) { y += a * (b[i] - y); b[i] = y; }
            }
        }

        static void LoopHp(float[] b, float fc, int passes)
        {
            float a = 1f - Mathf.Exp(-2f * Mathf.PI * fc / _fs);
            for (int p = 0; p < passes; p++)
            {
                float y = 0f;
                for (int i = 0; i < b.Length; i++) y += a * (b[i] - y);
                for (int i = 0; i < b.Length; i++) { y += a * (b[i] - y); b[i] -= y; }
            }
        }

        static void Normalize(float[] b, float peak)
        {
            float m = 0f;
            for (int i = 0; i < b.Length; i++) { float v = Mathf.Abs(b[i]); if (v > m) m = v; }
            if (m < 1e-6f) return;
            float k = peak / m;
            for (int i = 0; i < b.Length; i++) b[i] *= k;
        }

        // ------------------------------------------------------------ 音

        /// <summary>
        /// 磁気安定器のうなり。北米の 60Hz 電源なので鉄心は 120Hz で鳴る。
        /// 倍音は 240/360/480 と落ちていき、蛍光管そのものからは
        /// 1〜3kHz の細かい「ジー」が少しだけ乗る。
        /// </summary>
        static float[] BallastHum(float seconds, int seed, bool failing)
        {
            int n = Mathf.RoundToInt(seconds * _fs);
            var b = new float[n];
            var rng = new Rng(seed);
            // 秒数の逆数の整数倍だけを使えば、必ずきれいに繋がる
            float baseHz = 120f;
            float[] amp = failing
                ? new[] { 1.00f, 0.62f, 0.44f, 0.30f, 0.20f, 0.13f }
                : new[] { 1.00f, 0.34f, 0.19f, 0.11f, 0.07f, 0.04f };
            for (int h = 0; h < amp.Length; h++)
            {
                float f = baseHz * (h + 1);
                if (Mathf.Abs(f * seconds - Mathf.Round(f * seconds)) > 1e-4f) continue;
                float ph = rng.Unit() * 2f * Mathf.PI;
                float w = 2f * Mathf.PI * f / _fs;
                for (int i = 0; i < n; i++) b[i] += amp[h] * Mathf.Sin(w * i + ph);
            }
            // 電源の非対称でわずかに 60Hz が残る
            {
                float w = 2f * Mathf.PI * 60f / _fs, ph = rng.Unit() * 2f * Mathf.PI;
                for (int i = 0; i < n; i++) b[i] += 0.06f * Mathf.Sin(w * i + ph);
            }
            // 管の細かいジー
            var sz = NoiseLoop(n, 1100f, failing ? 4200f : 2600f, 2, seed + 3);
            Normalize(sz, failing ? 0.42f : 0.13f);
            for (int i = 0; i < n; i++) b[i] += sz[i];

            // ゆっくりした振幅のゆらぎ。2Hz は 1 秒ループの整数倍。
            float wm = 2f * Mathf.PI * 2f / _fs;
            float depth = failing ? 0.22f : 0.05f;
            for (int i = 0; i < n; i++) b[i] *= 1f + depth * Mathf.Sin(wm * i);

            Normalize(b, 0.55f);
            return b;
        }

        /// <summary>空調。低い唸りと、その上に薄く乗る風の音。</summary>
        static float[] HvacBed(float seconds, int seed)
        {
            int n = Mathf.RoundToInt(seconds * _fs);
            var low = NoiseLoop(n, 26f, 110f, 3, seed);
            var air = NoiseLoop(n, 260f, 1400f, 2, seed + 5);
            Normalize(low, 0.85f);
            Normalize(air, 0.16f);
            var b = new float[n];
            for (int i = 0; i < n; i++) b[i] = low[i] + air[i];
            // 送風機の回転むら。0.25Hz は 4 秒ループの整数倍。
            float w = 2f * Mathf.PI * 0.25f / _fs;
            for (int i = 0; i < n; i++) b[i] *= 1f + 0.10f * Mathf.Sin(w * i);
            Normalize(b, 0.6f);
            return b;
        }

        /// <summary>部屋の空気。ほとんど何も無いが、完全な無音は嘘になる。</summary>
        static float[] Tone(float seconds, int seed)
        {
            int n = Mathf.RoundToInt(seconds * _fs);
            var b = NoiseLoop(n, 120f, 3200f, 1, seed);
            Normalize(b, 0.35f);
            return b;
        }

        /// <summary>
        /// カーペットの上の足音。
        ///
        /// 1 歩は 1 発。硬い床に硬い靴なら、かかとの接地とつま先の打ちつけが
        /// 別々に聞こえるが、カーペットではそうならない。蹴り出しは衝撃では
        /// ないうえ毛足が吸ってしまう。前は 150ms 後に 2 発目の立ち上がりを
        /// 置いていて、それが 2 歩目に聞こえていた。
        ///
        /// 音程のある成分は入れない。カーペットにコンクリート下地という
        /// 組み合わせに共振するものは何も無い。正弦波を混ぜると太鼓になる。
        ///
        /// 帯域を分けない。前は衝撃を 230Hz 以下、擦れを 1.5kHz 以上に
        /// 分けていて、実際の足音で一番エネルギーのある 230Hz〜1.5kHz が
        /// 空いていた。代わりに、遮断周波数が時間とともに下がるローパスに
        /// 雑音を通す。高い成分ほど速く消えるという当たり前のことを
        /// そのまま書くと、帯域は連続したまま音色が動く。
        /// </summary>
        static float[] Footstep(int seed, bool right, int variant)
        {
            int n = Mathf.RoundToInt(0.20f * _fs);
            var b = new float[n];
            var rng = new Rng(seed * 31 + (right ? 7 : 0) + variant * 101);

            // 靴 1 足ぶんの性格。左右と 1 歩ごとの違いはこれより小さくとる。
            var shoe = new Rng(seed);
            float fcTop = 3200f + shoe.Unit() * 1400f;   // 接地した瞬間の上端
            float fcEnd = 150f + shoe.Unit() * 90f;      // 落ち着く先
            float tFall = 0.009f + shoe.Unit() * 0.007f; // 上端が落ちる速さ
            float dec = 0.034f + shoe.Unit() * 0.014f;   // 全体の減衰
            float roll = 0.045f + shoe.Unit() * 0.030f;  // 足が転がる音の量

            // 左右差と 1 歩ごとの差。踏み込みの強さだけ。
            float lvl = (right ? 0.93f : 1.0f) * (0.94f + rng.Unit() * 0.12f);
            float atk = 0.005f + rng.Unit() * 0.003f;

            // ---- 毛足が潰れる音 ----
            var imp = new float[n];
            for (int i = 0; i < n; i++) imp[i] = rng.Bi();
            {
                float y = 0f;
                float kf = 1f / (tFall * _fs);
                for (int i = 0; i < n; i++)
                {
                    float fc = fcEnd + (fcTop - fcEnd) * Mathf.Exp(-i * kf);
                    float a = 1f - Mathf.Exp(-2f * Mathf.PI * fc / _fs);
                    y += a * (imp[i] - y);
                    imp[i] = y;
                }
            }
            float ta = atk * _fs, td = dec * _fs;
            for (int i = 0; i < n; i++)
                imp[i] *= (1f - Mathf.Exp(-i / ta)) * Mathf.Exp(-i / td);
            Normalize(imp, 1f);

            // ---- 足が転がるときの毛足の擦れ ----
            // 新しい立ち上がりは作らない。減衰の続きとして薄く伸びるだけ。
            var sc = new float[n];
            for (int i = 0; i < n; i++) sc[i] = rng.Bi();
            Lp(sc, 5200f); Hp(sc, 850f);
            Normalize(sc, 1f);
            int at = Mathf.RoundToInt(0.012f * _fs);
            float ts = 0.048f * _fs;
            for (int i = 0; i < n; i++)
                sc[i] *= i < at ? 0f : Mathf.Exp(-(i - at) / ts) * (1f - Mathf.Exp(-(i - at) / (0.010f * _fs)));

            for (int i = 0; i < n; i++) b[i] = (imp[i] + sc[i] * roll) * lvl;

            int e = Mathf.RoundToInt(0.003f * _fs);
            for (int i = 0; i < e; i++) b[n - 1 - i] *= i / (float)e;
            Normalize(b, 0.75f * lvl);
            return b;
        }

        /// <summary>切れかけの管の点灯管が弾く音。</summary>
        static float[] Click(int seed)
        {
            int n = Mathf.RoundToInt(0.045f * _fs);
            var b = new float[n];
            var rng = new Rng(seed);
            for (int i = 0; i < n; i++) b[i] = rng.Bi();
            Lp(b, 5200f); Hp(b, 700f);
            float t = 0.006f * _fs;
            for (int i = 0; i < n; i++) b[i] *= Mathf.Exp(-i / t);
            int e = Mathf.RoundToInt(0.002f * _fs);
            for (int i = 0; i < e; i++) b[n - 1 - i] *= i / (float)e;
            Normalize(b, 0.7f);
            return b;
        }

        // ループしない音は普通のフィルタでよい
        static void Lp(float[] b, float fc)
        {
            float a = 1f - Mathf.Exp(-2f * Mathf.PI * fc / _fs);
            float y = 0f;
            for (int i = 0; i < b.Length; i++) { y += a * (b[i] - y); b[i] = y; }
        }

        static void Hp(float[] b, float fc)
        {
            float a = 1f - Mathf.Exp(-2f * Mathf.PI * fc / _fs);
            float y = 0f;
            for (int i = 0; i < b.Length; i++) { y += a * (b[i] - y); b[i] -= y; }
        }
    }
}
