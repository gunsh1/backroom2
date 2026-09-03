# -*- coding: utf-8 -*-
"""吸音天井タイルのアトラスを作る。

現状は ambientCG の 1 枚を 6x6 に切って使っていたが、元が 1 タイルぶんの
写真なので、切ったセルの中に「タイルの縁」も「タイル 1 枚の模様の完結性」も
無い。実物の鉱物繊維タイル (Armstrong Cortega 相当) は次の要素でできている。

  繊維の亀裂 (fissure) … 押し出し方向に伸びた不規則な溝。1 枚ごとに違う
  針穴 (pinhole)       … 直径 1-2mm の穴が一様に散る
  縁の汚れ             … グリッドの隙間を空気が抜けるので、タイルの外周
                          6-10mm だけが埃を吸って暗くなる。実物で最も
                          目に付く手掛かりで、これが無いと「白い板」に見える
  雨漏りのシミ         … 全体の 1 割弱。外周に濃い潮目のリングが残る

8x8 = 64 枚を 1 枚のアトラスに焼く。メッシュ側で 8 通りの向きを掛けるので
実質 512 通りになり、同じ模様が並んで見えることはない。

    python tools/make-ceiling-textures.py
"""
import os
import numpy as np
from PIL import Image, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, 'BackroomsRT', 'Assets', '_Project', 'Art', 'Textures', 'OfficeCeiling001')
os.makedirs(OUT, exist_ok=True)

N = 8                # 8x8 枚
C = 512              # 1 枚あたり 512px = 600mm -> 0.85 px/mm
SIZE = N * C         # 4096
rng = np.random.default_rng(1996)
PX_PER_MM = C / 600.0


def fbm(h, w, octaves=6, gain=0.5, base=64):
    out = np.zeros((h, w), np.float32)
    amp, freq, norm = 1.0, 1.0, 0.0
    for _ in range(octaves):
        gh, gw = max(2, int(h / base * freq)), max(2, int(w / base * freq))
        g = rng.random((gh, gw)).astype(np.float32)
        layer = np.asarray(Image.fromarray((g * 255).astype(np.uint8)).resize((w, h), Image.BICUBIC),
                           np.float32) / 255.0
        out += layer * amp
        norm += amp
        amp *= gain
        freq *= 2.0
    return out / norm


def blur(a, r):
    return np.asarray(Image.fromarray((np.clip(a, 0, 1) * 255).astype(np.uint8))
                      .filter(ImageFilter.GaussianBlur(r)), np.float32) / 255.0


