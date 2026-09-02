using System.Collections.Generic;
using UnityEngine;

namespace Backrooms.Generation
{
    /// <summary>M1 サンドボックスで使うマテリアル一式。</summary>
    [System.Serializable]
    public class MaterialSet
    {
        public Material Wallpaper;
        public Material Carpet;
        public Material CeilingTile;
        public Material Metal;        // T バー・トロファー枠
        public Material Baseboard;
        public Material DiffuserOn;
        public Material DiffuserOff;
        public Material Chrome;    // スプリンクラー
        public Material Plastic;   // 感知器・コンセント
    }

    /// <summary>
    /// Level 0 の実寸ジオメトリを生成する。
    /// 寸法は実在のオフィス建築に合わせてある。ここを外すと何をしても CG に見える。
    /// </summary>
    public static class Level0Builder
    {
        // ---- 実寸定数 ------------------------------------------------------
        public const float Cell = 0.60f;      // 天井グリッド 600mm
        public const float TBarW = 0.024f;    // T バー見付 24mm
        public const float TBarH = 0.032f;    // T バー成 32mm
        public const float TileDrop = 0.014f; // タイル面は T バー下端より 14mm 上
        public const float WallT = 0.15f;     // 壁厚 150mm（開口の見込みが出る）
        public const float BaseH = 0.10f;     // 幅木 100mm
        public const float BaseT = 0.018f;    // 幅木の出 18mm
        public const float WallOverrun = 0.20f; // 壁は天井面より上まで伸ばす（外周の隙間封じ）

        // ---- 面取り量（実在の建築に数学的な直角は存在しない）------------------
        const float BevelWall = 0.004f;   // 塗装が盛られた石膏ボードのコーナービード
        const float BevelBase = 0.002f;   // ビニル幅木の角
        const float BevelBar  = 0.0012f;  // ロール成形された T バー
        const float BevelFrame= 0.0015f;  // 器具の枠

        // ---- UV スケール（テクスチャ 1 周期のワールド寸法）-------------------
        const float UvCarpet = 2.00f;
        const float UvWall = 2.12f;   // 壁紙ロール幅 0.53m x 4
        const float UvMetal = 1.00f;

        public struct Room
        {
            public float X0, X1, Z0, Z1, H;
            public Room(float x0, float x1, float z0, float z1, float h)
            { X0 = x0; X1 = x1; Z0 = z0; Z1 = z1; H = h; }
        }

        public class Result
        {
            public GameObject Root;
            public List<Vector3> TrofferCenters = new List<Vector3>();
            public List<bool> TrofferAlive = new List<bool>();
            public List<float> TrofferHeight = new List<float>();
            public int Verts, Tris;
        }

