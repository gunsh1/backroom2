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
            // 参照と同じ構図で撮って測ると、塊の面が全高で 0.15 暗かった
            // (参照 0.649 / こちら 0.500)。1.09 倍にしたら +0.078 行き過ぎたので 0.99 倍で戻す。
            SetCol(wall, "_BaseColor", Srgb(0.870f, 0.829f, 0.593f));
            // 青を 3 回上げたのは、暖色の黒体光源が青を食っていた分の埋め合わせ
            // だった。光源を蛍光灯の実測色にした今、それが二重に効いて壁が
            // 白くなった。参照から逆算して戻す:
            //   render_B/R = albedo_B/R^1.3 x lamp_B/R という実測どおりの関係から
            //   目標 0.394 / lamp 0.822 -> albedo_B/R = 0.568 (現 0.682)
            //   同様に R/G は albedo 1.021 (現 1.083)
            // 実効アルベド (0.520, 0.509, 0.295)。
            // 露出を +3 EV にしたら ACES の脱色が効いて B/R が 0.26 -> 0.44 まで
            // 自力で上がった。残りは実測の応答 (B/R_render はベース色の青の
            // およそ 0.55 乗で動く) から逆算し、青を linear 0.390 -> 0.573 へ。
            // 予測 B/R = 0.54。
            // 参照の 0.65 まで行くには壁紙をほぼ白にする必要があり、その場合
            // 黄色は全て照明と GI が作ることになる。行き過ぎを避けて一段手前で止める。
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
            // 参照と同じ構図で測ると +0.054 明るかった (参照 0.523 / こちら 0.577)
            SetCol(carpet, "_BaseColor", Srgb(0.726f, 0.662f, 0.509f));
            // 参照のカーペットは B/R 0.102 / R/G 1.461。Carpet011 の素の色が
            // 既に linear (0.400, 0.344, 0.115) と十分黄色いので、ティントは
            // ほぼ中性にして素材そのものに色を作らせる。
            // 参照では床は壁より暗い。こちらは器具の真下で受けるぶん
            // Y=0.639 と壁 (0.508) より明るくなっていた。
            // 輝度アルベド 0.247 -> 0.167 (業務用カーペットの実測 15-20% の範囲)。
            SetF(carpet, "_NormalScale", 1.0f);
            // 毛足には向きがある。見る角度で明るさが変わるのが本物の手掛かりで、
            // 等方的な粒のままだと「床のテクスチャ」にしか見えない。
            // UV はワールド XZ 投影なので、接線はワールド X に揃う
            // = 一方向に敷いたロールカーペットと同じになる。
            SetF(carpet, "_MaterialID", 2f);      // 2 = Anisotropy
            SetF(carpet, "_Anisotropy", 0.60f);
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
            // 9m 周期のディテールを 0.80 の強さで掛けていたので、床に 10m 級の
            // 淡い斑が出て「濡れた床」に見えていた。低周波の情報自体は要るので
            // 消さずに、目に付かない強さまで落とす。
            Detail(carpet, detail, uvScale: 2.00f, meters: 9f, albedo: 0.28f, smooth: 0.25f);
            Save(carpet);

            // ------------------------------------------------------ 天井タイル
            // ambientCG の写真 1 枚を 6x6 に切って使っていたが、元が「タイル 1 枚」の
            // 撮影なので、切り出したセルの中にタイルの縁が無く、1 枚としての
            // まとまりも無かった。実物の鉱物繊維タイルを構成する要素
            // （押し出し方向の亀裂・針穴・グリッドの隙間を空気が抜けることで
            // 外周だけが吸う埃・雨漏りの潮目）を持つ 8x8 のアトラスを自前で焼く。
            // tools/make-ceiling-textures.py
            void CeilingTile(string name, float fillNits)
            {
                var c = Make(name);
                SetTex(c, "_BaseColorMap", Tex("OfficeCeiling001/CeilingTile_Atlas_Color.png"));
                SetTex(c, "_NormalMap", Tex("OfficeCeiling001/CeilingTile_Atlas_NormalGL.png"));
                SetTex(c, "_MaskMap", Tex("OfficeCeiling001/CeilingTile_Atlas_MaskMap.png"));
                SetCol(c, "_BaseColor", Srgb(0.834f, 0.739f, 0.564f));
                // 亀裂は実測で幅 2-4mm、深さ 1mm 程度。0.5 では波打った漆喰に見えた。
                SetF(c, "_NormalScale", 0.25f);
                Smoothness(c, 0.05f, 0.20f);
                SetF(c, "_AORemapMin", 0.45f);
                if (fillNits > 0f) Emissive(c, Srgb(1.00f, 0.974f, 0.735f), nits: fillNits);
                Save(c);
            }
            // 補填の 2 nits は「埋めるべき光がある所」にだけ入れる。
            // 蛍光灯が全部落ちている区画にまで掛けると、真っ暗であるべき部屋の
            // 天井だけが光り続ける。
            CeilingTile("M_CeilingTile", 2f);
            CeilingTile("M_CeilingTileDark", 0f);

            // 参照から逆算した実効アルベド linear (0.5255, 0.3633, 0.1702) を保つ。
            // 新しいアトラスの素の明度は linear (0.793, 0.720, 0.612) で、
            // 旧テクスチャ (0.931, 0.916, 0.906) より暗く黄色いぶんティントは薄い。
            // 値は CeilingTile() の中にまとめてある。

            // ------------------------------------------------------ 天井裏
            // タイルが 1 枚抜けた穴から見える。断熱材と鋼製デッキの裏は
            // ほぼ光が入らないので、実効反射率 2% 程度の黒。
            var plenum = Make("M_Plenum");
            SetCol(plenum, "_BaseColor", Srgb(0.16f, 0.15f, 0.13f));
            SetF(plenum, "_Metallic", 0f);
            SetF(plenum, "_Smoothness", 0.05f);
            Save(plenum);

            // -------------------------------------------- T バー／トロファー枠
            var metal = Make("M_GridMetal");
            SetCol(metal, "_BaseColor", Srgb(0.845f, 0.815f, 0.560f));  // 黄ばんだ白塗装鋼板
            // 実測では T バーはタイルの 0.45 段下で、これは実物どおり。
            // ただし素の反射率が linear 0.53 と低かったので、白塗装の 0.69 に寄せる。
            SetF(metal, "_Metallic", 0f);
            SetF(metal, "_Smoothness", 0.30f);   // 埃を被った塗装は光らない
            // T バーも天井タイルと同じ「真下を向いた面」で、RTGI が運ぶ間接光が
            // 同じだけ足りない。タイルにだけ 10 nits の補填を入れていたので、
            // 補填の無いバーが取り残されて実測でタイルの 1.7 段下 (比 0.31) の
            // 黒い格子になっていた。実物の T バーはタイルの 0.45 段下 (比 0.73)。
            // 補填のうちアルベド比で見合う分を戻す: 10 nits x 0.47 = 5 nits。
            Emissive(metal, Srgb(1.00f, 0.974f, 0.735f), nits: 1f);   // タイルの 2 nits にアルベド比で見合う量
            Save(metal);

            // 暗黒区画用。補填を外しただけの同じ塗装鋼板。
            var metalDark = Make("M_GridMetalDark");
            SetCol(metalDark, "_BaseColor", Srgb(0.845f, 0.815f, 0.560f));
            SetF(metalDark, "_Metallic", 0f);
            SetF(metalDark, "_Smoothness", 0.30f);
            Save(metalDark);

            // ---------------------------------------------------------- 幅木
            var baseb = Make("M_Baseboard");
            // 参照と同じ構図で測ると 0.599 に対し 0.473 と暗かった。
            // ティントを上げられるだけ上げる (それでも -0.08 残る)。
            SetCol(baseb, "_BaseColor", Srgb(0.95f, 0.923f, 0.549f));   // 淡いタンのビニル幅木
            // 参照の幅木は壁とほとんど見分けがつかない淡いタン。
            // linear 0.342 -> 0.603 (+0.82 段)。残る差は床際の接地 AO なので
            // アルベドで詰められるのはここまで。
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
            // 参照と同じ露出で撮ると画面の 2.0% が飛んでいた (参照 0.001%)。
            // 飛んだ画素は情報が失われるので、記録系では戻せない。
            Emissive(on, Srgb(1.00f, 0.974f, 0.735f), nits: 700f);
            // 参照の器具は Y=0.971 で白飛びしていない (白飛び率 0.00%)。
            // 1800 nits では露出 +2.2 EV で 2.5% 飛んでいたので落とす。
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
            // 掃除機の筋は色ではなく艶の縞。アルベドには効かせない。
            MakeDecal("M_Decal_Vacuum",  "Decals/Decal_Vacuum.png",  0.60f, smooth: 0.44f, albedo: false);

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
        static void MakeDecal(string name, string tex, float blend, float smooth, bool albedo = true)
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
            SetF(m, "_AffectAlbedo", albedo ? 1f : 0f);
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
