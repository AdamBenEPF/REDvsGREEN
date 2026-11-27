"""Simulateur RED vs GREEN : reproduction du jeu Unity du prof (squelette + IA verte), en 2,5 D.

Fidèle au code C# pour : arbres verts (drone, tourelle), missiles (Missile.cs), grenades (Rocket.cs / Turret.cs),
santé, Annexe A. Approché pour : NavMeshAgent (grille 0,5 m issue du NavMesh exporté), rotation des tourelles,
relief (les missiles vont tout droit en xz), collisions entre unités (ignorées).
"""
import json, math, os, random
import numpy as np
from scipy.sparse import csr_matrix
from scipy.sparse.csgraph import dijkstra

DT = 0.04
G = 9.81

# ---------------------------------------------------------------- carte
HERE = os.path.dirname(os.path.abspath(__file__))
_m = np.load(os.path.join(HERE, 'map.npz'))
NAV = _m['nav']; NAVH = _m['navh']; TERR = _m['H']; TRI = _m['tri']
XMIN = float(_m['xmin']); ZMIN = float(_m['zmin']); STEP = float(_m['step'])
N = NAV.shape[0]
_U = json.load(open(os.path.join(HERE, 'units.json')))
CRATES = [(o['center'][0] - o['size'][0] / 2, o['center'][0] + o['size'][0] / 2,
           o['center'][2] - o['size'][2] / 2, o['center'][2] + o['size'][2] / 2) for o in _U['obstacles']]
MAX_STEP_H = 3.0  # même surface du NavMesh (étiquette TRI)

def cell(x, z):
    return int(round((z - ZMIN) / STEP)), int(round((x - XMIN) / STEP))

def in_grid(i, j):
    return 0 <= i < N and 0 <= j < N

def walk(i, j):
    return in_grid(i, j) and NAV[i, j]

def nav_h(x, z):
    i, j = cell(x, z)
    if in_grid(i, j) and NAV[i, j]: return float(NAVH[i, j])
    if in_grid(i, j) and not math.isnan(TERR[i, j]): return float(TERR[i, j])
    return 0.0

def terr_h(x, z):
    i, j = cell(x, z)
    if in_grid(i, j) and not math.isnan(TERR[i, j]): return float(TERR[i, j])
    return None

# graphe de la grille (8 voisins, marche de hauteur limitée)
_rows, _cols, _w = [], [], []
for i in range(N):
    for j in range(N):
        if not NAV[i, j]: continue
        for di, dj in ((0, 1), (1, 0), (1, 1), (1, -1)):
            a, b = i + di, j + dj
            if walk(a, b) and TRI[i, j] == TRI[a, b] and abs(NAVH[i, j] - NAVH[a, b]) < MAX_STEP_H:
                if di and dj and not (walk(i + di, j) and walk(i, j + dj)): continue
                w = STEP * math.hypot(di, dj)
                _rows += [i * N + j, a * N + b]; _cols += [a * N + b, i * N + j]; _w += [w, w]
GRAPH = csr_matrix((_w, (_rows, _cols)), shape=(N * N, N * N))
from scipy.sparse.csgraph import connected_components
_ncomp, COMP = connected_components(GRAPH, directed=False)
COMP = COMP.reshape(N, N)
_cache = {}
_near_cache = {}

def nearest_in_comp(i, j, comp):
    key = (i, j, comp)
    r = _near_cache.get(key)
    if r is None:
        ii, jj = np.nonzero(NAV & (COMP == comp))
        k = int(np.argmin((ii - i) ** 2 + (jj - j) ** 2))
        r = (int(ii[k]), int(jj[k])); _near_cache[key] = r
    return r

def nearest_walk(x, z):
    i, j = cell(x, z)
    if walk(i, j): return i, j
    best = None
    for r in range(1, 30):
        for a in range(i - r, i + r + 1):
            for b in (j - r, j + r) if abs(a - i) != r else range(j - r, j + r + 1):
                if walk(a, b):
                    d = (a - i) ** 2 + (b - j) ** 2
                    if best is None or d < best[0]: best = (d, a, b)
        if best: return best[1], best[2]
    return i, j

