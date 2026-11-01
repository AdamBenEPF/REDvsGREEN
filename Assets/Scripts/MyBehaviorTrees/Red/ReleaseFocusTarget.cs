using BehaviorDesigner.Runtime.Tasks;

[TaskCategory("MyTasks/Red")]
[TaskDescription("Signale à l'état-major que le drone a tiré et n'est plus engagé sur sa cible")]
public class ReleaseFocusTarget : Action
{
	IArmyElement m_ArmyElement;

	public override void OnAwake()
	{
		m_ArmyElement = (IArmyElement)GetComponent(typeof(IArmyElement));
	}

	public override TaskStatus OnUpdate()
	{
		(m_ArmyElement.ArmyManager as ArmyManagerRed)?.ReleaseFocusTarget(gameObject);
		return TaskStatus.Success;
	}
}
