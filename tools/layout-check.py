"""Level0Layout を Python で写して、間取りの変更が通行性に何をしたかを測る。

Unity を起動せずに壁の確率を振れるようにしておく。壁を増やす変更は
「行けない場所ができる」形で失敗するので、採用する前にここで確かめる。
"""
import sys
from collections import deque

M32 = 0xFFFFFFFF
BAY, PITCH = 1.80, 3.60
THIN, THICK, COLHALF = 1.20, 1.80, 0.60
RUN, ZONE = 4, 4


def h32(x, y, salt):
    h = ((x * 0x27D4EB2D) & M32) ^ ((y * 0x165667B1) & M32) ^ ((salt * 0x9E3779B1) & M32)
    h ^= h >> 15; h = (h * 0x2C1B3C6D) & M32
    h ^= h >> 12; h = (h * 0x297A2D39) & M32
    h ^= h >> 15
    return h


def unit(x, y, salt):
    return (h32(x, y, salt) & 0xFFFFFF) / 16777215.0


def fdiv(a, b):
    return a // b if a >= 0 else -((-a + b - 1) // b)


class Layout:
    """band = (大部屋, 標準, 小割り) の壁帯確率。vary=False なら全域 0.34。"""

    def __init__(self, seed, vary=True, band=(0.13, 0.34, 0.46),
                 hall_p=0.11, warren_p=0.92, col_hall=0.34):
        self.s = seed; self.vary = vary; self.band = band
        self.hall_p, self.warren_p, self.col_hall = hall_p, warren_p, col_hall

    def space(self, i, j):
        if not self.vary:
            return 1
        u = unit(fdiv(i, ZONE), fdiv(j, ZONE), self.s * 7 + 51)
        if u < self.hall_p:
            return 0
        if u > self.warren_p:
            return 2
        return 1

    def chance(self, i, j):
        return self.band[self.space(i, j)]

    def wall_x(self, i, j):
        if unit(i, fdiv(j, RUN), self.s * 7 + 11) > self.chance(i, j):
            return False
        return unit(i, j, self.s * 7 + 12) < 0.86

    def wall_z(self, i, j):
        if unit(fdiv(i, RUN), j, self.s * 7 + 21) > self.chance(i, j):
            return False
        return unit(i, j, self.s * 7 + 22) < 0.86

    def thick_x(self, i, j):
        return THICK if unit(i, fdiv(j, RUN), self.s * 7 + 13) < 0.28 else THIN

    def thick_z(self, i, j):
        return THICK if unit(fdiv(i, RUN), j, self.s * 7 + 23) < 0.28 else THIN

    @staticmethod
    def _shift(t, h):
        return 0.0 if t <= THIN + 0.01 else (0.30 if (h & 1) == 0 else -0.30)

    def cx(self, i, j):
        return i * PITCH + self._shift(self.thick_x(i, j),
                                       h32(i, fdiv(j, RUN), self.s * 7 + 14))

    def cz(self, i, j):
        return j * PITCH + self._shift(self.thick_z(i, j),
                                       h32(fdiv(i, RUN), j, self.s * 7 + 24))

    def column(self, i, j):
        p = self.col_hall if self.space(i, j) == 0 else 0.075
        if unit(i, j, self.s * 7 + 31) > p:
            return False
        return not (self.wall_x(i, j) or self.wall_x(i, j - 1)
                    or self.wall_z(i, j) or self.wall_z(i - 1, j))

    def solid(self, x, z):
        i0, j0 = int(x // PITCH), int(z // PITCH)
        for i in range(i0 - 1, i0 + 2):
            for j in range(j0 - 1, j0 + 2):
                if self.wall_x(i, j):
                    t = self.thick_x(i, j) * 0.5
                    if abs(x - self.cx(i, j)) < t and j * PITCH < z < (j + 1) * PITCH:
                        return True
                if self.wall_z(i, j):
                    t = self.thick_z(i, j) * 0.5
                    if abs(z - self.cz(i, j)) < t and i * PITCH < x < (i + 1) * PITCH:
                        return True
                if self.column(i, j) and abs(x - i * PITCH) < COLHALF \
                        and abs(z - j * PITCH) < COLHALF:
                    return True
        return False


def clear(L, x, z, r=0.45):
    import math
    if L.solid(x, z):
        return False
    for a in range(16):
        t = a * math.pi / 8
        c, s = math.cos(t), math.sin(t)
        if L.solid(x + c * r, z + s * r) or L.solid(x + c * r * 0.6, z + s * r * 0.6):
            return False
    return True


def flood(L, half_m=60.0, g=0.60, r=0.45):
    """原点近くの空いた点から 0.6m 格子で塗り、届く割合を返す。"""
    n = int(half_m / g)
    start = None
    for ring in range(n):
        for di in range(-ring, ring + 1):
            for dj in range(-ring, ring + 1):
                if max(abs(di), abs(dj)) != ring:
                    continue
                if clear(L, di * g, dj * g, r):
                    start = (di, dj); break
            if start: break
        if start: break
    openset = set()
    for i in range(-n, n + 1):
        for j in range(-n, n + 1):
            if clear(L, i * g, j * g, r):
                openset.add((i, j))
    seen = {start}
    q = deque([start])
    while q:
        ci, cj = q.popleft()
        for d in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            k = (ci + d[0], cj + d[1])
            if k in openset and k not in seen:
                seen.add(k); q.append(k)
    return len(seen), len(openset), len(openset) - len(seen)


if __name__ == "__main__":
    seed = 1996
    cases = [
        ("変更前 (全域 0.34)", Layout(seed, vary=False)),
        ("採用値 (0.13/0.34/0.46)", Layout(seed)),
        ("小割りを 0.42 に", Layout(seed, band=(0.13, 0.34, 0.42))),
        ("小割りを 0.52 に", Layout(seed, band=(0.13, 0.34, 0.52))),
    ]
    # 壁の面が 0.6m のタイル格子に乗っているか。乗っていない面は
    # 天井のマスを二分し、そこだけ天井が抜けて黒い帯になる。
    L = cases[1][1]
    off = tot = 0
    for i in range(-30, 31):
        for j in range(-30, 31):
            for c, t in ((L.cx(i, j), L.thick_x(i, j)) if L.wall_x(i, j) else (None, 0),):
                if c is None:
                    continue
                for f in (c - t * 0.5, c + t * 0.5):
                    tot += 1
                    if abs(round(f / 0.60) * 0.60 - f) > 1e-6:
                        off += 1
    print(f"壁面がタイル格子に乗らないもの: {off} / {tot}")
    print()
    for name, L in cases:
        r, tot, lost = flood(L, half_m=float(sys.argv[1]) if len(sys.argv) > 1 else 48.0)
        area = tot * 0.36
        print(f"{name:28s} 通行可 {area:7.0f} m2  到達 {r*100.0/tot:5.1f}%  "
              f"孤立 {lost*0.36:6.0f} m2")
