using System.Collections.Generic;
using UnityEngine;

namespace Backrooms.Generation
{
    /// <summary>
    /// 建築ジオメトリを組み立てる軽量メッシュビルダ。
    /// UV は常にワールド座標から作るので、隣接面の間でテクスチャがずれない。
    ///
    /// 巻き順について: Unity 組み込みの Quad は頂点 (-x,-y)(+x,-y)(-x,+y)(+x,+y) に対して
    /// tris = [0,2,1, 2,3,1]、法線 (0,0,-1) を持つ。これを基準に取ると、
    /// 頂点を [o, o+r, o+r+u, o+u] の順に並べた場合、法線 +Cross(right, up) の面を表にするには
    /// 三角形を (0,1,2)(0,2,3) の順で張る必要がある。
    /// </summary>
    public class MeshBuilder
    {
        readonly List<Vector3> _v = new List<Vector3>();
        readonly List<Vector3> _n = new List<Vector3>();
        readonly List<Vector2> _uv = new List<Vector2>();
        readonly List<int> _t = new List<int>();

        public int VertexCount => _v.Count;
        public int TriangleCount => _t.Count / 3;

        /// <summary>
        /// 0 より大きい場合、垂直面の V 軸だけこのスケール（m）を使う。
        /// 壁の高さ方向にテクスチャを 1 回だけ張れるようにするための仕組み。
        /// これができると「幅木際の汚れ」「腰高のこすれ」「天井際の埃」を
        /// 実際の高さに焼き込める。繰り返しタイリングでは絶対に作れない情報。
        /// </summary>
        public float UvScaleV = 0f;

        float VScale(float uvScale) => UvScaleV > 0f ? 1f / UvScaleV : 1f / uvScale;

        public void Quad(Vector3 origin, Vector3 right, Vector3 up,
                         Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3)
        {
            var nrm = Vector3.Cross(right, up).normalized;
            int b = _v.Count;
            _v.Add(origin);
            _v.Add(origin + right);
            _v.Add(origin + right + up);
            _v.Add(origin + up);
            for (int i = 0; i < 4; i++) _n.Add(nrm);
            _uv.Add(uv0); _uv.Add(uv1); _uv.Add(uv2); _uv.Add(uv3);
            _t.Add(b); _t.Add(b + 1); _t.Add(b + 2);
            _t.Add(b); _t.Add(b + 2); _t.Add(b + 3);
        }

        // ---------------------------------------------------------- 水平面
        /// <summary>床 (faceUp=true) / 天井 (faceUp=false)。UV はワールド XZ。</summary>
        public void PlaneXZ(float x0, float x1, float z0, float z1, float y, bool faceUp, float uvScale)
        {
            float s = 1f / uvScale;
            if (faceUp)
            {
                // 法線 +Y = Cross(right:+Z, up:+X)
                Quad(new Vector3(x0, y, z0), new Vector3(0, 0, z1 - z0), new Vector3(x1 - x0, 0, 0),
                     new Vector2(x0 * s, z0 * s), new Vector2(x0 * s, z1 * s),
                     new Vector2(x1 * s, z1 * s), new Vector2(x1 * s, z0 * s));
            }
            else
            {
                // 法線 -Y = Cross(right:+X, up:+Z)
                Quad(new Vector3(x0, y, z0), new Vector3(x1 - x0, 0, 0), new Vector3(0, 0, z1 - z0),
                     new Vector2(x0 * s, z0 * s), new Vector2(x1 * s, z0 * s),
                     new Vector2(x1 * s, z1 * s), new Vector2(x0 * s, z1 * s));
            }
        }

