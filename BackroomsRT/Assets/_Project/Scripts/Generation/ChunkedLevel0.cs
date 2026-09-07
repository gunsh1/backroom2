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
                    if (dx * dx + dz * dz <= wr * wr) Ensure(c.x + dx, c.y + dz);
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
                for (int n = 0; n < Mathf.Max(1, ChunksPerFrame); n++)
                {
                    Vector2Int? next = null;
                    int best = int.MaxValue;
                    for (int dx = -LoadRadius; dx <= LoadRadius; dx++)
                        for (int dz = -LoadRadius; dz <= LoadRadius; dz++)
                        {
                            int d = dx * dx + dz * dz;
                            if (d > LoadRadius * LoadRadius || d >= best) continue;
                            var k = new Vector2Int(c.x + dx, c.y + dz);
                            if (_live.ContainsKey(Key(k.x, k.y))) continue;
                            best = d; next = k;
                        }
                    if (!next.HasValue) break;
                    Ensure(next.Value.x, next.Value.y);
                    made = true;
                }
                if (made && _culler != null) _culler.Rebuild();
                yield return wait;
            }
        }

        void Retire(Vector2Int c)
        {
            _wanted.Clear();
            foreach (var kv in _live)
            {
                int x = (int)(kv.Key >> 32), z = (int)(uint)kv.Key;
                int dx = x - c.x, dz = z - c.y;
                if (dx * dx + dz * dz > (LoadRadius + 1) * (LoadRadius + 1))
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
            int dx = cx - c.x, dz = cz - c.y;
            bool lights = dx * dx + dz * dz <= LightRadius * LightRadius;
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

            var ch = new Chunk { HasLights = withLights, Phase = Level0Layout.Hash(cx, cz, 7) % 1000 / 1000f };
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
            var trim = new MeshBuilder();
            int w0 = cx * WallUnitsPerChunk, w1 = w0 + WallUnitsPerChunk;
            int v0 = cz * WallUnitsPerChunk, v1 = v0 + WallUnitsPerChunk;
            EmitWalls(wall, bb, trim, w0, w1, v0, v1, S);
            Spawn(T, "Walls", wall, Materials.Wallpaper, true);
            Spawn(T, "Baseboards", bb, Materials.Baseboard, false);

            // ---- 天井 ----
            var tiles = new MeshBuilder();
            var bars = new MeshBuilder();
            var fixMesh = new MeshBuilder();
            var lensOn = new MeshBuilder();
            var lensOff = new MeshBuilder();
            var lensFlk = new MeshBuilder();

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
                    if (withLights && st != Level0Layout.Lamp.Dead)
                    {
                        var l = MakeLight(T, Level0Layout.FixtureCenter(i, j), i, j);
                        ch.Lights.Add(l);
                        if (st == Level0Layout.Lamp.Flicker)
                            ch.FlickerLights.Add(l.GetComponent<HDAdditionalLightData>());
                    }
                }

            for (int ci = a0; ci < a1; ci++)
                for (int cj = b0; cj < b1; cj++)
                {
                    if (isFix[ci - a0, cj - b0]) continue;
                    if (Level0Layout.Solid((ci + 0.5f) * Cell, (cj + 0.5f) * Cell, S)) continue;
                    tiles.PlaneXZ(ci * Cell + TW * 0.5f, (ci + 1) * Cell - TW * 0.5f,
                                  cj * Cell + TW * 0.5f, (cj + 1) * Cell - TW * 0.5f,
                                  H + RV, false, Cell * 8f);
                }

            // T バーは連続区間にまとめる。1 マスずつ箱にすると頂点が 20 倍になる。
            // 線はチャンクの左端と手前端だけが持つ (半開区間)。隣と重ならない。
            System.Func<int, int, bool> open = (ci, cj) =>
                !Level0Layout.Solid((ci + 0.5f) * Cell, (cj + 0.5f) * Cell, S);
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
            Spawn(T, "CeilingTrim", trim, Materials.Metal, false, false);
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
            LiveLights = 0;
            foreach (var kv in _live) LiveLights += kv.Value.Lights.Count;
            LiveLights += ch.Lights.Count;
            return ch;
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

        void EmitWalls(MeshBuilder wall, MeshBuilder bb, MeshBuilder trim,
                       int i0, int i1, int j0, int j1, int S)
        {
            const float P = Level0Layout.WallPitch, H = Level0Layout.CeilH;
            const float BT = Level0Builder.BaseT, BH = Level0Builder.BaseH;
            const float AW = Level0Builder.AngleW, AH = Level0Builder.AngleH, Bite = 0.004f;

            void Mass(float ax0, float az0, float ax1, float az1)
            {
                wall.BeveledBox(new Vector3(ax0, 0f, az0), new Vector3(ax1, H, az1),
                                Level0Builder.BevelWall, Level0Builder.UvWall);
                bb.Box(new Vector3(ax0 - BT, 0f, az0 - BT), new Vector3(ax1 + BT, BH, az0), Level0Builder.UvMetal);
                bb.Box(new Vector3(ax0 - BT, 0f, az1), new Vector3(ax1 + BT, BH, az1 + BT), Level0Builder.UvMetal);
                bb.Box(new Vector3(ax0 - BT, 0f, az0), new Vector3(ax0, BH, az1), Level0Builder.UvMetal);
                bb.Box(new Vector3(ax1, 0f, az0), new Vector3(ax1 + BT, BH, az1), Level0Builder.UvMetal);
                // 壁見切りアングル。木口を壁面ちょうどに置くと黒い筋が抜けるので食い込ませる
                trim.Box(new Vector3(ax0 - AW, H, az0 - AW), new Vector3(ax1 + AW, H + AH, az0 + Bite), Level0Builder.UvMetal);
                trim.Box(new Vector3(ax0 - AW, H, az1 - Bite), new Vector3(ax1 + AW, H + AH, az1 + AW), Level0Builder.UvMetal);
                trim.Box(new Vector3(ax0 - AW, H, az0 - Bite), new Vector3(ax0 + Bite, H + AH, az1 + Bite), Level0Builder.UvMetal);
                trim.Box(new Vector3(ax1 - Bite, H, az0 - Bite), new Vector3(ax1 + AW, H + AH, az1 + Bite), Level0Builder.UvMetal);
            }

            // x 一定の壁。同じ厚みが続く区間はひとまとめにする
            for (int i = i0; i < i1; i++)
            {
                int j = j0;
                while (j < j1)
                {
                    if (!Level0Layout.WallX(i, j, S)) { j++; continue; }
                    float t = Level0Layout.WallThickX(i, j, S) * 0.5f;
                    int s = j;
                    while (j < j1 && Level0Layout.WallX(i, j, S)
                           && Mathf.Approximately(Level0Layout.WallThickX(i, j, S) * 0.5f, t)) j++;
                    Mass(i * P - t, s * P, i * P + t, j * P);
                }
            }
            for (int j = j0; j < j1; j++)
            {
                int i = i0;
                while (i < i1)
                {
                    if (!Level0Layout.WallZ(i, j, S)) { i++; continue; }
                    float t = Level0Layout.WallThickZ(i, j, S) * 0.5f;
                    int s = i;
                    while (i < i1 && Level0Layout.WallZ(i, j, S)
                           && Mathf.Approximately(Level0Layout.WallThickZ(i, j, S) * 0.5f, t)) i++;
                    Mass(s * P, j * P - t, i * P, j * P + t);
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
