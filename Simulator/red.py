"""IA rouge paramétrable : reproduit ArmyManagerRed (versions 7 à 15) et ses options, pour les comparer en simulation."""
import math
from world import *

DEFAULT = dict(
    hunters=9,
    assault_order=['TurretGreen (%d)' % i for i in (1, 2, 3, 4, 5, 6, 7, 8, 0)],
    priority_turrets=['TurretGreen (6)'],
    predict_clusters=True,
    cluster_radius=4.0,
    evade='endgame',          # none | hunters | all | endgame
    endgame_green=6,
    evade_jit=False,          # esquive "juste à temps" : seulement si l'impact arrive avant qu'on puisse partir plus tard
    jit_margin=0.6,           # marge de temps (s)
    strafe=False,
    firing_distance=14.0,
    hug=3.0,
    expected_dmg=7.0, dist_weight=1.0, parked_bonus=25.0,
    turret_focus_turrets=False,
    evade_in_wait=False,      # l'esquive peut aussi interrompre le Wait (Selector(Esquive, Wait) dans l'arbre)
    snipe=False,              # plus de drones verts : tirer sur les tourelles depuis un point hors de portée de toutes
    snipe_min=51.0, snipe_max=56.0,
    kite=False,               # chasseurs : bougent en permanence entre deux salves (repositionnement latéral)
    kite_leg=6.0,
)

GREN_R = 5.4
PRED_NOISE = 0.0  # erreur (m) de la prédiction des groupes verts dans le jeu réel (calibrage)  # rayon de la grenade + rayon du drone


