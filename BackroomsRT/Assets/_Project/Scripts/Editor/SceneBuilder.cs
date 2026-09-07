using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Backrooms.Capture;
using Backrooms.Generation;
using Backrooms.Player;
using Backrooms.Post;

namespace Backrooms.EditorTools
{
    /// <summary>
    /// フォトリアル版 Level 0 のシーンを丸ごと生成する。
    /// 目標は「現実と見まがう画」なので、色作りの演出は一切入れず、
    /// 実在のカメラとレンズが持つ性質だけを再現する。
    /// </summary>
    public static class SceneBuilder
    {
        const string ScenePath = "Assets/_Project/Scenes/Level0.unity";
        const string ProfilePath = "Assets/_Project/Settings/Level0_Volume.asset";
        const string MatRoot = "Assets/_Project/Art/Materials";
        static readonly List<string> Log = new List<string>();

        // 生成器ごとに変わる撮影条件。既定は M2 の乱数生成シーン。
        // 複製シーンでは参照画像から逆算した値に差し替える。
        static float _fovV = 47f;          // 垂直画角
        static float _eyeH = 1.68f;        // 視点高
        static Vector2 _areaSize = new Vector2(1.16f, 0.56f);  // 2x4 トロファーの発光面
        static float _lumens = 3200f;
        static int _maxActiveLights = 28;
        static float _lightRadius = 17f;
        static float _expComp = 1.98f;      // 自動露出の補正 (EV)
        static float _lensK1 = 0f;          // 書き出し時に掛ける放射歪み
        static float _blurSigma = 0f;       // MTF に相当するガウス幅 (px)
        static float _glare = 0f;           // ベーリンググレアの混合率
        static float _glareSigma = 0f;
        static Color _mediumGain = Color.white;
        static float _lookAhead = 0f;
        static bool _vhs = false;        // VHS ポストを載せるか
        static float _aspect = 16f / 9f;  // 記録媒体の縦横比
        static int _outW = 1920, _outH = 1080;

        [MenuItem("Backrooms/3. Build Scene")]
        public static void Build()
        {
            _fovV = 47f; _eyeH = 1.68f;
            _areaSize = new Vector2(1.16f, 0.56f); _lumens = 3200f;
            _maxActiveLights = 28; _lightRadius = 17f; _lookAhead = 0f;
            _expComp = 1.98f;
            _lensK1 = 0f; _blurSigma = 0f; _glare = 0f; _glareSigma = 0f;
            _mediumGain = Color.white;
            _vhs = false; _aspect = 16f / 9f; _outW = 1920; _outH = 1080;
            BuildWith(null);
        }

        /// <summary>
        /// 参照画像と同じ構造を組んだ複製シーン。カメラも器具も参照から
        /// 逆算した値に差し替える (tools/reverse-geometry.py)。
        ///
        /// 器具は 600 角の 2x2 が 1.8m 格子。1 台あたり 3.24 平米を受け持つので
        /// 2 灯 32W の U 字管が 6 割の出力、と置くと 1600 lm。床面で 490 lx 相当。
        /// 実在のオフィスの設計照度 300-500 lx に収まる。
        /// </summary>
        [MenuItem("Backrooms/3b. Build Replica Scene")]
        public static void BuildReplicaScene()
        {
            _fovV = Level0Replica.FovV;
            _eyeH = Level0Replica.EyeH;
            _areaSize = new Vector2(0.556f, 0.556f);
            _lumens = 1600f;
            // 参照は 35m 先まで照らされている。近い 28 台だけでは床が届かない。
            // HDRP の maxAreaLightsOnScreen を 512 に上げた。80 台のときは
            // 25m より奥の器具が全部落ちて中景の壁が参照の半分の明るさ、
            // 250 台でも 12m 以遠の床が -0.12 暗かった。参照の床は 30m 先まで
            // ほぼ一定の明るさなので、直接光がそこまで届いている必要がある。
            _maxActiveLights = 500; _lightRadius = 52f; _lookAhead = 16f;
            // 参照より明るかったので下げた。-0.50 EV では中央値が 0.483 (参照 0.515)
            // まで落ちたので -0.40 EV に戻す。
            _expComp = 1.98f - 0.40f;
            // レンズと記録系は CaptureMedium が持つ。値は
            // tools/fit-capture-medium.py が参照画像から実測したもの。
            _lensK1 = Level0Replica.LensK1;
            _blurSigma = Level0Replica.BlurSigma;
            _glare = Level0Replica.Glare;
            _glareSigma = Level0Replica.GlareSigma;
            _mediumGain = Level0Replica.MediumGain;
            _vhs = false; _aspect = 16f / 9f; _outW = 1920; _outH = 1080;
            BuildWith(Level0Replica.Build);
        }

