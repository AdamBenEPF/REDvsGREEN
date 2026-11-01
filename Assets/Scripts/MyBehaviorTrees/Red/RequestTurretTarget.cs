using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;

[TaskCategory("MyTasks/Red")]
[TaskDescription("Demande à l'état-major la cible de la tourelle : défense de nos tourelles, sinon artillerie verte")]
public class RequestTurretTarget : Action
{
	public SharedTransform target;
	public SharedFloat minRadius = 5f;   // Annexe A
	public SharedFloat maxRadius = 50f;  // Annexe A

	IArmyElement m_ArmyElement;

	public override void OnAwake()
	{
		m_ArmyElement = (IArmyElement)GetComponent(typeof(IArmyElement));
	}

	public override TaskStatus OnUpdate()
	{
		ArmyManagerRed manager = m_ArmyElement.ArmyManager as ArmyManagerRed;
		if (manager == null) return TaskStatus.Running; // état-major pas encore injecté

		target.Value = manager.GetTurretTarget(gameObject, minRadius.Value, maxRadius.Value);
		return target.Value != null ? TaskStatus.Success : TaskStatus.Failure;
	}
}
