using System.Collections.Generic;
using UnityEngine;

namespace Backrooms.Generation
{
    /// <summary>
    /// M2: ハイブリッド生成。
    ///
    /// 手作りモジュールを並べるのではなく、実在のオフィスビルの造られ方
    /// —— 2.4m のベイ割り、その上に載る 600mm の天井グリッド、区画ごとの
    /// 天井高、天井高が変わる境界に落ちる垂れ壁 —— を規則として持たせ、
    /// 間仕切りの位置だけを乱数で決める。
    ///
    /// 「部屋を掘って繋ぐ」のではなく「一枚の床に間仕切りを立てる」構成に
    /// しているのは、Level 0 が本来オフィスの内装であって迷路ではないため。
    /// 廊下を掘ると必ずダンジョンに見える。
    /// </summary>
    public static class Level0Generator
    {
        public const float Bay = 2.40f;          // 柱間。天井グリッド 4 枚ぶん
        public const int CellsPerBay = 4;

        const float Cell = Level0Builder.Cell;
        const float WallT = Level0Builder.WallT;
        const float BulkT = Level0Builder.BulkT;
        const float TBarW = Level0Builder.TBarW;
        const float TBarH = Level0Builder.TBarH;
        const float AngleW = Level0Builder.AngleW;
        const float AngleH = Level0Builder.AngleH;
        const float TileReveal = Level0Builder.TileReveal;
        const float BaseH = Level0Builder.BaseH;
        const float BaseT = Level0Builder.BaseT;
        const float WallTop = Level0Builder.WallTop;
        const float UvMetal = Level0Builder.UvMetal;

        const float DoorW = 1.60f, DoorH = 2.10f;
        /// <summary>
        /// 壁見切りアングルを壁面に食い込ませる量。壁は WallBow で最大 4mm
        /// 反るのに天井のグリッドは反らないので、木口を壁面ちょうどに置くと
        /// その差が天井裏まで抜ける黒い筋として出る。必ず重ねる。
        /// </summary>
        const float AngleBite = 0.004f;
        const float ColHalf = 0.288f;            // 天井グリッド 1 マスにちょうど納まる柱

        public struct DecalSpec
        {
            public string Kind;
            public Vector3 Pos, Euler;
            public Vector2 Size;
            public DecalSpec(string k, Vector3 p, Vector3 e, Vector2 s)
            { Kind = k; Pos = p; Euler = e; Size = s; }
        }

        public struct ShotSpec
        {
            public string Name;
            public Vector3 Pos;
            public float Yaw, Pitch;
            public bool Light;
            public ShotSpec(string n, Vector3 p, float yaw, float pitch, bool light = false)
            { Name = n; Pos = p; Yaw = yaw; Pitch = pitch; Light = light; }
        }

        public class Result
        {
            public GameObject Root;
            public List<Vector3> TrofferCenters = new List<Vector3>();
            public List<bool> TrofferAlive = new List<bool>();
            public List<DecalSpec> Decals = new List<DecalSpec>();
            public List<ShotSpec> Shots = new List<ShotSpec>();
            public Vector3 Spawn;
            public float SpawnYaw;
            public List<Vector3> Walk = new List<Vector3>();
            public List<string> Log = new List<string>();
            public int Verts, Tris;
        }

        /// <summary>
        /// 天井のメッシュ束。同じ形の面でも「明かりが生きている区画」と
        /// 「落ちている区画」でマテリアルを分ける必要がある。
        ///
        /// 天井タイルに入れている 10 nits は、HDRP の RTGI が下向き面に運ぶ
        /// 間接光の不足を埋めるための補填で、物理ではない。埋めるべき光が
        /// そもそも無い暗黒区画にまで一律に掛けると、蛍光灯が全部落ちている
        /// 部屋の天井だけが光り続ける。補填は「光がある所」にだけ入れる。
        /// </summary>
        class CeilMeshes
        {
            public MeshBuilder Tiles = new MeshBuilder();
            public MeshBuilder TilesDark = new MeshBuilder();
            public MeshBuilder Bars = new MeshBuilder();
            public MeshBuilder BarsDark = new MeshBuilder();
            public MeshBuilder Fix = new MeshBuilder();
            public MeshBuilder FixDark = new MeshBuilder();
            public MeshBuilder Vents = new MeshBuilder();
            public MeshBuilder DiffOn = new MeshBuilder();
            public MeshBuilder DiffOff = new MeshBuilder();
            public MeshBuilder Plenum = new MeshBuilder();

            public MeshBuilder T(bool dark) => dark ? TilesDark : Tiles;
            public MeshBuilder B(bool dark) => dark ? BarsDark : Bars;
            public MeshBuilder F(bool dark) => dark ? FixDark : Fix;
        }

        struct Zone
        {
            public int X0, X1, Z0, Z1;   // ベイ座標 [X0,X1) x [Z0,Z1)
            public float H;
            public int Light;            // 0 通常 / 1 まばら / 2 暗黒
        }

        /// <summary>壁の一面。デカールを置く候補になる。</summary>
        struct Face
        {
            public Vector3 Center;   // y = 0 の面上
            public float Yaw;        // デカールの forward(+Z) が面に刺さる向き
            public float Len;
        }

        // ================================================================ 本体
        public static Result Build(MaterialSet mats, int bx = 10, int bz = 9, int seed = 1996)
        {
            var g = new Gen(bx, bz, seed);
            g.Plan();
            return g.Emit(mats);
        }

        // ------------------------------------------------------------- 生成器
        class Gen
        {
            public readonly int BX, BZ, NX, NZ;
            readonly System.Random rnd;

            // 間仕切り。0 = 無し / 1 = 壁 / 2 = 開口付きの壁
            int[,] wx;   // [BX+1, BZ]   x = i*Bay の面
            int[,] wz;   // [BX, BZ+1]   z = j*Bay の面
            bool[,] col; // [BX+1, BZ+1] 独立柱（実体は隣接ベイの隅のセル）
            int[,] zoneOf;
            readonly List<Zone> zones = new List<Zone>();
            readonly List<Face> faces = new List<Face>();
            readonly List<Vector3> columnPts = new List<Vector3>();
            readonly List<(Vector3 pos, float yaw, bool xWall)> doorPts = new List<(Vector3, float, bool)>();
            readonly List<Vector3> stainPts = new List<Vector3>();   // タイルが落ちた位置

            int spawnBX = 1, spawnBZ = 1;
            public readonly List<string> log = new List<string>();

            public Gen(int bx, int bz, int seed)
            {
                BX = bx; BZ = bz;
                NX = bx * CellsPerBay; NZ = bz * CellsPerBay;
                rnd = new System.Random(seed);
                wx = new int[BX + 1, BZ];
                wz = new int[BX, BZ + 1];
                col = new bool[BX + 1, BZ + 1];
                zoneOf = new int[BX, BZ];
            }

            float R => (float)rnd.NextDouble();
            float ZoneH(int bx, int bz) => zones[zoneOf[Mathf.Clamp(bx, 0, BX - 1), Mathf.Clamp(bz, 0, BZ - 1)]].H;
            int ZoneLight(int bx, int bz) => zones[zoneOf[bx, bz]].Light;
            float CellH(int i, int j) => ZoneH(Mathf.Clamp(i, 0, NX - 1) / CellsPerBay,
                                                Mathf.Clamp(j, 0, NZ - 1) / CellsPerBay);
            static int Key(int a, int b) => a * 4096 + b;

