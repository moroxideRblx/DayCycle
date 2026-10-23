using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DayCycle
{
	public enum DeadlineMode { OneDayExtended, Days, DayAndHour }
	public enum DeadlineDaysSource { GameOrOtherMods, DayCycle }

	// replaces vanilla's deadline spending, host decides and syncs; lives in timeUntilDeadline (1 day = totalTime = 24h).
	// on a vanilla day the game holds an 18h-day copy and the real value is here
	// "zero" keeps a small leftover like vanilla's "0 Days" so the Company trip stays; quota is checked on the next takeoff at zero
	public static class Deadline
	{
		private static float landingClock = 8f; // 0-24, the moon's clock at landing
		private const string StateMessage = "DayCycle.DeadlineState";
		private const string AlertMessage = "DayCycle.DeadlineAlert";

		// the host's mode, synced to clients
		private static DeadlineMode activeMode = DeadlineMode.OneDayExtended;

		// host only, except landingClock, landGlobalTime and landDayTime (every machine)
		private static bool visitActive, landedAtZero, reachedZero, leaving, leaveWarned;
		private static float landGlobalTime;
		private static float landDayTime; // vanilla day: moon time at landing
		private static int deductedCount, alertedCount; // Days mode
		private static float chargedHours; // DayAndHour mode
		private static float wipePenaltyHours; // synced for the takeoff popup
		private static bool dirty;
		private static float nextBroadcast, nextForcedBroadcast;

		private static readonly Dictionary<int, int> routePrices = new Dictionary<int, int>(); // levelID -> credits

		internal static void Patch()
		{
			DayCycle.Hook("StartOfRound.Start", () => On.StartOfRound.Start += StartOfRound_Start);
			DayCycle.Hook("TimeOfDay.Update", () => On.TimeOfDay.Update += TimeOfDay_Update);
			DayCycle.Hook("StartOfRound.OnShipLandedMiscEvents", () => On.StartOfRound.OnShipLandedMiscEvents += StartOfRound_OnShipLandedMiscEvents);
			DayCycle.Hook("StartOfRound.ShipLeave", () => On.StartOfRound.ShipLeave += StartOfRound_ShipLeave);
			DayCycle.Hook("StartOfRound.PassTimeToNextDay", () => On.StartOfRound.PassTimeToNextDay += StartOfRound_PassTimeToNextDay);
			DayCycle.Hook("TimeOfDay.UpdateProfitQuotaCurrentTime", () => On.TimeOfDay.UpdateProfitQuotaCurrentTime += TimeOfDay_UpdateProfitQuotaCurrentTime);
			DayCycle.Hook("HUDManager.DisplayDaysLeft", () => On.HUDManager.DisplayDaysLeft += HUDManager_DisplayDaysLeft);
			DayCycle.Hook("Terminal.TextPostProcess", () => On.Terminal.TextPostProcess += Terminal_TextPostProcess);
			DayCycle.Hook("StartOfRound.SetTimeAndPlanetToSavedSettings", () => On.StartOfRound.SetTimeAndPlanetToSavedSettings += StartOfRound_SetTimeAndPlanetToSavedSettings);
			DayCycle.Hook("TimeOfDay.SyncNewProfitQuotaClientRpc", () => On.TimeOfDay.SyncNewProfitQuotaClientRpc += TimeOfDay_SyncNewProfitQuotaClientRpc);
			DayCycle.Hook("StartOfRound.ResetShip", () => On.StartOfRound.ResetShip += StartOfRound_ResetShip);
			DayCycle.Hook("StartOfRound.ArriveAtLevel", () => On.StartOfRound.ArriveAtLevel += StartOfRound_ArriveAtLevel);
			DayCycle.Hook("TimeOfDay.SyncTimeClientRpc", () => On.TimeOfDay.SyncTimeClientRpc += TimeOfDay_SyncTimeClientRpc);
		}

		#region fresh deadline length
		// a fresh deadline is only set on a new save, a new quota or being fired
		private static float resetCheckAt = -1f;
		private static string resetReason = "";

		private static void StartOfRound_SetTimeAndPlanetToSavedSettings(On.StartOfRound.orig_SetTimeAndPlanetToSavedSettings orig, StartOfRound self)
		{
			bool newSave = !ES3.KeyExists("DeadlineTime", GameNetworkManager.Instance.currentSaveFileName);
			orig(self);
			orbitedLevelId = self.currentLevelID;
			if (newSave && !self.isChallengeFile) { DeadlineReset("new save"); }
		}

		private static void TimeOfDay_SyncNewProfitQuotaClientRpc(On.TimeOfDay.orig_SyncNewProfitQuotaClientRpc orig, TimeOfDay self, int newProfitQuota, int overtimeBonus, int fulfilledQuota)
		{
			orig(self, newProfitQuota, overtimeBonus, fulfilledQuota);
			DeadlineReset("new quota");
		}

		private static void StartOfRound_ResetShip(On.StartOfRound.orig_ResetShip orig, StartOfRound self)
		{
			orig(self);
			orbitedLevelId = self.currentLevelID;
			DeadlineReset("fired");
		}

		// re-checked shortly after: quota mods often patch these same methods and may run after us
		private static void DeadlineReset(string reason)
		{
			if (!IsHost || Tod == null || StartOfRound.Instance == null || StartOfRound.Instance.isChallengeFile) { return; }
			ApplyFreshDeadline(reason);
			resetReason = reason;
			resetCheckAt = Time.time + 0.5f;
		}

		private static void ApplyFreshDeadline(string reason)
		{
			float day = Day;
			float vanillaDay = Tod.lengthOfHours * MoonBlacklist.VanillaHours;
			float current = Tod.timeUntilDeadline;
			float target;

			if (ModConfig.deadlineDaysSource.Value == DeadlineDaysSource.DayCycle)
			{
				int days = ModConfig.deadlineDays.Value;
				Tod.quotaVariables.deadlineDaysAmount = days; // the buying rate is computed from it
				target = days * day;
				if (!Approximately(current, target)) { P.Log($"Deadline: set to {days} days from the DayCycle setting ({reason})"); }
			}
			else
			{
				int days = Tod.quotaVariables.deadlineDaysAmount;
				if (Approximately(current, days * day)) { return; }
				if (Approximately(current, days * vanillaDay))
				{
					target = days * day;
					P.Log($"Deadline: corrected {days} days from 18h days to 24h days ({reason})");
				}
				else if (WholeDays(current, day, out int chosen))
				{
					return; // a mod chose whole 24h days: keep it
				}
				else if (WholeDays(current, vanillaDay, out int vanillaDays))
				{
					target = vanillaDays * day;
					P.Log($"Deadline: corrected {vanillaDays} days from 18h days to 24h days ({reason})");
				}
				else
				{
					target = days * day;
					P.Log($"Deadline: corrected {current / Tod.lengthOfHours:0.#}h to {days} days of 24h ({reason})");
				}
			}

			Tod.timeUntilDeadline = target;
			Tod.SetBuyingRateForDay();
			Broadcast();
		}

		// the save stores the deadline as an int, so allow a little rounding
		private static bool Approximately(float a, float b) => Mathf.Abs(a - b) < 1.5f;

		private static bool WholeDays(float time, float dayLength, out int days)
		{
			days = Mathf.RoundToInt(time / dayLength);
			return days >= 1 && Approximately(time, days * dayLength);
		}
		#endregion

		#region helpers
		private static bool IsHost => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
		private static TimeOfDay Tod => TimeOfDay.Instance;
		private static float Leftover => Tod.lengthOfHours * 0.5f;
		private static float Day => Tod.lengthOfHours * 24f;

		// the ship's terminal, searched for once per lobby (a new lobby has a new one)
		private static Terminal? terminal;
		private static Terminal? Term => terminal != null ? terminal : (terminal = Object.FindObjectOfType<Terminal>());

		// the deadline in 24h days; on a vanilla day changes also move the game's 18h-day copy
		private static float held;
		internal static float Value
		{
			get => MoonBlacklist.Active ? held : Tod.timeUntilDeadline;
			set
			{
				if (MoonBlacklist.Active)
				{
					Tod.timeUntilDeadline += (value - held) * MoonBlacklist.Scale;
					held = value;
				}
				else { Tod.timeUntilDeadline = value; }
			}
		}

		// every machine; days left stay the same
		internal static void EnterVanillaDay()
		{
			held = Tod.timeUntilDeadline;
			Tod.timeUntilDeadline = held * MoonBlacklist.Scale;
			Tod.UpdateProfitQuotaCurrentTime();
			if (IsHost) { Broadcast(); } // clients learn the host's day length now
		}

		// the host's value; clients got it with the takeoff broadcast
		internal static void ExitVanillaDay()
		{
			Tod.timeUntilDeadline = held;
			Tod.UpdateProfitQuotaCurrentTime();
			if (IsHost) { Broadcast(); }
		}

		// OneDayExtended and Days count whole days, DayAndHour counts hours
		private static bool CountsWholeDays => activeMode != DeadlineMode.DayAndHour;

		// whole days: under one day left (vanilla's "0 Days"); DayAndHour: only the leftover
		private static bool AtZero()
		{
			return CountsWholeDays ? Value < Day : Value <= Leftover + 0.01f;
		}

		internal static float HoursSinceLanding()
		{
			SelectableLevel level = Tod.currentLevel;
			// vanilla day: the game's own moon time, which mods may compute differently (see MoonBlacklist.LandingHour)
			if (MoonBlacklist.Active && level != null) { return Mathf.Max(Tod.CalculatePlanetTime(level) - landDayTime, 0f) / Tod.lengthOfHours; }
			float mult = level != null && level.DaySpeedMultiplier > 0f ? level.DaySpeedMultiplier : 1f;
			return (Tod.globalTime - landGlobalTime) * mult / Tod.lengthOfHours;
		}

		private static float CycleHours => ModConfig.countingHours.Value + ModConfig.giftHours.Value;

		// "2 Days, 6 Hours", like the monitor's own placeholder "DEADLINE:\n7 Days,\n3 Hours"
		public static string FormatDeadline(float time, string separator = ", ")
		{
			int days = Mathf.Max((int)Mathf.Floor(time / Day), 0);
			string dayText = days == 1 ? "1 Day" : $"{days} Days";
			if (CountsWholeDays) { return dayText; }
			int hours = Mathf.Clamp((int)Mathf.Floor((time - days * Day) / Tod.lengthOfHours), 0, 23);
			return dayText + separator + Hours(hours);
		}

		private static string Hours(float hours) => $"{hours:0.#} {(hours == 1f ? "Hour" : "Hours")}";

		internal static bool IsMoonsPage(Terminal term, TerminalNode node, string rawText)
		{
			if (rawText.Contains("[planetTime]")) { return true; }
			TerminalKeyword? moons = term.terminalNodes?.allKeywords?.FirstOrDefault(k => k != null && k.word == "moons");
			return moons != null && node != null && node == moons.specialKeywordResult;
		}

		// "8 Titan" -> "8-Titan", as on the terminal's route pages
		private static string RouteName(SelectableLevel level) => Regex.Replace(level.PlanetName, @"^(\d+)\s+", "$1-");
		#endregion

		#region networking
		private static void StartOfRound_Start(On.StartOfRound.orig_Start orig, StartOfRound self)
		{
			orig(self);
			visitActive = false;
			activeMode = ModConfig.deadlineMode.Value;
			CustomMessagingManager? messaging = NetworkManager.Singleton?.CustomMessagingManager;
			if (messaging != null)
			{
				messaging.RegisterNamedMessageHandler(StateMessage, OnStateMessage);
				messaging.RegisterNamedMessageHandler(AlertMessage, OnAlertMessage);
			}
			// modded moons are added to the level list and terminal around now
			self.StartCoroutine(ScanMoonsDelayed());
		}

		private static void Broadcast()
		{
			dirty = false;
			nextForcedBroadcast = Time.time + 5f;
			if (Tod == null) { return; }
			Tod.UpdateProfitQuotaCurrentTime();
			if (!IsHost || NetworkManager.Singleton.CustomMessagingManager == null) { return; }
			using (FastBufferWriter writer = new FastBufferWriter(32, Allocator.Temp))
			{
				writer.WriteValueSafe((int)activeMode);
				writer.WriteValueSafe(MoonBlacklist.Active);
				writer.WriteValueSafe(Value);
				writer.WriteValueSafe(wipePenaltyHours);
				writer.WriteValueSafe(Tod.quotaVariables.deadlineDaysAmount);
				NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(StateMessage, writer, NetworkDelivery.Reliable);
			}
		}

		private static void OnStateMessage(ulong sender, FastBufferReader reader)
		{
			if (IsHost) { return; }
			reader.ReadValueSafe(out int mode);
			reader.ReadValueSafe(out hostVanillaDay);
			reader.ReadValueSafe(out float time);
			reader.ReadValueSafe(out wipePenaltyHours);
			reader.ReadValueSafe(out int deadlineDaysAmount);
			activeMode = (DeadlineMode)mode;
			if (Tod == null) { return; }
			Tod.quotaVariables.deadlineDaysAmount = deadlineDaysAmount; // host's, so the buying rate matches
			Value = time; // always 24h days
			Tod.UpdateProfitQuotaCurrentTime();
		}

		private static bool hostVanillaDay;

		// the game's own time sync carries the host's deadline on the host's day length: kept out while the two differ
		private static void TimeOfDay_SyncTimeClientRpc(On.TimeOfDay.orig_SyncTimeClientRpc orig, TimeOfDay self, float time, int deadline)
		{
			if (IsHost || hostVanillaDay == MoonBlacklist.Active) { orig(self, time, deadline); return; }
			float own = self.timeUntilDeadline;
			orig(self, time, deadline);
			self.timeUntilDeadline = own;
		}

		// sent by the host; each player's own toggles decide what they see
		private enum Alert : byte
		{
			AdvanceWarning, // pilot dialogue
			LowDeadline,    // red warning tip "Deadline alert!"
			DayDeducted,    // global notification
			TravelCost,     // global notification
			LandingCost,    // global notification
			ZeroWarning,    // pilot dialogue + ship-leaving icon
			ZeroDeadline,   // red warning tip "HALT!" (same toggle as LowDeadline)
		}

		private static void SendAlert(Alert alert, string text) => SendAlert((byte)alert, text);

		private static void SendAlert(byte kind, string text)
		{
			ShowAlert(kind, text);
			if (NetworkManager.Singleton?.CustomMessagingManager == null) { return; }
			using (FastBufferWriter writer = new FastBufferWriter(64 + text.Length * 4, Allocator.Temp))
			{
				writer.WriteValueSafe(kind);
				writer.WriteValueSafe(text);
				NetworkManager.Singleton.CustomMessagingManager.SendNamedMessageToAll(AlertMessage, writer, NetworkDelivery.Reliable);
			}
		}

		private static void OnAlertMessage(ulong sender, FastBufferReader reader)
		{
			if (IsHost) { return; }
			reader.ReadValueSafe(out byte kind);
			reader.ReadValueSafe(out string text);
			ShowAlert(kind, text);
		}

		private static void ShowAlert(byte kind, string text)
		{
			HUDManager hud = HUDManager.Instance;
			if (hud == null) { return; }
			switch ((Alert)kind)
			{
				case Alert.AdvanceWarning:
					if (ModConfig.notifyAdvanceWarning.Value) { PilotDialogue(hud, text); }
					break;
				case Alert.LowDeadline:
					if (ModConfig.notifyLowDeadline.Value) { hud.DisplayTip("Deadline alert!", text, isWarning: true); }
					break;
				case Alert.ZeroDeadline:
					if (ModConfig.notifyLowDeadline.Value) { hud.DisplayTip("HALT!", text, isWarning: true); }
					break;
				case Alert.DayDeducted:
					if (ModConfig.notifyDayDeducted.Value) { hud.DisplayGlobalNotification(text); }
					break;
				case Alert.TravelCost:
					if (ModConfig.notifyTravelCost.Value) { hud.DisplayGlobalNotification(text); }
					break;
				case Alert.LandingCost:
					if (ModConfig.notifyLandingCost.Value) { hud.DisplayGlobalNotification(text); }
					break;
				case Alert.ZeroWarning:
					// each mode has its own toggle; the host's mode decides which
					if ((activeMode == DeadlineMode.DayAndHour ? ModConfig.notifyZeroWarningDayAndHour : ModConfig.notifyZeroWarning).Value)
					{
						PilotDialogue(hud, text);
						hud.shipLeavingEarlyIcon.enabled = true;
					}
					break;
			}
		}

		private static void PilotDialogue(HUDManager hud, string text)
		{
			hud.ReadDialogue(new[] { new DialogueSegment { speakerText = "PILOT COMPUTER", bodyText = text, waitTime = 5f } });
		}
		#endregion

		#region landing time
		// landing frame, every machine: moon time at landing, for the hours spent and the alert times
		private static void TimeOfDay_Update(On.TimeOfDay.orig_Update orig, TimeOfDay self)
		{
			if (self.currentDayTimeStarted && self.timeStartedThisFrame && self.currentLevel != null && self.currentLevel.planetHasTime)
			{
				landGlobalTime = self.globalTime;
				landDayTime = self.CalculatePlanetTime(self.currentLevel); // vanilla day: the game's own moon time
				landingClock = (landDayTime / self.lengthOfHours + 6f) % 24f;
			}
			orig(self);
			if (IsHost && StartOfRound.Instance != null && !StartOfRound.Instance.isChallengeFile) { HostTick(); }
		}
		#endregion

		#region visit (host)
		private static void StartOfRound_OnShipLandedMiscEvents(On.StartOfRound.orig_OnShipLandedMiscEvents orig, StartOfRound self)
		{
			orig(self);
			if (!IsHost || self.isChallengeFile) { return; }

			activeMode = ModConfig.deadlineMode.Value;
			visitActive = true;
			leaving = reachedZero = leaveWarned = false;
			deductedCount = alertedCount = 0;
			chargedHours = 0f;
			wipePenaltyHours = 0f;
			landedAtZero = AtZero();
			SelectableLevel level = self.currentLevel;
			P.Log($"Deadline: landed on {level.PlanetName} ({activeMode}{(MoonBlacklist.Active ? ", vanilla day" : "")}), {FormatDeadline(Value)} left{(landedAtZero ? ", already at zero: quota is evaluated at takeoff" : "")}");

			orbitedLevelId = level.levelID;
			// travel and Company costs are charged on arrival in orbit
			if (activeMode == DeadlineMode.DayAndHour && !landedAtZero && level.planetHasTime
				&& ModConfig.landingPenaltyEnabled.Value && ModConfig.landingPenaltyHours.Value > 0f)
			{
				Deduct(ModConfig.landingPenaltyHours.Value * Tod.lengthOfHours, "landing penalty");
				Broadcast();
				SendAlert(Alert.LandingCost, $"Landing fee! Deadline -{Hours(ModConfig.landingPenaltyHours.Value)}.");
			}
			Broadcast();
		}

		// only a real move counts; a refused route never travels, so it never arrives
		private static int orbitedLevelId = -1;

		private static void StartOfRound_ArriveAtLevel(On.StartOfRound.orig_ArriveAtLevel orig, StartOfRound self)
		{
			orig(self);
			if (!IsHost || self.isChallengeFile || Tod == null || self.currentLevel == null) { return; }

			SelectableLevel level = self.currentLevel;
			bool moved = level.levelID != orbitedLevelId;
			orbitedLevelId = level.levelID;
			if (!moved || ModConfig.deadlineMode.Value != DeadlineMode.DayAndHour) { return; }

			if (!level.planetHasTime)
			{
				float cost = ModConfig.companyVisitHours.Value;
				if (cost > 0f)
				{
					Deduct(cost * Tod.lengthOfHours, "travel to the Company");
					Broadcast();
					SendAlert(Alert.TravelCost, $"Routed to the Company building! Deadline -{Hours(cost)}.");
				}
			}
			else if (ModConfig.travelPenaltyEnabled.Value)
			{
				ScanMoons();
				float travel = TravelHours(level);
				if (travel > 0f)
				{
					Deduct(travel * Tod.lengthOfHours, $"travel to {level.PlanetName}");
					Broadcast();
					SendAlert(Alert.TravelCost, $"Routed to {RouteName(level)}! Deadline -{Hours(travel)}.");
				}
			}
			Broadcast();
		}

		private static void HostTick()
		{
			if (!visitActive) { activeMode = ModConfig.deadlineMode.Value; }
			if (resetCheckAt >= 0f && Time.time >= resetCheckAt)
			{
				resetCheckAt = -1f;
				ApplyFreshDeadline(resetReason);
			}
			StartOfRound sor = StartOfRound.Instance;
			bool counting = visitActive && !MoonBlacklist.Active && !leaving && !landedAtZero && !reachedZero && sor.shipHasLanded && !sor.shipIsLeaving
				&& Tod.currentDayTimeStarted && Tod.currentLevel != null && Tod.currentLevel.planetHasTime;
			if (counting)
			{
				if (activeMode == DeadlineMode.Days) { TickDays(); }
				else if (activeMode == DeadlineMode.DayAndHour) { TickHours(); }
				else { DrainVisit(); }
			}
			if ((dirty && Time.time >= nextBroadcast) || Time.time >= nextForcedBroadcast)
			{
				nextBroadcast = Time.time + 1f;
				Broadcast();
			}
		}

		// Days mode from landing: counting hours, 1 day deducted, gift hours, repeat
		private static float NextDeductionHours => ModConfig.countingHours.Value + CycleHours * deductedCount;

		private static void TickDays()
		{
			float hours = HoursSinceLanding();
			while (hours >= NextDeductionHours && !reachedZero)
			{
				deductedCount++;
				Deduct(Day, $"{ModConfig.countingHours.Value:0.##} counting hours done");
				Broadcast();
				SendAlert(Alert.DayDeducted, $"1 day deducted! {FormatDeadline(Value)} left to meet the quota.");
			}
			if (reachedZero) { return; }

			float next = NextDeductionHours;
			if (alertedCount <= deductedCount && hours >= next - ModConfig.alertLeadHours.Value)
			{
				alertedCount = deductedCount + 1;
				string at = DayCycle.FormatHour((landingClock + next) % 24f);
				float after = Value - Day;
				int daysAfter = Mathf.Max((int)Mathf.Floor(after / Day), 0);
				if (after < Day)
				{
					SendAlert(Alert.ZeroWarning, $"WARNING!!! The deadline expires at {at} and the autopilot ship will leave. Please return quickly.");
				}
				else
				{
					SendAlert(Alert.AdvanceWarning, $"Alert! At {at}, one day will be deducted from your deadline.");
				}
				if (daysAfter <= 1)
				{
					if (daysAfter == 0) { SendAlert(Alert.ZeroDeadline, $"You will have 0 days left to meet the quota at {at}. Use the terminal to route to the company and sell."); }
					else { SendAlert(Alert.LowDeadline, $"Only 1 day will be left to meet the quota after {at}."); }
				}
			}
		}

		private static void ChargeHours()
		{
			float hours = HoursSinceLanding();
			if (hours > chargedHours)
			{
				Deduct((hours - chargedHours) * Tod.lengthOfHours, null);
				chargedHours = hours;
			}
		}

		// OneDayExtended: drains with the moon clock during the visit (max one day), the rest goes at takeoff
		private static void DrainVisit()
		{
			float hours = Mathf.Min(HoursSinceLanding(), 24f);
			if (hours > chargedHours)
			{
				Deduct((hours - chargedHours) * Tod.lengthOfHours, null);
				chargedHours = hours;
			}
		}

		private static void TickHours()
		{
			ChargeHours();
			float hours = chargedHours;
			if (!leaveWarned && !reachedZero)
			{
				float remaining = (Value - Leftover) / Tod.lengthOfHours;
				if (remaining <= ModConfig.dayAndHourAlertLeadHours.Value)
				{
					leaveWarned = true;
					string at = DayCycle.FormatHour((landingClock + hours + remaining) % 24f);
					SendAlert(Alert.ZeroWarning, $"WARNING!!! The deadline expires at {at} and the autopilot ship will leave. Please return quickly.");
				}
			}
		}

		// never below the leftover; reaching zero on a moon makes the ship leave
		private static void Deduct(float amount, string? reason)
		{
			float before = Value;
			Value = Mathf.Min(before, Mathf.Max(before - amount, Leftover));
			dirty = true;
			if (reason != null)
			{
				P.Log($"Deadline: -{amount / Tod.lengthOfHours:0.##}h ({reason}), {FormatDeadline(Value)} left");
			}
			RefreshBuyingRate(before, reason);
			if (reachedZero || !AtZero()) { return; }

			reachedZero = true;
			StartOfRound sor = StartOfRound.Instance;
			if (MoonBlacklist.Active) { P.Log("Deadline: reached zero on a vanilla day, no early leave"); }
			else if (activeMode == DeadlineMode.OneDayExtended) { P.Log("Deadline: reached zero during the visit, no early leave (Company trip still available)"); }
			else if (!leaving && Tod.currentLevel != null && Tod.currentLevel.planetHasTime && sor.shipHasLanded && !sor.shipIsLeaving)
			{
				P.Log("Deadline: reached zero on a moon, ship leaves (Company trip still available)");
				Tod.SetShipToLeaveOnMidnightClientRpc();
			}
		}

		// vanilla only sets the buying rate at the day change; a cost that drops a whole day before that
		// (travel or Company in orbit) would sell at the old rate, so refresh it
		private static void RefreshBuyingRate(float before, string? reason)
		{
			StartOfRound sor = StartOfRound.Instance;
			if (sor == null || sor.isChallengeFile) { return; }
			int daysBefore = Mathf.FloorToInt(before / Day);
			int daysAfter = Mathf.FloorToInt(Value / Day);
			if (daysBefore == daysAfter)
			{
				if (reason != null) { P.LogDebug($"Deadline: {before / Tod.lengthOfHours:0.##}h -> {Value / Tod.lengthOfHours:0.##}h, buying rate {sor.companyBuyingRate:P0} (same days)"); }
				return;
			}
			float rate = sor.companyBuyingRate;
			Tod.SetBuyingRateForDay();
			sor.SyncCompanyBuyingRateClientRpc(sor.companyBuyingRate);
			P.LogDebug($"Deadline: {before / Tod.lengthOfHours:0.##}h -> {Value / Tod.lengthOfHours:0.##}h ({reason ?? "hours on the moon"}), {daysBefore} -> {daysAfter} days, buying rate {rate:P0} -> {sor.companyBuyingRate:P0}");
		}

		private static void StartOfRound_ShipLeave(On.StartOfRound.orig_ShipLeave orig, StartOfRound self)
		{
			if (IsHost && P.DebugOn) { P.LogDebug($"Ship leave: {LeaveReason(self)}"); }
			if (IsHost && visitActive && !self.isChallengeFile) { EndVisit(self); }
			orig(self);
		}

		private static string LeaveReason(StartOfRound sor)
		{
			if (!sor.shipLeftAutomatically) { return "lever pulled"; }
			if (sor.allPlayersDead) { return "all players dead"; }
			if (reachedZero && !MoonBlacklist.Active) { return "deadline reached zero"; }
			if (MoonBlacklist.Active) { return Tod.shipLeaveAutomaticallyTime < 0.998f ? "leave-early vote (vanilla day)" : "midnight (vanilla day)"; } // a vote lowers it from 0.998
			return DayCycle.LeaveEarlyVoted(Tod) ? "leave-early vote" : "automatic (other cause)";
		}

		private static void EndVisit(StartOfRound sor)
		{
			leaving = true;
			visitActive = false;
			SelectableLevel level = sor.currentLevel;

			if (activeMode == DeadlineMode.OneDayExtended)
			{
				// exactly one day per visit: the rest of the day the visit drained (all of it on a vanilla day)
				if (!landedAtZero && level.planetHasTime)
				{
					if (!MoonBlacklist.Active) { DrainVisit(); }
					if (chargedHours < 24f) { Deduct((24f - chargedHours) * Tod.lengthOfHours, $"takeoff, rest of the visit's day ({chargedHours:0.##}h drained)"); }
				}
			}
			else if (!landedAtZero && level.planetHasTime && !reachedZero)
			{
				if (activeMode == DeadlineMode.Days)
				{
					// an undeducted counting period costs 1 day; the first starts at landing, so every visit costs at least 1
					int period = (int)Mathf.Floor(HoursSinceLanding() / CycleHours);
					if (period >= deductedCount)
					{
						deductedCount = period + 1;
						Deduct(Day, "takeoff");
					}
				}
				else
				{
					ChargeHours();
					P.Log($"Deadline: takeoff after {chargedHours:0.##}h on {level.PlanetName}, {FormatDeadline(Value)} left");
				}
			}

			// DayAndHour team wipe; never below the leftover, so the Company trip stays
			wipePenaltyHours = 0f;
			if (activeMode == DeadlineMode.DayAndHour && sor.allPlayersDead && !landedAtZero && level.planetHasTime
				&& ModConfig.teamWipePenaltyEnabled.Value && ModConfig.teamWipePenaltyHours.Value > 0f)
			{
				wipePenaltyHours = ModConfig.teamWipePenaltyHours.Value;
				Deduct(wipePenaltyHours * Tod.lengthOfHours, "team wipe");
			}

			// like vanilla: taking off while already at zero ends the deadline
			if (landedAtZero || (!level.planetHasTime && AtZero()))
			{
				Value = 0f;
				P.Log("Deadline: reached, quota is evaluated after this takeoff");
			}
			Broadcast();
		}

		// vanilla PassTimeToNextDay without its deadline spending
		private static void StartOfRound_PassTimeToNextDay(On.StartOfRound.orig_PassTimeToNextDay orig, StartOfRound self, int connectedPlayersOnServer)
		{
			MoonBlacklist.Exit();
			if (self.isChallengeFile) { orig(self, connectedPlayersOnServer); return; }

			TimeOfDay tod = Tod;
			if (self.currentLevel.planetHasTime || tod.timeUntilDeadline <= 0f) { tod.OnDayChanged(); }
			tod.globalTime = 100f;
			if (self.IsServer) { Landing.Roll(); } // before the map screen is rebuilt
			tod.UpdateProfitQuotaCurrentTime();
			if (self.currentLevel.planetHasTime)
			{
				HUDManager.Instance.DisplayDaysLeft((int)Mathf.Floor(tod.timeUntilDeadline / tod.totalTime));
			}
			Term?.SetItemSales();
			self.SetMapScreenInfoToCurrentLevel();
			if (tod.timeUntilDeadline > 0f && tod.daysUntilDeadline <= 0 && tod.timesFulfilledQuota <= 0)
			{
				self.StartCoroutine(self.playDaysLeftAlertSFXDelayed());
			}
		}
		#endregion

		#region display
		private static void TimeOfDay_UpdateProfitQuotaCurrentTime(On.TimeOfDay.orig_UpdateProfitQuotaCurrentTime orig, TimeOfDay self)
		{
			orig(self);
			if (!MoonBlacklist.Active && activeMode == DeadlineMode.DayAndHour && StartOfRound.Instance != null && !StartOfRound.Instance.isChallengeFile && self.timeUntilDeadline > 0f)
			{
				StartOfRound.Instance.deadlineMonitorText.text = $"DEADLINE:\n{FormatDeadline(self.timeUntilDeadline, ",\n")}";
			}
		}

		private static void HUDManager_DisplayDaysLeft(On.HUDManager.orig_DisplayDaysLeft orig, HUDManager self, int daysLeft)
		{
			orig(self, daysLeft);
			if (!MoonBlacklist.Active && activeMode == DeadlineMode.DayAndHour && daysLeft >= 0)
			{
				string text = $"{FormatDeadline(Value)} Left";
				if (wipePenaltyHours > 0f) { text += $"\nCrew lost: -{Hours(wipePenaltyHours)}"; }
				self.profitQuotaDaysLeftText.text = text;
				self.profitQuotaDaysLeftText2.text = text;
			}
		}

		private static string Terminal_TextPostProcess(On.Terminal.orig_TextPostProcess orig, Terminal self, string modifiedDisplayText, TerminalNode node)
		{
			bool moonsPage = IsMoonsPage(self, node, modifiedDisplayText);
			string text = orig(self, modifiedDisplayText, node);
			if (!moonsPage || activeMode == DeadlineMode.OneDayExtended || MoonBlacklist.Active || Tod == null || StartOfRound.Instance == null || StartOfRound.Instance.isChallengeFile) { return text; }

			string line = Value <= 0f ? "Deadline: NOW." : $"Deadline: {FormatDeadline(Value)}.";
			// under the header's "____" line (vanilla and LethalLevelLoader), away from the credits counter
			Match separator = Regex.Match(text, @"_{5,}[ \t]*\r?\n");
			if (separator.Success) { return text.Insert(separator.Index + separator.Length, line + "\n"); }
			int start = 0;
			while (start < text.Length && (text[start] == '\n' || text[start] == '\r')) { start++; }
			return text.Insert(start, "\n" + line + "\n\n");
		}
		#endregion

		#region moons
		private static IEnumerator ScanMoonsDelayed()
		{
			yield return new WaitForSeconds(3f);
			if (ModConfig.travelPenaltyEnabled.Value) { ScanMoons(); }
		}

		// moons with a clock that the terminal can route to
		internal static List<SelectableLevel> RoutableMoons()
		{
			if (routePrices.Count == 0) { ScanRoutes(); }
			StartOfRound sor = StartOfRound.Instance;
			List<SelectableLevel> moons = new List<SelectableLevel>();
			if (sor == null) { return moons; }
			foreach (int id in routePrices.Keys)
			{
				if (id < sor.levels.Length && sor.levels[id] != null && sor.levels[id].planetHasTime) { moons.Add(sor.levels[id]); }
			}
			return moons;
		}

		internal static void RebuildMoonList()
		{
			routePrices.Clear();
			ScanMoons();
		}

		// default by route price
		private static void ScanMoons()
		{
			ScanRoutes();
			int moons = 0;
			foreach (SelectableLevel level in RoutableMoons())
			{
				int price = routePrices[level.levelID];
				ModConfig.MoonTravelHours(level.PlanetName, DefaultTravelHours(price), price);
				moons++;
			}
			P.Log($"Deadline: travel hours configured for {moons} moons");
		}

		private static void ScanRoutes()
		{
			Terminal? term = Term;
			if (StartOfRound.Instance == null || term == null || term.terminalNodes == null || term.terminalNodes.allKeywords == null) { return; }

			routePrices.Clear();
			TerminalKeyword route = term.terminalNodes.allKeywords.FirstOrDefault(k => k != null && k.word == "route");
			if (route?.compatibleNouns == null) { return; }
			foreach (CompatibleNoun noun in route.compatibleNouns)
			{
				TerminalNode prompt = noun?.result!;
				if (prompt?.terminalOptions == null) { continue; }
				foreach (CompatibleNoun option in prompt.terminalOptions)
				{
					TerminalNode confirm = option?.result!;
					if (confirm == null || confirm.buyRerouteToMoon < 0) { continue; }
					int price = Mathf.Max(confirm.itemCost, prompt.itemCost);
					routePrices[confirm.buyRerouteToMoon] = routePrices.TryGetValue(confirm.buyRerouteToMoon, out int known) ? Mathf.Max(known, price) : price;
				}
			}
		}

		// free = 1h, paid = 2h, 1000+ credits = 3h
		private static float DefaultTravelHours(int price)
		{
			return price <= 0 ? 1f : price < 1000 ? 2f : 3f;
		}

		private static float TravelHours(SelectableLevel level)
		{
			int price = routePrices.TryGetValue(level.levelID, out int p) ? p : 0;
			return ModConfig.MoonTravelHours(level.PlanetName, DefaultTravelHours(price), price)?.Value ?? 0f; // null: penalty off
		}
		#endregion
	}
}
