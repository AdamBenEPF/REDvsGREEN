using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

// État-major rouge
//  - 4 drones d'assaut en 2 escouades de 2, chacune sur une tourelle verte jusqu'à sa destruction :
//      escouade A dans l'ordre TurretGreen (1) à (8) puis (0), escouade B dans l'ordre inverse.
//  - 9 drones chasseurs : ils attaquent les drones verts. Chaque chasseur garde sa cible jusqu'à ce qu'elle meure.
//  - Quand un groupe n'a plus de cible (plus de tourelle ou plus de drone vert), il rejoint l'autre tâche.
//  - Tir en mouvement : les drones tournent autour de leur cible pendant la visée, le tir et le Wait
//    (m_StrafeWhileFiring, voir ArmyManagerRed.Strafe.cs et StrafeAroundTarget).
//  - Esquive des grenades vertes (m_EvadeMode, voir ArmyManagerRed.Grenades.cs) et repositionnement à chaque salve
//    quand l'artillerie verte peut viser le drone (m_RepositionWhenExposed, voir ArmyManagerRed.Reposition.cs).
//  - Nos tourelles : le plus gros groupe de drones verts à portée (rayon de 4 m), sinon la tourelle verte la plus proche ;
//    ou, avec m_TurretPriority = GreenTurrets, contre-batterie concentrée sur une tourelle verte.
//    Les tourelles de m_TurretPriorityTargets passent avant tout : TurretGreen (6), perchée, que nos drones atteignent mal.
public partial class ArmyManagerRed : ArmyManager
{
	enum DroneRole { SquadA, SquadB, Hunter }
	public enum EvadeMode { None, HuntersOnly, All, Endgame }
	public enum TurretPriority { DroneClusters, GreenTurrets }

	class TurretAim
	{
		public Transform marker;                                   // point visé, suit le centre du groupe
		public List<ArmyElement> members = new List<ArmyElement>(); // drones verts du groupe visé
	}

	[Header("Rôles des drones")]
	[Tooltip("Nombre de drones chasseurs, sur les drones verts (les autres forment les 2 escouades d'assaut)")]
	[SerializeField] int m_HunterCount = 9;
	[Tooltip("Ordre d'attaque des tourelles vertes par l'escouade A (noms dans la Hierarchy). L'escouade B le suit à l'envers")]
	[SerializeField] string[] m_AssaultOrder = { "TurretGreen (1)", "TurretGreen (2)", "TurretGreen (3)", "TurretGreen (4)", "TurretGreen (5)", "TurretGreen (6)", "TurretGreen (7)", "TurretGreen (8)", "TurretGreen (0)" };

	[Tooltip("Drones qui esquivent les grenades vertes. Endgame : les chasseurs, plus tous les drones quand il reste peu de drones verts")]
	[SerializeField] EvadeMode m_EvadeMode = EvadeMode.Endgame;
	[Tooltip("Mode Endgame : tous nos drones esquivent quand il reste au plus ce nombre de drones verts")]
	[SerializeField] int m_EndgameGreenDrones = 6;

	[Header("Chasseurs")]
	[Tooltip("Dégâts attendus d'une salve de drone : 2 missiles x 5 pts, moins les ratés")]
	[SerializeField] float m_ExpectedDamagePerDrone = 7f;
	[Tooltip("Poids de la distance dans le choix de la cible (PV équivalents par mètre)")]
	[SerializeField] float m_DistanceWeight = 1f;
	[Tooltip("En dessous de cette vitesse (m/s), une unité verte est considérée immobile")]
	[SerializeField] float m_ParkedSpeed = 1f;
	[Tooltip("Bonus de priorité d'un drone vert immobile, plus facile à toucher (PV équivalents)")]
	[SerializeField] float m_ParkedBonus = 25f;