        // ---------------------------------------------------------- 垂直面
        /// <summary>Z 固定の壁面。facePosZ で向きを決める。UV は (x, y)。</summary>
        public void PlaneXY(float x0, float x1, float y0, float y1, float z, bool facePosZ, float uvScale)
        {
            float s = 1f / uvScale;
            float sv = VScale(uvScale);
            if (facePosZ)
            {
                // 法線 +Z = Cross(right:+X, up:+Y) ではなく Cross(X,Y)=+Z なので right=+X, up=+Y
                Quad(new Vector3(x0, y0, z), new Vector3(x1 - x0, 0, 0), new Vector3(0, y1 - y0, 0),
                     new Vector2(x0 * s, y0 * sv), new Vector2(x1 * s, y0 * sv),
                     new Vector2(x1 * s, y1 * sv), new Vector2(x0 * s, y1 * sv));
            }
            else
            {
                // 法線 -Z = Cross(right:+Y, up:+X)
                Quad(new Vector3(x0, y0, z), new Vector3(0, y1 - y0, 0), new Vector3(x1 - x0, 0, 0),
                     new Vector2(x0 * s, y0 * sv), new Vector2(x0 * s, y1 * sv),
                     new Vector2(x1 * s, y1 * sv), new Vector2(x1 * s, y0 * sv));
            }
        }

        /// <summary>X 固定の壁面。UV は (z, y)。</summary>
        public void PlaneZY(float z0, float z1, float y0, float y1, float x, bool facePosX, float uvScale)
        {
            float s = 1f / uvScale;
            float sv = VScale(uvScale);
            if (facePosX)
            {
                // 法線 +X = Cross(right:+Y, up:+Z)
                Quad(new Vector3(x, y0, z0), new Vector3(0, y1 - y0, 0), new Vector3(0, 0, z1 - z0),
                     new Vector2(z0 * s, y0 * sv), new Vector2(z0 * s, y1 * sv),
                     new Vector2(z1 * s, y1 * sv), new Vector2(z1 * s, y0 * sv));
            }
            else
            {
                // 法線 -X = Cross(right:+Z, up:+Y)
                Quad(new Vector3(x, y0, z0), new Vector3(0, 0, z1 - z0), new Vector3(0, y1 - y0, 0),
                     new Vector2(z0 * s, y0 * sv), new Vector2(z1 * s, y0 * sv),
                     new Vector2(z1 * s, y1 * sv), new Vector2(z0 * s, y1 * sv));
            }
        }

        // ---------------------------------------------------------------- 箱
        /// <summary>6 面を外向きに張った箱。壁・幅木・柱・T バーに使う。</summary>
        public void Box(Vector3 min, Vector3 max, float uvScale)
        {
            PlaneXZ(min.x, max.x, min.z, max.z, max.y, true, uvScale);   // 上
            PlaneXZ(min.x, max.x, min.z, max.z, min.y, false, uvScale);  // 下
            PlaneXY(min.x, max.x, min.y, max.y, max.z, true, uvScale);   // +Z
            PlaneXY(min.x, max.x, min.y, max.y, min.z, false, uvScale);  // -Z
            PlaneZY(min.z, max.z, min.y, max.y, max.x, true, uvScale);   // +X
            PlaneZY(min.z, max.z, min.y, max.y, min.x, false, uvScale);  // -X
        }

        public void BoxSize(Vector3 center, Vector3 size, float uvScale)
        {
            Box(center - size * 0.5f, center + size * 0.5f, uvScale);
        }

        // ------------------------------------------------------------ 面取り箱
        /// <summary>
        /// 頂点をワールド座標で直接指定する四角形。法線は Cross(b-a, d-a)、
        /// UV は法線の主軸に垂直な平面へ投影して作る。
        /// </summary>
        public void QuadWorld(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float uvScale)
        {
            Vector3 n = Vector3.Cross(b - a, d - a).normalized;
            int i = _v.Count;
            _v.Add(a); _v.Add(b); _v.Add(c); _v.Add(d);
            for (int k = 0; k < 4; k++) _n.Add(n);
            _uv.Add(ProjectUv(a, n, uvScale));
            _uv.Add(ProjectUv(b, n, uvScale));
            _uv.Add(ProjectUv(c, n, uvScale));
            _uv.Add(ProjectUv(d, n, uvScale));
            _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
            _t.Add(i); _t.Add(i + 2); _t.Add(i + 3);
        }

