# -*- coding: utf-8 -*-
"""間取りを、壁と床の取り合い線が参照と一致するように追い込む。

Unity を起動しなくても、カメラが分かっていれば「この間取りなら取り合い線は
画面のどの行に出るか」は解析的に計算できる。列ごとに床の上を手前から奥へ
辿り、最初に塊に入る位置がその列の取り合い。参照から検出した行と比べれば、
間取りの良し悪しがレンダなしで測れる。

  python tools/fit-plan.py            現在の間取りを評価
  python tools/fit-plan.py --fit      塊の面を最適化して提案を出す
"""
import sys, json
import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage

CAL = json.load(open('artifacts/replica/calib.json'))
F, K1 = CAL['f'], CAL['k1']
PITCH_UP = np.radians(CAL['pitch'])
YAW = np.radians(36.59)
EYE = 1.345
CAM = np.array([4.533, EYE, 2.400])
W, H = CAL['W'], CAL['H']
CX, CY = W/2.0, H/2.0

fwd = np.array([np.sin(YAW)*np.cos(PITCH_UP), np.sin(PITCH_UP), np.cos(YAW)*np.cos(PITCH_UP)])
right = np.array([np.cos(YAW), 0.0, -np.sin(YAW)])
up = np.cross(fwd, right)

BLOCKS = [
    [0.00, 13.25, 6.43, 19.00],
    [12.21, 7.15, 19.80, 8.25],
    [14.83, 12.55, 15.50, 13.20],
    [19.70, 14.03, 25.00, 14.75],
    [16.95, 24.70, 20.00, 44.00],
    [21.62, 29.20, 25.20, 36.00],
    [25.20, 27.00, 31.00, 34.00],
    [8.50, 27.00, 11.00, 34.00],
]

# ---------------------------------------------------------------- 参照の取り合い
im = Image.open('artifacts/reference/Backrooms_1.jpg').convert('RGB')
A = np.asarray(im, np.float64)/255.
Y = A[..., 0]*.2126 + A[..., 1]*.7152 + A[..., 2]*.0722
Ys = ndimage.gaussian_filter(Y, 1.8)
gy = np.gradient(Ys, axis=0)
ref_v = np.full(W, np.nan)
for x in range(2, W-2):
    col = -gy[int(CY)+5:H-3, max(0, x-1):x+2].mean(1)
    k = int(np.argmax(col))
    if col[k] < 0.012:
        continue
    a, b, c = col[max(k-1, 0)], col[k], col[min(k+1, len(col)-1)]
    d = 0.5*(a-c)/(a-2*b+c) if (a-2*b+c) != 0 else 0
    ref_v[x] = int(CY)+5+k+d


def floor_points(u, vs):
    """列 u の行 vs に対応する床の点 (world x,z)。"""
    xn = (u-CX)/F
    yn = (vs-CY)/F
    if K1 != 0.0:
        for _ in range(6):
            r2 = xn*xn + yn*yn
            d = 1.0 + K1*r2
            xn = (u-CX)/F/d
            yn = (vs-CY)/F/d
    d = fwd[None, :] + xn*right[None, :] - yn[:, None]*up[None, :]
    t = (0.0-CAM[1])/d[:, 1]
    P = CAM[None, :] + t[:, None]*d
    return P[:, 0], P[:, 2], t


VS = np.arange(H-2, int(CY)+3, -1.0)      # 手前から奥へ


def predict(blocks):
    out = np.full(W, np.nan)
    bl = np.array(blocks)
    for u in range(2, W-2):
        px, pz, t = floor_points(u, VS)
        ok = t > 0
        inside = np.zeros(len(VS), bool)
        for (x0, z0, x1, z1) in bl:
            inside |= (px > x0) & (px < x1) & (pz > z0) & (pz < z1)
        inside &= ok
        idx = np.nonzero(inside)[0]
        if len(idx):
            out[u] = VS[idx[0]]
    return out


def score(pred, lo=2, hi=None):
    hi = hi or W-2
    m = ~np.isnan(pred) & ~np.isnan(ref_v)
    m[:lo] = False; m[hi:] = False
    if m.sum() < 50:
        return np.nan, 0
    d = np.abs(pred[m]-ref_v[m])
    return float(np.median(d)), int(m.sum())


pred = predict(BLOCKS)
med, n = score(pred)
print('現在の間取り: 取り合い行の差 中央値 %.1f px  (%d 列で比較)' % (med, n))
for a, b in ((2, 200), (200, 400), (400, 600), (600, 800), (800, 1000), (1000, 1298)):
    m, k = score(pred, a, b)
    print('   列 %4d-%4d  中央値 %6.1f px  (%d 列)' % (a, b, m, k))

# ---------------------------------------------------------------- 提案
SEGS = json.load(open('artifacts/replica/plan_segments.json'))
print('\n参照から取り出した壁面:')
for s in SEGS:
    nm = 'x=%6.2f  z %6.2f..%6.2f' % (s['off'], s['lo'], s['hi']) if s['axis'] == 0 \
        else 'z=%6.2f  x %6.2f..%6.2f' % (s['off'], s['lo'], s['hi'])
    print('   列 %4d-%4d  %s   ばらつき %.3f m  距離 %.1f m'
          % (s['col0'], s['col1'], nm, s['rms'], s['depth']))


def draw(blocks, pred, path):
    img = Image.new('RGB', (W, 420), (22, 23, 26))
    dr = ImageDraw.Draw(img)
    for y in range(420, 0, -50):
        dr.line([0, y, W, y], fill=(46, 48, 55))
    for x in range(0, W, 100):
        dr.line([x, 0, x, 420], fill=(46, 48, 55))
        dr.text((x+3, 404), str(x), fill=(110, 116, 128))
    for x in range(W):
        if not np.isnan(ref_v[x]):
            dr.point([(x, ref_v[x]-380)], fill=(217, 164, 65))
        if not np.isnan(pred[x]):
            dr.point([(x, pred[x]-380)], fill=(155, 224, 207))
    dr.text((8, 8), 'ORANGE = reference   TEAL = predicted   (row - 380)', fill=(200, 205, 215))
    img.save(path)


draw(BLOCKS, pred, 'artifacts/replica/baseline_curve.png')
print('\n-> artifacts/replica/baseline_curve.png')