	[Header("Tourelles")]
	[Tooltip("Rayon de dégâts d'une grenade (Annexe A)")]
	[SerializeField] float m_GrenadeRadius = 5f;
	[Tooltip("Dégâts d'une grenade (Annexe A)")]
	[SerializeField] float m_GrenadeDamage = 10f;
	[Tooltip("Rayon d'un groupe de drones verts visé par une grenade (m)")]
	[SerializeField] float m_ClusterRadius = 4f;
	[Tooltip("Tourelles vertes que nos tourelles détruisent en priorité quand elles sont à portée (noms dans la Hierarchy)")]
	[SerializeField] string[] m_TurretPriorityTargets = { "TurretGreen (6)" };
	[Tooltip("Cible prioritaire de nos tourelles")]
	[SerializeField] TurretPriority m_TurretPriority = TurretPriority.DroneClusters;
	[Tooltip("Durée pendant laquelle un tir de grenade déjà commandé est compté sur sa cible (s) : rotation + vol")]
	[SerializeField] float m_PendingShotDuration = 6f;

	readonly Dictionary<GameObject, DroneRole> m_Roles = new Dictionary<GameObject, DroneRole>();
	// Chasseurs : cible verrouillée jusqu'à sa mort
	readonly Dictionary<GameObject, ArmyElement> m_DroneLocks = new Dictionary<GameObject, ArmyElement>();
	// Tourelles : point visé par chacune de nos tourelles
	readonly Dictionary<GameObject, TurretAim> m_TurretAims = new Dictionary<GameObject, TurretAim>();
	// Assaut : tourelle verte attaquée en ce moment par chaque escouade, verrouillée jusqu'à sa mort
	readonly Dictionary<DroneRole, Turret> m_SquadTargets = new Dictionary<DroneRole, Turret>();
	// Tirs de nos tourelles commandés sur les tourelles vertes (cible, heure de la commande), pour éviter le gaspillage
	readonly List<KeyValuePair<Turret, float>> m_PendingTurretShots = new List<KeyValuePair<Turret, float>>();

	void Update()
	{
		UpdateTurretAims();
	}

	// =====================================================================
	// RÔLES
	// =====================================================================

	void AssignRolesIfNeeded()
	{
		if (m_Roles.Count > 0) return;

		// Les chasseurs sont les drones les plus proches de nos tourelles au départ ;
		// les autres partent à l'assaut, répartis en alternance entre les escouades A et B
		List<Turret> ourTurrets = m_ArmyElements.OfType<Turret>().Where(t => t != null).ToList();
		Vector3 center = ourTurrets.Count > 0 ? Centroid(ourTurrets.Select(t => t.transform.position)) : Vector3.zero;
		List<Drone> drones = m_ArmyElements.OfType<Drone>()
			.Where(d => d != null)
			.OrderBy(d => Vector3.Distance(d.transform.position, center))
			.ToList();

		for (int i = 0; i < drones.Count; i++)
		{
			if (i < m_HunterCount) m_Roles[drones[i].gameObject] = DroneRole.Hunter;
			else m_Roles[drones[i].gameObject] = (i - m_HunterCount) % 2 == 0 ? DroneRole.SquadA : DroneRole.SquadB;
		}
		if (drones.Count > 0) ComputeTurretEngageDistances(drones[0].transform.position);
	}

	DroneRole RoleOf(GameObject drone)
	{
		return m_Roles.TryGetValue(drone, out DroneRole role) ? role : DroneRole.SquadA;
	}

	bool IsHunter(GameObject drone)
	{
		return RoleOf(drone) == DroneRole.Hunter;
	}

	// =====================================================================
	// DRONES
	// =====================================================================

	public Transform GetFocusTarget(GameObject requester)
	{
		AssignRolesIfNeeded();

		ArmyElement target;
		DroneRole role = RoleOf(requester);
		if (role == DroneRole.Hunter)
		{
			target = GetHunterTarget(requester);
			if (target == null) target = GetSquadTurret(DroneRole.SquadA); // plus de drone vert : renfort de l'escouade A
		}
		else
		{
			target = GetSquadTurret(role);
			if (target == null) target = GetHunterTarget(requester); // plus de tourelle verte : chasse aux drones
		}
		return target != null ? target.transform : null;
	}

