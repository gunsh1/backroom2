# VR 移植のときにやること

non-VR のフォトリアル版が固まってから着手する。

## パッケージ

`Packages/manifest.json` に以下を足す。**`com.unity.modules.vr` を忘れないこと。**
これが無いと `com.unity.xr.management` の依存解決が失敗し、パッケージ解決の段階で
Unity が終了してしまい、`-executeMethod` にすら到達しない（一度踏んだ）。

```json
"com.unity.modules.vr": "1.0.0",
"com.unity.modules.xr": "1.0.0",
"com.unity.xr.management": "4.5.0",
"com.unity.xr.openxr": "1.14.0"
```

## ランタイム

このマシンの OpenXR アクティブランタイムは Virtual Desktop:

```
HKLM\SOFTWARE\Khronos\OpenXR\1\ActiveRuntime
  = C:\Program Files\Virtual Desktop Streamer\OpenXR\virtualdesktop-openxr.json
```

SteamVR も入っているので、切り替える場合はここを見る。

## 描画の切り替え

VR ではレイトレーシングが成立しない（片目 2000px 級 x 両眼 x 90Hz）。
ベイクしたライトマップに切り替える前提で組む。

- HDRP アセットの `xrSettings.singlePass` を有効化
- ボリュームの `GlobalIllumination.tracing` を RayTracing から外す
- `ScreenSpaceReflection` と `Fog.enableVolumetricFog` を無効化
- `MotionBlur` は必ず無効（VR では酔いの原因）
- 露出の自動順応は遅くするか固定にする

## プレイヤー

`FirstPersonController` を外し、XR リグに差し替える。
視点は必ず子の `Head` を介して動かす設計にしてあるので、差し替え点はそこだけ。
歩行に伴う視点の揺れ（`BobVertical` など）は VR では全てゼロにすること。
