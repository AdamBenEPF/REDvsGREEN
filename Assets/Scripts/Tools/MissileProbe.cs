using UnityEngine;

namespace RedVsGreen.Tools
{
	// Ajoutée à chaque missile par BattleSeriesRunner pendant une série de mesures.
	// Elle observe sans rien changer : le missile garde son propre comportement (Missile.cs).
	public class MissileProbe : MonoBehaviour
	{
		BattleSeriesRunner m_Runner;
		int m_Run;
		string m_ShooterTag;
		Vector3 m_SpawnPos;
		bool m_Resolved;

		public void Init(BattleSeriesRunner runner)
		{
			m_Runner = runner;
			m_Run = runner.RunIndex;
			m_ShooterTag = gameObject.tag;
			m_SpawnPos = transform.position;
			runner.ReportMissileFired(m_ShooterTag);
		}

		bool Valid => m_Runner != null && m_Runner.RunIndex == m_Run;

		// Même règle que Missile.OnTriggerEnter : le premier collider d'un autre tag fait exploser le missile
		void OnTriggerEnter(Collider other)
		{
			if (m_Resolved || other.CompareTag(m_ShooterTag)) return;
			m_Resolved = true;
			if (!Valid) return;

			Vector3 d = transform.position - m_SpawnPos;
			d.y = 0;
			m_Runner.ReportMissileHit(m_ShooterTag, other, d.magnitude);
		}

		void OnDestroy()
		{
			if (m_Resolved || !Valid) return;
			m_Resolved = true;
			m_Runner.ReportMissileExpired(m_ShooterTag);
		}
	}
}
