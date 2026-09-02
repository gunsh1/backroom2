using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Backrooms.Generation;
using Backrooms.Player;

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

        [MenuItem("Backrooms/3. Build Scene")]
        public static void Build()
        {
            Log.Clear();
            Log.Add("=== SceneBuilder (photoreal) " + DateTime.Now + " ===");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var mats = LoadMaterials();
            var geo = Level0Builder.BuildSandbox(mats);
            Log.Add($"geometry: {geo.Verts} verts / {geo.Tris} tris / {geo.TrofferCenters.Count} troffers");

            BuildLights(geo);
            BuildDecals();
            BuildVolume();
            BuildPlayer();

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
                Metal = M("M_GridMetal"),
                Baseboard = M("M_Baseboard"),
                DiffuserOn = M("M_DiffuserOn"),
                DiffuserOff = M("M_DiffuserOff"),
                Chrome = M("M_Chrome"),
                Plastic = M("M_Plastic"),
                DoorFrame = M("M_DoorFrame"),
            };
        }

        // ------------------------------------------------------------ lights
        static void BuildLights(Level0Builder.Result geo)
        {
            var root = new GameObject("Lights");
            int made = 0, shadowed = 0;

            for (int i = 0; i < geo.TrofferCenters.Count; i++)
            {
                if (!geo.TrofferAlive[i]) continue;
                var p = geo.TrofferCenters[i];

                var go = new GameObject($"Troffer_{i:00}");
                go.transform.SetParent(root.transform, false);
                go.transform.position = new Vector3(p.x, p.y - 0.012f, p.z);
                go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

                var light = go.AddComponent<Light>();
                var hd = go.AddComponent<HDAdditionalLightData>();
                try
                {
                    hd.SetLightTypeAndShape(HDLightTypeAndShape.RectangleArea);
                    hd.SetAreaLightSize(new Vector2(1.16f, 0.56f));
                    // 4 灯 32W の T8 が入った古い 2x4 トロファー相当。
                    // 器具の汚れと管の劣化で新品の 6 割程度しか出ていない想定。
                    hd.SetIntensity(3200f, LightUnit.Lumen);
                    hd.range = 9f;
                    hd.affectsVolumetric = true;
                }
                catch (Exception e) { Log.Add("  light setup failed: " + e.Message); }

                // 蛍光灯は黒体放射ではない。水銀の 546nm の輝線が強く出るため、
                // 色温度では表せない緑寄りの色になる。これが参照の「病んだ黄緑」の
                // 正体で、黒体の 4200K からは絶対に出ない。
                // HDRP は color.linear x CCT(T) を掛けるので、色温度を使うと
                // 必ず黒体の軌跡上に乗ってしまう。ここは色温度を切って直接指定する。
                // 参照の実測から逆算: 実効リニア (1.000, 0.943, 0.500)。
                // R/G = 1.06、B/R = 0.50。暖白色の蛍光管に相当する。
                light.useColorTemperature = false;
                light.color = new Color(1.0f, 0.974f, 0.735f);

                bool wantShadow = shadowed < 4 && p.z < 8f;
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
            Log.Add($"lights: {made} area lights ({shadowed} casting shadows)");
        }

        // ------------------------------------------------------------ decals

        /// <summary>
        /// 原因が「その場所」に紐づく汚れを置く。
        /// 壁紙は 2.12m ごとにタイリングするので、手擦れや水の垂れのような
        /// 一点物をテクスチャに焼くと必ず等間隔で並んで見える。
        /// </summary>
        static void BuildDecals()
        {
            var root = new GameObject("Decals");
            Material M(string n) => AssetDatabase.LoadAssetAtPath<Material>(MatRoot + "/" + n + ".mat");
            var scuff = M("M_Decal_Scuff");
            var drip = M("M_Decal_Drip");
            var traffic = M("M_Decal_Traffic");
            var dent = M("M_Decal_Dent");
            int n = 0;

            // --- 外角の手擦れ。人が角を曲がるときに手と肩が当たる高さ ---
            // 柱 (3.40-3.80, 5.20-5.60) の、通路側を向いた 2 面
            n += Add(root, scuff, "Scuff_Col_A", new Vector3(3.40f, 1.05f, 5.34f),
                     new Vector3(0f, -90f, 0f), new Vector2(0.42f, 0.95f));
            n += Add(root, scuff, "Scuff_Col_B", new Vector3(3.66f, 1.05f, 5.20f),
                     new Vector3(0f, 180f, 0f), new Vector2(0.42f, 0.95f));
            // 袖壁 (7.50-7.65, z 0-3.60) の先端
            n += Add(root, scuff, "Scuff_Fin", new Vector3(7.50f, 1.05f, 3.42f),
                     new Vector3(0f, -90f, 0f), new Vector2(0.40f, 0.95f));
            // 開口の枠脇。ここは最も触られる
            n += Add(root, scuff, "Scuff_Door", new Vector3(6.95f, 1.05f, 9.00f),
                     new Vector3(0f, 180f, 0f), new Vector2(0.34f, 1.00f));

            // --- 天井から垂れた水の筋。天井際から下に向かって落ちる ---
            n += Add(root, drip, "Drip_East", new Vector3(12.00f, 1.95f, 3.10f),
                     new Vector3(0f, -90f, 0f), new Vector2(1.10f, 1.55f));
            n += Add(root, drip, "Drip_North", new Vector3(9.40f, 1.95f, 9.00f),
                     new Vector3(0f, 180f, 0f), new Vector2(0.95f, 1.50f));
            n += Add(root, drip, "Drip_RoomB", new Vector3(3.00f, 1.80f, 12.40f),
                     new Vector3(0f, 90f, 0f), new Vector2(0.90f, 1.35f));

            // --- カーペットの通行帯。毛足が踏み倒されて沈み、わずかに艶が出る ---
            // 床は真下に投影する。回転 (90,yaw,0) で local +Z が下を向く。
            n += Add(root, traffic, "Traffic_ToDoor", new Vector3(6.00f, 0f, 5.20f),
                     new Vector3(90f, 0f, 0f), new Vector2(1.45f, 7.20f));
            n += Add(root, traffic, "Traffic_Cross", new Vector3(5.40f, 0f, 4.40f),
                     new Vector3(90f, 90f, 0f), new Vector2(1.30f, 8.80f));
            n += Add(root, traffic, "Traffic_RoomB", new Vector3(6.00f, 0f, 11.60f),
                     new Vector3(90f, 0f, 0f), new Vector2(1.30f, 4.60f));

            // --- 掃除機の筋。通行の少ない隅にだけ、最後に清掃された向きが残る ---
            var vac = M("M_Decal_Vacuum");
            n += Add(root, vac, "Vacuum_NE", new Vector3(10.20f, 0f, 6.60f),
                     new Vector3(90f, 0f, 0f), new Vector2(3.20f, 3.00f));
            n += Add(root, vac, "Vacuum_SW", new Vector3(2.40f, 0f, 7.20f),
                     new Vector3(90f, 90f, 0f), new Vector2(3.00f, 2.80f));

            // --- 家具の脚が長く乗っていた跡。4 点で 1 組の机 ---
            foreach (var (dx, dz) in new[] { (0f, 0f), (1.40f, 0f), (0f, 0.70f), (1.40f, 0.70f) })
                n += Add(root, dent, "Dent", new Vector3(1.90f + dx, 0f, 1.80f + dz),
                         new Vector3(90f, 0f, 0f), new Vector2(0.11f, 0.11f));

            Log.Add($"decals: {n} projectors");
        }

        static int Add(GameObject root, Material mat, string name,
                       Vector3 pos, Vector3 euler, Vector2 size, float depth = 0.45f)
        {
            if (mat == null) { Log.Add("  decal material missing for " + name); return 0; }
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.transform.position = pos;
            go.transform.rotation = Quaternion.Euler(euler);
            var dp = go.AddComponent<DecalProjector>();
            dp.material = mat;
            dp.size = new Vector3(size.x, size.y, depth);
            dp.pivot = Vector3.zero;
            dp.drawDistance = 45f;
            dp.fadeFactor = 1f;
            return 1;
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
            Set(exp, "compensation", 1.98f);  // 1.85 では中央値 0.188 (参照 0.240) と下げすぎた
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
            Set(chroma, "intensity", 0.055f); // 周辺の色収差。効かせすぎると玩具に見える

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
            Set(grain, "intensity", 0.12f);
            Set(grain, "response", 0.75f);

            // --- レンズの歪曲 ---------------------------------------------
            // 24mm 相当なのに直線が完全な直線なのは実在のレンズではありえない。
            var dist = AddOverride<LensDistortion>(profile);
            Set(dist, "intensity", -0.08f);   // 樽型をごく僅かに
            Set(dist, "scale", 1.0f);

            var mb = AddOverride<MotionBlur>(profile);
            Set(mb, "intensity", 0.22f);

            var ao = AddOverride<ScreenSpaceAmbientOcclusion>(profile);
            Set(ao, "intensity", 0.75f);   // 隅の落ち込みが足りなかった
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
        static void BuildPlayer()
        {
            var player = new GameObject("Player");
            player.transform.position = new Vector3(1.6f, 0f, 1.6f);
            player.transform.rotation = Quaternion.Euler(0f, 32f, 0f);
            player.AddComponent<CharacterController>();
            player.AddComponent<FirstPersonController>();

            var head = new GameObject("Head");
            head.transform.SetParent(player.transform, false);
            head.transform.localPosition = new Vector3(0f, 1.68f, 0f);

            var camGo = new GameObject("MainCamera");
            camGo.transform.SetParent(head.transform, false);
            var cam = camGo.AddComponent<Camera>();
            // 垂直 47 度 = 16:9 で水平 75.4 度、フルサイズ換算 24mm。
            // 実際の室内・建築写真がこの画角で撮られている。これより狭いと
            // 空間の広がりが読めず、これより広いと周辺の引き伸ばしが CG に見える。
            cam.fieldOfView = 47f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 80f;
            cam.allowHDR = true;
            camGo.tag = "MainCamera";
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
                Log.Add("camera: TAA High, " + cam.fieldOfView + "deg vFOV, black background");
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

                var mask = camData.renderingPathCustomFrameSettingsOverrideMask;
                mask.mask[(uint)FrameSettingsField.RayTracing] = true;
                mask.mask[(uint)FrameSettingsField.SSGI] = true;
                mask.mask[(uint)FrameSettingsField.SSR] = true;
                mask.mask[(uint)FrameSettingsField.Volumetrics] = true;
                mask.mask[(uint)FrameSettingsField.ScreenSpaceShadows] = true;
                mask.mask[(uint)FrameSettingsField.ContactShadows] = true;
                mask.mask[(uint)FrameSettingsField.Decals] = true;
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

            Log.Add("player rig built at " + player.transform.position);
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
