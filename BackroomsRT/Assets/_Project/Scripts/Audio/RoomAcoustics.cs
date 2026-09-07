using UnityEngine;
using Backrooms.Generation;

namespace Backrooms.Audio
{
    /// <summary>
    /// いま立っている場所の音響を、間取りから直接計算する。
    ///
    /// Level0Layout はどこに壁があるかを式で答えられるので、
    /// 物理的なレイキャストを撃たずに壁までの距離が分かる。
    /// そこから Sabine の式で残響時間を出す。
    ///
    ///     RT60 = 0.161 V / A        A = Σ S_i α_i
    ///
    /// 吸音率は実測の公表値。500Hz〜1kHz で
    ///     カーペット (薄毛足・コンクリート下地) 0.25
    ///     鉱物繊維の吸音天井タイル             0.68
    ///     壁紙貼りの石膏ボード                 0.05
    /// 4kHz では 0.55 / 0.85 / 0.07 に上がる。高域のほうがよく吸われる。
    ///
    /// この計算をすると、Backrooms の残響が部屋の広さでほとんど変わらない
    /// ことが分かる。天井高が一定なら V/S が一定なので、
    ///     RT60 ≒ 0.161 h / (α_床 + α_天井)
    /// になり、5m 角でも 50m 角でも 0.5 秒前後にしかならない。
    /// 空間の違いは残響の「長さ」ではなく、初期反射の時刻と残響の「量」に出る。
    /// </summary>
    public static class RoomAcoustics
    {
        // 500Hz〜1kHz
        public const float AlphaCarpet = 0.25f;
        public const float AlphaCeiling = 0.68f;
        public const float AlphaWall = 0.05f;
        // 4kHz
        public const float AlphaCarpetHf = 0.55f;
        public const float AlphaCeilingHf = 0.85f;
        public const float AlphaWallHf = 0.07f;

        public const float Sound = 343f;          // m/s
        public const int Rays = 24;
        public const float MaxDist = 45f;

        public struct Estimate
        {
            public float Rt60;          // 秒
            public float Rt60Hf;        // 4kHz
            public float HfRatio;
            public float Volume;        // m3
            public float FloorArea;     // m2
            public float WallArea;      // m2
            public float Absorption;    // m2 sabin
            public float MeanAlpha;
            public float RoomConstant;  // R = A/(1-a)
            public float Wet;           // 残響の混ぜ量 0..1
            public float NearWall;      // 一番近い壁まで (m)
            public float FarWall;
            public float MeanWall;
            public bool Enclosed;       // 全方向が 45m 以内で閉じているか
            /// <summary>方向ごとの壁までの距離。初期反射はここから組む。</summary>
            public float[] Dist;
        }



        /// <summary>その点から各方向の壁までの距離を測る。</summary>
        public static void March(Vector3 p, int seed, float[] outDist)
        {
            int n = outDist.Length;
            for (int a = 0; a < n; a++)
            {
                float t = a * 2f * Mathf.PI / n;
                float dx = Mathf.Cos(t), dz = Mathf.Sin(t);
                // 0.3m 刻み。壁の最小の厚みが 1.2m なので抜けることはない。
                float d = 0.3f;
                while (d < MaxDist && !Level0Layout.Solid(p.x + dx * d, p.z + dz * d, seed)) d += 0.3f;
                outDist[a] = d;
            }
        }