        /// <summary>
        /// 無限に歩ける Level 0。地形は持たず、Level0Layout の規則から
        /// プレイヤーの周りだけを ChunkedLevel0 がその場で組み立てる。
        ///
        /// 器具は全部が点いているわけではない。参照の Level 0 も、
        /// 一帯まるごと電気が来ていない区画と、1 台ずつ切れた蛍光灯が混ざっている。
        /// 最後に VHS を通す。実機のテープが記録できない細部は、そもそも写らない。
        /// </summary>
        [MenuItem("Backrooms/3c. Build Infinite Scene")]
        public static void BuildInfiniteScene()
        {
            // 記録媒体が 4:3 になったので、垂直画角のほうを広げる。
            // 参照の水平画角 66.26 度を 4:3 で保つと垂直は 52.17 度。
            // ここで 40.3 度のままにすると、単に両脇を切り落としただけの
            // 「寄った絵」になり、空間の広がりが読めなくなる。
            _aspect = 4f / 3f; _outW = 1440; _outH = 1080;
            _fovV = 52.17f;
            _eyeH = Level0Replica.EyeH;
            _areaSize = new Vector2(0.556f, 0.556f);
            _lumens = 700f;
            _maxActiveLights = 300; _lightRadius = 42f; _lookAhead = 12f;
            _expComp = 1.98f - 0.40f;
            // 記録系は VHS ポストが持つので、書き出し側の媒体は掛けない
            _lensK1 = 0f; _blurSigma = 0f; _glare = 0f; _glareSigma = 0f;
            _mediumGain = Color.white;
            _vhs = true;
            EnsureVhsRegistered();
            BuildWith(InfiniteWorld);
        }

        /// <summary>
        /// VHS のカスタムポストを HDRP に登録する。
        ///
        /// HDRP はこの一覧を GraphicsSettings.GetRenderPipelineSettings から読む。
        /// HDRenderPipelineGlobalSettings.asset の旧フィールド
        /// (m_CustomPostProcessOrdersSettings) を手で書いても参照されない。
        /// 型名は AssemblyQualifiedName でないと Type.GetType が解決できず、
        /// 解決できなかった項目は HDRP 側で黙って削除される。
        ///
        /// CustomPostProcessOrdersSettings は internal なのでリフレクションで触る。
        /// </summary>
        static void EnsureVhsRegistered()
        {
            try
            {
                var hdAsm = typeof(HDAdditionalCameraData).Assembly;
                var ordersType = hdAsm.GetType(
                    "UnityEngine.Rendering.HighDefinition.CustomPostProcessOrdersSettings");
                if (ordersType == null) { Log.Add("VHS: 設定の型が見つからない"); return; }

                MethodInfo getter = null;
                foreach (var mi in typeof(GraphicsSettings).GetMethods(
                             BindingFlags.Public | BindingFlags.Static))
                    if (mi.Name == "GetRenderPipelineSettings" && mi.IsGenericMethodDefinition
                        && mi.GetParameters().Length == 0) { getter = mi; break; }
                if (getter == null) { Log.Add("VHS: GetRenderPipelineSettings が無い"); return; }

                var orders = getter.MakeGenericMethod(ordersType).Invoke(null, null);
                if (orders == null) { Log.Add("VHS: 設定が取得できない"); return; }

                var prop = ordersType.GetProperty("afterPostProcessCustomPostProcesses", Any);
                var list = prop?.GetValue(orders);
                if (list == null) { Log.Add("VHS: After Post Process の一覧が取れない"); return; }

                string tn = typeof(VhsPostProcess).AssemblyQualifiedName;
                var contains = list.GetType().GetMethod("Contains", new[] { typeof(string) });
                var add = list.GetType().GetMethod("Add", new[] { typeof(string) });
                bool has = contains != null && (bool)contains.Invoke(list, new object[] { tn });
                if (!has && add != null)
                {
                    bool ok = (bool)add.Invoke(list, new object[] { tn });
                    Log.Add("VHS: After Post Process に登録 " + (ok ? "OK" : "失敗") + "  " + tn);
                }
                else Log.Add("VHS: すでに登録済み");

                var gs = AssetDatabase.LoadAssetAtPath<ScriptableObject>(
                    "Assets/Settings/HDRPDefaultResources/HDRenderPipelineGlobalSettings.asset");
                if (gs != null) { EditorUtility.SetDirty(gs); AssetDatabase.SaveAssets(); }
            }
            catch (Exception e) { Log.Add("VHS の登録に失敗: " + e.Message); }
        }