# ---------------------------------------------------------------- 亀裂
def fissures(seed_scale):
    """押し出し方向に伸びた不規則な溝。等方なノイズを一方向にだけ潰して作る。

    等高線状の輪郭が出ると地形図に見えるので、閾値の「下側」を丸ごと溝に
    する。実物の溝は線ではなく、面として抉れている。
    """
    n = fbm(C, C, octaves=5, base=int(9 * seed_scale))
    # 水平に引き伸ばす = 押し出し方向に伸びた繊維
    stretched = np.asarray(Image.fromarray((n * 255).astype(np.uint8))
                           .resize((C // 9, C), Image.BILINEAR)
                           .resize((C, C), Image.BICUBIC), np.float32) / 255.0
    thr = np.quantile(stretched, 0.30)
    groove = np.clip((thr - stretched) / 0.055, 0, 1) ** 0.8      # 面として抉れる
    # 細いひび。溝より浅く、向きが揃わない
    m2 = fbm(C, C, octaves=6, base=int(7 * seed_scale))
    crack = np.clip(1.0 - np.abs(m2 - np.quantile(m2, 0.5)) / 0.012, 0, 1) ** 1.6
    return blur(np.clip(groove + crack * 0.45, 0, 1), 0.8)


# ---------------------------------------------------------------- 針穴
def pinholes():
    """直径 1.6mm 前後の穴を 1 枚あたり 1400 個ほど。"""
    a = np.zeros((C, C), np.float32)
    count = 1400
    xs = rng.integers(4, C - 4, count)
    ys = rng.integers(4, C - 4, count)
    rr = np.clip(rng.normal(0.8 * PX_PER_MM, 0.22 * PX_PER_MM, count), 0.6, 3.0)
    yy, xx = np.mgrid[-3:4, -3:4].astype(np.float32)
    d = np.hypot(xx, yy)
    for x, y, r in zip(xs, ys, rr):
        stamp = np.clip(1.0 - d / max(r, 0.7), 0, 1)
        a[y - 3:y + 4, x - 3:x + 4] = np.maximum(a[y - 3:y + 4, x - 3:x + 4], stamp)
    return a


# ------------------------------------------------------- 1 枚ぶんを合成
def make_tile(idx):
    """returns (albedo HxWx3 linearish 0-1, height, ao, smooth)"""
    tone = 1.0 + (rng.random() - 0.5) * 0.055          # 枚ごとの色ムラ
    f = fissures(0.8 + rng.random() * 0.5)
    ph = pinholes()
    grit = fbm(C, C, octaves=6, base=140)              # 大きなうねり

    # --- 縁の汚れ -------------------------------------------------------
    # グリッドの隙間を空気が抜け、外周だけが埃を吸って暗くなる。
    u = np.linspace(0.0, 1.0, C, dtype=np.float32)
    du = np.minimum(u, 1.0 - u)
    edge = np.minimum(du[None, :], du[:, None])        # 0 = 縁
    soil = np.clip(1.0 - edge / 0.055, 0, 1) ** 1.5    # 外周 33mm
    soil = soil * (0.55 + 0.45 * fbm(C, C, octaves=4, base=90))

    # --- 明度 -----------------------------------------------------------
    v = np.full((C, C), 0.895, np.float32) * tone
    v *= 1.0 - f * 0.115                               # 溝は暗い
    v *= 1.0 - ph * 0.34                               # 針穴は深く暗い
    v *= 0.97 + 0.06 * grit
    v *= 1.0 - soil * 0.13                             # 縁の汚れ

    rgb = np.dstack([v, v * 0.985, v * 0.955])         # 素の材料はごくわずかに暖色

    # --- 雨漏りのシミ ---------------------------------------------------
    stain_kind = 0
    if idx % 11 == 3 or idx % 17 == 5:
        stain_kind = 2 if idx % 17 == 5 else 1
        cx, cy = rng.random() * 0.8 + 0.1, rng.random() * 0.8 + 0.1
        yy, xx = np.mgrid[0:C, 0:C] / (C - 1.0)
        d = np.hypot(xx - cx, yy - cy) / (0.16 + rng.random() * 0.16)
        d = d * (0.75 + 0.5 * fbm(C, C, octaves=4, base=70))   # 縁は不規則
        body = np.clip(1.0 - d, 0, 1) ** 0.55
        ring = np.clip(1.0 - np.abs(d - 0.92) / 0.10, 0, 1) ** 1.2   # 潮目
        amt = (body * 0.55 + ring * 0.85) * (0.55 if stain_kind == 1 else 1.0)
        amt = np.clip(amt, 0, 1)
        tint = np.array([0.62, 0.47, 0.26], np.float32)
        rgb = rgb * (1.0 - amt[..., None] * 0.75) + tint * amt[..., None] * 0.75

    height = 0.5 - f * 0.35 - ph * 0.5 + (grit - 0.5) * 0.10
    ao = np.clip(1.0 - f * 0.30 - ph * 0.55 - soil * 0.10, 0, 1)
    smooth = np.clip(0.075 - f * 0.03 - soil * 0.02, 0, 1)
    return rgb, height, ao, smooth


def normal_from_height(h, strength):
    gy, gx = np.gradient(h.astype(np.float32))
    nx, ny, nz = -gx * strength, -gy * strength, np.ones_like(h)
    l = np.sqrt(nx * nx + ny * ny + nz * nz)
    return np.dstack([nx / l, ny / l, nz / l]) * 0.5 + 0.5   # OpenGL (+Y up)


col = np.zeros((SIZE, SIZE, 3), np.float32)
msk = np.zeros((SIZE, SIZE, 4), np.float32)
nrm = np.zeros((SIZE, SIZE, 3), np.float32)

for j in range(N):
    for i in range(N):
        rgb, h, ao, sm = make_tile(j * N + i)
        y0, x0 = j * C, i * C
        col[y0:y0 + C, x0:x0 + C] = rgb
        nrm[y0:y0 + C, x0:x0 + C] = normal_from_height(h, 26.0)
        msk[y0:y0 + C, x0:x0 + C, 0] = 0.0        # metallic
        msk[y0:y0 + C, x0:x0 + C, 1] = ao
        msk[y0:y0 + C, x0:x0 + C, 2] = 0.0        # detail mask
        msk[y0:y0 + C, x0:x0 + C, 3] = sm         # smoothness
    print('  row %d/%d' % (j + 1, N))


def save(name, arr, mode='RGB'):
    p = os.path.join(OUT, name)
    Image.fromarray((np.clip(arr, 0, 1) * 255 + 0.5).astype(np.uint8), mode).save(p)
    print('%-38s %s' % (name, arr.shape))


save('CeilingTile_Atlas_Color.png', col)
save('CeilingTile_Atlas_MaskMap.png', msk, 'RGBA')
save('CeilingTile_Atlas_NormalGL.png', nrm)

# 検証: 縁が実際に暗いか、シミのある枚数はいくつか
mid = col[C // 2::C, C // 2::C, 0]
rim = col[8::C, C // 2::C, 0]
print('中央の明度 平均 %.3f / 縁 8px の明度 平均 %.3f  (縁が %.1f%% 暗い)'
      % (mid.mean(), rim.mean(), (1 - rim.mean() / mid.mean()) * 100))
print('出力先:', OUT)
