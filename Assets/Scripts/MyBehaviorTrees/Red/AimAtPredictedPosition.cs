using UnityEngine;
using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;
using Tooltip = BehaviorDesigner.Runtime.Tasks.TooltipAttribute;

[TaskCategory("MyTasks/Red")]
[TaskDescription("Oriente le drone pour que ses missiles atteignent la position future de la cible (tir anticipé, compensation du relief)")]
public class AimAtPredictedPosition : Action
{
	public SharedTransform target;
	[Tooltip("Coefficient de slerp, Annexe A")]
	public SharedFloat slerpCoef = 4f;
	[Tooltip("Angle d'arrivée, Annexe A (°)")]
	public SharedFloat arrivalAngle = 5f;
	[Tooltip("Abandon de la visée au-delà de cette durée (s)")]
	public SharedFloat timeout = 1.5f;

	const float AimRefreshPeriod = 0.1f; // recalcul du cap (s)
	const float AlignedAngle = 0.5f;     // écart de cap toléré (°)

	float m_Elapsed;
	float m_RefreshTimer;
	float m_AimYaw;
	float m_WantedYaw;

	public override void OnStart()
	{
		m_Elapsed = 0f;
		m_RefreshTimer = 0f;
	}

	public override TaskStatus OnUpdate()
	{
		if (target.Value == null) return TaskStatus.Failure;
		m_Elapsed += Time.deltaTime;

		m_RefreshTimer -= Time.deltaTime;
		if (m_RefreshTimer <= 0f)
		{
			RefreshAim();
			m_RefreshTimer = AimRefreshPeriod;
		}

		if (Mathf.Abs(Mathf.DeltaAngle(transform.eulerAngles.y, m_AimYaw)) < AlignedAngle) return TaskStatus.Success;

		transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.Euler(0f, m_WantedYaw, 0f), Time.deltaTime * slerpCoef.Value);
		if (Mathf.Abs(Mathf.DeltaAngle(transform.eulerAngles.y, m_WantedYaw)) < arrivalAngle.Value) return TaskStatus.Success;

		return m_Elapsed > timeout.Value ? TaskStatus.Failure : TaskStatus.Running;
	}

	void RefreshAim()
	{
		Vector3 aimPoint = FiringSolution.PredictAimPoint(transform.position, target.Value);
		m_AimYaw = FiringSolution.ComputeAimYaw(transform.position, aimPoint, FiringSolution.GetTargetRadius(target.Value), out _);

		// La rotation s'arrête dès qu'on est à moins de 5° (Annexe A) du cap demandé, soit jusqu'à 5° d'erreur.
		// On demande donc un cap décalé de 4,5° au-delà de la cible, dans le sens de rotation :
		// le drone s'arrête à 0,5° près du bon cap.
		float delta = Mathf.DeltaAngle(transform.eulerAngles.y, m_AimYaw);
		m_WantedYaw = m_AimYaw + Mathf.Sign(delta) * (arrivalAngle.Value - AlignedAngle);
	}

	public override void OnReset()
	{
		target = null;
		slerpCoef = 4f;
		arrivalAngle = 5f;
		timeout = 1.5f;
	}
}