class RedPolicy:
    def __init__(self, **kw):
        self.p = dict(DEFAULT); self.p.update(kw)

    # ------------------------------------------------------------------ init
    def init(self, b):
        self.b = b
        drones = [u for u in b.units if u.side == 'R' and u.kind == 'D']
        drones.sort(key=lambda u: u.name)
        self.role = {}
        for k, u in enumerate(drones):
            if k < self.p['hunters']: self.role[u.id] = 'H'
            else: self.role[u.id] = 'A' if (k - self.p['hunters']) % 2 == 0 else 'B'
        self.locks = {}       # chasseur -> drone vert
        self.squad_t = {}     # escouade -> tourelle verte
        self.reach = {}       # distance d'engagement des tourelles vertes
        for t in b.units:
            if t.side == 'G' and t.kind == 'T':
                rx, rz = nearest_reachable(t.x, t.z)
                self.reach[t.id] = max(self.p['hug'], math.hypot(rx - t.x, rz - t.z) + 1.0)
        self.turret_aims = {}  # tourelle rouge -> membres visés
        self.pending = []      # (tourelle verte, t)
        for u in b.units:
            if u.side == 'R':
                u.ai = RedDrone(u, self) if u.kind == 'D' else RedTurret(u, self)
        self.impacts = []

    def update(self, b, dt):
        # radar à grenades : point d'impact et temps restant (connus dès le tir : trajectoire balistique)
        self.impacts = [(g[1], g[3], g[0] - b.t) for g in b.grenades if g[4] == 'G']
        self.green_drones = b.enemies('R', 'D')
        self.green_turrets = b.enemies('R', 'T')
        for u in b.units:
            if u.alive and u.side == 'R': u.ai.update(dt)

    # ------------------------------------------------------------------ grenades
    def danger(self, x, z, margin=1.0, horizon=99.0):
        for (gx, gz, tt) in self.impacts:
            if tt <= horizon and math.hypot(x - gx, z - gz) < 5.0 + margin: return True
        return False

    def predicted_pos(self, u, tt):
        """position du drone dans tt secondes s'il continue ce qu'il fait (chemin en cours ou freinage)"""
        sp = math.hypot(u.vx, u.vz)
        if u.path and not u.stopped:
            if u.remaining() - u.stop_dist <= sp * tt + 0.5:
                ex, ez = u.path[-1]
                L = math.hypot(ex - u.x, ez - u.z)
                if L > 1e-6 and u.stop_dist > 0: k = max(0.0, (L - u.stop_dist) / L); return u.x + (ex - u.x) * k, u.z + (ez - u.z) * k
                return ex, ez
            return u.x + u.vx * tt, u.z + u.vz * tt
        brake = sp * sp / 16.0
        if sp < 1e-3: return u.x, u.z
        return u.x + u.vx / sp * brake, u.z + u.vz / sp * brake

    def threat(self, u):
        """impact le plus proche dans le temps qui touchera le drone (position actuelle ou prévue à l'impact)"""
        best = None
        R = GREN_R + 0.6
        for (gx, gz, tt) in self.impacts:
            px, pz = self.predicted_pos(u, tt)
            if math.hypot(u.x - gx, u.z - gz) < R or math.hypot(px - gx, pz - gz) < R:
                if best is None or tt < best[2]: best = (gx, gz, tt)
        return best

    def escape_time(self, u, gx, gz):
        """temps pour sortir de la zone en partant à l'opposé du point d'impact"""
        R = GREN_R + 0.8
        d = math.hypot(u.x - gx, u.z - gz)
        need = R - d
        if need <= 0: need = 1.0  # on y entre : il faut au moins s'arrêter et repartir
        sp = math.hypot(u.vx, u.vz)
        # vitesse radiale actuelle (positive = on s'éloigne)
        vr = ((u.x - gx) * u.vx + (u.z - gz) * u.vz) / d if d > 1e-6 else 0.0
        t_brake = max(0.0, -vr) / 8.0; extra = max(0.0, -vr) ** 2 / 16.0
        need += extra; v0 = max(0.0, vr)
        t_acc = (10.0 - v0) / 8.0; d_acc = v0 * t_acc + 4.0 * t_acc * t_acc
        t = (-v0 + math.sqrt(v0 * v0 + 16.0 * need)) / 8.0 if need <= d_acc else t_acc + (need - d_acc) / 10.0
        return t + t_brake + 0.1

    def can_evade(self, u):
        e = self.p['evade']
        if e == 'none': return False
        if e == 'all': return True
        if e == 'hunters': return self.role[u.id] == 'H'
        if e == 'endgame': return self.role[u.id] == 'H' or len(self.green_drones) <= self.p['endgame_green']
        return False

    def in_danger(self, u):
        if not self.can_evade(u): return False
        if not self.p['evade_jit']:
            return self.danger(u.x, u.z, 1.0)
        th = self.threat(u)
        if th is None: return False
        return th[2] <= self.escape_time(u, th[0], th[1]) + self.p['jit_margin']

    def escape_point(self, u, target):
        if self.p['evade_jit']: return self.escape_point_jit(u, target)
        hugging = target is not None and target.kind == 'T'
        want = 0.0 if target is None else (4.0 if hugging else self.engage_dist(target))
        best, bs = None, 1e9
        cands = []
        ring = 5.0 + 1.0 + 1.5
        for k in range(16):
            a = math.radians(k * 22.5); dx, dz = math.sin(a), math.cos(a)
            cands.append((u.x + dx * ring, u.z + dz * ring)); cands.append((u.x + dx * (ring + 2), u.z + dz * (ring + 2)))
            if hugging: cands.append((target.x + dx * 4.0, target.z + dz * 4.0))
        for (cx, cz) in cands:
            i, j = cell(cx, cz)
            if not walk(i, j): continue
            if self.danger(cx, cz, 1.0): continue
            tr = math.hypot(cx - u.x, cz - u.z)
            if tr > 10.0 or not line_ok(u.x, u.z, cx, cz): continue
            s = tr
            if target is not None: s += 2.0 * max(0.0, math.hypot(cx - target.x, cz - target.z) - want)
            s += 3.0 * sum(1 for v in self.b.units if v.alive and v.side == 'R' and v.kind == 'D' and v is not u and math.hypot(v.x - cx, v.z - cz) < 5.5)
            if s < bs: bs, best = s, (cx, cz)
        return best

    def travel_time(self, u, d):
        # départ depuis la vitesse actuelle (approché), 8 m/s², 10 m/s
        return d / 8.0 + 0.5 if d < 6.25 else 1.25 + (d - 6.25) / 10.0 + 0.3

    def point_safe(self, x, z, arrive, stay=2.0, skip=None):
        """aucune grenade ne tombe sur ce point entre maintenant et notre arrivée + stay"""
        for (gx, gz, tt) in self.impacts:
            if skip is not None and gx == skip[0] and gz == skip[1]: continue
            if tt < arrive + stay and math.hypot(x - gx, z - gz) < GREN_R + 0.5: return False
        return True

    def escape_point_jit(self, u, target):
        """sortir de la zone de l'impact menaçant par le chemin le plus court, en visant 2 m au-delà du bord
        (le drone traverse le bord à pleine vitesse), sans entrer dans une autre zone qui explosera bientôt"""
        th = self.threat(u)
        if th is None: return None
        gx, gz, tt = th
        R = GREN_R + 0.8
        d = math.hypot(u.x - gx, u.z - gz)
        base = math.atan2(u.x - gx, u.z - gz) if d > 1e-3 else self.b.rng.uniform(-math.pi, math.pi)
        want = None if target is None else (4.0 if target.kind == 'T' else self.engage_dist(target))
        best, bs = None, 1e9
        for k in range(-8, 9):
            a = base + math.radians(k * 15.0)
            dx, dz = math.sin(a), math.cos(a)
            # distance pour sortir du cercle de rayon R dans la direction (dx, dz)
            ox, oz = u.x - gx, u.z - gz
            bq = ox * dx + oz * dz; c = ox * ox + oz * oz - R * R
            disc = bq * bq - c
            sx = -bq + math.sqrt(disc) if disc > 0 else 0.0
            sx = max(sx, 0.0)
            L = sx + 2.0
            cx, cz = u.x + dx * L, u.z + dz * L
            if not walk(*cell(cx, cz)) or not line_ok(u.x, u.z, cx, cz): continue
            if not self.point_safe(cx, cz, self.travel_time(u, L), stay=1.5, skip=(gx, gz)): continue
            s = 3.0 * sx
            if target is not None: s += 0.5 * abs(math.hypot(cx - target.x, cz - target.z) - want)
            s += 3.0 * sum(1 for v in self.b.units if v.alive and v.side == 'R' and v.kind == 'D' and v is not u and math.hypot(v.x - cx, v.z - cz) < 5.5)
            if s < bs: bs, best = s, (cx, cz)
        if best is None:
            # repli : aucun point parfaitement sûr (pluie de grenades, murs) : le point le moins exposé
            for r in (3.0, 6.0, 9.0):
                for k in range(24):
                    a = math.radians(k * 15.0); cx, cz = u.x + math.sin(a) * r, u.z + math.cos(a) * r
                    if not walk(*cell(cx, cz)): continue
                    straight = line_ok(u.x, u.z, cx, cz)
                    ta = self.travel_time(u, r) * (1.0 if straight else 1.6)
                    s = r * 0.3 + (0.0 if straight else 3.0)
                    for (hx, hz, ht) in self.impacts:
                        if math.hypot(cx - hx, cz - hz) < GREN_R + 0.5 and ht < ta + 1.5: s += 10.0 / (ht + 0.5)
                    if s < bs: bs, best = s, (cx, cz)
        return best

    # ------------------------------------------------------------------ tir d'élite (fin de bataille)
    def threat_count(self, x, y, z, exclude=None):
        n = 0
        for t in self.green_turrets:
            if t is exclude: continue
            d = math.sqrt((x - t.x) ** 2 + (y - t.y) ** 2 + (z - t.z) ** 2)
            if 5.0 < d < 50.0: n += 1
        return n

    def sniper_spot(self, t):
        """point joignable, à plus de 50 m (3D) de la tourelle visée et hors de portée des autres, ligne de tir dégagée"""
        if not self.p['snipe'] or self.green_drones: return None
        key = (t.id, tuple(sorted(v.id for v in self.green_turrets)))
        cache = getattr(self, '_snipe_cache', {})
        self._snipe_cache = cache
        if key in cache: return cache[key]
        best, bs = None, 1e9
        for r in (self.p['snipe_min'] + 0.5, (self.p['snipe_min'] + self.p['snipe_max']) / 2, self.p['snipe_max'] - 0.5):
            for k in range(72):
                a = math.radians(k * 5.0); cx, cz = t.x + math.sin(a) * r, t.z + math.cos(a) * r
                i, j = cell(cx, cz)
                if not (in_grid(i, j) and NAV[i, j] and REACH[i, j]): continue
                cy = NAVH[i, j] + 1.0
                if math.sqrt((cx - t.x) ** 2 + (cy - t.y) ** 2 + (cz - t.z) ** 2) < self.p['snipe_min']: continue
                if missile_blocked(cx, cz, t.x, t.z): continue
                n = self.threat_count(cx, cy, cz, exclude=t)
                if n > 0: continue
                s_ = r
                if s_ < bs: bs, best = s_, (cx, cz)
        cache[key] = best
        return best

    def snipe_target(self, u):
        """tourelle verte qu'on peut atteindre depuis un point sûr (la plus proche de ce point)"""
        best, bd = None, 1e9
        for t in self.green_turrets:
            sp = self.sniper_spot(t)
            if sp is None: continue
            d = math.hypot(sp[0] - u.x, sp[1] - u.z)
            if d < bd: bd, best = d, t
        return best

    # ------------------------------------------------------------------ cibles
    def engage_dist(self, t):
        if t.kind == 'D': return self.p['firing_distance']
        return self.reach.get(t.id, self.p['hug'])

    def hunter_target(self, u):
        t = self.locks.get(u.id)
        if t is not None and t.alive: return t
        self.locks.pop(u.id, None)
        best, bsat, bs = None, True, 1e9
        for e in self.green_drones:
            n = sum(1 for v in self.locks.values() if v is e)
            rem = e.hp - n * self.p['expected_dmg']; sat = rem <= 0
            s = abs(rem) + self.p['dist_weight'] * dist3(u, e)
            if math.hypot(e.vx, e.vz) < 1.0: s -= self.p['parked_bonus']
            if (bsat and not sat) or (sat == bsat and s < bs): best, bsat, bs = e, sat, s
        if best is not None: self.locks[u.id] = best
        return best

    def squad_turret(self, squad, u):
        t = self.squad_t.get(squad)
        if t is not None and t.alive: return t
        if not self.green_turrets: self.squad_t.pop(squad, None); return None
        order = self.p['assault_order'] if squad == 'A' else list(reversed(self.p['assault_order']))
        names = {t.name: t for t in self.green_turrets}
        for n in order:
            if n in names: self.squad_t[squad] = names[n]; return names[n]
        t = min(self.green_turrets, key=lambda t: hdist(t, u)); self.squad_t[squad] = t; return t

    def target_for(self, u):
        if self.p['snipe'] and not self.green_drones:
            t = self.snipe_target(u)
            if t is not None: return t
        r = self.role[u.id]
        if r == 'H':
            t = self.hunter_target(u)
            if t is None: t = self.squad_turret('A', u)
            return t
        t = self.squad_turret(r, u)
        if t is None: t = self.hunter_target(u)
        return t

    # ------------------------------------------------------------------ tourelles rouges
    def predict_green(self, d, horizon):
        sp = math.hypot(d.vx, d.vz)
        if sp < 1.0:
            return d.x, d.z, (1.0 if horizon <= 2.5 else 0.4)
        fx, fz = d.vx / sp, d.vz / sp
        travel = 10.0 * horizon
        stop = 1e9
        for o in self.b.units:
            if not o.alive or o.side != 'R': continue
            tx, tz = o.x - d.x, o.z - d.z; along = tx * fx + tz * fz
            if along <= 0: continue
            lat = abs(tx * fz - tz * fx)
            if lat > 2.0: continue
            stop = min(stop, max(0.0, along - 6.0))
        if stop < 1e9:
            if travel >= stop:
                tstop = stop / max(sp, 1.0)
                return d.x + fx * stop, d.z + fz * stop, (1.0 if horizon - tstop <= 2.5 else 0.4)
            return d.x + fx * travel, d.z + fz * travel, 0.8
        return d.x + fx * travel, d.z + fz * travel, 0.5

    def turret_target(self, tur):
        b = self.b
        # priorité : tourelles listées (contre-batterie sans gaspillage)
        self.pending = [(t, ts) for (t, ts) in self.pending if t.alive and b.t - ts < 6.0]
        pri = [t for t in self.green_turrets if t.name in self.p['priority_turrets'] and 5 < dist3(tur, t) < 50]
        if self.p['turret_focus_turrets']:
            pri = [t for t in self.green_turrets if 5 < dist3(tur, t) < 50]
        best, br = None, 1e9
        for t in pri:
            rem = t.hp - 10.0 * sum(1 for (q, _) in self.pending if q is t)
            if rem <= 0: continue
            rem += 0.01 * dist3(tur, t)
            if rem < br: br, best = rem, t
        if best is not None:
            self.pending.append((best, b.t)); self.turret_aims.pop(tur.id, None)
            return ('unit', best)
        # groupes de drones verts
        if self.green_drones:
            if self.p['predict_clusters']:
                hz = 4.0 + 1.0
                preds = [(d,) + self.predict_green(d, hz) for d in self.green_drones]
                bm, bsc = None, 0.0
                for (d, px, pz, c) in preds:
                    if not (5 < math.sqrt((tur.x - px) ** 2 + (tur.z - pz) ** 2 + (tur.y - d.y) ** 2) < 50): continue
                    near = [(q, x, z) for (q, x, z, cc) in preds if math.hypot(x - px, z - pz) <= 4.9]
                    cx = sum(x for (_, x, _) in near) / len(near); cz = sum(z for (_, _, z) in near) / len(near)
                    mem = [(q, cc) for (q, x, z, cc) in preds if math.hypot(x - cx, z - cz) <= 4.9]
                    sc = 0.0
                    for (q, cc) in mem:
                        aimed = sum(1 for k, ms in self.turret_aims.items() if k != tur.id and q in ms)
                        sc += cc * (1.0 if q.hp - aimed * 10 > 0 else 0.3)
                    if sc > bsc: bsc, bm = sc, [q for (q, _) in mem]
                if bm:
                    self.turret_aims[tur.id] = bm
                    return ('cluster', bm)
            else:
                bm, bsc = None, 0.0
                for d in self.green_drones:
                    if not (5 < dist3(tur, d) < 50): continue
                    mem = [q for q in self.green_drones if dist3(q, d) <= self.p['cluster_radius']]
                    sc = len(mem) + 0.1 * sum(1 for q in mem if math.hypot(q.vx, q.vz) < 1)
                    if sc > bsc: bsc, bm = sc, mem
                if bm:
                    self.turret_aims[tur.id] = bm
                    return ('cluster', bm)
        ts = [t for t in self.green_turrets if 5 < dist3(tur, t) < 50]
        if ts: return ('unit', min(ts, key=lambda t: dist3(tur, t)))
        return None

    def cluster_point(self, members):
        ms = [m for m in members if m.alive]
        if not ms: return None
        if self.p['predict_clusters']:
            pts = [self.predict_green(m, 4.0)[:2] for m in ms]
        else:
            pts = [(m.x, m.z) for m in ms]
        x = sum(p[0] for p in pts) / len(pts); z = sum(p[1] for p in pts) / len(pts)
        return x, nav_h(x, z) + 1.0, z


