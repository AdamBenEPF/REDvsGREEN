using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

// État-major rouge
//  Phase 1 (il reste des tourelles vertes) :
//    - 10 drones d'assaut détruisent les tourelles vertes une par une, de gauche à droite ;
//    - nos tourelles les détruisent de droite à gauche, ou visent le plus gros groupe vert à portée ;
//    - 3 drones gardes restent devant nos tourelles et abattent les drones verts qui s'en approchent.
//  Phase 2 (plus de tourelle verte) : tous les drones en focus fire sur les drones verts restants.
public partial class ArmyManagerRed : ArmyManager
{
	enum DroneRole { Assault, Guard }

	[Header("Rôles des drones")]
	[Tooltip("Nombre de drones gardes (les plus proches de nos tourelles au début)")]
	[SerializeField] int m_GuardCount = 3;
	[Tooltip("Ordre d'assaut : coché = les tourelles vertes de plus petit z d'abord. Décoche si gauche/droite est inversé à l'écran")]
	[SerializeField] bool m_AssaultFromLowestZ = true;
	[Tooltip("Distance du poste de garde devant nos tourelles, côté ennemi (m)")]
	[SerializeField] float m_GuardPostDistance = 14f;
	[Tooltip("Écart latéral entre deux gardes (m)")]
	[SerializeField] float m_GuardSpacing = 8f;
	[Tooltip("Un drone vert à moins de cette distance d'une de nos tourelles est une menace pour les gardes (m)")]
	[SerializeField] float m_ThreatRadius = 15f;
	[Tooltip("Un drone vert arrêté à moins de cette distance d'un de nos drones est un agresseur à abattre en priorité (m)")]
	[SerializeField] float m_SelfDefenseRadius = 10f;

	[Header("Focus fire sur les drones verts")]
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

	readonly Dictionary<GameObject, DroneRole> m_Roles = new Dictionary<GameObject, DroneRole>();
	readonly Dictionary<GameObject, int> m_GuardIndex = new Dictionary<GameObject, int>();
	// Quel drone rouge est engagé sur quelle unité verte (libéré après le tir)
	readonly Dictionary<GameObject, ArmyElement> m_Assignments = new Dictionary<GameObject, ArmyElement>();
	// Dernière cible de chaque drone rouge (conservée après le tir)
	readonly Dictionary<GameObject, ArmyElement> m_LastTargets = new Dictionary<GameObject, ArmyElement>();

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
		Vector3 from = requester.transform.position;

		ArmyElement target;
		List<Turret> enemyTurrets = AliveEnemyTurretsInAssaultOrder();
		if (enemyTurrets.Count > 0)
		{
			// Phase 1
			if (IsActingAsGuard(requester))
			{
				List<Drone> threats = GetAllEnemiesOfType<Drone>(false).Where(d => IsThreateningOurTurrets(d)).ToList();
				target = ChooseFocusTarget(threats, from, previous);
			}
			else
			{
				// Autodéfense : un drone vert arrêté près de nos drones est en train de leur tirer dessus.
				// On l'abat d'abord (cible immobile et proche), sinon on continue l'assaut des tourelles
				List<Drone> attackers = GetAllEnemiesOfType<Drone>(false)
					.Where(d => IsAttackingOurDrones(d) && d.Health - CommittedMissileDamageOn(d) > 0)
					.ToList();
				target = ChooseFocusTarget(attackers, from, previous);
				if (target == null) target = enemyTurrets[0];
			}
		}
		else
		{
			// Phase 2
			target = ChooseFocusTarget(GetAllEnemiesOfType<Drone>(false), from, previous);
		}

		if (target == null) return null; // garde sans menace : il rejoint son poste
		m_Assignments[requester] = target;
		m_LastTargets[requester] = target;
		return target.transform;
	}

	public void ReleaseFocusTarget(GameObject requester)
	{
		m_Assignments.Remove(requester);
	}

	public Vector3 GetGuardPost(GameObject requester)
	{
		AssignRolesIfNeeded();
		if (!IsActingAsGuard(requester) || !m_GuardIndex.TryGetValue(requester, out int index))
			return requester.transform.position;

		Vector3 ourCenter = Centroid(OurTurrets().Select(t => t.transform.position));
		List<Turret> enemyTurrets = AliveEnemyTurretsInAssaultOrder();
		Vector3 enemyCenter = enemyTurrets.Count > 0 ? Centroid(enemyTurrets.Select(t => t.transform.position)) : ourCenter + Vector3.left;

		Vector3 forward = Vector3.ProjectOnPlane(enemyCenter - ourCenter, Vector3.up).normalized;
		if (forward.sqrMagnitude < 0.01f) forward = Vector3.left;
		Vector3 right = Vector3.Cross(Vector3.up, forward);
		float lateral = (index - (m_GuardCount - 1) * 0.5f) * m_GuardSpacing;
		Vector3 post = ourCenter + forward * m_GuardPostDistance + right * lateral;

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
	// TOURELLES : tourelles vertes de droite à gauche, sinon le plus gros groupe vert à portée
	// =====================================================================

	public Transform GetTurretTarget(GameObject requester, float minRadius, float maxRadius)
	{
		Vector3 from = requester.transform.position;

		List<Turret> order = AliveEnemyTurretsInAssaultOrder();
		order.Reverse(); // nos tourelles partent de l'autre bout que nos drones
		if (order.Count > 0 && IsWithinRadius(from, order[0].transform.position, minRadius, maxRadius))
			return order[0].transform;

		ArmyElement cluster = ChooseLargestCluster(from, minRadius, maxRadius);
		return cluster != null ? cluster.transform : null;
	}

	ArmyElement ChooseLargestCluster(Vector3 from, float minRadius, float maxRadius)
	{
		List<ArmyElement> enemies = GetAllEnemiesOfType<ArmyElement>(false).Where(e => e.Health > 0).ToList();
		ArmyElement best = null;
		float bestScore = 0f;

		foreach (ArmyElement candidate in enemies)
		{
			if (!IsWithinRadius(from, candidate.transform.position, minRadius, maxRadius)) continue;

			// Une grenade touche tout dans un rayon de 5 m ; une unité immobile compte double (elle sera encore là à l'impact)
			float score = 0f;
			foreach (ArmyElement other in enemies)
				if (Vector3.Distance(other.transform.position, candidate.transform.position) < m_GrenadeRadius)
					score += IsStationary(other) ? 1f : 0.5f;

			if (score > bestScore)
			{
				bestScore = score;
				best = candidate;
			}
		}
		return best;
	}

	// =====================================================================
	// OUTILS COMMUNS
	// =====================================================================

	List<Turret> AliveEnemyTurretsInAssaultOrder()
	{
		IEnumerable<Turret> turrets = GetAllEnemiesOfType<Turret>(false).Where(t => t.Health > 0);
		IEnumerable<Turret> ordered = m_AssaultFromLowestZ
			? turrets.OrderBy(t => t.transform.position.z)
			: turrets.OrderByDescending(t => t.transform.position.z);
		return ordered.ToList();
	}

	List<Turret> OurTurrets()
	{
		return m_ArmyElements.OfType<Turret>().Where(t => t != null).ToList();
	}

	bool IsAttackingOurDrones(Drone enemy)
	{
		if (!IsStationary(enemy)) return false;
		foreach (Drone drone in m_ArmyElements.OfType<Drone>())
			if (drone && Vector3.Distance(drone.transform.position, enemy.transform.position) < m_SelfDefenseRadius) return true;
		return false;
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