        /// <summary>M1 用のレイアウト: 天井 2.70m の主室 + 開口 + 天井 2.40m の副室。</summary>
        public static Result BuildSandbox(MaterialSet mats, int seed = 1996)
        {
            var rnd = new System.Random(seed);
            var res = new Result();
            var root = new GameObject("Level0_Sandbox_Geometry");
            res.Root = root;

            var roomA = new Room(0f, 12f, 0f, 9f, 2.70f);
            var roomB = new Room(3f, 9f, 9f, 15f, 2.40f);

            const float openX0 = 5.2f, openX1 = 6.8f, openH = 2.10f;

            // ---------------------------------------------------------- 床
            var floor = new MeshBuilder();
            floor.PlaneXZ(roomA.X0, roomA.X1, roomA.Z0, roomA.Z1, 0f, true, UvCarpet);
            floor.PlaneXZ(roomB.X0, roomB.X1, roomB.Z0, roomB.Z1, 0f, true, UvCarpet);
            MeshKit.Spawn(root.transform, "Floor", floor.ToMesh("Floor"), mats.Carpet);

            // ---------------------------------------------------------- 壁
            var wall = new MeshBuilder();
            // 高さ方向は壁の全高 (2.70 + 上部の伸ばし 0.20) をテクスチャ 1 枚で覆う。
            // これで幅木際の汚れと天井際の埃を、実際の高さに焼き込める。
            wall.UvScaleV = roomA.H + WallOverrun;
            // 主室
            wall.BeveledBox(new Vector3(roomA.X0 - WallT, 0, roomA.Z0 - WallT),
                     new Vector3(roomA.X0, roomA.H + WallOverrun, roomA.Z1 + WallT), BevelWall, UvWall);   // 西
            wall.BeveledBox(new Vector3(roomA.X1, 0, roomA.Z0 - WallT),
                     new Vector3(roomA.X1 + WallT, roomA.H + WallOverrun, roomA.Z1 + WallT), BevelWall, UvWall);   // 東
            wall.BeveledBox(new Vector3(roomA.X0 - WallT, 0, roomA.Z0 - WallT),
                     new Vector3(roomA.X1 + WallT, roomA.H + WallOverrun, roomA.Z0), BevelWall, UvWall);   // 南
            // 北壁（開口で 3 分割）
            wall.BeveledBox(new Vector3(roomA.X0 - WallT, 0, roomA.Z1),
                     new Vector3(openX0, roomA.H + WallOverrun, roomA.Z1 + WallT), BevelWall, UvWall);
            wall.BeveledBox(new Vector3(openX1, 0, roomA.Z1),
                     new Vector3(roomA.X1 + WallT, roomA.H + WallOverrun, roomA.Z1 + WallT), BevelWall, UvWall);
            wall.BeveledBox(new Vector3(openX0, openH, roomA.Z1),
                     new Vector3(openX1, roomA.H + WallOverrun, roomA.Z1 + WallT), BevelWall, UvWall);   // 開口上の垂れ壁
            // 副室
            wall.BeveledBox(new Vector3(roomB.X0 - WallT, 0, roomB.Z0),
                     new Vector3(roomB.X0, roomB.H + WallOverrun, roomB.Z1 + WallT), BevelWall, UvWall);
            wall.BeveledBox(new Vector3(roomB.X1, 0, roomB.Z0),
                     new Vector3(roomB.X1 + WallT, roomB.H + WallOverrun, roomB.Z1 + WallT), BevelWall, UvWall);
            wall.BeveledBox(new Vector3(roomB.X0 - WallT, 0, roomB.Z1),
                     new Vector3(roomB.X1 + WallT, roomB.H + WallOverrun, roomB.Z1 + WallT), BevelWall, UvWall);
            // 迷路感を出す袖壁と柱
            wall.BeveledBox(new Vector3(7.50f, 0, roomA.Z0), new Vector3(7.65f, roomA.H + WallOverrun, 3.60f), BevelWall, UvWall);
            wall.BeveledBox(new Vector3(3.40f, 0, 5.20f), new Vector3(3.80f, roomA.H + WallOverrun, 5.60f), BevelWall, UvWall);
            MeshKit.Spawn(root.transform, "Walls", wall.ToMesh("Walls"), mats.Wallpaper);

            // -------------------------------------------------------- 幅木
            var bb = new MeshBuilder();
            AddBaseboardRect(bb, roomA.X0, roomA.X1, roomA.Z0, roomA.Z1, openX0, openX1);
            AddBaseboardRect(bb, roomB.X0, roomB.X1, roomB.Z0, roomB.Z1, openX0, openX1);
            // 袖壁と柱の足元
            AddBaseboardAround(bb, 7.50f, 7.65f, roomA.Z0, 3.60f);
            AddBaseboardAround(bb, 3.40f, 3.80f, 5.20f, 5.60f);
            MeshKit.Spawn(root.transform, "Baseboards", bb.ToMesh("Baseboards"), mats.Baseboard);

            // -------------------------------------------------------- 天井
            var tiles = new MeshBuilder();
            var bars = new MeshBuilder();
            var frames = new MeshBuilder();
            var diffOn = new MeshBuilder();
            var diffOff = new MeshBuilder();

            // 器具のピッチ。3.0m 格子 (9.0 m2/灯) では天井の大半が暗いタイルになり、
            // 参照写真のような「天井そのものが光の面になっている」状態にならない。
            // 2.4m 格子 = 5.4 m2/灯 に詰める。
            BuildCeiling(roomA, new[] { (1, 1), (5, 1), (9, 1), (13, 1), (17, 1),
                                        (1, 5), (5, 5), (9, 5), (13, 5), (17, 5),
                                        (1, 9), (5, 9), (9, 9), (13, 9), (17, 9),
                                        (1, 13), (5, 13), (9, 13), (13, 13), (17, 13) },
                         new[] { false, false, false, false, false,
                                 false, false, true,  false, false,
                                 false, false, false, false, false,
                                 true,  false, false, false, false },   // 2 灯は切れている
                         tiles, bars, frames, diffOn, diffOff, res, rnd,
                         new[] { (3, 3), (15, 11) });   // 点検口

            BuildCeiling(roomB, new[] { (6, 16), (12, 16), (6, 20), (12, 20), (6, 24), (12, 24) },
                         new[] { false, false, false, false, true, false },   // 奥の 1 灯は切れている
                         tiles, bars, frames, diffOn, diffOff, res, rnd);

            // -------------------------------------------------- 建築設備
            // 実際のオフィスの天井には必ずスプリンクラー・煙感知器・空調吹出口があり、
            // 壁にはコンセントがある。これが無い空間は、材質をどれだけ詰めても
            // 「人が使っていた場所」に見えない。
            var chrome = new MeshBuilder();
            var plastic = new MeshBuilder();
            var vents = new MeshBuilder();
            BuildServices(roomA, roomB, chrome, plastic, vents);
            // 人が使っていた痕跡。スイッチ、サーモスタット、配線モール、床の見切り。
            // 設備が「建物として要る物」なのに対し、こちらは「人が触った物」で、
            // 無いと空間が『誰も居ない』ではなく『誰も居たことがない』に見える。
            BuildTraces(roomA, plastic, vents);
            if (!chrome.IsEmpty)
                MeshKit.Spawn(root.transform, "Sprinklers", chrome.ToMesh("Sprinklers"), mats.Chrome);
            if (!plastic.IsEmpty)
                MeshKit.Spawn(root.transform, "Fittings", plastic.ToMesh("Fittings"), mats.Plastic);
            if (!vents.IsEmpty)
                MeshKit.Spawn(root.transform, "AirDiffusers", vents.ToMesh("AirDiffusers"), mats.Metal);

            MeshKit.Spawn(root.transform, "CeilingTiles", tiles.ToMesh("CeilingTiles"), mats.CeilingTile);
            MeshKit.Spawn(root.transform, "CeilingGrid", bars.ToMesh("CeilingGrid"), mats.Metal);
            MeshKit.Spawn(root.transform, "TrofferFrames", frames.ToMesh("TrofferFrames"), mats.Metal);
            if (!diffOn.IsEmpty)
                MeshKit.Spawn(root.transform, "DiffusersOn", diffOn.ToMesh("DiffusersOn"), mats.DiffuserOn, false);
            if (!diffOff.IsEmpty)
                MeshKit.Spawn(root.transform, "DiffusersOff", diffOff.ToMesh("DiffusersOff"), mats.DiffuserOff, false);

            foreach (var mf in root.GetComponentsInChildren<MeshFilter>())
            {
                res.Verts += mf.sharedMesh.vertexCount;
                res.Tris += mf.sharedMesh.triangles.Length / 3;
            }
            return res;
        }

