using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// Radar à grenades et esquive « juste à temps ».
// L'état-major voit les grenades vertes en vol. Une grenade suit une trajectoire balistique de 4 s (Rocket.cs) et fait
// ses dégâts dans un rayon de 5 m autour du point visé au moment du tir : dès qu'elle est lancée, on connaît son point
// et son heure d'impact. Un drone n'esquive donc pas dès qu'une grenade est en l'air : il continue de viser et de tirer,
// et ne part qu'au dernier moment, quand le temps restant avant l'impact devient à peine supérieur au temps qu'il lui
// faut pour sortir de la zone (environ 1,3 s depuis l'arrêt, avec 8 m/s² et 10 m/s, Annexe A).
// Réglages validés sur le simulateur Python (Tools/Simulator) puis en jeu.
public partial class ArmyManagerRed
{
	[Header("Esquive des grenades")]
	[Tooltip("Durée de vol d'une grenade (Annexe A : Rocket Travel Duration)")]
	[SerializeField] float m_GrenadeTravelDuration = 4f;
	[Tooltip("Rayon horizontal d'un drone (m) : il est touché si son collider entre dans la sphère de la grenade")]
	[SerializeField] float m_DroneRadius = 0.4f;
	[Tooltip("Marge de sécurité autour de la zone d'impact (m)")]
	[SerializeField] float m_DodgeDistanceMargin = 0.6f;
	[Tooltip("Marge de temps (s) : le drone part quand l'impact arrive dans (temps de sortie + cette marge)")]
	[SerializeField] float m_DodgeTimeMargin = 0.8f;
	[Tooltip("Le drone vise un point situé à cette distance au-delà du bord de la zone (m) : il le franchit à pleine vitesse")]
	[SerializeField] float m_DodgeOvershoot = 2f;
	[Tooltip("Pénalité par drone rouge à portée de la même grenade (m équivalents)")]
	[SerializeField] float m_EscapeCrowdPenalty = 3f;

	const float DroneAcceleration = 8f; // NavMeshAgent, Annexe A (m/s²)
	const float DroneMaxSpeed = 10f;    // My Seek, Annexe A (m/s)

	struct GrenadeImpact
	{
		public Vector3 position;
		public float timeToImpact;
	}

	readonly Dictionary<Rocket, float> m_GrenadeFirstSeen = new Dictionary<Rocket, float>();
	readonly List<GrenadeImpact> m_IncomingImpacts = new List<GrenadeImpact>();
	readonly List<Rocket> m_ForgottenGrenades = new List<Rocket>();
	readonly Dictionary<GameObject, NavMeshAgent> m_Agents = new Dictionary<GameObject, NavMeshAgent>();

	float DangerRadius => m_GrenadeRadius + m_DroneRadius + m_DodgeDistanceMargin;

	void FixedUpdate()
	{
		UpdateIncomingImpacts();
	}

	void UpdateIncomingImpacts()
	{
		m_IncomingImpacts.Clear();
		string ownTag = OwnTag;
		if (ownTag == null) return;

		foreach (Rocket grenade in FindObjectsByType<Rocket>(FindObjectsSortMode.None))
		{
			if (grenade.CompareTag(ownTag)) continue; // nos propres grenades ne nous blessent pas
			if (!m_GrenadeFirstSeen.ContainsKey(grenade)) m_GrenadeFirstSeen[grenade] = Time.time;

			Rigidbody body = grenade.GetComponent<Rigidbody>();
			if (body == null || body.linearVelocity.sqrMagnitude < 0.01f) continue; // pas encore lancée

			float timeToImpact = m_GrenadeTravelDuration - (Time.time - m_GrenadeFirstSeen[grenade]);
			if (timeToImpact <= 0f) continue;

			// Trajectoire balistique : p(t) = p0 + v0.t + g.t²/2
			Vector3 impact = body.position + body.linearVelocity * timeToImpact
				+ 0.5f * Physics.gravity * timeToImpact * timeToImpact;
			m_IncomingImpacts.Add(new GrenadeImpact { position = impact, timeToImpact = timeToImpact });
		}

		// On oublie les grenades qui ont explosé
		m_ForgottenGrenades.Clear();
		foreach (Rocket grenade in m_GrenadeFirstSeen.Keys)
			if (grenade == null) m_ForgottenGrenades.Add(grenade);
		foreach (Rocket grenade in m_ForgottenGrenades) m_GrenadeFirstSeen.Remove(grenade);
	}

	// Vrai si une grenade va tomber près de ce point, quel que soit le moment (choix des positions de tir)
	public bool IsInGrenadeDanger(Vector3 position, float margin)
	{
		foreach (GrenadeImpact impact in m_IncomingImpacts)
			if (HorizontalDistance(position, impact.position) < m_GrenadeRadius + margin) return true;
		return false;
	}

	// =====================================================================
	// ESQUIVE JUSTE À TEMPS
	// =====================================================================