class RedTurret:
    def __init__(self, u, pol):
        self.u = u; self.pol = pol; self.state = 'select'; self.timer = 0.0; self.tgt = None

    def aim_point(self):
        kind, t = self.tgt
        if kind == 'unit':
            return (t.x, t.y, t.z) if t.alive else None
        return self.pol.cluster_point(t)

    def update(self, dt):
        b = self.pol.b
        if self.state == 'select':
            self.tgt = self.pol.turret_target(self.u)
            if self.tgt is None: return
            p = self.aim_point()
            if p is None: return
            self.timer = b.turret_rotation_time(self.u, *p); self.state = 'rotate'
        if self.state == 'rotate':
            self.timer -= dt
            if self.timer <= 0:
                p = self.aim_point()
                if p is None: self.state = 'select'; return
                if self.tgt[0] == 'cluster' and PRED_NOISE:
                    p = (p[0] + b.rng.gauss(0, PRED_NOISE), p[1], p[2] + b.rng.gauss(0, PRED_NOISE))
                b.fire_grenade(self.u, *p)
                self.pol.turret_aims.pop(self.u.id, None)
                self.state = 'wait'; self.timer = b.rng.uniform(1.0, 3.0)
            return
        if self.state == 'wait':
            self.timer -= dt
            if self.timer <= 0: self.state = 'select'


