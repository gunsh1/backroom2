namespace Backrooms.Audio
{
    /// <summary>
    /// 音量の基準。
    ///
    /// 一つずつ耳で決めると全体のバランスが取れない。実際うるさすぎた。
    /// 相対値は実測の音圧レベルから出す。オフィスでの代表値は
    ///     カーペット上の足音 (耳の位置)      48 dBA
    ///     空調の吹き出し (NC-35 のオフィス)  38 dBA
    ///     磁気安定器 1 台 2.5m               32 dBA
    ///     部屋の暗騒音                       30 dBA
    /// 足音を 0dB としたときの相対は -10 / -16 / -18 dB になる。
    ///
    /// 絶対の音量を決めるのは Output ひとつだけ。カムコーダの AGC が
    /// 最後に均してしまうので、ここで実際に効くのは比と、AGC の目標値。
    /// 前は目標が 0.14 rms で、静かな部屋を音楽並みまで持ち上げていた。
    /// </summary>
    public static class Mix
    {
        /// <summary>足音。基準 (0 dB)。</summary>
        public const float Step = 0.110f;

        /// <summary>空調。-10 dB。</summary>
        public const float Hvac = 0.035f;

        /// <summary>
        /// 蛍光灯 1 台。導出は -16dB = 0.017 だが、実際には近くの数台が
        /// 非同期に足し合わさるうえ、これが Backrooms で一番効く音なので
        /// +1.5dB だけ上に置いている。
        /// </summary>
        public const float Ballast = 0.020f;

        /// <summary>部屋の暗騒音。-18 dB。</summary>
        public const float Tone = 0.014f;

        /// <summary>
        /// 最終的な音量 = AGC の目標 rms。うるさい／小さいはここだけ動かす。
        ///
        /// 0.012 は -38 dBFS。最初は 0.14 (-17 dBFS) で、静かな部屋を
        /// 音楽並みまで持ち上げていた。0.028 にしたら今度は音源側が
        /// 下がりすぎて AGC が上限に張り付き、まったく動かなくなった。
        /// 張り付いた AGC はただの固定利得で、カムコーダらしさが消える。
        /// 素の混合が 0.0035 rms なので、3.4 倍で届く 0.012 に置く。
        /// </summary>
        public const float Output = 0.012f;

        /// <summary>テープのヒス。出力を下げたぶん一緒に下げる。</summary>
        public const float Hiss = 0.0010f;

        /// <summary>
        /// AGC が掛けられる最大の利得。目標に届く値より上に置くこと。
        /// 届かないと常に上限に張り付き、足音で絞られることもなくなる。
        /// </summary>
        public const float AgcMaxGain = 5.0f;
    }
}
