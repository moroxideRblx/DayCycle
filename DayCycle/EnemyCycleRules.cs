using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;

namespace DayCycle
{
	public enum EnemyCycleRule { Cycled, Vanilla }
	public enum EnemyCycleGroup { Daytime, Outside, Inside }

	// Day-cycle enemy groups and per-type rules (config 2.1 / 2.2 / 2.3)
	public static class EnemyCycleRules
	{
		internal static void Patch()
		{
			DayCycle.Hook("StartOfRound.Start (enemy cycle)", () => On.StartOfRound.Start += StartOfRound_Start);
		}

		public static EnemyCycleGroup GroupOf(EnemyType type)
		{
			if (type.isDaytimeEnemy) { return EnemyCycleGroup.Daytime; }
			return type.isOutsideEnemy ? EnemyCycleGroup.Outside : EnemyCycleGroup.Inside;
		}

		public static EnemyCycleRule RuleFor(EnemyType? type)
		{
			if (type == null || string.IsNullOrWhiteSpace(type.enemyName)) { return EnemyCycleRule.Cycled; }
			return ModConfig.EnemyRule(type)?.Value ?? EnemyCycleRule.Cycled; // null: the group's toggle is off
		}

		public static bool IsOldBird(EnemyType type) => PrefabHas<RadMechAI>(type);
		public static bool IsCircuitBees(EnemyType type) => PrefabHas<RedLocustBees>(type);
		public static bool IsButlerBees(EnemyType type) => PrefabHas<ButlerBeesEnemyAI>(type);
		public static bool IsCadaverBloom(EnemyType type) => PrefabHas<CadaverBloomAI>(type);

		private static bool PrefabHas<T>(EnemyType type) where T : Component
		{
			return type.enemyPrefab != null && type.enemyPrefab.GetComponent<T>() != null;
		}

		// never removed indoors: nutcracker gun only drops on a real death, maneater baby can be held,
		// ghost girl and cadaver bloom are bound to a player
		public static bool NeverRemovedIndoors(EnemyType type)
		{
			GameObject? prefab = type.enemyPrefab;
			return prefab != null && (prefab.GetComponent<NutcrackerEnemyAI>() != null || prefab.GetComponent<CaveDwellerAI>() != null
				|| prefab.GetComponent<DressGirlAI>() != null || prefab.GetComponent<CadaverBloomAI>() != null);
		}

		#region names
		// display name, asset name or AI class name (GiantKiwiAI -> giantkiwi), spaces and case ignored
		public static bool MatchesAnyName(EnemyType type, ICollection<string> normalizedNames)
		{
			if (normalizedNames.Count == 0) { return false; }
			if (normalizedNames.Contains(Normalize(type.enemyName ?? "")) || normalizedNames.Contains(Normalize(type.name ?? ""))) { return true; }
			EnemyAI? prefabAI = type.enemyPrefab != null ? type.enemyPrefab.GetComponent<EnemyAI>() : null;
			if (prefabAI == null) { return false; }
			string cls = Normalize(prefabAI.GetType().Name);
			return normalizedNames.Contains(cls) || normalizedNames.Contains(StripSuffix(cls, "ai")) || normalizedNames.Contains(StripSuffix(cls, "enemy"));
		}

		public static string Normalize(string name)
		{
			return new string(name.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
		}

		private static string StripSuffix(string s, string suffix)
		{
			return s.EndsWith(suffix) ? s.Substring(0, s.Length - suffix.Length) : s;
		}
		#endregion

		#region registration
		private static void StartOfRound_Start(On.StartOfRound.orig_Start orig, StartOfRound self)
		{
			orig(self);
			self.StartCoroutine(RegisterDelayed());
		}

		// after LethalLevelLoader / LethalLib / DawnLib have added their enemies
		private static IEnumerator RegisterDelayed()
		{
			yield return new WaitForSeconds(3f);
			RegisterAll();
		}

		// every level's spawn lists, the weed list and every enemy prefab on the network (modded ones and enemy-spawned ones)
		// only for groups that are on; TestEnemy (Red pill) never spawns, skip it
		// anything not found here gets its entry when the cycle first meets it
		internal static void RegisterAll()
		{
			ModConfig.HideDisabledSections();
			HashSet<EnemyType> types = new HashSet<EnemyType>();
			if (StartOfRound.Instance != null)
			{
				foreach (SelectableLevel level in StartOfRound.Instance.levels)
				{
					if (level == null) { continue; }
					foreach (SpawnableEnemyWithRarity s in level.Enemies.Concat(level.OutsideEnemies).Concat(level.DaytimeEnemies)) { if (s?.enemyType != null) { types.Add(s.enemyType); } }
				}
			}
			if (RoundManager.Instance != null)
			{
				foreach (SpawnableEnemyWithRarity s in RoundManager.Instance.WeedEnemies) { if (s?.enemyType != null) { types.Add(s.enemyType); } }
			}
			IReadOnlyList<NetworkPrefab>? prefabs = NetworkManager.Singleton?.NetworkConfig?.Prefabs?.Prefabs;
			if (prefabs != null)
			{
				foreach (NetworkPrefab p in prefabs)
				{
					EnemyAI? ai = p?.Prefab != null ? p.Prefab.GetComponent<EnemyAI>() : null;
					if (ai != null && ai.enemyType != null && ai.GetType() != typeof(TestEnemy)) { types.Add(ai.enemyType); }
				}
			}

			int[] perGroup = new int[3];
			foreach (EnemyType type in types)
			{
				if (type.enemyPrefab == null || string.IsNullOrWhiteSpace(type.enemyName)) { continue; }
				if (ModConfig.EnemyRule(type) != null) { perGroup[(int)GroupOf(type)]++; }
			}
			P.Log($"EnemyCycle: enemy rules for {perGroup[0]} daytime, {perGroup[1]} outside, {perGroup[2]} inside enemy types");
		}
		#endregion
	}
}