def lead_point(shooter, t):
    vx, vz = t.vx, t.vz
    ax, az = t.x, t.z
    for _ in range(3):
        d = math.hypot(ax - shooter.x, az - shooter.z)
        ft = flight_time(d)
        ax, az = t.x + vx * ft, t.z + vz * ft
    return ax, az

def flight_time(d):
    # départ 10 m/s, 40 m/s² jusqu'à 20 m/s (0,25 s, 3,75 m), puis 20 m/s
    if d <= 3.75: return (-10 + math.sqrt(100 + 80 * d)) / 40.0
    return 0.25 + (d - 3.75) / 20.0


class RedDrone:
    """Repeater > Sequence[ Selector(Esquive, Attaque[Request > Move > Aim > Shoot]), Wait 1-2 ] (+ options)"""
    def __init__(self, u, pol):
        self.u = u; self.pol = pol; self.state = 'request'; self.timer = 0.0; self.target = None
        self.esc = None; self.repath = 0.0; self.stuck = 0.0; self.elapsed = 0.0
        self.aim_yaw = 0.0; self.want_yaw = 0.0; self.kite_pt = None

    def goto(self, s):
        self.state = s; self.elapsed = 0.0; self.repath = 0.0; self.stuck = 0.0

    def update(self, dt):
        u, pol, b = self.u, self.pol, self.pol.b
        self.elapsed += dt
        # ---- branche Esquive (abort Lower Priority) : interrompt l'attaque, pas le Wait
        if (self.state in ('request', 'move', 'aim') or (self.state == 'wait' and pol.p['evade_in_wait'])) and pol.in_danger(u):
            self.from_wait = self.state == 'wait'
            u.stop()
            self.goto('evade'); self.esc = None
        if self.state == 'evade':
            safe = (pol.threat(u) is None) if pol.p['evade_jit'] else (not pol.danger(u.x, u.z, 1.0))
            if safe or self.elapsed > 3.0:
                u.stop()
                if getattr(self, 'from_wait', False): self.goto('request')
                else: self.start_wait()
                return
            self.repath -= dt
            if self.repath <= 0:
                self.repath = 0.3
                t = pol.target_for(u)
                p = pol.escape_point(u, t)
                if p: u.update_rot = True; u.speed = 10.0; u.set_destination(p[0], p[1], 0.0)
            return
        if self.state == 'request':
            self.target = pol.target_for(u)
            if self.target is None: self.start_wait(); return
            self.goto('move')
        t = self.target
        if self.state == 'move':
            if not t.alive: u.stop(); self.start_wait(); return
            if self.elapsed > 6.0: u.stop(); self.start_wait(); return
            spot = pol.sniper_spot(t) if t.kind == 'T' else None
            if spot is not None:
                if self.elapsed > 12.0: u.stop(); self.start_wait(); return
                if math.hypot(spot[0] - u.x, spot[1] - u.z) <= 1.0:
                    u.stop(); self.goto('aim'); self.compute_aim(); return
                self.repath -= dt
                if self.repath <= 0:
                    self.repath = 0.5; u.speed = 10.0; u.update_rot = True; u.set_destination(spot[0], spot[1], 0.0)
                return
            want = min(pol.engage_dist(t), 30.0)
            d = hdist(u, t)
            clear = not missile_blocked(u.x, u.z, t.x, t.z)
            sp = math.hypot(u.vx, u.vz)
            self.stuck = self.stuck + dt if sp < 0.5 else 0.0
            if (d <= want and clear) or (self.stuck > 0.5 and d <= want + 3 and clear):
                u.stop(); self.goto('aim'); self.compute_aim(); return
            self.repath -= dt
            if self.repath <= 0:
                self.repath = 0.25; u.speed = 10.0; u.update_rot = True; u.set_destination(t.x, t.z, 0.0)
            return
        if self.state == 'aim':
            if not t.alive: self.start_wait(); return
            if self.aim_step(dt):
                b.shoot_missiles(u, t); self.start_wait()
            elif self.elapsed > 1.5:
                self.start_wait()
            return
        if self.state == 'wait':
            self.timer -= dt
            if pol.p['kite'] and pol.role[u.id] == 'H': self.kite(dt)
            if self.timer <= 0:
                if pol.p['kite']: u.update_rot = True
                self.goto('request')

    def start_wait(self):
        self.goto('wait'); self.timer = self.pol.b.rng.uniform(1.0, 2.0); self.kite_pt = None

    def compute_aim(self):
        u, t = self.u, self.target
        ax, az = lead_point(u, t)
        self.aim_yaw = yaw_of(ax - u.x, az - u.z)
        d = dangle(u.yaw, self.aim_yaw)
        self.want_yaw = self.aim_yaw + (4.5 if d >= 0 else -4.5)

    def aim_step(self, dt):
        u = self.u
        self.repath -= dt
        if self.repath <= 0: self.repath = 0.1; self.compute_aim()
        if abs(dangle(u.yaw, self.aim_yaw)) < 0.5: return True
        u.yaw += dangle(u.yaw, self.want_yaw) * min(1.0, dt * 4.0)
        return abs(dangle(u.yaw, self.want_yaw)) < 5.0

    def kite(self, dt):
        """chasseur : bouge latéralement pendant le Wait (la cible verte tire sur une position périmée)"""
        u, pol = self.u, self.pol
        if self.kite_pt is None or math.hypot(self.kite_pt[0] - u.x, self.kite_pt[1] - u.z) < 1.0:
            t = self.target if (self.target is not None and self.target.alive) else None
            best, bs = None, 1e9
            for k in range(16):
                a = math.radians(k * 22.5)
                cx, cz = u.x + math.sin(a) * pol.p['kite_leg'], u.z + math.cos(a) * pol.p['kite_leg']
                i, j = cell(cx, cz)
                if not walk(i, j) or not line_ok(u.x, u.z, cx, cz): continue
                if pol.danger(cx, cz, 0.8): continue
                s = 0.0
                if t is not None:
                    s += abs(math.hypot(cx - t.x, cz - t.z) - pol.engage_dist(t)) * 2
                    if missile_blocked(cx, cz, t.x, t.z): s += 20
                s += b_crowd(pol, u, cx, cz)
                s += pol.b.rng.random()
                if s < bs: bs, best = s, (cx, cz)
            if best: self.kite_pt = best; u.speed = 10.0; u.set_destination(best[0], best[1], 0.0)


def b_crowd(pol, u, x, z):
    return 3.0 * sum(1 for v in pol.b.units if v.alive and v.side == 'R' and v.kind == 'D' and v is not u and math.hypot(v.x - x, v.z - z) < 5.5)
