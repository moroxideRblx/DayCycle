using GameNetcodeStuff;
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DayCycle
{
	// dawn window: night enemies leave, Old Birds power down. host only
	public static class EnemyCycle
	{
		private enum WindowState { Idle, Armed, Active, Overtime, Done }

		private static WindowState state = WindowState.Idle;
		private static readonly Dictionary<EnemyAI, float> despawnHour = new Dictionary<EnemyAI, float>(); // in "hours since window start"
		private static readonly HashSet<EnemyAI> staying = new HashSet<EnemyAI>(); // skipped by the despawn percent this window
		private static readonly Dictionary<EnemyAI, float> retryAt = new Dictionary<EnemyAI, float>(); // Time.time
		private static readonly System.Random rng = new System.Random();
		private static float nextCheck;
		private static bool apparatusPulled;

		// new day = end of the dawn window (daily spawn reset)
		private static int dayIndex = int.MinValue;

		internal static void Patch()
		{
			DayCycle.Hook("RoundManager.Update", () => On.RoundManager.Update += RoundManager_Update);
			DayCycle.Hook("RoundManager.SpawnEnemiesOutside", () => On.RoundManager.SpawnEnemiesOutside += RoundManager_SpawnEnemiesOutside);
			DayCycle.Hook("RoundManager.SpawnWeedEnemies", () => On.RoundManager.SpawnWeedEnemies += RoundManager_SpawnWeedEnemies);
			DayCycle.Hook("LungProp.EquipItem", () => On.LungProp.EquipItem += LungProp_EquipItem);
		}

		// 0-24, normalized 0 = 6 AM
		private static float ClockHour()
		{
			return (TimeOfDay.Instance.normalizedTimeOfDay * 24f + 6f) % 24f;
		}

		private static float WindowLength()
		{
			return (ModConfig.dawnEndHour.Value - ModConfig.dawnStartHour.Value + 24f) % 24f;
		}

		private static float HoursSinceStart()
		{
			return (ClockHour() - ModConfig.dawnStartHour.Value + 24f) % 24f;
		}

		private static bool InWindow()
		{
			float len = WindowLength();
			return len > 0f && HoursSinceStart() < len;
		}

		private static bool RoundActive()
		{
			return StartOfRound.Instance != null && TimeOfDay.Instance != null && RoundManager.Instance != null
				&& StartOfRound.Instance.shipHasLanded && !StartOfRound.Instance.shipIsLeaving && TimeOfDay.Instance.currentDayTimeStarted;
		}

		private static bool Eclipsed => TimeOfDay.Instance.currentLevelWeather == LevelWeatherType.Eclipsed;
		private static bool OutsideActive => ModConfig.dawnFadeEnabled.Value && !Eclipsed;

		#region enemy filtering
		private static bool IsFadeType(EnemyType type)
		{
			if (type == null || EnemyCycleRules.GroupOf(type) != EnemyCycleGroup.Outside || EnemyCycleRules.RuleFor(type) == EnemyCycleRule.Vanilla) { return false; }
			if (EnemyCycleRules.IsOldBird(type)) { return !apparatusPulled && type.nestSpawnPrefab != null; }
			return true;
		}

		private static bool IsFadeCandidate(EnemyAI enemy)
		{
			return enemy != null && !enemy.isEnemyDead && IsFadeType(enemy.enemyType);
		}
		#endregion

		#region spawn blocking
		private static bool BlockOutsideSpawns()
		{
			return RoundActive() && !MoonBlacklist.Active && OutsideActive && state == WindowState.Active;
		}

		private static void RoundManager_SpawnEnemiesOutside(On.RoundManager.orig_SpawnEnemiesOutside orig, RoundManager self)
		{
			WithFadeSpawnsDisabled(self, () => orig(self));
		}

		private static void RoundManager_SpawnWeedEnemies(On.RoundManager.orig_SpawnWeedEnemies orig, RoundManager self)
		{
			WithFadeSpawnsDisabled(self, () => orig(self));
		}

		// vanilla skips spawningDisabled types, so Vanilla-rule types (and Old Birds after the Apparatus) still spawn
		private static void WithFadeSpawnsDisabled(RoundManager rm, Action spawn)
		{
			if (!BlockOutsideSpawns()) { spawn(); return; }

			List<EnemyType> disabled = new List<EnemyType>();
			foreach (SpawnableEnemyWithRarity s in rm.currentLevel.OutsideEnemies.Concat(rm.WeedEnemies))
			{
				EnemyType type = s.enemyType;
				if (type != null && !type.spawningDisabled && !disabled.Contains(type) && IsFadeType(type))
				{
					type.spawningDisabled = true;
					disabled.Add(type);
				}
			}
			try { spawn(); }
			finally
			{
				foreach (EnemyType type in disabled) { type.spawningDisabled = false; }
			}
		}
		#endregion

		private static void LungProp_EquipItem(On.LungProp.orig_EquipItem orig, LungProp self)
		{
			bool wasDocked = self.isLungDocked;
			orig(self);
			if (wasDocked && !self.isLungDocked && !apparatusPulled)
			{
				apparatusPulled = true;
				P.Log("EnemyCycle: Apparatus pulled, Old Birds will be left alone this round");
			}
		}

		private static void RoundManager_Update(On.RoundManager.orig_Update orig, RoundManager self)
		{
			orig(self);
			if (!self.IsServer) { return; }

			if (!RoundActive() || MoonBlacklist.Active)
			{
				apparatusPulled = false;
				dayIndex = int.MinValue;
				Reset();
				return;
			}
			if (Time.time < nextCheck) { return; }
			nextCheck = Time.time + 0.5f;

			CheckNewDay(self);
			if (!OutsideActive)
			{
				Reset();
				return;
			}

			bool inWindow = InWindow();
			switch (state)
			{
				case WindowState.Idle:
					// a landing inside the window counts as dawn
					state = WindowState.Armed;
					break;
				case WindowState.Done:
					// wait for this window to pass
					if (!inWindow) { state = WindowState.Armed; }
					break;
				case WindowState.Armed:
					if (inWindow) { BeginWindow(self); }
					break;
				case WindowState.Active:
					if (!inWindow)
					{
						state = WindowState.Overtime;
						if (despawnHour.Count > 0) { P.Log($"EnemyCycle: window over, {despawnHour.Count} outside enemies left, removing each once unseen"); }
					}
					Tick(self, overtime: false);
					break;
				case WindowState.Overtime:
					// the next dawn came first: it schedules everyone again, the leftovers too
					if (inWindow)
					{
						P.LogDebug($"EnemyCycle: next window opened with {despawnHour.Count} outside enemies still waiting from the last one");
						BeginWindow(self);
						break;
					}
					Tick(self, overtime: true);
					if (despawnHour.Count == 0) { state = WindowState.Armed; }
					break;
			}
		}

		private static void Reset()
		{
			state = WindowState.Idle;
			despawnHour.Clear();
			staying.Clear();
			retryAt.Clear();
		}

		private static void BeginWindow(RoundManager rm)
		{
			state = WindowState.Active;
			despawnHour.Clear();
			staying.Clear();
			retryAt.Clear();
			ScheduleNewEnemies(rm, windowStart: true);
			P.Log($"EnemyCycle: window opened at {DayCycle.FormatHour(ClockHour())}, {despawnHour.Count} outside enemies scheduled (+{staying.Count} staying)");
		}

		// only the despawn percent of them leaves, the rest stay till the next window; late arrivals get the same odds
		private static void ScheduleNewEnemies(RoundManager rm, bool windowStart = false)
		{
			if (!OutsideActive) { return; }
			float now = HoursSinceStart();
			float len = WindowLength();
			float share = ModConfig.outsideDespawnPercent.Value / 100f;
			List<EnemyAI> fresh = rm.SpawnedEnemies.Where(e => IsFadeCandidate(e) && !despawnHour.ContainsKey(e) && !staying.Contains(e)).OrderBy(_ => rng.Next()).ToList();
			int leaving = Mathf.RoundToInt(fresh.Count * share);
			for (int i = 0; i < fresh.Count; i++)
			{
				bool leaves = windowStart ? i < leaving : rng.NextDouble() < share;
				if (leaves) { despawnHour[fresh[i]] = now + (float)rng.NextDouble() * Mathf.Max(len - now, 0f); }
				else { staying.Add(fresh[i]); }
			}
		}

		// despawn takes the power off and OnDestroy does it again unless removedPowerLevel is set, so set it first
		private static void Despawn(RoundManager rm, EnemyAI enemy)
		{
			string before = PowerText(rm);
			enemy.removedPowerLevel = true;
			rm.DespawnEnemyOnServer(enemy.NetworkObject);
			P.LogDebug($"EnemyCycle: removed {enemy.enemyType.enemyName} (power {enemy.enemyType.PowerLevel:0.#}); power {before} -> {PowerText(rm)}");
		}

		private static string PowerText(RoundManager rm)
		{
			return $"outside {rm.currentOutsideEnemyPower:0.#}, inside {rm.currentEnemyPower:0.#}, daytime {rm.currentDaytimeEnemyPower:0.#}";
		}

		#region new day
		// spawn caps count since landing (vanilla thinks a visit is one day), so reset them each new day
		private static void CheckNewDay(RoundManager rm)
		{
			float hours = DayCycle.PlanetTimeUnwrapped(TimeOfDay.Instance) / TimeOfDay.Instance.lengthOfHours; // since 6 AM of the landing day
			int index = Mathf.FloorToInt((hours + 6f - ModConfig.dawnEndHour.Value) / 24f);
			if (dayIndex == int.MinValue) { dayIndex = index; return; } // landing: same day, nothing to reset
			if (index <= dayIndex) { return; }
			dayIndex = index;

			List<string> groups = new List<string>();
			SelectableLevel level = rm.currentLevel;
			if (ModConfig.daytimeCycleEnabled.Value)
			{
				P.LogDebug($"EnemyCycle: daytime reset, power {rm.currentDaytimeEnemyPower:0.#} (no deaths {rm.currentDaytimeEnemyPowerNoDeaths:0.#}) / {level.maxDaytimeEnemyPowerCount}");
				ResetSpawnCounts(rm, level.DaytimeEnemies, EnemyCycleGroup.Daytime, chargesDiversity: false);
				rm.currentDaytimeEnemyPowerNoDeaths = rm.currentDaytimeEnemyPower;
				groups.Add("daytime");
			}
			if (ModConfig.dawnFadeEnabled.Value)
			{
				P.LogDebug($"EnemyCycle: outside reset, power {rm.currentOutsideEnemyPower:0.#} (no deaths {rm.currentOutsideEnemyPowerNoDeaths:0.#}) / {rm.currentMaxOutsidePower:0.#}, diversity {rm.currentOutsideEnemyDiversityLevel} / {rm.currentMaxOutsideDiversityLevel}");
				int freed = ResetSpawnCounts(rm, level.OutsideEnemies, EnemyCycleGroup.Outside, chargesDiversity: true);
				ResetSpawnCounts(rm, rm.WeedEnemies, EnemyCycleGroup.Outside, chargesDiversity: false);
				rm.currentOutsideEnemyDiversityLevel = Mathf.Max(rm.currentOutsideEnemyDiversityLevel - freed, 0);
				rm.currentOutsideEnemyPowerNoDeaths = rm.currentOutsideEnemyPower;
				groups.Add("outside");
			}
			if (groups.Count > 0) { P.Log($"EnemyCycle: new day at {DayCycle.FormatHour(ClockHour())}, spawn counts reset for {string.Join(", ", groups)} enemies"); }
			P.LogDebug($"EnemyCycle: after reset, outside diversity {rm.currentOutsideEnemyDiversityLevel}");
		}

		// count = the ones still alive
		// none left: refund its diversity, or it pays twice and may never come back (v81 charges it on first spawn)
		private static int ResetSpawnCounts(RoundManager rm, IEnumerable<SpawnableEnemyWithRarity> list, EnemyCycleGroup group, bool chargesDiversity)
		{
			int freed = 0;
			foreach (EnemyType type in list.Select(s => s?.enemyType).Where(t => t != null).Distinct()!)
			{
				if (EnemyCycleRules.GroupOf(type!) != group || EnemyCycleRules.RuleFor(type) == EnemyCycleRule.Vanilla) { continue; }
				int alive = rm.SpawnedEnemies.Count(e => e != null && !e.isEnemyDead && e.enemyType == type);
				bool refund = chargesDiversity && alive == 0 && type!.hasSpawnedAtLeastOne;
				if (refund)
				{
					type!.hasSpawnedAtLeastOne = false;
					freed += type.DiversityPowerLevel;
				}
				P.LogDebug($"EnemyCycle: {type!.enemyName} living {alive}, count {type.numberSpawned} -> {alive}{(refund ? $", diversity {type.DiversityPowerLevel} refunded" : "")}");
				type.numberSpawned = alive;
			}
			return freed;
		}
		#endregion

		private static void Tick(RoundManager rm, bool overtime)
		{
			if (!OutsideActive) { despawnHour.Clear(); return; }
			if (!overtime) { ScheduleNewEnemies(rm); } // enemies spawned by other mods mid-window

			float now = overtime ? float.MaxValue : HoursSinceStart();
			List<EnemyAI> due = new List<EnemyAI>();
			List<EnemyAI> gone = new List<EnemyAI>();
			foreach (KeyValuePair<EnemyAI, float> kv in despawnHour)
			{
				// also drops Old Birds once the Apparatus is pulled
				if (!IsFadeCandidate(kv.Key)) { gone.Add(kv.Key); }
				else if (kv.Value <= now && (!retryAt.TryGetValue(kv.Key, out float t) || Time.time >= t)) { due.Add(kv.Key); }
			}
			foreach (EnemyAI enemy in gone) { Forget(enemy); }

			foreach (EnemyAI enemy in due)
			{
				if (!CanRemove(enemy, overtime))
				{
					retryAt[enemy] = Time.time + 3f + (float)rng.NextDouble() * 7f;
					continue;
				}
				Forget(enemy);
				if (enemy is RadMechAI bird)
				{
					P.Log($"EnemyCycle: Old Bird powered down at {DayCycle.FormatHour(ClockHour())}");
					PowerDownOldBird(rm, bird);
				}
				else
				{
					P.Log($"EnemyCycle: despawned {enemy.enemyType.enemyName} at {DayCycle.FormatHour(ClockHour())}");
					Despawn(rm, enemy);
				}
			}
		}

		private static void Forget(EnemyAI enemy)
		{
			despawnHour.Remove(enemy);
			retryAt.Remove(enemy);
		}

		private static bool CanRemove(EnemyAI enemy, bool overtime)
		{
			if (enemy.NetworkObject == null || !enemy.NetworkObject.IsSpawned) { return false; }

			// sand worm: only while fully underground and not lining up an emerge
			if (enemy is SandWormAI worm)
			{
				return !worm.emerged && !worm.inEmergingState && (overtime || worm.currentBehaviourStateIndex == 0);
			}
			if (enemy.inSpecialAnimation || enemy.inSpecialAnimationWithPlayer != null) { return false; }
			// Old Bird: never mid-flight, the dormant nest has to sit on the ground
			if (enemy is RadMechAI bird && (bird.currentBehaviourStateIndex == 2 || bird.inFlyingMode)) { return false; }

			// past the window, being busy no longer delays it; being seen still does
			if (!overtime && IsEngagingPlayer(enemy)) { return false; }
			return !AnyPlayerCanSee(enemy, insideBuilding: false);
		}

		private static bool IsEngagingPlayer(EnemyAI enemy)
		{
			switch (enemy)
			{
				case MouthDogAI _: return enemy.currentBehaviourStateIndex >= 2; // 2 chasing, 3 lunging
				case ForestGiantAI _: return enemy.currentBehaviourStateIndex == 1; // chasing
				case BaboonBirdAI _: return enemy.currentBehaviourStateIndex == 2; // fighting a threat
				case RadMechAI bird: return bird.currentBehaviourStateIndex != 0 || bird.isAlerted || bird.aimingGun || bird.chargingForward || bird.attemptingGrab;
				default: return enemy.targetPlayer != null || enemy.movingTowardsTargetPlayer; // modded / other
			}
		}

		// only players on the same side (outside / inside the building) can see the enemy
		private static bool AnyPlayerCanSee(EnemyAI enemy, bool insideBuilding)
		{
			Vector3 body = enemy.transform.position + Vector3.up;
			Vector3? eye = enemy.eye != null ? enemy.eye.position : (Vector3?)null;
			foreach (PlayerControllerB player in StartOfRound.Instance.allPlayerScripts)
			{
				if (player == null || !player.isPlayerControlled || player.isPlayerDead || player.isInsideFactory != insideBuilding) { continue; }
				// wide cone + short "can feel it behind you" radius, long range
				if (player.HasLineOfSightToPosition(body, 70f, 250, 20f)) { return true; }
				if (eye.HasValue && player.HasLineOfSightToPosition(eye.Value, 70f, 250, 20f)) { return true; }
			}
			return false;
		}

		// swap the bird for a dormant nest on the spot, it can wake up again like vanilla
		private static void PowerDownOldBird(RoundManager rm, RadMechAI bird)
		{
			EnemyType type = bird.enemyType;
			Vector3 pos = bird.transform.position;
			Quaternion rot = Quaternion.Euler(0f, bird.transform.eulerAngles.y, 0f);

			Despawn(rm, bird);
			type.numberSpawned = Mathf.Max(type.numberSpawned - 1, 0); // lets the hourly spawn wake it again later

			GameObject nest = Object.Instantiate(type.nestSpawnPrefab, pos, rot);
			NetworkObject nestNet = nest.GetComponentInChildren<NetworkObject>();
			EnemyAINestSpawnObject nestSpawn = nest.GetComponent<EnemyAINestSpawnObject>();
			if (nestNet == null || nestSpawn == null)
			{
				P.Fail("Old Bird nest prefab is missing its NetworkObject or EnemyAINestSpawnObject");
				Object.Destroy(nest);
				return;
			}
			nestNet.Spawn(destroyWithScene: true);
			rm.enemyNestSpawnObjects.Add(nestSpawn);

			// clients wake Old Birds from their own nest list, so resend it like level gen does
			rm.enemyNestSpawnObjects.RemoveAll(n => n == null);
			NetworkObjectReference[] refs = rm.enemyNestSpawnObjects
				.Select(n => n.GetComponent<NetworkObject>())
				.Where(n => n != null && n.IsSpawned)
				.Select(n => (NetworkObjectReference)n)
				.ToArray();
			rm.SyncNestSpawnObjectsOrderServerRpc(refs);
		}

	}
}
