using System.Collections.Generic;
using UnityEngine;

namespace Backrooms.Generation
{
    /// <summary>
    /// 参照画像 (artifacts/reference/Backrooms_1.jpg) と「同じ構造」を組む複製シーン。
    ///
    /// 乱数で作った間取りを参照画像と比べても、比べているのは色と階調だけで、
    /// 構造が合っているかは分からない。ここでは参照画像そのものを測って出した
    /// 寸法で組み、同じカメラから撮る。そうして初めて画素どうしを重ねられる。
    ///
    /// 寸法の出どころは tools/reverse-geometry.py。要点だけ:
    ///   * 床との取り合い線を 6 本追跡し、「2 群が直交する」ことだけを条件に
    ///     焦点距離と水平線を解いた。交差角の残差は 0.06 度
    ///   * 天井を正射投影に戻すと、器具はタイル 1 枚ぶん (600 角) で、
    ///     3 タイル (1.80m) ごとに格子と平行に並んでいた。
    ///     ここが今までの実装 (600x1200 の 2x4) との最大の構造上の違い
    ///   * 器具ピッチ 1.80m は画面上で視点-天井間距離の 0.993 倍。
    ///     よって視点から天井まで 1.81m
    ///   * 視点高は壁面が 600mm モジュールに載る条件から 1.345m
    ///     (胸の高さで構えたカムコーダ)。天井高はその和で 3.16m
    /// </summary>
    public static class Level0Replica
    {
        public const float Cell = Level0Builder.Cell;      // 600mm
        public const float LightPitch = 1.80f;             // 器具の間隔 = 3 タイル
        public const float CeilH = 3.157f;                 // 実測 1.812 + 1.345
        public const float EyeH = 1.345f;                  // カムコーダの構え位置
        public const float FovV = 40.30f;                  // 2*atan(731/2/996)
        public const float CamYaw = 36.59f;                // 壁の方位 A
        public const float CamPitch = -2.27f;              // 水平線が画像中心より下 = 仰角

        // 参照画像を作ったレンズと記録系。tools/fit-medium2.py の実測値。
        // 演出ではなく、参照がそう写っている理由。詳しくは CaptureMedium。
        //
        // 歪みは入れない。間取りを取り合い線に対して 0.5px まで詰めた状態では、
        // k1 を入れると逆に幾何が壊れる。参照の長い直線のたわみ (0.07-0.47px) は
        // 線分当てはめの雑音 (0.2-0.4px) と同程度で、歪みがあるとは言い切れない。
        public const float LensK1 = 0f;
        // 生のレンダは参照より鮮鋭 (勾配 0.0119 対 0.0081)。2.2px でちょうど合う。
        public const float BlurSigma = 2.20f;
        // 生のレンダの明暗比は既に 2.04 で参照の 2.20 より低い。グレアはほぼ要らない。
        // 0.20 を掛けたときは 1.80 まで潰れた。
        public const float Glare = 0.04f;
        public const float GlareSigma = 177f;
        // 生の時点で R/G が 1.244 (参照 1.195)、B/R が 0.294 (参照 0.327) と
        // わずかに赤く寄っていたので、R だけ 0.92 に落として合わせる。
        public static readonly Color MediumGain = new Color(0.92f, 1.00f, 1.01f, 1f);

        // 参照画像で測ったカメラ位置。器具の位相 (1.8k+1.767, 1.8k+0.30) が
        // ワールドの位相 (1.8k+0.9, 1.8k+0.9) に合うように置いてある。
        public static readonly Vector2 Cam = new Vector2(4.533f, 2.400f);

        // 参照では中央の廊下が 56m 先まで抜けている。ホールを 42x38.4 に
        // していたときは、そこに壁が立ってしまい 10px ずれていた。
        public const float HallX = 48.0f, HallZ = 57.6f;   // 600mm の倍数

        /// <summary>床との取り合いから逆算し、600mm 格子に丸めた塊の平面。</summary>
        struct Blk
        {
            public float X0, X1, Z0, Z1;
            public string Note;
            public Blk(float x0, float z0, float x1, float z1, string note)
            { X0 = x0; Z0 = z0; X1 = x1; Z1 = z1; Note = note; }
        }