        static Level0Generator.Result InfiniteWorld(MaterialSet mats)
        {
            var res = new Level0Generator.Result();
            var root = new GameObject("World");
            res.Root = root;

            var world = root.AddComponent<ChunkedLevel0>();
            world.Materials = mats;
            world.Seed = 1996;
            world.LoadRadius = 4;      // 57.6m
            world.WarmRadius = 4;      // 歩き出す前に全部作っておく
            world.ChunksPerFrame = 2;
            world.LightRadius = 2;
            world.Lumens = _lumens;
            world.AreaSize = _areaSize;
            world.DiffuserNits = 700f;

            // 器具の間引きはこの root の下に付ける。チャンクが増減するたびに
            // ChunkedLevel0 が Rebuild を呼ぶ。
            var culler = root.AddComponent<LightCuller>();
            culler.MaxActive = _maxActiveLights;
            culler.Radius = _lightRadius;
            culler.LookAhead = _lookAhead;

            // 無限に続く以上、立ち位置も向きも自分で解かないと壁を撮ることになる。
            // 半径 0.9m の円が入る場所を探し、そこから最も遠くまで見通せる向きを選ぶ。
            var spawn = Level0Layout.FindOpen(new Vector3(2.7f, 0f, 2.7f), world.Seed, 0.9f);
            res.Spawn = spawn;
            res.SpawnYaw = Level0Layout.BestYaw(spawn, world.Seed);

            var far1 = Level0Layout.FindOpen(spawn + new Vector3(38f, 0f, 26f), world.Seed, 0.9f);
            var far2 = Level0Layout.FindOpen(spawn + new Vector3(-30f, 0f, 62f), world.Seed, 0.9f);
            res.Shots.Add(new Level0Generator.ShotSpec("I1_spawn", spawn, res.SpawnYaw, -2.27f));
            res.Shots.Add(new Level0Generator.ShotSpec("I2_ceiling", spawn, res.SpawnYaw + 25f, -26f));
            res.Shots.Add(new Level0Generator.ShotSpec("I3_far", far1, Level0Layout.BestYaw(far1, world.Seed), -2.27f));
            res.Shots.Add(new Level0Generator.ShotSpec("I4_farther", far2, Level0Layout.BestYaw(far2, world.Seed), -2.27f));

            // 歩行テストの経路は幅優先で解く。直線で結んだ経由点では必ず壁に当たる。
            var path = Level0Layout.Path(spawn, far1, world.Seed, 0.45f);
            if (path.Count >= 2) res.Walk.AddRange(path);
            else { res.Walk.Add(spawn); res.Walk.Add(far1); }

            res.Log.Add("=== 無限生成 (ChunkedLevel0) ===");
            res.Log.Add($"チャンク {ChunkedLevel0.ChunkSize:F1} m 角 / 読み込み半径 {world.LoadRadius} "
                        + $"({world.LoadRadius * ChunkedLevel0.ChunkSize:F0} m) / "
                        + $"ライトは半径 {world.LightRadius} まで");
            res.Log.Add($"スポーン {spawn} yaw {res.SpawnYaw:F0}  遠景 1 {far1}  遠景 2 {far2}");
            res.Log.Add($"歩行経路 {res.Walk.Count} 点 / 直線距離 {Vector3.Distance(spawn, far1):F1} m"
                        + (res.Walk.Count >= 2 ? "" : "  <- 経路が見つからなかった"));
            int lit = 0, dead = 0, flk = 0, none = 0;
            for (int i = -40; i < 40; i++)
                for (int j = -40; j < 40; j++)
                {
                    if (!Level0Layout.HasFixture(i, j, world.Seed)) { none++; continue; }
                    switch (Level0Layout.LampAt(i, j, world.Seed))
                    {
                        case Level0Layout.Lamp.Lit: lit++; break;
                        case Level0Layout.Lamp.Dead: dead++; break;
                        default: flk++; break;
                    }
                }
            int tot = lit + dead + flk + none;
            res.Log.Add($"器具の状態 (6400 ベイの標本): 点灯 {lit * 100 / tot}% / "
                        + $"切れ {dead * 100 / tot}% / ちらつき {flk * 100 / tot}% / "
                        + $"器具なし {none * 100 / tot}%");
            return res;
        }

        static void BuildWith(Func<MaterialSet, Level0Generator.Result> gen)
        {
            Log.Clear();
            Log.Add("=== SceneBuilder (photoreal) " + DateTime.Now + " ===");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var mats = LoadMaterials();
            var geo = gen == null ? Level0Generator.Build(mats) : gen(mats);
            Log.AddRange(geo.Log);

            // 無限生成のシーンは器具を実行時に作るので、ここでは何も置かない
            if (geo.TrofferCenters.Count > 0) BuildLights(geo);
            BuildDecals(geo);
            BuildVolume();
            BuildPlayer(geo);

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            Log.Add("saved: " + ScenePath);

            AssetDatabase.SaveAssets();
            Log.Add("RESULT: OK");
            Report();
        }

        // --------------------------------------------------------- materials
        static MaterialSet LoadMaterials()
        {
            Material M(string n)
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(MatRoot + "/" + n + ".mat");
                if (m == null) Log.Add("  MISSING MATERIAL " + n);
                return m;
            }
            return new MaterialSet
            {
                Wallpaper = M("M_Wallpaper"),
                Carpet = M("M_Carpet"),
                CeilingTile = M("M_CeilingTile"),
                CeilingTileDark = M("M_CeilingTileDark"),
                Metal = M("M_GridMetal"),
                MetalDark = M("M_GridMetalDark"),
                Baseboard = M("M_Baseboard"),
                DiffuserOn = M("M_DiffuserOn"),
                DiffuserOff = M("M_DiffuserOff"),
                Chrome = M("M_Chrome"),
                Plastic = M("M_Plastic"),
                DoorFrame = M("M_DoorFrame"),
                Plenum = M("M_Plenum"),
            };
        }

