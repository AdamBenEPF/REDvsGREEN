using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// Distance à laquelle un drone rouge s'arrête pour tirer
public partial class ArmyManagerRed
{
	[Header("Distances d'engagement")]
	[Tooltip("Distance de tir sur un drone vert (m)")]
	[SerializeField] float m_FiringDistance = 14f;
	[Tooltip("Distance à laquelle nos drones se collent à une tourelle verte (m). Sous 5 m, elle ne peut pas les viser (Min Radius, Annexe A)")]
	[SerializeField] float m_HugDistance = 3f;
	[Tooltip("Distance maximale à laquelle on cherche un point joignable autour d'une tourelle verte perchée (m)")]
	[SerializeField] float m_MaxReachSearch = 14f;

	// Distance d'engagement de chaque tourelle verte, calculée une fois au début.
	// Certaines tourelles sont perchées hors du NavMesh (TurretGreen (6) : le point joignable le plus proche est à ~7 m) :
	// un drone ne peut pas s'en approcher à 3 m et restait bloqué sans jamais tirer.
	readonly Dictionary<Turret, float> m_TurretEngageDistances = new Dictionary<Turret, float>();

	// Collé à une tourelle (dans son angle mort sous 5 m) si possible, sinon au plus près ; à distance de tir d'un drone
	public float GetEngageDistance(Transform target)
	{
		Turret turret = target.GetComponent<Turret>();
		if (turret == null) return m_FiringDistance;
		return m_TurretEngageDistances.TryGetValue(turret, out float distance) ? distance : m_HugDistance;
	}

	// Pour chaque tourelle verte, plus petit rayon autour d'elle où un point du NavMesh est joignable depuis notre camp
	void ComputeTurretEngageDistances(Vector3 from)
	{
		m_TurretEngageDistances.Clear();
		if (!NavMesh.SamplePosition(from, out NavMeshHit start, 3f, NavMesh.AllAreas)) return;
		NavMeshPath path = new NavMeshPath();

		foreach (Turret turret in GetAllEnemiesOfType<Turret>(false))
		{
			Vector3 center = turret.transform.position;
			float reach = m_MaxReachSearch;
			for (float radius = 1f; radius <= m_MaxReachSearch && reach == m_MaxReachSearch; radius += 1f)
				for (int i = 0; i < 16; i++)
				{
					Vector3 candidate = center + Quaternion.Euler(0f, i * 22.5f, 0f) * Vector3.forward * radius;
					if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, 0.75f, NavMesh.AllAreas)) continue;
					if (!NavMesh.CalculatePath(start.position, hit.position, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete) continue;
					reach = HorizontalDistance(center, hit.position);
					break;
				}
			m_TurretEngageDistances[turret] = Mathf.Max(m_HugDistance, reach + 1f);
		}
	}
}