	public bool CanEvade(GameObject drone)
	{
		switch (m_EvadeMode)
		{
			case EvadeMode.All: return true;
			case EvadeMode.HuntersOnly: return IsHunter(drone);
			// En fin de bataille, l'artillerie verte est la principale menace et il n'y a presque plus de drones à abattre :
			// le temps passé à esquiver ne coûte plus rien
			case EvadeMode.Endgame: return IsHunter(drone) || GreenDroneCount() <= m_EndgameGreenDrones;
			default: return false;
		}
	}

	int m_GreenDroneCount, m_GreenDroneCountFrame = -1;
	int GreenDroneCount()
	{
		if (m_GreenDroneCountFrame != Time.frameCount)
		{
			m_GreenDroneCountFrame = Time.frameCount;
			m_GreenDroneCount = GetAllEnemiesOfType<Drone>(false).Count(d => d.Health > 0);
		}
		return m_GreenDroneCount;
	}

	// Cible actuelle d'un drone, sans en attribuer de nouvelle (utilisée pour choisir où esquiver)
	ArmyElement CurrentTargetOf(GameObject drone)
	{
		if (IsHunter(drone))
			return m_DroneLocks.TryGetValue(drone, out ArmyElement locked) && locked != null && locked.Health > 0 ? locked : null;
		return m_SquadTargets.TryGetValue(RoleOf(drone), out Turret turret) && turret != null && turret.Health > 0 ? turret : null;
	}

	// Chaque escouade reste sur sa tourelle jusqu'à sa destruction, puis prend la suivante dans son ordre :
	// l'escouade A suit m_AssaultOrder, l'escouade B le suit à l'envers.
	// Une tourelle absente de la liste n'est attaquée qu'à la fin (la plus proche de l'escouade d'abord)
	Turret GetSquadTurret(DroneRole squad)
	{
		if (m_SquadTargets.TryGetValue(squad, out Turret current) && current != null && current.Health > 0) return current;

		List<Turret> enemyTurrets = GetAllEnemiesOfType<Turret>(false).Where(t => t.Health > 0).ToList();
		if (enemyTurrets.Count == 0)
		{
			m_SquadTargets.Remove(squad);
			return null;
		}

		IEnumerable<string> order = squad == DroneRole.SquadB ? m_AssaultOrder.Reverse() : m_AssaultOrder;
		foreach (string turretName in order)
		{
			Turret next = enemyTurrets.FirstOrDefault(t => t.name == turretName);
			if (next != null)
			{
				m_SquadTargets[squad] = next;
				return next;
			}
		}

		Vector3 squadCenter = SquadCenter(squad);
		Turret nearest = enemyTurrets.OrderBy(t => Vector3.Distance(t.transform.position, squadCenter)).First();
		m_SquadTargets[squad] = nearest;
		return nearest;
	}

	Vector3 SquadCenter(DroneRole squad)
	{
		List<Vector3> positions = m_Roles
			.Where(kv => kv.Key != null && kv.Value == squad)
			.Select(kv => kv.Key.transform.position)
			.ToList();
		if (positions.Count == 0)
			positions = m_ArmyElements.OfType<Drone>().Where(d => d != null).Select(d => d.transform.position).ToList();
		return Centroid(positions);
	}

