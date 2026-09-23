using UnityEngine;
using Backrooms.Generation;

namespace Backrooms.Game
{
    /// <summary>
    /// 出口。空間の異常として現れる。
    ///
    /// M-A では位置と当たり判定だけ。見た目は仮の黒い箱で、本物
    /// （天井タイルを 3x3 抜いて 6m の竪穴、T バーを破り、壁に裂け目）は
    /// M-C でチャンクの生成に入れる。
    ///
    /// 光らせない。音も出さない。風も吹かせない。手がかりは置かない、
    /// と決めてある。見付け方は「天井があるべき所に穴がある」ことに
    /// 気付くことだけ。
    ///
    /// これがこのプロジェクト初のトリガー領域になる。CharacterController は
    /// Rigidbody 無しでもトリガーを発火するので、これで足りる。
    /// </summary>
    [DisallowMultipleComponent]
    public class Exit : MonoBehaviour
    {
        [Tooltip("スポーンからこの距離に置く。M-C で計画ファイルの座標に変える")]
        public float DistanceFromSpawn = 60f;
        [Tooltip("置く向き (度)")]
        public float Bearing = 35f;
        [Tooltip("仮の見た目を出すか。M-A の確認用")]
        public bool Placeholder = true;

        public Vector3 Where { get; private set; }

        int _seed = 1996;

        void Start()
        {
            var world = FindFirstObjectByType<ChunkedLevel0>();
            if (world != null) _seed = world.Seed;

            var spawn = transform.position;
            var fps = FindFirstObjectByType<Player.FirstPersonController>();
            if (fps != null) spawn = fps.transform.position;

            Where = Choose(spawn);
            transform.position = Where;

            var box = gameObject.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.center = new Vector3(0f, 1.2f, 0f);
            box.size = new Vector3(1.8f, 2.4f, 1.8f);

            if (Placeholder) BuildPlaceholder();

            float d = Vector3.Distance(new Vector3(spawn.x, 0f, spawn.z),
                                       new Vector3(Where.x, 0f, Where.z));
            Debug.Log($"[exit] ({Where.x:F1}, {Where.z:F1}) / スポーンから直線 {d:F1} m");
        }

        Vector3 Choose(Vector3 spawn)
        {
            // 指定の方角から少しずつ回して、体が入る所を探す。
            for (int k = 0; k < 72; k++)
            {
                float a = (Bearing + k * 5f) * Mathf.Deg2Rad;
                float x = spawn.x + Mathf.Cos(a) * DistanceFromSpawn;
                float z = spawn.z + Mathf.Sin(a) * DistanceFromSpawn;
                if (Level0Layout.Clear(x, z, _seed, 0.80f)) return new Vector3(x, 0f, z);
            }
            return Level0Layout.FindOpen(spawn + Vector3.forward * DistanceFromSpawn, _seed);
        }

        /// <summary>M-A の仮の目印。天井に黒い穴が開いているように見せるだけ。</summary>
        void BuildPlaceholder()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Exit_Placeholder";
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, Level0Layout.CeilH + 0.9f, 0f);
            go.transform.localScale = new Vector3(1.8f, 1.8f, 1.8f);
            Destroy(go.GetComponent<Collider>());
            var r = go.GetComponent<MeshRenderer>();
            var m = new Material(Shader.Find("HDRP/Unlit"));
            if (m.shader == null) m = new Material(Shader.Find("Unlit/Color"));
            m.SetColor("_UnlitColor", Color.black);
            m.color = Color.black;
            r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        void OnTriggerEnter(Collider other)
        {
            if (other.GetComponentInParent<Player.FirstPersonController>() == null) return;
            if (GameSession.Instance != null) GameSession.Instance.Escape();
        }
    }
}