        /// <summary>頂点位置と UV を全て明示する四角形。平面でなくてもよい。</summary>
        public void QuadUv(Vector3 a, Vector3 b, Vector3 c, Vector3 d,
                           Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3)
        {
            Vector3 n = Vector3.Cross(b - a, d - a).normalized;
            int i = _v.Count;
            _v.Add(a); _v.Add(b); _v.Add(c); _v.Add(d);
            for (int k = 0; k < 4; k++) _n.Add(n);
            _uv.Add(uv0); _uv.Add(uv1); _uv.Add(uv2); _uv.Add(uv3);
            _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
            _t.Add(i); _t.Add(i + 2); _t.Add(i + 3);
        }

        public void TriWorld(Vector3 a, Vector3 b, Vector3 c, float uvScale)
        {
            Vector3 n = Vector3.Cross(b - a, c - a).normalized;
            int i = _v.Count;
            _v.Add(a); _v.Add(b); _v.Add(c);
            for (int k = 0; k < 3; k++) _n.Add(n);
            _uv.Add(ProjectUv(a, n, uvScale));
            _uv.Add(ProjectUv(b, n, uvScale));
            _uv.Add(ProjectUv(c, n, uvScale));
            _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
        }

        Vector2 ProjectUv(Vector3 p, Vector3 n, float uvScale)
        {
            float s = 1f / uvScale;
            float sv = VScale(uvScale);
            Vector3 a = new Vector3(Mathf.Abs(n.x), Mathf.Abs(n.y), Mathf.Abs(n.z));
            if (a.y >= a.x && a.y >= a.z) return new Vector2(p.x * s, p.z * s);   // 水平面（V は高さでない）
            if (a.x >= a.z) return new Vector2(p.z * s, p.y * sv);                // X 面
            return new Vector2(p.x * s, p.y * sv);                                // Z 面
        }

        /// <summary>
        /// Y 軸に沿った円柱。配管・スプリンクラー・感知器などの設備に使う。
        /// 設備が一切無い空間は、材質をどれだけ詰めても「作られた場所」に見える。
        /// </summary>
        public void Cylinder(Vector3 baseCenter, float radius, float height,
                             int sides, float uvScale, bool capTop = true, bool capBottom = true)
        {
            sides = Mathf.Max(3, sides);
            var lo = new Vector3[sides];
            var hi = new Vector3[sides];
            for (int i = 0; i < sides; i++)
            {
                float a = i / (float)sides * Mathf.PI * 2f;
                var off = new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
                lo[i] = baseCenter + off;
                hi[i] = lo[i] + Vector3.up * height;
            }
            for (int i = 0; i < sides; i++)
            {
                int j = (i + 1) % sides;
                // 外向き
                QuadWorld(lo[i], lo[j], hi[j], hi[i], uvScale);
            }
            if (capTop)
            {
                var c = baseCenter + Vector3.up * height;
                for (int i = 0; i < sides; i++)
                {
                    int j = (i + 1) % sides;
                    TriWorld(c, hi[i], hi[j], uvScale);
                }
            }
            if (capBottom)
            {
                for (int i = 0; i < sides; i++)
                {
                    int j = (i + 1) % sides;
                    TriWorld(baseCenter, lo[j], lo[i], uvScale);
                }
            }
        }