	// Un chasseur garde sa cible jusqu'à sa mort. Sinon, nouvelle cible :
	// d'abord un drone vert que les autres chasseurs n'ont pas encore de quoi tuer (focus fire),
	// en privilégiant le plus proche, le plus entamé et les drones immobiles (plus faciles à toucher)
	ArmyElement GetHunterTarget(GameObject hunter)
	{
		if (m_DroneLocks.TryGetValue(hunter, out ArmyElement locked) && locked != null && locked.Health > 0) return locked;
		m_DroneLocks.Remove(hunter);

		Vector3 from = hunter.transform.position;
		ArmyElement best = null;
		bool bestIsSaturated = true;
		float bestScore = float.MaxValue;

		foreach (Drone enemy in GetAllEnemiesOfType<Drone>(false))
		{
			if (enemy.Health <= 0) continue; // déjà en train de mourir

			int lockedHunters = m_DroneLocks.Values.Count(target => target == enemy);
			float remaining = enemy.Health - lockedHunters * m_ExpectedDamagePerDrone;
			bool saturated = remaining <= 0;

			// Plus le score est bas, plus la cible est prioritaire
			float score = Mathf.Abs(remaining) + m_DistanceWeight * Vector3.Distance(from, enemy.transform.position);
			if (IsStationary(enemy)) score -= m_ParkedBonus;

			// Une cible qui a encore besoin de dégâts passe toujours avant une cible déjà couverte
			bool isBetter = (bestIsSaturated && !saturated) || (saturated == bestIsSaturated && score < bestScore);
			if (isBetter)
			{
				best = enemy;
				bestIsSaturated = saturated;
				bestScore = score;
			}
		}

		if (best != null) m_DroneLocks[hunter] = best;
		return best;
	}

	// =====================================================================
	// TOURELLES : le plus gros groupe de drones verts à portée, sinon la tourelle verte la plus proche
	// =====================================================================

	public Transform GetTurretTarget(GameObject requester, float minRadius, float maxRadius)
	{
		Vector3 from = requester.transform.position;
		if (m_TurretPriorityTargets.Length > 0)
		{
			Turret priority = GetArtilleryFocus(from, minRadius, maxRadius, m_TurretPriorityTargets);
			if (priority != null) return priority.transform;
		}
		if (m_TurretPriority == TurretPriority.GreenTurrets)
		{
			Turret focus = GetArtilleryFocus(from, minRadius, maxRadius, null);
			if (focus != null) return focus.transform;
		}
		List<Drone> greenDrones = GetAllEnemiesOfType<Drone>(false).Where(d => d.Health > 0).ToList();

		List<ArmyElement> bestCluster = null;
		float bestScore = 0f;
		foreach (Drone candidate in greenDrones)
		{
			if (!IsWithinRadius(from, candidate.transform.position, minRadius, maxRadius)) continue;

			List<ArmyElement> cluster = greenDrones
				.Where(other => Vector3.Distance(other.transform.position, candidate.transform.position) <= m_ClusterRadius)
				.Cast<ArmyElement>()
				.ToList();
			// Taille du groupe d'abord ; à égalité, les drones immobiles (ils seront encore là à l'impact, 4 s plus tard)
			float score = cluster.Count + 0.1f * cluster.Count(member => IsStationary(member));
			if (score > bestScore)
			{
				bestScore = score;
				bestCluster = cluster;
			}
		}
		if (bestCluster != null) return AimAtCluster(requester, bestCluster);

		// Aucun drone vert à portée : la tourelle verte la plus proche à portée
		Turret nearest = GetAllEnemiesOfType<Turret>(false)
			.Where(t => t.Health > 0 && IsWithinRadius(from, t.transform.position, minRadius, maxRadius))
			.OrderBy(t => Vector3.Distance(from, t.transform.position))
			.FirstOrDefault();
		return nearest != null ? nearest.transform : null;
	}