            // ======================================================== 平面計画
            public void Plan()
            {
                SplitZones(0, BX, 0, BZ, 0);
                for (int z = 0; z < zones.Count; z++)
                {
                    var Z = zones[z];
                    for (int i = Z.X0; i < Z.X1; i++)
                        for (int j = Z.Z0; j < Z.Z1; j++) zoneOf[i, j] = z;
                }
                AssignHeights();

                for (int j = 0; j < BZ; j++) { wx[0, j] = 1; wx[BX, j] = 1; }
                for (int i = 0; i < BX; i++) { wz[i, 0] = 1; wz[i, BZ] = 1; }

                // 区画の境界には壁が立ちやすく、区画の中では疎らに立つ。
                for (int i = 1; i < BX; i++)
                    for (int j = 0; j < BZ; j++)
                        if (R < (zoneOf[i - 1, j] != zoneOf[i, j] ? 0.60f : 0.24f)) wx[i, j] = 1;
                for (int i = 0; i < BX; i++)
                    for (int j = 1; j < BZ; j++)
                        if (R < (zoneOf[i, j - 1] != zoneOf[i, j] ? 0.60f : 0.24f)) wz[i, j] = 1;

                Connect();

                // 全ての間仕切りが行き止まりでは迷路になる。実在のオフィスは
                // 区画の間に必ずドアがあり、そこだけ枠が入る。
                int doors = 0;
                for (int i = 1; i < BX; i++)
                    for (int j = 0; j < BZ; j++)
                        if (wx[i, j] == 1 && R < 0.20f) { wx[i, j] = 2; doors++; }
                for (int i = 0; i < BX; i++)
                    for (int j = 1; j < BZ; j++)
                        if (wz[i, j] == 1 && R < 0.20f) { wz[i, j] = 2; doors++; }

                // 独立柱。4 辺とも壁が無い格子点にだけ立つ。Backrooms の
                // 「柱が奥へ何列も続く」画は、廊下ではなくこの柱の反復で作られている。
                int cols = 0;
                for (int i = 1; i < BX; i++)
                    for (int j = 1; j < BZ; j++)
                        if (wx[i, j - 1] == 0 && wx[i, j] == 0 && wz[i - 1, j] == 0 && wz[i, j] == 0
                            && !(i == spawnBX && j == spawnBZ) && R < 0.32f)
                        { col[i, j] = true; cols++; }

                int walls = 0;
                foreach (var v in wx) if (v != 0) walls++;
                foreach (var v in wz) if (v != 0) walls++;
                log.Add($"plan: {BX}x{BZ} bays = {BX * Bay:F1} x {BZ * Bay:F1} m,  {zones.Count} zones,"
                        + $"  {walls} wall segments ({doors} with openings),  {cols} columns");
                for (int z = 0; z < zones.Count; z++)
                {
                    var Z = zones[z];
                    log.Add($"  zone {z}: bays [{Z.X0},{Z.X1}) x [{Z.Z0},{Z.Z1})  ceiling {Z.H:F2} m  "
                            + (Z.Light == 0 ? "lit" : Z.Light == 1 ? "sparse" : "DARK"));
                }
                DumpPlan();
            }

            /// <summary>
            /// 間取りをログに落とす。生成規則が意図どおり働いたかは、
            /// レンダを 9 枚見ても分からない。平面で見る必要がある。
            /// 上段は人が読む図、下段は図を描き起こすための素の配列。
            /// </summary>
            void DumpPlan()
            {
                var sb = new System.Text.StringBuilder();
                log.Add("plan map  ( | 壁 / : 開口 / --- 壁 / -D- 開口 / * 柱 / L=2.40 M=2.70 H=3.00 / 数字は区画 )");
                for (int j = BZ - 1; j >= 0; j--)
                {
                    sb.Clear();
                    for (int i = 0; i < BX; i++)
                        sb.Append('+').Append(wz[i, j + 1] == 1 ? "---" : wz[i, j + 1] == 2 ? "-D-" : "   ");
                    sb.Append('+');
                    log.Add("  " + sb);

                    sb.Clear();
                    for (int i = 0; i < BX; i++)
                    {
                        sb.Append(wx[i, j] == 1 ? '|' : wx[i, j] == 2 ? ':' : ' ');
                        var Z = zones[zoneOf[i, j]];
                        sb.Append((char)('0' + zoneOf[i, j]));
                        sb.Append(col[i, j] ? '*' : (Z.Light == 2 ? '#' : Z.Light == 1 ? '~' : ' '));
                        sb.Append(Z.H < 2.5f ? 'L' : Z.H > 2.8f ? 'H' : 'M');
                    }
                    sb.Append(wx[BX, j] == 1 ? '|' : wx[BX, j] == 2 ? ':' : ' ');
                    log.Add("  " + sb);
                }
                sb.Clear();
                for (int i = 0; i < BX; i++)
                    sb.Append('+').Append(wz[i, 0] == 1 ? "---" : wz[i, 0] == 2 ? "-D-" : "   ");
                sb.Append('+');
                log.Add("  " + sb);

                // 図を描き起こすための素の配列
                log.Add($"PLAN BX={BX} BZ={BZ} BAY={Bay}");
                sb.Clear();
                for (int i = 0; i <= BX; i++) for (int j = 0; j < BZ; j++) sb.Append((char)('0' + wx[i, j]));
                log.Add("PLAN_WX " + sb);
                sb.Clear();
                for (int i = 0; i < BX; i++) for (int j = 0; j <= BZ; j++) sb.Append((char)('0' + wz[i, j]));
                log.Add("PLAN_WZ " + sb);
                sb.Clear();
                for (int i = 0; i < BX; i++)
                    for (int j = 0; j < BZ; j++)
                    {
                        var Z = zones[zoneOf[i, j]];
                        sb.Append((char)('0' + zoneOf[i, j]));
                        sb.Append(Z.H < 2.5f ? 'L' : Z.H > 2.8f ? 'H' : 'M');
                        sb.Append((char)('0' + Z.Light));
                        sb.Append(col[i, j] ? '*' : '.');
                    }
                log.Add("PLAN_CELL " + sb);
            }

            void SplitZones(int x0, int x1, int z0, int z1, int depth)
            {
                int w = x1 - x0, h = z1 - z0;
                bool canX = w >= 5, canZ = h >= 5;
                if (depth >= 3 || (!canX && !canZ) || (w * h <= 12 && R < 0.7f))
                {
                    zones.Add(new Zone { X0 = x0, X1 = x1, Z0 = z0, Z1 = z1, H = 2.70f, Light = 0 });
                    return;
                }
                if (canX && (!canZ || w >= h))
                {
                    int c = x0 + 2 + rnd.Next(w - 3);
                    SplitZones(x0, c, z0, z1, depth + 1);
                    SplitZones(c, x1, z0, z1, depth + 1);
                }
                else
                {
                    int c = z0 + 2 + rnd.Next(h - 3);
                    SplitZones(x0, x1, z0, c, depth + 1);
                    SplitZones(x0, x1, c, z1, depth + 1);
                }
            }

            void AssignHeights()
            {
                // 実在のオフィスには 1 つの基準高さがあり、設備の都合で数区画だけ
                // 下がる（ダクトを通した所）か上がる（もとが倉庫だった所）。
                // 全区画をばらばらにすると遊園地になる。
                var order = new List<int>();
                for (int i = 0; i < zones.Count; i++) order.Add(i);
                for (int i = order.Count - 1; i > 0; i--)
                {
                    int k = rnd.Next(i + 1);
                    int t = order[i]; order[i] = order[k]; order[k] = t;
                }

                int lowCount = Mathf.Max(1, zones.Count / 4);
                int highCount = Mathf.Max(1, zones.Count / 6);
                for (int k = 0; k < order.Count; k++)
                {
                    var Z = zones[order[k]];
                    Z.H = k < lowCount ? 2.40f : k < lowCount + highCount ? 3.00f : 2.70f;
                    zones[order[k]] = Z;
                }

                // 蛍光灯が全部生きている建物は無い。1 区画は完全に落ちていて、
                // そこは手持ちライトでしか歩けない。
                int spawnZone = zoneOf[spawnBX, spawnBZ];
                int dark = order[order.Count - 1];
                if (dark == spawnZone && order.Count > 1) dark = order[order.Count - 2];
                var d = zones[dark]; d.Light = 2; zones[dark] = d;

                int sparse = 0;
                for (int k = 0; k < order.Count && sparse < 2; k++)
                {
                    if (order[k] == dark || order[k] == spawnZone) continue;
                    var s = zones[order[k]];
                    if (s.Light != 0) continue;
                    s.Light = 1; zones[order[k]] = s; sparse++;
                }
            }

            bool Passable(int i, int j, int di, int dj)
            {
                if (di != 0) return wx[di > 0 ? i + 1 : i, j] != 1;
                return wz[i, dj > 0 ? j + 1 : j] != 1;
            }

            static readonly (int, int)[] Dirs = { (1, 0), (-1, 0), (0, 1), (0, -1) };

            /// <summary>
            /// 到達できないベイを残さない。ここを省くと生成のたびに「入れない部屋」が
            /// でき、外からは壁の向こうに光が漏れているだけの死んだ空間になる。
            /// </summary>
            void Connect()
            {
                var seen = new bool[BX, BZ];
                int opened = 0;
                for (int guard = 0; guard < 400; guard++)
                {
                    System.Array.Clear(seen, 0, seen.Length);
                    var q = new Queue<(int, int)>();
                    q.Enqueue((spawnBX, spawnBZ)); seen[spawnBX, spawnBZ] = true;
                    int n = 1;
                    while (q.Count > 0)
                    {
                        var (i, j) = q.Dequeue();
                        foreach (var (di, dj) in Dirs)
                        {
                            int a = i + di, b = j + dj;
                            if (a < 0 || b < 0 || a >= BX || b >= BZ || seen[a, b]) continue;
                            if (!Passable(i, j, di, dj)) continue;
                            seen[a, b] = true; n++; q.Enqueue((a, b));
                        }
                    }
                    if (n == BX * BZ)
                    { log.Add($"connectivity: all {n} bays reachable (opened {opened} walls)"); return; }

                    bool cut = false;
                    for (int i = 0; i < BX && !cut; i++)
                        for (int j = 0; j < BZ && !cut; j++)
                        {
                            if (!seen[i, j]) continue;
                            foreach (var (di, dj) in Dirs)
                            {
                                int a = i + di, b = j + dj;
                                if (a < 0 || b < 0 || a >= BX || b >= BZ || seen[a, b]) continue;
                                if (di != 0) wx[di > 0 ? i + 1 : i, j] = 2;
                                else wz[i, dj > 0 ? j + 1 : j] = 2;
                                opened++; cut = true; break;
                            }
                        }
                    if (!cut) break;
                }
                log.Add("connectivity: gave up after 400 passes");
            }