        // 面の位置も奥行きも、参照から検出した「壁と床の取り合い線」に対して
        // 直接当てはめた値 (tools/fit-plan.py)。カメラが分かっていれば
        // 「この間取りなら取り合いは画面のどの行に出るか」は解析的に出せるので、
        // Unity を起動せずに詰められる。全 1296 列で中央値 0.49 px。
        //
        // 奥行きが 0.7-1.1m と薄いのは測定の結果であって手抜きではない。
        // これより深くすると、その面が奥の面を隠してしまい参照と食い違う。
        // 参照の Backrooms は「厚い塊」ではなく「自立した厚い壁と柱」でできている。
        static readonly Blk[] Blocks =
        {
            new Blk( 0.00f, 13.25f,  6.43f, 19.00f, "左手前"),
            new Blk(12.21f,  7.15f, 19.80f,  8.25f, "右手前  厚 1.10m"),
            new Blk(14.83f, 12.55f, 15.50f, 13.20f, "右の柱  0.67 x 0.65m"),
            new Blk(19.70f, 14.03f, 25.00f, 14.75f, "右中    厚 0.72m"),
            new Blk(16.95f, 24.70f, 20.00f, 44.00f, "中央左  奥へ 19m"),
            new Blk(21.62f, 29.20f, 25.20f, 36.00f, "中央右"),
            new Blk(25.20f, 27.00f, 31.00f, 34.00f, "右奥"),
            new Blk( 8.50f, 27.00f, 11.00f, 34.00f, "左奥"),
        };

