# -*- coding: utf-8 -*-
"""自分のレンダのレンズ歪みと解像感を、シーン座標からの理想投影と比べて実測する。

複製シーンでは照明の world 座標が分かっている。理想的なピンホール投影の位置と、
実際に描かれた位置の差が、そのまま HDRP の LensDistortion が掛けている量になる。
参照画像で測った k1 に合わせるための intensity をここから逆算する。

器具は滲んで隣とつながるので、連結成分では拾えない。予測位置の周りに窓を置いて
その中の重心を取る。位置が既知だからこそできる測り方。

  python tools/measure-render-lens.py artifacts/shots/R1_match.png
"""
import sys, json
import numpy as np
from PIL import Image
from scipy import ndimage
from scipy.optimize import least_squares

MINE = sys.argv[1] if len(sys.argv) > 1 else 'artifacts/shots/R1_match.png'

# ---- 複製シーンの寸法 (Level0Replica.cs と一致させること) ----
CEIL, EYE, PITCH_M = 3.157, 1.345, 1.80
CAM = np.array([4.533, EYE, 2.400])
YAW = np.radians(36.59)
PITCH_UP = np.radians(2.27)
FOVV = 40.30
HALLX, HALLZ = 48.0, 57.6
BLOCKS = [(0.00, 13.25, 6.43, 19.00), (12.21, 7.15, 19.80, 8.25),
          (14.83, 12.55, 15.50, 13.20), (19.70, 14.03, 25.00, 14.75),
          (16.95, 24.70, 20.00, 44.00), (21.62, 29.20, 25.20, 36.00),
          (25.20, 27.00, 31.00, 34.00), (8.50, 27.00, 11.00, 34.00)]

im = Image.open(MINE).convert('RGB')
W, H = im.size
A = np.asarray(im, np.float64)/255.
Y = A[..., 0]*.2126 + A[..., 1]*.7152 + A[..., 2]*.0722

f = (H/2.0)/np.tan(np.radians(FOVV/2.0))
fwd = np.array([np.sin(YAW)*np.cos(PITCH_UP), np.sin(PITCH_UP), np.cos(YAW)*np.cos(PITCH_UP)])
right = np.array([np.cos(YAW), 0.0, -np.sin(YAW)])
up = np.cross(fwd, right)
print('%s  %dx%d   焦点距離 %.1f px (垂直画角 %.2f deg)' % (MINE, W, H, f, FOVV))


def free(cx, cz):
    for (x0, z0, x1, z1) in BLOCKS:
        if cx+0.3 > x0 and cx-0.3 < x1 and cz+0.3 > z0 and cz-0.3 < z1:
            return False
    return True


ideal, depth = [], []
for i in range(int(HALLX/PITCH_M)+1):
    for j in range(int(HALLZ/PITCH_M)+1):
        cx, cz = PITCH_M*i+0.9, PITCH_M*j+0.9
        if cx > HALLX or cz > HALLZ or not free(cx, cz):
            continue
        d = np.array([cx, CEIL, cz]) - CAM
        zc = d @ fwd
        if zc < 1.0:
            continue
        u = W/2.0 + f*(d @ right)/zc
        v = H/2.0 - f*(d @ up)/zc
        ideal.append((u, v)); depth.append(zc)
ideal = np.array(ideal); depth = np.array(depth)
inside = (ideal[:, 0] > 40) & (ideal[:, 0] < W-40) & (ideal[:, 1] > 20) & (ideal[:, 1] < H-20)
ideal, depth = ideal[inside], depth[inside]
print('画面に入る器具 (理想投影) %d 台' % len(ideal))

# ---- 予測位置の窓の中で重心を取る ----
Ys = ndimage.gaussian_filter(Y, 1.0)
meas, wts, keep = [], [], []
for (u, v), zc in zip(ideal, depth):
    # 器具は 0.6m 角。画面上の見かけの大きさから窓を決める
    px = f*0.6/zc
    r = max(6, min(70, px*1.15))
    x0, x1 = int(u-r), int(u+r)+1
    y0, y1 = int(v-r), int(v+r)+1
    if x0 < 0 or y0 < 0 or x1 > W or y1 > H:
        keep.append(False); meas.append((np.nan, np.nan)); wts.append(0.0); continue
    win = Ys[y0:y1, x0:x1]
    thr = win.max()-0.10
    m = win > thr
    # 窓の半分以上が光っていたら隣とつながっている。小さすぎるものは遠すぎる
    if m.sum() < 6 or m.sum() > 0.45*m.size or win.max() < 0.70:
        keep.append(False); meas.append((np.nan, np.nan)); wts.append(0.0); continue
    ys, xs = np.nonzero(m)
    w = win[m]-thr
    mx, my = x0+(xs*w).sum()/w.sum(), y0+(ys*w).sum()/w.sum()
    # 重心が窓の中心から大きく外れていたら隣の器具を拾っている
    if np.hypot(mx-u, my-v) > 0.55*r:
        keep.append(False); meas.append((np.nan, np.nan)); wts.append(0.0); continue
    meas.append((mx, my))
    wts.append(np.sqrt(m.sum()))
    keep.append(True)
meas = np.array(meas); wts = np.array(wts); keep = np.array(keep)
print('窓の中で重心が取れた器具 %d 台' % keep.sum())

P_ideal, P_det, Wt = ideal[keep], meas[keep], wts[keep]
raw = np.linalg.norm(P_ideal-P_det, axis=1)
print('理想投影との生の距離: 中央値 %.2f px  平均 %.2f  最大 %.2f'
      % (np.median(raw), raw.mean(), raw.max()))
c = np.array([W/2.0, H/2.0])


def model(q, P):
    k1, k2, s, tx, ty = q
    d = (P-c)/f
    r2 = (d*d).sum(1)
    dd = d*(1.0 + k1*r2[:, None] + k2*(r2*r2)[:, None])
    return c + f*dd*s + np.array([tx, ty])


best = None
for nm, mask in (('倍率と並進のみ', [0, 0, 1, 1, 1]),
                 ('k1 を含む', [1, 0, 1, 1, 1]),
                 ('k1, k2 を含む', [1, 1, 1, 1, 1])):
    mask = np.array(mask, bool)
    q0 = np.array([0.0, 0.0, 1.0, 0.0, 0.0])

    def rf(qq, mask=mask, q0=q0):
        q = q0.copy(); q[mask] = qq
        return ((model(q, P_ideal)-P_det)*Wt[:, None]).ravel()
    sol = least_squares(rf, q0[mask], loss='soft_l1', f_scale=1.5, max_nfev=4000)
    q = q0.copy(); q[mask] = sol.x
    e = np.linalg.norm(model(q, P_ideal)-P_det, axis=1)
    rc = np.hypot(W/2, H/2)/f
    shift = f*rc*(q[0]*rc**2 + q[1]*rc**4)
    print('  %-14s RMS %5.2f px  中央値 %5.2f  最大 %5.2f   '
          'k1 %+.5f  k2 %+.5f  倍率 %.5f  隅で %+.1f px'
          % (nm, np.sqrt((e**2).mean()), np.median(e), e.max(), q[0], q[1], q[2], shift))
    if nm == 'k1 を含む':
        best = dict(k1=float(q[0]), scale=float(q[2]), tx=float(q[3]), ty=float(q[4]),
                    corner_shift_px=float(shift), rms=float(np.sqrt((e**2).mean())),
                    used=int(keep.sum()), W=W, H=H, f=float(f))
json.dump(best, open('artifacts/replica/render_lens.json', 'w'), indent=1)
print('-> artifacts/replica/render_lens.json')
