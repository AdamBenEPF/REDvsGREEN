using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

public partial class ArmyManagerRed : ArmyManager
{
	[Header("Drones : focus fire")]
	[Tooltip("Dégâts attendus d'une salve de drone : 2 missiles x 5 pts, moins les ratés")]
	[SerializeField] float m_ExpectedDamagePerDrone = 7f;
	[Tooltip("Poids de la distance dans le choix de la cible (PV équivalents par mètre)")]
	[SerializeField] float m_DistanceWeight = 1f;
	[Tooltip("Pénalité d'une cible hors de portée ou masquée par un obstacle (PV équivalents)")]
	[SerializeField] float m_OutOfRangePenalty = 30f;
	[Tooltip("Bonus pour garder la cible de la salve précédente (évite de courir partout)")]
	[SerializeField] float m_KeepTargetBonus = 20f;

	[Header("Menaces sur nos tourelles")]
	[Tooltip("Un drone vert à moins de cette distance d'une de nos tourelles est une menace")]
	[SerializeField] float m_ThreatRadius = 12f;
	[Tooltip("En dessous de cette vitesse (m/s), un drone vert est à l'arrêt")]
	[SerializeField] float m_ParkedSpeed = 1f;
	[Tooltip("Bonus de priorité d'une menace pour nos drones (PV équivalents)")]
	[SerializeField] float m_ThreatBonus = 60f;
	[Tooltip("Bonus de priorité d'un drone vert à l'arrêt pour nos drones (PV équivalents)")]
	[SerializeField] float m_ParkedBonus = 25f;

	[Header("Tourelles")]
	[Tooltip("Dégâts d'une grenade (Annexe A)")]
	[SerializeField] float m_GrenadeDamage = 10f;
	[Tooltip("Rayon de dégâts d'une grenade (Annexe A)")]
	[SerializeField] float m_GrenadeRadius = 5f;
	[Tooltip("Durée pendant laquelle une grenade compte comme engagée : rotation + vol (s)")]
	[SerializeField] float m_GrenadeCommitDuration = 6f;
	[Tooltip("Bonus par drone vert supplémentaire dans le rayon de la grenade (PV équivalents)")]
	[SerializeField] float m_ClusterBonus = 30f;

	// Drones : quel drone rouge est engagé sur quelle unité verte (libéré après le tir)
	readonly Dictionary<GameObject, ArmyElement> m_Assignments = new Dictionary<GameObject, ArmyElement>();
	// Drones : dernière cible de chaque drone rouge (conservée après le tir)
	readonly Dictionary<GameObject, ArmyElement> m_LastTargets = new Dictionary<GameObject, ArmyElement>();
	// Tourelles : grenades engagées (cible + instant de la décision)
	readonly List<(ArmyElement target, float time)> m_GrenadeShots = new List<(ArmyElement target, float time)>();

	// =====================================================================
	// DRONES : phase 1 = artillerie verte, phase 2 = drones verts restants
	// =====================================================================

	public Transform GetFocusTarget(GameObject requester)
	{
		RemoveDeadEntries(m_Assignments);
		RemoveDeadEntries(m_LastTargets);
		m_Assignments.Remove(requester); // le demandeur ne compte pas dans les engagements en cours
		m_LastTargets.TryGetValue(requester, out ArmyElement previous);

		Vector3 from = requester.transform.position;
		ArmyElement target = ChooseDroneTarget(GetAllEnemiesOfType<Turret>(false), from, previous);
		if (target == null) target = ChooseDroneTarget(GetAllEnemiesOfType<Drone>(false), from, previous);
		if (target == null) return null;

		m_Assignments[requester] = target;
		m_LastTargets[requester] = target;
		return target.transform;
	}

	public void ReleaseFocusTarget(GameObject requester)
	{
		m_Assignments.Remove(requester);
	}

	ArmyElement ChooseDroneTarget<T>(List<T> enemies, Vector3 from, ArmyElement previous) where T : ArmyElement
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
			if (IsThreateningOurTurrets(enemy)) score -= m_ThreatBonus;
			if (IsParked(enemy)) score -= m_ParkedBonus;
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
	// TOURELLES : 1) drones verts qui attaquent nos tourelles
	//             2) focus fire sur une tourelle verte
	//             3) n'importe quel drone vert à portée
	// =====================================================================