        public static Level0Generator.Result Build(MaterialSet mats)
        {
            var res = new Level0Generator.Result();
            var log = res.Log;
            log.Add("=== Level0Replica: 参照画像と同じ構造 ===");
            log.Add($"天井高 {CeilH:F3} m / 視点高 {EyeH:F3} m / 差 {CeilH - EyeH:F3} m");
            log.Add($"器具 {Cell * 1000f:F0} 角、{LightPitch:F2} m 格子。垂直画角 {FovV:F2} deg");

            int NX = Mathf.RoundToInt(HallX / Cell), NZ = Mathf.RoundToInt(HallZ / Cell);
            // 塊は 600mm 格子に載っていない (載せると遠方で画面上 60px ずれる)。
            // 天井を塞ぐのは「完全に塊の中に入るマス」だけにする。こうすると
            // 開いたマスのタイルが最大 600mm ぶん壁にめり込むが、壁のほうが
            // 高いので見えない。逆向きに丸めると天井裏へ抜ける穴が開く。
            var solid = new bool[NX, NZ];
            foreach (var b in Blocks)
                for (int i = Mathf.CeilToInt(b.X0 / Cell); i < Mathf.FloorToInt(b.X1 / Cell); i++)
                    for (int j = Mathf.CeilToInt(b.Z0 / Cell); j < Mathf.FloorToInt(b.Z1 / Cell); j++)
                        if (i >= 0 && i < NX && j >= 0 && j < NZ) solid[i, j] = true;
            System.Func<int, int, bool> Open =
                (i, j) => i >= 0 && i < NX && j >= 0 && j < NZ && !solid[i, j];
            // 器具は塊に少しでも掛かったら置かない
            System.Func<float, float, bool> FreeTile = (cx, cz) =>
            {
                foreach (var b in Blocks)
                    if (cx + Cell * 0.5f > b.X0 && cx - Cell * 0.5f < b.X1 &&
                        cz + Cell * 0.5f > b.Z0 && cz - Cell * 0.5f < b.Z1) return false;
                return true;
            };

            var root = new GameObject("Level0_Replica");
            res.Root = root;

            var floor = new MeshBuilder();
            floor.PlaneXZ(0f, HallX, 0f, HallZ, 0f, true, Level0Builder.UvCarpet);
            MeshKit.Spawn(root.transform, "Floor", floor.ToMesh("Floor"), mats.Carpet);

            // V を天井高そのものにする。既定の 2.12m のままだと 3.157m の壁の
            // 途中でテクスチャが折り返し、横一文字の継ぎ目が出る。
            // 1 周期を床から天井までに合わせれば、床際の汚れが床際に来る。
            var wall = new MeshBuilder { UvScaleV = CeilH };
            var bb = new MeshBuilder();
            EmitBlocks(wall, bb);
            EmitPerimeter(wall, bb);
            MeshKit.Spawn(root.transform, "Blocks", wall.ToMesh("Blocks"), mats.Wallpaper);
            MeshKit.Spawn(root.transform, "Baseboards", bb.ToMesh("Baseboards"), mats.Baseboard);

            var tiles = new MeshBuilder();
            var bars = new MeshBuilder();
            var fix = new MeshBuilder();
            var lens = new MeshBuilder();
            int lit = EmitCeiling(tiles, bars, fix, lens, Open, FreeTile, NX, NZ, res, log);

            MeshKit.Spawn(root.transform, "CeilingTiles", tiles.ToMesh("CeilingTiles"), mats.CeilingTile);
            MeshKit.Spawn(root.transform, "CeilingGrid", bars.ToMesh("CeilingGrid"), mats.Metal);
            MeshKit.Spawn(root.transform, "TrofferFrames", fix.ToMesh("TrofferFrames"), mats.Metal);
            MeshKit.Spawn(root.transform, "Diffusers", lens.ToMesh("Diffusers"), mats.DiffuserOn, false);

            res.Spawn = new Vector3(Cam.x, 0f, Cam.y);
            res.SpawnYaw = CamYaw;
            res.Shots.Add(new Level0Generator.ShotSpec("R1_match", res.Spawn, CamYaw, CamPitch));
            // 同じ間取りを別の位置から。構造が正しければどこから撮っても参照に似る
            res.Shots.Add(new Level0Generator.ShotSpec("R2_ceiling",
                new Vector3(9.0f, 0f, 6.0f), 40f, -22f));
            res.Shots.Add(new Level0Generator.ShotSpec("R3_corner",
                new Vector3(9.6f, 0f, 13.8f), 78f, 0f));
            res.Shots.Add(new Level0Generator.ShotSpec("R4_corridor",
                new Vector3(19.8f, 0f, 21.0f), 12f, 0f));
            res.Walk.Add(res.Spawn);
            res.Walk.Add(new Vector3(9.6f, 0f, 13.8f));
            res.Walk.Add(new Vector3(19.8f, 0f, 21.0f));

            res.Verts = tiles.VertexCount + bars.VertexCount + fix.VertexCount
                        + lens.VertexCount + wall.VertexCount + bb.VertexCount + floor.VertexCount;
            res.Tris = tiles.TriangleCount + bars.TriangleCount + fix.TriangleCount
                       + lens.TriangleCount + wall.TriangleCount + bb.TriangleCount + floor.TriangleCount;
            log.Add($"塊 {Blocks.Length} 個 / 器具 {lit} 台 / {res.Verts} 頂点 {res.Tris} 三角形");
            log.Add($"カメラ (x={Cam.x:F3}, z={Cam.y:F3}) 高さ {EyeH:F3} "
                    + $"yaw {CamYaw:F2} pitch {CamPitch:F2} fovV {FovV:F2}");
            return res;
        }

        // ------------------------------------------------------------- 塊

        static void EmitBlocks(MeshBuilder wall, MeshBuilder bb)
        {
            const float T = Level0Builder.BaseT, BH = Level0Builder.BaseH;
            foreach (var b in Blocks)
            {
                wall.BeveledBox(new Vector3(b.X0, 0f, b.Z0),
                                new Vector3(b.X1, CeilH, b.Z1),
                                Level0Builder.BevelWall, Level0Builder.UvWall);
                // 幅木は 4 面ぐるりと。参照でも右手前の塊の足元に明るい帯が出ている
                bb.Box(new Vector3(b.X0 - T, 0f, b.Z0 - T), new Vector3(b.X1 + T, BH, b.Z0), Level0Builder.UvMetal);
                bb.Box(new Vector3(b.X0 - T, 0f, b.Z1), new Vector3(b.X1 + T, BH, b.Z1 + T), Level0Builder.UvMetal);
                bb.Box(new Vector3(b.X0 - T, 0f, b.Z0), new Vector3(b.X0, BH, b.Z1), Level0Builder.UvMetal);
                bb.Box(new Vector3(b.X1, 0f, b.Z0), new Vector3(b.X1 + T, BH, b.Z1), Level0Builder.UvMetal);
            }
        }