        // ------------------------------------------------------------ 設備生成
        static void BuildServices(Room a, Room b, MeshBuilder chrome, MeshBuilder plastic, MeshBuilder vents)
        {
            // --- スプリンクラー（消防法のおおむね 3.5m 間隔）---
            // 器具の位置を避けて配置する
            var heads = new (float x, float z, float h)[]
            {
                // z=3.00 は器具の列 (z 3.0-3.6) と重なるため 2.40 に逃がす
                (3.30f, 2.40f, a.H), (6.60f, 2.40f, a.H), (9.90f, 2.40f, a.H),
                (3.30f, 6.60f, a.H), (6.60f, 6.60f, a.H), (9.90f, 6.60f, a.H),
                (4.50f, 11.10f, b.H), (7.50f, 13.50f, b.H),
            };
            foreach (var (x, z, h) in heads)
            {
                // 天井を貫通する 15mm の落とし配管
                chrome.Cylinder(new Vector3(x, h - 0.055f, z), 0.0075f, 0.075f, 10, 0.5f);
                // 42mm のデフレクタ板
                chrome.Cylinder(new Vector3(x, h - 0.058f, z), 0.021f, 0.003f, 12, 0.5f);
                // 感熱体
                chrome.Cylinder(new Vector3(x, h - 0.052f, z), 0.004f, 0.014f, 8, 0.5f);
            }

            // --- 煙感知器 ---
            var detectors = new (float x, float z, float h)[]
            {
                (2.10f, 5.40f, a.H), (9.00f, 2.70f, a.H), (6.00f, 12.60f, b.H),
            };
            foreach (var (x, z, h) in detectors)
            {
                plastic.Cylinder(new Vector3(x, h - 0.042f, z), 0.065f, 0.042f, 20, 0.5f);
                plastic.Cylinder(new Vector3(x, h - 0.050f, z), 0.030f, 0.010f, 16, 0.5f);
            }

            // --- 空調吹出口（600 角の 4 方向ルーバ）---
            var diffusers = new (float x, float z, float h)[]
            {
                (5.10f, 5.10f, a.H), (10.50f, 6.30f, a.H), (6.30f, 10.50f, b.H),
            };
            foreach (var (cx, cz, h) in diffusers)
            {
                for (int k = 0; k < 4; k++)
                {
                    float half = (0.595f - k * 0.125f) * 0.5f;
                    float y = h - 0.004f + k * 0.024f;
                    float bar = 0.030f;
                    // 4 本のバーで角リングを作る。下から見ると同心の段になる。
                    vents.BeveledBox(new Vector3(cx - half, y, cz - half),
                                     new Vector3(cx + half, y + 0.020f, cz - half + bar), 0.0015f, 0.4f);
                    vents.BeveledBox(new Vector3(cx - half, y, cz + half - bar),
                                     new Vector3(cx + half, y + 0.020f, cz + half), 0.0015f, 0.4f);
                    vents.BeveledBox(new Vector3(cx - half, y, cz - half + bar),
                                     new Vector3(cx - half + bar, y + 0.020f, cz + half - bar), 0.0015f, 0.4f);
                    vents.BeveledBox(new Vector3(cx + half - bar, y, cz - half + bar),
                                     new Vector3(cx + half, y + 0.020f, cz + half - bar), 0.0015f, 0.4f);
                }
            }

            // --- 壁のコンセント（床上 300mm、実物どおり縦長）---
            AddOutlet(plastic, new Vector3(0.002f, 0.30f, 2.20f), true);
            AddOutlet(plastic, new Vector3(0.002f, 0.30f, 6.50f), true);
            AddOutlet(plastic, new Vector3(4.50f, 0.30f, 0.002f), false);
            AddOutlet(plastic, new Vector3(9.20f, 0.30f, 0.002f), false);
        }

