using UnityEngine;
using UnityEngine.AI;
using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;

[TaskCategory("MyTasks/Red")]
[TaskDescription("Rejoint le poste de garde attribué par l'état-major devant nos tourelles")]
public class ReturnToGuardPost : Action
{
	public SharedFloat speed = 10f;         // Annexe A (m/s)
	public SharedFloat angularSpeed = 180f; // Annexe A (°/s)
	public SharedFloat timeout = 5f;        // au-delà, on rend la main pour réévaluer la situation (s)

	const float StuckSpeed = 0.5f; // en dessous, le drone est considéré à l'arrêt (m/s)
	const float StuckDelay = 0.5f; // durée à l'arrêt avant de se considérer bloqué (s)

	NavMeshAgent m_Agent;
	IArmyElement m_ArmyElement;
	float m_Elapsed;
	float m_StuckTime;

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

		ArmyManagerRed manager = m_ArmyElement.ArmyManager as ArmyManagerRed;
		if (manager == null) return;
		m_Agent.isStopped = false;
		m_Agent.SetDestination(manager.GetGuardPost(gameObject));
	}

	public override TaskStatus OnUpdate()
	{
		if (!(m_ArmyElement.ArmyManager is ArmyManagerRed)) return TaskStatus.Failure;

		m_Elapsed += Time.deltaTime;
		if (m_Elapsed > timeout.Value) return TaskStatus.Success;
		if (m_Agent.pathPending) return TaskStatus.Running;
		if (m_Agent.remainingDistance <= m_Agent.stoppingDistance + 0.5f) return TaskStatus.Success;

		if (m_Agent.velocity.magnitude < StuckSpeed) m_StuckTime += Time.deltaTime;
		else m_StuckTime = 0f;
		return m_StuckTime > StuckDelay ? TaskStatus.Success : TaskStatus.Running;
	}

	public override void OnEnd()
	{
		if (m_Agent == null || !m_Agent.isActiveAndEnabled || !m_Agent.isOnNavMesh) return; // drone en cours de destruction
		if (m_Agent.hasPath) m_Agent.ResetPath();
		m_Agent.isStopped = true;
	}
}
