using UnityEngine;
using UnityEngine.AI;

// Calculs de tir partagés par l'état-major et les tâches des drones rouges
public static class FiringSolution
{
	public const float DefaultFiringRange = 30f;   // portée réelle du missile : ~55 m, on garde de la marge

	const float MissileStartSpeed = 10f;   // vitesse du drone transmise au missile (Drone.Shoot)
	const float MissileMaxSpeed = 20f;     // Annexe A
	const float MissileAcceleration = 40f; // Annexe A

	// Temps mis par un missile pour parcourir une distance horizontale
	public static float FlightTime(float distance)
	{
		float accelTime = (MissileMaxSpeed - MissileStartSpeed) / MissileAcceleration;
		float accelDistance = (MissileStartSpeed + MissileMaxSpeed) * 0.5f * accelTime;
		if (distance <= accelDistance)
			return (-MissileStartSpeed + Mathf.Sqrt(MissileStartSpeed * MissileStartSpeed + 2f * MissileAcceleration * distance)) / MissileAcceleration;
		return accelTime + (distance - accelDistance) / MissileMaxSpeed;
	}

	// Position de la cible au moment où le missile l'atteindra (vitesse supposée constante)
	public static Vector3 PredictAimPoint(Vector3 shooterPos, Transform target)
	{
		Vector3 velocity = GetVelocity(target);
		Vector3 aimPoint = target.position;
		for (int i = 0; i < 3; i++)
		{
			float distance = Vector3.ProjectOnPlane(aimPoint - shooterPos, Vector3.up).magnitude;
			aimPoint = target.position + velocity * FlightTime(distance);
		}
		return aimPoint;
	}

	public static Vector3 GetVelocity(Transform target)
	{
		NavMeshAgent agent = target.GetComponent<NavMeshAgent>();
		return agent ? agent.velocity : Vector3.zero;
	}

	// Vrai si aucun obstacle ne coupe la ligne de tir (le terrain et les unités ne comptent pas)
	public static bool HasClearShot(Vector3 from, Transform target)
	{
		Vector3 toTarget = target.position - from;
		float distance = toTarget.magnitude;
		if (distance < 0.01f) return true;

		RaycastHit[] hits = Physics.RaycastAll(from, toTarget / distance, distance, ~0, QueryTriggerInteraction.Ignore);
		foreach (RaycastHit hit in hits)
		{
			Transform hitTransform = hit.collider.transform;
			if (hitTransform == target || hitTransform.IsChildOf(target)) continue;
			if (hitTransform.CompareTag("Terrain")) continue;
			if (hit.collider.GetComponentInParent<ArmyElement>() != null) continue;
			return false; // caisse ou autre obstacle
		}
		return true;
	}
}
