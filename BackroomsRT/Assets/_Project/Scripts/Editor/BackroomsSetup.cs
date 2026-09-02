using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;

namespace Backrooms.EditorTools
{
    /// <summary>
    /// HDRP + DXR プロジェクトをコードだけで構成する。
    /// SerializedProperty のパスはバージョンで揺れるため、末端名でのファジー検索と
    /// 全プロパティのダンプを併用して、1 回の起動で確実に効かせる。
    /// </summary>
    public static class BackroomsSetup
    {
        static readonly List<string> Log = new List<string>();
        const string ArtifactDir = "../artifacts";

        [MenuItem("Backrooms/1. Configure HDRP + DXR")]
        public static void ConfigureAll()
        {
            Log.Clear();
            L("=== Backrooms setup : " + DateTime.Now + " ===");
            L("Unity        : " + Application.unityVersion);
            L("supportsRT   : " + SystemInfo.supportsRayTracing + "   (batchmode では false になる)");
            L("graphicsAPI  : " + SystemInfo.graphicsDeviceType);

            try
            {
                ConfigurePlayer();
                ConfigureRenderPipelineAssets();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                L("RESULT: OK");
            }
            catch (Exception e)
            {
                L("RESULT: FATAL " + e);
            }
            finally
            {
                WriteReport("setup_report.txt");
            }
        }

        // ------------------------------------------------------------- player
        static void ConfigurePlayer()
        {
            L("");
            L("--- PlayerSettings ---");
            var bt = BuildTarget.StandaloneWindows64;
            var btg = NamedBuildTarget.Standalone;

            // DXR は DirectX 12 が必須
            PlayerSettings.SetUseDefaultGraphicsAPIs(bt, false);
            PlayerSettings.SetGraphicsAPIs(bt, new[] { GraphicsDeviceType.Direct3D12 });
            L("graphics APIs -> Direct3D12 only");

            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.runInBackground = true;
            PlayerSettings.defaultScreenWidth = 1920;
            PlayerSettings.defaultScreenHeight = 1080;
            PlayerSettings.fullScreenMode = FullScreenMode.FullScreenWindow;
            PlayerSettings.SetScriptingBackend(btg, ScriptingImplementation.Mono2x);
            PlayerSettings.productName = "The Backrooms 1996";
            PlayerSettings.companyName = "Found Footage";

            // 実行時生成ジオメトリと RTAS の相性のため静的バッチングは切る
            SetStaticBatching(bt, false);
            L("colorSpace=Linear, 1920x1080, staticBatching=off");
        }

