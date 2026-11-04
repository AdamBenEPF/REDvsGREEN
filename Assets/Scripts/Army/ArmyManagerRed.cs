using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

// État-major rouge
//  - 10 drones d'assaut : tous sur la même tourelle verte (la plus proche du groupe), puis la suivante, une à une.
//    Plus de tourelle verte : focus fire sur les drones verts restants.
//  - 3 drones gardes, en retrait devant nos tourelles : ils abattent les drones verts qui s'en approchent.
//    Un garde garde sa cible jusqu'à ce qu'elle meure.
//  - Nos tourelles : le plus gros groupe de drones verts à portée (rayon de 4 m), sinon la tourelle verte la plus proche.
public partial class ArmyManagerRed : ArmyManager
{
	enum DroneRole { Assault, Guard }

	class TurretAim
	{
		public Transform marker;                                   // point visé, suit le centre du groupe
		public List<ArmyElement> members = new List<ArmyElement>(); // drones verts du groupe visé
	}

	[Header("Rôles des drones")]
	[Tooltip("Nombre de drones gardes (les plus proches de nos tourelles au début)")]
	[SerializeField] int m_GuardCount = 3;

	[Header("Gardes")]
	[Tooltip("Un drone vert à moins de cette distance d'une de nos tourelles est une cible pour les gardes (m)")]
	[SerializeField] float m_ThreatRadius = 15f;
	[Tooltip("Distance du poste de garde devant nos tourelles, côté ennemi (m)")]
	[SerializeField] float m_GuardPostDistance = 8f;
	[Tooltip("Écart latéral entre deux gardes (m)")]
	[SerializeField] float m_GuardSpacing = 8f;
	[Tooltip("Distance minimale entre un poste de garde et nos tourelles, que l'artillerie verte bombarde (m)")]
	[SerializeField] float m_GuardTurretClearance = 8f;

	[Header("Focus fire sur les drones verts (phase finale)")]
	[Tooltip("Dégâts attendus d'une salve de drone : 2 missiles x 5 pts, moins les ratés")]
	[SerializeField] float m_ExpectedDamagePerDrone = 7f;
	[Tooltip("Poids de la distance dans le choix de la cible (PV équivalents par mètre)")]
	[SerializeField] float m_DistanceWeight = 1f;
	[Tooltip("Pénalité d'une cible hors de portée ou masquée par un obstacle (PV équivalents)")]
	[SerializeField] float m_OutOfRangePenalty = 30f;
	[Tooltip("Bonus pour garder la cible de la salve précédente (PV équivalents)")]
	[SerializeField] float m_KeepTargetBonus = 20f;
	[Tooltip("En dessous de cette vitesse (m/s), une unité verte est considérée immobile")]
	[SerializeField] float m_ParkedSpeed = 1f;
	[Tooltip("Bonus de priorité d'un drone vert immobile, plus facile à toucher (PV équivalents)")]
	[SerializeField] float m_ParkedBonus = 25f;

	[Header("Tourelles")]
	[Tooltip("Rayon de dégâts d'une grenade (Annexe A)")]
	[SerializeField] float m_GrenadeRadius = 5f;
	[Tooltip("Rayon d'un groupe de drones verts visé par une grenade (m)")]
	[SerializeField] float m_ClusterRadius = 4f;

	readonly Dictionary<GameObject, DroneRole> m_Roles = new Dictionary<GameObject, DroneRole>();
	readonly Dictionary<GameObject, int> m_GuardIndex = new Dictionary<GameObject, int>();
	// Gardes : cible verrouillée jusqu'à sa mort
	readonly Dictionary<GameObject, ArmyElement> m_GuardLocks = new Dictionary<GameObject, ArmyElement>();
	// Quel drone rouge est engagé sur quelle unité verte (libéré après le tir)
	readonly Dictionary<GameObject, ArmyElement> m_Assignments = new Dictionary<GameObject, ArmyElement>();
	// Dernière cible de chaque drone rouge (conservée après le tir)
	readonly Dictionary<GameObject, ArmyElement> m_LastTargets = new Dictionary<GameObject, ArmyElement>();
	// Tourelles : point visé par chacune de nos tourelles
	readonly Dictionary<GameObject, TurretAim> m_TurretAims = new Dictionary<GameObject, TurretAim>();
	// Assaut : tourelle verte attaquée en ce moment par tout le groupe
	Turret m_AssaultTarget;

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

