using System.Collections.Generic;
using UnityEngine;

namespace Backrooms.Generation
{
    /// <summary>
    /// 無限に続く Level 0 の間取りを、座標から直接決める規則。
    ///
    /// 配列も乱数の状態も持たない。ある場所に壁があるかどうかは
    /// 「その場所の整数座標を混ぜた値」だけで決まるので、どの順番で
    /// どのチャンクを作っても、何度作り直しても必ず同じ形になる。
    /// 無限に歩ける世界で継ぎ目を出さないための最低条件がこれ。
    ///
    /// 寸法は参照画像 (artifacts/reference/Backrooms_1.jpg) を写真測量して
    /// 出したもの。tools/reverse-geometry.py と Level0Replica を参照。
    ///   天井 3.157m / 器具は 600 角を 1.80m 格子 / タイル 600mm
    /// </summary>
    public static class Level0Layout
    {
        public const float Cell = 0.60f;          // 天井タイル 600mm
        public const float Bay = 1.80f;           // 器具の間隔 = 3 タイル
        public const int CellsPerBay = 3;
        public const float CeilH = 3.157f;
        public const float EyeH = 1.345f;

        /// <summary>塊の厚み。参照では 0.65〜3.9m の幅があった。</summary>
        public const float ThinT = 1.20f;         // 2 タイル
        // 3.6m 格子に厚み 2.4m を向かい合わせると隙間が 1.2m しか残らず、
        // 角で人が引っ掛かる。1.8m にすると最狭部が 1.8m になる。
        public const float ThickT = 1.80f;        // 3 タイル

        /// <summary>
        /// 壁が立つ格子。器具のベイ (1.8m) の 2 つぶん。
        ///
        /// 1.8m 格子に厚み 1.2m の壁を立てると、隣り合う壁の間が 0.6m しか
        /// 残らず人が通れない。参照の Level 0 の部屋は 5〜15m あるので、
        /// 壁を 2 ベイ間隔にするのは通行の都合であると同時に形としても正しい。
        /// </summary>
        public const float WallPitch = Bay * 2f;  // 3.6m

        // ------------------------------------------------------------ ハッシュ

        /// <summary>座標を混ぜて 32bit を返す。負の座標でも偏らないこと。</summary>
        public static uint Hash(int x, int y, int salt)
        {
            unchecked
            {
                uint h = (uint)(x * 0x27D4EB2D) ^ (uint)(y * 0x165667B1) ^ (uint)(salt * 0x9E3779B1);
                h ^= h >> 15; h *= 0x2C1B3C6D;
                h ^= h >> 12; h *= 0x297A2D39;
                h ^= h >> 15;
                return h;
            }
        }

        static float Unit(int x, int y, int salt) => (Hash(x, y, salt) & 0xFFFFFF) / 16777215f;

        // -------------------------------------------------------------- 壁

        /// <summary>
        /// 壁は「格子線の上に、何区画か続く帯」として立てる。
        ///
        /// 1 区画ずつ独立に決めると切れ端が散らばった迷路になってオフィスに
        /// 見えない。4 区画 (14.4m) をひとまとまりにして帯ごとに決め、
        /// その中で端を欠けさせる。欠けた所がそのまま出入口になる。
        /// </summary>
        const int RunUnits = 4;

        /// <summary>一帯の性格。14.4m 角ごとに決まる。</summary>
        public enum Space { Hall, Normal, Warren }

        /// <summary>壁の格子 4 区画ぶん = 14.4m 角。部屋 1 つぶんの大きさ。</summary>
        public const int SpaceZone = 4;

