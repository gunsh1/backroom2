# -*- coding: utf-8 -*-
"""参照画像を床平面に投影し直して、平面図として読む。

壁と床の取り合いは床平面上にあるので、歪みを外してから床に投影し直せば
そのまま平面図の輪郭になる。壁の向こう側は必ず「カメラから見て奥へ伸びる
影」になるので、どこが壁でどこが床かも読める。

赤は現在の Level0Replica.cs の塊。緑はカメラと画角。

  python tools/reference-plan.py
"""
import json
import numpy as np
from PIL import Image, ImageDraw

REF = 'artifacts/reference/Backrooms_1.jpg'
CAL = json.load(open('artifacts/replica/calib.json'))
F = CAL['f']
K1 = CAL['k1']
PITCH_UP = np.radians(CAL['pitch'])          # 上向きが正
YAW = np.radians(36.59)                      # 複製シーンの world 基準
EYE = 1.345
CAM = np.array([4.533, EYE, 2.400])

im = Image.open(REF).convert('RGB')
W, H = im.size
A = np.asarray(im, np.float64)
CX, CY = W/2.0, H/2.0

fwd = np.array([np.sin(YAW)*np.cos(PITCH_UP), np.sin(PITCH_UP), np.cos(YAW)*np.cos(PITCH_UP)])
right = np.array([np.cos(YAW), 0.0, -np.sin(YAW)])
up = np.cross(fwd, right)

X0, X1, Z0, Z1, S = -3.0, 45.0, -3.0, 45.0, 21
nx, nz = int((X1-X0)*S), int((Z1-Z0)*S)
gx, gz = np.meshgrid(np.linspace(X0, X1, nx), np.linspace(Z0, Z1, nz))
d = np.stack([gx-CAM[0], np.full_like(gx, -CAM[1]), gz-CAM[2]], -1)
zc = d @ fwd
xc = d @ right
yc = d @ up
with np.errstate(divide='ignore', invalid='ignore'):
    xn = xc/zc
    yn = -yc/zc
r2 = xn*xn + yn*yn
dd = 1.0 + K1*r2
u = CX + F*xn*dd
v = CY + F*yn*dd
valid = (zc > 0.4) & np.isfinite(u) & np.isfinite(v)
ui = np.zeros_like(u, int); vi = np.zeros_like(v, int)
ui[valid] = np.clip(np.round(u[valid]), 0, W-1).astype(int)
vi[valid] = np.clip(np.round(v[valid]), 0, H-1).astype(int)
ok = valid & (u >= 0) & (u < W) & (v >= 0) & (v < H)
out = np.zeros((nz, nx, 3)); out[..., 0] = 26; out[..., 1] = 27; out[..., 2] = 30
out[ok] = A[vi[ok], ui[ok]]
out = out[::-1]

img = Image.fromarray(out.astype(np.uint8))
dr = ImageDraw.Draw(img)


def px(x, z):
    return ((x-X0)*S, nz-(z-Z0)*S)


for k in range(0, int(X1)+1, 6):
    dr.line([px(k, Z0), px(k, Z1)], fill=(66, 70, 80), width=1)
    dr.text((px(k, Z0)[0]+3, nz-15), '%d' % k, fill=(125, 131, 142))
for k in range(0, int(Z1)+1, 6):
    dr.line([px(X0, k), px(X1, k)], fill=(66, 70, 80), width=1)
    dr.text((4, px(X0, k)[1]-14), '%d' % k, fill=(125, 131, 142))

BLOCKS = [(0.00, 13.24, 6.43, 19.00), (12.21, 7.20, 19.80, 10.20),
          (14.77, 12.16, 21.60, 17.00), (17.09, 23.91, 21.00, 38.40),
          (21.60, 29.42, 27.60, 34.80), (28.80, 12.00, 34.20, 18.00),
          (30.00, 24.00, 36.00, 31.20)]
for (x0, z0, x1, z1) in BLOCKS:
    a = px(x0, z0); b = px(x1, z1)
    dr.rectangle([min(a[0], b[0]), min(a[1], b[1]),
                  max(a[0], b[0]), max(a[1], b[1])], outline=(255, 66, 56), width=2)

cp = px(CAM[0], CAM[2])
dr.ellipse([cp[0]-6, cp[1]-6, cp[0]+6, cp[1]+6], fill=(88, 255, 160))
for sgn in (-1, 1):
    a = YAW + sgn*np.arctan(W/2.0/F)
    e = np.array([CAM[0], CAM[2]]) + np.array([np.sin(a), np.cos(a)])*56
    dr.line([cp, px(e[0], e[1])], fill=(88, 255, 160), width=1)

img.save('artifacts/replica/plan_reference.png')
print('平面図 %dx%d  (1m = %d px)' % (nx, nz, S))
print('カメラ (%.2f, %.2f) 高さ %.3f  yaw %.2f deg  仰角 %.2f deg  水平画角 %.2f deg  k1 %+.4f'
      % (CAM[0], CAM[2], EYE, np.degrees(YAW), np.degrees(PITCH_UP),
         2*np.degrees(np.arctan(W/2/F)), K1))
print('-> artifacts/replica/plan_reference.png')
