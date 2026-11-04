using UnityEngine;
using UnityEngine.AI;

// Calculs de tir partagés par l'état-major et les tâches des drones rouges.
// Un missile ne vole pas en ligne droite : il suit le relief à hauteur constante (Missile.FixedUpdate),
// se redresse selon la pente, donc dévie sur les terrains inclinés, et s'écrase contre les murs des terrasses.
// On rejoue donc son vol pas à pas avant de tirer, et on corrige le cap du drone en conséquence.
public static class FiringSolution
{
	public const float DefaultFiringRange = 30f;

	const float MissileStartSpeed = 10f;   // vitesse du drone transmise au missile (Drone.Shoot)
	const float MissileMaxSpeed = 20f;     // Annexe A
	const float MissileAcceleration = 40f; // Annexe A
	const float MissileLifeDuration = 3f;  // Annexe A
	const float MissileSpread = 0.3f;      // les 2 missiles partent à ±0,34 m de l'axe : l'un des deux touche si l'écart est en dessous (m)
	const float MaxClimbSlope = 1f;        // montée plus raide que 45° : le missile percute le mur de la terrasse
	const int AimIterations = 3;

	struct MissileFlight
	{
		public bool crashed;
		public float signedMiss;     // écart latéral au plus près de la cible (> 0 : cible à droite) (m)
		public float rangeAtClosest; // distance parcourue jusqu'à ce point (m)
	}

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

	// Rayon horizontal du collider de la cible : 0,4 m pour un drone, 1 m pour une tourelle
	public static float GetTargetRadius(Transform target)
	{
		Collider collider = target.GetComponent<Collider>();
		return collider ? Mathf.Min(collider.bounds.extents.x, collider.bounds.extents.z) : 0.5f;
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

	// Vrai si un missile tiré depuis "from" peut atteindre la cible : obstacles, murs de terrasse, déviation sur les pentes
	public static bool CanHit(Vector3 from, Transform target)
	{
		if (!HasClearShot(from, target)) return false;
		ComputeAimYaw(from, PredictAimPoint(from, target), GetTargetRadius(target), out bool willHit);
		return willHit;
	}

	// Cap (°) à donner au drone pour que le missile, dévié par le relief, passe sur le point visé
	public static float ComputeAimYaw(Vector3 from, Vector3 aimPoint, float targetRadius, out bool willHit)
	{
		Vector3 flat = Vector3.ProjectOnPlane(aimPoint - from, Vector3.up);
		float yaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
		willHit = false;

		for (int i = 0; i < AimIterations; i++)
		{
			MissileFlight flight = SimulateMissile(from, yaw, aimPoint);
			if (flight.crashed) return yaw;
			if (Mathf.Abs(flight.signedMiss) <= targetRadius + MissileSpread)
			{
				willHit = true;
				return yaw;
			}
			if (flight.rangeAtClosest < 0.5f) return yaw;
			yaw += Mathf.Atan2(flight.signedMiss, flight.rangeAtClosest) * Mathf.Rad2Deg; // correction de l'écart
		}
		return yaw;
	}

	// Rejoue pas à pas le vol d'un missile, avec le même calcul que Missile.FixedUpdate
	static MissileFlight SimulateMissile(Vector3 spawnPos, float yawDegrees, Vector3 aimPoint)
	{
		MissileFlight flight = new MissileFlight { crashed = true, signedMiss = float.MaxValue, rangeAtClosest = 0f };
		TerrainManager terrain = TerrainManager.Instance;
		Vector3 ground = Vector3.zero, normal = Vector3.zero;
		if (terrain == null || !terrain.GetVerticallyAlignedPositionOnTerrain(spawnPos, ref ground, ref normal)) return flight;
		float heightFromGround = Vector3.Distance(ground, spawnPos);

		Vector3 position = spawnPos;
		Quaternion rotation = Quaternion.Euler(0f, yawDegrees, 0f);
		float speed = MissileStartSpeed;
		float dt = Time.fixedDeltaTime;
		float closest = float.MaxValue;
		float traveled = 0f;

		for (float t = 0f; t < MissileLifeDuration; t += dt)
		{
			// --- même calcul que Missile.FixedUpdate ---
			speed = Mathf.Min(MissileMaxSpeed, speed + dt * MissileAcceleration);
			float step = speed * dt;
			Vector3 next = position + step * (rotation * Vector3.forward);
			if (!terrain.GetVerticallyAlignedPositionOnTerrain(next, ref next, ref normal)) break; // hors du terrain : missile détruit
			next += Vector3.up * heightFromGround;

			Vector3 move = next - position;
			if (move.sqrMagnitude > 0f)
			{
				float horizontal = new Vector2(move.x, move.z).magnitude;
				if (move.y > MaxClimbSlope * horizontal + 0.05f)
				{
					flight.crashed = true; // mur de terrasse
					return flight;
				}
				Vector3 displacement = move.normalized * step;
				position += displacement;
				traveled += new Vector2(displacement.x, displacement.z).magnitude;
			}
			Quaternion upright = Quaternion.FromToRotation(rotation * Vector3.up, normal);
			rotation = Quaternion.Slerp(rotation, upright * rotation, dt);

			// --- écart à la cible ---
			Vector3 toTarget = Vector3.ProjectOnPlane(aimPoint - position, Vector3.up);
			float distance = toTarget.magnitude;
			if (distance < closest)
			{
				closest = distance;
				Vector3 heading = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
				flight.crashed = false;
				flight.signedMiss = Vector3.Cross(heading, toTarget).y >= 0f ? distance : -distance;
				flight.rangeAtClosest = traveled;
			}
			else if (distance > closest + 1f) break; // la cible est dépassée
		}
		return flight;
	}
}