        public static Estimate Probe(Vector3 p, int seed)
        {
            // 測定ごとに自前の配列を持つ。共用の静的配列にすると、
            // 別の点を測った瞬間に前の結果が壊れる。実際それで
            // 3 か所とも同じ初期反射になっていた。
            var dist = new float[Rays];
            March(p, seed, dist);

            float dth = 2f * Mathf.PI / Rays;
            float floor = 0f, perim = 0f, near = MaxDist, far = 0f, mean = 0f;
            int open = 0;
            for (int i = 0; i < Rays; i++)
            {
                float d = dist[i];
                floor += 0.5f * d * d * dth;     // 扇形の面積
                perim += d * dth;                // 半径 d での弧長 = 壁の長さ
                if (d < near) near = d;
                if (d > far) far = d;
                mean += d;
                if (d >= MaxDist - 0.31f) open++;
            }
            mean /= Rays;

            var e = new Estimate();
            e.Dist = dist;
            e.FloorArea = floor;
            e.WallArea = perim * Level0Layout.CeilH;
            e.Volume = floor * Level0Layout.CeilH;
            e.NearWall = near; e.FarWall = far; e.MeanWall = mean;
            e.Enclosed = open == 0;

            e.Absorption = floor * (AlphaCarpet + AlphaCeiling) + e.WallArea * AlphaWall;
            float aHf = floor * (AlphaCarpetHf + AlphaCeilingHf) + e.WallArea * AlphaWallHf;
            float sTot = 2f * floor + e.WallArea;
            e.MeanAlpha = sTot > 0f ? e.Absorption / sTot : 0.3f;

            e.Rt60 = e.Absorption > 0f ? 0.161f * e.Volume / e.Absorption : 0.5f;
            e.Rt60Hf = aHf > 0f ? 0.161f * e.Volume / aHf : 0.3f;
            e.HfRatio = Mathf.Clamp(e.Rt60 > 0f ? e.Rt60Hf / e.Rt60 : 0.6f, 0.2f, 1f);
            e.Rt60 = Mathf.Clamp(e.Rt60, 0.20f, 1.60f);

            // 室定数 R = A/(1-a)。残響音圧は 1/sqrt(R) に比例する。
            // 廊下では R が小さく残響がよく乗り、広間では大きく乗らない。
            e.RoomConstant = Mathf.Max(1f, e.Absorption / Mathf.Max(0.05f, 1f - e.MeanAlpha));
            // 15m 角の部屋 (R≒306) でおよそ 0.33 になるように合わせる
            e.Wet = Mathf.Clamp(5.8f / Mathf.Sqrt(e.RoomConstant), 0.12f, 0.60f);
            return e;
        }

        /// <summary>
        /// 初期反射のタップを組む。床・天井・壁。
        ///
        /// 音源が聞き手のすぐ近くにあるとして往復距離を取る。足音も呼吸も
        /// 実際にそうだし、遠くの器具の音には音源側の距離減衰が別に掛かる。
        /// 圧力の反射率は sqrt(1-α)。α はエネルギーの吸音率なので。
        /// </summary>
        public static int BuildTaps(in Estimate e, Vector3 fwd, float[] sec, float[] gain, float[] pan,
                                    float eyeH, float scale = 0.6f)
        {
            int n = 0;
            float rFloor = Mathf.Sqrt(1f - AlphaCarpet);
            float rCeil = Mathf.Sqrt(1f - AlphaCeiling);
            float rWall = Mathf.Sqrt(1f - AlphaWall);

            float dFloor = 2f * eyeH;
            sec[n] = dFloor / Sound; gain[n] = scale * rFloor / dFloor; pan[n] = 0f; n++;

            float dCeil = 2f * (Level0Layout.CeilH - eyeH);
            sec[n] = dCeil / Sound; gain[n] = scale * rCeil / dCeil; pan[n] = 0f; n++;

            // 壁は方向ごとに 1 本ずつ。同じ壁に当たった光線を何本も
            // タップにしても、同じ時刻に重なるだけで部屋の形は伝わらない。
            // 全周を 8 区画に割って、各区画で最も近い壁だけを取る。
            var right = new Vector3(fwd.z, 0f, -fwd.x).normalized;
            int sectors = Mathf.Min(8, Early.MaxTaps - n);
            int per = Rays / sectors;
            for (int sIdx = 0; sIdx < sectors; sIdx++)
            {
                int best = -1; float bd = float.MaxValue;
                for (int k = 0; k < per; k++)
                {
                    int i = sIdx * per + k;
                    if (e.Dist[i] >= MaxDist - 0.31f) continue;   // 開いている方向
                    if (e.Dist[i] < bd) { bd = e.Dist[i]; best = i; }
                }
                if (best < 0) continue;
                float t = best * 2f * Mathf.PI / Rays;
                var dir = new Vector3(Mathf.Cos(t), 0f, Mathf.Sin(t));
                float d = 2f * bd;
                sec[n] = d / Sound;
                gain[n] = scale * rWall / d;
                pan[n] = Mathf.Clamp(Vector3.Dot(dir, right), -1f, 1f) * 0.8f;
                n++;
            }
            return n;
        }

    }
}
