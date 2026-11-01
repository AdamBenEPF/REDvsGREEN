using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// Radar à grenades : l'état-major voit les grenades vertes en vol et calcule leur point d'impact
public partial class ArmyManagerRed
{
	[Header("Esquive des grenades")]
	[Tooltip("Durée de vol d'une grenade (Annexe A : Rocket Travel Duration)")]
	[SerializeField] float m_GrenadeTravelDuration = 4f;

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

	// Point sûr le plus proche, à l'opposé des impacts et à l'écart des autres drones rouges
	public Vector3 GetEscapePoint(Vector3 position, float margin)
	{
		Vector3 away = Vector3.zero;
		foreach (GrenadeImpact impact in m_IncomingImpacts)
		{
			Vector3 offset = Vector3.ProjectOnPlane(position - impact.position, Vector3.up);
			if (offset.magnitude < m_GrenadeRadius + margin)
				away += offset.sqrMagnitude > 0.01f ? offset.normalized : Vector3.forward;
		}
		away = away.normalized;

		float escapeDistance = m_GrenadeRadius + margin + 2f;
		Vector3 best = position;
		float bestScore = float.MaxValue;
		for (int i = 0; i < 12; i++)
		{
			Vector3 direction = Quaternion.Euler(0f, i * 30f, 0f) * Vector3.forward;
			if (!NavMesh.SamplePosition(position + direction * escapeDistance, out NavMeshHit hit, 2f, NavMesh.AllAreas)) continue;
			if (IsInGrenadeDanger(hit.position, margin)) continue;

			float score = -Vector3.Dot(direction, away) + (IsTooCloseToOtherDrones(null, hit.position) ? 1f : 0f);
			if (score < bestScore)
			{
				bestScore = score;
				best = hit.position;
			}
		}
		return best;
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
