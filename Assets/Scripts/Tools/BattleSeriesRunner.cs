using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace RedVsGreen.Tools
{
	// Enchaîne automatiquement une série de batailles et écrit les résultats dans le dossier Results/ à la racine du projet.
	// Se lance depuis le menu RedVsGreen (voir Editor/BattleSeriesMenu.cs). Aucun effet sur le jeu quand aucune série n'est active.
	//
	// Fichiers produits (tabulation, point décimal) :
	//   <label>_battles.tsv   : format officiel de l'Annexe B (deadlocks et timeouts exclus)
	//   <label>_telemetry.tsv : une ligne par bataille avec le détail des tirs, dégâts, morts, déplacements
	//   <label>_events.tsv    : chaque mort d'unité (temps, camp, cause, position) et les obstacles touchés par les missiles
	public class BattleSeriesRunner : MonoBehaviour
	{
		public const string RedTag = "1";
		public const string GreenTag = "2";

		public enum Cause { None, Missile, Grenade }

		public class UnitTrack
		{
			public ArmyElement element;
			public string name;
			public bool isRed;
			public bool isDrone;
			public NavMeshAgent agent;
			public float lastHealth;
			public int pendingMissileHits;
			public Cause lastCause;
			public bool dead;
			public Vector3 lastPos;
		}

		public class SideStats
		{
			public int missiles, grenades;
			public int hitDrone, hitTurret, hitFriendly, hitTerrain, hitObstacle, hitMissile, expired;
			public float hitDistanceSum;
			public float dmgByMissile, dmgByGrenade;                // dégâts infligés à l'ennemi
			public int killDroneMsl, killDroneGren, killTurretMsl, killTurretGren;
			public float droneMovingTime, droneAliveTime;
			public float firstTurretLost = -1, allTurretsLost = -1;
			public Dictionary<string, int> obstacleHits = new Dictionary<string, int>();
		}

		enum State { WaitingStart, Running, Ending, Loading }

		static BattleSeriesRunner s_Instance;
		public static BattleSeriesRunner Instance => s_Instance;

		// Réglages de la série (remplis par Configure)
		int m_TargetBattles = 20;
		List<KeyValuePair<string, string>> m_Variants = new List<KeyValuePair<string, string>>(); // (nom, réglages)
		readonly Dictionary<string, List<float[]>> m_VariantResults = new Dictionary<string, List<float[]>>();
		float m_TimeScale = 3f;
		float m_TimeoutSeconds = 400f;
		string m_Label = "serie";

		// Avancement
		int m_RunIndex = 0;          // toutes les batailles lancées
		int m_ValidIndex = 0;        // batailles comptées (hors deadlock / timeout)
		int m_Victories = 0;
		State m_State = State.WaitingStart;
		float m_StateTime;
		float m_DeadlockSince = -1;
		float m_EndRealTime;

		// Bataille en cours
		readonly Dictionary<ArmyElement, UnitTrack> m_Units = new Dictionary<ArmyElement, UnitTrack>();
		SideStats m_Red, m_Green;
		readonly StringBuilder m_EventLines = new StringBuilder();

		string m_BattlesPath, m_TelemetryPath, m_EventsPath;
		static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

		public int RunIndex => m_RunIndex;
		public bool IsCounting => m_State == State.Running;

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
		static void ResetStatics() { s_Instance = null; }

#if UNITY_EDITOR
		// Démarre la série si elle a été demandée depuis le menu RedVsGreen
		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
		static void Bootstrap()
		{
			if (!UnityEditor.SessionState.GetBool(BattleSeriesKeys.Active, false)) return;
			Create(UnityEditor.SessionState.GetInt(BattleSeriesKeys.Battles, 20),
				UnityEditor.SessionState.GetFloat(BattleSeriesKeys.TimeScale, 3f),
				UnityEditor.SessionState.GetString(BattleSeriesKeys.Label, "serie"),
				UnityEditor.SessionState.GetString(BattleSeriesKeys.Variants, ""));
		}
#endif

		// battles : nombre de batailles par variante.
		// variants : "nom:champ=valeur,champ=valeur|nom2:..." ; chaque variante change des champs de ArmyManagerRed.
		// Les variantes sont jouées à tour de rôle. Vide : une seule variante, les réglages de la scène.
		public static BattleSeriesRunner Create(int battles, float timeScale, string label, string variants = "")
		{
			if (s_Instance != null) return s_Instance;
			var go = new GameObject("[BattleSeriesRunner]");
			DontDestroyOnLoad(go);
			s_Instance = go.AddComponent<BattleSeriesRunner>();
			s_Instance.Configure(battles, timeScale, label, variants);
			return s_Instance;
		}

		void Configure(int battles, float timeScale, string label, string variants)
		{
			foreach (string v in (variants ?? "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
			{
				int colon = v.IndexOf(':');
				m_Variants.Add(colon < 0 ? new KeyValuePair<string, string>(v.Trim(), "")
					: new KeyValuePair<string, string>(v.Substring(0, colon).Trim(), v.Substring(colon + 1).Trim()));
			}
			if (m_Variants.Count == 0) m_Variants.Add(new KeyValuePair<string, string>("defaut", ""));
			m_TargetBattles = Mathf.Max(1, battles) * m_Variants.Count;
			m_TimeScale = Mathf.Clamp(timeScale, 1f, 8f);
			m_Label = label;

			string dir = Path.Combine(Application.dataPath, "..", "Results");
			Directory.CreateDirectory(dir);
			string baseName = Path.Combine(dir, label + "_" + DateTime.Now.ToString("yyyyMMdd-HHmm", Inv));
			m_BattlesPath = baseName + "_battles.tsv";
			m_TelemetryPath = baseName + "_telemetry.tsv";
			m_EventsPath = baseName + "_events.tsv";

			File.WriteAllText(m_BattlesPath, "INDEX_BATTLE\tIS_VICTORY\tDURATION\tALIVE_DRONES\tALIVE_TURRETS\tREMAINING_CUMULATED_HEALTH_POINTS\n");
			File.WriteAllText(m_TelemetryPath, TelemetryHeader() + "\n");
			File.WriteAllText(m_EventsPath, "RUN\tTIME\tEVENT\tSIDE\tUNIT\tCAUSE\tX\tZ\tDETAIL\n");

			Application.runInBackground = true;
			SceneManager.sceneLoaded += OnSceneLoaded;
			BeginBattle();
			Debug.Log($"[Série] {m_TargetBattles} batailles, vitesse x{m_TimeScale}, résultats dans {dir}");
		}

		void OnDestroy()
		{
			SceneManager.sceneLoaded -= OnSceneLoaded;
			Time.timeScale = 1f;
			if (s_Instance == this) s_Instance = null;
		}

		void OnSceneLoaded(Scene scene, LoadSceneMode mode) { BeginBattle(); }

		void BeginBattle()
		{
			m_Units.Clear();
			m_Red = new SideStats();
			m_Green = new SideStats();
			m_EventLines.Clear();
			m_State = State.WaitingStart;
			m_StateTime = 0;
			m_DeadlockSince = -1;
			Time.timeScale = m_TimeScale;
			ApplyVariant(CurrentVariant);
		}

		KeyValuePair<string, string> CurrentVariant => m_Variants[m_RunIndex % m_Variants.Count];

		// Change les champs de ArmyManagerRed avant son Start : "m_HunterCount=3,m_EvadeMode=All"
		static void ApplyVariant(KeyValuePair<string, string> variant)
		{
			if (string.IsNullOrEmpty(variant.Value)) return;
			var manager = FindFirstObjectByType<ArmyManagerRed>();
			if (manager == null) { Debug.LogError("[Série] ArmyManagerRed introuvable"); return; }

			foreach (string assignment in variant.Value.Split(','))
			{
				string[] parts = assignment.Split('=');
				if (parts.Length != 2) { Debug.LogError($"[Série] réglage illisible : {assignment}"); continue; }
				string name = parts[0].Trim(), value = parts[1].Trim();

				FieldInfo field = null;
				for (Type t = manager.GetType(); t != null && field == null; t = t.BaseType)
					field = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (field == null) { Debug.LogError($"[Série] champ inconnu dans ArmyManagerRed : {name}"); continue; }

				Type ft = field.FieldType;
				object parsed;
				if (ft.IsEnum) parsed = Enum.Parse(ft, value);
				else if (ft == typeof(bool)) parsed = bool.Parse(value);
				else if (ft == typeof(int)) parsed = int.Parse(value, Inv);
				else if (ft == typeof(float)) parsed = float.Parse(value, Inv);
				else if (ft == typeof(string[])) parsed = value.Split(';').Select(x => x.Trim()).ToArray();
				else { Debug.LogError($"[Série] type non géré pour {name} : {ft.Name}"); continue; }
				field.SetValue(manager, parsed);
			}
		}

		// ===================================================================
		// Boucle
		// ===================================================================

		void LateUpdate()
		{
			switch (m_State)
			{
				case State.WaitingStart:
					m_StateTime += Time.deltaTime;
					if (m_StateTime > 0.2f) RegisterUnits();
					break;

				case State.Running:
					AttachProbes();
					PollUnits();
					CheckEnd();
					break;

				case State.Ending:
					// petite pause en temps réel pour laisser les derniers objets se détruire
					if (Time.realtimeSinceStartup - m_EndRealTime > 0.5f) NextBattleOrStop();
					break;
			}
		}

		void RegisterUnits()
		{
			foreach (var ae in FindObjectsByType<ArmyElement>(FindObjectsSortMode.None))
			{
				bool isRed = ae.CompareTag(RedTag);
				if (!isRed && !ae.CompareTag(GreenTag)) continue;
				m_Units[ae] = new UnitTrack
				{
					element = ae,
					name = ae.name,
					isRed = isRed,
					isDrone = ae is Drone,
					agent = ae.GetComponent<NavMeshAgent>(),
					lastHealth = ae.Health,
					lastPos = ae.transform.position,
				};
			}
			m_State = State.Running;
		}

		void AttachProbes()
		{
			foreach (var m in FindObjectsByType<Missile>(FindObjectsSortMode.None))
				if (!m.GetComponent<MissileProbe>()) m.gameObject.AddComponent<MissileProbe>().Init(this);

			foreach (var r in FindObjectsByType<Rocket>(FindObjectsSortMode.None))
				if (!r.GetComponent<RocketProbe>()) r.gameObject.AddComponent<RocketProbe>().Init(this);
		}

		void PollUnits()
		{
			float t = Timer.Instance ? Timer.Value : Time.timeSinceLevelLoad;
			float dt = Time.deltaTime;

			foreach (var u in m_Units.Values)
			{
				if (u.dead) continue;

				bool exists = u.element != null;
				float h = exists ? u.element.Health : 0f;
				if (exists) u.lastPos = u.element.transform.position;

				float delta = u.lastHealth - h;
				if (delta > 0.001f)
				{
					float mslDmg = Mathf.Min(delta, 5f * u.pendingMissileHits);
					float grenDmg = delta - mslDmg;
					SideStats attacker = u.isRed ? m_Green : m_Red;
					attacker.dmgByMissile += mslDmg;
					attacker.dmgByGrenade += grenDmg;
					u.lastCause = grenDmg > 0.001f ? Cause.Grenade : Cause.Missile;
				}
				u.pendingMissileHits = 0;
				u.lastHealth = h;

				if (h <= 0f) { RecordDeath(u, t); continue; }

				if (u.isDrone)
				{
					SideStats own = u.isRed ? m_Red : m_Green;
					own.droneAliveTime += dt;
					if (u.agent && u.agent.velocity.magnitude > 0.5f) own.droneMovingTime += dt;
				}
			}
		}

		void RecordDeath(UnitTrack u, float t)
		{
			u.dead = true;
			SideStats attacker = u.isRed ? m_Green : m_Red;
			if (u.isDrone)
			{
				if (u.lastCause == Cause.Grenade) attacker.killDroneGren++; else attacker.killDroneMsl++;
			}
			else
			{
				if (u.lastCause == Cause.Grenade) attacker.killTurretGren++; else attacker.killTurretMsl++;
				SideStats own = u.isRed ? m_Red : m_Green;
				if (own.firstTurretLost < 0) own.firstTurretLost = t;
				bool anyLeft = m_Units.Values.Any(o => !o.dead && !o.isDrone && o.isRed == u.isRed);
				if (!anyLeft) own.allTurretsLost = t;
			}
			AddEvent(t, "DEATH", u.isRed ? "RED" : "GREEN", u.name, u.lastCause.ToString(), u.lastPos, u.isDrone ? "drone" : "turret");
		}

		void CheckEnd()
		{
			float t = Timer.Instance ? Timer.Value : Time.timeSinceLevelLoad;
			var alive = m_Units.Values.Where(u => !u.dead && u.element != null).ToList();
			int red = alive.Count(u => u.isRed), green = alive.Count(u => !u.isRed);

			if (green == 0 && red > 0) { EndBattle("VICTORY", t); return; }
			if (red == 0) { EndBattle("DEFEAT", t); return; }
			if (t > m_TimeoutSeconds) { EndBattle("TIMEOUT", t); return; }

			bool onlyTurrets = alive.All(u => !u.isDrone);
			bool inRange = onlyTurrets && alive.Where(u => u.isRed).Any(r => alive.Where(g => !g.isRed).Any(g =>
			{
				float d = Vector3.Distance(r.element.transform.position, g.element.transform.position);
				return d > 5f && d < 50f;
			}));
			if (onlyTurrets && !inRange)
			{
				if (m_DeadlockSince < 0) m_DeadlockSince = t;
				else if (t - m_DeadlockSince > 3f) EndBattle("DEADLOCK", t);
			}
			else m_DeadlockSince = -1;
		}

		// ===================================================================
		// Rapports des sondes
		// ===================================================================

		public void ReportMissileFired(string shooterTag)
		{
			if (!IsCounting) return;
			Side(shooterTag).missiles++;
		}

		public void ReportGrenadeFired(string shooterTag)
		{
			if (!IsCounting) return;
			Side(shooterTag).grenades++;
		}

		public void ReportMissileHit(string shooterTag, Collider other, float flightDistance)
		{
			if (!IsCounting) return;
			SideStats s = Side(shooterTag);

			ArmyElement ae = other.GetComponentInParent<ArmyElement>();
			if (ae != null)
			{
				if (ae.CompareTag(shooterTag)) { s.hitFriendly++; return; }
				if (ae is Drone) s.hitDrone++; else s.hitTurret++;
				s.hitDistanceSum += flightDistance;
				if (m_Units.TryGetValue(ae, out var u)) u.pendingMissileHits++;
				return;
			}
			if (other.GetComponentInParent<Missile>() || other.GetComponentInParent<Rocket>()) { s.hitMissile++; return; }
			if (other.CompareTag("Terrain") || other.gameObject.layer == LayerMask.NameToLayer("Terrain")) { s.hitTerrain++; return; }

			s.hitObstacle++;
			string key = other.name;
			s.obstacleHits[key] = s.obstacleHits.TryGetValue(key, out int n) ? n + 1 : 1;
		}

		public void ReportMissileExpired(string shooterTag)
		{
			if (!IsCounting) return;
			Side(shooterTag).expired++;
		}

		SideStats Side(string tag) => tag == RedTag ? m_Red : m_Green;

		// ===================================================================
		// Fin de bataille et écriture
		// ===================================================================

		void EndBattle(string result, float t)
		{
			m_State = State.Ending;
			m_EndRealTime = Time.realtimeSinceStartup;

			var alive = m_Units.Values.Where(u => !u.dead && u.element != null).ToList();
			int redDrones = alive.Count(u => u.isRed && u.isDrone);
			int redTurrets = alive.Count(u => u.isRed && !u.isDrone);
			int redHp = (int)alive.Where(u => u.isRed).Sum(u => u.element.Health);
			int greenDrones = alive.Count(u => !u.isRed && u.isDrone);
			int greenTurrets = alive.Count(u => !u.isRed && !u.isDrone);
			int greenHp = (int)alive.Where(u => !u.isRed).Sum(u => u.element.Health);

			bool counted = result == "VICTORY" || result == "DEFEAT";
			int battleIndex = -1;
			if (counted)
			{
				battleIndex = m_ValidIndex++;
				string vname = CurrentVariant.Key;
				if (!m_VariantResults.ContainsKey(vname)) m_VariantResults[vname] = new List<float[]>();
				m_VariantResults[vname].Add(new float[] { result == "VICTORY" ? 1 : 0, redHp, greenHp, t });
				bool victory = result == "VICTORY";
				if (victory) m_Victories++;
				// Annexe B : en cas de défaite, l'armée rouge n'a plus rien
				string line = string.Join("\t", battleIndex, victory ? 1 : 0, (int)t,
					victory ? redDrones : 0, victory ? redTurrets : 0, victory ? redHp : 0);
				if (m_Variants.Count == 1) File.AppendAllText(m_BattlesPath, line + "\n");
			}

			foreach (var kv in m_Red.obstacleHits) AddEvent(t, "OBSTACLE_HITS", "RED", kv.Key, "", Vector3.zero, kv.Value.ToString(Inv));
			foreach (var kv in m_Green.obstacleHits) AddEvent(t, "OBSTACLE_HITS", "GREEN", kv.Key, "", Vector3.zero, kv.Value.ToString(Inv));
			File.AppendAllText(m_EventsPath, m_EventLines.ToString());

			var cols = new List<string>
			{
				m_RunIndex.ToString(Inv), battleIndex.ToString(Inv), result, CurrentVariant.Key, F(t),
				redDrones.ToString(Inv), redTurrets.ToString(Inv), redHp.ToString(Inv),
				greenDrones.ToString(Inv), greenTurrets.ToString(Inv), greenHp.ToString(Inv),
			};
			cols.AddRange(SideColumns(m_Red));
			cols.AddRange(SideColumns(m_Green));
			File.AppendAllText(m_TelemetryPath, string.Join("\t", cols) + "\n");

			Debug.Log($"[Série] bataille {m_RunIndex} ({CurrentVariant.Key}) : {result} en {(int)t} s | rouge {redDrones}D {redTurrets}T {redHp}PV | vert {greenDrones}D {greenTurrets}T {greenHp}PV" +
				$" | précision rouge {Pct(m_Red.hitDrone + m_Red.hitTurret, m_Red.missiles)} vert {Pct(m_Green.hitDrone + m_Green.hitTurret, m_Green.missiles)}");
		}

		void NextBattleOrStop()
		{
			m_RunIndex++;
			if (m_ValidIndex >= m_TargetBattles || m_RunIndex >= m_TargetBattles * 2)
			{
				Debug.Log($"[Série {m_Label}] terminée : {m_Victories}/{m_ValidIndex} victoires. Fichiers : {m_TelemetryPath}");
				foreach (var kv in m_VariantResults)
					Debug.Log($"[Série] {kv.Key} : {kv.Value.Sum(r => r[0])}/{kv.Value.Count} victoires, PV rouges {kv.Value.Average(r => r[1]):0}, PV verts {kv.Value.Average(r => r[2]):0}");
				Time.timeScale = 1f;
#if UNITY_EDITOR
				UnityEditor.SessionState.SetBool(BattleSeriesKeys.Active, false);
				UnityEditor.EditorApplication.isPlaying = false;
#endif
				Destroy(gameObject);
				return;
			}
			m_State = State.Loading; // rien n'est compté pendant le rechargement ; OnSceneLoaded relance la mesure
			SceneManager.LoadScene(SceneManager.GetActiveScene().name);
		}

		void AddEvent(float t, string evt, string side, string unit, string cause, Vector3 pos, string detail)
		{
			m_EventLines.Append(string.Join("\t", m_RunIndex.ToString(Inv), F(t), evt, side, unit, cause, F(pos.x), F(pos.z), detail)).Append('\n');
		}

		static string TelemetryHeader()
		{
			var h = new List<string> { "RUN", "INDEX_BATTLE", "RESULT", "VARIANT", "DURATION", "RED_DRONES", "RED_TURRETS", "RED_HP", "GREEN_DRONES", "GREEN_TURRETS", "GREEN_HP" };
			foreach (var s in new[] { "RED", "GREEN" })
				h.AddRange(new[] {
					"MSL", "MSL_HIT_DRONE", "MSL_HIT_TURRET", "MSL_HIT_FRIENDLY", "MSL_HIT_TERRAIN", "MSL_HIT_OBSTACLE", "MSL_HIT_MISSILE", "MSL_EXPIRED",
					"MSL_HIT_DIST_AVG", "GRENADES", "DMG_BY_MSL", "DMG_BY_GRENADE",
					"KILL_DRONE_MSL", "KILL_DRONE_GREN", "KILL_TURRET_MSL", "KILL_TURRET_GREN",
					"DRONE_MOVING_PCT", "FIRST_TURRET_LOST", "ALL_TURRETS_LOST" }.Select(c => s + "_" + c));
			return string.Join("\t", h);
		}

		static IEnumerable<string> SideColumns(SideStats s)
		{
			int hits = s.hitDrone + s.hitTurret;
			return new[]
			{
				s.missiles.ToString(Inv), s.hitDrone.ToString(Inv), s.hitTurret.ToString(Inv), s.hitFriendly.ToString(Inv),
				s.hitTerrain.ToString(Inv), s.hitObstacle.ToString(Inv), s.hitMissile.ToString(Inv), s.expired.ToString(Inv),
				F(hits > 0 ? s.hitDistanceSum / hits : 0), s.grenades.ToString(Inv), F(s.dmgByMissile), F(s.dmgByGrenade),
				s.killDroneMsl.ToString(Inv), s.killDroneGren.ToString(Inv), s.killTurretMsl.ToString(Inv), s.killTurretGren.ToString(Inv),
				F(s.droneAliveTime > 0 ? 100f * s.droneMovingTime / s.droneAliveTime : 0), F(s.firstTurretLost), F(s.allTurretsLost),
			};
		}

		static string F(float v) => v.ToString("0.0", Inv);
		static string Pct(int a, int b) => b > 0 ? (100f * a / b).ToString("0", Inv) + "%" : "-";
	}

	public static class BattleSeriesKeys
	{
		public const string Active = "RvG.Series.Active";
		public const string Battles = "RvG.Series.Battles";
		public const string TimeScale = "RvG.Series.TimeScale";
		public const string Label = "RvG.Series.Label";
		public const string Variants = "RvG.Series.Variants";
	}
}