        /// <summary>
        /// その一帯が「大部屋」か「小割り」か。
        ///
        /// 壁の出る確率を全域で同じにすると、部屋の寸法がどこでも同じになる。
        /// 歩いても景色が変わらないのは形が変わらないからで、明かりの欠けを
        /// いくら振っても直らない。実際のフロアは、間仕切りを抜いた大部屋と
        /// 小割りにした一帯が混ざっている。
        ///
        /// ただし珍しいから効く。8 割は標準のままにして、変わった一帯が
        /// ときどき現れる形にする。
        /// </summary>
        public static Space SpaceAt(int i, int j, int seed)
        {
            float u = Unit(FloorDiv(i, SpaceZone), FloorDiv(j, SpaceZone), seed * 7 + 51);
            if (u < 0.11f) return Space.Hall;
            if (u > 0.92f) return Space.Warren;
            return Space.Normal;
        }

        /// <summary>その一帯で壁帯が出る確率。</summary>
        static float BandChance(int i, int j, int seed)
        {
            switch (SpaceAt(i, j, seed))
            {
                // 大部屋。壁がほとんど無く、代わりに柱だけが残る
                case Space.Hall: return 0.13f;
                // 小割り。ここだけ通路が細かく折れる。0.46 より上げると
                // 四方を塞がれた区画ができて通れなくなる
                case Space.Warren: return 0.46f;
                default: return 0.34f;
            }
        }

        /// <summary>x = i*WallPitch の線上、z ∈ [j, j+1) 区画に壁があるか。</summary>
        public static bool WallX(int i, int j, int seed)
        {
            if (Unit(i, FloorDiv(j, RunUnits), seed * 7 + 11) > BandChance(i, j, seed)) return false;
            return Unit(i, j, seed * 7 + 12) < 0.86f;
        }

        /// <summary>z = j*WallPitch の線上、x ∈ [i, i+1) 区画に壁があるか。</summary>
        public static bool WallZ(int i, int j, int seed)
        {
            if (Unit(FloorDiv(i, RunUnits), j, seed * 7 + 21) > BandChance(i, j, seed)) return false;
            return Unit(i, j, seed * 7 + 22) < 0.86f;
        }

        /// <summary>その壁帯の厚み。帯ごとに揃える。</summary>
        public static float WallThickX(int i, int j, int seed)
            => Unit(i, FloorDiv(j, RunUnits), seed * 7 + 13) < 0.28f ? ThickT : ThinT;

        public static float WallThickZ(int i, int j, int seed)
            => Unit(FloorDiv(i, RunUnits), j, seed * 7 + 23) < 0.28f ? ThickT : ThinT;

        /// <summary>
        /// 壁帯の中心線。厚い壁だけ 0.3m 寄せる。
        ///
        /// 3.6m の格子線に厚み 1.8m を左右対称に載せると、壁の面が
        /// 0.6m のタイル格子のちょうど真ん中を通る。天井はタイル 1 枚
        /// 単位でしか置けないので、面がマスを二分すると、そのマスを
        /// 置くか置かないかで 0.3m の食い違いが必ず出る。しかも中心が
        /// 面にちょうど重なるので、Solid の勝敗が float の丸めで決まる。
        /// 実測すると 1.8m 壁の面 34 のうち 16 が「塊」と判定されていて、
        /// そこだけ天井が 1 枚抜け、壁沿いに 0.3m の穴が回っていた。
        /// 穴の向こうには何も無いので真っ黒に落ち、壁と天井の境に
        /// 濃い線が引かれたように見える。
        ///
        /// 厚い壁を 0.3m 振れば面は必ずタイルの境界に乗り、二分が起きない。
        /// 実在の間仕切りも柱心から振れて立つので、形としても正しい。
        /// </summary>
        static float Shift(float thick, uint h)
            => thick > ThinT + 0.01f ? ((h & 1) == 0 ? Cell * 0.5f : -Cell * 0.5f) : 0f;

        /// <summary>x 固定の壁帯の中心 (world x)。</summary>
        public static float WallCenterX(int i, int j, int seed)
            => i * WallPitch + Shift(WallThickX(i, j, seed),
                                     Hash(i, FloorDiv(j, RunUnits), seed * 7 + 14));

