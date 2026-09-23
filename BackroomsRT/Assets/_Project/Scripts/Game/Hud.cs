using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace Backrooms.Game
{
    /// <summary>
    /// ポーズと札。
    ///
    /// カムコーダの表示 (REC・タイムコード・残量) は Vhs.shader の中で
    /// 描く。テープの一部なので、ドロップアウトや走査線の下に無いと
    /// 一緒に劣化しない。こちらは逆に「テープの一部ではないもの」だけ、
    /// つまりポーズと死亡とクリアの札を持つ。VHS の後に合成されるのが正しい。
    ///
    /// prefab は作らない。このプロジェクトは全部コードから建てる流儀で、
    /// シーンが何度でも無損失に作り直せることがその流儀の値打ちになっている。
    ///
    /// 表記は英語。1996 年のアメリカのオフィスを撮ったテープ、という
    /// 建て付けと、音声・字幕を英語にした決定に合わせる。日本語フォントの
    /// アセットが要らなくなる、という実利もある。
    /// </summary>
    [DisallowMultipleComponent]
    public class Hud : MonoBehaviour
    {
        [Tooltip("本文の字の大きさ。1080 基準。16px 相当を必ず超えること")]
        public int BodySize = 26;
        [Tooltip("見出しの字の大きさ")]
        public int TitleSize = 64;

        GameSession _session;
        Canvas _canvas;
        GameObject _scrim, _menu;
        Text _title, _body;
        Button _resume, _restart, _quit;
        Phase _shown = (Phase)(-1);

        void Start()
        {
            _session = GameSession.Instance;
            Build();
            Refresh(true);
        }

        void Update()
        {
            if (_session == null) { _session = GameSession.Instance; return; }
            Refresh(false);
        }

        void Refresh(bool force)
        {
            var p = _session != null ? _session.Current : Phase.Playing;
            if (!force && p == _shown) return;
            _shown = p;

            bool show = p != Phase.Playing;
            _scrim.SetActive(show);
            if (!show) return;

            bool paused = p == Phase.Paused;
            _title.text = paused ? "PAUSED" : _session.CardTitle;
            _body.text = paused
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "TAPE {0:00}   {1:MMM dd yyyy}",
                                _session.Tape.TapeNumber, _session.Tape.Date).ToUpperInvariant()
                : _session.CardBody;

            // ポーズだけ操作できる。札は読ませるだけで、勝手に進む。
            // 脱出のときだけ、もう一度やる手段を出す。
            _resume.gameObject.SetActive(paused);
            _restart.gameObject.SetActive(paused || p == Phase.Escaped);
            _quit.gameObject.SetActive(paused || p == Phase.Escaped);
        }

        // ------------------------------------------------------------ 組み立て

        void Build()
        {
            if (FindFirstObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem");
                es.transform.SetParent(transform, false);
                es.AddComponent<EventSystem>();
                es.AddComponent<StandaloneInputModule>();
            }

            var go = new GameObject("Hud_Canvas");
            go.transform.SetParent(transform, false);
            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1440f, 1080f);
            // 高さで合わせる。シェーダが切る 4:3 の窓も高さが基準なので、
            // これで両者の座標系が一致する。
            scaler.matchWidthOrHeight = 1f;
            go.AddComponent<GraphicRaycaster>();

            _scrim = Panel(go.transform, "Scrim", new Color(0f, 0f, 0f, 0.90f));
            Stretch(_scrim.GetComponent<RectTransform>());

            // 中身は 4:3 の窓の中にだけ置く。外に出るとピラーボックスの
            // 黒の上にボタンが載り、押せるのに見えない、が起きる。
            _menu = new GameObject("Menu", typeof(RectTransform));
            _menu.transform.SetParent(_scrim.transform, false);
            var mr = _menu.GetComponent<RectTransform>();
            mr.anchorMin = mr.anchorMax = new Vector2(0.5f, 0.5f);
            mr.pivot = new Vector2(0.5f, 0.5f);
            mr.sizeDelta = new Vector2(1440f, 1080f);
            mr.anchoredPosition = Vector2.zero;

            _title = Label(mr, "Title", TitleSize, new Vector2(0f, 170f), FontStyle.Bold);
            _body = Label(mr, "Body", BodySize, new Vector2(0f, 90f), FontStyle.Normal);

            _resume = MakeButton(mr, "Resume", "RESUME", new Vector2(0f, -10f),
                                 () => { if (_session != null) _session.SetPaused(false); });
            _restart = MakeButton(mr, "Restart", "RESTART", new Vector2(0f, -80f),
                                  () => { if (_session != null) _session.Restart(); });
            _quit = MakeButton(mr, "Quit", "QUIT", new Vector2(0f, -150f),
                               () => { if (_session != null) _session.Quit(); });

            _scrim.SetActive(false);
        }

        static Font Ui()
        {
            var f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return f;
        }

        static GameObject Panel(Transform parent, string name, Color c)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = c;
            return go;
        }

        static void Stretch(RectTransform r)
        {
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
            r.offsetMin = Vector2.zero; r.offsetMax = Vector2.zero;
        }

        static Text Label(RectTransform parent, string name, int size, Vector2 at, FontStyle style)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>();
            t.font = Ui();
            t.fontSize = size;
            t.fontStyle = style;
            t.alignment = TextAnchor.MiddleCenter;
            // #F2F2F2 を 90% の黒の上に。コントラスト比は 15:1 を超える。
            t.color = new Color32(0xF2, 0xF2, 0xF2, 0xFF);
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            var r = go.GetComponent<RectTransform>();
            r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f);
            r.sizeDelta = new Vector2(1200f, size + 24f);
            r.anchoredPosition = at;
            return t;
        }

        Button MakeButton(RectTransform parent, string name, string label, Vector2 at,
                          UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.06f);
            var r = go.GetComponent<RectTransform>();
            r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f);
            r.sizeDelta = new Vector2(420f, 56f);
            r.anchoredPosition = at;

            var t = Label(r, "Label", BodySize, Vector2.zero, FontStyle.Normal);
            t.text = label;

            var b = go.AddComponent<Button>();
            b.targetGraphic = img;
            var colors = b.colors;
            colors.normalColor = new Color(1f, 1f, 1f, 0.06f);
            colors.highlightedColor = new Color(1f, 1f, 1f, 0.18f);
            colors.pressedColor = new Color(1f, 1f, 1f, 0.30f);
            colors.selectedColor = new Color(1f, 1f, 1f, 0.18f);
            b.colors = colors;
            b.onClick.AddListener(onClick);
            return b;
        }
    }
}
