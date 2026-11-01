using UnityEngine;
using UnityEngine.AI;
using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;
using Tooltip = BehaviorDesigner.Runtime.Tasks.TooltipAttribute;

[TaskCategory("MyTasks/Red")]
[TaskDescription("Avance vers la cible seulement jusqu'à être à portée avec une ligne de tir dégagée")]
public class MoveToFiringRange : Action
{
	public SharedTransform target;
	[Tooltip("Distance de tir (m)")]
	public SharedFloat firingRange = FiringSolution.DefaultFiringRange;
	[Tooltip("Vitesse du drone, Annexe A (m/s)")]
	public SharedFloat speed = 10f;
	[Tooltip("Vitesse angulaire du drone, Annexe A (°/s)")]
	public SharedFloat angularSpeed = 180f;

	const float RepathPeriod = 0.25f;
	NavMeshAgent m_Agent;
	float m_RepathTimer;

	public override void OnAwake()
	{
		m_Agent = GetComponent<NavMeshAgent>();
	}

	public override void OnStart()
	{
		m_Agent.speed = speed.Value;
		m_Agent.angularSpeed = angularSpeed.Value;
		m_RepathTimer = 0f;
	}

	public override TaskStatus OnUpdate()
	{
		if (target.Value == null) return TaskStatus.Failure;
		if (IsInFiringPosition()) return TaskStatus.Success;

		m_RepathTimer -= Time.deltaTime;
		if (m_RepathTimer <= 0f)
		{
			m_Agent.isStopped = false;
			m_Agent.SetDestination(target.Value.position); // la cible bouge : on recalcule régulièrement
			m_RepathTimer = RepathPeriod;
		}
		return TaskStatus.Running;
	}

	bool IsInFiringPosition()
	{
		float distance = Vector3.Distance(transform.position, target.Value.position);
		return distance <= firingRange.Value && FiringSolution.HasClearShot(transform.position, target.Value);
	}

	public override void OnEnd()
	{
		if (m_Agent.hasPath) m_Agent.ResetPath();
		m_Agent.isStopped = true;
	}

	public override void OnReset()
	{
		target = null;
		firingRange = FiringSolution.DefaultFiringRange;
		speed = 10f;
		angularSpeed = 180f;
	}
}