        // ------------------------------------------------------------ lights
        static void BuildLights(Level0Generator.Result geo)
        {
            var root = new GameObject("Lights");
            int made = 0, shadowed = 0;

            // 影を落とすのはスポーン付近の 4 灯だけ。エリアライトの影は
            // 1 台ごとに専用のシャドウマップを要求するので、数を増やすと
            // レイトレの予算を全て持っていく。
            var order = new List<int>();
            for (int i = 0; i < geo.TrofferCenters.Count; i++) if (geo.TrofferAlive[i]) order.Add(i);
            order.Sort((x, y) => (geo.TrofferCenters[x] - geo.Spawn).sqrMagnitude
                                 .CompareTo((geo.TrofferCenters[y] - geo.Spawn).sqrMagnitude));
            var wantShadows = new HashSet<int>();
            for (int k = 0; k < order.Count && k < 4; k++) wantShadows.Add(order[k]);

            for (int i = 0; i < geo.TrofferCenters.Count; i++)
            {
                if (!geo.TrofferAlive[i]) continue;
                var p = geo.TrofferCenters[i];

                var go = new GameObject($"Troffer_{i:000}");
                go.transform.SetParent(root.transform, false);
                go.transform.position = new Vector3(p.x, p.y - 0.012f, p.z);
                go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

                var light = go.AddComponent<Light>();
                var hd = go.AddComponent<HDAdditionalLightData>();
                try
                {
                    hd.SetLightTypeAndShape(HDLightTypeAndShape.RectangleArea);
                    hd.SetAreaLightSize(_areaSize);
                    // 4 灯 32W の T8 が入った古い 2x4 トロファー相当。
                    // 器具の汚れと管の劣化で新品の 6 割程度しか出ていない想定。
                    hd.SetIntensity(_lumens, LightUnit.Lumen);
                    hd.range = 9f;
                    hd.affectsVolumetric = true;
                }
                catch (Exception e) { Log.Add("  light setup failed: " + e.Message); }

                // 蛍光灯は黒体放射ではない。水銀の 546nm の輝線が強く出るため、
                // 色温度では表せない緑寄りの色になる。HDRP は
                // color.linear x CCT(T) を掛けるので、色温度を使うと必ず
                // 黒体の軌跡上に乗ってしまう。ここは色温度を切って直接指定する。
                // 参照の実測から逆算: 実効リニア (1.000, 0.943, 0.500)。
                light.useColorTemperature = false;
                light.color = new Color(1.0f, 0.974f, 0.735f);

                bool wantShadow = wantShadows.Contains(i);
                light.shadows = wantShadow ? LightShadows.Soft : LightShadows.None;
                if (wantShadow)
                {
                    try
                    {
                        hd.SetShadowResolution(1024);
                        // ライト側の既定は override=false。ここを立てないと
                        // Volume の ContactShadows は一切効かない。
                        hd.useContactShadow.useOverride = true;
                        hd.useContactShadow.@override = true;
                    }
                    catch { }
                    shadowed++;
                }
                made++;
            }

            var culler = root.AddComponent<LightCuller>();
            culler.MaxActive = _maxActiveLights;
            culler.Radius = _lightRadius;
            culler.LookAhead = _lookAhead;
            Log.Add($"lights: {made} area lights ({shadowed} casting shadows), "
                    + $"culler keeps the nearest {culler.MaxActive} within {culler.Radius} m"
                    + (culler.LookAhead > 0f ? $" (focus {culler.LookAhead} m ahead)" : ""));
        }

        // ------------------------------------------------------------ decals

        /// <summary>
        /// 原因が「その場所」に紐づく汚れを置く。壁紙は 2.12m ごとに
        /// タイリングするので、手擦れや水の垂れのような一点物をテクスチャに
        /// 焼くと必ず等間隔で並んで見える。位置は生成器が決める。
        /// </summary>
        static void BuildDecals(Level0Generator.Result geo)
        {
            var root = new GameObject("Decals");
            Material M(string n) => AssetDatabase.LoadAssetAtPath<Material>(MatRoot + "/M_Decal_" + n + ".mat");
            var cache = new Dictionary<string, Material>();
            int placed = 0, miss = 0;
            foreach (var d in geo.Decals)
            {
                if (!cache.TryGetValue(d.Kind, out var mat)) { mat = M(d.Kind); cache[d.Kind] = mat; }
                if (mat == null) { miss++; continue; }
                var go = new GameObject("Decal_" + d.Kind + "_" + placed);
                go.transform.SetParent(root.transform, false);
                go.transform.position = d.Pos;
                go.transform.rotation = Quaternion.Euler(d.Euler);
                var dp = go.AddComponent<DecalProjector>();
                dp.material = mat;
                dp.size = new Vector3(d.Size.x, d.Size.y, 0.45f);
                dp.pivot = Vector3.zero;
                dp.drawDistance = 45f;
                dp.fadeFactor = 1f;
                placed++;
            }
            Log.Add($"decals: {placed} projectors" + (miss > 0 ? $" ({miss} skipped, material missing)" : ""));
        }

