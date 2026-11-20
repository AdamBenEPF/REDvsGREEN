using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// Radar à grenades : l'état-major voit les grenades vertes en vol et calcule leur point d'impact
public partial class ArmyManagerRed
{
	[Header("Esquive des grenades")]
	[Tooltip("Durée de vol d'une grenade (Annexe A : Rocket Travel Duration)")]
	[SerializeField] float m_GrenadeTravelDuration = 4f;
	[Tooltip("Distance maximale d'une esquive (m) : au-delà, le drone n'aurait pas le temps d'y arriver")]
	[SerializeField] float m_MaxEscapeDistance = 10f;
	[Tooltip("Distance à la tourelle verte visée après une esquive (m), sous ses 5 m de rayon minimal de tir")]
	[SerializeField] float m_EscapeHugDistance = 4f;
	[Tooltip("Pénalité par mètre d'éloignement de la cible lors d'une esquive")]
	[SerializeField] float m_EscapeTargetWeight = 2f;
	[Tooltip("Pénalité par drone rouge à portée de la même grenade (m équivalents)")]
	[SerializeField] float m_EscapeCrowdPenalty = 3f;

	struct GrenadeImpact
	{
		public Vector3 position;
		public float timeToImpact;
	}

	readonly Dictionary<Rocket, float> m_GrenadeFirstSeen = new Dictionary<Rocket, float>();
	readonly List<GrenadeImpact> m_IncomingImpacts = new List<GrenadeImpact>();
	readonly List<Rocket> m_ForgottenGrenades = new List<Rocket>();

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

	public bool IsInGrenadeDanger(Vector3 position, float margin)
	{
		foreach (GrenadeImpact impact in m_IncomingImpacts)
			if (HorizontalDistance(position, impact.position) < m_GrenadeRadius + margin) return true;
		return false;
	}

	// Point où se mettre à l'abri : hors de portée de toutes les grenades en vol, joignable en ligne droite,
	// le plus proche possible, sans s'éloigner de la cible du drone et sans se coller aux autres drones rouges.
	// Un drone collé à une tourelle verte passe de préférence de l'autre côté de celle-ci : il reste dans son angle mort.
	public Vector3 GetEscapePoint(GameObject drone, float margin)
	{
		Vector3 position = drone.transform.position;
		ArmyElement target = CurrentTargetOf(drone);
		bool hugging = target is Turret;
		float wantedDistance = target == null ? 0f : (hugging ? m_EscapeHugDistance : GetEngageDistance(target.transform));

		// Le drone vole 1 m au-dessus du NavMesh (Base Offset) : on part de sa projection sur le NavMesh
		Vector3 origin = NavMesh.SamplePosition(position, out NavMeshHit self, 2f, NavMesh.AllAreas) ? self.position : position;
		bool requireStraightPath = true;
		Vector3 best = position;
		float bestScore = float.MaxValue;

		void Consider(Vector3 candidate)
		{
			if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, 1.5f, NavMesh.AllAreas)) return;
			Vector3 point = hit.position;
			if (IsInGrenadeDanger(point, margin)) return;

			float travel = HorizontalDistance(position, point);
			if (travel > m_MaxEscapeDistance) return;
			if (requireStraightPath && NavMesh.Raycast(origin, point, out _, NavMesh.AllAreas)) return; // mur ou bord de terrasse sur le chemin

			float score = travel;
			if (target != null)
				score += m_EscapeTargetWeight * Mathf.Max(0f, HorizontalDistance(point, target.transform.position) - wantedDistance);
			score += m_EscapeCrowdPenalty * CountRedDronesNear(drone, point, m_GrenadeRadius + 0.5f);

			if (score < bestScore)
			{
				bestScore = score;
				best = point;
			}
		}

		float ring = m_GrenadeRadius + margin + 1.5f;
		for (int pass = 0; pass < 2 && bestScore == float.MaxValue; pass++)
		{
			requireStraightPath = pass == 0; // 2e passage, si rien n'est joignable en ligne droite : on accepte un détour
			for (int i = 0; i < 16; i++)
			{
				Vector3 direction = Quaternion.Euler(0f, i * 22.5f, 0f) * Vector3.forward;
				Consider(origin + direction * ring);
				Consider(origin + direction * (ring + 2f));
				if (hugging) Consider(target.transform.position + direction * m_EscapeHugDistance);
			}
		}
		return best;
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
