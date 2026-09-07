using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace Backrooms.Generation
{
    /// <summary>
    /// Level 0 を無限に伸ばす。プレイヤーの周りだけを作って、離れたら捨てる。
    ///
    /// 形は Level0Layout が座標から直接決めるので、ここは「どの範囲を今
    /// 実体にしておくか」だけを見る。戻ってきたときに同じ景色になるのは
    /// 覚えているからではなく、同じ式が同じ答えを返すから。
    ///
    /// 1 フレームに 1 チャンクずつしか作らない。14.4m 角のチャンクは
    /// 天井タイル 576 枚と器具 64 台ぶんのメッシュを組むので、
    /// まとめて作るとその場で 100ms 単位で止まる。
    /// </summary>
    [DisallowMultipleComponent]
    public class ChunkedLevel0 : MonoBehaviour
    {
        public const int BaysPerChunk = 8;
        public const float ChunkSize = Level0Layout.Bay * BaysPerChunk;   // 14.4 m
        public const int CellsPerChunk = BaysPerChunk * Level0Layout.CellsPerBay;
        /// <summary>壁の格子は 2 ベイ間隔なので、チャンク 1 つに 4 区画。</summary>
        public const int WallUnitsPerChunk = BaysPerChunk / 2;

        [Header("素材")]
        public MaterialSet Materials = new MaterialSet();

        [Header("生成")]
        public int Seed = 1996;
        [Tooltip("この距離のチャンクまで実体を作る")]
        public int LoadRadius = 4;
        [Tooltip("開始時にここまでは同期で作ってしまう。歩き出す前に読み込みを終わらせる")]
        public int WarmRadius = 4;
        [Tooltip("1 フレームに作るチャンク数。増やすと繋ぎ目が見えにくくなる代わりに引っ掛かる")]
        public int ChunksPerFrame = 2;
        [Tooltip("この距離のチャンクにだけ実際のライトを置く。外は自発光だけ")]
        public int LightRadius = 2;
        public Transform Viewer;

        [Header("器具")]
        public float Lumens = 700f;
        public Vector2 AreaSize = new Vector2(0.556f, 0.556f);
        public Color LightColor = new Color(1.0f, 0.974f, 0.735f);
        public float LightRange = 9f;
        [Tooltip("ディフューザの自発光。MaterialBuilder と揃えること")]
        public float DiffuserNits = 700f;

        public int LoadedChunks => _live.Count;
        public int LiveLights { get; private set; }

        class Chunk
        {
            public int Cx, Cz;
            public GameObject Root;
            public List<Light> Lights = new List<Light>();
            public Material FlickerMat;
            public List<HDAdditionalLightData> FlickerLights = new List<HDAdditionalLightData>();
            public bool HasLights;
            public float Phase;
        }

        readonly Dictionary<long, Chunk> _live = new Dictionary<long, Chunk>();
        readonly List<Vector2Int> _wanted = new List<Vector2Int>();
        Player.LightCuller _culler;
        bool _busy;
        int _lastCx = int.MinValue, _lastCz = int.MinValue;

        static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        /// <summary>
        /// チャンク格子上の距離。円ではなく正方形で測る。
        ///
        /// 円にすると斜め方向だけが早く途切れる。半径 4 の円なら (3,3) は
        /// 外なので、自分のチャンクの角に立ったとき未生成の角が 41m 先に
        /// 来てしまう。破棄はさらに近く 61m。遠クリップが 80m なので
        /// どちらも見える。しかも斜めにだけ起きるので「四角い継ぎ目」に見える。
        /// 正方形なら向きによらず (半径-1) x 14.4m を必ず確保できる。
        /// </summary>
        static int Cheb(int dx, int dz) => Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz));

        void Awake()
        {
            _culler = GetComponent<Player.LightCuller>();
        }

        void Start()
        {
            if (Viewer == null && Camera.main != null) Viewer = Camera.main.transform;
            // 開始時は読み込み半径ぶんをまとめて同期で作る。
            // 1 リングだけにすると床が無くて落ちることは防げるが、歩き出した
            // 直後に手前で世界が組み上がっていくのが見えてしまう。ここは
            // 「読み込み」なので止まってよい場所で、走ってから止まるより良い。
            var c = ChunkOf(Viewer != null ? Viewer.position : Vector3.zero);
            int wr = Mathf.Max(1, Mathf.Min(WarmRadius, LoadRadius));
            for (int dx = -wr; dx <= wr; dx++)
                for (int dz = -wr; dz <= wr; dz++)
                    Ensure(c.x + dx, c.y + dz);
            if (_culler != null) _culler.Rebuild();
            StartCoroutine(Stream());
        }

        public static Vector2Int ChunkOf(Vector3 p) => new Vector2Int(
            Mathf.FloorToInt(p.x / ChunkSize), Mathf.FloorToInt(p.z / ChunkSize));

        IEnumerator Stream()
        {
            var wait = new WaitForEndOfFrame();
            while (true)
            {
                if (Viewer == null) { yield return wait; continue; }
                var c = ChunkOf(Viewer.position);
                if (c.x != _lastCx || c.y != _lastCz)
                {
                    _lastCx = c.x; _lastCz = c.y;
                    Retire(c);
                }

                // 近い順に。1 フレームあたりの数だけ作ったら必ず返す。
                bool made = false;
                int budget = Mathf.Max(1, ChunksPerFrame);
                for (int n = 0; n < budget; n++)
                {
                    Vector2Int? next = null;
                    int best = int.MaxValue;
                    for (int dx = -LoadRadius; dx <= LoadRadius; dx++)
                        for (int dz = -LoadRadius; dz <= LoadRadius; dz++)
                        {
                            int d = dx * dx + dz * dz;              // 順序だけは近い順
                            if (d >= best) continue;
                            var k = new Vector2Int(c.x + dx, c.y + dz);
                            if (_live.ContainsKey(Key(k.x, k.y))) continue;
                            best = d; next = k;
                        }
                    if (!next.HasValue) break;
                    Ensure(next.Value.x, next.Value.y);
                    made = true;
                }

                // ライトの付け外し。作るのと同じ予算で、近い順に 1 つずつ直す。
                for (int n = 0; n < budget; n++)
                {
                    Chunk fix = null;
                    int best = int.MaxValue;
                    foreach (var kv in _live)
                    {
                        var ch = kv.Value;
                        bool want = Cheb(ch.Cx - c.x, ch.Cz - c.y) <= LightRadius;
                        if (ch.HasLights == want) continue;
                        int d = (ch.Cx - c.x) * (ch.Cx - c.x) + (ch.Cz - c.y) * (ch.Cz - c.y);
                        if (d >= best) continue;
                        best = d; fix = ch;
                    }
                    if (fix == null) break;
                    SetChunkLights(fix, !fix.HasLights);
                    made = true;
                }

                if (made && _culler != null)
                {
                    _culler.Rebuild();
                    int n2 = 0;
                    foreach (var kv in _live) n2 += kv.Value.Lights.Count;
                    LiveLights = n2;
                }
                yield return wait;
            }
        }

        void Retire(Vector2Int c)
        {
            _wanted.Clear();
            foreach (var kv in _live)
            {
                int x = (int)(kv.Key >> 32), z = (int)(uint)kv.Key;
                if (Cheb(x - c.x, z - c.y) > LoadRadius + 1)
                    _wanted.Add(new Vector2Int(x, z));
            }
            foreach (var k in _wanted)
            {
                var ch = _live[Key(k.x, k.y)];
                if (ch.FlickerMat != null) Destroy(ch.FlickerMat);
                Destroy(ch.Root);
                _live.Remove(Key(k.x, k.y));
            }
            if (_wanted.Count > 0 && _culler != null) _culler.Rebuild();
        }

        void Ensure(int cx, int cz)
        {
            long k = Key(cx, cz);
            if (_live.ContainsKey(k)) return;
            var c = ChunkOf(Viewer != null ? Viewer.position : Vector3.zero);
            bool lights = Cheb(cx - c.x, cz - c.y) <= LightRadius;
            _live[k] = Build(cx, cz, lights);
        }

        // --------------------------------------------------------------- 生成

        static GameObject Spawn(Transform parent, string name, MeshBuilder m, Material mat,
                                bool collider, bool shadows = true)
        {
            if (m == null || m.IsEmpty) return null;
            var mesh = m.ToMesh(name);
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = shadows ? UnityEngine.Rendering.ShadowCastingMode.On
                                           : UnityEngine.Rendering.ShadowCastingMode.Off;
            if (collider) go.AddComponent<MeshCollider>().sharedMesh = mesh;
            return go;
        }

        Chunk Build(int cx, int cz, bool withLights)
        {
            const float Cell = Level0Layout.Cell, Bay = Level0Layout.Bay;
            const float H = Level0Layout.CeilH;
            const float TW = Level0Builder.TBarW, RV = Level0Builder.TileReveal;
            int S = Seed;

            var ch = new Chunk { Cx = cx, Cz = cz, HasLights = false,
                                 Phase = Level0Layout.Hash(cx, cz, 7) % 1000 / 1000f };
            ch.Root = new GameObject($"Chunk_{cx}_{cz}");
            ch.Root.transform.SetParent(transform, false);
            var T = ch.Root.transform;

            int i0 = cx * BaysPerChunk, i1 = i0 + BaysPerChunk;
            int j0 = cz * BaysPerChunk, j1 = j0 + BaysPerChunk;
            int a0 = cx * CellsPerChunk, a1 = a0 + CellsPerChunk;
            int b0 = cz * CellsPerChunk, b1 = b0 + CellsPerChunk;
            float x0 = i0 * Bay, x1 = i1 * Bay, z0 = j0 * Bay, z1 = j1 * Bay;

            // ---- 床 ----
            var floor = new MeshBuilder();
            floor.PlaneXZ(x0, x1, z0, z1, 0f, true, Level0Builder.UvCarpet);
            Spawn(T, "Floor", floor, Materials.Carpet, true, false);

            // ---- 壁と柱 ----
            var wall = new MeshBuilder { UvScaleV = H };
            var bb = new MeshBuilder();
            int w0 = cx * WallUnitsPerChunk, w1 = w0 + WallUnitsPerChunk;
            int v0 = cz * WallUnitsPerChunk, v1 = v0 + WallUnitsPerChunk;
            EmitWalls(wall, bb, w0, w1, v0, v1, S);
            Spawn(T, "Walls", wall, Materials.Wallpaper, true);
            Spawn(T, "Baseboards", bb, Materials.Baseboard, false);

            // ---- 天井 ----
            var tiles = new MeshBuilder();
            var bars = new MeshBuilder();
            var fixMesh = new MeshBuilder();
            var lensOn = new MeshBuilder();
            var lensOff = new MeshBuilder();
            var lensFlk = new MeshBuilder();

            var plenum = new MeshBuilder();

            System.Func<int, int, bool> open = (ci, cj) =>
                !Level0Layout.Solid((ci + 0.5f) * Cell, (cj + 0.5f) * Cell, S);

            var isFix = new bool[CellsPerChunk, CellsPerChunk];
            for (int i = i0; i < i1; i++)
                for (int j = j0; j < j1; j++)
                {
                    if (!Level0Layout.HasFixture(i, j, S) || !Level0Layout.FixtureFree(i, j, S)) continue;
                    int ci = i * Level0Layout.CellsPerBay + 1, cj = j * Level0Layout.CellsPerBay + 1;
                    isFix[ci - a0, cj - b0] = true;
                    var st = Level0Layout.LampAt(i, j, S);
                    EmitTroffer(fixMesh, st == Level0Layout.Lamp.Lit ? lensOn
                                       : st == Level0Layout.Lamp.Flicker ? lensFlk : lensOff, ci, cj, H);
                }

            for (int ci = a0; ci < a1; ci++)
                for (int cj = b0; cj < b1; cj++)
                {
                    if (isFix[ci - a0, cj - b0]) continue;
                    if (!open(ci, cj)) continue;

                    // タイル 1 枚ぶんの場所を取る物は、タイルの代わりに入る。
                    // 壁に接したマスには入れない。器具も点検口も吹き出し口も
                    // 壁際には付けないし、そこだけタイルを壁まで伸ばす処理と
                    // 噛み合わずに 12mm の隙間が開く。
                    bool inner = open(ci - 1, cj) && open(ci + 1, cj)
                              && open(ci, cj - 1) && open(ci, cj + 1);
                    var kind = inner ? Level0Layout.CeilAt(ci, cj, S) : Level0Layout.Ceil.Tile;
                    if (kind == Level0Layout.Ceil.Missing)
                    { EmitMissingTile(plenum, tiles, ci, cj, H, S); continue; }
                    if (kind == Level0Layout.Ceil.Hatch)
                    { EmitHatch(fixMesh, ci, cj, H); continue; }
                    if (kind == Level0Layout.Ceil.Vent)
                    { EmitVent(fixMesh, plenum, ci, cj, H); continue; }

                    // 壁に接する辺には T バーが立たない。見付ぶんの隙間を残すと
                    // そこだけ天井裏が細い黒線になって覗く。壁の中まで伸ばして塞ぐ。
                    const float Lap = 0.030f;
                    float ex0 = open(ci - 1, cj) ? TW * 0.5f : -Lap;
                    float ex1 = open(ci + 1, cj) ? TW * 0.5f : -Lap;
                    float ez0 = open(ci, cj - 1) ? TW * 0.5f : -Lap;
                    float ez1 = open(ci, cj + 1) ? TW * 0.5f : -Lap;
                    tiles.PlaneXZ(ci * Cell + ex0, (ci + 1) * Cell - ex1,
                                  cj * Cell + ez0, (cj + 1) * Cell - ez1,
                                  H + RV, false, Cell * 8f);
                }

            // T バーは連続区間にまとめる。1 マスずつ箱にすると頂点が 20 倍になる。
            // 線はチャンクの左端と手前端だけが持つ (半開区間)。隣と重ならない。
            for (int ci = a0; ci < a1; ci++)
            {
                int cj = b0;
                while (cj < b1)
                {
                    if (!(open(ci - 1, cj) && open(ci, cj))) { cj++; continue; }
                    int s = cj;
                    while (cj < b1 && open(ci - 1, cj) && open(ci, cj)) cj++;
                    bars.Box(new Vector3(ci * Cell - TW * 0.5f, H, s * Cell),
                             new Vector3(ci * Cell + TW * 0.5f, H + Level0Builder.TBarH, cj * Cell),
                             Level0Builder.UvMetal);
                }
            }
            for (int cj = b0; cj < b1; cj++)
            {
                int ci = a0;
                while (ci < a1)
                {
                    if (!(open(ci, cj - 1) && open(ci, cj))) { ci++; continue; }
                    int s = ci;
                    while (ci < a1 && open(ci, cj - 1) && open(ci, cj)) ci++;
                    bars.Box(new Vector3(s * Cell, H, cj * Cell - TW * 0.5f),
                             new Vector3(ci * Cell, H + Level0Builder.TBarH, cj * Cell + TW * 0.5f),
                             Level0Builder.UvMetal);
                }
            }
            Spawn(T, "CeilingTiles", tiles, Materials.CeilingTile, false, false);
            Spawn(T, "CeilingGrid", bars, Materials.Metal, false, false);
            Spawn(T, "Plenum", plenum, Materials.Plenum, false, false);
            Spawn(T, "TrofferFrames", fixMesh, Materials.Metal, false, false);
            Spawn(T, "DiffusersOn", lensOn, Materials.DiffuserOn, false, false);
            Spawn(T, "DiffusersOff", lensOff, Materials.DiffuserOff, false, false);
            if (!lensFlk.IsEmpty)
            {
                // ちらつく器具はチャンクごとに 1 つのマテリアルを持たせる。
                // 全部で共有すると世界中の蛍光灯が一斉に瞬いて嘘になり、
                // 1 台ずつ持たせるとマテリアルが数千になる。同じチャンク =
                // 同じ回路、と考えると 14.4m まとめて瞬くのは自然。
                ch.FlickerMat = new Material(Materials.DiffuserOn);
                Spawn(T, "DiffusersFlicker", lensFlk, ch.FlickerMat, false, false);
            }
            if (withLights) SetChunkLights(ch, true);
            return ch;
        }

        /// <summary>
        /// チャンクのエリアライトを付ける／外す。
        ///
        /// 以前は作る瞬間の視点からの距離で決め打ちしていた。歩いて近づいても
        /// 後から足されないので、開始時に遠かったチャンクは、その中に立っても
        /// 直接光が 1 台も無いままだった。自発光だけは残るので気付きにくいが、
        /// 床と壁への当たり方がチャンクごとに変わる。視点が動いたら直す。
        /// </summary>
        void SetChunkLights(Chunk ch, bool want)
        {
            if (ch.HasLights == want || ch.Root == null) return;
            ch.HasLights = want;
            if (!want)
            {
                for (int i = 0; i < ch.Lights.Count; i++)
                    if (ch.Lights[i] != null) Destroy(ch.Lights[i].gameObject);
                ch.Lights.Clear();
                ch.FlickerLights.Clear();
                return;
            }
            int S = Seed;
            int i0 = ch.Cx * BaysPerChunk, i1 = i0 + BaysPerChunk;
            int j0 = ch.Cz * BaysPerChunk, j1 = j0 + BaysPerChunk;
            for (int i = i0; i < i1; i++)
                for (int j = j0; j < j1; j++)
                {
                    if (!Level0Layout.HasFixture(i, j, S) || !Level0Layout.FixtureFree(i, j, S)) continue;
                    var st = Level0Layout.LampAt(i, j, S);
                    if (st == Level0Layout.Lamp.Dead) continue;
                    var l = MakeLight(ch.Root.transform, Level0Layout.FixtureCenter(i, j), i, j);
                    ch.Lights.Add(l);
                    if (st == Level0Layout.Lamp.Flicker)
                        ch.FlickerLights.Add(l.GetComponent<HDAdditionalLightData>());
                }
        }

        Light MakeLight(Transform parent, Vector3 p, int i, int j)
        {
            var go = new GameObject($"L_{i}_{j}");
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(p.x, p.y - 0.012f, p.z);
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            var light = go.AddComponent<Light>();
            var hd = go.AddComponent<HDAdditionalLightData>();
            try
            {
                hd.SetLightTypeAndShape(HDLightTypeAndShape.RectangleArea);
                hd.SetAreaLightSize(AreaSize);
                hd.SetIntensity(Lumens, UnityEngine.Rendering.LightUnit.Lumen);
                hd.range = LightRange;
                hd.affectsVolumetric = true;
            }
            catch { }
            // 蛍光灯は黒体放射ではない。水銀の輝線が強く出るので色温度では表せない。
            light.useColorTemperature = false;
            light.color = LightColor;
            light.shadows = LightShadows.None;
            light.enabled = false;      // 点けるかどうかは LightCuller が決める
            return light;
        }

        /// <summary>
        /// 壁の上端は天井面より上まで伸ばす。
        ///
        /// 壁を天井とちょうど同じ高さで止め、そこに壁見切りアングルを回して
        /// いた。実物にある部品だが、下を向いた 24mm の水平脚には器具の光が
        /// 一切当たらない。エリアライトは全て下向きなので、天井まわりで
        /// 上を向いていない面はどこにも光源を持たない。結果として部屋の
        /// 四周に暗い帯が回り、天井と壁の境が線で縁取られていた。
        /// 壁をタイル面より上まで伸ばしてタイルを壁に差し込めば、
        /// 見えるのは壁紙と天井タイルだけになって帯が消える。
        /// </summary>
        public const float WallOverlap = 0.050f;

        void EmitWalls(MeshBuilder wall, MeshBuilder bb, int i0, int i1, int j0, int j1, int S)
        {
            const float P = Level0Layout.WallPitch;
            const float H = Level0Layout.CeilH + WallOverlap;
            const float BT = Level0Builder.BaseT, BH = Level0Builder.BaseH;

            void Mass(float ax0, float az0, float ax1, float az1)
            {
                wall.BeveledBox(new Vector3(ax0, 0f, az0), new Vector3(ax1, H, az1),
                                Level0Builder.BevelWall, Level0Builder.UvWall);
                bb.Box(new Vector3(ax0 - BT, 0f, az0 - BT), new Vector3(ax1 + BT, BH, az0), Level0Builder.UvMetal);
                bb.Box(new Vector3(ax0 - BT, 0f, az1), new Vector3(ax1 + BT, BH, az1 + BT), Level0Builder.UvMetal);
                bb.Box(new Vector3(ax0 - BT, 0f, az0), new Vector3(ax0, BH, az1), Level0Builder.UvMetal);
                bb.Box(new Vector3(ax1, 0f, az0), new Vector3(ax1 + BT, BH, az1), Level0Builder.UvMetal);
            }

            // x 一定の壁。同じ厚みが続く区間はひとまとめにする
            for (int i = i0; i < i1; i++)
            {
                int j = j0;
                while (j < j1)
                {
                    if (!Level0Layout.WallX(i, j, S)) { j++; continue; }
                    float t = Level0Layout.WallThickX(i, j, S) * 0.5f;
                    float c = Level0Layout.WallCenterX(i, j, S);
                    int s = j;
                    // 厚みだけでなく中心も揃っている区間だけをまとめる。
                    // 厚い壁は 0.3m 振ってあるので、振り方が違えば別の壁。
                    while (j < j1 && Level0Layout.WallX(i, j, S)
                           && Mathf.Approximately(Level0Layout.WallThickX(i, j, S) * 0.5f, t)
                           && Mathf.Approximately(Level0Layout.WallCenterX(i, j, S), c)) j++;
                    Mass(c - t, s * P, c + t, j * P);
                }
            }
            for (int j = j0; j < j1; j++)
            {
                int i = i0;
                while (i < i1)
                {
                    if (!Level0Layout.WallZ(i, j, S)) { i++; continue; }
                    float t = Level0Layout.WallThickZ(i, j, S) * 0.5f;
                    float c = Level0Layout.WallCenterZ(i, j, S);
                    int s = i;
                    while (i < i1 && Level0Layout.WallZ(i, j, S)
                           && Mathf.Approximately(Level0Layout.WallThickZ(i, j, S) * 0.5f, t)
                           && Mathf.Approximately(Level0Layout.WallCenterZ(i, j, S), c)) i++;
                    Mass(s * P, c - t, i * P, c + t);
                }
            }
            for (int i = i0; i < i1; i++)
                for (int j = j0; j < j1; j++)
                    if (Level0Layout.Column(i, j, S))
                        Mass(i * P - Level0Layout.ColHalf, j * P - Level0Layout.ColHalf,
                             i * P + Level0Layout.ColHalf, j * P + Level0Layout.ColHalf);
        }

        /// <summary>埋め込み 2x2 (600 角)。枠がグリッド面まで落ち、レンズはさらに 12mm 引っ込む。</summary>
        static void EmitTroffer(MeshBuilder fix, MeshBuilder lens, int ci, int cj, float H)
        {
            const float FrameW = 0.022f, FrameT = 0.006f, LensDrop = 0.012f;
            const float TW = Level0Builder.TBarW, Cell = Level0Layout.Cell;
            float x0 = ci * Cell + TW * 0.5f, x1 = (ci + 1) * Cell - TW * 0.5f;
            float z0 = cj * Cell + TW * 0.5f, z1 = (cj + 1) * Cell - TW * 0.5f;
            float yb = H - 0.001f, yt = H + FrameT;
            fix.Box(new Vector3(x0, yb, z0), new Vector3(x1, yt, z0 + FrameW), Level0Builder.UvMetal);
            fix.Box(new Vector3(x0, yb, z1 - FrameW), new Vector3(x1, yt, z1), Level0Builder.UvMetal);
            fix.Box(new Vector3(x0, yb, z0 + FrameW), new Vector3(x0 + FrameW, yt, z1 - FrameW), Level0Builder.UvMetal);
            fix.Box(new Vector3(x1 - FrameW, yb, z0 + FrameW), new Vector3(x1, yt, z1 - FrameW), Level0Builder.UvMetal);
            lens.PlaneXZ(x0 + FrameW, x1 - FrameW, z0 + FrameW, z1 - FrameW, H + LensDrop, false, 1f);
        }

        // -------------------------------------------------------- 天井の変化

        /// <summary>点検口。タイル 1 枚と入れ替わる鋼板の蓋。掛け金が下に出る。</summary>
        static void EmitHatch(MeshBuilder fix, int ci, int cj, float H)
        {
            const float TW = Level0Builder.TBarW, Cell = Level0Layout.Cell;
            float x0 = ci * Cell + TW * 0.5f, x1 = (ci + 1) * Cell - TW * 0.5f;
            float z0 = cj * Cell + TW * 0.5f, z1 = (cj + 1) * Cell - TW * 0.5f;
            fix.Box(new Vector3(x0, H + 0.001f, z0), new Vector3(x1, H + 0.017f, z1),
                    Level0Builder.UvMetal);
            float mx = (x0 + x1) * 0.5f;
            fix.Box(new Vector3(mx - 0.028f, H - 0.005f, z0 + 0.012f),
                    new Vector3(mx + 0.028f, H + 0.001f, z0 + 0.044f), Level0Builder.UvMetal);
        }

        /// <summary>
        /// 空調の吹き出し口。段付きのアルミ製で、タイル 1 枚と入れ替わる。
        /// 羽根の間から奥が見えるので、裏にダクトぶんの箱を置く。
        /// 何も置かないと羽根の隙間から世界の外が抜ける。
        /// </summary>
        static void EmitVent(MeshBuilder fix, MeshBuilder duct, int ci, int cj, float H)
        {
            const float TW = Level0Builder.TBarW, Cell = Level0Layout.Cell;
            float x0 = ci * Cell + TW * 0.5f, x1 = (ci + 1) * Cell - TW * 0.5f;
            float z0 = cj * Cell + TW * 0.5f, z1 = (cj + 1) * Cell - TW * 0.5f;
            duct.BoxInside(new Vector3(x0, H, z0), new Vector3(x1, H + 0.30f, z1), 1f);

            float cx = (x0 + x1) * 0.5f, cz = (z0 + z1) * 0.5f;
            for (int k = 0; k < 4; k++)
            {
                float half = (0.560f - k * 0.120f) * 0.5f;
                float y = H - 0.002f + k * 0.022f;
                const float bar = 0.028f;
                fix.Box(new Vector3(cx - half, y, cz - half),
                        new Vector3(cx + half, y + 0.018f, cz - half + bar), 0.4f);
                fix.Box(new Vector3(cx - half, y, cz + half - bar),
                        new Vector3(cx + half, y + 0.018f, cz + half), 0.4f);
                fix.Box(new Vector3(cx - half, y, cz - half + bar),
                        new Vector3(cx - half + bar, y + 0.018f, cz + half - bar), 0.4f);
                fix.Box(new Vector3(cx + half - bar, y, cz - half + bar),
                        new Vector3(cx + half, y + 0.018f, cz + half - bar), 0.4f);
            }
        }

        /// <summary>
        /// 抜けたタイル 1 枚。穴の中の天井裏と、床に落ちた本体。
        ///
        /// 数を出す物ではない。1 枚あるだけで「この階は誰も手入れしていない」
        /// と分かる。落ちた本体を置かずに穴だけ開けると、天井に四角い黒が
        /// 描いてあるようにしか見えない。
        /// </summary>
        static void EmitMissingTile(MeshBuilder plenum, MeshBuilder tiles,
                                    int ci, int cj, float H, int seed)
        {
            const float TW = Level0Builder.TBarW, Cell = Level0Layout.Cell;
            float x0 = ci * Cell + TW * 0.5f, x1 = (ci + 1) * Cell - TW * 0.5f;
            float z0 = cj * Cell + TW * 0.5f, z1 = (cj + 1) * Cell - TW * 0.5f;
            plenum.BoxInside(new Vector3(x0, H, z0), new Vector3(x1, H + 0.42f, z1), 1f);

            // 落ちた本体。位置も向きも座標から決める。乱数を持たせると
            // 同じ場所に戻ってきたとき別の所に落ちていることになる。
            float r0 = (Level0Layout.Hash(ci, cj, seed * 7 + 71) & 0xFFFF) / 65535f;
            float r1 = (Level0Layout.Hash(ci, cj, seed * 7 + 72) & 0xFFFF) / 65535f;
            float r2 = (Level0Layout.Hash(ci, cj, seed * 7 + 73) & 0xFFFF) / 65535f;
            int ai = (int)(Level0Layout.Hash(ci, cj, seed * 7 + 74) % 8);
            int aj = (int)(Level0Layout.Hash(ci, cj, seed * 7 + 75) % 8);

            float cx = (x0 + x1) * 0.5f + (r0 - 0.5f) * 0.9f;
            float cz = (z0 + z1) * 0.5f + (r1 - 0.5f) * 0.9f;
            float yaw = r2 * Mathf.PI * 2f;
            float ca = Mathf.Cos(yaw), sa = Mathf.Sin(yaw);
            const float h = 0.288f;
            float u0 = (ai + 0.02f) / 8f, u1 = (ai + 0.98f) / 8f;
            float v0 = (aj + 0.02f) / 8f, v1 = (aj + 0.98f) / 8f;

            Vector3 C(float sx, float sz, float y)
                => new Vector3(cx + (sx * h * ca - sz * h * sa), y, cz + (sx * h * sa + sz * h * ca));

            // 天井のタイルは -Y を向くのが正しいが、床に落ちた本体は +Y。
            // 同じ巻き順で張ると裏返り、上から見ると消えて木口だけが残る。
            var a = C(-1, -1, 0.016f); var b = C(1, -1, 0.019f);
            var c = C(1, 1, 0.020f); var d = C(-1, 1, 0.014f);
            tiles.QuadUv(a, d, c, b,
                         new Vector2(u0, v0), new Vector2(u0, v1),
                         new Vector2(u1, v1), new Vector2(u1, v0));
            var a2 = C(-1, -1, 0f); var b2 = C(1, -1, 0f);
            var c2 = C(1, 1, 0f); var d2 = C(-1, 1, 0f);
            var e0 = new Vector2(u0, v0); var e1 = new Vector2(u0 + (u1 - u0) * 0.06f, v0);
            tiles.QuadUv(a2, b2, b, a, e0, e1, e1, e0);
            tiles.QuadUv(b2, c2, c, b, e0, e1, e1, e0);
            tiles.QuadUv(c2, d2, d, c, e0, e1, e1, e0);
            tiles.QuadUv(d2, a2, a, d, e0, e1, e1, e0);
        }

        // ------------------------------------------------------------ ちらつき

        static readonly int EmissiveColor = Shader.PropertyToID("_EmissiveColor");

        void LateUpdate()
        {
            foreach (var kv in _live)
            {
                var ch = kv.Value;
                if (ch.FlickerMat == null && ch.FlickerLights.Count == 0) continue;
                // 切れかけの蛍光管。ほとんど点いていて、時々沈む。
                // 完全な明滅にすると 1 秒で嘘に見えるので、8 割の時間は点灯のまま。
                float t = Time.time * 7.3f + ch.Phase * 100f;
                float n = Mathf.PerlinNoise(t, ch.Phase * 37f);
                float k = n < 0.34f ? Mathf.Clamp01(n * 2.1f) : 1f;
                if (Random.value < 0.02f) k *= 0.25f;
                if (ch.FlickerMat != null)
                    ch.FlickerMat.SetColor(EmissiveColor, LightColor * (DiffuserNits * k));
                for (int i = 0; i < ch.FlickerLights.Count; i++)
                {
                    var hd = ch.FlickerLights[i];
                    if (hd != null) { try { hd.SetIntensity(Lumens * k, UnityEngine.Rendering.LightUnit.Lumen); } catch { } }
                }
            }
        }
    }
}