	// Vrai s'il faut partir maintenant : une grenade menace le drone et il ne lui reste que le temps de sortir de la zone
	public bool ShouldDodge(GameObject drone)
	{
		if (!CanEvade(drone)) return false;
		if (!TryGetThreat(drone, out GrenadeImpact threat)) return false;
		return threat.timeToImpact <= EscapeTime(drone, threat.position) + m_DodgeTimeMargin;
	}

	// Vrai tant qu'une grenade tombera sur le drone, là où il est ou là où il va
	public bool IsThreatened(GameObject drone)
	{
		return TryGetThreat(drone, out _);
	}

	// Impact le plus proche dans le temps qui touchera le drone : à sa position actuelle, ou à celle qu'il aura atteinte
	// au moment de l'impact s'il continue son déplacement (un drone qui fonce vers sa cible peut entrer dans une zone)
	bool TryGetThreat(GameObject drone, out GrenadeImpact threat)
	{
		threat = default;
		bool found = false;
		Vector3 position = drone.transform.position;
		foreach (GrenadeImpact impact in m_IncomingImpacts)
		{
			bool hereHit = HorizontalDistance(position, impact.position) < DangerRadius;
			bool laterHit = HorizontalDistance(PredictedPosition(drone, impact.timeToImpact), impact.position) < DangerRadius;
			if ((hereHit || laterHit) && (!found || impact.timeToImpact < threat.timeToImpact))
			{
				threat = impact;
				found = true;
			}
		}
		return found;
	}

	NavMeshAgent AgentOf(GameObject drone)
	{
		if (!m_Agents.TryGetValue(drone, out NavMeshAgent agent))
		{
			agent = drone.GetComponent<NavMeshAgent>();
			m_Agents[drone] = agent;
		}
		return agent;
	}

	// Position du drone dans "time" secondes s'il continue ce qu'il fait : chemin en cours, ou freinage jusqu'à l'arrêt
	Vector3 PredictedPosition(GameObject drone, float time)
	{
		Vector3 position = drone.transform.position;
		NavMeshAgent agent = AgentOf(drone);
		if (agent == null || !agent.isActiveAndEnabled || !agent.isOnNavMesh) return position;

		Vector3 velocity = Vector3.ProjectOnPlane(agent.velocity, Vector3.up);
		float speed = velocity.magnitude;
		if (agent.hasPath && !agent.isStopped && !agent.pathPending)
		{
			float remaining = agent.remainingDistance - agent.stoppingDistance;
			if (!float.IsInfinity(remaining) && remaining <= speed * time + 0.5f)
			{
				Vector3 end = agent.destination;
				end.y = position.y;
				return Vector3.MoveTowards(end, position, agent.stoppingDistance);
			}
			return position + velocity * time;
		}
		if (speed < 0.01f) return position;
		return position + velocity / speed * (speed * speed / (2f * DroneAcceleration)); // distance de freinage
	}

	// Temps pour sortir de la zone d'un impact en partant à l'opposé : freiner si on s'en approche, puis accélérer
	float EscapeTime(GameObject drone, Vector3 impact)
	{
		Vector3 offset = Vector3.ProjectOnPlane(drone.transform.position - impact, Vector3.up);
		float distance = offset.magnitude;
		float need = DangerRadius + 0.2f - distance;
		if (need <= 0f) need = 1f; // le drone y entre : il doit au moins s'arrêter et repartir

		NavMeshAgent agent = AgentOf(drone);
		Vector3 velocity = agent != null ? Vector3.ProjectOnPlane(agent.velocity, Vector3.up) : Vector3.zero;
		float radialSpeed = distance > 0.001f ? Vector3.Dot(velocity, offset / distance) : 0f; // > 0 : on s'éloigne

		float brakeTime = Mathf.Max(0f, -radialSpeed) / DroneAcceleration;
		need += radialSpeed < 0f ? radialSpeed * radialSpeed / (2f * DroneAcceleration) : 0f;
		float startSpeed = Mathf.Max(0f, radialSpeed);

		float accelerationTime = (DroneMaxSpeed - startSpeed) / DroneAcceleration;
		float accelerationDistance = startSpeed * accelerationTime + 0.5f * DroneAcceleration * accelerationTime * accelerationTime;
		float runTime = need <= accelerationDistance
			? (-startSpeed + Mathf.Sqrt(startSpeed * startSpeed + 2f * DroneAcceleration * need)) / DroneAcceleration
			: accelerationTime + (need - accelerationDistance) / DroneMaxSpeed;
		return brakeTime + runTime + 0.1f;
	}

	// Temps pour parcourir une distance depuis l'arrêt, approché
	static float TravelTime(float distance)
	{
		return distance < 6.25f ? distance / DroneAcceleration + 0.5f : 1.25f + (distance - 6.25f) / DroneMaxSpeed + 0.3f;
	}

