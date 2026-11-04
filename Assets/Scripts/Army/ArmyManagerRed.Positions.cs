using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

// Placement des drones :
//  - sur une tourelle verte : collés à elle, sous son rayon minimal de tir (5 m, Annexe A) ;
//  - sur un drone vert : à distance de tir, espacés de 10 m les uns des autres.
// Dans les deux cas, la position tourne autour de la cible à chaque salve.
public partial class ArmyManagerRed
{
	[Header("Placement des drones")]
	[Tooltip("Distance de tir autour d'un drone vert (m)")]
	[SerializeField] float m_FiringDistance = 14f;
	[Tooltip("Distance minimale entre deux drones rouges autour d'un drone vert (m)")]
	[SerializeField] float m_MinSpacing = 10f;
	[Tooltip("Distance à laquelle nos drones se collent à une tourelle verte (m). Sous 5 m, elle ne peut pas les viser (Min Radius, Annexe A)")]
	[SerializeField] float m_HugDistance = 3f;
	[Tooltip("Distance minimale entre deux drones rouges collés à une tourelle (m)")]
	[SerializeField] float m_HugSpacing = 1.5f;
	[Tooltip("Décalage autour de la cible à chaque salve (°) : un drone ne reste jamais au même endroit")]
	[SerializeField] float m_OrbitStep = 25f;
	[Tooltip("Marge de sécurité autour d'un impact de grenade (m)")]
	[SerializeField] float m_DangerMargin = 2f;

	readonly Dictionary<GameObject, Vector3> m_FiringSlots = new Dictionary<GameObject, Vector3>();
	// Sens de rotation autour de la cible, attribué en alternance au premier appel de chaque drone
	readonly Dictionary<GameObject, float> m_OrbitSigns = new Dictionary<GameObject, float>();

	public Vector3 GetFiringPosition(GameObject requester, Transform target)
	{
		var deadKeys = m_FiringSlots.Keys.Where(key => key == null).ToList();
		foreach (var key in deadKeys) m_FiringSlots.Remove(key);
		m_FiringSlots.Remove(requester);

		bool hugTurret = target.GetComponent<Turret>() != null;
		float distance = hugTurret ? m_HugDistance : m_FiringDistance;
		float spacing = hugTurret ? m_HugSpacing : m_MinSpacing;
		float angleStep = hugTurret ? 30f : 20f;

		Vector3 targetPos = target.position;
		Vector3 fromTarget = Vector3.ProjectOnPlane(requester.transform.position - targetPos, Vector3.up);
		if (fromTarget.sqrMagnitude < 0.01f) fromTarget = Vector3.forward;
		float baseAngle = Mathf.Atan2(fromTarget.x, fromTarget.z) * Mathf.Rad2Deg + GetOrbitSign(requester) * m_OrbitStep;

		// 1er passage : on respecte l'espacement. 2e passage : on l'ignore si aucune place n'a été trouvée
		for (int pass = 0; pass < 2; pass++)
		{
			for (int k = 0; k < 13; k++)
			{
				float offset = ((k + 1) / 2) * angleStep * (k % 2 == 0 ? 1f : -1f); // 0, -pas, +pas, -2 pas...
				float angle = (baseAngle + offset) * Mathf.Deg2Rad;
				Vector3 candidate = targetPos + new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * distance;

				Vector3 onTerrain = candidate, normal = Vector3.zero;
				if (TerrainManager.Instance.GetVerticallyAlignedPositionOnTerrain(candidate, ref onTerrain, ref normal))
					candidate = onTerrain;
				if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, hugTurret ? 2f : 3f, NavMesh.AllAreas)) continue;

				Vector3 slot = hit.position;
				if (pass == 0 && IsTooCloseToOtherDrones(requester, slot, spacing)) continue;
				if (CanEvade(requester) && IsInGrenadeDanger(slot, m_DangerMargin)) continue;
				if (!FiringSolution.CanHit(slot + Vector3.up, target)) continue;

				m_FiringSlots[requester] = slot;
				return slot;
			}
		}

		m_FiringSlots[requester] = requester.transform.position; // aucune place : MoveToFiringSlot fonce vers la cible
		return requester.transform.position;
	}

	float GetOrbitSign(GameObject drone)
	{
		if (!m_OrbitSigns.TryGetValue(drone, out float sign))
		{
			sign = m_OrbitSigns.Count % 2 == 0 ? 1f : -1f;
			m_OrbitSigns[drone] = sign;
		}
		return sign;
	}

	bool IsTooCloseToOtherDrones(GameObject requester, Vector3 position)
	{
		return IsTooCloseToOtherDrones(requester, position, m_MinSpacing);
	}

	bool IsTooCloseToOtherDrones(GameObject requester, Vector3 position, float spacing)
	{
		foreach (var slot in m_FiringSlots)
			if (slot.Key != requester && HorizontalDistance(slot.Value, position) < spacing) return true;

		foreach (Drone drone in m_ArmyElements.OfType<Drone>())
			if (drone && drone.gameObject != requester
				&& HorizontalDistance(drone.transform.position, position) < spacing * 0.5f) return true;

		return false;
	}
}