	public Transform GetTurretTarget(GameObject requester, float minRadius, float maxRadius)
	{
		m_GrenadeShots.RemoveAll(shot => shot.target == null || Time.time - shot.time > m_GrenadeCommitDuration);
		Vector3 from = requester.transform.position;

		ArmyElement target = ChooseGrenadeTargetAmongDrones(from, minRadius, maxRadius, true);
		if (target == null) target = ChooseGrenadeTargetAmongTurrets(from, minRadius, maxRadius);
		if (target == null) target = ChooseGrenadeTargetAmongDrones(from, minRadius, maxRadius, false);
		if (target == null) return null;

		m_GrenadeShots.Add((target, Time.time));
		return target.transform;
	}

	ArmyElement ChooseGrenadeTargetAmongDrones(Vector3 from, float minRadius, float maxRadius, bool onlyParkedThreats)
	{
		List<Drone> enemyDrones = GetAllEnemiesOfType<Drone>(false);
		ArmyElement best = null;
		float bestScore = float.MaxValue;

		foreach (Drone drone in enemyDrones)
		{
			if (drone.Health <= 0) continue;
			if (!IsWithinRadius(from, drone.transform.position, minRadius, maxRadius)) continue;
			bool parked = IsParked(drone);
			if (onlyParkedThreats && !(parked && IsThreateningOurTurrets(drone))) continue;

			float remaining = drone.Health - CommittedGrenadeDamageOn(drone);
			if (remaining <= 0) continue;

			// Une grenade touche tout dans un rayon de 5 m : on vise les groupes
			int neighbours = enemyDrones.Count(other => other != drone
				&& Vector3.Distance(other.transform.position, drone.transform.position) < m_GrenadeRadius);
			float score = remaining - m_ClusterBonus * neighbours - (parked ? m_ParkedBonus : 0f);

			if (score < bestScore)
			{
				best = drone;
				bestScore = score;
			}
		}
		return best;
	}

	ArmyElement ChooseGrenadeTargetAmongTurrets(Vector3 from, float minRadius, float maxRadius)
	{
		ArmyElement best = null;
		bool bestIsSaturated = true;
		float bestScore = float.MaxValue;

		foreach (Turret turret in GetAllEnemiesOfType<Turret>(false))
		{
			if (turret.Health <= 0) continue;
			if (!IsWithinRadius(from, turret.transform.position, minRadius, maxRadius)) continue;

			float remaining = turret.Health - CommittedGrenadeDamageOn(turret);
			bool saturated = remaining <= 0;
			float score = Mathf.Abs(remaining); // la plus entamée d'abord : toutes nos tourelles convergent

			bool isBetter = (bestIsSaturated && !saturated) || (saturated == bestIsSaturated && score < bestScore);
			if (isBetter)
			{
				best = turret;
				bestIsSaturated = saturated;
				bestScore = score;
			}
		}
		return best;
	}

	float CommittedGrenadeDamageOn(ArmyElement enemy)
	{
		return m_GrenadeShots.Count(shot => shot.target == enemy) * m_GrenadeDamage;
	}

	static bool IsWithinRadius(Vector3 from, Vector3 to, float minRadius, float maxRadius)
	{
		float distance = Vector3.Distance(from, to);
		return distance > minRadius && distance < maxRadius;
	}

	// =====================================================================
	// OUTILS COMMUNS
	// =====================================================================

	bool IsThreateningOurTurrets(ArmyElement enemy)
	{
		foreach (Turret turret in m_ArmyElements.OfType<Turret>())
		{
			if (turret && Vector3.Distance(turret.transform.position, enemy.transform.position) < m_ThreatRadius)
				return true;
		}
		return false;
	}

	bool IsParked(ArmyElement enemy)
	{
		NavMeshAgent agent = enemy.GetComponent<NavMeshAgent>();
		return agent != null && agent.velocity.magnitude < m_ParkedSpeed;
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
