using UnityEngine;
using UnityEngine.AI;

// Tir en mouvement : choix des points de passage de StrafeAroundTarget.
// Les points sont sur un cercle autour de la cible : collé à une tourelle verte (dans son angle mort sous 5 m),
// à distance de tir d'un drone vert. Chaque bond fait au moins m_StrafeMinLeg mètres, pour quitter à coup sûr
// la zone d'impact d'une grenade tirée sur la position de départ, et évite les impacts déjà prévus.
public partial class ArmyManagerRed
{
	[Header("Tir en mouvement")]
	[Tooltip("Les drones continuent de se déplacer pendant la visée, le tir et le Wait")]
	[SerializeField] bool m_StrafeWhileFiring = true;
	[Tooltip("Longueur minimale d'un bond (m) : rayon de la grenade (5 m) + rayon du drone + marge")]
	[SerializeField] float m_StrafeMinLeg = 6f;
	[Tooltip("Rayon du cercle autour d'une tourelle verte (m), sous ses 5 m de rayon minimal de tir")]
	[SerializeField] float m_StrafeHugRadius = 3.5f;
	[Tooltip("Rayon du cercle autour d'une tourelle verte quand l'artillerie verte peut viser le drone (m). Sur un cercle de 3,5 m, "
		+ "aucun point n'est assez loin d'un impact de grenade (5 m) : il faut plus de place pour esquiver en tournant")]
	[SerializeField] float m_StrafeExposedTurretRadius = 8f;
	[Tooltip("Distance minimale à un drone vert visé (m)")]
	[SerializeField] float m_StrafeMinDroneDistance = 8f;

	public bool StrafeWhileFiring => m_StrafeWhileFiring;

	float StrafeRadius(Vector3 dronePosition, Transform target)
	{
		if (target.GetComponent<Turret>() != null) return Mathf.Max(m_StrafeHugRadius, GetEngageDistance(target, dronePosition));
		return Mathf.Clamp(HorizontalDistance(dronePosition, target.position), m_StrafeMinDroneDistance, m_FiringDistance);
	}

	public bool IsStrafePointStillValid(GameObject drone, Transform target, Vector3 point)
	{
		if (IsInGrenadeDanger(point, 1f)) return false;
		float radius = StrafeRadius(drone.transform.position, target);
		// la cible (un drone) a pu bouger : le point doit rester à distance de tir
		return Mathf.Abs(HorizontalDistance(point, target.position) - radius) < 3f;
	}

	public bool TryGetStrafePoint(GameObject drone, Transform target, out Vector3 point)
	{
		point = Vector3.zero;
		Vector3 position = drone.transform.position;
		bool isTurret = target.GetComponent<Turret>() != null;
		float radius = StrafeRadius(position, target);

		float bestScore = float.MaxValue;
		for (int i = 0; i < 24; i++)
		{
			Vector3 direction = Quaternion.Euler(0f, i * 15f, 0f) * Vector3.forward;
			if (!NavMesh.SamplePosition(target.position + direction * radius, out NavMeshHit hit, 1.5f, NavMesh.AllAreas)) continue;
			Vector3 candidate = hit.position;

			float leg = HorizontalDistance(position, candidate);
			if (leg < m_StrafeMinLeg) continue;
			if (IsInGrenadeDanger(candidate, 1f)) continue;
			Vector3 flying = candidate + Vector3.up * (position.y - candidate.y); // à hauteur de vol
			if (!isTurret && !FiringSolution.HasClearShot(flying, target)) continue;

			// bond le plus court possible, à l'écart des autres drones rouges, de préférence hors de portée de l'artillerie
			float score = leg
				+ m_EscapeCrowdPenalty * CountRedDronesNear(drone, candidate, m_GrenadeRadius + 0.5f)
				- (IsExposedToArtillery(flying) ? 0f : m_SafeSpotBonus);
			if (score < bestScore)
			{
				bestScore = score;
				point = candidate;
			}
		}
		return bestScore < float.MaxValue;
	}
}
