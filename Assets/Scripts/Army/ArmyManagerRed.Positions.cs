using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

// Placement des drones : chacun sa position de tir, espacées, et qui tournent autour de la cible
public partial class ArmyManagerRed
{
	[Header("Placement des drones")]
	[Tooltip("Distance de tir visée autour de la cible (m)")]
	[SerializeField] float m_FiringDistance = 22f;
	[Tooltip("Distance minimale entre deux drones rouges (m) : une grenade touche dans un rayon de 5 m")]
	[SerializeField] float m_MinSpacing = 10f;
	[Tooltip("Décalage autour de la cible à chaque salve (°) : un drone ne reste jamais au même endroit")]
	[SerializeField] float m_OrbitStep = 25f;
	[Tooltip("Marge de sécurité autour d'un impact de grenade (m)")]
	[SerializeField] float m_DangerMargin = 2f;

	readonly Dictionary<GameObject, Vector3> m_FiringSlots = new Dictionary<GameObject, Vector3>();

	// Sens de rotation autour de la cible, attribué en alternance au premier appel de chaque drone
	readonly Dictionary<GameObject, float> m_OrbitSigns = new Dictionary<GameObject, float>();

	float GetOrbitSign(GameObject drone)
	{
		if (!m_OrbitSigns.TryGetValue(drone, out float sign))
		{
			sign = m_OrbitSigns.Count % 2 == 0 ? 1f : -1f;
			m_OrbitSigns[drone] = sign;
		}
		return sign;
	}

	public Vector3 GetFiringPosition(GameObject requester, Transform target)
	{
		var deadKeys = m_FiringSlots.Keys.Where(key => key == null).ToList();
		foreach (var key in deadKeys) m_FiringSlots.Remove(key);
		m_FiringSlots.Remove(requester);

		Vector3 targetPos = target.position;
		Vector3 fromTarget = Vector3.ProjectOnPlane(requester.transform.position - targetPos, Vector3.up);
		if (fromTarget.sqrMagnitude < 0.01f) fromTarget = Vector3.forward;

		// La moitié des drones tourne dans un sens, l'autre moitié dans l'autre
		float orbitSign = GetOrbitSign(requester);
		float baseAngle = Mathf.Atan2(fromTarget.x, fromTarget.z) * Mathf.Rad2Deg + orbitSign * m_OrbitStep;

		// 1er passage : on respecte l'espacement. 2e passage : on l'ignore si aucune place n'a été trouvée
		for (int pass = 0; pass < 2; pass++)
		{
			for (int k = 0; k < 13; k++)
			{
				float offset = ((k + 1) / 2) * 20f * (k % 2 == 0 ? 1f : -1f); // 0, -20, +20, -40, +40...
				float angle = (baseAngle + offset) * Mathf.Deg2Rad;
				Vector3 candidate = targetPos + new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * m_FiringDistance;

				Vector3 onTerrain = candidate, normal = Vector3.zero;
				if (TerrainManager.Instance.GetVerticallyAlignedPositionOnTerrain(candidate, ref onTerrain, ref normal))
					candidate = onTerrain;
				if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, 3f, NavMesh.AllAreas)) continue;

				Vector3 slot = hit.position;
				if (pass == 0 && IsTooCloseToOtherDrones(requester, slot)) continue;
				if (IsInGrenadeDanger(slot, m_DangerMargin)) continue;
				if (!FiringSolution.HasClearShot(slot + Vector3.up, target)) continue;

				m_FiringSlots[requester] = slot;
				return slot;
			}
		}

		m_FiringSlots[requester] = requester.transform.position; // aucune place : on reste où on est
		return requester.transform.position;
	}

	bool IsTooCloseToOtherDrones(GameObject requester, Vector3 position)
	{
		foreach (var slot in m_FiringSlots)
			if (slot.Key != requester && HorizontalDistance(slot.Value, position) < m_MinSpacing) return true;

		foreach (Drone drone in m_ArmyElements.OfType<Drone>())
			if (drone && drone.gameObject != requester
				&& HorizontalDistance(drone.transform.position, position) < m_MinSpacing * 0.5f) return true;

		return false;
	}
}
