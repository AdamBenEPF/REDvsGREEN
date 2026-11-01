using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class ArmyManagerRed : ArmyManager
{
	[Header("Focus fire")]
	[Tooltip("Dégâts attendus d'une salve de drone : 2 missiles x 5 pts, moins les ratés")]
	[SerializeField] float m_ExpectedDamagePerDrone = 7f;
	[Tooltip("Poids de la distance dans le choix de la cible (points de vie équivalents par mètre)")]
	[SerializeField] float m_DistanceWeight = 1f;

	// Registre des engagements : quel drone rouge tire sur quelle unité verte
	readonly Dictionary<GameObject, ArmyElement> m_Assignments = new Dictionary<GameObject, ArmyElement>();

	// ---------- Focus fire ----------

	public Transform GetFocusTarget(GameObject requester)
	{
		CleanAssignments();
		m_Assignments.Remove(requester); // le demandeur ne compte pas dans les engagements en cours

		Vector3 from = requester.transform.position;
		ArmyElement target = ChooseTarget(GetAllEnemiesOfType<Drone>(false), from);
		if (target == null) target = ChooseTarget(GetAllEnemiesOfType<Turret>(false), from);
		if (target == null) return null;

		m_Assignments[requester] = target;
		return target.transform;
	}

	public void ReleaseFocusTarget(GameObject requester)
	{
		m_Assignments.Remove(requester);
	}

	ArmyElement ChooseTarget<T>(List<T> enemies, Vector3 from) where T : ArmyElement
	{
		ArmyElement best = null;
		bool bestIsSaturated = true;
		float bestScore = float.MaxValue;

		foreach (T enemy in enemies)
		{
			if (enemy.Health <= 0) continue; // déjà en train de mourir

			float remaining = enemy.Health - CommittedDamageOn(enemy);
			bool saturated = remaining <= 0;
			// Cible non saturée : on privilégie la plus entamée et la plus proche.
			// Cible saturée : on minimise les dégâts gaspillés.
			float score = Mathf.Abs(remaining) + m_DistanceWeight * Vector3.Distance(from, enemy.transform.position);

			bool isBetter = (bestIsSaturated && !saturated) || (saturated == bestIsSaturated && score < bestScore);
			if (isBetter)
			{
				best = enemy;
				bestIsSaturated = saturated;
				bestScore = score;
			}
		}
		return best;
	}

	float CommittedDamageOn(ArmyElement enemy)
	{
		int nDrones = m_Assignments.Values.Count(target => target == enemy);
		return nDrones * m_ExpectedDamagePerDrone;
	}

	void CleanAssignments()
	{
		var deadKeys = m_Assignments.Where(kv => kv.Key == null || kv.Value == null).Select(kv => kv.Key).ToList();
		foreach (var key in deadKeys) m_Assignments.Remove(key);
	}

	// ---------- Statistiques (code d'origine du squelette) ----------

	public override void ArmyElementHasBeenKilled(GameObject go)
	{
		m_Assignments.Remove(go);
		base.ArmyElementHasBeenKilled(go);
		if (m_ArmyElements.Count == 0)
		{
			GUIUtility.systemCopyBuffer = "0\t" + ((int)Timer.Value).ToString() + "\t0\t0\t0";
		}
	}

	public void GreenArmyIsDead(string deadArmyTag)
	{
		int nDrones = 0, nTurrets = 0, health = 0;
		ComputeStatistics(ref nDrones, ref nTurrets, ref health);
		GUIUtility.systemCopyBuffer = "1\t" + ((int)Timer.Value).ToString() + "\t" + nDrones.ToString() + "\t" + nTurrets.ToString() + "\t" + health.ToString();

		RefreshHudDisplay(); // dernière mise à jour en cas de victoire
	}
}
