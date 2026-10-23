
using BepInEx.Configuration;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace DayCycle
{
	public enum ClockIcons { Original, DayCycle }

	public static class DayCycle
	{
		public static Animator sunAnim;
		internal static Animator? fallbackSun; // this landing's sun when it has no 24h version
		internal static void Patch()
		{
			Hook("TimeOfDay.Awake", () => On.TimeOfDay.Awake += TimeOfDay_Awake);
			Hook("TimeOfDay.Start", () => On.TimeOfDay.Start += TimeOfDay_Start);
			Hook("TimeOfDay.MoveGlobalTime", () => On.TimeOfDay.MoveGlobalTime += TimeOfDay_MoveGlobalTime);
			Hook("TimeOfDay.CalculatePlanetTime", () => On.TimeOfDay.CalculatePlanetTime += TimeOfDay_CalculatePlanetTime);
			Hook("TimeOfDay.CalculatePlanetTimeClampToEndOfDay", () => On.TimeOfDay.CalculatePlanetTimeClampToEndOfDay += TimeOfDay_CalculatePlanetTimeClampToEndOfDay);
			Hook("TimeOfDay.MoveTimeOfDay (IL)", () => IL.TimeOfDay.MoveTimeOfDay += TimeOfDay_MoveTimeOfDay);
			Hook("TimeOfDay.TimeOfDayEvents (IL)", () => IL.TimeOfDay.TimeOfDayEvents += TimeOfDay_TimeOfDayEvents);
			Hook("TimeOfDay.GetDayPhase", () => On.TimeOfDay.GetDayPhase += TimeOfDay_GetDayPhase);
			Hook("TimeOfDay.SetShipLeaveEarlyClientRpc", () => On.TimeOfDay.SetShipLeaveEarlyClientRpc += TimeOfDay_SetShipLeaveEarlyClientRpc);
			Hook("TimeOfDay.SetShipLeaveEarlyServerRpc (IL)", () => IL.TimeOfDay.SetShipLeaveEarlyServerRpc += TimeOfDay_SetShipLeaveEarlyServerRpc);

			// normalizedTimeOfDay Patches
			Hook("EnemyAI.CheckTimeOfDayToLeave (IL)", () => IL.EnemyAI.CheckTimeOfDayToLeave += EnemyAI_CheckTimeOfDayToLeave);
			Hook("RoundManager.SpawnRandomDaytimeEnemy (IL)", () => IL.RoundManager.SpawnRandomDaytimeEnemy += RoundManager_SpawnRandomDaytimeEnemy);
			Hook("PlayerControllerB.IVisibleThreat_GetThreatLevel (IL)", () => IL.GameNetcodeStuff.PlayerControllerB.IVisibleThreat_GetThreatLevel += PlayerControllerB_IVisibleThreat_GetThreatLevel);
			Hook("RoundManager.AdvanceHourAndSpawnNewBatchOfEnemies (IL)", () => IL.RoundManager.AdvanceHourAndSpawnNewBatchOfEnemies += RoundManager_AdvanceHourAndSpawnNewBatchOfEnemies);

			// spawn rate curve patches
			Hook("RoundManager.PlotOutEnemiesForNextHour (IL)", () => IL.RoundManager.PlotOutEnemiesForNextHour += RoundManager_PlotOutEnemiesForNextHour);
			Hook("RoundManager.PredictAllOutsideEnemies (IL)", () => IL.RoundManager.PredictAllOutsideEnemies += RoundManager_PredictAllOutsideEnemies);
			Hook("RoundManager.SpawnEnemiesOutside (IL)", () => IL.RoundManager.SpawnEnemiesOutside += RoundManager_SpawnEnemiesOutside);
			Hook("RoundManager.SpawnRandomOutsideEnemy (IL)", () => IL.RoundManager.SpawnRandomOutsideEnemy += RoundManager_SpawnRandomOutsideEnemy);
			Hook("RoundManager.SpawnRandomWeedEnemy (IL)", () => IL.RoundManager.SpawnRandomWeedEnemy += RoundManager_SpawnRandomWeedEnemy);
			Hook("RoundManager.SpawnDaytimeEnemiesOutside (IL)", () => IL.RoundManager.SpawnDaytimeEnemiesOutside += RoundManager_SpawnDaytimeEnemiesOutside);
			Hook("RoundManager.AssignRandomEnemyToVent (IL)", () => IL.RoundManager.AssignRandomEnemyToVent += RoundManager_AssignRandomEnemyToVent);
			Hook("RoundManager.SpawnInsideEnemiesFromVentsIfReady (IL)", () => IL.RoundManager.SpawnInsideEnemiesFromVentsIfReady += RoundManager_SpawnInsideEnemiesFromVentsIfReady);
			Hook("EnemyVent.Update (IL)", () => IL.EnemyVent.Update += EnemyVent_Update);

			// clock
			Hook("HUDManager.SetClock", () => On.HUDManager.SetClock += HUDManager_SetClock);
			Hook("HUDManager.GetClockTimeFormatted", () => On.HUDManager.GetClockTimeFormatted += HUDManager_GetClockTimeFormatted);
			Hook("HUDManager.SetClockIcon", () => On.HUDManager.SetClockIcon += HUDManager_SetClockIcon);
			Hook("TimeOfDay.TimeOfDayEvents", () => On.TimeOfDay.TimeOfDayEvents += TimeOfDay_TimeOfDayEvents1); // PlayerSeesNewTimeOfDay() patch
			Hook("TimeOfDay.Update", () => On.TimeOfDay.Update += TimeOfDay_Update);
			ModConfig.clockIcons.SettingChanged += (_, _) => phasesChanged = true;

			//sun animator swap
			Hook("animatedSun.Start", () => On.animatedSun.Start += AnimatedSun_Start);

			P.Log("Patched");
		}

		// a failing hook (e.g. IL changed by a game update) must not stop the others
		internal static void Hook(string name, Action apply)
		{
			try { apply(); }
			catch (Exception e) { P.Fail($"hook {name} failed: {e.GetType().Name}: {e.Message}"); }
		}

		// vanilla only stings when dayMode goes up, which never happens on a looping day; count phase starts forward
		// (wraps * starts + starts passed) so each change fires once and a time-sync rollback never replays one;
		// starts, not phases, so it also works for the original phases (their day begins at 3 AM)
		private static int phaseWraps;
		private static float previousPhaseTime = -1f;
		private static int lastSeenPhase = int.MinValue;
		private static bool phasesChanged;

		private static void TimeOfDay_TimeOfDayEvents1(On.TimeOfDay.orig_TimeOfDayEvents orig, TimeOfDay self)
		{
			if (MoonBlacklist.Active) { orig(self); return; }
			float time = ((self.currentDayTime / self.totalTime) % 1f + 1f) % 1f;
			if (previousPhaseTime >= 0f)
			{
				if (time < previousPhaseTime - 0.5f) { phaseWraps++; }
				else if (time > previousPhaseTime + 0.5f) { phaseWraps--; }  // synced back across 6 AM
			}
			previousPhaseTime = time;
			float[] starts = PhaseStarts;
			int phase = phaseWraps * starts.Length;
			foreach (float start in starts) { if (time >= start) { phase++; } }

			DayMode seen = self.dayModeLastTimePlayerWasOutside;
			if (seen == DayMode.None) { lastSeenPhase = int.MinValue; } // reset on landing
			else if (phasesChanged) { lastSeenPhase = phase; seen = self.GetDayPhase(time); } // "Clock icons" switched: no cue, the icon follows below
			phasesChanged = false;
			bool newPhase = phase > lastSeenPhase;
			// None lets vanilla's check fire, Midnight blocks it
			self.dayModeLastTimePlayerWasOutside = newPhase ? DayMode.None : DayMode.Midnight;
			orig(self);
			if (newPhase && self.dayModeLastTimePlayerWasOutside != DayMode.None) { lastSeenPhase = phase; }
			else { self.dayModeLastTimePlayerWasOutside = seen; }

			RefreshClockIcon(self);
		}

		
		private static void AnimatedSun_Start(On.animatedSun.orig_Start orig, animatedSun self)
		{
			sunAnim = self.gameObject.GetComponent<Animator>();
			fallbackSun = null;
			string sunAnimName = sunAnim.runtimeAnimatorController.name;
			SelectableLevel? level = StartOfRound.Instance != null ? StartOfRound.Instance.currentLevel : null;

			if (MoonBlacklist.Active)
			{
				P.Log($"Sun: {sunAnimName} unchanged (vanilla day)");
			}
			else if (API.sunAnimators.TryGetValue(sunAnimName, out RuntimeAnimatorController newAnimator))
			{
				sunAnim.runtimeAnimatorController = newAnimator;
				P.Log($"Sun: replaced {sunAnimName} with 24h version");
			}
			else if (level != null && !level.planetHasTime)
			{
				P.Log($"Sun: {sunAnimName} unchanged (no clock)");
			}
			else
			{
				fallbackSun = sunAnim;
				P.Log($"Sun: fallback for {sunAnimName}");
			}

			orig(self);
		}

		// the save loads before TimeOfDay.Start, so set 24h here or a new save's deadline is in 18h days
		private static void TimeOfDay_Awake(On.TimeOfDay.orig_Awake orig, TimeOfDay self)
		{
			if (self.numberOfHours > 0 && self.numberOfHours != 24) { MoonBlacklist.VanillaHours = self.numberOfHours; }
			self.numberOfHours = 24;
			self.totalTime = self.lengthOfHours * self.numberOfHours;
			orig(self);
		}

		private static void TimeOfDay_Start(On.TimeOfDay.orig_Start orig, TimeOfDay self)
		{
			self.numberOfHours = 24;

			orig(self);
		}

		#region TimeofDay
		private static void TimeOfDay_MoveGlobalTime(On.TimeOfDay.orig_MoveGlobalTime orig, TimeOfDay self)
		{
			if (MoonBlacklist.Active) { orig(self); return; }
			// unclamped so time loops; Deadline spends the deadline
			self.globalTime += Time.deltaTime * self.globalTimeSpeedMultiplier;
		}

		private static float TimeOfDay_CalculatePlanetTime(On.TimeOfDay.orig_CalculatePlanetTime orig, TimeOfDay self, SelectableLevel level)
		{
			if (MoonBlacklist.Active) { return orig(self, level); }
			return (self.globalTime + level.OffsetFromGlobalTime) * level.DaySpeedMultiplier % self.totalTime; // exactly 24h (vanilla wraps at totalTime + 1)
		}
		private static float TimeOfDay_CalculatePlanetTimeClampToEndOfDay(On.TimeOfDay.orig_CalculatePlanetTimeClampToEndOfDay orig, TimeOfDay self, SelectableLevel level)
		{
			if (MoonBlacklist.Active) { return orig(self, level); }
			return (self.globalTime + level.OffsetFromGlobalTime) * level.DaySpeedMultiplier % self.totalTime; // exactly 24h (vanilla wraps at totalTime + 1)
		}
		private static void TimeOfDay_MoveTimeOfDay(ILContext il)
		{
			ILCursor c = new ILCursor(il);
			// make hour infinitely increment by swapping currentDayTime with globalTime
			if (c.TryGotoNext(MoveType.After, x => x.MatchStfld<TimeOfDay>(nameof(TimeOfDay.currentDayTime))
				, x => x.MatchLdarg(0), x => x.MatchLdarg(0)
			))
			{
				// IL_0034: ldarg.0
				c.Remove(); // IL_0035: ldfld float32 TimeOfDay::currentDayTime REMOVES
				c.EmitDelegate<Func<TimeOfDay, float>>(SpawnDayTime);
			}
			else { P.Fail("cant find the hour"); }

			// STOP CLAMPING TIME
			if (c.TryGotoNext(MoveType.After, x => x.MatchCall<Mathf>(nameof(Mathf.Clamp))))
			{
				//IL_00b1: call float32 [UnityEngine.CoreModule]UnityEngine.Mathf::Clamp(float32, float32, float32)
				c.Emit(OpCodes.Pop);
				c.Emit(OpCodes.Ldarg_0);
				c.EmitDelegate<Func<TimeOfDay, float>>(SunTime);
				//IL_00b6: callvirt instance void [UnityEngine.AnimationModule]UnityEngine.Animator::SetFloat(string, float32)
			}
			else { P.Fail("can't patch sunAnimator.."); }

			// configurable time update interval
			if (c.TryGotoNext(MoveType.Before, x => x.MatchLdcR4(3f)))
			{
				c.Remove();
				c.EmitDelegate<Func<float>>(() => { return MoonBlacklist.Active ? 3f : ModConfig.intervalSetting.Value; });

			} else { P.Fail("cant patch interval..."); }
		}
		// fallback suns use vanilla's 6 AM - midnight range (capped at 0.99 like vanilla) and hold the night pose after
		private static float SunTime(TimeOfDay tod)
		{
			if (MoonBlacklist.Active) { return Mathf.Clamp(tod.normalizedTimeOfDay, 0f, 0.99f); }
			float normalized = ((tod.normalizedTimeOfDay % 1f) + 1f) % 1f;
			if (fallbackSun == null || tod.sunAnimator != fallbackSun) { return normalized; }
			float hoursSince6AM = normalized * 24f;
			return Mathf.Min(hoursSince6AM / 18f, 0.99f);
		}

		private static void TimeOfDay_TimeOfDayEvents(ILContext il)
		{
			ILCursor c = new ILCursor(il);
			if (c.TryGotoNext(MoveType.After, x => x.MatchLdcR4(0.9f))) // shipLeavingAlert patch
			{
				c.Emit(OpCodes.Pop);
				c.Emit(OpCodes.Pop);
				c.Emit(OpCodes.Ldarg_0);
				c.EmitDelegate<Func<TimeOfDay, bool>>(
					(self) => {
						if (MoonBlacklist.Active) { return self.currentDayTime / self.totalTime > 0.9f; }
						if (self.globalTime > float.MaxValue - 108) { return true; }
						return false;
					}
				);
				//c.Index += 1;
				c.Next.OpCode = OpCodes.Brfalse_S;
			}
			else { P.Fail("event the hell!!!"); }

			if (c.TryGotoNext(MoveType.Before, x => x.MatchBltUn(out _))) // shipLeavingOnMidnight patch
			{
				c.Emit(OpCodes.Pop);
				c.Emit(OpCodes.Pop);
				c.Emit(OpCodes.Ldarg_0);
				c.EmitDelegate<Func<TimeOfDay, bool>>(
					(self) => {
						if (MoonBlacklist.Active) { return self.currentDayTime / self.totalTime >= self.shipLeaveAutomaticallyTime; }
						if (self.globalTime >= float.MaxValue - 4.32) { return true; }
						if (LeaveEarlyVoted(self) && PlanetTimeUnwrapped(self) >= leaveEarlyAt)
						{
							P.LogDebug("Ship leave: leave-early vote time reached");
							return true;
						}
						return false;
					}
				);
				//c.Index += 1;
				c.Next.OpCode = OpCodes.Brfalse_S;
			}
			else { P.Fail("event the hell TWO!!!"); }
		}
		// leave-early vote: vanilla's check is the one the midnight patch kills, and the time can be past the wrap,
		// so keep it on unwrapped planet time
		private static float leaveEarlyFor = float.NaN, leaveEarlyAt;

		// the game resets shipLeaveAutomaticallyTime on every landing, which ends an old vote
		internal static bool LeaveEarlyVoted(TimeOfDay tod) => !float.IsNaN(leaveEarlyFor) && tod.shipLeaveAutomaticallyTime == leaveEarlyFor;

		// host: vanilla leaves 0.1 of its 18h day (1.8 h) after the vote, same hours on 24h (the warning reads it too)
		private static void TimeOfDay_SetShipLeaveEarlyServerRpc(ILContext il)
		{
			ILCursor c = new ILCursor(il);
			if (c.TryGotoNext(MoveType.After, x => x.MatchLdfld<TimeOfDay>(nameof(TimeOfDay.normalizedTimeOfDay))) && c.Next.MatchLdcR4(out float vanillaValue))
			{
				c.Remove();
				c.EmitDelegate<Func<float>>(() => MoonBlacklist.Active ? vanillaValue : vanillaValue * MoonBlacklist.Scale);
			}
			else { P.Fail("leave-early vote time not found"); }
		}

		private static void TimeOfDay_SetShipLeaveEarlyClientRpc(On.TimeOfDay.orig_SetShipLeaveEarlyClientRpc orig, TimeOfDay self, float timeToLeaveEarly, int votes)
		{
			orig(self, timeToLeaveEarly, votes);
			// only the call that applied it (the host also passes here when sending)
			if (MoonBlacklist.Active || self.shipLeaveAutomaticallyTime != timeToLeaveEarly || LeaveEarlyVoted(self)) { return; }
			leaveEarlyFor = timeToLeaveEarly;
			leaveEarlyAt = PlanetTimeUnwrapped(self) + (timeToLeaveEarly - self.normalizedTimeOfDay) * self.totalTime; // the time in "Please return by X"
			P.LogDebug($"Ship leave: vote passed with {votes} votes, leaving at {FormatHour((leaveEarlyAt / self.lengthOfHours + 6f) % 24f)}");
		}
		public static Dictionary<DayMode, float> phaseTimes = new Dictionary<DayMode, float>(){
			//{ "dawn", 0f/24f }, // 6:00 AM -  11:59 AM
			{ DayMode.Noon, 6f/24f }, // 12:00 PM - 5:20 PM
			{ DayMode.Sundown, 11.35f/24f }, // 5:21 PM - 11:11 PM
			{ DayMode.Midnight, 16.2f/24f }, // 11:12 PM - 2:59 AM
			{ DayMode.Dawn, 21f/24f } // 3:00 AM - 5:59 AM ( time is offset +6 so it hits 24 here )
		};
		// "Clock icons" = DayCycle: hours since 6 AM / 24, before Noon is Dawn
		public static Dictionary<DayMode, float> dayCyclePhaseTimes = new Dictionary<DayMode, float>(){
			{ DayMode.Noon, 5f/24f }, // 11:00 AM
			{ DayMode.Sundown, 14f/24f }, // 8:00 PM
			{ DayMode.Midnight, 18f/24f }, // 12:00 AM
		};
		private static bool NewIcons => ModConfig.clockIcons.Value == ClockIcons.DayCycle;

		// where each phase begins in the day (0 = 6 AM), for the sting
		private static readonly float[] originalPhaseStarts = { phaseTimes[DayMode.Noon], phaseTimes[DayMode.Sundown], phaseTimes[DayMode.Midnight], phaseTimes[DayMode.Dawn] };
		private static readonly float[] dayCyclePhaseStarts = { 0f, dayCyclePhaseTimes[DayMode.Noon], dayCyclePhaseTimes[DayMode.Sundown], dayCyclePhaseTimes[DayMode.Midnight] };
		private static float[] PhaseStarts => NewIcons ? dayCyclePhaseStarts : originalPhaseStarts;

		private static DayMode TimeOfDay_GetDayPhase(On.TimeOfDay.orig_GetDayPhase orig, TimeOfDay self, float time)
		{
			if (MoonBlacklist.Active) { return orig(self, time); }
			if (NewIcons)
			{
				time = ((time % 1f) + 1f) % 1f;
				if (time >= dayCyclePhaseTimes[DayMode.Midnight]) { return DayMode.Midnight; }
				if (time >= dayCyclePhaseTimes[DayMode.Sundown]) { return DayMode.Sundown; }
				if (time >= dayCyclePhaseTimes[DayMode.Noon]) { return DayMode.Noon; }
				return DayMode.Dawn;
			}
			if (time >= phaseTimes[DayMode.Dawn]) { return DayMode.Dawn; } 
			else if (time >= phaseTimes[DayMode.Midnight]) { return DayMode.Midnight; } 
			else if (time >= phaseTimes[DayMode.Sundown]) { return DayMode.Sundown; } 
			else if (time >= phaseTimes[DayMode.Noon]) { return DayMode.Noon; }	
			else { return DayMode.Dawn; }
		}

		// HUDManager.clockIcons: 0 full sun, 1 half sun, 2 crescent moon, 3 skull
		private const float AfternoonIconHour = 16f;

		private static int ClockIconIndex(DayMode dayMode)
		{
			// original: each phase's own icon, the half sun from 3 AM
			if (!NewIcons) { return dayMode == DayMode.Dawn && TimeOfDay.Instance.normalizedTimeOfDay >= phaseTimes[DayMode.Dawn] ? (int)DayMode.Noon : (int)dayMode; }
			switch (dayMode)
			{
				case DayMode.Dawn: return 1;
				case DayMode.Noon:
					float clock = ((TimeOfDay.Instance.normalizedTimeOfDay % 1f + 1f) % 1f * 24f + 6f) % 24f;
					return clock >= 11f && clock < AfternoonIconHour ? 0 : 1;
				case DayMode.Sundown: return 2;
				case DayMode.Midnight: return 3;
				default: return (int)dayMode;
			}
		}

		// icon changes inside a phase (original: 6 AM, DayCycle: 4 PM) aren't refreshed by vanilla; outside only, like vanilla
		private static void RefreshClockIcon(TimeOfDay tod)
		{
			HUDManager hud = HUDManager.Instance;
			var player = GameNetworkManager.Instance?.localPlayerController;
			if (hud == null || player == null || tod.dayModeLastTimePlayerWasOutside != tod.dayMode) { return; }
			if (player.isInsideFactory || player.isInHangarShipRoom || !StartOfRound.Instance.shipHasLanded) { return; }
			if (hud.clockIcons == null || hud.clockIcons.Length < 4) { return; }
			if (hud.clockIcon.sprite != hud.clockIcons[ClockIconIndex(tod.dayMode)]) { hud.SetClockIcon(tod.dayMode); }
		}

		// the landing frame sets the icon from dayMode, still the last round's
		private static void TimeOfDay_Update(On.TimeOfDay.orig_Update orig, TimeOfDay self)
		{
			if (!MoonBlacklist.Active && self.currentDayTimeStarted && self.timeStartedThisFrame && self.currentLevel != null && self.currentLevel.planetHasTime)
			{
				self.dayMode = self.GetDayPhase(self.CalculatePlanetTime(self.currentLevel) / self.totalTime);
			}
			orig(self);
		}
		#endregion

		#region normalizedTimeOfDay
		private static void PlayerControllerB_IVisibleThreat_GetThreatLevel(ILContext il)
		{
			// < 0.2 -> < 0.15 (& new  > 0.9)
			ILCursor c = new ILCursor(il);
			if (c.TryGotoNext(MoveType.After, x => x.MatchLdfld<TimeOfDay>(nameof(TimeOfDay.normalizedTimeOfDay))))
			{
				c.Remove();
				c.EmitDelegate<Func<float, bool>>((norm) => { return MoonBlacklist.Active ? norm < 0.2f : (norm < 0.15f || norm >= 0.9f); });
				c.Next.OpCode = OpCodes.Brfalse;
			}
			else { P.Fail("coundt find threat..."); }

			// > 0.8 -> > 0.6 (and before dawn at 0.9)
			if (c.TryGotoNext(MoveType.After, x => x.MatchLdfld<TimeOfDay>(nameof(TimeOfDay.normalizedTimeOfDay))))
			{
				c.Remove();
				c.EmitDelegate<Func<float, bool>>((norm) => { return MoonBlacklist.Active ? norm > 0.8f : (norm >= 0.6f && norm < 0.9f); });
				c.Next.OpCode = OpCodes.Brfalse;
			}
			else { P.Fail("coundt find threat 2..."); }
		}
		private static void EnemyAI_CheckTimeOfDayToLeave(ILContext il)
		{
			ILCursor c = new ILCursor(il);
			if (c.TryGotoNext(MoveType.Before, x => x.MatchLdfld<TimeOfDay>(nameof(TimeOfDay.normalizedTimeOfDay))))
			{
				c.Remove();
				c.EmitDelegate<Func<TimeOfDay, float>>(LeaveTime);
			}
			else { P.Fail("cant check!!"); }
		}
		private static void RoundManager_SpawnRandomDaytimeEnemy(ILContext il)
		{
			// both "too late in the day" checks, not just the first
			ILCursor c = new ILCursor(il);
			int patched = 0;
			while (c.TryGotoNext(MoveType.Before, x => x.MatchLdfld<TimeOfDay>(nameof(TimeOfDay.normalizedTimeOfDay))))
			{
				c.Remove();
				c.EmitDelegate<Func<TimeOfDay, float>>(LeaveTime);
				patched++;
			}
			if (patched < 2) { P.Fail("cant spawn random daytona!!"); }
			// the per-type curve too, its argument grows past one day
			if (RemapDayCurves(il) < 1) { P.Fail("daytime probability curve not remapped"); }
		}
		private static void RoundManager_AdvanceHourAndSpawnNewBatchOfEnemies(ILContext il)
		{
			ILCursor c = new ILCursor(il);
			if (c.TryGotoNext(MoveType.After, x => x.MatchLdfld<TimeOfDay>(nameof(TimeOfDay.normalizedTimeOfDay))) && c.Next.Operand is float vanillaValue)
			{
				c.Remove();
				c.EmitDelegate<Func<float>>(() => MoonBlacklist.Active ? vanillaValue : 0.225f);
			}
			else { P.Fail("advance rejected.."); }
		}

		// vanilla's 18h fraction on the 24h day
		private static float LeaveTime(TimeOfDay tod) => MoonBlacklist.Active ? tod.normalizedTimeOfDay : tod.normalizedTimeOfDay * (4.0f / 3f);
		#endregion

		#region RoundManager
		private static void RoundManager_SpawnDaytimeEnemiesOutside(ILContext il)
		{
			ILCursor c = new ILCursor(il);
			//  currentLevel.daytimeEnemySpawnChanceThroughDay
			if (c.TryGotoNext(MoveType.Before, x => x.MatchCallvirt<AnimationCurve>(nameof(AnimationCurve.Evaluate))))
			{
				c.Emit(OpCodes.Ldarg_0);    // self
				c.Emit(OpCodes.Ldloc_0);    // num (basically timeUpToCurrentHour)
				c.EmitDelegate<Func<float, RoundManager, float, float>>((vanilla, rm, num) => {
					if (MoonBlacklist.Active) { return LogCurve(rm, "daytime", rm.currentLevel.daytimeEnemySpawnChanceThroughDay, vanilla); }
					float val = (num % rm.timeScript.totalTime) / rm.timeScript.totalTime;
					return LogCurve(rm, "daytime", rm.currentLevel.daytimeEnemySpawnChanceThroughDay, val < 0.75f ? val * (4.0f / 3.0f) : 1 - (val - 0.75f) * 4);
				});
			}
			else { P.Fail("day broken"); }
		}
		private static void RoundManager_SpawnRandomWeedEnemy(ILContext il)
		{
			//  enemyType.probabilityCurve (with and without numberSpawnedFalloff)
			if (RemapDayCurves(il) < 2) { P.Fail("no weed"); }
		}
		private static void RoundManager_SpawnEnemiesOutside(ILContext il)
		{
			ILCursor c = new ILCursor(il);
			// currentLevel.outsideEnemySpawnChanceThroughDay
			if (c.TryGotoNext(MoveType.Before, x => x.MatchCallvirt<AnimationCurve>(nameof(AnimationCurve.Evaluate))))
			{
				c.Emit(OpCodes.Ldarg_0);    // self
				c.Emit(OpCodes.Ldloc_0);    // num
				c.EmitDelegate<Func<float, RoundManager, float, float>>((vanilla, rm, num) => // num keeps growing past one day
					LogCurve(rm, "outside", rm.currentLevel.outsideEnemySpawnChanceThroughDay, MoonBlacklist.Active ? vanilla : DayCurve(num / rm.timeScript.totalTime)));
			}
			else { P.Fail("can't go outside"); }
		}
		private static void RoundManager_SpawnRandomOutsideEnemy(ILContext il)
		{
			// enemyType.probabilityCurve (with and without numberSpawnedFalloff); its argument grows past one day
			if (RemapDayCurves(il) < 2) { P.Fail("outside probability curve not remapped"); }
		}

		// hourly, host: the spawn curve's input and value, with the group's power and diversity budget
		private static float LogCurve(RoundManager rm, string group, AnimationCurve curve, float time)
		{
			if (!P.DebugOn || curve == null) { return time; }
			TimeOfDay tod = rm.timeScript;
			string clock = FormatHour((tod.normalizedTimeOfDay * tod.numberOfHours + 6f) % 24f);
			string budget = group == "outside" ? $"power {rm.currentOutsideEnemyPower:0.#} (no deaths {rm.currentOutsideEnemyPowerNoDeaths:0.#}) / {rm.currentMaxOutsidePower:0.#}, diversity {rm.currentOutsideEnemyDiversityLevel} / {rm.currentMaxOutsideDiversityLevel}"
				: group == "inside" ? $"power {rm.currentEnemyPower:0.#} (no deaths {rm.currentEnemyPowerNoDeaths:0.#}) / {rm.currentMaxInsidePower:0.#}, diversity {rm.currentInsideEnemyDiversityLevel} / {rm.currentMaxInsideDiversityLevel}"
				: $"power {rm.currentDaytimeEnemyPower:0.#} (no deaths {rm.currentDaytimeEnemyPowerNoDeaths:0.#}) / {rm.currentLevel.maxDaytimeEnemyPowerCount}";
			P.LogDebug($"Spawn curve ({group}) at {clock}: time {time:0.###}, value {curve.Evaluate(time):0.###}; {budget}");
			return time;
		}

		// currentDayTime without the wrap at 6 AM, so the hour counter and vent spawn times survive the wrap
		public static float PlanetTimeUnwrapped(TimeOfDay tod)
		{
			SelectableLevel level = tod.currentLevel;
			if (level == null) { return tod.globalTime; }
			return (tod.globalTime + level.OffsetFromGlobalTime) * level.DaySpeedMultiplier;
		}

		// hour counter and vent spawn times
		private static float SpawnDayTime(TimeOfDay tod) => MoonBlacklist.Active ? tod.currentDayTime : PlanetTimeUnwrapped(tod);

		// vent spawn times come from the unwrapped hour counter
		private static void UseUnwrappedDayTime(ILContext il, string name)
		{
			ILCursor c = new ILCursor(il);
			int count = 0;
			while (c.TryGotoNext(MoveType.Before, x => x.MatchLdfld<TimeOfDay>(nameof(TimeOfDay.currentDayTime))))
			{
				c.Remove();
				c.EmitDelegate<Func<TimeOfDay, float>>(SpawnDayTime);
				count++;
			}
			if (count == 0) { P.Fail($"{name}: no currentDayTime to replace"); }
		}
		private static void RoundManager_SpawnInsideEnemiesFromVentsIfReady(ILContext il) { UseUnwrappedDayTime(il, "vent spawns"); }
		private static void EnemyVent_Update(ILContext il) { UseUnwrappedDayTime(il, "vent audio"); }
		private static void RoundManager_PredictAllOutsideEnemies(ILContext il)
		{
			// currentLevel.outsideEnemySpawnChanceThroughDay + enemyType.probabilityCurve (incl. the ones v81 logs)
			if (RemapDayCurves(il) < 3) { P.Fail("predict these nuts"); }
		}
		private static void RoundManager_PlotOutEnemiesForNextHour(ILContext il)
		{
			ILCursor c = new ILCursor(il);
			if (c.TryGotoNext(MoveType.Before, x => x.MatchCallvirt<AnimationCurve>(nameof(AnimationCurve.Evaluate))))
			{
				c.Emit(OpCodes.Ldarg_0);
				c.EmitDelegate<Func<float, RoundManager, float>>((vanilla, rm) => {
					if (MoonBlacklist.Active) { return LogCurve(rm, "inside", rm.currentLevel.enemySpawnChanceThroughoutDay, vanilla); }
					float val = rm.timeScript.currentDayTime / rm.timeScript.totalTime;
					return LogCurve(rm, "inside", rm.currentLevel.enemySpawnChanceThroughoutDay, val < 0.75f ? val * (4.0f / 3.0f) : 1 - (val - 0.75f) * 4);
				});
			}
			else { P.Fail("plot my balls"); }
		}
		private static void RoundManager_AssignRandomEnemyToVent(ILContext il)
		{
			// enemyType.probabilityCurve (with and without numberSpawnedFalloff)
			if (RemapDayCurves(il) < 2) { P.Fail("assign't"); }
		}

		// 24h spawn curve: rises until midnight (75% of the day), then falls toward dawn
		public static float DayCurve(float val)
		{
			val %= 1f;
			return (val < 0.75f ? val * (4.0f / 3.0f) : 1 - (val - 0.75f) * 4);
		}

		private static float CurveTime(float val) => MoonBlacklist.Active ? val : DayCurve(val);

		// DayCurve() before every AnimationCurve.Evaluate fed "x / totalTime" (directly or via a local);
		// matches by pattern, not local index, so it survives the game reshuffling locals
		private static int RemapDayCurves(ILContext il)
		{
			// locals like "num3 = lengthOfHours * currentHour / totalTime"
			HashSet<int> dayLocals = new HashSet<int>();
			foreach (Instruction ins in il.Instrs)
			{
				if (ins.MatchDiv() && IsTotalTime(ins.Previous) && ins.Next != null && ins.Next.MatchStloc(out int stored))
				{
					dayLocals.Add(stored);
				}
			}

			ILCursor c = new ILCursor(il);
			int count = 0;
			while (c.TryGotoNext(MoveType.Before, x => x.MatchCallvirt<AnimationCurve>(nameof(AnimationCurve.Evaluate))))
			{
				Instruction arg = c.Prev;
				if ((arg.MatchDiv() && IsTotalTime(arg.Previous)) || (arg.MatchLdloc(out int loc) && dayLocals.Contains(loc)))
				{
					c.EmitDelegate<Func<float, float>>(CurveTime);
					count++;
				}
				c.Index++;
			}
			return count;
		}

		private static bool IsTotalTime(Instruction ins)
		{
			return ins != null && ins.MatchLdfld<TimeOfDay>(nameof(TimeOfDay.totalTime));
		}
		#endregion

		// call the original with the time wrapped into the 24h loop so other clock mods still run (vanilla shows anything past its day as 12:00 AM)
		private static void HUDManager_SetClock(On.HUDManager.orig_SetClock orig, HUDManager self, float timeNormalized, float numberOfHours, bool createNewLine)
		{
			if (MoonBlacklist.Active) { orig(self, timeNormalized, numberOfHours, createNewLine); return; }
			orig(self, WrapClockTime(timeNormalized, numberOfHours), numberOfHours, createNewLine);
			self.clockNumber.text = FixMidnightHour(self.clockNumber.text);
		}

		// used by the leave-early vote message
		private static string HUDManager_GetClockTimeFormatted(On.HUDManager.orig_GetClockTimeFormatted orig, HUDManager self, float timeNormalized, float numberOfHours, bool createNewLine)
		{
			if (MoonBlacklist.Active) { return orig(self, timeNormalized, numberOfHours, createNewLine); }
			return FixMidnightHour(orig(self, WrapClockTime(timeNormalized, numberOfHours), numberOfHours, createNewLine));
		}

		// a t the game's (int)(t * minutesPerDay) + 360 turns into the same minute of the 24h loop
		private static float WrapClockTime(float timeNormalized, float numberOfHours)
		{
			int day = (int)(60f * numberOfHours);
			if (day <= 0) { return timeNormalized; }
			int minute = ((Mathf.FloorToInt(timeNormalized * day) + 360) % day + day) % day;
			int k = minute - 360;
			// half-minute nudge so the (int) cast lands on k, negative k too
			return (k + (k < 0 ? -0.5f : 0.5f)) / day;
		}

		// vanilla prints hour 0 as ":05 AM"; only that form is fixed, so a 24h mod's "00:05" is left alone
		private static string FixMidnightHour(string text)
		{
			return text != null && text.StartsWith(":") ? "12" + text : text;
		}

		// calls the original so other mods' patches still run
		private static void HUDManager_SetClockIcon(On.HUDManager.orig_SetClockIcon orig, HUDManager self, DayMode dayMode)
		{
			if (MoonBlacklist.Active || TimeOfDay.Instance == null || self.clockIcons == null || self.clockIcons.Length < 4) { orig(self, dayMode); return; }
			orig(self, (DayMode)ClockIconIndex(dayMode));
		}

		// 0-24 clock hour -> "2:00 AM"
		internal static string FormatHour(float hour)
		{
			int minutes = (Mathf.RoundToInt(hour * 60f) % 1440 + 1440) % 1440; // round, not truncate (13.8333 h = 1:50 PM)
			int h = minutes / 60;
			string ampm = h >= 12 ? "PM" : "AM";
			h %= 12;
			if (h == 0) { h = 12; }
			return $"{h}:{minutes % 60:00} {ampm}";
		}

		internal static void UnPatch()
		{
			P.Log("Unpatched");
		}

	}
}