            // ================================================== ジオメトリ生成
            public Result Emit(MaterialSet mats)
            {
                var res = new Result();
                var root = new GameObject("Level0_Generated");
                res.Root = root;
                float W = BX * Bay, D = BZ * Bay;

                var floor = new MeshBuilder();
                floor.PlaneXZ(0f, W, 0f, D, 0f, true, Level0Builder.UvCarpet);
                MeshKit.Spawn(root.transform, "Floor", floor.ToMesh("Floor"), mats.Carpet);

                var wall = new MeshBuilder { UvScaleV = Level0Builder.UvWallV };
                var frames = new MeshBuilder();
                var bb = new MeshBuilder();
                var metal = new MeshBuilder();
                EmitWalls(wall, frames, bb, metal);
                EmitColumns(wall, bb);
                EmitBulkheads(wall);
                wall.Warp(Level0Builder.WallBow);
                bb.Warp(Level0Builder.WallBow);
                frames.Warp(Level0Builder.WallBow);
                MeshKit.Spawn(root.transform, "Walls", wall.ToMesh("Walls"), mats.Wallpaper);
                MeshKit.Spawn(root.transform, "Baseboards", bb.ToMesh("Baseboards"), mats.Baseboard);
                if (!frames.IsEmpty)
                    MeshKit.Spawn(root.transform, "OpeningFrames", frames.ToMesh("OpeningFrames"),
                                  mats.DoorFrame != null ? mats.DoorFrame : mats.Metal);

                var cm = new CeilMeshes();
                EmitCeiling(cm, res);
                foreach (var m in new[] { cm.Tiles, cm.TilesDark, cm.Bars, cm.BarsDark })
                    m.Warp(Level0Builder.CeilingSag);
                var tileDarkMat = mats.CeilingTileDark != null ? mats.CeilingTileDark : mats.CeilingTile;
                var metalDarkMat = mats.MetalDark != null ? mats.MetalDark : mats.Metal;
                Spawn(root, "CeilingTiles", cm.Tiles, mats.CeilingTile);
                Spawn(root, "CeilingTiles_Dark", cm.TilesDark, tileDarkMat);
                Spawn(root, "CeilingGrid", cm.Bars, mats.Metal);
                Spawn(root, "CeilingGrid_Dark", cm.BarsDark, metalDarkMat);
                Spawn(root, "TrofferFrames", cm.Fix, mats.Metal);
                Spawn(root, "TrofferFrames_Dark", cm.FixDark, metalDarkMat);
                Spawn(root, "AirDiffusers", cm.Vents, mats.Metal);
                if (!cm.DiffOn.IsEmpty)
                    MeshKit.Spawn(root.transform, "DiffusersOn", cm.DiffOn.ToMesh("DiffusersOn"), mats.DiffuserOn, false);
                if (!cm.DiffOff.IsEmpty)
                    MeshKit.Spawn(root.transform, "DiffusersOff", cm.DiffOff.ToMesh("DiffusersOff"), mats.DiffuserOff, false);
                Spawn(root, "Plenum", cm.Plenum, mats.Plenum != null ? mats.Plenum : mats.Metal);

                var chrome = new MeshBuilder();
                var plastic = new MeshBuilder();
                EmitServices(chrome, plastic);
                var vents = cm.Vents;
                EmitTraces(plastic, metal);
                plastic.Warp(Level0Builder.WallBow);
                if (!chrome.IsEmpty) MeshKit.Spawn(root.transform, "Sprinklers", chrome.ToMesh("Sprinklers"), mats.Chrome);
                if (!plastic.IsEmpty) MeshKit.Spawn(root.transform, "Fittings", plastic.ToMesh("Fittings"), mats.Plastic);
                if (!metal.IsEmpty) MeshKit.Spawn(root.transform, "Thresholds", metal.ToMesh("Thresholds"), mats.Metal);

                PlaceDecals(res);
                PlaceShots(res);

                foreach (var mf in root.GetComponentsInChildren<MeshFilter>())
                {
                    res.Verts += mf.sharedMesh.vertexCount;
                    res.Tris += mf.sharedMesh.triangles.Length / 3;
                }
                int alive = 0;
                foreach (var a in res.TrofferAlive) if (a) alive++;
                log.Add($"geometry: {res.Verts} verts / {res.Tris} tris / "
                        + $"{res.TrofferCenters.Count} troffers ({alive} lit)");
                res.Log.AddRange(log);
                return res;
            }

            static void Spawn(GameObject root, string name, MeshBuilder m, Material mat)
            {
                if (m == null || m.IsEmpty) return;
                MeshKit.Spawn(root.transform, name, m.ToMesh(name), mat);
            }

            // ------------------------------------------------------------ 壁
            void EmitWalls(MeshBuilder wall, MeshBuilder frames, MeshBuilder bb, MeshBuilder metal)
            {
                for (int i = 0; i <= BX; i++)
                {
                    int j = 0;
                    while (j < BZ)
                    {
                        if (wx[i, j] != 1)
                        {
                            if (wx[i, j] == 2) EmitDoorWall(wall, frames, bb, metal, true, i, j);
                            j++; continue;
                        }
                        int j0 = j;
                        while (j < BZ && wx[i, j] == 1) j++;
                        float x = i * Bay;
                        wall.BeveledBox(new Vector3(x - WallT * 0.5f, 0f, j0 * Bay),
                                        new Vector3(x + WallT * 0.5f, WallTop, j * Bay),
                                        Level0Builder.BevelWall, Level0Builder.UvWall);
                        BaseRunZ(bb, x, j0 * Bay, j * Bay, i > 0, i < BX);
                        AddFacesZ(x, j0 * Bay, j * Bay, i > 0, i < BX);
                    }
                }
                for (int j = 0; j <= BZ; j++)
                {
                    int i = 0;
                    while (i < BX)
                    {
                        if (wz[i, j] != 1)
                        {
                            if (wz[i, j] == 2) EmitDoorWall(wall, frames, bb, metal, false, i, j);
                            i++; continue;
                        }
                        int i0 = i;
                        while (i < BX && wz[i, j] == 1) i++;
                        float z = j * Bay;
                        wall.BeveledBox(new Vector3(i0 * Bay, 0f, z - WallT * 0.5f),
                                        new Vector3(i * Bay, WallTop, z + WallT * 0.5f),
                                        Level0Builder.BevelWall, Level0Builder.UvWall);
                        BaseRunX(bb, z, i0 * Bay, i * Bay, j > 0, j < BZ);
                        AddFacesX(z, i0 * Bay, i * Bay, j > 0, j < BZ);
                    }
                }
            }

            /// <summary>開口付きの壁 1 ベイぶん。袖壁 2 枚 + 垂れ壁 + 三方枠 + 敷居。</summary>
            void EmitDoorWall(MeshBuilder wall, MeshBuilder frames, MeshBuilder bb, MeshBuilder metal,
                              bool xWall, int i, int j)
            {
                float fixedPos = xWall ? i * Bay : j * Bay;
                float a0 = xWall ? j * Bay : i * Bay;
                float a1 = a0 + Bay;
                float c = (a0 + a1) * 0.5f;
                float d0 = c - DoorW * 0.5f, d1 = c + DoorW * 0.5f;
                float p0 = fixedPos - WallT * 0.5f, p1 = fixedPos + WallT * 0.5f;

                void Bx(float u0, float y0, float u1, float y1)
                {
                    var min = xWall ? new Vector3(p0, y0, u0) : new Vector3(u0, y0, p0);
                    var max = xWall ? new Vector3(p1, y1, u1) : new Vector3(u1, y1, p1);
                    wall.BeveledBox(min, max, Level0Builder.BevelWall, Level0Builder.UvWall);
                }
                Bx(a0, 0f, d0, WallTop);
                Bx(d1, 0f, a1, WallTop);
                Bx(d0, DoorH, d1, WallTop);
                Level0Builder.OpeningFrame(frames, !xWall, d0, d1, DoorH, fixedPos);

                if (xWall)
                {
                    BaseRunZ(bb, fixedPos, a0, d0, i > 0, i < BX);
                    BaseRunZ(bb, fixedPos, d1, a1, i > 0, i < BX);
                    AddFacesZ(fixedPos, a0, d0, i > 0, i < BX);
                    AddFacesZ(fixedPos, d1, a1, i > 0, i < BX);
                    // 床の見切り。部屋が変われば必ず床材の継ぎ目があり、アルミが入る。
                    metal.BeveledBox(new Vector3(p0 - 0.004f, 0f, d0), new Vector3(p1 + 0.004f, 0.004f, d1),
                                     0.0008f, 1f);
                    doorPts.Add((new Vector3(fixedPos, 0f, c), 90f, true));
                }
                else
                {
                    BaseRunX(bb, fixedPos, a0, d0, j > 0, j < BZ);
                    BaseRunX(bb, fixedPos, d1, a1, j > 0, j < BZ);
                    AddFacesX(fixedPos, a0, d0, j > 0, j < BZ);
                    AddFacesX(fixedPos, d1, a1, j > 0, j < BZ);
                    metal.BeveledBox(new Vector3(d0, 0f, p0 - 0.004f), new Vector3(d1, 0.004f, p1 + 0.004f),
                                     0.0008f, 1f);
                    doorPts.Add((new Vector3(c, 0f, fixedPos), 0f, false));
                }
            }

