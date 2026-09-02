using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Backrooms.EditorTools
{
    /// <summary>
    /// HDRP/Lit マテリアルをコードで組み立てる。
    /// 「実写に見える」かどうかはほぼここで決まるので、値は全て実在の材料に寄せてある。
    /// </summary>
    public static class MaterialBuilder
    {
        const string TexRoot = "Assets/_Project/Art/Textures";
        const string MatRoot = "Assets/_Project/Art/Materials";
        static readonly List<string> Log = new List<string>();

        [MenuItem("Backrooms/2. Build Materials")]
        public static void BuildAll()
        {
            Log.Clear();
            Log.Add("=== MaterialBuilder " + DateTime.Now + " ===");

            ReimportTextures();
            Directory.CreateDirectory(MatRoot);

            var detail = Tex("DetailVariation.png");

            // ------------------------------------------------------- 壁紙
            // カノンの「モノイエロー」。テクスチャはほぼ白なので、色は全てティントで作る。
            var wall = Make("M_Wallpaper");
            SetTex(wall, "_BaseColorMap", Tex("Wallpaper001B/Wallpaper_Grimed_Color.png"));
            SetTex(wall, "_NormalMap", Tex("Wallpaper001B/Wallpaper001B_NormalGL_Seamed.png"));
            SetTex(wall, "_MaskMap", Tex("Wallpaper001B/Wallpaper_Grimed_MaskMap.png"));
            SetCol(wall, "_BaseColor", Srgb(0.855f, 0.775f, 0.533f));
            // 旧 Srgb(0.83, 0.735, 0.410)。テクスチャを掛けた実効アルベドが
            // linear (0.452, 0.356, 0.087) で、青が 2 バウンスで 0.19^2 まで潰れ、
            // 室内全体がセピアに寄っていた。実測した目標 (0.52, 0.45, 0.24) の
            // 半分まで戻す: 実効 (0.487, 0.404, 0.152)、輝度アルベド 0.357 -> 0.404。   // カノンのモノイエロー
            SetF(wall, "_NormalScale", 0.85f);
            Smoothness(wall, 0.05f, 0.24f);                            // マットな紙
            SetF(wall, "_AORemapMin", 0.55f);
            Detail(wall, detail, uvScale: 2.12f, meters: 11f, albedo: 0.18f, smooth: 0.30f);
            Save(wall);

            // ----------------------------------------------------- カーペット
            // 低毛足の業務用カーペット。湿っているので少しだけ光る。
            var carpet = Make("M_Carpet");
            SetTex(carpet, "_BaseColorMap", Tex("Carpet011/Carpet011_Color.jpg"));
            SetTex(carpet, "_NormalMap", Tex("Carpet011/Carpet011_NormalGL.jpg"));
            SetTex(carpet, "_MaskMap", Tex("Carpet011/Carpet011_MaskMap.png"));
            SetTex(carpet, "_HeightMap", Tex("Carpet011/Carpet011_Displacement.jpg"));
            // Carpet011 の素の色をほぼ通す。ここを暗くしすぎると反射率が 8% まで落ちて
            // 床が黒く沈む（業務用カーペットの実測は 15〜20%）。
            SetCol(carpet, "_BaseColor", Srgb(0.90f, 0.86f, 0.76f));
            SetF(carpet, "_NormalScale", 1.0f);
            Smoothness(carpet, 0.06f, 0.30f);                           // 湿った分だけ上限を上げる
            SetF(carpet, "_AORemapMin", 0.40f);
            // 視差オクルージョン（毛足の奥行き）。控えめに開始する。
            SetF(carpet, "_DisplacementMode", 2f);
            SetF(carpet, "_HeightPoMAmplitude", 0.5f);   // cm
            SetF(carpet, "_HeightAmplitude", 0.005f);    // m
            SetF(carpet, "_HeightCenter", 0.5f);
            SetF(carpet, "_PPDMinSamples", 5f);
            SetF(carpet, "_PPDMaxSamples", 12f);
            SetF(carpet, "_PPDLodThreshold", 5f);
            Detail(carpet, detail, uvScale: 2.00f, meters: 9f, albedo: 0.80f, smooth: 0.95f);
            Save(carpet);

            // ------------------------------------------------------ 天井タイル
            var ceil = Make("M_CeilingTile");
            SetTex(ceil, "_BaseColorMap", Tex("OfficeCeiling001/OfficeCeiling_Aged_Color.png"));
            SetTex(ceil, "_NormalMap", Tex("OfficeCeiling001/OfficeCeiling001_NormalGL.jpg"));
            SetTex(ceil, "_MaskMap", Tex("OfficeCeiling001/OfficeCeiling001_MaskMap.png"));
            SetCol(ceil, "_BaseColor", Srgb(0.86f, 0.845f, 0.800f));    // わずかに黄ばんだ白
            SetF(ceil, "_NormalScale", 0.9f);
            Smoothness(ceil, 0.02f, 0.16f);                             // 吸音材はほぼ無反射
            SetF(ceil, "_AORemapMin", 0.45f);
            Save(ceil);

            // -------------------------------------------- T バー／トロファー枠
            var metal = Make("M_GridMetal");
            SetCol(metal, "_BaseColor", Srgb(0.760f, 0.745f, 0.690f));  // 黄ばんだ古い塗装鋼板
            SetF(metal, "_Metallic", 0f);
            SetF(metal, "_Smoothness", 0.30f);   // 埃を被った塗装は光らない
            Save(metal);

            // ---------------------------------------------------------- 幅木
            var baseb = Make("M_Baseboard");
            SetCol(baseb, "_BaseColor", Srgb(0.40f, 0.355f, 0.300f));   // 濃いビニル幅木
            SetF(baseb, "_Metallic", 0f);
            SetF(baseb, "_Smoothness", 0.48f);   // 半艶のゴム。面取りに光が乗らないと黒い帯に見える
            Save(baseb);

            // ------------------------------------------------- ディフューザ（点灯）
            var on = Make("M_DiffuserOn");
            SetCol(on, "_BaseColor", Srgb(0.05f, 0.05f, 0.05f));
            SetF(on, "_Smoothness", 0.55f);
            Emissive(on, Srgb(1.00f, 0.985f, 0.905f), nits: 1800f);     // 4200K 相当のわずかに緑寄り
            Save(on);

            // ------------------------------------------------------ 設備の材質
            // 古い建物のスプリンクラーは真鍮。クロームより時代が出る。
            var chrome = Make("M_Chrome");
            SetCol(chrome, "_BaseColor", Srgb(0.72f, 0.61f, 0.37f));
            SetF(chrome, "_Metallic", 1f);
            SetF(chrome, "_Smoothness", 0.58f);
            Save(chrome);

            // 感知器とコンセントの樹脂。経年で黄ばんでいる。
            var plastic = Make("M_Plastic");
            SetCol(plastic, "_BaseColor", Srgb(0.80f, 0.775f, 0.700f));
            SetF(plastic, "_Metallic", 0f);
            SetF(plastic, "_Smoothness", 0.44f);
            Save(plastic);

            // ------------------------------------------------- ディフューザ（切れ）
            var off = Make("M_DiffuserOff");
            SetCol(off, "_BaseColor", Srgb(0.60f, 0.598f, 0.575f));
            SetF(off, "_Smoothness", 0.55f);
            Save(off);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Log.Add("RESULT: OK");
            Report();
        }

        // ------------------------------------------------------------ helpers
        static void ReimportTextures()
        {
            var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { TexRoot });
            foreach (var g in guids)
                AssetDatabase.ImportAsset(AssetDatabase.GUIDToAssetPath(g), ImportAssetOptions.ForceUpdate);
            Log.Add($"reimported {guids.Length} textures with import rules");
        }

        /// <summary>sRGB 表記の色をリニアに変換する。HDRP のシェーダはリニアを期待する。</summary>
        static Color Srgb(float r, float g, float b) => new Color(r, g, b, 1f).linear;

        static Texture Tex(string relative)
        {
            var path = TexRoot + "/" + relative;
            var t = AssetDatabase.LoadAssetAtPath<Texture>(path);
            if (t == null) Log.Add("  MISSING TEXTURE " + path);
            return t;
        }

        static Material Make(string name)
        {
            var shader = Shader.Find("HDRP/Lit");
            if (shader == null) { Log.Add("FATAL: HDRP/Lit が見つからない"); return null; }
            var path = MatRoot + "/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(m, path);
            }
            m.shader = shader;
            Log.Add("material: " + name);
            return m;
        }

        static void SetTex(Material m, string prop, Texture t)
        {
            if (m == null || t == null) return;
            if (!m.HasProperty(prop)) { Log.Add("  MISS prop " + prop); return; }
            m.SetTexture(prop, t);
        }

        static void SetF(Material m, string prop, float v)
        {
            if (m == null) return;
            if (!m.HasProperty(prop)) { Log.Add("  MISS prop " + prop); return; }
            m.SetFloat(prop, v);
        }

        static void SetCol(Material m, string prop, Color c)
        {
            if (m == null) return;
            if (!m.HasProperty(prop)) { Log.Add("  MISS prop " + prop); return; }
            m.SetColor(prop, c);
        }

        static void Smoothness(Material m, float min, float max)
        {
            SetF(m, "_SmoothnessRemapMin", min);
            SetF(m, "_SmoothnessRemapMax", max);
        }

        /// <summary>大スケールの斑でタイリングの繰り返しを壊す。フォトリアルの最大の敵はこれ。</summary>
        static void Detail(Material m, Texture detail, float uvScale, float meters,
                           float albedo, float smooth)
        {
            if (detail == null) return;
            SetTex(m, "_DetailMap", detail);
            SetF(m, "_LinkDetailsWithBase", 0f);
            float s = uvScale / meters;
            m.SetTextureScale("_DetailMap", new Vector2(s, s));
            SetF(m, "_DetailAlbedoScale", albedo);
            SetF(m, "_DetailNormalScale", 0f);
            SetF(m, "_DetailSmoothnessScale", smooth);
        }

        static void Emissive(Material m, Color ldr, float nits)
        {
            SetF(m, "_UseEmissiveIntensity", 1f);
            SetCol(m, "_EmissiveColorLDR", ldr);
            SetF(m, "_EmissiveIntensity", nits);
            SetF(m, "_EmissiveIntensityUnit", 0f);   // 0 = Nits
            SetCol(m, "_EmissiveColor", ldr * nits);
            SetF(m, "_AlbedoAffectEmissive", 0f);
        }

        /// <summary>
        /// HDRP はプロパティを書き換えただけではキーワードが更新されない。
        /// ここを忘れるとノーマルもマスクも POM も一切効かない。
        /// </summary>
        static void Save(Material m)
        {
            if (m == null) return;
            bool validated = false;
            try
            {
                var hdMat = Type.GetType("UnityEngine.Rendering.HighDefinition.HDMaterial, Unity.RenderPipelines.HighDefinition.Runtime");
                var mi = hdMat?.GetMethod("ValidateMaterial", BindingFlags.Public | BindingFlags.Static,
                                          null, new[] { typeof(Material) }, null);
                if (mi != null) { mi.Invoke(null, new object[] { m }); validated = true; }
            }
            catch (Exception e) { Log.Add("  ValidateMaterial failed: " + e.Message); }

            if (!validated)
            {
                try
                {
                    var utils = Type.GetType("UnityEditor.Rendering.HighDefinition.HDShaderUtils, Unity.RenderPipelines.HighDefinition.Editor");
                    var mi = utils?.GetMethod("ResetMaterialKeywords", BindingFlags.Public | BindingFlags.Static);
                    if (mi != null) { mi.Invoke(null, new object[] { m }); validated = true; }
                }
                catch (Exception e) { Log.Add("  ResetMaterialKeywords failed: " + e.Message); }
            }
            Log.Add("  validated: " + validated + "   keywords: " + string.Join(",", m.shaderKeywords));
            EditorUtility.SetDirty(m);
        }

        static void Report()
        {
            try
            {
                Directory.CreateDirectory("../artifacts");
                File.WriteAllLines("../artifacts/material_report.txt", Log);
            }
            catch { }
            Debug.Log(string.Join("\n", Log));
        }
    }
}
