using UnityEngine;
using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;
using Tooltip = BehaviorDesigner.Runtime.Tasks.TooltipAttribute;

[TaskCategory("MyTasks/Red")]
[TaskDescription("Oriente le drone vers la position future de la cible (tir anticipé)")]
public class AimAtPredictedPosition : Action
{
	public SharedTransform target;
	[Tooltip("Coefficient de slerp, Annexe A")]
	public SharedFloat slerpCoef = 4f;
	[Tooltip("Angle d'arrivée, Annexe A (°)")]
	public SharedFloat arrivalAngle = 5f;
	[Tooltip("Abandon de la visée au-delà de cette durée (s)")]
	public SharedFloat timeout = 1.5f;

	float m_Elapsed;

	public override void OnStart()
	{
		m_Elapsed = 0f;
	}

	public override TaskStatus OnUpdate()
	{
		if (target.Value == null) return TaskStatus.Failure;
		m_Elapsed += Time.deltaTime;

		Vector3 aimPoint = FiringSolution.PredictAimPoint(transform.position, target.Value);
		Vector3 direction = Vector3.ProjectOnPlane(aimPoint - transform.position, Vector3.up);
		if (direction.sqrMagnitude < 0.0001f) return TaskStatus.Success;

		Quaternion wanted = Quaternion.LookRotation(direction.normalized);
		transform.rotation = Quaternion.Slerp(transform.rotation, wanted, Time.deltaTime * slerpCoef.Value);

		if (Quaternion.Angle(transform.rotation, wanted) < arrivalAngle.Value) return TaskStatus.Success;
		return m_Elapsed > timeout.Value ? TaskStatus.Failure : TaskStatus.Running;
	}

	public override void OnReset()
	{
		target = null;
		slerpCoef = 4f;
		arrivalAngle = 5f;
		timeout = 1.5f;
	}
}
