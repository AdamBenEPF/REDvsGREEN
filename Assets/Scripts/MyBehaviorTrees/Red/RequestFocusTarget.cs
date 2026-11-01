using UnityEngine;
using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;

[TaskCategory("MyTasks/Red")]
[TaskDescription("Demande à l'état-major rouge la cible du focus fire")]
public class RequestFocusTarget : Action
{
	public SharedTransform target;
	IArmyElement m_ArmyElement;

	public override void OnAwake()
	{
		m_ArmyElement = (IArmyElement)GetComponent(typeof(IArmyElement));
	}

	public override TaskStatus OnUpdate()
	{
		ArmyManagerRed manager = m_ArmyElement.ArmyManager as ArmyManagerRed;
		if (manager == null) return TaskStatus.Running; // état-major pas encore injecté

		target.Value = manager.GetFocusTarget(gameObject);
		return target.Value != null ? TaskStatus.Success : TaskStatus.Failure;
	}
}
