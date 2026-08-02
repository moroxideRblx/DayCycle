
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
		internal static void Patch()
		{
			On.TimeOfDay.Start += TimeOfDay_Start;
			On.TimeOfDay.MoveGlobalTime += TimeOfDay_MoveGlobalTime;
			On.TimeOfDay.CalculatePlanetTime += TimeOfDay_CalculatePlanetTime;
			On.TimeOfDay.CalculatePlanetTimeClampToEndOfDay += TimeOfDay_CalculatePlanetTimeClampToEndOfDay;
			IL.TimeOfDay.MoveTimeOfDay += TimeOfDay_MoveTimeOfDay;
			IL.TimeOfDay.TimeOfDayEvents += TimeOfDay_TimeOfDayEvents;
			On.TimeOfDay.GetDayPhase += TimeOfDay_GetDayPhase;

			IL.StartOfRound.PassTimeToNextDay += StartOfRound_PassTimeToNextDay;

			// normalizedTimeOfDay Patches
			IL.EnemyAI.CheckTimeOfDayToLeave += EnemyAI_CheckTimeOfDayToLeave;
			IL.RoundManager.SpawnRandomDaytimeEnemy += RoundManager_SpawnRandomDaytimeEnemy;
			IL.GameNetcodeStuff.PlayerControllerB.IVisibleThreat_GetThreatLevel += PlayerControllerB_IVisibleThreat_GetThreatLevel;
			IL.RoundManager.AdvanceHourAndSpawnNewBatchOfEnemies += RoundManager_AdvanceHourAndSpawnNewBatchOfEnemies;

			// spawn rate curve patches
			IL.RoundManager.PlotOutEnemiesForNextHour += RoundManager_PlotOutEnemiesForNextHour;
			IL.RoundManager.PredictAllOutsideEnemies += RoundManager_PredictAllOutsideEnemies;
			IL.RoundManager.SpawnEnemiesOutside += RoundManager_SpawnEnemiesOutside;
			IL.RoundManager.SpawnRandomWeedEnemy += RoundManager_SpawnRandomWeedEnemy;
			IL.RoundManager.SpawnDaytimeEnemiesOutside += RoundManager_SpawnDaytimeEnemiesOutside;
			IL.RoundManager.AssignRandomEnemyToVent += RoundManager_AssignRandomEnemyToVent;

			// water
			On.FloodWeather.OnGlobalTimeSync += FloodWeather_OnGlobalTimeSync;

			// clock
			On.HUDManager.SetClock += HUDManager_SetClock;
			On.HUDManager.SetClockIcon += HUDManager_SetClockIcon;
			On.TimeOfDay.TimeOfDayEvents += TimeOfDay_TimeOfDayEvents1; // PlayerSeesNewTimeOfDay() patch

			//sun animator swap
			On.animatedSun.Start += AnimatedSun_Start;

			P.Log("Patched");
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

		private static void TimeOfDay_Start(On.TimeOfDay.orig_Start orig, TimeOfDay self)
		{
			self.numberOfHours = 24;

			orig(self);
		}

		#region TimeofDay
		private static void TimeOfDay_MoveGlobalTime(On.TimeOfDay.orig_MoveGlobalTime orig, TimeOfDay self)
		{
			float dt = Time.deltaTime * self.globalTimeSpeedMultiplier;
			self.timeUntilDeadline -= Mathf.Clamp(self.globalTime + dt, 0f, self.globalTimeAtEndOfDay*0.996f) - self.globalTime; // NOTE: only to keep intended behaviour
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
				c.EmitDelegate<Func<TimeOfDay, float>>((self) => { return self.globalTime; });
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
						return false;
					}
				);
				//c.Index += 1;
				c.Next.OpCode = OpCodes.Brfalse_S;
			}
			else { P.Fail("event the hell TWO!!!"); }
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

			// > 0.8 -> > 0.6
			if (c.TryGotoNext(MoveType.After, x => x.MatchLdfld<TimeOfDay>(nameof(TimeOfDay.normalizedTimeOfDay))))
			{
				c.Remove();
				c.EmitDelegate<Func<float, bool>>((norm) => { return (norm >= 0.6f || norm < 0.9f); });
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
				c.EmitDelegate<Func<TimeOfDay, float>>((self) => { return self.normalizedTimeOfDay * (4.0f/3f); });
			}
			else { P.Fail("cant check!!"); }
		}
		private static void RoundManager_SpawnRandomDaytimeEnemy(ILContext il)
		{
			ILCursor c = new ILCursor(il);
			if (c.TryGotoNext(MoveType.Before, x => x.MatchLdfld<TimeOfDay>(nameof(TimeOfDay.normalizedTimeOfDay))))
			{
				c.Remove();
				c.EmitDelegate<Func<TimeOfDay, float>>((self) => { return self.normalizedTimeOfDay * (4.0f / 3f); });
			}
			else { P.Fail("cant spawn random daytona!!"); }
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
		#endregion

		#region RoundManager
		private static void RoundManager_SpawnDaytimeEnemiesOutside(ILContext il)
		{
			ILCursor c = new ILCursor(il);
			//  currentLevel.daytimeEnemySpawnChanceThroughDay
			if (c.TryGotoNext(MoveType.Before, x => x.MatchCallvirt<AnimationCurve>(nameof(AnimationCurve.Evaluate))))
			{
				c.Emit(OpCodes.Pop);
				c.Emit(OpCodes.Ldarg_0);    // self
				c.Emit(OpCodes.Ldloc_0);    // num (basically timeUpToCurrentHour)
				c.EmitDelegate<Func<RoundManager, float, float>>((rm, num) => {
					float val = (num % rm.timeScript.totalTime) / rm.timeScript.totalTime;
					return (val < 0.75f ? val * (4.0f / 3.0f) : 1 - (val - 0.75f) * 4);
				});
			}
			else { P.Fail("day broken"); }
		}
		private static void RoundManager_SpawnRandomWeedEnemy(ILContext il)
		{
			ILCursor c = new ILCursor(il);
			//  enemyType.probabilityCurve
			if (c.TryGotoNext(MoveType.Before, x => x.MatchCallvirt<AnimationCurve>(nameof(AnimationCurve.Evaluate))))
			{
				c.Emit(OpCodes.Pop);
				c.Emit(OpCodes.Ldarg_0);    // self
				c.Emit(OpCodes.Ldarg_2);    // timeUpToCurrentHour
				c.EmitDelegate<Func<RoundManager, float, float>>((rm, timeUpToCurrentHour) => {
					float val = (timeUpToCurrentHour % rm.timeScript.totalTime) / rm.timeScript.totalTime;
					return (val < 0.75f ? val * (4.0f / 3.0f) : 1 - (val - 0.75f) * 4);
				});
			}
			else { P.Fail("no weed"); }

			//  enemyType.probabilityCurve * enemyType.numberSpawnedFalloff
			if (c.TryGotoNext(MoveType.Before, x => x.MatchCallvirt<AnimationCurve>(nameof(AnimationCurve.Evaluate))))
			{
				c.Emit(OpCodes.Pop);
				c.Emit(OpCodes.Ldarg_0);    // self
				c.Emit(OpCodes.Ldarg_2);    // timeUpToCurrentHour
				c.EmitDelegate<Func<RoundManager, float, float>>((rm, timeUpToCurrentHour) => {
					float val = (timeUpToCurrentHour % rm.timeScript.totalTime) / rm.timeScript.totalTime;
					return (val < 0.75f ? val * (4.0f / 3.0f) : 1 - (val - 0.75f) * 4);
				});
			}
			else { P.Fail("no weed 2"); }
		}
		private static void RoundManager_SpawnEnemiesOutside(ILContext il)
		{
			ILCursor c = new ILCursor(il);
			// currentLevel.outsideEnemySpawnChanceThroughDay
			if (c.TryGotoNext(MoveType.Before, x => x.MatchCallvirt<AnimationCurve>(nameof(AnimationCurve.Evaluate))))
			{
				c.Emit(OpCodes.Pop);
				c.Emit(OpCodes.Ldarg_0);    // self
				c.Emit(OpCodes.Ldloc_0);    // num
				c.EmitDelegate<Func<RoundManager, float, float>>((rm, num) => {
					float val = num / rm.timeScript.totalTime;
					return (val < 0.75f ? val * (4.0f / 3.0f) : 1 - (val - 0.75f) * 4);
				});
			}
			else { P.Fail("can't go outside"); }
		}
		private static void RoundManager_PredictAllOutsideEnemies(ILContext il)
		{
			ILCursor c = new ILCursor(il);

			// currentLevel.outsideEnemySpawnChanceThroughDay
			if (c.TryGotoNext(MoveType.Before, x => x.MatchCallvirt<AnimationCurve>(nameof(AnimationCurve.Evaluate))
			//, x => x.MatchStloc(4)
			))
			{
				c.Emit(OpCodes.Pop);
				c.Emit(OpCodes.Ldarg_0);
				c.Emit(OpCodes.Ldloc_3);
				c.EmitDelegate<Func<RoundManager, float, float>>((rm, num3) => {
					float val = num3 / rm.timeScript.totalTime;
					return (val < 0.75f ? val * (4.0f / 3.0f) : 1 - (val - 0.75f) * 4);
				});
			}
			else { P.Fail("predict these nuts"); }

			// enemyType.probabilityCurve
			if (c.TryGotoNext(MoveType.Before, x => x.MatchCallvirt<AnimationCurve>(nameof(AnimationCurve.Evaluate))
			//, x => x.MatchStloc(14)
			))
			{
				c.Emit(OpCodes.Pop);
				c.Emit(OpCodes.Ldarg_0);
				c.Emit(OpCodes.Ldloc_3);
				c.EmitDelegate<Func<RoundManager, float, float>>((rm, num3) => {
					float val = num3 / rm.timeScript.totalTime;
					return (val < 0.75f ? val * (4.0f / 3.0f) : 1 - (val - 0.75f) * 4);
				});
			}
			else { P.Fail("predict these nuts 2"); }

			// enemyType.probabilityCurve * enemyType.numberSpawnedFalloff
			if (c.TryGotoNext(MoveType.Before, x => x.MatchCallvirt<AnimationCurve>(nameof(AnimationCurve.Evaluate))
			//, x => x.MatchMul(), x => x.MatchConvI4()
			))
			{
				c.Emit(OpCodes.Pop);
				c.Emit(OpCodes.Ldarg_0);
				c.Emit(OpCodes.Ldloc_3);
				c.EmitDelegate<Func<RoundManager, float, float>>((rm, num3) => {
					float val = num3 / rm.timeScript.totalTime;
					return (val < 0.75f ? val * (4.0f / 3.0f) : 1 - (val - 0.75f) * 4);
				});
			}
			else { P.Fail("predict these nuts 2"); }
		}
		private static void RoundManager_PlotOutEnemiesForNextHour(ILContext il)
		{
			ILCursor c = new ILCursor(il);
			if (c.TryGotoNext(MoveType.Before, x => x.MatchCallvirt<AnimationCurve>(nameof(AnimationCurve.Evaluate))))
			{
				c.Emit(OpCodes.Pop);
				c.Emit(OpCodes.Ldarg_0);
				c.EmitDelegate<Func<RoundManager, float>>((rm) => {
					float val = rm.timeScript.currentDayTime / rm.timeScript.totalTime;
					return (val < 0.75f ? val * (4.0f / 3.0f) : 1 - (val - 0.75f) * 4);
				});
			}
			else { P.Fail("plot my balls"); }
		}
		private static void RoundManager_AssignRandomEnemyToVent(ILContext il)
		{
			ILCursor c = new ILCursor(il);
			// enemyType.probabilityCurve
			if (c.TryGotoNext(MoveType.Before, x => x.MatchCallvirt<AnimationCurve>(nameof(AnimationCurve.Evaluate))
			, x => x.MatchLdloc(1)
			))
			{
				c.EmitDelegate<Func<float, float>>((norm) => { return (norm < 0.75f ? norm * (4.0f / 3.0f) : 1 - (norm - 0.75f) * 4); });
			}
			else { P.Fail("assign't"); }

			// enemyType.probabilityCurve * enemyType.numberSpawnedFalloff
			if (c.TryGotoNext(MoveType.Before, x => x.MatchCallvirt<AnimationCurve>(nameof(AnimationCurve.Evaluate))
				, x => x.MatchMul(), x => x.MatchConvI4()
			))
			{
				c.EmitDelegate<Func<float, float>>((norm) => { return (norm < 0.75f ? norm * (4.0f / 3.0f) : 1 - (norm - 0.75f) * 4); });
			}
			else { P.Fail("assign't 2"); }
		}
		#endregion

		private static void FloodWeather_OnGlobalTimeSync(On.FloodWeather.orig_OnGlobalTimeSync orig, FloodWeather self)
		{
			self.floodLevelOffset = Mathf.Clamp(TimeOfDay.Instance.globalTime / TimeOfDay.Instance.totalTime, 0f, 100f) * TimeOfDay.Instance.currentWeatherVariable2;
			// right now this code makes the flood level keep rising. its nutso
			// add a way for the level to go down eventually?
		}

		private static string HUDManager_SetClock(On.HUDManager.orig_SetClock orig, HUDManager self, float timeNormalized, float numberOfHours, bool createNewLine)
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
			return text;
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