        // ------------------------------------------------------------ volume
        static void BuildVolume()
        {
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath));
            AssetDatabase.CreateAsset(profile, ProfilePath);

            // 屋内なので空は無い。全ての光はエリアライトと GI から来る。
            var env = AddOverride<VisualEnvironment>(profile);
            Set(env, "skyType", 0);

            // --- 露出：人間の目の順応に合わせる -------------------------
            var exp = AddOverride<Exposure>(profile);
            Set(exp, "mode", (int)ExposureMode.AutomaticHistogram);
            Set(exp, "meteringMode", (int)MeteringMode.CenterWeighted);
            // ヒストグラム自動露出は avgEV から compensation を引いてから
            // クランプするだけなので、フィードバックループに食われない素の
            // オフセットとして効く。壁の実測 Y=0.077 に対し参照は約 0.54
            // (2.8 段上) だが、行き過ぎを避けてまず +1.5 段。
            // 壁は Y=0.164 で参照の 0.54 に対しまだ 1.72 段下。ACES が圧縮する分
            // 入力側は 1.72 段以上要るが、行き過ぎを避けて +1.5 段だけ足す。
            // 参照画像を実測したところ、目測は逆方向に大きく外れていた。
            // 参照の中央値 Y は 0.240、白飛び 0.00%。こちらは 0.468 で
            // 白飛び 2.58% だったので、1 段落とす。
            Set(exp, "compensation", _expComp);  // 1.85 では中央値 0.188 (参照 0.240) と下げすぎた
            Set(exp, "limitMin", -1.0f);
            Set(exp, "limitMax", 13.5f);
            Set(exp, "adaptationSpeedDarkToLight", 2.0f);
            Set(exp, "adaptationSpeedLightToDark", 0.9f);

            // --- レイトレーシング GI -------------------------------------
            // VHS が無くなって内部解像度が 960x720 から 1920x1080 に上がったが、
            // 旧構成で 300fps 出ていたので品質側に振り直す余裕がある。
            var gi = AddOverride<GlobalIllumination>(profile);
            UseCustomQuality(gi);
            Set(gi, "enable", true);
            Set(gi, "tracing", (int)RayCastingMode.RayTracing);
            // bounceCount は QualityRTGI の中でしか読まれず、Performance では
            // 一度も参照されない。ただし Quality に切り替えても天井タイルは
            // 0.020 -> 0.015 で改善せず、fps だけ 72 -> 18 に落ちた。
            // 天井が暗いのは GI ではなく参照側の黒の持ち上げが原因だったので、
            // 2 倍のコストに見合わない Performance に戻す。
            Set(gi, "mode", (int)RayTracingMode.Performance);
            Set(gi, "fullResolution", true);
            Set(gi, "rayLength", 14f);   // 12x9m の室内に 20m は過剰
            // 以前 8.0 を試して変化しなかったが、あれは室内全体が 5 段暗く、
            // クランプに掛かるサンプルがそもそも無い状態での試験だった。
            // 露出 +2.2 EV の今、器具直下の床は直接光で十分明るく、
            // 天井が受け取るべきサンプルがここで切られている可能性が高い。
            Set(gi, "clampValue", 12.0f);
            Set(gi, "denoise", true);
            Set(gi, "halfResolutionDenoiser", false); // 旧: true（間接光の滲みが減る）
            Set(gi, "denoiserRadius", 0.45f);
            Set(gi, "bounceCount", 2);
            Set(gi, "maxMixedRaySteps", 64);

            // --- レイトレーシング反射 -------------------------------------
            var ssr = AddOverride<ScreenSpaceReflection>(profile);
            UseCustomQuality(ssr);
            Set(ssr, "enabled", true);
            Set(ssr, "tracing", (int)RayCastingMode.RayTracing);
            Set(ssr, "mode", (int)RayTracingMode.Performance);
            // Quality RTGI が重いので、反射は半解像度に落として予算を回す。
            Set(ssr, "fullResolution", false);
            Set(ssr, "rayLength", 16f);
            Set(ssr, "minSmoothness", 0.20f);         // 旧: 0.30（粗い面にも反射を出す）
            Set(ssr, "smoothnessFadeStart", 0.30f);
            Set(ssr, "denoise", true);

            // --- 間接光の総量 ---------------------------------------------
            // 天井タイルは下向き面で 100% 間接光。参照では壁の 40% の明るさが
            // あるのに、こちらは 6% しか出ない。RTGI が下向き面に運ぶ量が
            // 実測で 5-6 倍足りない (clamp も Quality モードも効果なしを確認済み)。
            // 直接光を受ける壁・床への影響は小さく、天井だけが選択的に上がる。
            var ilc = AddOverride<IndirectLightingController>(profile);
            Set(ilc, "indirectDiffuseLightingMultiplier", 1.8f);   // 2.6 は壁まで上げすぎた

            var rts = AddOverride<RayTracingSettings>(profile);
            Set(rts, "rayBias", 0.002f);
            Set(rts, "extendShadowCulling", true);
            Set(rts, "extendCameraCulling", true);

            // --- 空気 -----------------------------------------------------
            // 実際の屋内には必ず埃と湿気があり、遠景がわずかに霞む。
            var fog = AddOverride<Fog>(profile);
            Set(fog, "enabled", true);
            // 95m はほぼ真空で、遠くの壁がコントラストを一切失わなかった。
            // 実際の屋内は埃と湿気で 40-60m 相当。
            Set(fog, "meanFreePath", 55f);   // 旧 95
            Set(fog, "baseHeight", 0f);
            Set(fog, "maximumHeight", 40f);
            Set(fog, "enableVolumetricFog", true);
            Set(fog, "anisotropy", 0.15f);
            Set(fog, "depthExtent", 45f);
            SetColor(fog, "albedo", new Color(0.90f, 0.89f, 0.86f));

            // --- 発色：演出は入れない -------------------------------------
            var tone = AddOverride<Tonemapping>(profile);
            Set(tone, "mode", (int)TonemappingMode.ACES);   // 実写の階調に最も近い

            // ホワイトバランスは触らない。4200K の蛍光灯の下で
            // デイライトに合わせたカメラが見る色を、そのまま出す。
            // 中性の持ち上げは撤回した。参照の暗部は sRGB (0.271, 0.197, 0.044)、
            // B/R = 0.057 の濃いオレンジで、黒が持ち上がっているのではなく
            // 部屋のどこにも青が存在しないだけ。中性の lift を足すと
            // 平均彩度が 0.702 -> 0.304 まで壊れた (参照は 0.674)。
            // 正しい処置は全ての材質の彩度を参照に合わせること。

            var ca = AddOverride<ColorAdjustments>(profile);
            Set(ca, "postExposure", 0f);
            Set(ca, "contrast", 0f);
            Set(ca, "saturation", 0f);

            // --- レンズの物理的な性質だけを足す ---------------------------
            var bloom = AddOverride<Bloom>(profile);
            Set(bloom, "intensity", 0.13f);   // 実レンズの光条とにじみ
            Set(bloom, "scatter", 0.62f);

            var chroma = AddOverride<ChromaticAberration>(profile);
            // 0.055 では天井のスプリンクラーのような小さく明るい物に虹の縁が出て、
            // レンズの性質ではなく描画の破綻に見えていた。実レンズの色収差は
            // 画面周辺でようやく分かる程度。
            Set(chroma, "intensity", _vhs ? 0.010f : 0.030f);  // VHS 側で色がにじむ

            var vig = AddOverride<Vignette>(profile);
            Set(vig, "intensity", 0.13f);     // レンズの周辺光量落ち
            Set(vig, "smoothness", 0.55f);

            // --- 接地影 -------------------------------------------------
            // エリアライトの柔らかい影だけだと、柱や幅木の足元が床から浮いて見える。
            // 長さは接地部だけに効く 0.10m に留める。
            var cs = AddOverride<ContactShadows>(profile);
            Set(cs, "enable", true);
            Set(cs, "length", 0.10f);
            Set(cs, "opacity", 0.85f);
            Set(cs, "distanceScaleFactor", 0.5f);
            Set(cs, "maxDistance", 18f);
            Set(cs, "thicknessScale", 0.15f);

            // --- センサーノイズ -------------------------------------------
            // 完全に清潔な画は必ず CG に見える。実写は暗部に必ず粒子が乗る。
            // ISO 400 程度の控えめな量から始める。
            var grain = AddOverride<FilmGrain>(profile);
            Set(grain, "type", (int)FilmGrainLookup.Thin1);
            // VHS の粒子と二重に乗ると砂嵐になる
            Set(grain, "intensity", _vhs ? 0.04f : 0.12f);
            Set(grain, "response", 0.75f);

            // --- レンズの歪曲 ---------------------------------------------
            // 24mm 相当なのに直線が完全な直線なのは実在のレンズではありえない。
            // HDRP の LensDistortion は intensity と実際の歪み量の対応が分からず、
            // 参照から実測した k1 に合わせられない。複製シーンでは切っておき、
            // レンズの歪みは CaptureMedium が実測値どおりに掛ける。
            var dist = AddOverride<LensDistortion>(profile);
            Set(dist, "intensity", _lensK1 != 0f ? 0f : -0.08f);
            Set(dist, "scale", 1.0f);

            // --- VHS -----------------------------------------------------
            // 1996 年のテープが記録できる範囲まで落とす。実機の制約をそのまま
            // 並べたもので、演出として汚しているのではない。
            if (_vhs)
            {
                var vhs = AddOverride<VhsPostProcess>(profile);
                // 控えめから始める。掛けすぎると「テープ」ではなく「壊れた描画」に見える。
                Set(vhs, "intensity", 0.75f);
                Set(vhs, "lines", 486f);
                Set(vhs, "lumaBand", 420f);
                Set(vhs, "chromaBand", 60f);
                Set(vhs, "chroma", 0.85f);
                Set(vhs, "desaturate", 0.08f);
                Set(vhs, "sharpen", 0.10f);
                Set(vhs, "tape", 0.15f);
                Set(vhs, "tracking", 0.20f);
                Set(vhs, "dropout", 0.10f);
                Set(vhs, "headSwitch", 0.25f);
                Set(vhs, "scanline", 0.12f);
                Set(vhs, "grain", 0.25f);
                Set(vhs, "fisheye", 0.40f);     // 0 -> 0.40 (intensity 込みで 0.30)。中央が 21% 膨らむ
                Set(vhs, "pillarbox", true);
            }

            var mb = AddOverride<MotionBlur>(profile);
            Set(mb, "intensity", 0.22f);

            var ao = AddOverride<ScreenSpaceAmbientOcclusion>(profile);
            // 参照と同じ構図で測ると、幅木が 0.574 に対し 0.339 まで落ちていた。
            // 入隅の遮蔽を強く掛けすぎている。
            Set(ao, "intensity", 0.35f);   // 隅の落ち込みが足りなかった
            Set(ao, "radius", 0.45f);
            Set(ao, "rayTracing", true);      // 効けば RTAO になる

            var shadows = AddOverride<HDShadowSettings>(profile);
            Set(shadows, "maxShadowDistance", 45f);

            EditorUtility.SetDirty(profile);

            var go = new GameObject("Volume_Global");
            var v = go.AddComponent<Volume>();
            v.isGlobal = true;
            v.priority = 0;
            v.sharedProfile = profile;
            Log.Add("volume profile: " + ProfilePath);
        }

        // ------------------------------------------------------------ player
        static void BuildPlayer(Level0Generator.Result geo)
        {
            var player = new GameObject("Player");
            player.transform.position = geo.Spawn;
            player.transform.rotation = Quaternion.Euler(0f, geo.SpawnYaw, 0f);
            var cc = player.AddComponent<CharacterController>();
            // 既定の半径 0.5m では 1.2m の隙間を通れない。人の肩幅は 0.45m 前後。
            cc.radius = 0.30f;
            cc.height = 1.78f;
            cc.center = new Vector3(0f, 0.89f, 0f);
            cc.slopeLimit = 45f;
            cc.stepOffset = 0.25f;
            player.AddComponent<FirstPersonController>();

            var head = new GameObject("Head");
            head.transform.SetParent(player.transform, false);
            head.transform.localPosition = new Vector3(0f, _eyeH, 0f);
            // 目の高さは StandHeight + EyeOffset で決まる。参照のカムコーダは
            // 胸の高さ (1.345m) なので、身長はそのままに EyeOffset で下げる。
            player.GetComponent<FirstPersonController>().EyeOffset
                = _eyeH - player.GetComponent<FirstPersonController>().StandHeight;

            var camGo = new GameObject("MainCamera");
            camGo.transform.SetParent(head.transform, false);
            var cam = camGo.AddComponent<Camera>();
            // 垂直 47 度 = 16:9 で水平 75.4 度、フルサイズ換算 24mm。
            // 実際の室内・建築写真がこの画角で撮られている。これより狭いと
            // 空間の広がりが読めず、これより広いと周辺の引き伸ばしが CG に見える。
            // 歪みを後から掛ける分、レンダ側の画角を広げておく。広げないと
            // 画面の隅が「元画像の外」を要求して引き伸ばされる。
            float over = _lensK1 != 0f
                ? CaptureMedium.RequiredOverscan(_lensK1, _outW, _outH, _fovV) : 1f;
            cam.fieldOfView = 2f * Mathf.Atan(Mathf.Tan(_fovV * 0.5f * Mathf.Deg2Rad) * over)
                              * Mathf.Rad2Deg;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 80f;
            cam.allowHDR = true;
            camGo.tag = "MainCamera";
            // 描画先が何であれ 4:3 で描く。ウィンドウ比率のままだと
            // VHS 側で 4:3 の窓に詰め直したときに縦長に潰れる。
            camGo.AddComponent<FixedAspect>().Aspect = _aspect;
            var camData = camGo.AddComponent<HDAdditionalCameraData>();

            try
            {
                camData.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                camData.backgroundColorHDR = Color.black;
                // フォトリアルでは TAA が必須。無いと全ての細部がちらつく。
                camData.antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;
                camData.TAAQuality = HDAdditionalCameraData.TAAQualityLevel.High;
                camData.dithering = true;
                camData.stopNaNs = true;
                float hfov = 2f * Mathf.Atan(Mathf.Tan(_fovV * 0.5f * Mathf.Deg2Rad) * _aspect) * Mathf.Rad2Deg;
                Log.Add($"camera: TAA High, 出力 {_fovV:F2}deg vFOV / 水平 {hfov:F2}deg / "
                        + $"比率 {_aspect:F3} ({_outW}x{_outH}) / レンダ {cam.fieldOfView:F2}deg "
                        + $"(overscan {over:F3}), black background");
            }
            catch (Exception e) { Log.Add("  camera data setup failed: " + e.Message); }

            try
            {
                camData.customRenderingSettings = true;
                camData.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.RayTracing, true);
                camData.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.SSGI, true);
                camData.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.SSR, true);
                camData.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.Volumetrics, true);
                camData.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.ScreenSpaceShadows, true);
                camData.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.ContactShadows, true);
                camData.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.Decals, true);
                // これを立てないと Volume に VHS を載せても呼ばれない。
                // customRenderingSettings を立てた時点で既定値には戻らない。
                camData.renderingPathCustomFrameSettings.SetEnabled(
                    FrameSettingsField.CustomPostProcess, true);
                camData.renderingPathCustomFrameSettings.SetEnabled(
                    FrameSettingsField.Postprocess, true);

                var mask = camData.renderingPathCustomFrameSettingsOverrideMask;
                mask.mask[(uint)FrameSettingsField.RayTracing] = true;
                mask.mask[(uint)FrameSettingsField.SSGI] = true;
                mask.mask[(uint)FrameSettingsField.SSR] = true;
                mask.mask[(uint)FrameSettingsField.Volumetrics] = true;
                mask.mask[(uint)FrameSettingsField.ScreenSpaceShadows] = true;
                mask.mask[(uint)FrameSettingsField.ContactShadows] = true;
                mask.mask[(uint)FrameSettingsField.Decals] = true;
                mask.mask[(uint)FrameSettingsField.CustomPostProcess] = true;
                mask.mask[(uint)FrameSettingsField.Postprocess] = true;
                camData.renderingPathCustomFrameSettingsOverrideMask = mask;
                Log.Add("camera frame settings: RayTracing/SSGI/SSR/Volumetrics/SSShadows = on");
            }
            catch (Exception e) { Log.Add("  frame settings failed: " + e.Message); }

            // 手持ちライト。カメラのわずかに右下から照らすと、影の付き方が自然になる。
            var torchGo = new GameObject("Flashlight");
            torchGo.transform.SetParent(camGo.transform, false);
            torchGo.transform.localPosition = new Vector3(0.14f, -0.16f, 0.05f);
            torchGo.transform.localRotation = Quaternion.Euler(0.6f, -1.2f, 0f);
            torchGo.AddComponent<Light>();
            torchGo.AddComponent<HDAdditionalLightData>();
            torchGo.AddComponent<Flashlight>();

            // 生成された間取りに合わせた視点をシーンに載せる。
            // 直書きの座標は、間取りが乱数で決まる以上もう使えない。
            var routeGo = new GameObject("CaptureRoute");
            var route = routeGo.AddComponent<CaptureRoute>();
            var shots = new List<FlatCaptureRunner.Shot>();
            foreach (var s in geo.Shots)
                shots.Add(new FlatCaptureRunner.Shot(s.Name, s.Pos, s.Yaw, s.Pitch, s.Light));
            route.Shots = shots.ToArray();
            route.EyeHeight = _eyeH;
            route.LensK1 = _lensK1;
            route.LensOverscan = over;
            route.BlurSigma = _blurSigma;
            route.Glare = _glare;
            route.GlareSigma = _glareSigma;
            route.MediumGain = _mediumGain;
            route.Width = _outW; route.Height = _outH;
            route.FpsProbe = geo.Spawn;
            route.FpsYaw = geo.SpawnYaw;
            route.Waypoints = geo.Walk.ToArray();

            Log.Add($"player rig built at {player.transform.position} yaw {geo.SpawnYaw:F0}, "
                    + $"{route.Shots.Length} capture views");
        }

        // ----------------------------------------------------------- helpers
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        /// <summary>
        /// VolumeProfile にオーバーライドを足す。VolumeProfile.Add は
        /// ScriptableObject を生成してリストに入れるだけで、アセットには紐付けない。
        /// AddObjectToAsset を呼ばないと保存時に全て null になり、
        /// プロファイルは「14 個の空スロット」として書き出されて何も効かなくなる。
        /// </summary>
        static T AddOverride<T>(VolumeProfile profile) where T : VolumeComponent
        {
            var c = profile.Add<T>(true);
            c.hideFlags = HideFlags.HideInHierarchy | HideFlags.HideInInspector;
            AssetDatabase.AddObjectToAsset(c, profile);
            return c;
        }

        /// <summary>
        /// VolumeComponent の設定値を書く。HDRP は品質依存の項目を
        /// 「private な m_Xxx フィールド + public な素の型のプロパティ」で公開しているため、
        /// public フィールドだけを見ていると大半が設定できない。3 通り試す。
        /// </summary>
        static void Set(VolumeComponent c, string field, object value)
        {
            if (c == null) { Log.Add("  NULL component for " + field); return; }
            var type = c.GetType();

            var f = type.GetField(field, BindingFlags.Public | BindingFlags.Instance);
            if (f == null || !(f.GetValue(c) is VolumeParameter))
            {
                var alt = "m_" + char.ToUpperInvariant(field[0]) + field.Substring(1);
                var f2 = type.GetField(alt, Any);
                if (f2 != null && f2.GetValue(c) is VolumeParameter) f = f2;
            }

            if (f != null && f.GetValue(c) is VolumeParameter p)
            {
                var valueProp = p.GetType().GetProperty("value", BindingFlags.Public | BindingFlags.Instance);
                if (valueProp != null)
                {
                    try
                    {
                        valueProp.SetValue(p, Coerce(value, valueProp.PropertyType));
                        p.overrideState = true;
                        Log.Add($"  set  {type.Name}.{f.Name} = {valueProp.GetValue(p)}");
                        return;
                    }
                    catch (Exception e) { Log.Add($"  ERR  {type.Name}.{f.Name} : {e.Message}"); return; }
                }
            }

            var prop = type.GetProperty(field, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
            {
                try
                {
                    prop.SetValue(c, Coerce(value, prop.PropertyType));
                    Log.Add($"  set  {type.Name}.{field} (property) = {prop.GetValue(c)}");
                    return;
                }
                catch (Exception e) { Log.Add($"  ERR  {type.Name}.{field} (property) : {e.Message}"); return; }
            }

            Log.Add($"  MISS {type.Name}.{field}");
        }

        static object Coerce(object value, Type t)
        {
            if (t.IsEnum) return Enum.ToObject(t, Convert.ToInt32(value));
            if (t == typeof(float)) return Convert.ToSingle(value);
            if (t == typeof(int)) return Convert.ToInt32(value);
            if (t == typeof(bool)) return Convert.ToBoolean(value);
            return value;
        }

        /// <summary>
        /// 品質レベルを Custom にする。これをしないと rayLength などの個別指定は
        /// 無視され、HDRP の品質プリセット側の値が使われてしまう。
        /// </summary>
        static void UseCustomQuality(VolumeComponent c)
        {
            var f = c.GetType().GetField("quality", Any);
            if (f == null || !(f.GetValue(c) is VolumeParameter p))
            { Log.Add("  MISS quality on " + c.GetType().Name); return; }
            var valueProp = p.GetType().GetProperty("value");
            if (valueProp == null) return;
            valueProp.SetValue(p, 3);   // ScalableSettingLevelParameter は 3 が「カスタム」
            p.overrideState = true;
            Log.Add($"  set  {c.GetType().Name}.quality = Custom");
        }

        static void SetVec(VolumeComponent c, string field, Vector4 v)
        {
            var f = c?.GetType().GetField(field, Any);
            if (f == null) { Log.Add($"  MISS {c?.GetType().Name}.{field}"); return; }
            var p = f.GetValue(c) as VolumeParameter;
            var valueProp = p?.GetType().GetProperty("value");
            if (valueProp == null || valueProp.PropertyType != typeof(Vector4))
            { Log.Add("  MISS vec4 " + field); return; }
            valueProp.SetValue(p, v);
            p.overrideState = true;
            Log.Add($"  set  {c.GetType().Name}.{field} = {v}");
        }

        static void SetColor(VolumeComponent c, string field, Color color)
        {
            var f = c?.GetType().GetField(field, Any);
            if (f == null) { Log.Add($"  MISS {c?.GetType().Name}.{field}"); return; }
            var p = f.GetValue(c) as VolumeParameter;
            var valueProp = p?.GetType().GetProperty("value");
            if (valueProp == null || valueProp.PropertyType != typeof(Color))
            { Log.Add("  MISS color " + field); return; }
            valueProp.SetValue(p, color);
            p.overrideState = true;
            Log.Add($"  set  {c.GetType().Name}.{field} = {color}");
        }

        static void Report()
        {
            try
            {
                Directory.CreateDirectory("../artifacts");
                File.WriteAllLines("../artifacts/scene_report.txt", Log);
            }
            catch { }
            Debug.Log(string.Join("\n", Log));
        }
    }
}
