using BehaviorDesigner.Runtime.Tasks;

// Condition de la branche Esquive (abort Lower Priority) : vraie au dernier moment, quand une grenade verte va tomber
// sur le drone et qu'il lui reste juste le temps de sortir de la zone. Avant cela, le drone continue de viser et de tirer.
[TaskCategory("MyTasks/Red")]
[TaskDescription("Vrai quand il faut partir maintenant pour éviter une grenade verte (esquive juste à temps)")]
public class IsInGrenadeDanger : Conditional
{
	IArmyElement m_ArmyElement;

	public override void OnAwake()
	{
		m_ArmyElement = (IArmyElement)GetComponent(typeof(IArmyElement));
	}

	public override TaskStatus OnUpdate()
	{
		ArmyManagerRed manager = m_ArmyElement.ArmyManager as ArmyManagerRed;
		if (manager == null) return TaskStatus.Failure;
		return manager.ShouldDodge(gameObject) ? TaskStatus.Success : TaskStatus.Failure;
	}
}