		List<Turret> ourTurrets = OurTurrets();
		Vector3 center = ourTurrets.Count > 0 ? Centroid(ourTurrets.Select(t => t.transform.position)) : Vector3.zero;
		List<Drone> drones = m_ArmyElements.OfType<Drone>()
			.Where(d => d != null)
			.OrderBy(d => Vector3.Distance(d.transform.position, center))
			.ToList();

		for (int i = 0; i < drones.Count; i++)
		{
			bool isGuard = i < m_GuardCount;
			m_Roles[drones[i].gameObject] = isGuard ? DroneRole.Guard : DroneRole.Assault;
			if (isGuard) m_GuardIndex[drones[i].gameObject] = i;
		}
	}

	// Un garde n'a plus de raison d'être quand toutes nos tourelles sont détruites : il passe à l'assaut
	bool IsActingAsGuard(GameObject drone)
	{
		return m_Roles.TryGetValue(drone, out DroneRole role) && role == DroneRole.Guard && OurTurrets().Count > 0;
	}

	// Seuls les gardes esquivent les grenades : les drones d'assaut restent collés à leur tourelle, dans son angle mort
	public bool CanEvade(GameObject drone)
	{
		return IsActingAsGuard(drone);
	}

	// =====================================================================
	// DRONES
	// =====================================================================

	public Transform GetFocusTarget(GameObject requester)
	{
		AssignRolesIfNeeded();
		RemoveDeadEntries(m_Assignments);
		RemoveDeadEntries(m_LastTargets);
		m_Assignments.Remove(requester); // le demandeur ne compte pas dans les engagements en cours
		m_LastTargets.TryGetValue(requester, out ArmyElement previous);

		ArmyElement target;
		if (IsActingAsGuard(requester))
		{
			target = GetGuardTarget(requester);
		}
		else
		{
			target = GetAssaultTurret();
			if (target == null) target = ChooseFocusTarget(GetAllEnemiesOfType<Drone>(false), requester.transform.position, previous);
		}

		if (target == null) return null; // garde sans cible : il rejoint son poste
		m_Assignments[requester] = target;
		m_LastTargets[requester] = target;
		return target.transform;
	}

	public void ReleaseFocusTarget(GameObject requester)
	{
		m_Assignments.Remove(requester);
	}

	// Tout le groupe d'assaut sur la même tourelle ; quand elle tombe, la plus proche du groupe
	Turret GetAssaultTurret()
	{
		if (m_AssaultTarget != null && m_AssaultTarget.Health > 0) return m_AssaultTarget;

		List<Turret> enemyTurrets = GetAllEnemiesOfType<Turret>(false).Where(t => t.Health > 0).ToList();
		if (enemyTurrets.Count == 0)
		{
			m_AssaultTarget = null;
			return null;
		}

		Vector3 groupCenter = AssaultGroupCenter();
		m_AssaultTarget = enemyTurrets.OrderBy(t => Vector3.Distance(t.transform.position, groupCenter)).First();
		return m_AssaultTarget;
	}

	Vector3 AssaultGroupCenter()
	{
		List<Vector3> positions = m_Roles
			.Where(kv => kv.Key != null && kv.Value == DroneRole.Assault)
			.Select(kv => kv.Key.transform.position)
			.ToList();
		if (positions.Count == 0)
			positions = m_ArmyElements.OfType<Drone>().Where(d => d != null).Select(d => d.transform.position).ToList();
		return Centroid(positions);
	}

	// Un garde garde sa cible jusqu'à sa mort ; sinon, le drone vert menaçant nos tourelles le plus proche de lui
	ArmyElement GetGuardTarget(GameObject guard)
	{
		if (m_GuardLocks.TryGetValue(guard, out ArmyElement locked) && locked != null && locked.Health > 0) return locked;
		m_GuardLocks.Remove(guard);

		Drone best = null;
		float bestDistance = float.MaxValue;
		foreach (Drone drone in GetAllEnemiesOfType<Drone>(false))
		{
			if (drone.Health <= 0 || !IsThreateningOurTurrets(drone)) continue;
			float distance = Vector3.Distance(guard.transform.position, drone.transform.position);
			if (distance < bestDistance)
			{
				bestDistance = distance;
				best = drone;
			}
		}

		if (best != null) m_GuardLocks[guard] = best;
		return best;
	}

	public Vector3 GetGuardPost(GameObject requester)
	{
		AssignRolesIfNeeded();
		if (!IsActingAsGuard(requester) || !m_GuardIndex.TryGetValue(requester, out int index))
			return requester.transform.position;

		List<Turret> ourTurrets = OurTurrets();
		Vector3 ourCenter = Centroid(ourTurrets.Select(t => t.transform.position));
		List<ArmyElement> enemies = GetAllEnemiesOfType<ArmyElement>(false);
		Vector3 enemyCenter = enemies.Count > 0 ? Centroid(enemies.Select(e => e.transform.position)) : ourCenter + Vector3.left;

		Vector3 forward = Vector3.ProjectOnPlane(enemyCenter - ourCenter, Vector3.up).normalized;
		if (forward.sqrMagnitude < 0.01f) forward = Vector3.left;
		Vector3 right = Vector3.Cross(Vector3.up, forward);
		float lateral = (index - (m_GuardCount - 1) * 0.5f) * m_GuardSpacing;
		Vector3 post = ourCenter + forward * m_GuardPostDistance + right * lateral;

		// Pas trop près de nos tourelles : l'artillerie verte les bombarde
		for (int i = 0; i < 10 && ourTurrets.Any(t => HorizontalDistance(t.transform.position, post) < m_GuardTurretClearance); i++)
			post += forward * 2f;

		Vector3 onTerrain = post, normal = Vector3.zero;
		if (TerrainManager.Instance.GetVerticallyAlignedPositionOnTerrain(post, ref onTerrain, ref normal)) post = onTerrain;
		if (NavMesh.SamplePosition(post, out NavMeshHit hit, 5f, NavMesh.AllAreas)) post = hit.position;
		return post;
	}

	ArmyElement ChooseFocusTarget<T>(List<T> enemies, Vector3 from, ArmyElement previous) where T : ArmyElement
	{
		ArmyElement best = null;
		bool bestIsSaturated = true;
		float bestScore = float.MaxValue;

		foreach (T enemy in enemies)
		{
			if (enemy.Health <= 0) continue; // déjà en train de mourir

			float remaining = enemy.Health - CommittedMissileDamageOn(enemy);
			bool saturated = remaining <= 0;

			// Plus le score est bas, plus la cible est prioritaire
			float score = Mathf.Abs(remaining) + m_DistanceWeight * Vector3.Distance(from, enemy.transform.position);
			if (IsStationary(enemy)) score -= m_ParkedBonus;
			if (!IsInFiringPosition(from, enemy)) score += m_OutOfRangePenalty;
			if (enemy == previous) score -= m_KeepTargetBonus;

			// Une cible qui a encore besoin de dégâts passe toujours avant une cible déjà couverte
			bool isBetter = (bestIsSaturated && !saturated) || (saturated == bestIsSaturated && score < bestScore);
			if (isBetter)
			{
				best = enemy;
				bestIsSaturated = saturated;
				bestScore = score;
			}
		}
		return best;
	}

	float CommittedMissileDamageOn(ArmyElement enemy)
	{
		int nDrones = m_Assignments.Values.Count(target => target == enemy);
		return nDrones * m_ExpectedDamagePerDrone;
	}

	bool IsInFiringPosition(Vector3 from, ArmyElement enemy)
	{
		return Vector3.Distance(from, enemy.transform.position) <= FiringSolution.DefaultFiringRange
			&& FiringSolution.HasClearShot(from, enemy.transform);
	}

	// =====================================================================
	// TOURELLES : le plus gros groupe de drones verts à portée, sinon la tourelle verte la plus proche
	// =====================================================================

	public Transform GetTurretTarget(GameObject requester, float minRadius, float maxRadius)
	{
		Vector3 from = requester.transform.position;
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

	List<Turret> OurTurrets()
	{
		return m_ArmyElements.OfType<Turret>().Where(t => t != null).ToList();
	}

	bool IsThreateningOurTurrets(ArmyElement enemy)
	{
		foreach (Turret turret in OurTurrets())
			if (Vector3.Distance(turret.transform.position, enemy.transform.position) < m_ThreatRadius) return true;
		return false;
	}

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

	static void RemoveDeadEntries(Dictionary<GameObject, ArmyElement> dictionary)
	{
		var deadKeys = dictionary.Where(kv => kv.Key == null || kv.Value == null).Select(kv => kv.Key).ToList();
		foreach (var key in deadKeys) dictionary.Remove(key);
	}

	// =====================================================================
	// STATISTIQUES (code d'origine du squelette)
	// =====================================================================

	public override void ArmyElementHasBeenKilled(GameObject go)
	{
		m_Assignments.Remove(go);
		m_LastTargets.Remove(go);
		m_GuardLocks.Remove(go);
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
