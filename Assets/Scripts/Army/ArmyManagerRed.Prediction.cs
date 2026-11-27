using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

// Tir de nos tourelles sur la position FUTURE des groupes de drones verts.
// Une grenade tombe 4 s après le tir. Un drone vert se déplace en ligne droite à vitesse connue (Annexe A), et s'arrête
// à 6 m (Arrive Distance de My Seek) de l'unité rouge qu'il attaque. On prolonge donc sa trajectoire observée :
//  - si une de nos unités se trouve sur cette ligne, devant lui, il s'arrêtera à 6 m d'elle ;
//  - sinon, il continue en ligne droite.
// On ne lit pas la destination du drone vert (ce serait connaître son intention) : on la déduit de sa trajectoire.
// Les groupes sont ensuite formés sur ces positions prévues, et le point visé suit le centre prévu du groupe jusqu'au tir.
public partial class ArmyManagerRed
{
	[Header("Prédiction des groupes pour nos tourelles")]
	[Tooltip("Temps moyen de rotation de nos tourelles avant le tir (s), ajouté aux 4 s de vol au moment du choix du groupe")]
	[SerializeField] float m_TurretRotationEstimate = 1f;
	[Tooltip("Écart latéral maximal entre la trajectoire d'un drone vert et une de nos unités pour la considérer comme sa cible (m)")]
	[SerializeField] float m_TrajectoryTargetTolerance = 2f;
	[Tooltip("Arrive Distance de My Seek, Annexe A (m)")]
	[SerializeField] float m_GreenArriveDistance = 6f;
	[Tooltip("Durée pendant laquelle un drone vert arrêté reste sur place : rotation + tir + Wait de 1 à 2 s (s)")]
	[SerializeField] float m_GreenParkedDuration = 2.5f;

	// Position prévue d'un drone vert dans "horizon" secondes, et confiance dans cette prévision (0 à 1)
	Vector3 PredictGreenDrone(ArmyElement drone, float horizon, out float confidence)
	{
		Vector3 position = drone.transform.position;
		NavMeshAgent agent = drone.GetComponent<NavMeshAgent>();
		Vector3 velocity = agent ? Vector3.ProjectOnPlane(agent.velocity, Vector3.up) : Vector3.zero;
		float speed = velocity.magnitude;

		// Arrêté : il tire puis repart vers une nouvelle cible, on ne sait pas où
		if (speed < m_ParkedSpeed)
		{
			confidence = horizon <= m_GreenParkedDuration ? 1f : 0.4f;
			return position;
		}

		Vector3 direction = velocity / speed;
		float maxSpeed = agent ? agent.speed : 10f;
		float travel = maxSpeed * horizon; // il accélère jusqu'à sa vitesse maximale (8 m/s², atteinte en ~1 s)

		// Cible probable : la première de nos unités sur sa trajectoire, devant lui
		float stopAt = float.MaxValue;
		foreach (IArmyElement element in m_ArmyElements)
		{
			if (!(element is ArmyElement ours) || ours == null) continue;
			Vector3 toUnit = Vector3.ProjectOnPlane(ours.transform.position - position, Vector3.up);
			float along = Vector3.Dot(toUnit, direction);
			if (along <= 0f) continue;
			float lateral = (toUnit - along * direction).magnitude;
			if (lateral > m_TrajectoryTargetTolerance) continue;
			stopAt = Mathf.Min(stopAt, Mathf.Max(0f, along - m_GreenArriveDistance));
		}

		if (stopAt < float.MaxValue)
		{
			float timeToStop = stopAt / Mathf.Max(speed, 1f);
			if (travel >= stopAt)
			{
				// Arrivé avant l'impact : il y sera encore s'il n'a pas eu le temps de tirer et de repartir
				confidence = horizon - timeToStop <= m_GreenParkedDuration ? 1f : 0.4f;
				return position + direction * stopAt;
			}
			confidence = 0.8f;
			return position + direction * travel;
		}

		// Pas de cible identifiée : il continue tout droit, prévision moins sûre
		confidence = 0.5f;
		Vector3 predicted = position + direction * travel;
		return NavMesh.SamplePosition(predicted, out NavMeshHit hit, 3f, NavMesh.AllAreas) ? hit.position : predicted;
	}

	// Meilleur groupe de drones verts à l'arrivée d'une grenade tirée par cette tourelle
	Transform GetPredictedClusterTarget(GameObject requester, float minRadius, float maxRadius)
	{
		Vector3 from = requester.transform.position;
		float horizon = m_GrenadeTravelDuration + m_TurretRotationEstimate;
		float hitRadius = m_GrenadeRadius + 0.4f - 0.5f; // rayon de la grenade + rayon du drone - marge d'erreur

		var predictions = new List<(Drone drone, Vector3 pos, float confidence)>();
		foreach (Drone drone in GetAllEnemiesOfType<Drone>(false))
		{
			if (drone.Health <= 0) continue;
			Vector3 p = PredictGreenDrone(drone, horizon, out float confidence);
			predictions.Add((drone, p, confidence));
		}

		List<ArmyElement> bestMembers = null;
		float bestScore = 0f;
		foreach (var seed in predictions)
		{
			if (!IsWithinRadius(from, seed.pos, minRadius, maxRadius)) continue;

			// Centre du groupe autour de ce drone, puis membres effectivement dans le rayon de la grenade autour du centre
			Vector3 center = Centroid(predictions.Where(o => HorizontalDistance(o.pos, seed.pos) <= hitRadius).Select(o => o.pos));
			var members = predictions.Where(o => HorizontalDistance(o.pos, center) <= hitRadius).ToList();
			float score = members.Sum(o => o.confidence * PendingDamageFactor(requester, o.drone));
			if (score > bestScore)
			{
				bestScore = score;
				bestMembers = members.Select(o => (ArmyElement)o.drone).ToList();
			}
		}
		return bestMembers != null ? AimAtCluster(requester, bestMembers) : null;
	}

	// Un drone vert déjà visé par une autre de nos tourelles compte moins : il risque d'être mort ou blessé à l'arrivée
	float PendingDamageFactor(GameObject requester, ArmyElement drone)
	{
		int aimedBy = m_TurretAims.Count(kv => kv.Key != requester && kv.Value.members.Contains(drone));
		float remaining = drone.Health - aimedBy * m_GrenadeDamage;
		return remaining > 0f ? 1f : 0.3f;
	}

	// Point visé : centre prévu du groupe à l'arrivée de la grenade (mis à jour à chaque image jusqu'au tir)
	Vector3 PredictedClusterCenter(List<ArmyElement> members)
	{
		return Centroid(members.Select(member => PredictGreenDrone(member, m_GrenadeTravelDuration, out _)));
	}
}