	// Point où fuir : on sort de la zone menaçante par le chemin le plus court, en visant m_DodgeOvershoot mètres au-delà
	// du bord, sans entrer dans une autre zone qui explosera avant que le drone puisse en repartir, à l'écart des autres
	// drones rouges et sans trop s'éloigner de la cible. À défaut de point sûr (pluie de grenades, murs), le moins exposé.
	public Vector3 GetDodgePoint(GameObject drone)
	{
		Vector3 position = drone.transform.position;
		if (!TryGetThreat(drone, out GrenadeImpact threat)) return position;

		Vector3 origin = NavMesh.SamplePosition(position, out NavMeshHit self, 2f, NavMesh.AllAreas) ? self.position : position;
		ArmyElement target = CurrentTargetOf(drone);
		float wantedDistance = target == null ? 0f : (target is Turret ? m_HugDistance : GetEngageDistance(target.transform));

		Vector3 away = Vector3.ProjectOnPlane(position - threat.position, Vector3.up);
		float baseAngle = away.sqrMagnitude > 0.0001f ? Mathf.Atan2(away.x, away.z) * Mathf.Rad2Deg : Random.Range(0f, 360f);
		float radius = DangerRadius + 0.2f;

		Vector3 best = position;
		float bestScore = float.MaxValue;
		for (int k = -8; k <= 8; k++)
		{
			Vector3 direction = Quaternion.Euler(0f, baseAngle + k * 15f, 0f) * Vector3.forward;
			float exit = ExitDistance(position - threat.position, direction, radius);
			if (!NavMesh.SamplePosition(origin + direction * (exit + m_DodgeOvershoot), out NavMeshHit hit, 1f, NavMesh.AllAreas)) continue;
			Vector3 point = hit.position;
			if (NavMesh.Raycast(origin, point, out _, NavMesh.AllAreas)) continue; // mur ou bord de terrasse
			if (!IsPointSafe(point, TravelTime(exit + m_DodgeOvershoot), 1.5f, threat.position)) continue;

			float score = 3f * exit + m_EscapeCrowdPenalty * CountRedDronesNear(drone, point, m_GrenadeRadius + 0.5f);
			if (target != null) score += 0.5f * Mathf.Abs(HorizontalDistance(point, target.transform.position) - wantedDistance);
			if (score < bestScore)
			{
				bestScore = score;
				best = point;
			}
		}
		if (bestScore < float.MaxValue) return best;

		// Repli : le point le moins exposé autour du drone
		for (int ring = 1; ring <= 3; ring++)
			for (int k = 0; k < 24; k++)
			{
				Vector3 direction = Quaternion.Euler(0f, k * 15f, 0f) * Vector3.forward;
				if (!NavMesh.SamplePosition(origin + direction * (3f * ring), out NavMeshHit hit, 1f, NavMesh.AllAreas)) continue;
				bool straight = !NavMesh.Raycast(origin, hit.position, out _, NavMesh.AllAreas);
				float arrival = TravelTime(3f * ring) * (straight ? 1f : 1.6f);
				float score = 0.3f * 3f * ring + (straight ? 0f : 3f);
				foreach (GrenadeImpact impact in m_IncomingImpacts)
					if (HorizontalDistance(hit.position, impact.position) < m_GrenadeRadius + 0.5f && impact.timeToImpact < arrival + 1.5f)
						score += 10f / (impact.timeToImpact + 0.5f);
				if (score < bestScore)
				{
					bestScore = score;
					best = hit.position;
				}
			}
		return best;
	}

	// Distance à parcourir depuis "offset" (position relative au centre) dans "direction" pour sortir d'un cercle de rayon "radius"
	static float ExitDistance(Vector3 offset, Vector3 direction, float radius)
	{
		offset = Vector3.ProjectOnPlane(offset, Vector3.up);
		float b = Vector3.Dot(offset, direction);
		float c = offset.sqrMagnitude - radius * radius;
		float discriminant = b * b - c;
		return discriminant > 0f ? Mathf.Max(0f, -b + Mathf.Sqrt(discriminant)) : 0f;
	}

	// Vrai si aucune autre grenade ne tombe sur ce point avant que le drone ait pu y arriver puis en repartir
	bool IsPointSafe(Vector3 point, float arrival, float stay, Vector3 ignoredImpact)
	{
		foreach (GrenadeImpact impact in m_IncomingImpacts)
		{
			if ((impact.position - ignoredImpact).sqrMagnitude < 0.01f) continue;
			if (impact.timeToImpact < arrival + stay && HorizontalDistance(point, impact.position) < m_GrenadeRadius + 0.5f) return false;
		}
		return true;
	}

	int CountRedDronesNear(GameObject self, Vector3 point, float radius)
	{
		int count = 0;
		foreach (IArmyElement element in m_ArmyElements)
			if (element is Drone drone && drone && drone.gameObject != self
				&& HorizontalDistance(drone.transform.position, point) < radius) count++;
		return count;
	}

	string OwnTag
	{
		get
		{
			foreach (IArmyElement element in m_ArmyElements)
				if (element is Component component && component) return component.gameObject.tag;
			return null;
		}
	}

	static float HorizontalDistance(Vector3 a, Vector3 b)
	{
		return Vector3.ProjectOnPlane(a - b, Vector3.up).magnitude;
	}
}
