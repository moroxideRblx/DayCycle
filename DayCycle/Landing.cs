using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace DayCycle
{
	// random landing hour per moon, rolled by the host on each orbit and at lobby start, synced to clients;
	// moons without a rolled hour land at the game's own time
	public static class Landing
	{
		private const string HoursMessage = "DayCycle.LandingHours";

		private static readonly Dictionary<int, float> hours = new Dictionary<int, float>(); // levelID -> clock hour (0-24)
		private static bool enabled; // the host's setting
		private static readonly System.Random rng = new System.Random();
		private static float nextSync;
		private static float meteorDelayHours = -1f; // host, hours after landing

		internal static void Patch()
		{
			DayCycle.Hook("StartOfRound.Start (landing)", () => On.StartOfRound.Start += StartOfRound_Start);
			DayCycle.Hook("StartOfRound.SetMapScreenInfoToCurrentLevel", () => On.StartOfRound.SetMapScreenInfoToCurrentLevel += StartOfRound_SetMapScreenInfoToCurrentLevel);
			DayCycle.Hook("Terminal.TextPostProcess (landing)", () => On.Terminal.TextPostProcess += Terminal_TextPostProcess);
			DayCycle.Hook("TimeOfDay.DecideRandomDayEvents", () => On.TimeOfDay.DecideRandomDayEvents += TimeOfDay_DecideRandomDayEvents);
			DayCycle.Hook("TimeOfDay.Update (landing)", () => On.TimeOfDay.Update += TimeOfDay_Update);
			DayCycle.Hook("RoundManager.FinishGeneratingNewLevelClientRpc", () => On.RoundManager.FinishGeneratingNewLevelClientRpc += RoundManager_FinishGeneratingNewLevelClientRpc);
			DayCycle.Hook("RoundManager.ResetEnemySpawningVariables", () => On.RoundManager.ResetEnemySpawningVariables += RoundManager_ResetEnemySpawningVariables);
		}

		#region landing time
		// before the doors open, so day phase, sun and spawn planning see the landing hour; every machine
		private static void RoundManager_FinishGeneratingNewLevelClientRpc(On.RoundManager.orig_FinishGeneratingNewLevelClientRpc orig, RoundManager self)
		{
			orig(self);
			SelectableLevel level = StartOfRound.Instance.currentLevel;
			TimeOfDay tod = TimeOfDay.Instance;
			if (tod == null || !RolledHour(level, out float hour)) { return; }
			tod.currentLevel = level;
			SetLandingTime(tod, hour);
			// start spawn planning at the landing hour instead of catching up every batch since 6 AM
			if (self.IsServer)
			{
				self.currentHour = (int)(DayCycle.PlanetTimeUnwrapped(tod) / tod.lengthOfHours);
				PrePlaceOutdoorEnemies(self, hour);
			}
		}

		private static bool RolledHour(SelectableLevel? level, out float hour)
		{
			hour = 0f;
			return level != null && level.planetHasTime && !MoonBlacklist.Active && hours.TryGetValue(level.levelID, out hour);
		}

		// each moon has its own time offset and speed; solved for the 24h period of the moon's clock
		private static void SetLandingTime(TimeOfDay tod, float hour)
		{
			SelectableLevel level = tod.currentLevel;
			float mult = level.DaySpeedMultiplier > 0f ? level.DaySpeedMultiplier : 1f;
			float target = ((hour - 6f + 24f) % 24f) * tod.lengthOfHours; // clock = 6 AM + currentDayTime / lengthOfHours
			float g = target / mult - level.OffsetFromGlobalTime;
			float period = tod.totalTime / mult;
			while (g < 0f) { g += period; }
			tod.globalTime = g;
		}

		// The game's own landing time on the 24h day: the day starts at globalTime 100; the HUD clock shows whole minutes
		private static float GameLandingHour(SelectableLevel level)
		{
			TimeOfDay tod = TimeOfDay.Instance;
			float lengthOfHours = tod != null ? tod.lengthOfHours : 60f;
			float dayTime = (TimeOfDay.startingGlobalTime + level.OffsetFromGlobalTime) * level.DaySpeedMultiplier % (lengthOfHours * 24f);
			return 6f + Mathf.Floor(dayTime * 60f / lengthOfHours) / 60f;
		}
		#endregion

		#region enemies already present
		private static bool prePlacePending;

		// once per level load: the host also enters FinishGeneratingNewLevelClientRpc once without running its body
		private static void RoundManager_ResetEnemySpawningVariables(On.RoundManager.orig_ResetEnemySpawningVariables orig, RoundManager self)
		{
			orig(self);
			prePlacePending = true;
		}

		// the outdoor batches missed since the last dawn, with vanilla's rules and cap; Old Birds stay in their nests
		internal static void PrePlaceOutdoorEnemies(RoundManager rm, float landingClock)
		{
			if (!prePlacePending) { return; }
			prePlacePending = false;
			if (!ModConfig.enemiesPresentOnLateLanding.Value || StartOfRound.Instance.isChallengeFile || !rm.currentLevel.spawnEnemiesAndScrap) { return; }

			float dawnStart = ModConfig.dawnStartHour.Value;
			float dawnEnd = ModConfig.dawnEndHour.Value;
			float sinceDawn = (landingClock - dawnEnd + 24f) % 24f;
			float windowLen = (dawnEnd - dawnStart + 24f) % 24f;
			bool inWindow = windowLen > 0f && (landingClock - dawnStart + 24f) % 24f < windowLen;
			float until = inWindow ? (dawnStart - dawnEnd + 24f) % 24f : sinceDawn;
			int step = Mathf.Max(rm.hourTimeBetweenEnemySpawnBatches, 1);
			if (until < step) { return; }

			int landingHour = rm.currentHour;
			int start = landingHour - Mathf.RoundToInt(sinceDawn);
			int dayOffset = start < 0 ? 24 * Mathf.CeilToInt(-start / 24f) : 0; // spawn curves only use the time of day
			bool daytime = landingClock >= 6f && landingClock < 20f;

			List<EnemyType> oldBirds = new List<EnemyType>();
			foreach (SpawnableEnemyWithRarity s in rm.currentLevel.OutsideEnemies)
			{
				EnemyType type = s.enemyType;
				if (type != null && !type.spawningDisabled && type.enemyPrefab != null && type.enemyPrefab.GetComponent<RadMechAI>() != null)
				{
					type.spawningDisabled = true;
					oldBirds.Add(type);
				}
			}

			int before = rm.SpawnedEnemies.Count;
			int batches = 0;
			try
			{
				for (float h = step; h <= until; h += step)
				{
					rm.currentHour = start + dayOffset + (int)h;
					if (daytime) { rm.SpawnDaytimeEnemiesOutside(); }
					rm.SpawnEnemiesOutside();
					rm.SpawnWeedEnemies();
					batches++;
				}
			}
			catch (System.Exception e)
			{
				P.Fail($"pre-placing outdoor enemies failed: {e.GetType().Name}: {e.Message}");
			}
			finally
			{
				rm.currentHour = landingHour;
				foreach (EnemyType type in oldBirds) { type.spawningDisabled = false; }
			}
			P.Log($"Landing: {rm.SpawnedEnemies.Count - before} outdoor enemies already present ({batches} batches)");
		}
		#endregion

		private static bool IsHost => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

		public static float HourFor(SelectableLevel level)
		{
			if (MoonBlacklist.IsVanillaMoon(level)) { return MoonBlacklist.LandingHour(level!); }
			if (level != null && level.planetHasTime && hours.TryGetValue(level.levelID, out float h)) { return h; }
			return level != null ? GameLandingHour(level) : 8f;
		}

		public static string Label(SelectableLevel level)
		{
			return DayCycle.FormatHour(HourFor(level));
		}

		private static bool ShowTimes => enabled && StartOfRound.Instance != null && !StartOfRound.Instance.isChallengeFile;

		// the player's own display toggles
		private static bool ShowOnTerminal => ShowTimes && ModConfig.showMoonHourOnTerminal.Value;
		private static bool ShowOnShipScreen => ShowTimes && ModConfig.showMoonHourOnShipScreen.Value;

		#region rolling and sync
		private static void StartOfRound_Start(On.StartOfRound.orig_Start orig, StartOfRound self)
		{
			orig(self);
			hours.Clear();
			enabled = false;
			NetworkManager.Singleton?.CustomMessagingManager?.RegisterNamedMessageHandler(HoursMessage, OnHoursMessage);
			if (IsHost) { self.StartCoroutine(RollDelayed()); }
		}

		// after modded moons are registered
		private static IEnumerator RollDelayed()
		{
			yield return new WaitForSeconds(3.5f);
			Roll();
		}

		// hours roll at the next orbit
		internal static void CreateMoonEntries()
		{
			foreach (SelectableLevel level in Deadline.RoutableMoons()) { ModConfig.RandomLandingFor(level.PlanetName); }
		}

		internal static void Roll()
		{
			if (!IsHost || StartOfRound.Instance == null) { return; }
			hours.Clear();
			enabled = ModConfig.randomLanding.Value;
			if (enabled)
			{
				List<string> rolled = new List<string>();
				foreach (SelectableLevel level in Deadline.RoutableMoons())
				{
					if (MoonBlacklist.IsVanillaMoon(level))
					{
						rolled.Add($"{level.PlanetName} {DayCycle.FormatHour(HourFor(level))} (vanilla day)");
						continue;
					}
					if (!(ModConfig.RandomLandingFor(level.PlanetName)?.Value ?? true))
					{
						rolled.Add($"{level.PlanetName} {DayCycle.FormatHour(HourFor(level))} (game's time)");
						continue;
					}
					hours[level.levelID] = RollHour();
					rolled.Add($"{level.PlanetName} {DayCycle.FormatHour(hours[level.levelID])}");
				}
				P.Log("Landing: rolled " + string.Join(", ", rolled));
			}
			Send();
			if (StartOfRound.Instance.inShipPhase) { StartOfRound.Instance.SetMapScreenInfoToCurrentLevel(); }
		}

		// may cross midnight; 0 to 24 = whole day
		private static float RollHour()
		{
			float min = ModConfig.earliestLandingHour.Value;
			float max = ModConfig.latestLandingHour.Value;
			float len = max - min;
			if (len < 0f) { len += 24f; }
			float h = min + (float)rng.NextDouble() * len;
			return (Mathf.Round(h * 6f) / 6f) % 24f;
		}

		private static void Send()
		{
			nextSync = Time.time + 10f;
			CustomMessagingManager? messaging = NetworkManager.Singleton?.CustomMessagingManager;
			if (!IsHost || messaging == null) { return; }
			using (FastBufferWriter writer = new FastBufferWriter(8 + hours.Count * 8, Allocator.Temp))
			{
				writer.WriteValueSafe(enabled);
				writer.WriteValueSafe(hours.Count);
				foreach (KeyValuePair<int, float> kv in hours)
				{
					writer.WriteValueSafe(kv.Key);
					writer.WriteValueSafe(kv.Value);
				}
				messaging.SendNamedMessageToAll(HoursMessage, writer, NetworkDelivery.Reliable);
			}
		}

		private static void OnHoursMessage(ulong sender, FastBufferReader reader)
		{
			if (IsHost) { return; }
			reader.ReadValueSafe(out bool on);
			reader.ReadValueSafe(out int count);
			Dictionary<int, float> received = new Dictionary<int, float>();
			for (int i = 0; i < count; i++)
			{
				reader.ReadValueSafe(out int id);
				reader.ReadValueSafe(out float h);
				received[id] = h;
			}
			bool changed = on != enabled || received.Count != hours.Count || received.Any(kv => !hours.TryGetValue(kv.Key, out float h) || h != kv.Value);
			enabled = on;
			hours.Clear();
			foreach (KeyValuePair<int, float> kv in received) { hours[kv.Key] = kv.Value; }
			if (changed && StartOfRound.Instance != null && StartOfRound.Instance.inShipPhase) { StartOfRound.Instance.SetMapScreenInfoToCurrentLevel(); }
		}

		private static void TimeOfDay_Update(On.TimeOfDay.orig_Update orig, TimeOfDay self)
		{
			// landing frame; same value as before the doors opened
			if (self.currentDayTimeStarted && self.timeStartedThisFrame && RolledHour(self.currentLevel, out float hour)) { SetLandingTime(self, hour); }
			orig(self);
			if (!IsHost) { return; }
			if (Time.time >= nextSync) { Send(); } // late joiners

			StartOfRound sor = StartOfRound.Instance;
			if (meteorDelayHours >= 0f && sor != null && sor.shipHasLanded && !sor.shipIsLeaving && self.currentDayTimeStarted
				&& Deadline.HoursSinceLanding() >= meteorDelayHours)
			{
				meteorDelayHours = -1f;
				if (self.MeteorWeather != null)
				{
					P.Log("Landing: meteor shower starting");
					self.MeteorWeather.SetStartMeteorShower();
				}
			}
		}
		#endregion

		#region meteor shower
		// vanilla's shower time is a time of day, so a late landing started it at once; same delay as a normal landing instead
		private static void TimeOfDay_DecideRandomDayEvents(On.TimeOfDay.orig_DecideRandomDayEvents orig, TimeOfDay self)
		{
			orig(self);
			meteorDelayHours = -1f;
			if (!self.IsServer || self.meteorShowerAtTime <= 0f || !RolledHour(self.currentLevel, out _)) { return; }
			float vanillaClock = self.meteorShowerAtTime * 24f + 6f; // normalized 0 = 6 AM
			meteorDelayHours = Mathf.Max(vanillaClock - GameLandingHour(self.currentLevel), 0f);
			self.meteorShowerAtTime = -1f;
			P.Log($"Landing: meteor shower {meteorDelayHours:0.#}h after landing");
		}
		#endregion

		#region display
		private static void StartOfRound_SetMapScreenInfoToCurrentLevel(On.StartOfRound.orig_SetMapScreenInfoToCurrentLevel orig, StartOfRound self)
		{
			orig(self);
			// second line, so a long moon description can't push it off screen
			if (!ShowOnShipScreen || MoonBlacklist.Active || self.currentLevel == null || !self.currentLevel.planetHasTime) { return; }
			string text = self.screenLevelDescription.text;
			string line = "Landing time: " + Label(self.currentLevel);
			int firstLineEnd = text.IndexOf('\n');
			self.screenLevelDescription.text = firstLineEnd < 0 ? text + "\n" + line : text.Insert(firstLineEnd, "\n" + line);
		}

		private static string Terminal_TextPostProcess(On.Terminal.orig_TextPostProcess orig, Terminal self, string modifiedDisplayText, TerminalNode node)
		{
			bool moonsPage = Deadline.IsMoonsPage(self, node, modifiedDisplayText);
			string text = orig(self, modifiedDisplayText, node);
			if (!ShowOnTerminal || MoonBlacklist.Active) { return text; }

			if (moonsPage) { return AddMoonTimes(text); }
			SelectableLevel[] levels = StartOfRound.Instance.levels;
			if (node != null && node.displayPlanetInfo >= 0 && node.displayPlanetInfo < levels.Length && levels[node.displayPlanetInfo].planetHasTime)
			{
				// right after the route prompt's "It is currently <weather> on this moon."
				string line = $"You will land at {Label(levels[node.displayPlanetInfo])}.";
				const string weatherSentence = "on this moon.";
				int after = text.IndexOf(weatherSentence);
				if (after >= 0) { return text.Insert(after + weatherSentence.Length, "\n" + line); }
				int confirm = text.IndexOf("Please CONFIRM");
				return confirm >= 0 ? text.Insert(confirm, line + "\n\n") : text.TrimEnd() + "\n" + line + "\n\n";
			}
			return text;
		}

		// by name in the finished page, so it works with LethalLevelLoader's layout too
		private static string AddMoonTimes(string text)
		{
			string[] lines = text.Split('\n');
			foreach (SelectableLevel level in Deadline.RoutableMoons())
			{
				string name = Regex.Replace(level.PlanetName, @"^\s*\d+\s*", "").Trim(); // "41 Experimentation" -> "Experimentation"
				if (name.Length == 0) { continue; }
				Regex word = new Regex(@"\b" + Regex.Escape(name) + @"\b", RegexOptions.IgnoreCase);
				for (int i = 0; i < lines.Length; i++)
				{
					string trimmed = lines[i].TrimStart();
					if (trimmed.StartsWith("*") && word.IsMatch(trimmed) && !lines[i].Contains(" AM") && !lines[i].Contains(" PM"))
					{
						lines[i] = lines[i].TrimEnd('\r', ' ') + "   //   " + Label(level); // like "* The Company building   //   Buying at 30%."
						break;
					}
				}
			}
			return string.Join("\n", lines);
		}
		#endregion
	}
}
