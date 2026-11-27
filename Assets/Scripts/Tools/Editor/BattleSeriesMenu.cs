using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using RedVsGreen.Tools;

namespace RedVsGreen.EditorTools
{
	// Menu RedVsGreen : lance une série de batailles mesurées (voir BattleSeriesRunner).
	// Le nom des fichiers de résultats reprend la branche et le commit Git : commite avant de mesurer.
	public static class BattleSeriesMenu
	{
		const string ScenePath = "Assets/Scenes/RedVersusGreenBattle.unity";

		[MenuItem("RedVsGreen/Série de 20 batailles (vitesse x3)", priority = 1)]
		static void Run20x3() => StartSeries(20, 3f);

		[MenuItem("RedVsGreen/Série de 50 batailles (vitesse x3)", priority = 2)]
		static void Run50x3() => StartSeries(50, 3f);

		[MenuItem("RedVsGreen/Série de 5 batailles (vitesse x3)", priority = 3)]
		static void Run5x3() => StartSeries(5, 3f);

		[MenuItem("RedVsGreen/Série de 20 batailles (vitesse normale)", priority = 4)]
		static void Run20x1() => StartSeries(20, 1f);

		[MenuItem("RedVsGreen/Arrêter la série", priority = 20)]
		static void Stop()
		{
			SessionState.SetBool(BattleSeriesKeys.Active, false);
			if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
		}

		static void StartSeries(int battles, float timeScale)
		{
			if (EditorApplication.isPlaying) { Debug.LogWarning("Arrête d'abord le mode Play."); return; }
			if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
			EditorSceneManager.OpenScene(ScenePath);

			SessionState.SetBool(BattleSeriesKeys.Active, true);
			SessionState.SetInt(BattleSeriesKeys.Battles, battles);
			SessionState.SetFloat(BattleSeriesKeys.TimeScale, timeScale);
			SessionState.SetString(BattleSeriesKeys.Label, GitLabel());
			EditorApplication.isPlaying = true;
		}

		[InitializeOnLoadMethod]
		static void ClearFlagWhenLeavingPlayMode()
		{
			EditorApplication.playModeStateChanged += state =>
			{
				if (state == PlayModeStateChange.EnteredEditMode) SessionState.SetBool(BattleSeriesKeys.Active, false);
			};
		}

		// "strategie-1143821", ou "serie" si Git est introuvable
		static string GitLabel()
		{
			try
			{
				string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
				string git = Path.Combine(root, ".git");
				string head = File.ReadAllText(Path.Combine(git, "HEAD")).Trim();
				if (!head.StartsWith("ref: ")) return "detached-" + head.Substring(0, 7);

				string refName = head.Substring(5);
				string branch = refName.Replace("refs/heads/", "").Replace('/', '-');
				string refFile = Path.Combine(git, refName);
				string hash = null;
				if (File.Exists(refFile)) hash = File.ReadAllText(refFile).Trim();
				else
				{
					string packed = Path.Combine(git, "packed-refs");
					if (File.Exists(packed))
						foreach (string line in File.ReadAllLines(packed))
							if (line.EndsWith(" " + refName)) { hash = line.Substring(0, 40); break; }
				}
				return hash != null ? branch + "-" + hash.Substring(0, 7) : branch;
			}
			catch { return "serie"; }
		}
	}
}
