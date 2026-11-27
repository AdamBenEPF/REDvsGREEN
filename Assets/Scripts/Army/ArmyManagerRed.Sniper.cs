using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

// Tir d'élite en fin de bataille.
// Une tourelle verte ne vise que les unités situées entre 5 et 50 m d'elle (Select Enemy, Annexe A). Un missile, lui,
// porte jusqu'à ~58 m (0,25 s d'accélération puis 20 m/s jusqu'à 3 s de vol). Quand il n'y a plus de drone vert, nos
// drones tirent donc sur chaque tourelle verte depuis un point situé à plus de 50 m d'elle ET hors de portée de toutes
// les autres : plus aucune grenade ne peut les viser. Mesures v15 : 4 des 7 défaites étaient des fins de bataille
// contre 2 à 5 tourelles seules, où nos drones mouraient un à un sous les grenades.
public partial class ArmyManagerRed
{
	[Header("Tir d'élite (fin de bataille)")]
	[Tooltip("Plus de drone vert : tirer sur les tourelles vertes depuis un point hors de portée de toutes les tourelles")]
	[SerializeField] bool m_SnipeWhenNoGreenDrones = true;
	[Tooltip("Distance minimale (3D) à la tourelle visée (m) : 50 m de Max Radius + 1 m de marge")]
	[SerializeField] float m_SnipeMinDistance = 51f;
	[Tooltip("Distance maximale (m) : en dessous de la portée d'un missile (~58 m)")]
	[SerializeField] float m_SnipeMaxDistance = 56f;
	[Tooltip("Hauteur du drone au-dessus du NavMesh (Base Offset du NavMeshAgent, m)")]
	[SerializeField] float m_DroneFlightHeight = 1f;

	const int SniperDirections = 72;
	const int SniperPathChecks = 10;

	// Point de tir d'élite de chaque tourelle verte (null : aucun point sûr), recalculé à chaque tourelle détruite
	readonly Dictionary<Turret, Vector3?> m_SniperSpots = new Dictionary<Turret, Vector3?>();
	int m_SniperSpotsTurretCount = -1;

	List<Turret> AliveGreenTurrets() => GetAllEnemiesOfType<Turret>(false).Where(t => t.Health > 0).ToList();

	public bool TryGetSniperSpot(Transform target, out Vector3 spot)
	{
		spot = Vector3.zero;
		if (!m_SnipeWhenNoGreenDrones || GreenDroneCount() > 0) return false;
		Turret turret = target.GetComponent<Turret>();
		if (turret == null) return false;

		List<Turret> greenTurrets = AliveGreenTurrets();
		if (greenTurrets.Count != m_SniperSpotsTurretCount)
		{
			m_SniperSpots.Clear();
			m_SniperSpotsTurretCount = greenTurrets.Count;
		}
		if (!m_SniperSpots.TryGetValue(turret, out Vector3? cached))
		{
			cached = ComputeSniperSpot(turret, greenTurrets);
			m_SniperSpots[turret] = cached;
		}
		if (!cached.HasValue) return false;
		spot = cached.Value;
		return true;
	}

	// Tourelle verte qui a un point de tir d'élite, la plus proche du drone ; null si aucune (ou s'il reste des drones verts)
	Transform GetSniperTarget(GameObject requester)
	{
		if (!m_SnipeWhenNoGreenDrones || GreenDroneCount() > 0) return null;
		Transform best = null;
		float bestDistance = float.MaxValue;
		foreach (Turret turret in AliveGreenTurrets())
		{
			if (!TryGetSniperSpot(turret.transform, out Vector3 spot)) continue;
			float distance = HorizontalDistance(requester.transform.position, spot);
			if (distance < bestDistance)
			{
				bestDistance = distance;
				best = turret.transform;
			}
		}
		return best;
	}

	// Point joignable à plus de m_SnipeMinDistance de la tourelle, hors de portée des autres, d'où un missile l'atteint
	Vector3? ComputeSniperSpot(Turret target, List<Turret> greenTurrets)
	{
		Drone anyDrone = m_ArmyElements.OfType<Drone>().FirstOrDefault(d => d != null);
		if (anyDrone == null || !NavMesh.SamplePosition(anyDrone.transform.position, out NavMeshHit start, 3f, NavMesh.AllAreas)) return null;

		var candidates = new List<KeyValuePair<float, Vector3>>();
		float[] radii = { m_SnipeMinDistance + 0.5f, (m_SnipeMinDistance + m_SnipeMaxDistance) / 2f, m_SnipeMaxDistance - 0.5f };
		foreach (float radius in radii)
			for (int i = 0; i < SniperDirections; i++)
			{
				Vector3 direction = Quaternion.Euler(0f, i * 360f / SniperDirections, 0f) * Vector3.forward;
				if (!NavMesh.SamplePosition(target.transform.position + direction * radius, out NavMeshHit hit, 1f, NavMesh.AllAreas)) continue;
				Vector3 dronePosition = hit.position + Vector3.up * m_DroneFlightHeight;
				if (Vector3.Distance(dronePosition, target.transform.position) < m_SnipeMinDistance) continue;
				if (IsInRangeOfAnyTurret(dronePosition, greenTurrets)) continue;
				if (!FiringSolution.CanHit(dronePosition, target.transform)) continue; // caisses, murs, déviation sur le relief
				candidates.Add(new KeyValuePair<float, Vector3>(radius, hit.position));
			}

		// Le plus près possible (meilleure précision), et joignable depuis notre camp
		NavMeshPath path = new NavMeshPath();
		foreach (var candidate in candidates.OrderBy(c => c.Key).Take(SniperPathChecks))
			if (NavMesh.CalculatePath(start.position, candidate.Value, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete)
				return candidate.Value;
		return null;
	}

	static bool IsInRangeOfAnyTurret(Vector3 position, List<Turret> turrets)
	{
		foreach (Turret turret in turrets)
			if (IsWithinRadius(turret.transform.position, position, TurretMinRadius, TurretMaxRadius)) return true;
		return false;
	}
}
