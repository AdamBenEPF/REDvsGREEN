using UnityEngine;
using UnityEngine.AI;
using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;

[TaskCategory("MyTasks/Red")]
[TaskDescription("Fonce vers la cible et s'arrête dès qu'il est assez près pour tirer (3 m d'une tourelle, 14 m d'un drone)")]
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

	NavMeshAgent m_Agent;
	IArmyElement m_ArmyElement;
	ArmyManagerRed m_Manager;
	float m_Elapsed;
	float m_StuckTime;
	float m_RepathTimer;

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
	}

	public override TaskStatus OnUpdate()
	{
		if (m_Manager == null || target.Value == null) return TaskStatus.Failure;

		m_Elapsed += Time.deltaTime;
		if (m_Elapsed > timeout.Value) return TaskStatus.Failure;

		float wanted = Mathf.Min(m_Manager.GetEngageDistance(target.Value), firingRange.Value);
		float distance = Vector3.ProjectOnPlane(target.Value.position - transform.position, Vector3.up).magnitude;
		bool isTurret = target.Value.GetComponent<Turret>() != null;
		bool clearShot = isTurret || FiringSolution.HasClearShot(transform.position, target.Value);

		// Assez près : on s'arrête et on tire
		if (distance <= wanted && clearShot) return TaskStatus.Success;

		// Bloqué (autre drone, relief) presque à la bonne distance : on tire quand même
		if (m_Agent.velocity.magnitude < StuckSpeed) m_StuckTime += Time.deltaTime;
		else m_StuckTime = 0f;
		if (m_StuckTime > StuckDelay && distance <= wanted + StuckTolerance && clearShot) return TaskStatus.Success;

		m_RepathTimer -= Time.deltaTime;
		if (m_RepathTimer <= 0f)
		{
			m_Agent.isStopped = false;
			m_Agent.SetDestination(target.Value.position);
			m_RepathTimer = RepathPeriod;
		}
		return TaskStatus.Running;
	}

	public override void OnEnd()
	{
		if (m_Agent == null || !m_Agent.isActiveAndEnabled || !m_Agent.isOnNavMesh) return; // drone en cours de destruction
		if (m_Agent.hasPath) m_Agent.ResetPath();
		m_Agent.isStopped = true;
	}
}