        /// <summary>z 固定の壁帯の中心 (world z)。</summary>
        public static float WallCenterZ(int i, int j, int seed)
            => j * WallPitch + Shift(WallThickZ(i, j, seed),
                                     Hash(FloorDiv(i, RunUnits), j, seed * 7 + 24));

        /// <summary>
        /// 独立柱。壁が 1 枚も触れていない格子点にだけ立つ。
        ///
        /// 大部屋では確率を上げる。間仕切りを抜いたフロアで残るのは構造柱
        /// だけなので、広い所ほど柱が目立つのが本来の姿になる。柱が 3.6m
        /// 格子で並ぶ広間は、壁で仕切った部屋とは全く別の空間に見える。
        /// </summary>
        public static bool Column(int i, int j, int seed)
        {
            float p = SpaceAt(i, j, seed) == Space.Hall ? 0.34f : 0.075f;
            if (Unit(i, j, seed * 7 + 31) > p) return false;
            return !WallX(i, j, seed) && !WallX(i, j - 1, seed)
                && !WallZ(i, j, seed) && !WallZ(i - 1, j, seed);
        }

        public const float ColHalf = 0.60f;       // 1.2m 角の柱

        // -------------------------------------------------- ある点が塊の中か

        /// <summary>
        /// world 座標 (x, z) が壁や柱の中にあるか。
        /// 天井のタイルを塞ぐかどうか、プレイヤーが通れるかの判定に使う。
        /// </summary>
        public static bool Solid(float x, float z, int seed)
        {
            int i0 = Mathf.FloorToInt(x / WallPitch), j0 = Mathf.FloorToInt(z / WallPitch);
            for (int i = i0 - 1; i <= i0 + 1; i++)
                for (int j = j0 - 1; j <= j0 + 1; j++)
                {
                    if (WallX(i, j, seed))
                    {
                        float t = WallThickX(i, j, seed) * 0.5f;
                        if (Mathf.Abs(x - WallCenterX(i, j, seed)) < t
                            && z > j * WallPitch && z < (j + 1) * WallPitch) return true;
                    }
                    if (WallZ(i, j, seed))
                    {
                        float t = WallThickZ(i, j, seed) * 0.5f;
                        if (Mathf.Abs(z - WallCenterZ(i, j, seed)) < t
                            && x > i * WallPitch && x < (i + 1) * WallPitch) return true;
                    }
                    if (Column(i, j, seed)
                        && Mathf.Abs(x - i * WallPitch) < ColHalf
                        && Mathf.Abs(z - j * WallPitch) < ColHalf) return true;
                }
            return false;
        }

        // ------------------------------------------------------------ 照明

        public enum Lamp { Lit, Dead, Flicker }

        /// <summary>
        /// 器具の状態。参照の Level 0 は「全部点いている」のではない。
        ///
        /// 2 段構えにしてある。まず 6 ベイ角のまとまりごとに「この一帯は
        /// 電気が来ていない」かどうかを決め、そのうえで 1 台ごとに切れを決める。
        /// 1 台ごとの確率だけだと切れがまんべんなく散って、
        /// 「区画ごと落ちている」という Backrooms らしさが出ない。
        /// </summary>
        public static Lamp LampAt(int i, int j, int seed)
        {
            const int Zone = 6;
            float z = Unit(FloorDiv(i, Zone), FloorDiv(j, Zone), seed * 7 + 41);
            if (z < 0.14f) return Lamp.Dead;                       // 一帯が落ちている
            if (z < 0.20f)                                         // 一帯が瀕死
                return Unit(i, j, seed * 7 + 42) < 0.55f ? Lamp.Dead : Lamp.Flicker;

            float u = Unit(i, j, seed * 7 + 43);
            if (u < 0.08f) return Lamp.Dead;                       // 1 台ずつの切れ
            if (u < 0.145f) return Lamp.Flicker;
            return Lamp.Lit;
        }

