using UnityEditor;
using UnityEngine;

namespace Backrooms.EditorTools
{
    /// <summary>
    /// _Project/Art/Textures 以下のインポート設定を自動で正す。
    /// ノーマルマップの種別と sRGB フラグを間違えると、何をしても正しい絵にならない。
    /// </summary>
    public class TextureImportRules : AssetPostprocessor
    {
        const string Root = "_Project/Art/Textures";

        void OnPreprocessTexture()
        {
            if (!assetPath.Replace('\\', '/').Contains(Root)) return;
            var ti = (TextureImporter)assetImporter;

            ti.maxTextureSize = 2048;
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
            ti.wrapMode = TextureWrapMode.Repeat;
            ti.mipmapEnabled = true;
            // 床を浅い角度で見たときのボケを抑える。カーペットに効く。
            ti.anisoLevel = 8;
            ti.filterMode = FilterMode.Trilinear;

            string f = System.IO.Path.GetFileNameWithoutExtension(assetPath);

            if (f.Contains("_Normal"))
            {
                ti.textureType = TextureImporterType.NormalMap;
                ti.sRGBTexture = false;
            }
            else if (f.Contains("_Color") || f.StartsWith("Decal_"))
            {
                ti.textureType = TextureImporterType.Default;
                ti.sRGBTexture = true;
                // デカールは α を被覆率として使う。ここを落とすと
                // 投影ボックスの矩形がそのまま画面に出る。
                if (f.StartsWith("Decal_"))
                {
                    ti.alphaSource = TextureImporterAlphaSource.FromInput;
                    ti.alphaIsTransparency = true;
                }
            }
            else
            {
                // MaskMap / Roughness / AO / Displacement / DetailVariation はデータ。
                ti.textureType = TextureImporterType.Default;
                ti.sRGBTexture = false;
                if (f.Contains("MaskMap") || f.Contains("Detail"))
                    ti.alphaSource = TextureImporterAlphaSource.FromInput;
            }
        }
    }
}
