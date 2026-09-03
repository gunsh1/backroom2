using UnityEngine;

namespace Backrooms.Generation
{
    /// <summary>Level 0 の生成に使うマテリアル一式。</summary>
    [System.Serializable]
    public class MaterialSet
    {
        public Material Wallpaper;
        public Material Carpet;
        public Material CeilingTile;
        public Material CeilingTileDark;   // 明かりが落ちた区画。補填の自発光なし
        public Material Metal;        // T バー・壁見切り・トロファー枠
        public Material MetalDark;
        public Material Baseboard;
        public Material DiffuserOn;
        public Material DiffuserOff;
        public Material Chrome;    // スプリンクラー
        public Material Plastic;   // 感知器・コンセント
        public Material DoorFrame; // 開口の三方枠
        public Material Plenum;    // 天井裏。タイルが落ちた穴の中
    }

    /// <summary>
    /// 実寸の建築部品。寸法は実在のオフィス建築に合わせてある。
    /// ここを外すと何をしても CG に見える。
    /// </summary>
    public static class Level0Builder
    {
        // ---- 天井 ----------------------------------------------------------
        public const float Cell = 0.60f;      // 天井グリッド 600mm
        public const float TBarW = 0.024f;    // T バー見付 24mm (15/16")
        public const float TBarH = 0.038f;    // T バー成 38mm (1-1/2")。天井裏側なので見えない
        /// <summary>
        /// タイル面は T バーの見付面より「フランジの板厚ぶんだけ上」にある。
        /// 実物のフランジは 1.5mm 前後の鋼板で、タイルはその上に落とし込まれるだけ。
        /// ここを 14mm にしていたとき、下から見るとバーの側面 14mm が余分に
        /// 見え、線幅が実質 1.6 倍に太って格天井のように見えていた。
        /// </summary>
        public const float TileReveal = 0.0016f;
        public const float AngleW = 0.024f;   // 壁見切りアングルの水平脚 24mm
        public const float AngleH = 0.024f;   // 同 垂直脚

        // ---- 躯体 ----------------------------------------------------------
        public const float WallT = 0.15f;     // 壁厚 150mm（開口の見込みが出る）
        public const float BulkT = 0.10f;     // 天井段差の垂れ壁の厚み
        public const float BaseH = 0.10f;     // 幅木 100mm
        public const float BaseT = 0.018f;    // 幅木の出 18mm
        public const float WallTop = 3.20f;   // 壁の上端（最も高い天井 3.00 + 0.20）

        // ---- 面取り量（実在の建築に数学的な直角は存在しない）------------------
        public const float BevelWall = 0.004f;   // 塗装が盛られた石膏ボードのコーナービード
        public const float BevelBase = 0.002f;   // ビニル幅木の角
        public const float BevelFrame = 0.0015f; // 器具の枠

        // ---- UV スケール（テクスチャ 1 周期のワールド寸法）-------------------
        public const float UvCarpet = 2.00f;
        public const float UvWall = 2.12f;   // 壁紙ロール幅 0.53m x 4
        public const float UvMetal = 1.00f;
        /// <summary>壁の V 軸。床際の汚れを実際の高さに固定するため 2.90m を守る。</summary>
        public const float UvWallV = 2.90f;

        // ------------------------------------------------------ 幾何の不完全さ

        /// <summary>壁の反り。波長 5m 前後で最大 4mm、中間高さで最も反る。</summary>
        public static Vector3 WallBow(Vector3 p)
        {
            float h = Mathf.Sin(Mathf.Clamp01(p.y / 2.70f) * Mathf.PI);
            float dx = Mathf.Sin(p.z * 1.27f + 0.6f) * 0.004f * h;
            float dz = Mathf.Sin(p.x * 1.13f + 2.1f) * 0.004f * h;
            return new Vector3(dx, 0f, dz);
        }

        /// <summary>吊りボルト間の垂れ。2.4m 周期で最大 2.2mm 下がる。</summary>
        public static Vector3 CeilingSag(Vector3 p)
        {
            const float K = 2f * Mathf.PI / 2.4f;
            float s = (1f - Mathf.Cos(p.x * K)) * 0.5f * (1f - Mathf.Cos(p.z * K)) * 0.5f;
            return new Vector3(0f, -0.0022f * s, 0f);
        }

        // -------------------------------------------------------------- 開口枠

