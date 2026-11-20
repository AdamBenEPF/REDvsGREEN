using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using RedVsGreen.Tools;
using UnityEngine.AI;

namespace RedVsGreen.EditorTools
{
	// Menu RedVsGreen > Exporter la carte : écrit Results/map.json à partir de la scène de bataille.
	// Contenu : hauteur et pente du terrain sur une grille de 0,5 m, surface la plus haute (caisses, murs) avec le nom
	// de l'objet touché, NavMesh complet, obstacles, et position de départ de chaque unité.
	public static class MapExporter
	{
		const string ScenePath = "Assets/Scenes/RedVersusGreenBattle.unity";
		const float Step = 0.5f;
		static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

		[MenuItem("RedVsGreen/Exporter la carte (map.json)", priority = 40)]
		static void Export()
		{
			if (EditorApplication.isPlaying) { Debug.LogWarning("Arrête d'abord le mode Play."); return; }
			if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
			EditorSceneManager.OpenScene(ScenePath);
			Physics.SyncTransforms();

			int terrainLayer = LayerMask.NameToLayer("Terrain");
			int terrainMask = 1 << terrainLayer;

			var terrainColliders = Object.FindObjectsByType<Collider>(FindObjectsSortMode.None).Where(c => c.gameObject.layer == terrainLayer).ToList();
			if (terrainColliders.Count == 0) { Debug.LogError("Aucun collider sur le layer Terrain."); return; }
			Bounds b = terrainColliders[0].bounds;
			foreach (var c in terrainColliders) b.Encapsulate(c.bounds);

			int nx = Mathf.FloorToInt(b.size.x / Step) + 1;
			int nz = Mathf.FloorToInt(b.size.z / Step) + 1;

			var sb = new StringBuilder();
			sb.Append("{\n");
			sb.Append($"\"step\":{F(Step)},\"xMin\":{F(b.min.x)},\"zMin\":{F(b.min.z)},\"nx\":{nx},\"nz\":{nz},\n");

			// Grille : hauteur du terrain, normale Y (1 = plat), hauteur de la surface la plus haute, index de l'objet le plus haut
			var names = new System.Collections.Generic.List<string>();
			var height = new StringBuilder(); var slope = new StringBuilder(); var top = new StringBuilder(); var topObj = new StringBuilder();
			for (int iz = 0; iz < nz; iz++)
				for (int ix = 0; ix < nx; ix++)
				{
					Vector3 p = new Vector3(b.min.x + ix * Step, b.max.y + 50f, b.min.z + iz * Step);
					string sep = (ix == 0 && iz == 0) ? "" : ",";
					if (Physics.Raycast(p, Vector3.down, out RaycastHit hit, 500f, terrainMask, QueryTriggerInteraction.Ignore))
					{
						height.Append(sep).Append(F(hit.point.y));
						slope.Append(sep).Append(hit.normal.y.ToString("0.00", Inv));
					}
					else { height.Append(sep).Append("null"); slope.Append(sep).Append("null"); }

					// surface la plus haute, toutes couches confondues, sans les unités
					var hits = Physics.RaycastAll(p, Vector3.down, 500f, ~0, QueryTriggerInteraction.Ignore)
						.Where(h => h.collider.GetComponentInParent<ArmyElement>() == null)
						.OrderBy(h => h.distance).ToList();
					if (hits.Count > 0)
					{
						string n = hits[0].collider.name;
						int idx = names.IndexOf(n);
						if (idx < 0) { names.Add(n); idx = names.Count - 1; }
						top.Append(sep).Append(F(hits[0].point.y));
						topObj.Append(sep).Append(idx);
					}
					else { top.Append(sep).Append("null"); topObj.Append(sep).Append(-1); }
				}
			sb.Append("\"terrainHeight\":[").Append(height).Append("],\n");
			sb.Append("\"terrainNormalY\":[").Append(slope).Append("],\n");
			sb.Append("\"topHeight\":[").Append(top).Append("],\n");
			sb.Append("\"topObject\":[").Append(topObj).Append("],\n");
			sb.Append("\"topObjectNames\":[").Append(string.Join(",", names.Select(Q))).Append("],\n");

			// Obstacles : tous les colliders hors terrain et hors unités
			sb.Append("\"obstacles\":[");
			bool first = true;
			foreach (var c in Object.FindObjectsByType<Collider>(FindObjectsSortMode.None))
			{
				if (c.gameObject.layer == terrainLayer || c.GetComponentInParent<ArmyElement>() != null) continue;
				sb.Append(first ? "\n" : ",\n"); first = false;
				sb.Append($"{{\"name\":{Q(c.name)},\"tag\":{Q(c.tag)},\"trigger\":{(c.isTrigger ? "true" : "false")},\"center\":{V(c.bounds.center)},\"size\":{V(c.bounds.size)}}}");
			}
			sb.Append("],\n");

			// Unités
			sb.Append("\"units\":[");
			first = true;
			foreach (var ae in Object.FindObjectsByType<ArmyElement>(FindObjectsSortMode.None).OrderBy(a => a.name))
			{
				var col = ae.GetComponentInChildren<Collider>();
				var agent = ae.GetComponent<NavMeshAgent>();
				sb.Append(first ? "\n" : ",\n"); first = false;
				sb.Append($"{{\"name\":{Q(ae.name)},\"tag\":{Q(ae.tag)},\"type\":{Q(ae.GetType().Name)},\"pos\":{V(ae.transform.position)},\"yaw\":{F(ae.transform.eulerAngles.y)}");
				if (col) sb.Append($",\"colliderCenter\":{V(col.bounds.center)},\"colliderSize\":{V(col.bounds.size)}");
				if (agent) sb.Append($",\"agentRadius\":{F(agent.radius)},\"agentHeight\":{F(agent.height)},\"agentBaseOffset\":{F(agent.baseOffset)}");
				sb.Append("}");
			}
			sb.Append("],\n");

			// NavMesh
			var tri = NavMesh.CalculateTriangulation();
			sb.Append("\"navVertices\":[").Append(string.Join(",", tri.vertices.Select(v => V(v)))).Append("],\n");
			sb.Append("\"navIndices\":[").Append(string.Join(",", tri.indices)).Append("],\n");
			sb.Append("\"navAreas\":[").Append(string.Join(",", tri.areas)).Append("]\n");
			sb.Append("}\n");

			string dir = Path.Combine(Application.dataPath, "..", "Results");
			Directory.CreateDirectory(dir);
			string path = Path.Combine(dir, "map.json");
			File.WriteAllText(path, sb.ToString());
			Debug.Log($"[Carte] {nx}x{nz} points, {tri.indices.Length / 3} triangles de NavMesh → {Path.GetFullPath(path)}");
		}

		static string F(float v) => v.ToString("0.00", Inv);
		static string V(Vector3 v) => $"[{F(v.x)},{F(v.y)},{F(v.z)}]";
		static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
	}
}
