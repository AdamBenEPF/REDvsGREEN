import sys, json, statistics as st, multiprocessing as mp, time
import world, red
from world import Battle
world.Battle.AIM_NOISE = 2.0
from red import RedPolicy

def one(args):
    seed, kw = args
    kw = dict(kw)
    world.Battle.AIM_NOISE = kw.pop('_noise', 2.0)
    red.PRED_NOISE = kw.pop('_pred', 0.0)
    return Battle(seed, RedPolicy(**kw)).run(tmax=300)

def evaluate(kw, n=40, seed0=1000, pool=None):
    seed0 = kw.pop('_seed0', seed0)
    args = [(seed0 + i, kw) for i in range(n)]
    res = pool.map(one, args) if pool else list(map(one, args))
    res = [r for r in res if r['result'] in ('VICTORY', 'DEFEAT', 'TIMEOUT')]
    def m(k): return st.mean(r.get(k, 0) or 0 for r in res)
    return dict(n=len(res), win=sum(r['result'] == 'VICTORY' for r in res) / len(res),
                green_hp=m('green_hp'), red_hp=m('red_hp'), t=m('t'),
                gD=m('green_drones'), gT=m('green_turrets'),
                R_acc=sum(r['R_hit'] for r in res) / max(1, sum(r['R_msl'] for r in res)),
                G_acc=sum(r['G_hit'] for r in res) / max(1, sum(r['G_msl'] for r in res)),
                R_msl=m('R_msl'), G_msl=m('G_msl'), R_dmg_msl=m('R_dmg_msl'), G_dmg_msl=m('G_dmg_msl'), G_dmg_gren=m('G_dmg_gren'), R_dmg_gren=m('R_dmg_gren'),
                RD_msl=m('RD_msl'), RD_gren=m('RD_gren'), GD_msl=m('GD_msl'), GD_gren=m('GD_gren'), GT_msl=m('GT_msl'), GT_gren=m('GT_gren'),
                RT_lost=st.mean(r['R_turrets_lost_t'] or r['t'] for r in res), timeouts=sum(r['result']=='TIMEOUT' for r in res))

def fmt(name, r):
    return (f"{name:28s} win {r['win']*100:4.0f}%  gHP {r['green_hp']:5.0f} rHP {r['red_hp']:4.0f} t {r['t']:5.1f} gD {r['gD']:4.1f} gT {r['gT']:3.1f} | "
            f"accR {r['R_acc']:.2f} accG {r['G_acc']:.2f} mslR {r['R_msl']:4.0f} mslG {r['G_msl']:4.0f} | dmg R msl {r['R_dmg_msl']:4.0f} gren {r['R_dmg_gren']:4.0f} G msl {r['G_dmg_msl']:4.0f} gren {r['G_dmg_gren']:4.0f} | "
            f"RD msl {r['RD_msl']:.1f} gren {r['RD_gren']:.1f} GD msl {r['GD_msl']:.1f} gren {r['GD_gren']:.1f} GT {r['GT_msl']:.1f}/{r['GT_gren']:.1f} RTlost {r['RT_lost']:.0f} TO {r['timeouts']}")

if __name__ == '__main__':
    variants = json.loads(sys.argv[1]); n = int(sys.argv[2]) if len(sys.argv) > 2 else 40
    with mp.Pool(2) as pool:
        for name, kw in variants.items():
            t0 = time.time(); r = evaluate(kw, n, pool=pool)
            print(fmt(name, r), f"({time.time()-t0:.0f}s)", flush=True)