        /// <summary>
        /// 全エッジを面取りした箱。
        ///
        /// 実在の建築に数学的な直角は存在しない。塗装は角で盛られて丸まり、
        /// 石膏ボードのコーナービードは必ず R を持ち、幅木の角は欠ける。
        /// 数ミリの面取りが入るだけでハイライトが 1 本乗り、CG 臭が大きく落ちる。
        /// フォトリアルで最も費用対効果が高い処理。
        /// </summary>
        public void BeveledBox(Vector3 min, Vector3 max, float bevel, float uvScale)
        {
            // 面取り量が辺の長さに対して大きすぎる場合は素の箱に落とす
            Vector3 size = max - min;
            float b = Mathf.Min(bevel, Mathf.Min(size.x, Mathf.Min(size.y, size.z)) * 0.34f);
            if (b <= 1e-5f) { Box(min, max, uvScale); return; }

            float x0 = min.x, x1 = max.x, y0 = min.y, y1 = max.y, z0 = min.z, z1 = max.z;
            float xi0 = x0 + b, xi1 = x1 - b;
            float yi0 = y0 + b, yi1 = y1 - b;
            float zi0 = z0 + b, zi1 = z1 - b;

            // --- 6 面（面取り分だけ内側に縮めた本体面）---
            QuadWorld(new Vector3(x1, yi0, zi0), new Vector3(x1, yi1, zi0),
                      new Vector3(x1, yi1, zi1), new Vector3(x1, yi0, zi1), uvScale);   // +X
            QuadWorld(new Vector3(x0, yi0, zi1), new Vector3(x0, yi1, zi1),
                      new Vector3(x0, yi1, zi0), new Vector3(x0, yi0, zi0), uvScale);   // -X
            QuadWorld(new Vector3(xi0, y1, zi0), new Vector3(xi0, y1, zi1),
                      new Vector3(xi1, y1, zi1), new Vector3(xi1, y1, zi0), uvScale);   // +Y
            QuadWorld(new Vector3(xi0, y0, zi1), new Vector3(xi0, y0, zi0),
                      new Vector3(xi1, y0, zi0), new Vector3(xi1, y0, zi1), uvScale);   // -Y
            QuadWorld(new Vector3(xi0, yi0, z1), new Vector3(xi1, yi0, z1),
                      new Vector3(xi1, yi1, z1), new Vector3(xi0, yi1, z1), uvScale);   // +Z
            QuadWorld(new Vector3(xi1, yi0, z0), new Vector3(xi0, yi0, z0),
                      new Vector3(xi0, yi1, z0), new Vector3(xi1, yi1, z0), uvScale);   // -Z

            // --- 12 稜線 ---
            // Z 軸に平行な 4 本
            QuadWorld(new Vector3(x1, yi1, zi0), new Vector3(xi1, y1, zi0),
                      new Vector3(xi1, y1, zi1), new Vector3(x1, yi1, zi1), uvScale);
            QuadWorld(new Vector3(xi1, y0, zi0), new Vector3(x1, yi0, zi0),
                      new Vector3(x1, yi0, zi1), new Vector3(xi1, y0, zi1), uvScale);
            QuadWorld(new Vector3(xi0, y1, zi0), new Vector3(x0, yi1, zi0),
                      new Vector3(x0, yi1, zi1), new Vector3(xi0, y1, zi1), uvScale);
            QuadWorld(new Vector3(x0, yi0, zi0), new Vector3(xi0, y0, zi0),
                      new Vector3(xi0, y0, zi1), new Vector3(x0, yi0, zi1), uvScale);
            // X 軸に平行な 4 本
            QuadWorld(new Vector3(xi0, y1, zi1), new Vector3(xi0, yi1, z1),
                      new Vector3(xi1, yi1, z1), new Vector3(xi1, y1, zi1), uvScale);
            QuadWorld(new Vector3(xi1, y1, zi0), new Vector3(xi1, yi1, z0),
                      new Vector3(xi0, yi1, z0), new Vector3(xi0, y1, zi0), uvScale);
            QuadWorld(new Vector3(xi0, yi0, z1), new Vector3(xi0, y0, zi1),
                      new Vector3(xi1, y0, zi1), new Vector3(xi1, yi0, z1), uvScale);
            QuadWorld(new Vector3(xi1, yi0, z0), new Vector3(xi1, y0, zi0),
                      new Vector3(xi0, y0, zi0), new Vector3(xi0, yi0, z0), uvScale);
            // Y 軸に平行な 4 本
            QuadWorld(new Vector3(x1, yi0, zi1), new Vector3(x1, yi1, zi1),
                      new Vector3(xi1, yi1, z1), new Vector3(xi1, yi0, z1), uvScale);
            QuadWorld(new Vector3(xi1, yi0, z0), new Vector3(xi1, yi1, z0),
                      new Vector3(x1, yi1, zi0), new Vector3(x1, yi0, zi0), uvScale);
            QuadWorld(new Vector3(xi0, yi0, z1), new Vector3(xi0, yi1, z1),
                      new Vector3(x0, yi1, zi1), new Vector3(x0, yi0, zi1), uvScale);
            QuadWorld(new Vector3(x0, yi0, zi0), new Vector3(x0, yi1, zi0),
                      new Vector3(xi0, yi1, z0), new Vector3(xi0, yi0, z0), uvScale);

            // --- 8 隅 ---
            TriWorld(new Vector3(x1, yi1, zi1), new Vector3(xi1, y1, zi1), new Vector3(xi1, yi1, z1), uvScale);
            TriWorld(new Vector3(xi1, y1, zi0), new Vector3(x1, yi1, zi0), new Vector3(xi1, yi1, z0), uvScale);
            TriWorld(new Vector3(x0, yi1, zi1), new Vector3(xi0, yi1, z1), new Vector3(xi0, y1, zi1), uvScale);
            TriWorld(new Vector3(xi0, y1, zi0), new Vector3(xi0, yi1, z0), new Vector3(x0, yi1, zi0), uvScale);
            TriWorld(new Vector3(x1, yi0, zi1), new Vector3(xi1, yi0, z1), new Vector3(xi1, y0, zi1), uvScale);
            TriWorld(new Vector3(xi1, y0, zi0), new Vector3(xi1, yi0, z0), new Vector3(x1, yi0, zi0), uvScale);
            TriWorld(new Vector3(x0, yi0, zi1), new Vector3(xi0, y0, zi1), new Vector3(xi0, yi0, z1), uvScale);
            TriWorld(new Vector3(xi0, y0, zi0), new Vector3(xi0, yi0, z0), new Vector3(x0, yi0, zi0), uvScale);
        }

