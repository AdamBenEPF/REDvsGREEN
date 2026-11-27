using UnityEngine;
using UnityEngine.AI;
using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;
using Tooltip = BehaviorDesigner.Runtime.Tasks.TooltipAttribute;

// Déplacement continu autour de la cible pendant la visée, le tir et le Wait (dans un Parallel Complete).
// Une grenade verte tombe 4 s après le tir, là où était le drone au moment du tir : un drone qui ne s'arrête jamais
// n'y est plus. Les missiles verts, tirés vers la position actuelle, ratent aussi plus souvent une cible en mouvement.
// Le corps du drone reste libre de s'orienter vers la cible (AimAtPredictedPosition) : le NavMeshAgent ne le tourne plus.
// Ne se termine jamais : c'est l'autre branche du Parallel Complete (visée et tir, ou Wait) qui termine la boucle.
[TaskCategory("MyTasks/Red")]
[TaskDescription("Tourne autour de la cible, par bonds d'au moins 6 m, hors des zones d'impact des grenades, pendant la visée, le tir et le Wait")]
public class StrafeAroundTarget : Action
{
	public SharedTransform target;
	[Tooltip("Vitesse, Annexe A (m/s)")]
	public SharedFloat speed = 10f;

	const float ArrivalDistance = 1f;  // point de passage atteint (m)
	const float RecheckPeriod = 0.25f; // vérification du point de passage (s)

	NavMeshAgent m_Agent;
	IArmyElement m_ArmyElement;
	bool m_HasWaypoint;
	Vector3 m_Waypoint;
	float m_RecheckTimer;

	public override void OnAwake()
	{
		m_Agent = GetComponent<NavMeshAgent>();
		m_ArmyElement = (IArmyElement)GetComponent(typeof(IArmyElement));
	}

	public override void OnStart()
	{
		m_HasWaypoint = false;
		m_RecheckTimer = 0f;
		if (!AgentUsable()) return;
		m_Agent.speed = speed.Value;
		m_Agent.updateRotation = false;
	}

	public override TaskStatus OnUpdate()
	{
		ArmyManagerRed manager = m_ArmyElement.ArmyManager as ArmyManagerRed;
		if (manager == null || target.Value == null || !manager.ShouldStrafe(target.Value) || !AgentUsable())
		{
			Stop();
			return TaskStatus.Running;
		}

		m_RecheckTimer -= Time.deltaTime;
		bool reached = m_HasWaypoint && Vector3.ProjectOnPlane(m_Waypoint - transform.position, Vector3.up).magnitude <= ArrivalDistance;
		if (!m_HasWaypoint || reached || m_RecheckTimer <= 0f)
		{
			m_RecheckTimer = RecheckPeriod;
			// On garde le point de passage tant qu'il reste sûr ; sinon, ou une fois atteint, on en prend un nouveau
			if (!m_HasWaypoint || reached || !manager.IsStrafePointStillValid(gameObject, target.Value, m_Waypoint))
			{
				m_HasWaypoint = manager.TryGetStrafePoint(gameObject, target.Value, out m_Waypoint);
				if (m_HasWaypoint)
				{
					m_Agent.isStopped = false;
					m_Agent.SetDestination(m_Waypoint);
				}
				else Stop();
			}
		}
		return TaskStatus.Running;
	}

	public override void OnEnd()
	{
		if (!AgentUsable()) return; // drone en cours de destruction
		m_Agent.updateRotation = true;
		Stop();
	}

	bool AgentUsable() => m_Agent != null && m_Agent.isActiveAndEnabled && m_Agent.isOnNavMesh;

	void Stop()
	{
		m_HasWaypoint = false;
		if (!AgentUsable()) return;
		if (m_Agent.hasPath) m_Agent.ResetPath();
		m_Agent.isStopped = true;
	}

	public override void OnReset()
	{
		target = null;
		speed = 10f;
	}
}
