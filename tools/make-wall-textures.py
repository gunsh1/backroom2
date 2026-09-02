# -*- coding: utf-8 -*-
"""壁紙の汚しテクスチャを作り直す。

UV の性質:
  U … ワールドで 2.12m ごとに繰り返す   -> ここに置いた特徴は必ず反復して見える
  V … 壁の全高 2.90m を 0-1 で 1 周期   -> ここに置いた特徴は絶対に反復しない
      (MeshKit.ProjectUv が壁面で p.y * (1/UvScaleV) を返すため)

したがって「原因のある汚れ」は全て V 方向のプロファイルとして焼く。
  - 床際 0-0.35m … モップと靴の帯。実際のオフィスで最も汚れる場所
  - 天井際 2.45m 以上 … 空調の吹き出しで舞った埃が付着する帯
  - 中間 … 原因が無いので汚さない

U 方向には、繰り返しが知覚されない範囲 (特徴 <= タイルの 1/8 = 256px) の
微細な斑だけを乗せる。全高を走る縦筋は原因が無いので入れない。

    python tools/make-wall-textures.py
"""
import os
import numpy as np
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, 'BackroomsRT', 'Assets', '_Project', 'Art', 'Textures', 'Wallpaper001B')
N = 2048
TILE_M = 2.12      # U 1 周期のワールド寸法
WALL_M = 2.90      # V 1 周期のワールド寸法（壁の全高）
rng = np.random.default_rng(1996)


def tileable_noise(n, min_feature_px):
    """FFT ローパスで作る、数学的に完全に周期的なノイズ。0-1 に正規化。

    resize による fbm は端が繋がらない。周波数領域で切ると必ず繋がる。
    """
    f = np.fft.fftfreq(n) * n                      # -n/2 .. n/2
    fx, fy = np.meshgrid(f, f, indexing='xy')
    r = np.hypot(fx, fy)
    kmax = n / float(min_feature_px)               # これより高い周波数だけ残す…ではなく
    spec = np.zeros((n, n), dtype=np.complex128)
    white = np.fft.fft2(rng.standard_normal((n, n)))
    # 1/f で低周波を強め、min_feature_px より粗い成分は落とす
    with np.errstate(divide='ignore'):
        amp = np.where(r > 0, 1.0 / np.maximum(r, 1e-6), 0.0)
    amp[r < kmax * 0.5] = 0.0                      # 粗すぎる成分を除去 = 反復が見えない
    spec = white * amp
    out = np.real(np.fft.ifft2(spec))
    out -= out.min()
    out /= max(out.max(), 1e-9)
    return out


def smoothstep(e0, e1, x):
    t = np.clip((x - e0) / max(e1 - e0, 1e-9), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


# ---- V プロファイル（行 0 = 画像の上 = v=1 = 壁の天端）----------------------
row = np.arange(N)
v = 1.0 - (row + 0.5) / N                 # 0 = 床、1 = 天端
h = v * WALL_M                            # ワールドの高さ [m]

# 床際: 0m で 0.66 倍、0.35m で 1.0 に戻る
floor_dirt = 0.66 + 0.34 * smoothstep(0.0, 0.35, h)
# 天井際: 2.45m から上で徐々に 0.90 倍まで
ceil_dust = 1.0 - 0.10 * smoothstep(2.45, WALL_M, h)
vprofile = floor_dirt * ceil_dust          # (N,)

# ---- U 方向の微細な斑（特徴 >= 256px の成分は入れない）--------------------
fine = tileable_noise(N, 256)              # 0-1
fine = 1.0 + (fine - fine.mean()) * 0.09   # 振幅 +-4.5% 程度

# 床際の帯は一直線ではなく、モップの届き方でむらになる。
# ただしこれも粗い成分は入れられないので、細かい揺らぎだけ。
edge_wobble = tileable_noise(N, 320)
edge_wobble = (edge_wobble - edge_wobble.mean()) * 0.06

mult = vprofile[:, None] * fine
# 床際だけ、むらを効かせる（上の方には効かせない）
band = smoothstep(0.60, 0.0, h)[:, None]
mult = mult * (1.0 + edge_wobble * band)
mult = np.clip(mult, 0.0, 1.2)

# ---- ベースカラー -----------------------------------------------------------
base = np.asarray(Image.open(os.path.join(SRC, 'Wallpaper001B_Color_Seamed.png'))
                  .convert('RGB')).astype(np.float32) / 255.0
lin = np.where(base <= 0.04045, base / 12.92, ((base + 0.055) / 1.055) ** 2.4)
# 汚れは青を最もよく吸う（埃と皮脂は黄褐色）。チャンネルごとに効き方を変える。
chan = np.stack([mult, mult ** 1.10, mult ** 1.30], axis=-1)
lin = np.clip(lin * chan, 0.0, 1.0)
srgb = np.where(lin <= 0.0031308, lin * 12.92, 1.055 * lin ** (1 / 2.4) - 0.055)
Image.fromarray((np.clip(srgb, 0, 1) * 255 + 0.5).astype(np.uint8)) \
     .save(os.path.join(SRC, 'Wallpaper_Grimed_Color.png'))

# ---- マスクマップ (R:Metallic G:AO B:Detail A:Smoothness) -------------------
mask = np.asarray(Image.open(os.path.join(SRC, 'Wallpaper001B_MaskMap.png'))
                  .convert('RGBA')).astype(np.float32) / 255.0
dirt = np.clip((1.0 - mult) / 0.34, 0.0, 1.0)          # 0=きれい 1=最も汚れている
mask[..., 3] = np.clip(mask[..., 3] * (1.0 - 0.45 * dirt), 0.0, 1.0)   # 汚れは艶を失う
mask[..., 1] = np.clip(mask[..., 1] * (1.0 - 0.12 * dirt), 0.0, 1.0)   # わずかに落ち込む
Image.fromarray((np.clip(mask, 0, 1) * 255 + 0.5).astype(np.uint8)) \
     .save(os.path.join(SRC, 'Wallpaper_Grimed_MaskMap.png'))

# ---- 検証: 左右端が繋がっているか ------------------------------------------
a = np.asarray(Image.open(os.path.join(SRC, 'Wallpaper_Grimed_Color.png'))
               .convert('L')).astype(np.float32) / 255.0
seam = np.abs(a[:, 0] - a[:, -1]).mean()
mid = np.abs(a[:, N // 2] - a[:, N // 2 - 1]).mean()
print('継ぎ目の差 %.5f / 通常の隣接列の差 %.5f  -> %s'
      % (seam, mid, 'OK' if seam <= mid * 1.5 else '要確認'))
print('行平均  天端=%.3f  中央=%.3f  床際=%.3f' % (a[:40].mean(), a[N // 2 - 20:N // 2 + 20].mean(), a[-40:].mean()))
print('書き出し: Wallpaper_Grimed_Color.png / Wallpaper_Grimed_MaskMap.png')
