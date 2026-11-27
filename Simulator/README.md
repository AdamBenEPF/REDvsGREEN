# Simulateur RED vs GREEN (Python)

Reproduction du jeu Unity, sans rendu, pour tester des stratégies en masse : environ 3 s par bataille et par cœur,
contre ~45 s en jeu (vitesse x3).

## Ce qui est reproduit
- **Armée verte : code du prof à l'identique.** Arbres `DroneGreenBehavior` (RandomSelector, My Seek à 6 m avec
  destination figée, Slerp 4 / 5°, tir, Wait 1-2 s) et `TurretGreenBehavior` (tourelles d'abord, 5-50 m, rotation
  90 °/s, grenade sur la position de la cible au moment du tir, Wait 1-3 s).
- **Armes** : missiles (`Missile.cs` : départ à 10 m/s, 40 m/s², 20 m/s max, 3 s, 5 dégâts, arrêtés par les caisses),
  grenades (`Rocket.cs` : 4 s de vol, 10 dégâts dans 5 m, pas de tir ami).
- **Carte** : NavMesh, relief et caisses exportés depuis Unity (`Results/map.json`), NavMeshAgent approché
  (10 m/s, 8 m/s², freinage).
- **Armée rouge** (`red.py`) : `ArmyManagerRed` et ses options (rôles, verrouillage des cibles, prédiction des groupes
  pour les tourelles, esquive…).

## Calage sur les batailles réelles
Un seul paramètre libre, l'imprécision de visée au tir (2°, relief et évitement non simulés). Sur la version 14
(« immobile-prediction ») : 315 missiles rouges tirés (réel : 315), 1 184 dégâts par missile (réel : 1 185),
729 dégâts de grenades vertes (réel : 723), tourelles rouges toutes détruites à 21 s (réel : 23 s), même courbe de
survie des unités. Les variantes que les mesures réelles ont classées mauvaises (13 chasseurs, esquive de tous,
11 chasseurs) le sont aussi en simulation.

## Utilisation
```bash
pip install numpy scipy matplotlib
python3 prepare_map.py ../Results/map.json     # une fois, après chaque export de la carte
python3 run.py '{"v14": {}, "v15": {"evade_jit": true, "evade_in_wait": true, "evade": "all"}}' 40
```