        /// <summary>
        /// 人が触った物。壁付けのスイッチ類・配線モール・床の見切り。
        /// 実在の建物にこれらが無いことはなく、逆にこれが無いと
        /// どれだけ材質を詰めても「使われたことのない箱」に見える。
        /// </summary>
        static void BuildTraces(Room a, MeshBuilder plastic, MeshBuilder metal)
        {
            // --- 照明スイッチ（開口の脇、床上 1.25m）---
            // 北壁の室内側は z = a.Z1。室内は z が小さい側なので -Z 方向に出す。
            AddPlate(plastic, new Vector3(7.05f, 1.25f, a.Z1), Axis.Z, -1f, 0.086f, 0.122f);
            for (int i = 0; i < 2; i++)   // 2 連のロッカースイッチ
            {
                float cx = 7.05f + (i == 0 ? -0.020f : 0.020f);
                plastic.BeveledBox(new Vector3(cx - 0.016f, 1.25f - 0.030f, a.Z1 - 0.014f),
                                   new Vector3(cx + 0.016f, 1.25f + 0.030f, a.Z1 - 0.006f),
                                   0.0012f, 0.25f);
            }

            // --- サーモスタット（西壁、床上 1.50m）---
            AddPlate(plastic, new Vector3(a.X0, 1.50f, 4.20f), Axis.X, 1f, 0.088f, 0.088f);
            // 表示窓はプレート面 (X0 + 0.010) より前に出さないと埋もれる
            plastic.BeveledBox(new Vector3(a.X0 + 0.010f, 1.50f - 0.014f, 4.20f - 0.026f),
                               new Vector3(a.X0 + 0.0125f, 1.50f + 0.010f, 4.20f + 0.026f),
                               0.0008f, 0.25f);   // 表示窓

            // --- 配線モール（西壁、幅木の上を這わせる）---
            // 後から増設した LAN の配線。オフィスでは必ず見る。
            const float MouldY = BaseH + 0.012f, MouldH = 0.026f, MouldD = 0.020f;
            plastic.BeveledBox(new Vector3(a.X0, MouldY, 2.20f),
                               new Vector3(a.X0 + MouldD, MouldY + MouldH, 6.50f), 0.0015f, 0.5f);
            // 立ち上がりでコンセント高さまで上げる
            plastic.BeveledBox(new Vector3(a.X0, MouldY, 4.36f),
                               new Vector3(a.X0 + MouldD, 0.30f, 4.44f), 0.0015f, 0.5f);
            AddOutlet(plastic, new Vector3(a.X0 + 0.002f, 0.30f, 4.40f), true);

            // --- 床の見切り（開口の敷居）---
            // 部屋が変われば必ず床材の継ぎ目があり、アルミの見切りが入る。
            metal.BeveledBox(new Vector3(5.20f, 0f, a.Z1 - 0.004f),
                             new Vector3(6.80f, 0.004f, a.Z1 + WallT + 0.004f), 0.0008f, 1f);
        }

