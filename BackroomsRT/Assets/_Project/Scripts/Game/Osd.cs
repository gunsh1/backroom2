namespace Backrooms.Game
{
    /// <summary>
    /// ビューファインダに出す値の受け渡し。
    ///
    /// VhsPostProcess は VolumeComponent なので、シーンの中の物を参照
    /// できない。値だけを静的に置いて、TapeState が書き、シェーダ側が読む。
    ///
    /// Enabled を切れるようにしてあるのは参照画像のため。VHS パスは
    /// AfterPostProcess なので、切らないと artifacts/shots/ の全部に
    /// タイムコードが写り込み、見た目の前後比較が使えなくなる。
    /// </summary>
    public static class Osd
    {
        public static bool Enabled = true;
        public static int Hours, Minutes, Seconds;
        public static float Tape01 = 1f;
        public static bool Rec = true;
    }
}