        /// <summary>
        /// 器具そのものを置くか。「消えている」のとは意味が違う。
        ///
        /// 消えた蛍光灯が等間隔に並んでいる天井と、器具が最初から
        /// 1 台も無くて格子と吸音タイルだけが続く天井は、別の見え方をする。
        /// 明暗のムラだけでは前者にしかならないので、ここでも 2 段構えにして
        /// 「この一帯には照明が入っていない」区画を作る。
        /// </summary>
        public static bool HasFixture(int i, int j, int seed)
        {
            const int Zone = 5;                                    // 9m 角
            if (Unit(FloorDiv(i, Zone), FloorDiv(j, Zone), seed * 7 + 45) < 0.24f)
                return Unit(i, j, seed * 7 + 46) < 0.15f;          // ぽつんと残った 1 台だけ
            return Unit(i, j, seed * 7 + 44) < 0.88f;
        }

        // -------------------------------------------------------- 天井の変化

        public enum Ceil { Tile, Missing, Hatch, Vent }

        /// <summary>
        /// 天井 1 マス (600 角) に何があるか。器具の無いマスにだけ効く。
        ///
        /// 天井は画面の 4 割を占めるのに、同じタイルと同じ器具しか無かった。
        /// ただしどれも「たまに 1 つある」物で、並べると天井の設備工事に
        /// 見えてしまう。14.4m 角のチャンク 1 つあたり、抜けたタイル 0.9 枚 /
        /// 点検口 1.2 か所 / 吹き出し口 3.5 か所。
        /// </summary>
        public static Ceil CeilAt(int ci, int cj, int seed)
        {
            float u = Unit(ci, cj, seed * 7 + 61);
            if (u < 0.0015f) return Ceil.Missing;
            if (u < 0.0035f) return Ceil.Hatch;
            if (u < 0.0095f) return Ceil.Vent;
            return Ceil.Tile;
        }

        // ------------------------------------------------------------ 補助

        public static int FloorDiv(int a, int b) => a >= 0 ? a / b : -(((-a) + b - 1) / b);

        /// <summary>器具の中心 (world)。ベイ格子の中心に 1 台。</summary>
        public static Vector3 FixtureCenter(int i, int j)
            => new Vector3(i * Bay + Bay * 0.5f, CeilH, j * Bay + Bay * 0.5f);

        /// <summary>そのベイの中心が空いているか (器具を置けるか)。</summary>
        public static bool FixtureFree(int i, int j, int seed)
        {
            float cx = i * Bay + Bay * 0.5f, cz = j * Bay + Bay * 0.5f;
            return !Solid(cx - Cell * 0.5f, cz - Cell * 0.5f, seed)
                && !Solid(cx + Cell * 0.5f, cz + Cell * 0.5f, seed)
                && !Solid(cx - Cell * 0.5f, cz + Cell * 0.5f, seed)
                && !Solid(cx + Cell * 0.5f, cz - Cell * 0.5f, seed);
        }

        /// <summary>
        /// 半径 r の円がまるごと入る場所を、指定点の近くから探す。
        /// 点だけ空いていても、人が立てるとは限らない。
        /// </summary>
        public static Vector3 FindOpen(Vector3 near, int seed, float r = 0.75f)
        {
            for (int ring = 0; ring < 60; ring++)
                for (int di = -ring; di <= ring; di++)
                    for (int dj = -ring; dj <= ring; dj++)
                    {
                        if (Mathf.Max(Mathf.Abs(di), Mathf.Abs(dj)) != ring) continue;
                        float x = near.x + di * Bay, z = near.z + dj * Bay;
                        if (Clear(x, z, seed, r)) return new Vector3(x, 0f, z);
                    }
            return near;
        }

        /// <summary>
        /// 半径 r の円がまるごと空いているか。外周だけを 12 方向で見ると、
        /// 見本の間に入った角を見落として「通れるはずの経路」を作ってしまう。
        /// 16 方向と、内側の輪も見る。
        /// </summary>
        public static bool Clear(float x, float z, int seed, float r)
        {
            if (Solid(x, z, seed)) return false;
            for (int a = 0; a < 16; a++)
            {
                float t = a * Mathf.PI / 8f;
                float c = Mathf.Cos(t), s2 = Mathf.Sin(t);
                if (Solid(x + c * r, z + s2 * r, seed)) return false;
                if (Solid(x + c * r * 0.6f, z + s2 * r * 0.6f, seed)) return false;
            }
            return true;
        }

