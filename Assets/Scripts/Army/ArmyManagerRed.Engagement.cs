using UnityEngine;

public partial class ArmyManagerRed
{
	// Distance à laquelle un drone rouge s'arrête pour tirer :
	// collé à une tourelle (dans son angle mort sous 5 m), à distance de tir d'un drone
	public float GetEngageDistance(Transform target)
	{
		return target.GetComponent<Turret>() != null ? m_HugDistance : m_FiringDistance;
	}
}
