using UnityEngine;

namespace Backrooms.Game
{
    /// <summary>
    /// 差し込む実写を VHS のパスへ渡すだけの置き場。
    ///
    /// Osd と同じ事情でここに在る。VhsPostProcess は VolumeComponent なので
    /// シーンの中の MonoBehaviour を直接は掴めない。値を静的な場所に置いて、
    /// 描く側が読みに来る。
    ///
    /// 実写はシーンの上に重ねるのではなく、シーンと「差し替える」。
    /// 合成の位置は Sample() の中、つまりテープの処理より前なので、
    /// 帯域制限もドロップアウトもグレインも同じように掛かる。
    /// ダビングされた 1 世代前の映像に見えるのが正しい。
    /// </summary>
    public static class IntroFeed
    {
        /// <summary>0 で素通し、1 で実写に差し替わる。</summary>
        public static float Amount;

        /// <summary>映す絵。VideoPlayer の出力先。</summary>
        public static Texture Tex;

        /// <summary>
        /// 4:3 の窓の中での置き方。uv' = (uv - zw) / xy。
        /// 素材が 4:3 でなければ、はみ出さずに内側へ収めて余りを黒にする。
        /// </summary>
        public static Vector4 Fit = new Vector4(1f, 1f, 0f, 0f);

        /// <summary>素材の縦横比から窓への収め方を決める。</summary>
        public static Vector4 FitFor(int w, int h)
        {
            const float win = 4f / 3f;
            float a = (w > 0 && h > 0) ? (float)w / h : win;
            float sx = 1f, sy = 1f;
            if (a > win) sy = win / a;        // 横長 -> 上下に黒帯
            else if (a < win) sx = a / win;   // 縦長 -> 左右に黒帯
            return new Vector4(sx, sy, (1f - sx) * 0.5f, (1f - sy) * 0.5f);
        }

        public static void Clear()
        {
            Amount = 0f;
            Tex = null;
            Fit = new Vector4(1f, 1f, 0f, 0f);
        }
    }
}