        /// <summary>
        /// プレス鋼板の三方枠。見込みを板で覆い、両面に見付を返す。
        /// これが無いと開口が「壁を切り抜いた穴」に見え、
        /// 見込み面が素の壁材のまま暗く落ちる。
        /// axis=true なら Z 固定の壁（開口は X 方向に開く）。
        /// </summary>
        public static void OpeningFrame(MeshBuilder m, bool zWall, float a0, float a1,
                                        float h, float fixedPos)
        {
            const float Lin = 0.016f;   // 見込みを覆う板厚
            const float Face = 0.062f;  // 見付の出幅
            const float Proud = 0.014f; // 壁面からの出
            float p0 = fixedPos - WallT * 0.5f, p1 = fixedPos + WallT * 0.5f;

            void Bx(float u0, float y0, float v0, float u1, float y1, float v1)
            {
                var min = zWall ? new Vector3(u0, y0, v0) : new Vector3(v0, y0, u0);
                var max = zWall ? new Vector3(u1, y1, v1) : new Vector3(v1, y1, u1);
                m.BeveledBox(min, max, BevelFrame, 1f);
            }

            // 縦枠（見込みの板）と上枠
            Bx(a0, 0f, p0, a0 + Lin, h, p1);
            Bx(a1 - Lin, 0f, p0, a1, h, p1);
            Bx(a0, h - Lin, p0, a1, h, p1);

            // 見付（両面への返し）
            for (int s = 0; s < 2; s++)
            {
                float f0 = s == 0 ? p0 - Proud : p1;
                float f1 = s == 0 ? p0 : p1 + Proud;
                Bx(a0 - Face, 0f, f0, a0 + Lin, h + Face, f1);
                Bx(a1 - Lin, 0f, f0, a1 + Face, h + Face, f1);
                Bx(a0 - Face, h - Lin, f0, a1 + Face, h + Face, f1);
            }
        }

        public enum Axis { X, Z }

        /// <summary>壁付けのプレート。dir は室内側を向く符号。</summary>
        public static void AddPlate(MeshBuilder m, Vector3 p, Axis axis, float dir, float w, float h)
        {
            const float Proud = 0.010f;
            if (axis == Axis.X)
            {
                float x0 = dir > 0 ? p.x : p.x - Proud;
                m.BeveledBox(new Vector3(x0, p.y - h * 0.5f, p.z - w * 0.5f),
                             new Vector3(x0 + Proud, p.y + h * 0.5f, p.z + w * 0.5f), 0.0015f, 0.25f);
            }
            else
            {
                float z0 = dir > 0 ? p.z : p.z - Proud;
                m.BeveledBox(new Vector3(p.x - w * 0.5f, p.y - h * 0.5f, z0),
                             new Vector3(p.x + w * 0.5f, p.y + h * 0.5f, z0 + Proud), 0.0015f, 0.25f);
            }
        }

        /// <summary>2 口コンセント。onXWall=true なら X 固定の壁に、false なら Z 固定の壁に付ける。</summary>
        public static void AddOutlet(MeshBuilder m, Vector3 p, bool onXWall)
        {
            const float PlateW = 0.070f, PlateH = 0.115f, Proud = 0.004f;
            if (onXWall)
            {
                m.BeveledBox(new Vector3(p.x, p.y - PlateH * 0.5f, p.z - PlateW * 0.5f),
                             new Vector3(p.x + Proud, p.y + PlateH * 0.5f, p.z + PlateW * 0.5f), 0.0015f, 0.25f);
                for (int i = -1; i <= 1; i += 2)
                {
                    float cy = p.y + i * 0.019f;
                    m.BeveledBox(new Vector3(p.x + Proud - 0.001f, cy - 0.011f, p.z - 0.012f),
                                 new Vector3(p.x + Proud + 0.0005f, cy + 0.011f, p.z + 0.012f), 0.0004f, 0.25f);
                }
            }
            else
            {
                m.BeveledBox(new Vector3(p.x - PlateW * 0.5f, p.y - PlateH * 0.5f, p.z),
                             new Vector3(p.x + PlateW * 0.5f, p.y + PlateH * 0.5f, p.z + Proud), 0.0015f, 0.25f);
                for (int i = -1; i <= 1; i += 2)
                {
                    float cy = p.y + i * 0.019f;
                    m.BeveledBox(new Vector3(p.x - 0.012f, cy - 0.011f, p.z + Proud - 0.001f),
                                 new Vector3(p.x + 0.012f, cy + 0.011f, p.z + Proud + 0.0005f), 0.0004f, 0.25f);
                }
            }
        }
    }
}