        static void SetStaticBatching(BuildTarget bt, bool on)
        {
            try
            {
                var m = typeof(PlayerSettings).GetMethod("SetBatchingForPlatform",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (m != null)
                {
                    m.Invoke(null, new object[] { bt, on ? 1 : 0, 1 });
                    L("  staticBatching set via reflection");
                }
                else
                {
                    L("  WARN: SetBatchingForPlatform not found");
                }
            }
            catch (Exception e)
            {
                L("  WARN staticBatching: " + e.Message);
            }
        }

        // ---------------------------------------------------- pipeline assets
        static void ConfigureRenderPipelineAssets()
        {
            L("");
            L("--- RenderPipeline assets ---");
            var guids = AssetDatabase.FindAssets("t:RenderPipelineAsset");
            var assets = new List<RenderPipelineAsset>();
            foreach (var g in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                var a = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(path);
                if (a == null) continue;
                assets.Add(a);
                L("found: " + path + "   (" + a.GetType().Name + ")");
            }
            if (assets.Count == 0)
            {
                L("ERROR: RenderPipelineAsset が 1 つも見つからない");
                return;
            }

            bool dumped = false;
            foreach (var a in assets)
            {
                if (a.GetType().Name != "HDRenderPipelineAsset") continue;
                L("");
                L("configuring: " + AssetDatabase.GetAssetPath(a));
                var so = new SerializedObject(a);

                if (!dumped)
                {
                    DumpProperties(so, "hdrp_asset_properties.txt");
                    dumped = true;
                }

                // レイトレーシング本体
                SetBoolByLeaf(so, "supportRayTracing", true);
                // RT には Forward が要る。Both (ForwardOnly|DeferredOnly = 3) にする
                SetIntByLeaf(so, "supportedLitShaderMode", 3);
                // スクリーンスペース系（RT 版の土台になる）
                SetBoolByLeaf(so, "supportSSR", true);
                SetBoolByLeaf(so, "supportSSRTransparent", true);
                SetBoolByLeaf(so, "supportSSGI", true);
                SetBoolByLeaf(so, "supportSSAO", true);
                // RT エリアライトシャドウ
                SetBoolByLeaf(so, "supportScreenSpaceShadows", true);
                SetIntByLeaf(so, "maxScreenSpaceShadowSlots", 4);
                // エリアライト（蛍光灯トロファー）
                SetBoolByLeaf(so, "supportAreaLights", true);
                // デカール（M5 の手置き汚し用）
                SetBoolByLeaf(so, "supportDecals", true);
                SetBoolByLeaf(so, "supportDecalLayers", true);
                // ボリュメトリック（蛍光灯の光の柱）
                SetBoolByLeaf(so, "supportVolumetrics", true);
                // モーションベクタ（VHS のスミア表現に使う）
                SetBoolByLeaf(so, "supportMotionVectors", true);
                SetBoolByLeaf(so, "supportDistortion", true);
                SetBoolByLeaf(so, "supportTransparentBackface", true);

                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(a);
            }

            // 既定パイプラインを High Fidelity に
            var preferred = assets.FirstOrDefault(x => AssetDatabase.GetAssetPath(x).Contains("High Fidelity"))
                         ?? assets.FirstOrDefault(x => x.GetType().Name == "HDRenderPipelineAsset");
            if (preferred != null)
            {
                GraphicsSettings.defaultRenderPipeline = preferred;
                int levels = QualitySettings.names.Length;
                int cur = QualitySettings.GetQualityLevel();
                for (int i = 0; i < levels; i++)
                {
                    QualitySettings.SetQualityLevel(i, false);
                    QualitySettings.renderPipeline = preferred;
                }
                QualitySettings.SetQualityLevel(cur, false);
                L("");
                L("defaultRenderPipeline -> " + AssetDatabase.GetAssetPath(preferred)
                  + "   (quality levels: " + levels + ")");
            }
        }

        // ------------------------------------------------------------ helpers
        static void SetBoolByLeaf(SerializedObject so, string leaf, bool value)
        {
            SetByLeaf(so, leaf, p => p.boolValue = value, value.ToString());
        }

        static void SetIntByLeaf(SerializedObject so, string leaf, int value)
        {
            SetByLeaf(so, leaf, p => p.intValue = value, value.ToString());
        }

        /// <summary>末端名が一致する全プロパティに適用する（パスの揺れに強い）</summary>
        static void SetByLeaf(SerializedObject so, string leaf, Action<SerializedProperty> apply, string shown)
        {
            var targets = new List<string>();
            var it = so.GetIterator();
            while (it.NextVisible(true))
            {
                var path = it.propertyPath;
                if (path == leaf || path.EndsWith("." + leaf, StringComparison.Ordinal))
                    targets.Add(path);
            }

            if (targets.Count == 0)
            {
                L("  MISS " + leaf);
                return;
            }

            foreach (var path in targets)
            {
                var p = so.FindProperty(path);
                if (p == null) { L("  ERR  " + path + " : re-find failed"); continue; }
                try { apply(p); L("  SET  " + path + " = " + shown); }
                catch (Exception e) { L("  ERR  " + path + " : " + e.Message); }
            }
        }

        static void DumpProperties(SerializedObject so, string file)
        {
            var lines = new List<string> { "# " + so.targetObject.GetType().FullName };
            var it = so.GetIterator();
            while (it.NextVisible(true))
                lines.Add(it.propertyPath + "  [" + it.propertyType + "]");
            Directory.CreateDirectory(ArtifactDir);
            File.WriteAllLines(Path.Combine(ArtifactDir, file), lines);
            L("  dumped " + lines.Count + " properties -> artifacts/" + file);
        }

        static void L(string s)
        {
            Log.Add(s);
            Debug.Log("[BackroomsSetup] " + s);
        }

        static void WriteReport(string file)
        {
            try
            {
                Directory.CreateDirectory(ArtifactDir);
                File.WriteAllLines(Path.Combine(ArtifactDir, file), Log);
            }
            catch (Exception e)
            {
                Debug.LogError("report write failed: " + e);
            }
        }
    }
}