            void AddFacesZ(float xc, float z0, float z1, bool negSide, bool posSide)
            {
                if (z1 - z0 < 0.5f) return;
                float zc = (z0 + z1) * 0.5f, len = z1 - z0;
                // 面の法線 +X -> デカールの forward(+Z) は -X を向く = yaw -90
                if (posSide) faces.Add(new Face { Center = new Vector3(xc + WallT * 0.5f, 0f, zc), Yaw = -90f, Len = len });
                if (negSide) faces.Add(new Face { Center = new Vector3(xc - WallT * 0.5f, 0f, zc), Yaw = 90f, Len = len });
            }

            void AddFacesX(float zc, float x0, float x1, bool negSide, bool posSide)
            {
                if (x1 - x0 < 0.5f) return;
                float xc = (x0 + x1) * 0.5f, len = x1 - x0;
                if (posSide) faces.Add(new Face { Center = new Vector3(xc, 0f, zc + WallT * 0.5f), Yaw = 180f, Len = len });
                if (negSide) faces.Add(new Face { Center = new Vector3(xc, 0f, zc - WallT * 0.5f), Yaw = 0f, Len = len });
            }

            void BaseRunZ(MeshBuilder bb, float xc, float z0, float z1, bool negSide, bool posSide)
            {
                if (z1 - z0 < 0.02f) return;
                float e = BaseT;   // 入隅に隙間を残さないよう両端を延ばす
                if (posSide)
                    bb.BeveledBox(new Vector3(xc + WallT * 0.5f, 0f, z0 - e),
                                  new Vector3(xc + WallT * 0.5f + BaseT, BaseH, z1 + e), Level0Builder.BevelBase, 1f);
                if (negSide)
                    bb.BeveledBox(new Vector3(xc - WallT * 0.5f - BaseT, 0f, z0 - e),
                                  new Vector3(xc - WallT * 0.5f, BaseH, z1 + e), Level0Builder.BevelBase, 1f);
            }

            void BaseRunX(MeshBuilder bb, float zc, float x0, float x1, bool negSide, bool posSide)
            {
                if (x1 - x0 < 0.02f) return;
                float e = BaseT;
                if (posSide)
                    bb.BeveledBox(new Vector3(x0 - e, 0f, zc + WallT * 0.5f),
                                  new Vector3(x1 + e, BaseH, zc + WallT * 0.5f + BaseT), Level0Builder.BevelBase, 1f);
                if (negSide)
                    bb.BeveledBox(new Vector3(x0 - e, 0f, zc - WallT * 0.5f - BaseT),
                                  new Vector3(x1 + e, BaseH, zc - WallT * 0.5f), Level0Builder.BevelBase, 1f);
            }

            /// <summary>
            /// 独立柱。天井グリッドと切り合わないよう、格子点そのものではなく
            /// 隣のセル 1 マスに納める。実在の建物も、柱が来る位置に合わせて
            /// グリッドの割り付けを決めるのでタイルが柱で切られることはない。
            /// </summary>
            void EmitColumns(MeshBuilder wall, MeshBuilder bb)
            {
                for (int i = 1; i < BX; i++)
                    for (int j = 1; j < BZ; j++)
                    {
                        if (!col[i, j]) continue;
                        float x = i * Bay + Cell * 0.5f, z = j * Bay + Cell * 0.5f;
                        columnPts.Add(new Vector3(x, 0f, z));
                        wall.BeveledBox(new Vector3(x - ColHalf, 0f, z - ColHalf),
                                        new Vector3(x + ColHalf, WallTop, z + ColHalf),
                                        Level0Builder.BevelWall, Level0Builder.UvWall);
                        bb.BeveledBox(new Vector3(x - ColHalf - BaseT, 0f, z - ColHalf - BaseT),
                                      new Vector3(x + ColHalf + BaseT, BaseH, z + ColHalf + BaseT),
                                      Level0Builder.BevelBase, 1f);
                    }
            }

            bool ColumnAtCell(int i, int j)
            {
                if (i % CellsPerBay != 0 || j % CellsPerBay != 0) return false;
                int bi = i / CellsPerBay, bj = j / CellsPerBay;
                return bi >= 1 && bj >= 1 && bi <= BX && bj <= BZ && col[bi, bj];
            }

            /// <summary>
            /// 天井高が変わる境界の垂れ壁。壁が無いのに天井高だけ変わる箇所は
            /// 実在しない。必ずここに 100mm の壁が落ちてくる。
            /// </summary>
            void EmitBulkheads(MeshBuilder wall)
            {
                for (int i = 1; i < BX; i++)
                    for (int j = 0; j < BZ; j++)
                    {
                        if (wx[i, j] != 0) continue;
                        float ha = ZoneH(i - 1, j), hb = ZoneH(i, j);
                        if (Mathf.Approximately(ha, hb)) continue;
                        wall.BeveledBox(new Vector3(i * Bay - BulkT * 0.5f, Mathf.Min(ha, hb), j * Bay),
                                        new Vector3(i * Bay + BulkT * 0.5f, Mathf.Max(ha, hb) + 0.20f, (j + 1) * Bay),
                                        Level0Builder.BevelWall, Level0Builder.UvWall);
                    }
                for (int i = 0; i < BX; i++)
                    for (int j = 1; j < BZ; j++)
                    {
                        if (wz[i, j] != 0) continue;
                        float ha = ZoneH(i, j - 1), hb = ZoneH(i, j);
                        if (Mathf.Approximately(ha, hb)) continue;
                        wall.BeveledBox(new Vector3(i * Bay, Mathf.Min(ha, hb), j * Bay - BulkT * 0.5f),
                                        new Vector3((i + 1) * Bay, Mathf.Max(ha, hb) + 0.20f, j * Bay + BulkT * 0.5f),
                                        Level0Builder.BevelWall, Level0Builder.UvWall);
                    }
            }

            // ========================================================== 天井
            // グリッド線の種別。0 = 素の T バー / 1 = 壁 / 2 = 垂れ壁 / 3 = 外周
            int EdgeKindX(int gi, int j, out float inset)
            {
                inset = WallT * 0.5f;
                if (gi <= 0 || gi >= NX) return 3;
                if (gi % CellsPerBay != 0) { inset = TBarW * 0.5f; return 0; }
                int bi = gi / CellsPerBay, bj = j / CellsPerBay;
                if (wx[bi, bj] != 0) return 1;
                if (!Mathf.Approximately(ZoneH(bi - 1, bj), ZoneH(bi, bj))) { inset = BulkT * 0.5f; return 2; }
                inset = TBarW * 0.5f;
                return 0;
            }

            int EdgeKindZ(int i, int gj, out float inset)
            {
                inset = WallT * 0.5f;
                if (gj <= 0 || gj >= NZ) return 3;
                if (gj % CellsPerBay != 0) { inset = TBarW * 0.5f; return 0; }
                int bi = i / CellsPerBay, bj = gj / CellsPerBay;
                if (wz[bi, bj] != 0) return 1;
                if (!Mathf.Approximately(ZoneH(bi, bj - 1), ZoneH(bi, bj))) { inset = BulkT * 0.5f; return 2; }
                inset = TBarW * 0.5f;
                return 0;
            }

            float PadX(int gi, int j)
            {
                int k = EdgeKindX(gi, j, out float ins);
                return k == 0 ? TBarW * 0.5f : ins + AngleW;
            }

            float PadZ(int i, int gj)
            {
                int k = EdgeKindZ(i, gj, out float ins);
                return k == 0 ? TBarW * 0.5f : ins + AngleW;
            }

            /// <summary>この天井セルは明かりが落ちた区画にあるか。</summary>
            bool DarkCell(int i, int j)
                => ZoneLight(Mathf.Clamp(i, 0, NX - 1) / CellsPerBay,
                             Mathf.Clamp(j, 0, NZ - 1) / CellsPerBay) == 2;

