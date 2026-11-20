using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// Repositionnement sous le feu de l'artillerie verte.
// Une tourelle verte ne vise nos drones que lorsqu'aucune de nos tourelles n'est à sa portée (Selector de TurretGreenBehavior).
// Sa grenade tombe 4 s après le tir, là où était le drone au moment du tir. Un drone exposé change donc de position
// de tir à chaque salve, d'au moins m_RepositionDistance : il a quitté la zone d'impact avant que la grenade n'arrive.
public partial class ArmyManagerRed
{
	[Header("Repositionnement sous l'artillerie")]
	[Tooltip("Change de position de tir à chaque salve quand une tourelle verte peut viser le drone")]
	[SerializeField] bool m_RepositionWhenExposed = false;
	[Tooltip("Distance minimale entre deux positions de tir successives (m) : rayon de la grenade (5 m) + rayon du drone + marge")]
	[SerializeField] float m_RepositionDistance = 6.5f;
	[Tooltip("Rayon autour d'une tourelle verte visée (m), sous ses 5 m de rayon minimal de tir")]
	[SerializeField] float m_HugRingRadius = 4f;
	[Tooltip("Bonus d'une position hors de portée de l'artillerie verte (m équivalents)")]
	[SerializeField] float m_SafeSpotBonus = 4f;

	const float TurretMinRadius = 5f;  // Annexe A
	const float TurretMaxRadius = 50f; // Annexe A

	readonly Dictionary<GameObject, Vector3> m_LastFiringPositions = new Dictionary<GameObject, Vector3>();
	readonly List<Turret> m_DroneHuntingTurrets = new List<Turret>();
	int m_DroneHuntingTurretsFrame = -1;

	// Tourelles vertes qui visent nos drones : celles qui n'ont plus aucune de nos tourelles à portée
	List<Turret> DroneHuntingTurrets()
	{
		if (m_DroneHuntingTurretsFrame == Time.frameCount) return m_DroneHuntingTurrets;
		m_DroneHuntingTurretsFrame = Time.frameCount;
		m_DroneHuntingTurrets.Clear();

		foreach (Turret green in GetAllEnemiesOfType<Turret>(false))
		{
			if (green.Health <= 0) continue;
			bool hasRedTurretInRange = false;
			foreach (IArmyElement element in m_ArmyElements)
				if (element is Turret red && red && IsWithinRadius(green.transform.position, red.transform.position, TurretMinRadius, TurretMaxRadius))
				{
					hasRedTurretInRange = true;
					break;
				}
			if (!hasRedTurretInRange) m_DroneHuntingTurrets.Add(green);
		}
		return m_DroneHuntingTurrets;
	}

	public bool IsExposedToArtillery(Vector3 position)
	{
		foreach (Turret green in DroneHuntingTurrets())
			if (IsWithinRadius(green.transform.position, position, TurretMinRadius, TurretMaxRadius)) return true;
		return false;
	}

	public void RecordFiringPosition(GameObject drone, Vector3 position)
	{
		m_LastFiringPositions[drone] = position;
	}

	// Position de tir quand le drone est exposé à l'artillerie : sur un cercle autour de la cible (collé à une tourelle,
	// à distance de tir d'un drone), à plus de m_RepositionDistance de la dernière position de tir, hors des impacts prévus,
	// la plus proche possible, à l'écart des autres drones rouges, et de préférence hors de portée de l'artillerie.
	// Renvoie false si le drone n'est pas exposé ou si aucune position ne convient : le déplacement normal s'applique alors.
	public bool TryGetRepositionSpot(GameObject drone, Transform target, out Vector3 spot)
	{
		spot = Vector3.zero;
		if (!m_RepositionWhenExposed || target == null) return false;

		Vector3 position = drone.transform.position;
		if (!IsExposedToArtillery(position)) return false;

		bool hasLast = m_LastFiringPositions.TryGetValue(drone, out Vector3 last);
		bool isTurret = target.GetComponent<Turret>() != null;
		float radius = isTurret ? m_HugRingRadius : Mathf.Clamp(HorizontalDistance(position, target.position), 8f, m_FiringDistance);

		float bestScore = float.MaxValue;
		for (int i = 0; i < 24; i++)
		{
			Vector3 direction = Quaternion.Euler(0f, i * 15f, 0f) * Vector3.forward;
			if (!NavMesh.SamplePosition(target.position + direction * radius, out NavMeshHit hit, 1.5f, NavMesh.AllAreas)) continue;
			Vector3 candidate = hit.position + Vector3.up * (position.y - hit.position.y); // à hauteur de vol, pour les distances 3D
			if (hasLast && HorizontalDistance(candidate, last) < m_RepositionDistance) continue;
			if (IsInGrenadeDanger(candidate, 1f)) continue;
			if (!isTurret && !FiringSolution.HasClearShot(candidate, target)) continue;

			float score = HorizontalDistance(position, candidate)
				+ m_EscapeCrowdPenalty * CountRedDronesNear(drone, candidate, m_GrenadeRadius + 0.5f)
				- (IsExposedToArtillery(candidate) ? 0f : m_SafeSpotBonus);
			if (score < bestScore)
			{
				bestScore = score;
				spot = hit.position;
			}
		}
		return bestScore < float.MaxValue;
	}
}
