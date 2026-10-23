using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DayCycle
{
	// blacklisted moons get the game's own 18h day and every hook runs the original; deadline stays in 24h days.
	// no-clock moons count as blacklisted but keep the 24h day (no clock runs there)
	public static class MoonBlacklist
	{
		// on a vanilla day: from the moon's scene load to the end-of-day step
		internal static bool Active { get; private set; }

		// the game's own day length, read before DayCycle sets 24
		internal static int VanillaHours = 18;
		internal static float Scale => VanillaHours / 24f;

		private const string MoonsMessage = "DayCycle.VanillaMoons";
		private static readonly HashSet<int> syncedMoons = new HashSet<int>(); // clients: the host's blacklisted moons (levelID)
		private static string parsedFrom = "";
		private static readonly HashSet<string> listed = new HashSet<string>();
		private static float nextSync;

		internal static void Patch()
		{
			DayCycle.Hook("StartOfRound.Start (vanilla day)", () => On.StartOfRound.Start += StartOfRound_Start);
			DayCycle.Hook("StartOfRound.SceneManager_OnLoad", () => On.StartOfRound.SceneManager_OnLoad += StartOfRound_SceneManager_OnLoad);
			DayCycle.Hook("StartOfRound.StartGame", () => On.StartOfRound.StartGame += StartOfRound_StartGame);
			DayCycle.Hook("StartOfRound.PassTimeToNextDay (vanilla day)", () => On.StartOfRound.PassTimeToNextDay += StartOfRound_PassTimeToNextDay);
			DayCycle.Hook("GameNetworkManager.SaveGameValues", () => On.GameNetworkManager.SaveGameValues += GameNetworkManager_SaveGameValues);
			DayCycle.Hook("TimeOfDay.Update (vanilla day)", () => On.TimeOfDay.Update += TimeOfDay_Update);
			ModConfig.moonBlacklist.SettingChanged += (_, _) => SyncNow();
		}

		private static bool IsHost => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

		#region blacklist
		// "8 Titan", "8-Titan", "titan" -> "titan"
		private static string Normalize(string name)
		{
			string noNumber = Regex.Replace(name ?? "", @"^\s*\d+[\s\-]*", "");
			return new string(noNumber.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
		}

		private static bool Listed(SelectableLevel level)
		{
			string value = ModConfig.moonBlacklist.Value ?? "";
			if (value != parsedFrom)
			{
				parsedFrom = value;
				listed.Clear();
				foreach (string name in value.Split(',').Select(Normalize).Where(n => n.Length > 0)) { listed.Add(name); }
			}
			return listed.Contains(Normalize(level.PlanetName));
		}

		// blacklisted and with a clock; the host's list decides
		internal static bool IsVanillaMoon(SelectableLevel? level)
		{
			if (level == null || !level.planetHasTime) { return false; }
			return IsHost ? Listed(level) : syncedMoons.Contains(level.levelID);
		}

		private static List<int> HostMoonIds()
		{
			List<int> ids = new List<int>();
			if (StartOfRound.Instance == null) { return ids; }
			foreach (SelectableLevel level in StartOfRound.Instance.levels)
			{
				if (level != null && level.planetHasTime && Listed(level)) { ids.Add(level.levelID); }
			}
			return ids;
		}
		#endregion

		#region sync
		// every 10 s for late joiners, on a list change and when the lever is pulled
		internal static void SyncNow()
		{
			nextSync = Time.time + 10f;
			CustomMessagingManager? messaging = NetworkManager.Singleton?.CustomMessagingManager;
			if (!IsHost || messaging == null || StartOfRound.Instance == null) { return; }
			List<int> ids = HostMoonIds();
			using (FastBufferWriter writer = new FastBufferWriter(4 + ids.Count * 4, Allocator.Temp))
			{
				writer.WriteValueSafe(ids.Count);
				foreach (int id in ids) { writer.WriteValueSafe(id); }
				messaging.SendNamedMessageToAll(MoonsMessage, writer, NetworkDelivery.Reliable);
			}
		}

		private static void OnMoonsMessage(ulong sender, FastBufferReader reader)
		{
			if (IsHost) { return; }
			reader.ReadValueSafe(out int count);
			List<int> ids = new List<int>();
			for (int i = 0; i < count; i++)
			{
				reader.ReadValueSafe(out int id);
				ids.Add(id);
			}
			if (syncedMoons.SetEquals(ids)) { return; }
			syncedMoons.Clear();
			foreach (int id in ids) { syncedMoons.Add(id); }
		}

		private static void TimeOfDay_Update(On.TimeOfDay.orig_Update orig, TimeOfDay self)
		{
			orig(self);
			if (IsHost && Time.time >= nextSync) { SyncNow(); }
		}
		#endregion

		#region switch
		private static void StartOfRound_Start(On.StartOfRound.orig_Start orig, StartOfRound self)
		{
			Active = false; // a new lobby has a new TimeOfDay, already on 24h days
			syncedMoons.Clear();
			orig(self);
			NetworkManager.Singleton?.CustomMessagingManager?.RegisterNamedMessageHandler(MoonsMessage, OnMoonsMessage);
		}

		// host, lever pulled: resent ahead of the scene load for clients that just joined
		private static void StartOfRound_StartGame(On.StartOfRound.orig_StartGame orig, StartOfRound self)
		{
			if (self.IsServer && self.inShipPhase) { SyncNow(); }
			orig(self);
		}

		// every machine, as the moon scene starts loading: before its objects wake and the clock starts
		private static void StartOfRound_SceneManager_OnLoad(On.StartOfRound.orig_SceneManager_OnLoad orig, StartOfRound self, ulong clientId, string sceneName, LoadSceneMode loadSceneMode, AsyncOperation asyncOperation)
		{
			orig(self, clientId, sceneName, loadSceneMode, asyncOperation);
			if (Active || sceneName == "SampleSceneRelay" || sceneName == "MainMenu") { return; }
			SelectableLevel level = self.currentLevel;
			TimeOfDay tod = TimeOfDay.Instance;
			if (tod == null || level == null || level.sceneName != sceneName || !IsVanillaMoon(level)) { return; }

			tod.numberOfHours = VanillaHours;
			tod.totalTime = tod.lengthOfHours * VanillaHours;
			Active = true;
			EnterDeadline(tod);
			P.Log($"Vanilla day: {level.PlanetName} is blacklisted, {VanillaHours}-hour day");
		}

		// every machine, after the end-of-day step (moon unloaded, clock stopped)
		private static void StartOfRound_PassTimeToNextDay(On.StartOfRound.orig_PassTimeToNextDay orig, StartOfRound self, int connectedPlayersOnServer)
		{
			orig(self, connectedPlayersOnServer);
			if (!Active) { return; }
			TimeOfDay tod = TimeOfDay.Instance;
			Active = false;
			if (tod == null) { return; }
			float deadline = RealDeadline(tod);
			tod.numberOfHours = 24;
			tod.totalTime = tod.lengthOfHours * 24f;
			tod.timeUntilDeadline = deadline;
			tod.UpdateProfitQuotaCurrentTime();
			P.Log("Vanilla day: back to the 24-hour day");
		}
		#endregion

		#region deadline
		// the game spends its own 18h-day copy during the visit, the real value stays in 24h days
		private static float heldDeadline, enteredCopy;

		private static void EnterDeadline(TimeOfDay tod)
		{
			heldDeadline = tod.timeUntilDeadline;
			tod.timeUntilDeadline = heldDeadline * Scale;
			enteredCopy = tod.timeUntilDeadline;
			tod.UpdateProfitQuotaCurrentTime();
		}

		// what the visit spent so far, in 24h days
		private static float RealDeadline(TimeOfDay tod)
		{
			return heldDeadline - (enteredCopy - tod.timeUntilDeadline) / Scale;
		}

		// saves only happen in orbit, but if one hits a vanilla day write the 24h deadline
		private static void GameNetworkManager_SaveGameValues(On.GameNetworkManager.orig_SaveGameValues orig, GameNetworkManager self)
		{
			TimeOfDay tod = TimeOfDay.Instance;
			if (!Active || tod == null) { orig(self); return; }
			float copy = tod.timeUntilDeadline;
			tod.timeUntilDeadline = RealDeadline(tod);
			try { orig(self); }
			finally { tod.timeUntilDeadline = copy; }
		}
		#endregion
	}
}