            void EmitCeiling(CeilMeshes cm, Result res)
            {
                var occupied = new HashSet<int>();
                var breakX = new HashSet<int>();   // このグリッド線はこの行で途切れる
                int hatches = 0, missing = 0, ducts = 0;

                for (int bx = 0; bx < BX; bx++)
                    for (int bz = 0; bz < BZ; bz++)
                    {
                        float H = ZoneH(bx, bz);
                        int lightMode = ZoneLight(bx, bz);
                        int c0 = bx * CellsPerBay, r0 = bz * CellsPerBay;

                        // --- 2x4 埋め込みトロファー（x 方向に 2 セル）---
                        int ci = c0 + 1, cj = r0 + 2;
                        occupied.Add(Key(ci, cj)); occupied.Add(Key(ci + 1, cj));
                        breakX.Add(Key(ci + 1, cj));
                        bool dead = lightMode == 2 || (lightMode == 1 && R < 0.55f)
                                    || (lightMode == 0 && R < 0.06f);
                        EmitTroffer(cm.F(lightMode == 2), dead ? cm.DiffOff : cm.DiffOn, ci, cj, H);
                        res.TrofferCenters.Add(new Vector3((ci + 1) * Cell, H, (cj + 0.5f) * Cell));
                        res.TrofferAlive.Add(!dead);

                        // --- 独立柱の足元セル ---
                        if (ColumnAtCell(c0, r0)) occupied.Add(Key(c0, r0));

                        // --- 空調吹出口（600 角）---
                        if ((bx * 3 + bz * 5) % 7 == 2)
                        {
                            int vi = c0 + 3, vj = r0 + 3;
                            occupied.Add(Key(vi, vj));
                            EmitVent(cm.Vents, (vi + 0.5f) * Cell, (vj + 0.5f) * Cell, H);
                            ducts++;
                        }
                        // --- 天井点検口 ---
                        if ((bx * 7 + bz * 3) % 13 == 0)
                        {
                            int hi = c0, hj = r0 + 1;
                            occupied.Add(Key(hi, hj));
                            EmitHatch(cm.F(lightMode == 2), hi, hj, H);
                            hatches++;
                        }
                        // --- 落ちたタイル ---
                        // 吊り天井が長く放置された建物には必ずある。ここだけ
                        // 天井裏の暗がりが覗き、下に本体が落ちている。
                        if ((bx * 5 + bz * 11) % 19 == 4)
                        {
                            int mi = c0 + 2, mj = r0;
                            occupied.Add(Key(mi, mj));
                            EmitMissingTile(cm.Plenum, cm.T(lightMode == 2), mi, mj, H);
                            stainPts.Add(new Vector3((mi + 0.5f) * Cell, 0f, (mj + 0.5f) * Cell));
                            missing++;
                        }
                    }

                // ------------------------------------------------------ タイル
                const int Atlas = 8;
                for (int i = 0; i < NX; i++)
                    for (int j = 0; j < NZ; j++)
                    {
                        if (occupied.Contains(Key(i, j))) continue;
                        float H = CellH(i, j);
                        float x0 = i * Cell + PadX(i, j), x1 = (i + 1) * Cell - PadX(i + 1, j);
                        float z0 = j * Cell + PadZ(i, j), z1 = (j + 1) * Cell - PadZ(i, j + 1);
                        if (x1 - x0 < 0.04f || z1 - z0 < 0.04f) continue;
                        EmitTile(cm.T(DarkCell(i, j)), x0, x1, z0, z1, H, Atlas);
                    }

                // ---------------------------------------------- グリッド（縦線）
                for (int gi = 0; gi <= NX; gi++)
                {
                    var kind = new int[NZ];
                    var inset = new float[NZ];
                    var brk = new bool[NZ];
                    for (int j = 0; j < NZ; j++)
                    {
                        kind[j] = EdgeKindX(gi, j, out inset[j]);
                        brk[j] = breakX.Contains(Key(gi, j));
                    }
                    int j0 = 0;
                    while (j0 < NZ)
                    {
                        if (kind[j0] == 0 && brk[j0]) { j0++; continue; }
                        int j1 = j0 + 1;
                        while (j1 < NZ && kind[j1] == kind[j0] && !brk[j1]
                               && Mathf.Approximately(inset[j1], inset[j0])
                               && Mathf.Approximately(CellH(gi, j1), CellH(gi, j0))
                               && Mathf.Approximately(CellH(gi - 1, j1), CellH(gi - 1, j0))
                               && DarkCell(gi, j1) == DarkCell(gi, j0)
                               && DarkCell(gi - 1, j1) == DarkCell(gi - 1, j0)) j1++;
                        float za = j0 * Cell, zb = j1 * Cell, x = gi * Cell;
                        if (kind[j0] == 0)
                        {
                            float H = CellH(gi, j0);
                            cm.B(DarkCell(gi, j0) && DarkCell(gi - 1, j0))
                              .Box(new Vector3(x - TBarW * 0.5f, H, za),
                                   new Vector3(x + TBarW * 0.5f, H + TBarH, zb), UvMetal);
                        }
                        else
                        {
                            float ins = inset[j0];
                            if (gi > 0)
                            {
                                float h = CellH(gi - 1, j0);
                                cm.B(DarkCell(gi - 1, j0))
                                  .Box(new Vector3(x - ins - AngleW, h, za),
                                       new Vector3(x - ins + AngleBite, h + AngleH, zb), UvMetal);
                            }
                            if (gi < NX)
                            {
                                float h = CellH(gi, j0);
                                cm.B(DarkCell(gi, j0))
                                  .Box(new Vector3(x + ins - AngleBite, h, za),
                                       new Vector3(x + ins + AngleW, h + AngleH, zb), UvMetal);
                            }
                        }
                        j0 = j1;
                    }
                }

                // ---------------------------------------------- グリッド（横線）
                for (int gj = 0; gj <= NZ; gj++)
                {
                    var kind = new int[NX];
                    var inset = new float[NX];
                    for (int i = 0; i < NX; i++) kind[i] = EdgeKindZ(i, gj, out inset[i]);
                    int i0 = 0;
                    while (i0 < NX)
                    {
                        int i1 = i0 + 1;
                        while (i1 < NX && kind[i1] == kind[i0]
                               && Mathf.Approximately(inset[i1], inset[i0])
                               && Mathf.Approximately(CellH(i1, gj), CellH(i0, gj))
                               && Mathf.Approximately(CellH(i1, gj - 1), CellH(i0, gj - 1))
                               && DarkCell(i1, gj) == DarkCell(i0, gj)
                               && DarkCell(i1, gj - 1) == DarkCell(i0, gj - 1)) i1++;
                        float xa = i0 * Cell, xb = i1 * Cell, z = gj * Cell;
                        if (kind[i0] == 0)
                        {
                            float H = CellH(i0, gj);
                            cm.B(DarkCell(i0, gj) && DarkCell(i0, gj - 1))
                              .Box(new Vector3(xa, H, z - TBarW * 0.5f),
                                   new Vector3(xb, H + TBarH, z + TBarW * 0.5f), UvMetal);
                        }
                        else
                        {
                            float ins = inset[i0];
                            if (gj > 0)
                            {
                                float h = CellH(i0, gj - 1);
                                cm.B(DarkCell(i0, gj - 1))
                                  .Box(new Vector3(xa, h, z - ins - AngleW),
                                       new Vector3(xb, h + AngleH, z - ins + AngleBite), UvMetal);
                            }
                            if (gj < NZ)
                            {
                                float h = CellH(i0, gj);
                                cm.B(DarkCell(i0, gj))
                                  .Box(new Vector3(xa, h, z + ins - AngleBite),
                                       new Vector3(xb, h + AngleH, z + ins + AngleW), UvMetal);
                            }
                        }
                        i0 = i1;
                    }
                }

                log.Add($"ceiling: {NX}x{NZ} cells, {hatches} access panels, {ducts} air diffusers, "
                        + $"{missing} fallen tiles,  reveal {TileReveal * 1000f:F1} mm");
            }

