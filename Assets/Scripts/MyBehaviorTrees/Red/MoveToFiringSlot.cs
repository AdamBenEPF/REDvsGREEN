using UnityEngine;
using UnityEngine.AI;
using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;

[TaskCategory("MyTasks/Red")]
[TaskDescription("Fonce vers la cible et s'arrête dès qu'il est assez près pour tirer (3 m d'une tourelle, 14 m d'un drone). "
	+ "Sous le feu de l'artillerie verte, va à une nouvelle position de tir, loin de la précédente")]
public class MoveToFiringSlot : Action
{
	public SharedTransform target;
	public SharedFloat firingRange = FiringSolution.DefaultFiringRange; // distance de tir maximale (m)
	public SharedFloat speed = 10f;         // Annexe A (m/s)
	public SharedFloat angularSpeed = 180f; // Annexe A (°/s)
	public SharedFloat timeout = 6f;        // abandon au-delà (s)

	const float RepathPeriod = 0.25f;  // la cible peut bouger : on met à jour la destination (s)
	const float StuckSpeed = 0.5f;     // en dessous, le drone est considéré à l'arrêt (m/s)
	const float StuckDelay = 0.5f;     // durée à l'arrêt avant de se considérer bloqué (s)
	const float StuckTolerance = 3f;   // bloqué à moins de 3 m de la distance voulue : on tire quand même (m)
	const float SpotTolerance = 1f;    // arrivé à moins de 1 m de la position de tir (m)
	const float SniperTimeoutFactor = 3f; // tir d'élite : le point peut être de l'autre côté de la carte

	NavMeshAgent m_Agent;
	IArmyElement m_ArmyElement;
	ArmyManagerRed m_Manager;
	float m_Elapsed;
	float m_StuckTime;
	float m_RepathTimer;
	bool m_HasSpot;
	Vector3 m_Spot;

	public override void OnAwake()
	{
		m_Agent = GetComponent<NavMeshAgent>();
		m_ArmyElement = (IArmyElement)GetComponent(typeof(IArmyElement));
	}

	public override void OnStart()
	{
		m_Agent.speed = speed.Value;
		m_Agent.angularSpeed = angularSpeed.Value;
		m_Elapsed = 0f;
		m_StuckTime = 0f;
		m_RepathTimer = 0f;
		m_Manager = m_ArmyElement.ArmyManager as ArmyManagerRed;
		m_HasSpot = false;
	}

	public override TaskStatus OnUpdate()
	{
		if (m_Manager == null || target.Value == null) return TaskStatus.Failure;

		m_Elapsed += Time.deltaTime;

		// Fin de bataille : rejoindre le point de tir d'élite (la traversée de la carte peut dépasser le timeout normal)
		if (m_Manager.TryGetSniperSpot(target.Value, out Vector3 sniperSpot))
			return m_Elapsed > SniperTimeoutFactor * timeout.Value ? TaskStatus.Failure : MoveToSniperSpot(sniperSpot);

		if (m_Elapsed > timeout.Value) return TaskStatus.Failure;

		float wanted = Mathf.Min(m_Manager.GetEngageDistance(target.Value, transform.position), firingRange.Value);
		float distance = Vector3.ProjectOnPlane(target.Value.position - transform.position, Vector3.up).magnitude;
		bool isTurret = target.Value.GetComponent<Turret>() != null;
		bool clearShot = isTurret || FiringSolution.HasClearShot(transform.position, target.Value);

		if (m_Agent.velocity.magnitude < StuckSpeed) m_StuckTime += Time.deltaTime;
		else m_StuckTime = 0f;

		m_RepathTimer -= Time.deltaTime;
		if (m_RepathTimer <= 0f)
		{
			// Sous le feu de l'artillerie : nouvelle position de tir (null si le drone n'est pas exposé)
			m_HasSpot = m_Manager.TryGetRepositionSpot(gameObject, target.Value, out m_Spot);
			m_Agent.isStopped = false;
			m_Agent.SetDestination(m_HasSpot ? m_Spot : target.Value.position);
			m_RepathTimer = RepathPeriod;
		}

		if (m_HasSpot)
		{
			float toSpot = Vector3.ProjectOnPlane(m_Spot - transform.position, Vector3.up).magnitude;
			bool arrived = toSpot <= SpotTolerance || (m_StuckTime > StuckDelay && toSpot <= StuckTolerance);
			if (arrived && distance <= wanted + StuckTolerance && clearShot) return Arrive();
			return TaskStatus.Running;
		}

		// Assez près : on s'arrête et on tire
		if (distance <= wanted && clearShot) return Arrive();

		// Bloqué (autre drone, relief) presque à la bonne distance : on tire quand même
		if (m_StuckTime > StuckDelay && distance <= wanted + StuckTolerance && clearShot) return Arrive();

		return TaskStatus.Running;
	}

	TaskStatus MoveToSniperSpot(Vector3 spot)
	{
		if (m_Agent.velocity.magnitude < StuckSpeed) m_StuckTime += Time.deltaTime;
		else m_StuckTime = 0f;

		float toSpot = Vector3.ProjectOnPlane(spot - transform.position, Vector3.up).magnitude;
		if (toSpot <= SpotTolerance || (m_StuckTime > StuckDelay && toSpot <= StuckTolerance)) return Arrive();

		m_RepathTimer -= Time.deltaTime;
		if (m_RepathTimer <= 0f)
		{
			m_Agent.isStopped = false;
			m_Agent.SetDestination(spot);
			m_RepathTimer = RepathPeriod;
		}
		return TaskStatus.Running;
	}

	TaskStatus Arrive()
	{
		m_Manager.RecordFiringPosition(gameObject, transform.position);
		return TaskStatus.Success;
	}

	public override void OnEnd()
	{
		if (m_Agent == null || !m_Agent.isActiveAndEnabled || !m_Agent.isOnNavMesh) return; // drone en cours de destruction
		if (m_Agent.hasPath) m_Agent.ResetPath();
		m_Agent.isStopped = true;
	}
}
