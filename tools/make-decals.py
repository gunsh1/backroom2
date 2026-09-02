# -*- coding: utf-8 -*-
"""デカール用のテクスチャを作る。

タイリングするテクスチャには「その場所にしかない汚れ」を焼けない。
2.12m ごとに同じ手形が並ぶことになるので、原因が場所に紐づく汚れは
全てデカールとして個別に置く必要がある。

  Decal_Scuff     … 外角の手擦れ・肩の当たり。腰から胸の高さに出る
  Decal_Drip      … 天井から垂れた水の筋。染みのある天井の直下にだけ置く
  Decal_Traffic   … カーペットの通行帯。毛足が踏み倒されて色が沈み、
                     わずかに艶が出る
  Decal_Dent      … 家具の脚が長く乗っていた凹み。4 点で 1 組

RGBA の A が被覆率。HDRP/Decal はこれを _DecalBlend と掛けて使う。

    python tools/make-decals.py
"""
import os
import numpy as np
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, 'BackroomsRT', 'Assets', '_Project', 'Art', 'Textures', 'Decals')
os.makedirs(OUT, exist_ok=True)
rng = np.random.default_rng(2024)


def fbm(shape, octaves=5, lac=2.0, gain=0.5):
    """通常の fbm。デカールは繰り返さないので端が繋がる必要はない。"""
    h, w = shape
    out = np.zeros(shape, np.float32)
    amp, freq = 1.0, 1.0
    norm = 0.0
    for _ in range(octaves):
        gh, gw = max(2, int(h / 64 * freq)), max(2, int(w / 64 * freq))
        g = rng.random((gh, gw)).astype(np.float32)
        layer = np.asarray(Image.fromarray((g * 255).astype(np.uint8)).resize((w, h), Image.BICUBIC),
                           np.float32) / 255.0
        out += layer * amp
        norm += amp
        amp *= gain
        freq *= lac
    return out / norm


def save(name, rgb, alpha):
    a = np.dstack([np.clip(rgb, 0, 1), np.clip(alpha, 0, 1)])
    Image.fromarray((a * 255 + 0.5).astype(np.uint8)).save(os.path.join(OUT, name + '.png'))
    print('%-16s %s  被覆率 平均=%.3f 最大=%.3f' % (name, a.shape[:2], alpha.mean(), alpha.max()))


# --- 外角の手擦れ ------------------------------------------------------------
# 人が角を曲がるときに手や肩が触れる。上下に細長く、角に寄るほど濃い。
N = 1024
yy, xx = np.mgrid[0:N, 0:N] / (N - 1.0)
n = fbm((N, N), octaves=6)
# 角（U=0 側）に寄るほど濃く、縦方向は腰から胸の高さで最大
edge = np.exp(-(xx / 0.30) ** 1.6)
height = np.exp(-(((yy - 0.45) / 0.30) ** 2))
alpha = np.clip(edge * height * (0.35 + 0.65 * n) - 0.10, 0, 1) ** 1.3
alpha *= 0.75
rgb = np.dstack([np.full((N, N), 0.30), np.full((N, N), 0.26), np.full((N, N), 0.20)])
save('Decal_Scuff', rgb, alpha)

# --- 天井から垂れた水の筋 ----------------------------------------------------
# 上端が濃く、下に向かって細く薄くなる。縁が濃い（乾いた縁に粒子が残る）。
N = 1024
yy, xx = np.mgrid[0:N, 0:N] / (N - 1.0)
n = fbm((N, N), octaves=6)
runs = np.zeros((N, N), np.float32)
for cx, w, ln, amp in [(0.30, 0.030, 0.85, 1.0), (0.36, 0.018, 0.55, 0.7),
                       (0.62, 0.026, 0.72, 0.9), (0.68, 0.014, 0.40, 0.6)]:
    wob = (fbm((N, N), octaves=3)[:, :1] - 0.5) * 0.05      # 垂れは真っ直ぐには落ちない
    d = np.abs(xx - (cx + wob))
    body = np.exp(-(d / (w * (1.0 + yy * 1.8))) ** 2)        # 下ほど広がって薄まる
    fade = np.clip(1.0 - yy / ln, 0, 1) ** 0.7
    runs = np.maximum(runs, body * fade * amp)
rim = np.clip(np.abs(np.gradient(runs, axis=1)) * 26.0, 0, 1)   # 乾いた縁
alpha = np.clip((runs * 0.55 + rim * 0.35) * (0.55 + 0.45 * n), 0, 1) * 0.90
rgb = np.dstack([np.full((N, N), 0.34), np.full((N, N), 0.28), np.full((N, N), 0.19)])
save('Decal_Drip', rgb, alpha)

# --- カーペットの通行帯 ------------------------------------------------------
# 毛足が踏み倒されて色が沈み、寝た毛が光を返すので艶は上がる。
# 縁ははっきりせず、外側ほど薄い。
N = 1024
yy, xx = np.mgrid[0:N, 0:N] / (N - 1.0)
n = fbm((N, N), octaves=5)
band = np.exp(-(((xx - 0.5) / 0.30) ** 2))
ends = np.clip(np.minimum(yy / 0.14, (1 - yy) / 0.14), 0, 1)   # 端は自然に消す
alpha = np.clip(band * ends * (0.45 + 0.55 * n), 0, 1) * 0.62
rgb = np.dstack([np.full((N, N), 0.42), np.full((N, N), 0.36), np.full((N, N), 0.26)])
save('Decal_Traffic', rgb, alpha)

# --- 家具の脚の凹み ----------------------------------------------------------
# 長く置かれた家具の脚。周囲より色が濃く残り（日焼けしない）、毛足が潰れる。
N = 512
yy, xx = np.mgrid[0:N, 0:N] / (N - 1.0)
n = fbm((N, N), octaves=4)
d = np.hypot(xx - 0.5, yy - 0.5)
alpha = np.clip((1.0 - d / 0.34), 0, 1) ** 0.8 * (0.7 + 0.3 * n) * 0.65
rgb = np.dstack([np.full((N, N), 0.46), np.full((N, N), 0.40), np.full((N, N), 0.29)])
save('Decal_Dent', rgb, alpha)

# --- 掃除機の筋 --------------------------------------------------------------
# 毛足が交互に逆向きに寝るので、色ではなく艶が縞になる。
# アルベドには効かせず、平滑度だけを縞状に上げる。
N = 1024
yy, xx = np.mgrid[0:N, 0:N] / (N - 1.0)
n = fbm((N, N), octaves=4)
BANDS = 10                                  # 3m の投影に対して 0.30m ピッチ
stripe = 0.5 + 0.5 * np.cos(yy * BANDS * 2 * np.pi)
edge = np.clip(np.minimum(xx / 0.10, (1 - xx) / 0.10), 0, 1)
ends = np.clip(np.minimum(yy / 0.10, (1 - yy) / 0.10), 0, 1)
alpha = stripe * edge * ends * (0.6 + 0.4 * n) * 0.45
rgb = np.dstack([np.full((N, N), 0.50), np.full((N, N), 0.44), np.full((N, N), 0.32)])
save('Decal_Vacuum', rgb, alpha)

print('出力先:', OUT)
