using UnityEngine;

namespace RedVsGreen.Tools
{
	// Ajoutée à chaque grenade par BattleSeriesRunner pendant une série de mesures, pour compter les tirs.
	// Les dégâts des grenades sont mesurés à partir de la santé des unités.
	public class RocketProbe : MonoBehaviour
	{
		public void Init(BattleSeriesRunner runner)
		{
			runner.ReportGrenadeFired(gameObject.tag);
		}
	}
}
