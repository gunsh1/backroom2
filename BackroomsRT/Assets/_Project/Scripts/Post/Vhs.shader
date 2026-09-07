Shader "Hidden/Backrooms/Vhs"
{
    HLSLINCLUDE

    #pragma target 4.5
    #pragma only_renderers d3d11 d3d12 playstation xboxone xboxseries vulkan metal switch

    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/PostProcessing/Shaders/RTUpscale.hlsl"

    struct Attributes
    {
        uint vertexID : SV_VertexID;
        UNITY_VERTEX_INPUT_INSTANCE_ID
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float2 texcoord   : TEXCOORD0;
        UNITY_VERTEX_OUTPUT_STEREO
    };

    Varyings Vert(Attributes input)
    {
        Varyings output;
        UNITY_SETUP_INSTANCE_ID(input);
        UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
        output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
        output.texcoord   = GetFullScreenTriangleTexCoord(input.vertexID);
        return output;
    }

    TEXTURE2D_X(_MainTex);

    float  _Intensity;
    float  _Lines;          // 縦の走査線数 (NTSC の有効走査線 486 本)
    float  _LumaBand;       // 輝度の水平帯域 (画面幅あたりの標本数)
    float  _ChromaBand;     // 色差の水平帯域。VHS は輝度の 1/4 以下
    float  _Sharpen;        // VCR のアパーチャ補正が作る縁のリンギング
    float  _Tape;           // 走査線ごとの横ずれ
    float  _Dropout;        // ドロップアウト (白い破線)
    float  _HeadSwitch;     // 下端のヘッドスイッチングノイズ
    float  _Tracking;       // トラッキングのずれ
    float  _Grain;
    float  _Scanline;
    float  _Chroma;         // 色のにじみと遅れ
    float  _Desat;
    float  _Fisheye;        // 広角レンズの樽型歪み
    float  _Pillar;         // 4:3 に切り詰めるか

    // テープの縦横比。VHS-C は 4:3 しかない。
    #define TAPE_ASPECT (4.0 / 3.0)

    float Hash21(float2 p)
    {
        p = frac(p * float2(123.34, 456.21));
        p += dot(p, p + 45.32);
        return frac(p.x * p.y);
    }

    float Hash11(float x)
    {
        return frac(sin(x * 127.1) * 43758.5453);
    }

    // 注入点によっては入ってくる値が 1 を大きく超える。そのまま差分を取ると
    // 器具のように明るい所で補正項が桁違いになり、明暗が反転する。上限を切る。
    float3 Sample(float2 uv)
    {
        uv = clamp(uv, 0.0, 1.0);
        float3 c = SAMPLE_TEXTURE2D_X_LOD(_MainTex, s_linear_clamp_sampler,
                       ClampAndScaleUVForBilinearPostProcessTexture(uv), 0).xyz;
        return clamp(c, 0.0, 4.0);
    }

    // RGB <-> YIQ。VHS は輝度と色差を別々の帯域で記録するので、
    // この空間に移さないと「色だけがにじむ」が作れない。
    float3 ToYiq(float3 c)
    {
        return float3(dot(c, float3(0.299, 0.587, 0.114)),
                      dot(c, float3(0.596, -0.274, -0.322)),
                      dot(c, float3(0.211, -0.523, 0.312)));
    }
    float3 ToRgb(float3 y)
    {
        return float3(y.x + 0.956 * y.y + 0.621 * y.z,
                      y.x - 0.272 * y.y - 0.647 * y.z,
                      y.x - 1.106 * y.y + 1.703 * y.z);
    }

    float4 CustomPostProcess(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
        float2 uv = input.texcoord;
        float t = _TimeParameters.x;
        float k = _Intensity;

        // ---- 4:3 に切り詰める ---------------------------------------
        // テープは 4:3 でしか記録できない。画面がそれより横長なら
        // 両脇は「映像が無い」ので黒。カメラ側は 4:3 の画角で描いて
        // あるので、ここで中央の 4:3 の窓に詰め直すと比率が戻る。
        if (_Pillar > 0.5)
        {
            float srcAspect = _ScreenSize.x * _ScreenSize.w;
            if (srcAspect > TAPE_ASPECT + 0.002)
            {
                float f = TAPE_ASPECT / srcAspect;
                float xw = (uv.x - 0.5) / f + 0.5;
                if (xw < 0.0 || xw > 1.0) return float4(0.0, 0.0, 0.0, 1.0);
                uv.x = xw;
            }
        }

        // ---- 走査線の番号。以降のノイズは全部この単位で起きる ----
        float scan = floor(uv.y * _Lines);
        float lineRnd = Hash21(float2(scan, floor(t * 29.97)));

        // ---- トラッキングと、走査線ごとの横ずれ ----------------------
        // テープの伸びとヘッドの追従の遅れ。1 本ずつ独立にずれるので
        // 縦の直線がぎざぎざになる。これが VHS で最も目に付く手掛かり。
        float2 duv = uv;
        float track = (Hash11(floor(t * 3.1)) - 0.5) * 0.004 * _Tracking;
        duv.y += track * k;

        float burst = saturate(Hash11(floor(t * 2.3) + scan * 0.017) * 1.35 - 1.0);
        float jitter = (lineRnd - 0.5) * (0.0012 + burst * 0.02) * _Tape;
        duv.x += jitter * k;

        // ---- 下端のヘッドスイッチングノイズ --------------------------
        // 回転ヘッドが切り替わる瞬間は映像が無い。実機では必ず下端に出る。
        float hs = saturate((0.055 - uv.y) / 0.055);
        if (hs > 0.0)
        {
            float amt = hs * hs * _HeadSwitch * k;
            duv.x += (Hash21(float2(scan, floor(t * 60.0))) - 0.5) * 0.35 * amt;
            duv.y += 0.004 * amt;
        }

        // ---- レンズの樽型歪み ---------------------------------------
        // 家庭用カムコーダの広角端は、まっすぐな線が中央に向かって膨らむ。
        // 画角そのものは変えず、角がちょうど元の位置に戻るように正規化して
        // あるので、端に「元画像の外」を要求する穴は空かない。
        if (_Fisheye > 0.0001)
        {
            float2 c = float2((duv.x - 0.5) * TAPE_ASPECT, duv.y - 0.5);
            const float cornerR2 = TAPE_ASPECT * TAPE_ASPECT * 0.25 + 0.25;
            float m = (1.0 + _Fisheye * dot(c, c)) / (1.0 + _Fisheye * cornerR2);
            c *= m;
            duv = float2(c.x / TAPE_ASPECT + 0.5, c.y + 0.5);
        }

        // ---- 輝度の水平帯域 -----------------------------------------
        // VHS の輝度は画面幅あたり 330 本程度しか記録できない。
        // 帯域を落とすぶん、細部の粗も一緒に消える。
        float lumaStep = 1.0 / max(_LumaBand, 16.0);
        float3 y0 = ToYiq(Sample(duv));
        float3 ym1 = ToYiq(Sample(duv - float2(lumaStep, 0)));
        float3 yp1 = ToYiq(Sample(duv + float2(lumaStep, 0)));
        float luma = (ym1.x + y0.x * 2.0 + yp1.x) * 0.25;
        luma = lerp(y0.x, luma, k);

        // ---- アパーチャ補正のリンギング -------------------------------
        // VCR は失われた高域を無理に持ち上げるので、明暗の境の左右に
        // 明るい縁と暗い縁が付く。これがあると一気に「テープ」に見える。
        float2 sh = float2(lumaStep * 2.0, 0);
        float halo = clamp(ToYiq(Sample(duv - sh)).x - ToYiq(Sample(duv + sh)).x, -0.35, 0.35);
        luma += halo * _Sharpen * k;

        // ---- 色差の水平帯域と遅れ ------------------------------------
        // 色は輝度よりさらに狭い帯域で、しかも復調の遅れで右にずれる。
        float cStep = 1.0 / max(_ChromaBand, 4.0);
        float2 iq = 0;
        [unroll] for (int s = -3; s <= 3; s++)
        {
            float3 c = ToYiq(Sample(duv + float2(cStep * s - cStep * 1.2, 0)));
            iq += c.yz;
        }
        iq /= 7.0;
        iq = lerp(y0.yz, iq, k * _Chroma);
        iq *= lerp(1.0, 1.0 - _Desat, k);

        float3 col = ToRgb(float3(luma, iq));

        // ---- ドロップアウト -----------------------------------------
        // 磁性体の欠けた所は信号が拾えず、白い破線になって流れる。
        float doSeed = Hash21(float2(scan, floor(t * 8.0)));
        if (doSeed > 1.0 - _Dropout * k * 0.02)
        {
            float x0 = Hash21(float2(scan + 7.0, floor(t * 8.0)));
            float w = 0.02 + 0.10 * Hash21(float2(scan + 13.0, floor(t * 8.0)));
            float inSeg = step(x0, uv.x) * step(uv.x, x0 + w);
            col = lerp(col, float3(0.86, 0.86, 0.82), inSeg * 0.9);
        }

        // ---- 走査線とインターレース ----------------------------------
        // 29.97fps の 2 フィールド。細かい横縞が 1 本おきに動く。
        float field = fmod(floor(t * 59.94), 2.0);
        float sl = 1.0 - _Scanline * k * 0.5 * (1.0 - cos(uv.y * _Lines * TWO_PI)) * 0.5;
        float inter = 1.0 - _Scanline * k * 0.25 * step(0.5, frac((scan + field) * 0.5));
        col *= sl * inter;

        // ---- 粒子 ---------------------------------------------------
        float g = Hash21(uv * float2(1131.0, 977.0) + frac(t) * 71.0) - 0.5;
        col += g * _Grain * k * 0.12;

        // ヘッドスイッチの帯そのものは映像が乱れて白っぽく飽和する
        col = lerp(col, col * 0.6 + 0.30, hs * hs * _HeadSwitch * k * 0.8);

        return float4(max(col, 0.0), 1.0);
    }

    ENDHLSL

    SubShader
    {
        Tags{ "RenderPipeline" = "HDRenderPipeline" }
        Pass
        {
            Name "Vhs"
            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off

            HLSLPROGRAM
                #pragma fragment CustomPostProcess
                #pragma vertex Vert
            ENDHLSL
        }
    }
    Fallback Off
}