        enum Axis { X, Z }

        /// <summary>壁付けのプレート。dir は室内側を向く符号。</summary>
        static void AddPlate(MeshBuilder m, Vector3 p, Axis axis, float dir, float w, float h)
        {
            const float Proud = 0.010f;
            if (axis == Axis.X)
            {
                float x0 = dir > 0 ? p.x : p.x - Proud;
                m.BeveledBox(new Vector3(x0, p.y - h * 0.5f, p.z - w * 0.5f),
                             new Vector3(x0 + Proud, p.y + h * 0.5f, p.z + w * 0.5f), 0.0015f, 0.25f);
            }
            else
            {
                float z0 = dir > 0 ? p.z : p.z - Proud;
                m.BeveledBox(new Vector3(p.x - w * 0.5f, p.y - h * 0.5f, z0),
                             new Vector3(p.x + w * 0.5f, p.y + h * 0.5f, z0 + Proud), 0.0015f, 0.25f);
            }
        }

        /// <summary>2 口コンセント。onXWall=true なら X 固定の壁に、false なら Z 固定の壁に付ける。</summary>
        static void AddOutlet(MeshBuilder m, Vector3 p, bool onXWall)
        {
            const float PlateW = 0.070f, PlateH = 0.115f, Proud = 0.004f;
            if (onXWall)
            {
                m.BeveledBox(new Vector3(p.x, p.y - PlateH * 0.5f, p.z - PlateW * 0.5f),
                             new Vector3(p.x + Proud, p.y + PlateH * 0.5f, p.z + PlateW * 0.5f), 0.0015f, 0.25f);
                for (int i = -1; i <= 1; i += 2)
                {
                    float cy = p.y + i * 0.019f;
                    m.BeveledBox(new Vector3(p.x + Proud - 0.001f, cy - 0.011f, p.z - 0.012f),
                                 new Vector3(p.x + Proud + 0.0005f, cy + 0.011f, p.z + 0.012f), 0.0004f, 0.25f);
                }
            }
            else
            {
                m.BeveledBox(new Vector3(p.x - PlateW * 0.5f, p.y - PlateH * 0.5f, p.z),
                             new Vector3(p.x + PlateW * 0.5f, p.y + PlateH * 0.5f, p.z + Proud), 0.0015f, 0.25f);
                for (int i = -1; i <= 1; i += 2)
                {
                    float cy = p.y + i * 0.019f;
                    m.BeveledBox(new Vector3(p.x - 0.012f, cy - 0.011f, p.z + Proud - 0.001f),
                                 new Vector3(p.x + 0.012f, cy + 0.011f, p.z + Proud + 0.0005f), 0.0004f, 0.25f);
                }
            }
        }