        // -------------------------------------------------------------- 出力
        public Mesh ToMesh(string name)
        {
            var m = new Mesh { name = name };
            if (_v.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(_v);
            m.SetNormals(_n);
            m.SetUVs(0, _uv);
            m.SetTriangles(_t, 0);
            // ノーマルマップに必須。UV が入った後で計算する。
            m.RecalculateTangents();
            m.RecalculateBounds();
            return m;
        }

        public bool IsEmpty => _v.Count == 0;
    }

    public static class MeshKit
    {
        /// <summary>メッシュ + マテリアルを持つ子オブジェクトを作る。</summary>
        public static GameObject Spawn(Transform parent, string name, Mesh mesh, Material mat,
                                       bool castShadows = true, bool staticGeom = true)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = castShadows
                ? UnityEngine.Rendering.ShadowCastingMode.On
                : UnityEngine.Rendering.ShadowCastingMode.Off;
            // レイトレ加速構造に含めるため、常に RayTracingMode は既定 (Static) のままにする
            var col = go.AddComponent<MeshCollider>();
            col.sharedMesh = mesh;
#if UNITY_EDITOR
            if (staticGeom) UnityEditor.GameObjectUtility.SetStaticEditorFlags(go,
                UnityEditor.StaticEditorFlags.ContributeGI | UnityEditor.StaticEditorFlags.OccluderStatic |
                UnityEditor.StaticEditorFlags.OccludeeStatic | UnityEditor.StaticEditorFlags.BatchingStatic);
#endif
            return go;
        }
    }
}
