
using BepInEx.Configuration;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace DayCycle
{
	public static class DayCycle
	{
		public static Animator sunAnim;

		// the game's own day length, read before it's set to 24
		internal static int VanillaHours = 18;

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

			Hook("StartOfRound.PassTimeToNextDay (IL)", () => IL.StartOfRound.PassTimeToNextDay += StartOfRound_PassTimeToNextDay);

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

			// water
			Hook("FloodWeather.OnGlobalTimeSync", () => On.FloodWeather.OnGlobalTimeSync += FloodWeather_OnGlobalTimeSync);

			// clock
			Hook("HUDManager.SetClock", () => On.HUDManager.SetClock += HUDManager_SetClock);
			Hook("HUDManager.GetClockTimeFormatted", () => On.HUDManager.GetClockTimeFormatted += HUDManager_GetClockTimeFormatted);
			Hook("HUDManager.SetClockIcon", () => On.HUDManager.SetClockIcon += HUDManager_SetClockIcon);
			Hook("TimeOfDay.TimeOfDayEvents", () => On.TimeOfDay.TimeOfDayEvents += TimeOfDay_TimeOfDayEvents1); // PlayerSeesNewTimeOfDay() patch

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

		private static void TimeOfDay_TimeOfDayEvents1(On.TimeOfDay.orig_TimeOfDayEvents orig, TimeOfDay self)
		{
			orig(self);
			if (self.dayMode == DayMode.Dawn && self.dayModeLastTimePlayerWasOutside == DayMode.Midnight) {
				self.PlayerSeesNewTimeOfDay();
			}
		}

		
		private static void AnimatedSun_Start(On.animatedSun.orig_Start orig, animatedSun self)
		{
			sunAnim = self.gameObject.GetComponent<Animator>();
			string sunAnimName = sunAnim.runtimeAnimatorController.name;

			if (API.sunAnimators.TryGetValue(sunAnimName, out RuntimeAnimatorController newAnimator))
			{
				self.gameObject.GetComponent<Animator>().runtimeAnimatorController = newAnimator;
				P.Log($"Replaced SunAnimContainer animator \"{sunAnimName}\" with 24 hour version \"{newAnimator.name}\".");
			}
			else
			{
				P.Log($"Could not find replacement for SunAnimContainer animator \"{sunAnimName}\".");
				// TODO: Add fallback behavior
			}

			orig(self);
		}

		// the save loads before TimeOfDay.Start, so set 24h here or a new save's deadline is in 18h days
		private static void TimeOfDay_Awake(On.TimeOfDay.orig_Awake orig, TimeOfDay self)
		{
			if (self.numberOfHours > 0 && self.numberOfHours != 24) { VanillaHours = self.numberOfHours; }
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
			float dt = Time.deltaTime * self.globalTimeSpeedMultiplier;
			// never below 0: once the day's end is passed the difference turns negative and would refill the deadline every frame
			self.timeUntilDeadline -= Mathf.Max(Mathf.Clamp(self.globalTime + dt, 0f, self.globalTimeAtEndOfDay*0.996f) - self.globalTime, 0f); // NOTE: only to keep intended behaviour
			self.globalTime += dt; //self.globalTime = Mathf.Clamp(self.globalTime + dt, 0f, self.globalTimeAtEndOfDay);
		}
		private static void StartOfRound_PassTimeToNextDay(ILContext il)
		{
			ILCursor c = new ILCursor(il);
			if (c.TryGotoNext(MoveType.Before, x => x.MatchStloc(0)))
			{
				c.EmitDelegate<Func<float, float>>((num) => { return Mathf.Clamp(num, 0f, TimeOfDay.Instance.globalTimeAtEndOfDay); }); // might change later
			}
			else { P.Fail("pass fail"); }
		}

		private static float TimeOfDay_CalculatePlanetTime(On.TimeOfDay.orig_CalculatePlanetTime orig, TimeOfDay self, SelectableLevel level)
		{
			return (self.globalTime + level.OffsetFromGlobalTime) * level.DaySpeedMultiplier % (self.totalTime + 1f);
		}
		private static float TimeOfDay_CalculatePlanetTimeClampToEndOfDay(On.TimeOfDay.orig_CalculatePlanetTimeClampToEndOfDay orig, TimeOfDay self, SelectableLevel level)
		{
			return (self.globalTime + level.OffsetFromGlobalTime) * level.DaySpeedMultiplier % (self.totalTime + 1f);
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
				c.EmitDelegate<Func<TimeOfDay, float>>((self) => { return self.normalizedTimeOfDay % 1f; });
				//IL_00b6: callvirt instance void [UnityEngine.AnimationModule]UnityEngine.Animator::SetFloat(string, float32)
			}
			else { P.Fail("can't patch sunAnimator.."); }

			// configurable time update interval
			if (c.TryGotoNext(MoveType.Before, x => x.MatchLdcR4(3f)))
			{
				c.Remove();
				c.EmitDelegate<Func<float>>(() => { return ModConfig.intervalSetting.Value; });

			} else { P.Fail("cant patch interval..."); }
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
						if (self.globalTime >= float.MaxValue - 4.32) { return true; }
						if (LeaveEarlyVoted(self) && PlanetTimeUnwrapped(self) >= leaveEarlyAt) { return true; }
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
				c.EmitDelegate<Func<float>>(() => vanillaValue * VanillaHours / 24f);
			}
			else { P.Fail("leave-early vote time not found"); }
		}

		private static void TimeOfDay_SetShipLeaveEarlyClientRpc(On.TimeOfDay.orig_SetShipLeaveEarlyClientRpc orig, TimeOfDay self, float timeToLeaveEarly, int votes)
		{
			orig(self, timeToLeaveEarly, votes);
			// only the call that applied it (the host also passes here when sending)
			if (self.shipLeaveAutomaticallyTime != timeToLeaveEarly || LeaveEarlyVoted(self)) { return; }
			leaveEarlyFor = timeToLeaveEarly;
			leaveEarlyAt = PlanetTimeUnwrapped(self) + (timeToLeaveEarly - self.normalizedTimeOfDay) * self.totalTime; // the time in "Please return by X"
		}
		public static Dictionary<DayMode, float> phaseTimes = new Dictionary<DayMode, float>(){
			//{ "dawn", 0f/24f }, // 6:00 AM -  11:59 AM
			{ DayMode.Noon, 6f/24f }, // 12:00 PM - 5:20 PM
			{ DayMode.Sundown, 11.35f/24f }, // 5:21 PM - 11:11 PM
			{ DayMode.Midnight, 16.2f/24f }, // 11:12 PM - 2:59 AM
			{ DayMode.Dawn, 21f/24f } // 3:00 AM - 5:59 AM ( time is offset +6 so it hits 24 here )
		};
		private static DayMode TimeOfDay_GetDayPhase(On.TimeOfDay.orig_GetDayPhase orig, TimeOfDay self, float time)
		{
			if (time >= phaseTimes[DayMode.Dawn]) { return DayMode.Dawn; } 
			else if (time >= phaseTimes[DayMode.Midnight]) { return DayMode.Midnight; } 
			else if (time >= phaseTimes[DayMode.Sundown]) { return DayMode.Sundown; } 
			else if (time >= phaseTimes[DayMode.Noon]) { return DayMode.Noon; }	
			else { return DayMode.Dawn; }
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
				c.EmitDelegate<Func<float, bool>>((norm) => { return (norm < 0.15f || norm >= 0.9f); });
				c.Next.OpCode = OpCodes.Brfalse;
			}
			else { P.Fail("coundt find threat..."); }

			// > 0.8 -> > 0.6 (and before dawn at 0.9)
			if (c.TryGotoNext(MoveType.After, x => x.MatchLdfld<TimeOfDay>(nameof(TimeOfDay.normalizedTimeOfDay))))
			{
				c.Remove();
				c.EmitDelegate<Func<float, bool>>((norm) => { return (norm >= 0.6f && norm < 0.9f); });
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
			if (c.TryGotoNext(MoveType.After, x => x.MatchLdfld<TimeOfDay>(nameof(TimeOfDay.normalizedTimeOfDay))))
			{
				c.Next.Operand = 0.225f;
			}
			else { P.Fail("advance rejected.."); }
		}

		// vanilla's 18h fraction on the 24h day
		private static float LeaveTime(TimeOfDay tod) => tod.normalizedTimeOfDay * (4.0f / 3f);
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
					float val = (num % rm.timeScript.totalTime) / rm.timeScript.totalTime;
					return (val < 0.75f ? val * (4.0f / 3.0f) : 1 - (val - 0.75f) * 4);
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
				c.EmitDelegate<Func<float, RoundManager, float, float>>((vanilla, rm, num) => DayCurve(num / rm.timeScript.totalTime)); // num keeps growing past one day
			}
			else { P.Fail("can't go outside"); }
		}
		private static void RoundManager_SpawnRandomOutsideEnemy(ILContext il)
		{
			// enemyType.probabilityCurve (with and without numberSpawnedFalloff); its argument grows past one day
			if (RemapDayCurves(il) < 2) { P.Fail("outside probability curve not remapped"); }
		}

		// currentDayTime without the wrap at 6 AM, so the hour counter and vent spawn times survive the wrap
		public static float PlanetTimeUnwrapped(TimeOfDay tod)
		{
			SelectableLevel level = tod.currentLevel;
			if (level == null) { return tod.globalTime; }
			return (tod.globalTime + level.OffsetFromGlobalTime) * level.DaySpeedMultiplier;
		}

		// hour counter and vent spawn times
		private static float SpawnDayTime(TimeOfDay tod) => PlanetTimeUnwrapped(tod);

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
					float val = rm.timeScript.currentDayTime / rm.timeScript.totalTime;
					return (val < 0.75f ? val * (4.0f / 3.0f) : 1 - (val - 0.75f) * 4);
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

		private static float CurveTime(float val) => DayCurve(val);

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

		private static void FloodWeather_OnGlobalTimeSync(On.FloodWeather.orig_OnGlobalTimeSync orig, FloodWeather self)
		{
			self.floodLevelOffset = Mathf.Clamp(TimeOfDay.Instance.globalTime / TimeOfDay.Instance.totalTime, 0f, 100f) * TimeOfDay.Instance.currentWeatherVariable2;
			// right now this code makes the flood level keep rising. its nutso
			// add a way for the level to go down eventually?
		}

		private static void HUDManager_SetClock(On.HUDManager.orig_SetClock orig, HUDManager self, float timeNormalized, float numberOfHours, bool createNewLine)
		{
			int num = ((int)(timeNormalized * (60f * numberOfHours)) + 360) % (int)(60f * numberOfHours);
			int num2 = (int)Mathf.Floor(num / 60);

			self.newLine = (createNewLine ? "\n" : " ");
			self.amPM = self.newLine + (num2 >= 12 ? "PM" : "AM");

			//if (num2 >= 24) { self.clockNumber.text = "12:00 " + self.newLine + " AM"; return "12:00\nAM"; }
			if (num2 > 12) { num2 -= 12; }
			if (num2 == 0) { num2 = 12; }

			int num3 = num % 60;
			string text = $"{num2:00}:{num3:00}".TrimStart('0') + self.amPM;
			self.clockNumber.text = text;
		}
		// the leave-early vote's "Please return by X": same formatting as the clock above (vanilla shows anything past midnight as 12:00 AM)
		private static string HUDManager_GetClockTimeFormatted(On.HUDManager.orig_GetClockTimeFormatted orig, HUDManager self, float timeNormalized, float numberOfHours, bool createNewLine)
		{
			int num = ((int)(timeNormalized * (60f * numberOfHours)) + 360) % (int)(60f * numberOfHours);
			int num2 = (int)Mathf.Floor(num / 60);

			string amPM = (createNewLine ? "\n" : " ") + (num2 >= 12 ? "PM" : "AM");
			if (num2 > 12) { num2 -= 12; }
			if (num2 == 0) { num2 = 12; }

			int num3 = num % 60;
			return $"{num2:00}:{num3:00}".TrimStart('0') + amPM;
		}
		private static void HUDManager_SetClockIcon(On.HUDManager.orig_SetClockIcon orig, HUDManager self, DayMode dayMode)
		{
			if (dayMode == DayMode.Dawn && TimeOfDay.Instance.normalizedTimeOfDay >= phaseTimes[DayMode.Dawn])
			{
				dayMode = DayMode.Noon;
			}
			orig(self, dayMode);
		}

		internal static void UnPatch()
		{
			P.Log("Unpatched");
		}

	}
}