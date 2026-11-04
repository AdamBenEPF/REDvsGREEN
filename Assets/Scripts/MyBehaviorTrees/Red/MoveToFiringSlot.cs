using UnityEngine;
using UnityEngine.AI;
using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;

[TaskCategory("MyTasks/Red")]
[TaskDescription("Rejoint la position de tir attribuée par l'état-major ; si elle est inatteignable ou si le tir n'y passe pas, fonce vers la cible jusqu'à pouvoir tirer")]
public class MoveToFiringSlot : Action
{
	public SharedTransform target;
	public SharedFloat firingRange = FiringSolution.DefaultFiringRange; // (m)
	public SharedFloat speed = 10f;         // Annexe A (m/s)
	public SharedFloat angularSpeed = 180f; // Annexe A (°/s)
	public SharedFloat timeout = 6f;        // abandon au-delà (s)

	const float RetargetDistance = 5f;  // la cible a bougé de plus de 5 m : nouvelle position (m)
	const float StuckSpeed = 0.5f;      // en dessous, le drone est considéré à l'arrêt (m/s)
	const float StuckDelay = 0.5f;      // durée à l'arrêt avant de se considérer bloqué (s)
	const float RepathPeriod = 0.25f;   // fréquence de mise à jour de la poursuite (s)
	const float ShotCheckPeriod = 0.2f; // la simulation de tir est coûteuse : on ne la refait pas à chaque image (s)

	NavMeshAgent m_Agent;
	IArmyElement m_ArmyElement;
	ArmyManagerRed m_Manager;
	Vector3 m_Slot;
	Vector3 m_TargetPosAtSlot;
	float m_Elapsed;
	float m_StuckTime;
	float m_RepathTimer;
	float m_ShotCheckTimer;
	bool m_CanShootCached;
	bool m_Chasing;

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
		m_ShotCheckTimer = 0f;
		m_Chasing = false;
		m_Manager = m_ArmyElement.ArmyManager as ArmyManagerRed;
		if (m_Manager != null && target.Value != null) ComputeSlot();
	}

	void ComputeSlot()
	{
		m_Slot = m_Manager.GetFiringPosition(gameObject, target.Value);
		m_TargetPosAtSlot = target.Value.position;
		m_Agent.isStopped = false;
		m_Agent.SetDestination(m_Slot);
	}

	public override TaskStatus OnUpdate()
	{
		if (m_Manager == null || target.Value == null) return TaskStatus.Failure;

		m_Elapsed += Time.deltaTime;
		if (m_Elapsed > timeout.Value) return TaskStatus.Failure;

		bool canShoot = CanShoot();

		if (m_Chasing)
		{
			// Poursuite directe : on tire dès que c'est possible
			if (canShoot) return TaskStatus.Success;
			m_RepathTimer -= Time.deltaTime;
			if (m_RepathTimer <= 0f)
			{
				m_Agent.isStopped = false;
				m_Agent.SetDestination(target.Value.position);
				m_RepathTimer = RepathPeriod;
			}
			return TaskStatus.Running;
		}

		if (Vector3.Distance(target.Value.position, m_TargetPosAtSlot) > RetargetDistance) ComputeSlot();
		if (m_Agent.pathPending) return TaskStatus.Running;

		if (HasReachedOrIsStuck())
		{
			if (canShoot) return TaskStatus.Success;
			m_Chasing = true; // position inatteignable ou tir impossible d'ici : on fonce vers la cible
		}
		return TaskStatus.Running;
	}

	bool CanShoot()
	{
		m_ShotCheckTimer -= Time.deltaTime;
		if (m_ShotCheckTimer > 0f) return m_CanShootCached;
		m_ShotCheckTimer = ShotCheckPeriod;

		float distance = Vector3.Distance(transform.position, target.Value.position);
		m_CanShootCached = distance <= firingRange.Value && FiringSolution.CanHit(transform.position, target.Value);
		return m_CanShootCached;
	}

	bool HasReachedOrIsStuck()
	{
		if (m_Agent.remainingDistance <= m_Agent.stoppingDistance + 0.5f) return true;

		// Destination inatteignable (terrasse, obstacle) : l'agent s'arrête au plus près sans jamais "arriver"
		if (m_Agent.velocity.magnitude < StuckSpeed) m_StuckTime += Time.deltaTime;
		else m_StuckTime = 0f;
		return m_StuckTime > StuckDelay;
	}

	public override void OnEnd()
	{
		if (m_Agent == null || !m_Agent.isActiveAndEnabled || !m_Agent.isOnNavMesh) return; // drone en cours de destruction
		if (m_Agent.hasPath) m_Agent.ResetPath();
		m_Agent.isStopped = true;
	}
}