        static void EmitPerimeter(MeshBuilder wall, MeshBuilder bb)
        {
            const float T = Level0Builder.BaseT, BH = Level0Builder.BaseH;
            // 外周は内側を向いた面だけでよい。箱で作ると天井グリッドと干渉する
            wall.PlaneXY(0f, HallX, 0f, CeilH, 0f, true, Level0Builder.UvWall);
            wall.PlaneXY(0f, HallX, 0f, CeilH, HallZ, false, Level0Builder.UvWall);
            wall.PlaneZY(0f, HallZ, 0f, CeilH, 0f, true, Level0Builder.UvWall);
            wall.PlaneZY(0f, HallZ, 0f, CeilH, HallX, false, Level0Builder.UvWall);
            bb.Box(new Vector3(0f, 0f, 0f), new Vector3(HallX, BH, T), Level0Builder.UvMetal);
            bb.Box(new Vector3(0f, 0f, HallZ - T), new Vector3(HallX, BH, HallZ), Level0Builder.UvMetal);
            bb.Box(new Vector3(0f, 0f, 0f), new Vector3(T, BH, HallZ), Level0Builder.UvMetal);
            bb.Box(new Vector3(HallX - T, 0f, 0f), new Vector3(HallX, BH, HallZ), Level0Builder.UvMetal);
        }

        // ----------------------------------------------------------- 天井

        static int EmitCeiling(MeshBuilder tiles, MeshBuilder bars, MeshBuilder fix, MeshBuilder lens,
                               System.Func<int, int, bool> Open, System.Func<float, float, bool> FreeTile,
                               int NX, int NZ, Level0Generator.Result res, List<string> log)
        {
            const float TW = Level0Builder.TBarW, RV = Level0Builder.TileReveal;
            float H = CeilH;

            // 器具の格子。ワールドで (1.8i+0.9, 1.8j+0.9) がタイルの中心になる
            var isFix = new bool[NX, NZ];
            int lit = 0;
            for (float fx = 0.9f; fx < HallX; fx += LightPitch)
                for (float fz = 0.9f; fz < HallZ; fz += LightPitch)
                {
                    int i = Mathf.FloorToInt(fx / Cell), j = Mathf.FloorToInt(fz / Cell);
                    if (!Open(i, j) || !FreeTile(fx, fz)) continue;
                    isFix[i, j] = true;
                    EmitTroffer(fix, lens, i, j, H);
                    res.TrofferCenters.Add(new Vector3((i + 0.5f) * Cell, H, (j + 0.5f) * Cell));
                    res.TrofferAlive.Add(true);
                    lit++;
                }

            int plain = 0;
            for (int i = 0; i < NX; i++)
                for (int j = 0; j < NZ; j++)
                {
                    if (!Open(i, j) || isFix[i, j]) continue;
                    tiles.PlaneXZ(i * Cell + TW * 0.5f, (i + 1) * Cell - TW * 0.5f,
                                  j * Cell + TW * 0.5f, (j + 1) * Cell - TW * 0.5f,
                                  H + RV, false, Cell * 8f);
                    plain++;
                }

            // T バーは連続区間にまとめる。1 マスずつ箱にすると頂点が 20 倍になる
            int runs = 0;
            for (int i = 0; i <= NX; i++)
            {
                int j = 0;
                while (j < NZ)
                {
                    if (!(Open(i - 1, j) && Open(i, j))) { j++; continue; }
                    int j0 = j;
                    while (j < NZ && Open(i - 1, j) && Open(i, j)) j++;
                    bars.Box(new Vector3(i * Cell - TW * 0.5f, H, j0 * Cell),
                             new Vector3(i * Cell + TW * 0.5f, H + Level0Builder.TBarH, j * Cell),
                             Level0Builder.UvMetal);
                    runs++;
                }
            }
            for (int j = 0; j <= NZ; j++)
            {
                int i = 0;
                while (i < NX)
                {
                    if (!(Open(i, j - 1) && Open(i, j))) { i++; continue; }
                    int i0 = i;
                    while (i < NX && Open(i, j - 1) && Open(i, j)) i++;
                    bars.Box(new Vector3(i0 * Cell, H, j * Cell - TW * 0.5f),
                             new Vector3(i * Cell, H + Level0Builder.TBarH, j * Cell + TW * 0.5f),
                             Level0Builder.UvMetal);
                    runs++;
                }
            }

            // 壁見切りアングルは格子ではなく実際の壁面の位置に置く。
            // 塊が 600mm 格子から外れている以上、格子から出すと最大 300mm ずれる。
            // 木口を壁面ちょうどに置くと壁の反りぶんの黒い筋が抜けるので 4mm 食い込ませる。
            int ang = 0;
            const float AW = Level0Builder.AngleW, AH = Level0Builder.AngleH, Bite = 0.004f;
            System.Action<float, float, float, float> Ring = (x0, z0, x1, z1) =>
            {
                bars.Box(new Vector3(x0 - AW, H, z0 - AW), new Vector3(x1 + AW, H + AH, z0 + Bite), Level0Builder.UvMetal);
                bars.Box(new Vector3(x0 - AW, H, z1 - Bite), new Vector3(x1 + AW, H + AH, z1 + AW), Level0Builder.UvMetal);
                bars.Box(new Vector3(x0 - AW, H, z0 - Bite), new Vector3(x0 + Bite, H + AH, z1 + Bite), Level0Builder.UvMetal);
                bars.Box(new Vector3(x1 - Bite, H, z0 - Bite), new Vector3(x1 + AW, H + AH, z1 + Bite), Level0Builder.UvMetal);
                ang += 4;
            };
            foreach (var b in Blocks) Ring(b.X0, b.Z0, b.X1, b.Z1);
            bars.Box(new Vector3(0f, H, 0f), new Vector3(HallX, H + AH, AW), Level0Builder.UvMetal);
            bars.Box(new Vector3(0f, H, HallZ - AW), new Vector3(HallX, H + AH, HallZ), Level0Builder.UvMetal);
            bars.Box(new Vector3(0f, H, 0f), new Vector3(AW, H + AH, HallZ), Level0Builder.UvMetal);
            bars.Box(new Vector3(HallX - AW, H, 0f), new Vector3(HallX, H + AH, HallZ), Level0Builder.UvMetal);
            ang += 4;

            log.Add($"天井: タイル {plain + lit} 枚 (うち器具 {lit})、T バー {runs} 区間、"
                    + $"壁見切り {ang} 区間");
            return lit;
        }

