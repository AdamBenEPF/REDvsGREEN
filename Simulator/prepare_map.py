"""Prépare la carte du simulateur (map.npz, units.json) à partir de Results/map.json, exporté depuis Unity
(menu RedVsGreen > Exporter la carte).

Usage : python3 prepare_map.py ../Results/map.json
"""
import json, sys, os
import numpy as np
from matplotlib.path import Path
from scipy.spatial import cKDTree

HERE = os.path.dirname(os.path.abspath(__file__))
d = json.load(open(sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, '..', 'Results', 'map.json')))
n = d['nx']; step = d['step']; xmin = d['xMin']; zmin = d['zMin']
terr = np.array([np.nan if v is None else v for v in d['terrainHeight']]).reshape(n, n)
V = np.array(d['navVertices']); I = np.array(d['navIndices']).reshape(-1, 3)

# 1. Surfaces connexes du NavMesh (triangles reliés par des sommets communs ou très proches : bords de tuiles)
key = {}
vid = np.array([key.setdefault(tuple(np.round(v, 2)), len(key)) for v in V])
parent = list(range(len(key)))
def find(a):
    while parent[a] != a:
        parent[a] = parent[parent[a]]; a = parent[a]
    return a
for t in I:
    a, b, c = vid[t]; parent[find(a)] = find(b); parent[find(b)] = find(c)
for a, b in cKDTree(np.array(list(key.keys()))).query_pairs(0.15):
    parent[find(a)] = find(b)
tcomp = np.array([find(vid[t[0]]) for t in I])

# 2. Pixellisation du NavMesh sur une grille de 0,5 m : praticable, hauteur, surface
xs = xmin + np.arange(n) * step
X, Z = np.meshgrid(xs, xs)
P = np.c_[X.ravel(), Z.ravel()]
nav = np.zeros(len(P), bool); navh = np.full(len(P), np.nan); tri = np.full(len(P), -1)
for k, t in enumerate(I):
    a, b, c = V[t]
    inside = Path(V[t][:, [0, 2]]).contains_points(P, radius=0.05)
    if not inside.any(): continue
    v0, v1 = b - a, c - a
    den = v0[0] * v1[2] - v1[0] * v0[2]
    if abs(den) < 1e-9: continue
    dx = P[inside, 0] - a[0]; dz = P[inside, 1] - a[2]
    u = (dx * v1[2] - v1[0] * dz) / den; w = (v0[0] * dz - dx * v0[2]) / den
    navh[inside] = a[1] + u * v0[1] + w * v1[1]
    nav |= inside; tri[inside] = tcomp[k]
nav = nav.reshape(n, n); navh = navh.reshape(n, n); tri = tri.reshape(n, n)

# 3. Bouchage des lignes de cellules manquantes sur les bords de tuiles du NavMesh
for _ in range(2):
    for i in range(1, n - 1):
        for j in range(1, n - 1):
            if nav[i, j]: continue
            for (a, b), (c, e) in (((i, j - 1), (i, j + 1)), ((i - 1, j), (i + 1, j)), ((i - 1, j - 1), (i + 1, j + 1)), ((i - 1, j + 1), (i + 1, j - 1))):
                if nav[a, b] and nav[c, e] and abs(navh[a, b] - navh[c, e]) < 0.6 and tri[a, b] == tri[c, e]:
                    nav[i, j] = True; navh[i, j] = (navh[a, b] + navh[c, e]) / 2; tri[i, j] = tri[a, b]
                    break

# 4. Caisses : jamais praticables
for o in d['obstacles']:
    cx, cy, cz = o['center']; sx, sy, sz = o['size']
    nav[(abs(X - cx) <= sx / 2 + 0.3) & (abs(Z - cz) <= sz / 2 + 0.3)] = False

np.savez(os.path.join(HERE, 'map.npz'), nav=nav, navh=navh, H=terr, tri=tri, xmin=xmin, zmin=zmin, step=step)
json.dump({'units': d['units'], 'obstacles': d['obstacles']}, open(os.path.join(HERE, 'units.json'), 'w'))
print('cellules praticables :', int(nav.sum()))
