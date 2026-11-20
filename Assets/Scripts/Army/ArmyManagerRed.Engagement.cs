using UnityEngine;

// Distance à laquelle un drone rouge s'arrête pour tirer
public partial class ArmyManagerRed
{
	[Header("Distances d'engagement")]
	[Tooltip("Distance de tir sur un drone vert (m)")]
	[SerializeField] float m_FiringDistance = 14f;
	[Tooltip("Distance à laquelle nos drones se collent à une tourelle verte (m). Sous 5 m, elle ne peut pas les viser (Min Radius, Annexe A)")]
	[SerializeField] float m_HugDistance = 3f;

	// Collé à une tourelle (dans son angle mort sous 5 m), à distance de tir d'un drone
	public float GetEngageDistance(Transform target)
	{
		return target.GetComponent<Turret>() != null ? m_HugDistance : m_FiringDistance;
	}
}