        /// <summary>
        /// 埋め込み 2x2 (600 角)。枠がグリッド面まで落ち、レンズはその中に
        /// さらに 12mm 引っ込む。参照でもレンズはタイル面と同じ高さには見えない。
        /// </summary>
        static void EmitTroffer(MeshBuilder fix, MeshBuilder lens, int ci, int cj, float H)
        {
            const float FrameW = 0.022f, FrameT = 0.006f, LensDrop = 0.012f;
            const float TW = Level0Builder.TBarW;
            float x0 = ci * Cell + TW * 0.5f, x1 = (ci + 1) * Cell - TW * 0.5f;
            float z0 = cj * Cell + TW * 0.5f, z1 = (cj + 1) * Cell - TW * 0.5f;
            float yb = H - 0.001f, yt = H + FrameT;

            fix.Box(new Vector3(x0, yb, z0), new Vector3(x1, yt, z0 + FrameW), Level0Builder.UvMetal);
            fix.Box(new Vector3(x0, yb, z1 - FrameW), new Vector3(x1, yt, z1), Level0Builder.UvMetal);
            fix.Box(new Vector3(x0, yb, z0 + FrameW), new Vector3(x0 + FrameW, yt, z1 - FrameW), Level0Builder.UvMetal);
            fix.Box(new Vector3(x1 - FrameW, yb, z0 + FrameW), new Vector3(x1, yt, z1 - FrameW), Level0Builder.UvMetal);
            lens.PlaneXZ(x0 + FrameW, x1 - FrameW, z0 + FrameW, z1 - FrameW, H + LensDrop, false, 1f);
        }
    }
}