	// Contre-batterie : toutes nos tourelles sur la même tourelle verte, la plus entamée, sans gaspiller de tirs
	// sur une tourelle que les grenades déjà commandées suffisent à détruire. names : limite le choix à ces tourelles
	Turret GetArtilleryFocus(Vector3 from, float minRadius, float maxRadius, string[] names)
	{
		m_PendingTurretShots.RemoveAll(shot => shot.Key == null || Time.time - shot.Value > m_PendingShotDuration);

		Turret best = null;
		float bestRemaining = float.MaxValue;
		foreach (Turret enemy in GetAllEnemiesOfType<Turret>(false))
		{
			if (enemy.Health <= 0 || !IsWithinRadius(from, enemy.transform.position, minRadius, maxRadius)) continue;
			if (names != null && System.Array.IndexOf(names, enemy.name) < 0) continue;
			int pending = m_PendingTurretShots.Count(shot => shot.Key == enemy);
			float remaining = enemy.Health - pending * m_GrenadeDamage;
			if (remaining <= 0) continue;
			remaining += 0.01f * Vector3.Distance(from, enemy.transform.position); // à égalité, la plus proche
			if (remaining < bestRemaining)
			{
				bestRemaining = remaining;
				best = enemy;
			}
		}
		if (best != null) m_PendingTurretShots.Add(new KeyValuePair<Turret, float>(best, Time.time));
		return best;
	}

	// Le point visé est le centre du groupe ; il suit le groupe jusqu'au tir (voir UpdateTurretAims)
	Transform AimAtCluster(GameObject turret, List<ArmyElement> members)
	{
		if (!m_TurretAims.TryGetValue(turret, out TurretAim aim))
		{
			aim = new TurretAim { marker = new GameObject("RedTurretAimPoint").transform };
			m_TurretAims[turret] = aim;
		}
		aim.members = members;
		aim.marker.position = Centroid(members.Select(member => member.transform.position));
		return aim.marker;
	}

	void UpdateTurretAims()
	{
		var deadTurrets = m_TurretAims.Keys.Where(turret => turret == null).ToList();
		foreach (var turret in deadTurrets)
		{
			if (m_TurretAims[turret].marker != null) Destroy(m_TurretAims[turret].marker.gameObject);
			m_TurretAims.Remove(turret);
		}

		foreach (TurretAim aim in m_TurretAims.Values)
		{
			aim.members.RemoveAll(member => member == null);
			if (aim.members.Count > 0)
				aim.marker.position = Centroid(aim.members.Select(member => member.transform.position));
		}
	}

	// =====================================================================
	// OUTILS COMMUNS
	// =====================================================================

	// Une tourelle n'a pas de NavMeshAgent : elle est toujours immobile
	bool IsStationary(ArmyElement element)
	{
		NavMeshAgent agent = element.GetComponent<NavMeshAgent>();
		return agent == null || agent.velocity.magnitude < m_ParkedSpeed;
	}

	static bool IsWithinRadius(Vector3 from, Vector3 to, float minRadius, float maxRadius)
	{
		float distance = Vector3.Distance(from, to);
		return distance > minRadius && distance < maxRadius;
	}

	static Vector3 Centroid(IEnumerable<Vector3> points)
	{
		Vector3 sum = Vector3.zero;
		int count = 0;
		foreach (Vector3 point in points)
		{
			sum += point;
			count++;
		}
		return count > 0 ? sum / count : Vector3.zero;
	}

	// =====================================================================
	// STATISTIQUES (code d'origine du squelette)
	// =====================================================================

	public override void ArmyElementHasBeenKilled(GameObject go)
	{
		m_DroneLocks.Remove(go);
		m_LastFiringPositions.Remove(go);
		base.ArmyElementHasBeenKilled(go);
		if (m_ArmyElements.Count == 0)
		{
			GUIUtility.systemCopyBuffer = "0\t" + ((int)Timer.Value).ToString() + "\t0\t0\t0";
		}
	}

	public void GreenArmyIsDead(string deadArmyTag)
	{
		int nDrones = 0, nTurrets = 0, health = 0;
		ComputeStatistics(ref nDrones, ref nTurrets, ref health);
		GUIUtility.systemCopyBuffer = "1\t" + ((int)Timer.Value).ToString() + "\t" + nDrones.ToString() + "\t" + nTurrets.ToString() + "\t" + health.ToString();

		RefreshHudDisplay(); // dernière mise à jour en cas de victoire
	}
}