        // ------------------------------------------------------------ 天井生成
        static void BuildCeiling(Room r, (int, int)[] trofferCells, bool[] dead,
                                 MeshBuilder tiles, MeshBuilder bars, MeshBuilder frames,
                                 MeshBuilder diffOn, MeshBuilder diffOff,
                                 Result res, System.Random rnd,
                                 (int, int)[] hatches = null)
        {
            int i0 = Mathf.RoundToInt(r.X0 / Cell), i1 = Mathf.RoundToInt(r.X1 / Cell);
            int j0 = Mathf.RoundToInt(r.Z0 / Cell), j1 = Mathf.RoundToInt(r.Z1 / Cell);
            float H = r.H;

            // トロファーが占める 2 セル分をマークする（x 方向に 2 セル = 1200mm）
            var occupied = new HashSet<(int, int)>();

            // --- 天井点検口 ---
            // 吊り天井には必ず点検口がある。吸音タイルの代わりに鋼板の蓋が
            // 落とし込まれ、面が一段下がってヒンジと掛け金が見える。
            if (hatches != null)
            {
                foreach (var (hi, hj) in hatches)
                {
                    occupied.Add((hi, hj));
                    float hx0 = hi * Cell + TBarW * 0.5f, hx1 = (hi + 1) * Cell - TBarW * 0.5f;
                    float hz0 = hj * Cell + TBarW * 0.5f, hz1 = (hj + 1) * Cell - TBarW * 0.5f;
                    // 蓋本体（タイル面よりわずかに下がる）
                    frames.BeveledBox(new Vector3(hx0, H + 0.004f, hz0),
                                      new Vector3(hx1, H + 0.020f, hz1), 0.0012f, UvMetal);
                    // 掛け金
                    frames.BeveledBox(new Vector3((hx0 + hx1) * 0.5f - 0.028f, H - 0.002f, hz0 + 0.012f),
                                      new Vector3((hx0 + hx1) * 0.5f + 0.028f, H + 0.004f, hz0 + 0.044f),
                                      0.0008f, UvMetal);
                }
            }

            for (int t = 0; t < trofferCells.Length; t++)
            {
                var (ci, cj) = trofferCells[t];
                occupied.Add((ci, cj));
                occupied.Add((ci + 1, cj));

                float x0 = ci * Cell, x1 = (ci + 2) * Cell;
                float z0 = cj * Cell, z1 = (cj + 1) * Cell;
                float inset = 0.02f;

                // 器具本体はレンズより上（天井裏側）に置く。ここを下げるとレンズを覆い隠してしまう。
                frames.BeveledBox(new Vector3(x0 + TBarW * 0.5f, H + TileDrop, z0 + TBarW * 0.5f),
                           new Vector3(x1 - TBarW * 0.5f, H + TileDrop + 0.09f, z1 - TBarW * 0.5f), BevelFrame, UvMetal);

                // 乳白レンズはグリッド面よりわずかに下に出る（実機もそうなっている）。
                // T バーの下端 (y = H) より下に置かないと、グリッドに隠れて光って見えない。
                var target = dead[t] ? diffOff : diffOn;
                target.PlaneXZ(x0 + inset, x1 - inset, z0 + inset, z1 - inset, H - 0.004f, false, 1f);

                // エリアライトはレンズのさらに下。T バーに遮られない高さにする。
                res.TrofferCenters.Add(new Vector3((x0 + x1) * 0.5f, H, (z0 + z1) * 0.5f));
                res.TrofferAlive.Add(!dead[t]);
                res.TrofferHeight.Add(H);
            }

            // 吸音タイル：6x6 アトラスからランダムなセルを選び、内側 96% だけを使う
            // （テクスチャに焼かれたグリッド線を避け、実ジオメトリの T バーと二重にしない）
            const int Atlas = 6;
            float half = (Cell - TBarW) * 0.5f;
            for (int i = i0; i < i1; i++)
            {
                for (int j = j0; j < j1; j++)
                {
                    if (occupied.Contains((i, j))) continue;
                    float cx = (i + 0.5f) * Cell, cz = (j + 0.5f) * Cell;
                    int ai = rnd.Next(Atlas), aj = rnd.Next(Atlas);
                    float u0 = (ai + 0.02f) / Atlas, u1 = (ai + 0.98f) / Atlas;
                    float v0 = (aj + 0.02f) / Atlas, v1 = (aj + 0.98f) / Atlas;

                    // 吊り天井のタイルは決して面一にならない。個体ごとに
                    // 座り高さがずれ、わずかに傾き、経年で中央が垂れる。
                    // ここを完全に平坦にすると一気に CG に見える。
                    float seat = H + TileDrop + ((float)rnd.NextDouble() - 0.5f) * 0.003f;
                    int roll = rnd.Next(100);
                    if (roll < 4) seat += 0.006f;        // 押し上げられたタイル
                    else if (roll < 9) seat -= 0.004f;   // 落ちかけたタイル
                    float sag = 0.0012f + (float)rnd.NextDouble() * 0.0035f;
                    float[] cornerTilt =
                    {
                        ((float)rnd.NextDouble() - 0.5f) * 0.0022f,
                        ((float)rnd.NextDouble() - 0.5f) * 0.0022f,
                        ((float)rnd.NextDouble() - 0.5f) * 0.0022f,
                        ((float)rnd.NextDouble() - 0.5f) * 0.0022f,
                    };

                    Vector3 TileP(float fx, float fz)
                    {
                        // 四隅の傾きを双一次補間し、中央ほど垂れ下がらせる
                        float tilt = Mathf.Lerp(Mathf.Lerp(cornerTilt[0], cornerTilt[1], fx),
                                                Mathf.Lerp(cornerTilt[3], cornerTilt[2], fx), fz);
                        float droop = Mathf.Sin(fx * Mathf.PI) * Mathf.Sin(fz * Mathf.PI) * sag;
                        return new Vector3(cx - half + fx * half * 2f,
                                           seat + tilt - droop,
                                           cz - half + fz * half * 2f);
                    }
                    // タイルごとに UV の向きを 8 通りからランダムに選ぶ。
                    // 6x6 のアトラスが実質 288 通りになり、雨漏りのシミのような
                    // 目立つ模様が同じ形で繰り返して見えるのを防ぐ。
                    int orient = rnd.Next(8);
                    Vector2 TileUv(float fx, float fz)
                    {
                        float a = fx, b = fz;
                        if ((orient & 4) != 0) { float t = a; a = b; b = t; }
                        if ((orient & 1) != 0) a = 1f - a;
                        if ((orient & 2) != 0) b = 1f - b;
                        return new Vector2(Mathf.Lerp(u0, u1, a), Mathf.Lerp(v0, v1, b));
                    }

                    // 中央を垂れさせるため 2x2 に分割する。下向き面: 法線 -Y
                    const int Sub = 2;
                    for (int sx = 0; sx < Sub; sx++)
                    {
                        for (int sz = 0; sz < Sub; sz++)
                        {
                            float fx0 = sx / (float)Sub, fx1 = (sx + 1) / (float)Sub;
                            float fz0 = sz / (float)Sub, fz1 = (sz + 1) / (float)Sub;
                            tiles.QuadUv(TileP(fx0, fz0), TileP(fx1, fz0), TileP(fx1, fz1), TileP(fx0, fz1),
                                         TileUv(fx0, fz0), TileUv(fx1, fz0), TileUv(fx1, fz1), TileUv(fx0, fz1));
                        }
                    }
                }
            }

            // T バーグリッド（実ジオメトリ）
            // 外周の 1 本は芯を室内側に半分ずらす。芯を壁芯に置いたままだと
            // バーの半分 (12mm) が壁を突き抜け、隣室から見ると壁の途中に
            // 水平の金属線が浮いて見える。実物も外周は T バーではなく
            // 壁見切りのアングルで、壁面に接して納まる。
            // 芯を壁面ちょうどに寄せるとバーの手前面が壁面と完全な同一平面になり、
            // 隣室から Z ファイティングで金属の帯が透けて見える。2mm 逃がす。
            const float BarGap = 0.002f;
            float xa = r.X0 + TBarW * 0.5f + BarGap, xb = r.X1 - TBarW * 0.5f - BarGap;
            float za = r.Z0 + TBarW * 0.5f + BarGap, zb = r.Z1 - TBarW * 0.5f - BarGap;
            for (int i = i0; i <= i1; i++)
            {
                float x = Mathf.Clamp(i * Cell, xa, xb);
                // 木口も壁面と同一平面にしない。ここを揃えると壁の上端に
                // 白い点が並んで透ける。
                bars.BeveledBox(new Vector3(x - TBarW * 0.5f, H, r.Z0 + BarGap),
                         new Vector3(x + TBarW * 0.5f, H + TBarH, r.Z1 - BarGap), BevelBar, UvMetal);
            }
            for (int j = j0; j <= j1; j++)
            {
                float z = Mathf.Clamp(j * Cell, za, zb);
                bars.BeveledBox(new Vector3(r.X0 + BarGap, H, z - TBarW * 0.5f),
                         new Vector3(r.X1 - BarGap, H + TBarH, z + TBarW * 0.5f), BevelBar, UvMetal);
            }
        }

