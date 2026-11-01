using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;

[TaskCategory("MyTasks/Red")]
[TaskDescription("Vrai si une grenade verte en vol va tomber près du drone")]
public class IsInGrenadeDanger : Conditional
{
	public SharedFloat margin = 2f; // marge de sécurité au-delà du rayon de 5 m (m)

	IArmyElement m_ArmyElement;

	public override void OnAwake()
	{
		m_ArmyElement = (IArmyElement)GetComponent(typeof(IArmyElement));
	}

	public override TaskStatus OnUpdate()
	{
		ArmyManagerRed manager = m_ArmyElement.ArmyManager as ArmyManagerRed;
		if (manager == null) return TaskStatus.Failure;
		return manager.IsInGrenadeDanger(transform.position, margin.Value) ? TaskStatus.Success : TaskStatus.Failure;
	}
}
