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
            SetCol(wall, "_BaseColor", Srgb(0.881f, 0.814f, 0.656f));
            // 前回は目標の半分で止めた。実効アルベドは linear (0.487, 0.404, 0.152)
            // で B/R = 0.31、レンダでは 0.21 までしか出ず参照の 0.65 に遠い。
            // 残り半分を戻して実効 (0.52, 0.45, 0.24)、B/R = 0.46 にする。
            // 法線マップの主要な凹凸は実測で波長 11px = 1.1cm。壁紙のエンボスとして
            // 妥当なのは 2-8mm なので、そのままの強さだとコテ塗りのスタッコに見える。
            // タイリング周期を変えずに深さだけ落として粗さの印象を下げる。
            SetF(wall, "_NormalScale", 0.35f);                         // 旧 0.85
            // マット塗装でも斜めから見れば広い面のシーンが出る。上限 0.24 では
            // 完全な拡散反射に見え、全ての面が「紙」になっていた。
            Smoothness(wall, 0.08f, 0.34f);                            // 旧 0.05 - 0.24
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
            SetCol(baseb, "_BaseColor", Srgb(0.62f, 0.565f, 0.470f));   // 明るいビニル幅木
            // 旧 Srgb(0.40, 0.355, 0.300) = linear 0.133。壁の実効アルベド 0.45 に対し
            // 3.4 倍の差があり、実測でも壁の 1/17.8 (4.2 段下) の黒い帯になって
            // 部屋を線画のように縁取っていた。linear 0.34 まで上げて 1.35 段戻す。
            SetF(baseb, "_Metallic", 0f);
            SetF(baseb, "_Smoothness", 0.48f);   // 半艶のゴム。面取りに光が乗らないと黒い帯に見える
            Save(baseb);

            // ------------------------------------------------- ディフューザ（点灯）
            var on = Make("M_DiffuserOn");
            SetCol(on, "_BaseColor", Srgb(0.05f, 0.05f, 0.05f));
            SetF(on, "_Smoothness", 0.55f);
            Emissive(on, Srgb(1.00f, 0.985f, 0.905f), nits: 1800f);     // 4200K 相当のわずかに緑寄り
            Save(on);

            // ------------------------------------------------------- 開口枠
            // プレス鋼板の枠。真っ白だと単色の室内で浮くので、
            // 壁より明るい程度のベージュのエナメル塗装にする。
            var frame = Make("M_DoorFrame");
            SetCol(frame, "_BaseColor", Srgb(0.72f, 0.66f, 0.52f));
            SetF(frame, "_Metallic", 0f);
            SetF(frame, "_Smoothness", 0.42f);   // 半艶のエナメル
            Save(frame);

            // ---------------------------------------------------- デカール
            // タイリングするテクスチャには「その場所にしかない汚れ」を焼けない。
            // 2.12m ごとに同じ手形が並ぶことになるので、原因が場所に紐づく
            // 汚れは全てデカールで個別に置く。
            MakeDecal("M_Decal_Scuff",   "Decals/Decal_Scuff.png",   0.75f, smooth: 0.55f);
            MakeDecal("M_Decal_Drip",    "Decals/Decal_Drip.png",    0.80f, smooth: 0.30f);
            // 踏み倒された毛足は寝て光を返すので、通行帯だけ艶が上がる
            MakeDecal("M_Decal_Traffic", "Decals/Decal_Traffic.png", 0.70f, smooth: 0.40f);
            MakeDecal("M_Decal_Dent",    "Decals/Decal_Dent.png",    0.65f, smooth: 0.55f);

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

        static Material Make(string name) => Make(name, "HDRP/Lit");

        static Material Make(string name, string shaderName)
        {
            var shader = Shader.Find(shaderName);
            if (shader == null) { Log.Add("FATAL: シェーダが見つからない " + shaderName); return null; }
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

        /// <summary>
        /// HDRP/Decal のマテリアル。アルベドと平滑度だけに効かせ、法線は触らない。
        /// 法線まで足すと、下地の壁紙のエンボスと二重になって濁る。
        /// </summary>
        static void MakeDecal(string name, string tex, float blend, float smooth)
        {
            var m = Make(name, "HDRP/Decal");
            if (m == null) return;
            SetTex(m, "_BaseColorMap", Tex(tex));
            SetCol(m, "_BaseColor", Color.white);
            SetF(m, "_DecalBlend", blend);
            // 既定の 1 は「マスクマップの青」。マスクマップを与えていないので
            // 白 = 1 となり、平滑度が投影ボックス全体に一様にかかってしまう。
            // 0 = ベースカラーの α を被覆率として使う。
            SetF(m, "_MaskBlendSrc", 0f);
            SetF(m, "_NormalBlendSrc", 0f);
            SetF(m, "_AffectAlbedo", 1f);
            SetF(m, "_AffectNormal", 0f);
            SetF(m, "_AffectMetal", 0f);
            SetF(m, "_AffectAO", 0f);
            SetF(m, "_AffectSmoothness", 1f);
            SetF(m, "_Smoothness", smooth);
            Save(m);
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