        // ------------------------------------------------------------ 幅木生成
        static void AddBaseboardRect(MeshBuilder b, float x0, float x1, float z0, float z1,
                                     float openX0, float openX1)
        {
            // 南北（Z 固定）
            AddBaseboardRun(b, x0, x1, z0, true, openX0, openX1);
            AddBaseboardRun(b, x0, x1, z1, false, openX0, openX1);
            // 東西（X 固定）
            b.BeveledBox(new Vector3(x0, 0, z0), new Vector3(x0 + BaseT, BaseH, z1), BevelBase, 1f);
            b.BeveledBox(new Vector3(x1 - BaseT, 0, z0), new Vector3(x1, BaseH, z1), BevelBase, 1f);
        }

        /// <summary>Z 固定の壁沿いの幅木。開口部分は飛ばす。</summary>
        static void AddBaseboardRun(MeshBuilder b, float x0, float x1, float z, bool innerPosZ,
                                    float openX0, float openX1)
        {
            float za = innerPosZ ? z : z - BaseT;
            float zb = innerPosZ ? z + BaseT : z;
            bool splits = openX0 > x0 && openX1 < x1;
            if (splits)
            {
                b.BeveledBox(new Vector3(x0, 0, za), new Vector3(openX0, BaseH, zb), BevelBase, 1f);
                b.BeveledBox(new Vector3(openX1, 0, za), new Vector3(x1, BaseH, zb), BevelBase, 1f);
            }
            else
            {
                b.BeveledBox(new Vector3(x0, 0, za), new Vector3(x1, BaseH, zb), BevelBase, 1f);
            }
        }

        static void AddBaseboardAround(MeshBuilder b, float x0, float x1, float z0, float z1)
        {
            b.BeveledBox(new Vector3(x0 - BaseT, 0, z0 - BaseT), new Vector3(x1 + BaseT, BaseH, z0), BevelBase, 1f);
            b.BeveledBox(new Vector3(x0 - BaseT, 0, z1), new Vector3(x1 + BaseT, BaseH, z1 + BaseT), BevelBase, 1f);
            b.BeveledBox(new Vector3(x0 - BaseT, 0, z0), new Vector3(x0, BaseH, z1), BevelBase, 1f);
            b.BeveledBox(new Vector3(x1, 0, z0), new Vector3(x1 + BaseT, BaseH, z1), BevelBase, 1f);
        }
    }
}