            /// <summary>
            /// 1 枚の吸音タイル。吊り天井のタイルは決して面一にならない。
            /// 個体ごとに座り高さがずれ、わずかに傾き、経年で中央が垂れる。
            /// ただしフランジに載っている以上、縁がグリッド面より下がることはない。
            /// </summary>
            void EmitTile(MeshBuilder tiles, float x0, float x1, float z0, float z1, float H, int atlas)
            {
                int ai = rnd.Next(atlas), aj = rnd.Next(atlas);
                float u0 = (ai + 0.015f) / atlas, u1 = (ai + 0.985f) / atlas;
                float v0 = (aj + 0.015f) / atlas, v1 = (aj + 0.985f) / atlas;

                float seat = H + TileReveal + R * 0.0012f;
                float sag = 0.0006f + R * 0.0020f;
                float[] tilt = { R * 0.0016f, R * 0.0016f, R * 0.0016f, R * 0.0016f };
                if (rnd.Next(100) < 3) tilt[rnd.Next(4)] += 0.012f;   // 掛け違えて浮いたタイル

                int orient = rnd.Next(8);

                Vector3 P(float fx, float fz)
                {
                    float t = Mathf.Lerp(Mathf.Lerp(tilt[0], tilt[1], fx), Mathf.Lerp(tilt[3], tilt[2], fx), fz);
                    float droop = Mathf.Sin(fx * Mathf.PI) * Mathf.Sin(fz * Mathf.PI) * sag;
                    return new Vector3(Mathf.Lerp(x0, x1, fx), seat + t - droop, Mathf.Lerp(z0, z1, fz));
                }
                Vector2 U(float fx, float fz)
                {
                    float a = fx, b = fz;
                    if ((orient & 4) != 0) { float t = a; a = b; b = t; }
                    if ((orient & 1) != 0) a = 1f - a;
                    if ((orient & 2) != 0) b = 1f - b;
                    return new Vector2(Mathf.Lerp(u0, u1, a), Mathf.Lerp(v0, v1, b));
                }

                const int Sub = 2;
                for (int sx = 0; sx < Sub; sx++)
                    for (int sz = 0; sz < Sub; sz++)
                    {
                        float fx0 = sx / (float)Sub, fx1 = (sx + 1) / (float)Sub;
                        float fz0 = sz / (float)Sub, fz1 = (sz + 1) / (float)Sub;
                        tiles.QuadUv(P(fx0, fz0), P(fx1, fz0), P(fx1, fz1), P(fx0, fz1),
                                     U(fx0, fz0), U(fx1, fz0), U(fx1, fz1), U(fx0, fz1));
                    }
            }

            /// <summary>
            /// 2x4 の埋め込み蛍光灯。実物は「枠がグリッド面に落ち、レンズは
            /// その枠の中にさらに 12mm 引っ込んでいる」。レンズをグリッド面より
            /// 下に出すと、光る板が天井から生えているように見える。
            /// </summary>
            void EmitTroffer(MeshBuilder fix, MeshBuilder lens, int ci, int cj, float H)
            {
                const float FrameW = 0.022f, FrameT = 0.006f, LensDrop = 0.012f;
                float x0 = ci * Cell + TBarW * 0.5f, x1 = (ci + 2) * Cell - TBarW * 0.5f;
                float z0 = cj * Cell + TBarW * 0.5f, z1 = (cj + 1) * Cell - TBarW * 0.5f;
                float yb = H - 0.001f, yt = H + FrameT;   // 枠はフランジに 1mm 被る

                fix.BeveledBox(new Vector3(x0, yb, z0), new Vector3(x1, yt, z0 + FrameW), Level0Builder.BevelFrame, UvMetal);
                fix.BeveledBox(new Vector3(x0, yb, z1 - FrameW), new Vector3(x1, yt, z1), Level0Builder.BevelFrame, UvMetal);
                fix.BeveledBox(new Vector3(x0, yb, z0 + FrameW), new Vector3(x0 + FrameW, yt, z1 - FrameW), Level0Builder.BevelFrame, UvMetal);
                fix.BeveledBox(new Vector3(x1 - FrameW, yb, z0 + FrameW), new Vector3(x1, yt, z1 - FrameW), Level0Builder.BevelFrame, UvMetal);

                lens.PlaneXZ(x0 + FrameW, x1 - FrameW, z0 + FrameW, z1 - FrameW, H + LensDrop, false, 1f);
            }

            void EmitHatch(MeshBuilder fix, int ci, int cj, float H)
            {
                float x0 = ci * Cell + TBarW * 0.5f, x1 = (ci + 1) * Cell - TBarW * 0.5f;
                float z0 = cj * Cell + TBarW * 0.5f, z1 = (cj + 1) * Cell - TBarW * 0.5f;
                fix.BeveledBox(new Vector3(x0, H + 0.002f, z0), new Vector3(x1, H + 0.018f, z1),
                               0.0012f, UvMetal);
                fix.BeveledBox(new Vector3((x0 + x1) * 0.5f - 0.028f, H - 0.004f, z0 + 0.012f),
                               new Vector3((x0 + x1) * 0.5f + 0.028f, H + 0.002f, z0 + 0.044f),
                               0.0008f, UvMetal);   // 掛け金
            }

            void EmitVent(MeshBuilder vents, float cx, float cz, float H)
            {
                for (int k = 0; k < 4; k++)
                {
                    float half = (0.560f - k * 0.120f) * 0.5f;
                    float y = H - 0.002f + k * 0.022f;
                    const float bar = 0.028f;
                    vents.BeveledBox(new Vector3(cx - half, y, cz - half),
                                     new Vector3(cx + half, y + 0.018f, cz - half + bar), 0.0015f, 0.4f);
                    vents.BeveledBox(new Vector3(cx - half, y, cz + half - bar),
                                     new Vector3(cx + half, y + 0.018f, cz + half), 0.0015f, 0.4f);
                    vents.BeveledBox(new Vector3(cx - half, y, cz - half + bar),
                                     new Vector3(cx - half + bar, y + 0.018f, cz + half - bar), 0.0015f, 0.4f);
                    vents.BeveledBox(new Vector3(cx + half - bar, y, cz - half + bar),
                                     new Vector3(cx + half, y + 0.018f, cz + half - bar), 0.0015f, 0.4f);
                }
            }

            /// <summary>抜けたタイル 1 枚。穴の中の天井裏と、床に落ちた本体。</summary>
            void EmitMissingTile(MeshBuilder plenum, MeshBuilder tiles, int ci, int cj, float H)
            {
                float x0 = ci * Cell + TBarW * 0.5f, x1 = (ci + 1) * Cell - TBarW * 0.5f;
                float z0 = cj * Cell + TBarW * 0.5f, z1 = (cj + 1) * Cell - TBarW * 0.5f;
                plenum.BoxInside(new Vector3(x0, H, z0), new Vector3(x1, H + 0.42f, z1), 1f);

                // 落ちた本体。角が欠け、少しだけ浮いて反っている。
                float cx = (x0 + x1) * 0.5f + (R - 0.5f) * 0.9f;
                float cz = (z0 + z1) * 0.5f + (R - 0.5f) * 0.9f;
                float yaw = R * Mathf.PI * 2f;
                float ca = Mathf.Cos(yaw), sa = Mathf.Sin(yaw);
                const float h = 0.288f;
                int ai = rnd.Next(8), aj = rnd.Next(8);
                float u0 = (ai + 0.02f) / 8f, u1 = (ai + 0.98f) / 8f;
                float v0 = (aj + 0.02f) / 8f, v1 = (aj + 0.98f) / 8f;

                Vector3 C(float sx, float sz, float y)
                    => new Vector3(cx + (sx * h * ca - sz * h * sa), y, cz + (sx * h * sa + sz * h * ca));

                float lift = 0.004f;
                var a = C(-1, -1, 0.016f); var b = C(1, -1, 0.019f);
                var c = C(1, 1, 0.016f + lift); var d = C(-1, 1, 0.014f);
                // 巻き順は Cross(b-a, d-a)。天井のタイルは -Y を向くのが正しいが、
                // 床に落ちた本体は +Y を向かせる必要がある。同じ順で張ると
                // 裏返り、上から見て消えて木口だけが線として残る。
                tiles.QuadUv(a, d, c, b,
                             new Vector2(u0, v0), new Vector2(u0, v1), new Vector2(u1, v1), new Vector2(u1, v0));
                // 木口。切り口なので狭い帯を貼る
                var a2 = C(-1, -1, 0f); var b2 = C(1, -1, 0f);
                var c2 = C(1, 1, 0f); var d2 = C(-1, 1, 0f);
                var e0 = new Vector2(u0, v0); var e1 = new Vector2(u0 + (u1 - u0) * 0.06f, v0);
                tiles.QuadUv(a2, b2, b, a, e0, e1, e1, e0);
                tiles.QuadUv(b2, c2, c, b, e0, e1, e1, e0);
                tiles.QuadUv(c2, d2, d, c, e0, e1, e1, e0);
                tiles.QuadUv(d2, a2, a, d, e0, e1, e1, e0);
            }