def field(goal):
    """distances et prédécesseurs depuis la cellule but"""
    f = _cache.get(goal)
    if f is None:
        if len(_cache) > 3000: _cache.clear()
        dist, pred = dijkstra(GRAPH, indices=goal[0] * N + goal[1], return_predecessors=True, directed=False)
        f = (dist, pred); _cache[goal] = f
    return f

def line_ok(x0, z0, x1, z1):
    """ligne droite praticable sur le NavMesh (sans marche trop haute)"""
    L = math.hypot(x1 - x0, z1 - z0); n = max(1, int(L / 0.25))
    ph = None
    for k in range(n + 1):
        t = k / n; i, j = cell(x0 + (x1 - x0) * t, z0 + (z1 - z0) * t)
        if not walk(i, j): return False
        h = (TRI[i, j], NAVH[i, j])
        if ph is not None and (h[0] != ph[0] or abs(h[1] - ph[1]) > MAX_STEP_H): return False
        ph = h
    return True

def plan(x0, z0, x1, z1):
    """chemin (liste de points) vers le point du NavMesh joignable le plus proche de (x1,z1)"""
    s = nearest_walk(x0, z0)
    gi, gj = cell(x1, z1)
    if not walk(gi, gj) or COMP[gi, gj] != COMP[s]:
        gi, gj = nearest_in_comp(gi, gj, COMP[s])
    dist, pred = field((gi, gj))
    sidx = s[0] * N + s[1]
    if not np.isfinite(dist[sidx]):
        # but injoignable : point joignable le plus proche du but (comme NavMesh)
        return None
    gx, gz = XMIN + gj * STEP, ZMIN + gi * STEP
    if line_ok(x0, z0, gx, gz): return [(gx, gz)], float(math.hypot(gx - x0, gz - z0))
    cells = []; k = sidx
    while k >= 0 and k != gi * N + gj:
        k = pred[k]
        if k < 0: break
        cells.append((XMIN + (k % N) * STEP, ZMIN + (k // N) * STEP))
    if not cells: cells = [(gx, gz)]
    # lissage par visibilité
    pts = []; cx, cz = x0, z0; idx = 0
    while idx < len(cells):
        far = idx
        for t in range(len(cells) - 1, idx, -1):
            if line_ok(cx, cz, *cells[t]): far = t; break
        pts.append(cells[far]); cx, cz = cells[far]; idx = far + 1
    return pts, float(dist[sidx])

# Joignabilité depuis le camp rouge, pour rendre un but injoignable joignable (point le plus proche)
_red0 = [u for u in _U['units'] if u['tag'] == '1' and u['type'] == 'Drone'][0]['pos']
_dist_from_red, _ = dijkstra(GRAPH, indices=nearest_walk(_red0[0], _red0[2])[0] * N + nearest_walk(_red0[0], _red0[2])[1], return_predecessors=True, directed=False)
REACH = np.isfinite(_dist_from_red).reshape(N, N)

def nearest_reachable(x, z):
    i, j = cell(x, z)
    if in_grid(i, j) and REACH[i, j]: return x, z
    ii, jj = np.nonzero(REACH)
    k = np.argmin((ii - i) ** 2 + (jj - j) ** 2)
    return XMIN + jj[k] * STEP, ZMIN + ii[k] * STEP

def can_step(x0, z0, x1, z1):
    i, j = cell(x1, z1); i0, j0 = cell(x0, z0)
    if not walk(i, j): return False
    if not walk(i0, j0): return True
    return COMP[i, j] == COMP[i0, j0] and abs(NAVH[i, j] - NAVH[i0, j0]) < 1.0

def missile_blocked(x0, z0, x1, z1):
    for (a, b, c, d) in CRATES:
        # intersection segment / rectangle (Liang-Barsky)
        t0, t1 = 0.0, 1.0; dx, dz = x1 - x0, z1 - z0; ok = True
        for p, q in ((-dx, x0 - a), (dx, b - x0), (-dz, z0 - c), (dz, d - z0)):
            if abs(p) < 1e-12:
                if q < 0: ok = False; break
            else:
                r = q / p
                if p < 0: t0 = max(t0, r)
                else: t1 = min(t1, r)
                if t0 > t1: ok = False; break
        if ok: return True
    return False

def quat_look(yaw, pitch):
    # Ry(yaw) * Rx(pitch)
    cy, sy = math.cos(yaw / 2), math.sin(yaw / 2); cp, sp = math.cos(pitch / 2), math.sin(pitch / 2)
    return (cy * cp, cy * sp, sy * cp, -sy * sp)  # (w, x, y, z)

def yaw_of(dx, dz):
    return math.degrees(math.atan2(dx, dz))

def dangle(a, b):
    return (b - a + 180.0) % 360.0 - 180.0


# ---------------------------------------------------------------- unités
class Unit:
    __slots__ = ('id', 'name', 'side', 'kind', 'x', 'z', 'y', 'yaw', 'hp', 'alive', 'vx', 'vz', 'speed', 'path',
                 'stop_dist', 'stopped', 'update_rot', 'ai', 'head', 'brain', 'repath')
    def __init__(self, uid, u):
        self.id = uid; self.name = u['name']; self.side = 'R' if u['tag'] == '1' else 'G'
        self.kind = 'D' if u['type'] == 'Drone' else 'T'
        self.x, self.z = u['pos'][0], u['pos'][2]
        self.yaw = u['yaw']; self.hp = 50.0 if self.kind == 'D' else 100.0; self.alive = True
        self.vx = self.vz = 0.0; self.speed = 10.0; self.path = None; self.stop_dist = 0.0; self.stopped = True
        self.update_rot = True; self.ai = None; self.head = None; self.brain = {}; self.repath = 0.0
        self.y = (nav_h(self.x, self.z) + 1.0) if self.kind == 'D' else u['pos'][1]

    # ------------------------------------------------ NavMeshAgent approché
    def set_destination(self, x, z, stop_dist=0.0):
        r = plan(self.x, self.z, x, z)
        if r is None: self.path = None; self.stopped = True; return False
        self.path = list(r[0]); self.stop_dist = stop_dist; self.stopped = False
        return True

    def remaining(self):
        if not self.path: return 0.0
        d = 0.0; px, pz = self.x, self.z
        for (a, b) in self.path:
            d += math.hypot(a - px, b - pz); px, pz = a, b
        return d

    def stop(self):
        self.stopped = True; self.path = None

    def move(self, dt):
        dvx, dvz = 0.0, 0.0
        if not self.stopped and self.path:
            while len(self.path) > 1 and math.hypot(self.path[0][0] - self.x, self.path[0][1] - self.z) < 0.5:
                self.path.pop(0)
            tx, tz = self.path[0]
            dx, dz = tx - self.x, tz - self.z; L = math.hypot(dx, dz)
            rem = self.remaining() - self.stop_dist
            if rem <= 0.05 and len(self.path) <= 1:
                pass  # arrivé
            elif L > 1e-6:
                v = min(self.speed, math.sqrt(max(0.0, 2 * 8.0 * max(rem, 0.0))))  # freinage automatique
                dvx, dvz = dx / L * v, dz / L * v
        ax, az = dvx - self.vx, dvz - self.vz; A = math.hypot(ax, az); amax = 8.0 * dt
        if A > amax: ax *= amax / A; az *= amax / A
        self.vx += ax; self.vz += az
        nx_, nz_ = self.x + self.vx * dt, self.z + self.vz * dt
        if can_step(self.x, self.z, nx_, nz_):
            self.x, self.z = nx_, nz_
        elif can_step(self.x, self.z, nx_, self.z):
            self.x = nx_; self.vz *= 0.5
        elif can_step(self.x, self.z, self.x, nz_):
            self.z = nz_; self.vx *= 0.5
        else:
            self.vx *= 0.3; self.vz *= 0.3
        self.y = nav_h(self.x, self.z) + 1.0
        sp = math.hypot(self.vx, self.vz)
        if self.update_rot and sp > 0.3:
            want = yaw_of(self.vx, self.vz); d = dangle(self.yaw, want); m = 180.0 * dt
            self.yaw += max(-m, min(m, d))

    def vel(self):
        return self.vx, self.vz


class Battle:
    def __init__(self, seed, red_policy, red_turret_policy=None):
        self.rng = random.Random(seed)
        self.t = 0.0
        self.units = [Unit(k, u) for k, u in enumerate(_U['units'])]
        for u in self.units:
            if u.kind == 'T':
                u.head = (u.yaw, 0.0)  # (lacet, élévation) de la tête
        self.missiles = []  # [x, z, dirx, dirz, speed, age, side, h]
        self.grenades = []  # [t_impact, x, y, z, side, shooter]
        self.red_policy = red_policy
        self.stats = {'R_msl': 0, 'G_msl': 0, 'R_hit': 0, 'G_hit': 0, 'R_gren': 0, 'G_gren': 0,
                      'R_dmg_msl': 0.0, 'R_dmg_gren': 0.0, 'G_dmg_msl': 0.0, 'G_dmg_gren': 0.0,
                      'R_turrets_lost_t': None}
        self.events = []
        red_policy.init(self)

    def enemies(self, side, kind=None):
        return [u for u in self.units if u.alive and u.side != side and (kind is None or u.kind == kind)]

    def friends(self, side, kind=None):
        return [u for u in self.units if u.alive and u.side == side and (kind is None or u.kind == kind)]

    # ------------------------------------------------ armes
    AIM_NOISE = 0.0  # écart-type (°) ajouté au cap au moment du tir : relief, dérive, évitement (calibrage)

    def shoot_missiles(self, u, target=None):
        yaw = u.yaw + (self.rng.gauss(0.0, self.AIM_NOISE) if self.AIM_NOISE else 0.0)
        fx, fz = math.sin(math.radians(yaw)), math.cos(math.radians(yaw))
        if target is not None:
            self.stats[u.side + '_shots' + target.kind] = self.stats.get(u.side + '_shots' + target.kind, 0) + 1
        rx, rz = fz, -fx
        for s in (-0.34, 0.34):
            self.missiles.append([u.x + rx * s + fx * 0.3, u.z + rz * s + fz * 0.3, fx, fz, u.speed, 0.0, u.side])
        self.stats[u.side + '_msl'] += 2

    def turret_rotation_time(self, u, tx, ty, tz):
        """Turret.RotateTowards : durée = Quaternion.Angle(tête, LookRotation(vitesse initiale)) / 90 °/s.
        LookRotation = lacet puis tangage : l'angle relatif tient compte du lacet complet, même pour un tir très cambré."""
        dx, dy, dz = tx - u.x, ty - (u.y + 1.5), tz - u.z
        vx, vy, vz = dx / 4.0, (dy + 0.5 * G * 16.0) / 4.0, dz / 4.0
        yaw = math.radians(yaw_of(vx, vz)); pitch = -math.atan2(vy, math.hypot(vx, vz))  # Unity : tangage négatif = vers le haut
        q1 = quat_look(*[math.radians(a) for a in u.head]); q2 = quat_look(yaw, pitch)
        dot = abs(sum(p * q for p, q in zip(q1, q2)))
        ang = 2 * math.degrees(math.acos(min(1.0, dot)))
        u.head = (math.degrees(yaw), math.degrees(pitch))
        return ang / 90.0

    def fire_grenade(self, u, tx, ty, tz):
        self.grenades.append([self.t + 4.0, tx, ty, tz, u.side, u.id])
        self.stats[u.side + '_gren'] += 1

    def damage(self, v, amount, cause):
        if not v.alive: return
        v.hp -= amount
        att = 'G' if v.side == 'R' else 'R'
        self.stats[att + '_dmg_' + cause] += amount
        if v.hp <= 0:
            v.alive = False; v.vx = v.vz = 0
            self.events.append((self.t, v.side, v.kind, cause, v.x, v.z, v.name))
            if v.side == 'R' and v.kind == 'T' and not self.friends('R', 'T') and self.stats['R_turrets_lost_t'] is None:
                self.stats['R_turrets_lost_t'] = self.t

    def step_missiles(self, dt):
        if not self.missiles: return
        keep = []
        targets = {s: [v for v in self.units if v.alive and v.side == s] for s in 'RG'}
        for m in self.missiles:
            x, z, fx, fz, sp, age, side = m
            sp = min(20.0, sp + 40.0 * dt); age += dt
            nx_, nz_ = x + fx * sp * dt, z + fz * sp * dt
            if age > 3.0 or terr_h(nx_, nz_) is None: continue
            # collision avec une unité ennemie (premier touché le long du segment)
            best = None
            for v in targets['G' if side == 'R' else 'R']:
                r = 0.44 if v.kind == 'D' else 1.04
                if v.kind == 'T':
                    # sphère de la tourelle centrée 1 m au-dessus de sa base ; missile à ~1 m au-dessus du sol
                    hm = nav_h(v.x, v.z) + 1.0
                    dy = hm - (v.y + 1.0)
                    if abs(dy) >= 1.0: continue
                    r = math.sqrt(1.0 - dy * dy) + 0.04
                px, pz = v.x - x, v.z - z
                L = sp * dt
                tproj = max(0.0, min(L, px * fx + pz * fz))
                cx, cz = x + fx * tproj, z + fz * tproj
                if (v.x - cx) ** 2 + (v.z - cz) ** 2 <= r * r:
                    if best is None or tproj < best[0]: best = (tproj, v)
            if missile_blocked(x, z, nx_, nz_):
                # caisse touchée avant l'unité ?
                if best is None: continue
            if best is not None:
                self.stats[side + '_hit'] += 1
                self.stats[side + '_hit' + best[1].kind] = self.stats.get(side + '_hit' + best[1].kind, 0) + 1
                self.damage(best[1], 5.0, 'msl')
                continue
            keep.append([nx_, nz_, fx, fz, sp, age, side])
        self.missiles = keep

    def step_grenades(self):
        keep = []
        for g in self.grenades:
            if g[0] > self.t: keep.append(g); continue
            _, gx, gy, gz, side, _ = g
            for v in self.units:
                if not v.alive or v.side == side: continue
                if v.kind == 'D':
                    dy = max(0.0, abs(v.y - gy) - 0.6)
                    d = math.sqrt((v.x - gx) ** 2 + (v.z - gz) ** 2 + dy * dy)
                    if d <= 5.4: self.damage(v, 10.0, 'gren')
                else:
                    d = math.sqrt((v.x - gx) ** 2 + (v.z - gz) ** 2 + (v.y + 1.0 - gy) ** 2)
                    if d <= 6.0: self.damage(v, 10.0, 'gren')
        self.grenades = keep

    # ------------------------------------------------ boucle
    def run(self, tmax=400.0):
        greens = [u for u in self.units if u.side == 'G']
        for u in greens:
            u.ai = GreenDroneAI(u, self) if u.kind == 'D' else GreenTurretAI(u, self)
        while self.t < tmax:
            self.t += DT
            self.red_policy.update(self, DT)
            for u in self.units:
                if u.alive and u.side == 'G': u.ai.update(DT)
            for u in self.units:
                if u.alive and u.kind == 'D': u.move(DT)
            self.step_missiles(DT)
            self.step_grenades()
            r = self.friends('R'); g = self.friends('G')
            if not g: return self.result('VICTORY')
            if not r: return self.result('DEFEAT')
            if not any(u.kind == 'D' for u in r + g):
                # deadlock : plus que des tourelles, aucune à portée
                if not any(5 < dist3(a, b) < 50 for a in r for b in g): return self.result('DEADLOCK')
        return self.result('TIMEOUT')

    def result(self, res):
        r = self.friends('R'); g = self.friends('G')
        out = dict(result=res, t=round(self.t, 1),
                   red_drones=sum(u.kind == 'D' for u in r), red_turrets=sum(u.kind == 'T' for u in r),
                   red_hp=sum(u.hp for u in r), green_drones=sum(u.kind == 'D' for u in g),
                   green_turrets=sum(u.kind == 'T' for u in g), green_hp=sum(u.hp for u in g))
        out.update(self.stats)
        k = {}
        for (t, side, kind, cause, *_ ) in self.events: k[f'{side}{kind}_{cause}'] = k.get(f'{side}{kind}_{cause}', 0) + 1
        out.update(k)
        return out


def dist3(a, b):
    return math.sqrt((a.x - b.x) ** 2 + (a.y - b.y) ** 2 + (a.z - b.z) ** 2)

def hdist(a, b):
    return math.hypot(a.x - b.x, a.z - b.z)


# ---------------------------------------------------------------- IA verte (code du prof)
class GreenDroneAI:
    """Repeater > Sequence[ RandomSelector(SelectEnemyDrone, SelectEnemyTurret) > MySeek(6 m) > SlerpRotate(4, 5°) > Shoot > Wait 1-2 ]"""
    def __init__(self, u, b):
        self.u = u; self.b = b; self.state = 'select'; self.target = None; self.timer = 0.0

    def update(self, dt):
        u, b = self.u, self.b
        if self.state == 'select':
            kinds = ['D', 'T']; b.rng.shuffle(kinds)
            self.target = None
            for k in kinds:
                c = b.enemies(u.side, k)
                if c: self.target = b.rng.choice(c); break
            if self.target is None: return
            u.speed = 10.0; u.update_rot = True
            u.set_destination(self.target.x, self.target.z, 6.0)
            self.state = 'seek'
        t = self.target
        if self.state == 'seek':
            if not t.alive: u.stop(); self.state = 'select'; return
            if u.path is None or u.remaining() <= 6.0:
                u.stop(); self.state = 'rotate'
            return
        if self.state == 'rotate':
            if not t.alive: self.state = 'select'; return
            want = yaw_of(t.x - u.x, t.z - u.z)
            u.yaw += dangle(u.yaw, want) * min(1.0, dt * 4.0)
            if abs(dangle(u.yaw, want)) < 5.0:
                b.shoot_missiles(u, t); self.state = 'wait'; self.timer = b.rng.uniform(1.0, 2.0)
            return
        if self.state == 'wait':
            self.timer -= dt
            if self.timer <= 0: self.state = 'select'


class GreenTurretAI:
    """Repeater > Sequence[ Selector(SelectEnemyTurret, SelectEnemyDrone) 5-50 m > SeekTarget > Shoot > Wait 1-3 ]"""
    def __init__(self, u, b):
        self.u = u; self.b = b; self.state = 'select'; self.target = None; self.timer = 0.0

    def update(self, dt):
        u, b = self.u, self.b
        if self.state == 'select':
            self.target = None
            for k in ('T', 'D'):
                c = [v for v in b.enemies(u.side, k) if 5 < dist3(u, v) < 50]
                if c: self.target = b.rng.choice(c); break
            if self.target is None: return
            self.timer = b.turret_rotation_time(u, self.target.x, self.target.y, self.target.z)
            self.state = 'rotate'
        if self.state == 'rotate':
            if not self.target.alive: self.state = 'select'; return
            self.timer -= dt
            if self.timer <= 0:
                t = self.target
                b.fire_grenade(u, t.x, t.y, t.z)
                self.state = 'wait'; self.timer = b.rng.uniform(1.0, 3.0)
            return
        if self.state == 'wait':
            self.timer -= dt
            if self.timer <= 0: self.state = 'select'


class GreenPolicy:
    """Rouge joué avec l'IA par défaut (squelette du prof) : arbres identiques aux verts, Selector des tourelles"""
    def init(self, b):
        for u in b.units:
            if u.side == 'R':
                u.ai = GreenDroneAI(u, b) if u.kind == 'D' else GreenTurretAI(u, b)
    def update(self, b, dt):
        for u in b.units:
            if u.alive and u.side == 'R': u.ai.update(dt)
