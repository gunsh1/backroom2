using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Backrooms.Post
{
    /// <summary>
    /// 1996 年の VHS-C で撮った記録映像に見せる。
    ///
    /// 演出として「汚す」のではなく、実機のテープと VCR が必ず持っている
    /// 制約をそのまま並べてある。
    ///   * 輝度は画面幅あたり 330 本程度しか記録できない
    ///   * 色差はさらに狭く、しかも復調が遅れて右にずれる
    ///   * VCR は失われた高域を持ち上げるので縁にリンギングが出る
    ///   * ヘッドの追従が遅れて走査線ごとに横へずれる
    ///   * 回転ヘッドの切り替えで下端が必ず乱れる
    ///   * 磁性体の欠けは白い破線になって流れる
    /// この帯域制限は同時に最適化でもある。最終出力がテープなら、
    /// 描画側の細部の粗はそもそも記録できない。
    ///
    /// HDRP の Global Settings の After Post Process にこの型名を
    /// 登録しないと呼ばれない。BackroomsSetup.ConfigureAll が入れる。
    /// </summary>
    [Serializable, VolumeComponentMenu("Post-processing/Custom/VHS")]
    public sealed class VhsPostProcess : CustomPostProcessVolumeComponent, IPostProcessComponent
    {
        [Tooltip("全体の掛かり具合。0 で素通し")]
        public ClampedFloatParameter intensity = new ClampedFloatParameter(0f, 0f, 1f);

        [Tooltip("縦の走査線数。NTSC の有効走査線は 486 本")]
        public ClampedFloatParameter lines = new ClampedFloatParameter(486f, 120f, 720f);

        [Tooltip("輝度の水平帯域。画面幅あたりの標本数")]
        public ClampedFloatParameter lumaBand = new ClampedFloatParameter(333f, 80f, 1000f);

        [Tooltip("色差の水平帯域。VHS は輝度の 1/4 以下")]
        public ClampedFloatParameter chromaBand = new ClampedFloatParameter(40f, 8f, 200f);

        [Tooltip("色のにじみと遅れ")]
        public ClampedFloatParameter chroma = new ClampedFloatParameter(1f, 0f, 1f);

        [Tooltip("彩度の落ち")]
        public ClampedFloatParameter desaturate = new ClampedFloatParameter(0.10f, 0f, 0.6f);

        [Tooltip("アパーチャ補正のリンギング")]
        public ClampedFloatParameter sharpen = new ClampedFloatParameter(0.30f, 0f, 1.5f);

        [Tooltip("走査線ごとの横ずれ")]
        public ClampedFloatParameter tape = new ClampedFloatParameter(0.45f, 0f, 3f);

        [Tooltip("トラッキングのずれ")]
        public ClampedFloatParameter tracking = new ClampedFloatParameter(0.5f, 0f, 3f);

        [Tooltip("ドロップアウト (白い破線) の出やすさ")]
        public ClampedFloatParameter dropout = new ClampedFloatParameter(0.35f, 0f, 3f);

        [Tooltip("下端のヘッドスイッチングノイズ")]
        public ClampedFloatParameter headSwitch = new ClampedFloatParameter(0.7f, 0f, 2f);

        [Tooltip("走査線の縞とインターレース")]
        public ClampedFloatParameter scanline = new ClampedFloatParameter(0.25f, 0f, 1f);

        public ClampedFloatParameter grain = new ClampedFloatParameter(0.5f, 0f, 2f);

        Material _mat;

        public bool IsActive() => _mat != null && intensity.value > 0f;

        public override CustomPostProcessInjectionPoint injectionPoint
            => CustomPostProcessInjectionPoint.AfterPostProcess;

        public override void Setup()
        {
            var sh = Shader.Find("Hidden/Backrooms/Vhs");
            if (sh != null)
            {
                _mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                Debug.Log("[VHS] シェーダを読み込んだ: " + sh.name);
            }
            else Debug.LogWarning("[VHS] Hidden/Backrooms/Vhs が見つからない。"
                                  + "Always Included Shaders に入っているか確認する");
        }

        public override void Render(CommandBuffer cmd, HDCamera camera,
                                    RTHandle source, RTHandle destination)
        {
            if (_mat == null) { HDUtils.BlitCameraTexture(cmd, source, destination); return; }
            _mat.SetFloat(ID.Intensity, intensity.value);
            _mat.SetFloat(ID.Lines, lines.value);
            _mat.SetFloat(ID.LumaBand, lumaBand.value);
            _mat.SetFloat(ID.ChromaBand, chromaBand.value);
            _mat.SetFloat(ID.Chroma, chroma.value);
            _mat.SetFloat(ID.Desat, desaturate.value);
            _mat.SetFloat(ID.Sharpen, sharpen.value);
            _mat.SetFloat(ID.Tape, tape.value);
            _mat.SetFloat(ID.Tracking, tracking.value);
            _mat.SetFloat(ID.Dropout, dropout.value);
            _mat.SetFloat(ID.HeadSwitch, headSwitch.value);
            _mat.SetFloat(ID.Scanline, scanline.value);
            _mat.SetFloat(ID.Grain, grain.value);
            _mat.SetTexture(ID.MainTex, source);
            HDUtils.DrawFullScreen(cmd, _mat, destination, shaderPassId: 0);
        }

        public override void Cleanup() => CoreUtils.Destroy(_mat);

        static class ID
        {
            public static readonly int MainTex = Shader.PropertyToID("_MainTex");
            public static readonly int Intensity = Shader.PropertyToID("_Intensity");
            public static readonly int Lines = Shader.PropertyToID("_Lines");
            public static readonly int LumaBand = Shader.PropertyToID("_LumaBand");
            public static readonly int ChromaBand = Shader.PropertyToID("_ChromaBand");
            public static readonly int Chroma = Shader.PropertyToID("_Chroma");
            public static readonly int Desat = Shader.PropertyToID("_Desat");
            public static readonly int Sharpen = Shader.PropertyToID("_Sharpen");
            public static readonly int Tape = Shader.PropertyToID("_Tape");
            public static readonly int Tracking = Shader.PropertyToID("_Tracking");
            public static readonly int Dropout = Shader.PropertyToID("_Dropout");
            public static readonly int HeadSwitch = Shader.PropertyToID("_HeadSwitch");
            public static readonly int Scanline = Shader.PropertyToID("_Scanline");
            public static readonly int Grain = Shader.PropertyToID("_Grain");
        }
    }
}