            // ========================================================== 設備
            void EmitServices(MeshBuilder chrome, MeshBuilder plastic)
            {
                for (int bx = 0; bx < BX; bx++)
                    for (int bz = 0; bz < BZ; bz++)
                    {
                        float H = ZoneH(bx, bz);
                        // スプリンクラー（消防法のおおむね 3.5m 間隔 = 1 ベイおき）
                        if ((bx + bz) % 2 == 0)
                        {
                            float x = bx * Bay + 0.90f, z = bz * Bay + 0.30f;
                            chrome.Cylinder(new Vector3(x, H - 0.055f, z), 0.0075f, 0.075f, 10, 0.5f);
                            chrome.Cylinder(new Vector3(x, H - 0.058f, z), 0.021f, 0.003f, 12, 0.5f);
                            chrome.Cylinder(new Vector3(x, H - 0.052f, z), 0.004f, 0.014f, 8, 0.5f);
                        }
                        // 煙感知器
                        if ((bx * 2 + bz) % 5 == 1)
                        {
                            float x = bx * Bay + 2.10f, z = bz * Bay + 0.30f;
                            plastic.Cylinder(new Vector3(x, H - 0.042f, z), 0.065f, 0.042f, 20, 0.5f);
                            plastic.Cylinder(new Vector3(x, H - 0.050f, z), 0.030f, 0.010f, 16, 0.5f);
                        }
                    }
            }

            /// <summary>
            /// 人が触った物。設備が「建物として要る物」なのに対し、こちらは
            /// 無いと空間が『誰も居ない』ではなく『誰も居たことがない』に見える。
            /// </summary>
            void EmitTraces(MeshBuilder plastic, MeshBuilder metal)
            {
                int switches = 0;
                foreach (var (pos, yaw, xWall) in doorPts)
                {
                    if (switches >= 6) break;
                    // 開口の脇、床上 1.25m。押す側（開口の中心から 1.05m 横）
                    if (xWall)
                    {
                        float z = pos.z + 0.98f;
                        Level0Builder.AddPlate(plastic, new Vector3(pos.x + WallT * 0.5f, 1.25f, z),
                                               Level0Builder.Axis.X, 1f, 0.086f, 0.122f);
                        for (int i = 0; i < 2; i++)
                            plastic.BeveledBox(new Vector3(pos.x + WallT * 0.5f + 0.006f, 1.25f - 0.030f, z - 0.036f + i * 0.040f),
                                               new Vector3(pos.x + WallT * 0.5f + 0.014f, 1.25f + 0.030f, z - 0.004f + i * 0.040f),
                                               0.0012f, 0.25f);
                    }
                    else
                    {
                        float x = pos.x + 0.98f;
                        Level0Builder.AddPlate(plastic, new Vector3(x, 1.25f, pos.z + WallT * 0.5f),
                                               Level0Builder.Axis.Z, 1f, 0.086f, 0.122f);
                        for (int i = 0; i < 2; i++)
                            plastic.BeveledBox(new Vector3(x - 0.036f + i * 0.040f, 1.25f - 0.030f, pos.z + WallT * 0.5f + 0.006f),
                                               new Vector3(x - 0.004f + i * 0.040f, 1.25f + 0.030f, pos.z + WallT * 0.5f + 0.014f),
                                               0.0012f, 0.25f);
                    }
                    switches++;
                }

                // コンセント・サーモスタット・配線モールは、十分に長い壁面にだけ置く。
                int outlets = 0, moulds = 0;
                for (int k = 0; k < faces.Count && (outlets < 14 || moulds < 3); k++)
                {
                    var f = faces[k];
                    if (f.Len < 2.2f) continue;
                    bool xFace = Mathf.Abs(Mathf.Abs(f.Yaw) - 90f) < 1f;
                    float dir = (f.Yaw == 90f || f.Yaw == 0f) ? 1f : -1f;

                    if (outlets < 14 && k % 3 == 0)
                    {
                        var p = f.Center + new Vector3(0f, 0.30f, 0f);
                        if (xFace) Level0Builder.AddOutlet(plastic, new Vector3(p.x + (dir > 0 ? 0.002f : -0.006f), p.y, p.z), true);
                        else Level0Builder.AddOutlet(plastic, new Vector3(p.x, p.y, p.z + (dir > 0 ? 0.002f : -0.006f)), false);
                        outlets++;
                    }
                    if (moulds < 3 && k % 7 == 2)
                    {
                        // 後から増設した LAN の配線モール。幅木の上を這わせる。
                        const float My = BaseH + 0.012f, Mh = 0.026f, Md = 0.020f;
                        float half = Mathf.Min(f.Len * 0.5f - 0.1f, 1.8f);
                        if (xFace)
                        {
                            float x0 = dir > 0 ? f.Center.x : f.Center.x - Md;
                            plastic.BeveledBox(new Vector3(x0, My, f.Center.z - half),
                                               new Vector3(x0 + Md, My + Mh, f.Center.z + half), 0.0015f, 0.5f);
                        }
                        else
                        {
                            float z0 = dir > 0 ? f.Center.z : f.Center.z - Md;
                            plastic.BeveledBox(new Vector3(f.Center.x - half, My, z0),
                                               new Vector3(f.Center.x + half, My + Mh, z0 + Md), 0.0015f, 0.5f);
                        }
                        moulds++;
                    }
                }

                // サーモスタット。最も長い壁面に 1 つ。
                int best = -1; float bestLen = 0f;
                for (int k = 0; k < faces.Count; k++) if (faces[k].Len > bestLen) { bestLen = faces[k].Len; best = k; }
                if (best >= 0)
                {
                    var f = faces[best];
                    bool xFace = Mathf.Abs(Mathf.Abs(f.Yaw) - 90f) < 1f;
                    float dir = (f.Yaw == 90f || f.Yaw == 0f) ? 1f : -1f;
                    var p = new Vector3(f.Center.x, 1.50f, f.Center.z);
                    Level0Builder.AddPlate(plastic, p, xFace ? Level0Builder.Axis.X : Level0Builder.Axis.Z,
                                           dir, 0.088f, 0.088f);
                    // 表示窓はプレート面より前に出さないと埋もれる
                    if (xFace)
                        plastic.BeveledBox(new Vector3(p.x + dir * 0.010f - (dir > 0 ? 0f : 0.0025f), p.y - 0.014f, p.z - 0.026f),
                                           new Vector3(p.x + dir * 0.010f + (dir > 0 ? 0.0025f : 0f), p.y + 0.010f, p.z + 0.026f),
                                           0.0008f, 0.25f);
                    else
                        plastic.BeveledBox(new Vector3(p.x - 0.026f, p.y - 0.014f, p.z + dir * 0.010f - (dir > 0 ? 0f : 0.0025f)),
                                           new Vector3(p.x + 0.026f, p.y + 0.010f, p.z + dir * 0.010f + (dir > 0 ? 0.0025f : 0f)),
                                           0.0008f, 0.25f);
                }
                log.Add($"traces: {switches} switches, {outlets} outlets, {moulds} conduits, "
                        + $"{doorPts.Count} thresholds");
            }

            // ======================================================== デカール
            void PlaceDecals(Result res)
            {
                // --- 外角の手擦れ。人が柱を回るときに手と肩が当たる高さ ---
                int n = 0;
                foreach (var c in columnPts)
                {
                    if (n >= 10) break;
                    res.Decals.Add(new DecalSpec("Scuff", new Vector3(c.x - ColHalf, 1.05f, c.z),
                                                 new Vector3(0f, 90f, 0f), new Vector2(0.42f, 0.95f)));
                    res.Decals.Add(new DecalSpec("Scuff", new Vector3(c.x, 1.05f, c.z - ColHalf),
                                                 new Vector3(0f, 0f, 0f), new Vector2(0.42f, 0.95f)));
                    n += 2;
                }
                // --- 開口の枠脇。ここは最も触られる ---
                int d = 0;
                foreach (var (pos, yaw, xWall) in doorPts)
                {
                    if (d >= 8) break;
                    var p = xWall
                        ? new Vector3(pos.x - WallT * 0.5f, 1.05f, pos.z + DoorW * 0.5f + 0.16f)
                        : new Vector3(pos.x + DoorW * 0.5f + 0.16f, 1.05f, pos.z - WallT * 0.5f);
                    res.Decals.Add(new DecalSpec("Scuff", p, new Vector3(0f, xWall ? 90f : 0f, 0f),
                                                 new Vector2(0.34f, 1.00f)));
                    d++;
                }
                // --- 天井から垂れた水の筋。タイルが落ちた所の近くの壁に出る ---
                int drips = 0;
                foreach (var s in stainPts)
                {
                    if (drips >= 6) break;
                    int best = -1; float bestD = 6f;
                    for (int k = 0; k < faces.Count; k++)
                    {
                        float dist = Vector3.Distance(new Vector3(faces[k].Center.x, 0f, faces[k].Center.z), s);
                        if (dist < bestD) { bestD = dist; best = k; }
                    }
                    if (best < 0) continue;
                    var f = faces[best];
                    res.Decals.Add(new DecalSpec("Drip", new Vector3(f.Center.x, 1.95f, f.Center.z),
                                                 new Vector3(0f, f.Yaw, 0f), new Vector2(1.00f, 1.50f)));
                    drips++;
                }
                // --- カーペットの通行帯。開口を通る動線に沿って ---
                int t = 0;
                foreach (var (pos, yaw, xWall) in doorPts)
                {
                    if (t >= 6) break;
                    res.Decals.Add(new DecalSpec("Traffic", new Vector3(pos.x, 0f, pos.z),
                                                 new Vector3(90f, xWall ? 90f : 0f, 0f),
                                                 new Vector2(1.35f, 5.20f)));
                    t++;
                }
                // --- 掃除機の筋と家具の脚跡。人の居た証拠 ---
                for (int k = 0; k < columnPts.Count && k < 3; k++)
                    res.Decals.Add(new DecalSpec("Vacuum", new Vector3(columnPts[k].x + 1.6f, 0f, columnPts[k].z + 1.6f),
                                                 new Vector3(90f, k * 90f, 0f), new Vector2(3.00f, 2.80f)));
                foreach (var (dx, dz) in new[] { (0f, 0f), (1.40f, 0f), (0f, 0.70f), (1.40f, 0.70f) })
                    res.Decals.Add(new DecalSpec("Dent", new Vector3(spawnBX * Bay + 0.6f + dx, 0f, spawnBZ * Bay + 0.5f + dz),
                                                 new Vector3(90f, 0f, 0f), new Vector2(0.11f, 0.11f)));
                log.Add($"decals: {res.Decals.Count} projectors");
            }

