using UnityEngine;
using UnityEngine.AI;
using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;

[TaskCategory("MyTasks/Red")]
[TaskDescription("Sort de la zone d'impact des grenades vertes")]
public class EvadeGrenade : Action
{
	public SharedFloat margin = 2f;        // marge de sécurité (m)
	public SharedFloat speed = 10f;        // Annexe A (m/s)
	public SharedFloat angularSpeed = 180f; // Annexe A (°/s)
	public SharedFloat timeout = 3f;       // abandon au-delà (s)

	const float RepathPeriod = 0.3f;
	NavMeshAgent m_Agent;
	IArmyElement m_ArmyElement;
	float m_Elapsed;
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
		m_RepathTimer = 0f;
	}

	public override TaskStatus OnUpdate()
	{
		ArmyManagerRed manager = m_ArmyElement.ArmyManager as ArmyManagerRed;
		if (manager == null) return TaskStatus.Failure;

		if (!manager.IsInGrenadeDanger(transform.position, margin.Value)) return TaskStatus.Success;

		m_Elapsed += Time.deltaTime;
		if (m_Elapsed > timeout.Value) return TaskStatus.Failure;

		m_RepathTimer -= Time.deltaTime;
		if (m_RepathTimer <= 0f)
		{
			m_Agent.isStopped = false;
			m_Agent.SetDestination(manager.GetEscapePoint(transform.position, margin.Value));
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
