using UnityEngine;

namespace DayCycle
{
	// Daily tide on the moon's clock. Vanilla: start = currentWeatherVariable, end-of-day = + currentWeatherVariable2.
	public static class Flood
	{
		private const float DefaultRise = 8f, DefaultPeak = 0f, DefaultDrained = 6f;
		private static string lastWarned = "";

		internal static void Patch()
		{
			DayCycle.Hook("FloodWeather.OnEnable", () => On.FloodWeather.OnEnable += FloodWeather_OnEnable);
			DayCycle.Hook("FloodWeather.OnGlobalTimeSync", () => On.FloodWeather.OnGlobalTimeSync += FloodWeather_OnGlobalTimeSync);
			DayCycle.Hook("FloodWeather.Update", () => On.FloodWeather.Update += FloodWeather_Update);
		}

		// a late landing finds high water instead of watching it climb
		private static void FloodWeather_OnEnable(On.FloodWeather.orig_OnEnable orig, FloodWeather self)
		{
			orig(self);
			if (TimeOfDay.Instance == null || MoonBlacklist.Active) { return; }
			self.floodLevelOffset = TideOffset(TimeOfDay.Instance);
			self.transform.position = new Vector3(0f, TimeOfDay.Instance.currentWeatherVariable + self.floodLevelOffset, 0f);
		}

		private static void FloodWeather_OnGlobalTimeSync(On.FloodWeather.orig_OnGlobalTimeSync orig, FloodWeather self)
		{
			if (MoonBlacklist.Active) { orig(self); return; }
			if (TimeOfDay.Instance != null) { self.floodLevelOffset = TideOffset(TimeOfDay.Instance); }
		}

		// vanilla Update still glides towards the target (max 0.5 m/s)
		private static void FloodWeather_Update(On.FloodWeather.orig_Update orig, FloodWeather self)
		{
			if (TimeOfDay.Instance != null && !MoonBlacklist.Active) { self.floodLevelOffset = TideOffset(TimeOfDay.Instance); }
			orig(self);
		}

		private static float TideOffset(TimeOfDay tod)
		{
			return Tide(ClockHour(tod)) * Mathf.Max(tod.currentWeatherVariable2, 0f);
		}

		// not the HUD clock: it isn't updated yet when the water appears at landing
		private static float ClockHour(TimeOfDay tod)
		{
			float hours = DayCycle.PlanetTimeUnwrapped(tod) / tod.lengthOfHours; // since 6 AM of the landing day
			return ((hours + 6f) % 24f + 24f) % 24f;
		}

		// 0 = start, 1 = peak; eased so there's no jump at the boundaries
		private static float Tide(float clock)
		{
			GetHours(out float rise, out float peak, out float drained);
			float up = (peak - rise + 24f) % 24f;
			float down = (drained - peak + 24f) % 24f;
			float x = (clock - rise + 24f) % 24f;
			if (x < up) { return Mathf.SmoothStep(0f, 1f, x / up); }
			if (x < up + down) { return 1f - Mathf.SmoothStep(0f, 1f, (x - up) / down); }
			return 0f;
		}

		// rise -> peak -> drained in order around the clock, each step taking time
		private static void GetHours(out float rise, out float peak, out float drained)
		{
			rise = ModConfig.floodRiseHour.Value;
			peak = ModConfig.floodPeakHour.Value;
			drained = ModConfig.floodDrainedHour.Value;
			float up = (peak - rise + 24f) % 24f;
			float down = (drained - peak + 24f) % 24f;
			float rest = (rise - drained + 24f) % 24f;
			if (up > 0f && down > 0f && Mathf.Abs(up + down + rest - 24f) < 0.01f) { return; }

			string key = $"{rise}/{peak}/{drained}";
			if (key != lastWarned)
			{
				lastWarned = key;
				P.Fail($"Flood hours out of order (rise {rise}, peak {peak}, drained {drained}); using 8 / 0 / 6", LLV.Warning);
			}
			rise = DefaultRise;
			peak = DefaultPeak;
			drained = DefaultDrained;
		}
	}
}