            // ============================================================ 視点
            int RunLen(int bx, int bz, int dx, int dz)
            {
                int n = 0, i = bx, j = bz;
                while (true)
                {
                    if (!Passable(i, j, dx, dz)) break;
                    i += dx; j += dz;
                    if (i < 0 || j < 0 || i >= BX || j >= BZ) break;
                    n++;
                }
                return n;
            }

            /// <summary>スポーンから (tx,tz) までの、ベイ単位の最短経路。</summary>
            List<(int, int)> PathTo(int tx, int tz)
            {
                var prev = new (int, int)[BX, BZ];
                var seen = new bool[BX, BZ];
                var q = new Queue<(int, int)>();
                q.Enqueue((spawnBX, spawnBZ));
                seen[spawnBX, spawnBZ] = true;
                prev[spawnBX, spawnBZ] = (-1, -1);
                while (q.Count > 0)
                {
                    var (i, j) = q.Dequeue();
                    foreach (var (di, dj) in Dirs)
                    {
                        int a = i + di, b = j + dj;
                        if (a < 0 || b < 0 || a >= BX || b >= BZ || seen[a, b]) continue;
                        if (!Passable(i, j, di, dj)) continue;
                        seen[a, b] = true; prev[a, b] = (i, j); q.Enqueue((a, b));
                    }
                }
                var path = new List<(int, int)>();
                if (!seen[tx, tz]) return path;
                var cur = (tx, tz);
                while (cur.Item1 >= 0) { path.Add(cur); cur = prev[cur.Item1, cur.Item2]; }
                path.Reverse();
                return path;
            }

            static float YawOf(int dx, int dz) => dx > 0 ? 90f : dx < 0 ? 270f : dz > 0 ? 0f : 180f;
            Vector3 BayCenter(int bx, int bz) => new Vector3(bx * Bay + Bay * 0.5f, 0f, bz * Bay + Bay * 0.5f);

            void PlaceShots(Result res)
            {
                res.Spawn = BayCenter(spawnBX, spawnBZ);

                // 最も長い見通しを探す
                int bestLen = -1, bx0 = spawnBX, bz0 = spawnBZ, bdx = 1, bdz = 0;
                int spawnLen = -1, sdx = 1, sdz = 0;
                for (int i = 0; i < BX; i++)
                    for (int j = 0; j < BZ; j++)
                        foreach (var (dx, dz) in Dirs)
                        {
                            int L = RunLen(i, j, dx, dz);
                            if (L > bestLen) { bestLen = L; bx0 = i; bz0 = j; bdx = dx; bdz = dz; }
                            if (i == spawnBX && j == spawnBZ && L > spawnLen) { spawnLen = L; sdx = dx; sdz = dz; }
                        }
                res.SpawnYaw = YawOf(sdx, sdz);

                // 暗黒区画と、その手前のベイ
                int darkBX = -1, darkBZ = -1;
                for (int i = 0; i < BX && darkBX < 0; i++)
                    for (int j = 0; j < BZ; j++)
                        if (ZoneLight(i, j) == 2) { darkBX = i; darkBZ = j; break; }
                if (darkBX < 0) { darkBX = BX - 2; darkBZ = BZ - 2; }
                var darkC = BayCenter(Mathf.Clamp(darkBX + 1, 0, BX - 1), Mathf.Clamp(darkBZ + 1, 0, BZ - 1));

                // 天井段差（垂れ壁）の位置
                Vector3 bulk = BayCenter(BX / 2, BZ / 2);
                float bulkYaw = 90f;
                bool found = false;
                for (int i = 1; i < BX && !found; i++)
                    for (int j = 0; j < BZ; j++)
                        if (wx[i, j] == 0 && !Mathf.Approximately(ZoneH(i - 1, j), ZoneH(i, j)))
                        {
                            bulk = new Vector3(i * Bay - 2.6f, 0f, j * Bay + Bay * 0.5f);
                            bulkYaw = 90f; found = true; break;
                        }
                if (!found)
                    for (int i = 0; i < BX && !found; i++)
                        for (int j = 1; j < BZ; j++)
                            if (wz[i, j] == 0 && !Mathf.Approximately(ZoneH(i, j - 1), ZoneH(i, j)))
                            {
                                bulk = new Vector3(i * Bay + Bay * 0.5f, 0f, j * Bay - 2.6f);
                                bulkYaw = 0f; found = true; break;
                            }

                // 開口を正面に見る位置
                Vector3 doorView = res.Spawn; float doorYaw = res.SpawnYaw;
                if (doorPts.Count > 0)
                {
                    var (p, yaw, xWall) = doorPts[doorPts.Count / 2];
                    doorView = xWall ? new Vector3(p.x - 3.0f, 0f, p.z) : new Vector3(p.x, 0f, p.z - 3.0f);
                    doorYaw = xWall ? 90f : 0f;
                }

                // 柱が奥へ続いて見える位置
                // 柱を見る視点。柱のベイから最も長く開けている向きへ後退して
                // 振り返る。柱の座標から決め打ちで下がると壁の中に入る。
                Vector3 colView = res.Spawn; float colYaw = res.SpawnYaw;
                if (columnPts.Count > 0)
                {
                    var c = columnPts[0];
                    int cbx = Mathf.Clamp((int)(c.x / Bay), 0, BX - 1);
                    int cbz = Mathf.Clamp((int)(c.z / Bay), 0, BZ - 1);
                    int bestRun = -1, rdx = -1, rdz = 0;
                    foreach (var (dx, dz) in Dirs)
                    {
                        int L = Mathf.Min(RunLen(cbx, cbz, dx, dz), 3);
                        if (L > bestRun) { bestRun = L; rdx = dx; rdz = dz; }
                    }
                    int vx = Mathf.Clamp(cbx + rdx * Mathf.Max(bestRun, 1), 0, BX - 1);
                    int vz = Mathf.Clamp(cbz + rdz * Mathf.Max(bestRun, 1), 0, BZ - 1);
                    colView = BayCenter(vx, vz);
                    var look = new Vector3(c.x, 0f, c.z) - colView;
                    colYaw = Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg;
                }

                res.Walk.Clear();
                foreach (var (wx_, wz_) in PathTo(darkBX, darkBZ)) res.Walk.Add(BayCenter(wx_, wz_));

                var depth = BayCenter(bx0, bz0);
                res.Shots.Add(new ShotSpec("01_hero", res.Spawn, res.SpawnYaw, 2f));
                res.Shots.Add(new ShotSpec("02_depth", depth, YawOf(bdx, bdz), 1f));
                res.Shots.Add(new ShotSpec("03_ceiling", depth, YawOf(bdx, bdz) + 18f, -30f));
                res.Shots.Add(new ShotSpec("04_doorway", doorView, doorYaw, 1f));
                res.Shots.Add(new ShotSpec("05_columns", colView, colYaw, 0f));
                res.Shots.Add(new ShotSpec("06_detail", res.Spawn + new Vector3(0.4f, 0f, 0.4f),
                                           res.SpawnYaw + 150f, 32f));
                res.Shots.Add(new ShotSpec("07_torch", darkC, YawOf(bdx, bdz), 0f, true));
                res.Shots.Add(new ShotSpec("08_dark_edge", darkC + new Vector3(-2.4f, 0f, -2.4f),
                                           YawOf(bdx, bdz) + 24f, -4f));
                res.Shots.Add(new ShotSpec("09_bulkhead", bulk, bulkYaw, -22f));

                log.Add($"views: spawn {res.Spawn} yaw {res.SpawnYaw:F0}, longest sight line {bestLen + 1} bays "
                        + $"({(bestLen + 1) * Bay:F1} m), dark zone bay ({darkBX},{darkBZ}), "
                        + $"walk test {res.Walk.Count} waypoints");
            }
        }
    }
}