        /// <summary>
        /// その場所から最も遠くまで見通せる向き (Unity の yaw)。
        /// 無限に生成する以上、撮る向きも自分で決めないと壁を撮ることになる。
        /// </summary>
        public static float BestYaw(Vector3 p, int seed)
        {
            float best = 0f, bestD = -1f;
            for (int a = 0; a < 72; a++)
            {
                float yaw = a * 5f;
                float rad = yaw * Mathf.Deg2Rad;
                float dx = Mathf.Sin(rad), dz = Mathf.Cos(rad);
                float d = 0f;
                while (d < 45f && !Solid(p.x + dx * (d + 0.5f), p.z + dz * (d + 0.5f), seed)) d += 0.5f;
                if (d > bestD) { bestD = d; best = yaw; }
            }
            return best;
        }

        /// <summary>
        /// from から to まで実際に歩ける経路。0.6m 刻みの幅優先探索。
        ///
        /// 直線で結んだ経由点を歩かせても、間に壁があれば必ず止まる。
        /// 歩行テストを「通れることの証明」にするには経路のほうを解く。
        /// </summary>
        public static List<Vector3> Path(Vector3 from, Vector3 to, int seed,
                                         float radius = 0.45f, int maxNodes = 40000)
        {
            const float G = 0.60f;
            Vector2Int K(Vector3 p) => new Vector2Int(Mathf.RoundToInt(p.x / G), Mathf.RoundToInt(p.z / G));
            Vector3 W(Vector2Int k) => new Vector3(k.x * G, 0f, k.y * G);

            var start = K(from); var goal = K(to);
            var prev = new Dictionary<Vector2Int, Vector2Int>();
            var seen = new HashSet<Vector2Int> { start };
            var q = new Queue<Vector2Int>();
            q.Enqueue(start);
            var dirs = new[] { new Vector2Int(1, 0), new Vector2Int(-1, 0),
                               new Vector2Int(0, 1), new Vector2Int(0, -1) };
            bool found = false;
            int n = 0;
            while (q.Count > 0 && n++ < maxNodes)
            {
                var c = q.Dequeue();
                if (c == goal) { found = true; break; }
                foreach (var d in dirs)
                {
                    var k = c + d;
                    if (seen.Contains(k)) continue;
                    var w = W(k);
                    if (!Clear(w.x, w.z, seed, radius)) continue;
                    seen.Add(k); prev[k] = c; q.Enqueue(k);
                }
            }
            var outp = new List<Vector3>();
            if (!found) return outp;
            var cur = goal;
            while (cur != start) { outp.Add(W(cur)); cur = prev[cur]; }
            outp.Add(W(start));
            outp.Reverse();
            // 直線で結べる区間はまとめる。1 歩ずつ経由点にすると数百個になる。
            var simple = new List<Vector3> { outp[0] };
            for (int i = 1; i < outp.Count - 1; i++)
            {
                var a = simple[simple.Count - 1];
                // 簡約は本番より広い半径で判定する。ぎりぎりで繋ぐと
                // 実際に歩いたときに角へ引っ掛かる。
                if (!Walkable(a, outp[i + 1], seed, radius + 0.15f)) simple.Add(outp[i]);
            }
            simple.Add(outp[outp.Count - 1]);
            return simple;
        }

        static bool Walkable(Vector3 a, Vector3 b, int seed, float r)
        {
            float len = Vector3.Distance(a, b);
            int n = Mathf.CeilToInt(len / 0.3f);
            for (int i = 0; i <= n; i++)
            {
                var p = Vector3.Lerp(a, b, i / (float)n);
                if (!Clear(p.x, p.z, seed, r)) return false;
            }
            return true;
        }
    }
}
