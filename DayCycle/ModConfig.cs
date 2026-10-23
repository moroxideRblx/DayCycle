using BepInEx.Bootstrap;
using BepInEx.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace DayCycle
{
	// Sections are numbered because BepInEx and mod managers sort them alphabetically. Values are read live.
	public static class ModConfig
	{
		public const string LethalConfigGuid = "ainavt.lc.lethalconfig";

		public const string DayCycleSection = "1. Day Cycle";
		public const string FloodSection = "4. Flood";
		public const string ClockSection = "5. Clock";

		public static ConfigEntry<string> moonBlacklist;
		public static ConfigEntry<bool> debugLogging;
		public static ConfigEntry<float> floodRiseHour;
		public static ConfigEntry<float> floodPeakHour;
		public static ConfigEntry<float> floodDrainedHour;
		public static ConfigEntry<float> intervalSetting;
		public static ConfigEntry<ClockIcons> clockIcons;

		// in display order, for LethalConfig
		private static readonly List<ConfigEntryBase> all = new List<ConfigEntryBase>();

		public static ConfigFile Load()
		{
			ConfigFile cfg = P.Instance.Config;
			bool saveOnSet = cfg.SaveOnConfigSet;
			cfg.SaveOnConfigSet = false;

			moonBlacklist = Bind(cfg, DayCycleSection, "Moon blacklist", "Gordion",
				"Moons that use the vanilla day. Comma-separated, e.g. Gordion, Embrion.");
			debugLogging = Bind(cfg, DayCycleSection, "Debug logging", false,
				"Extra log lines for bug reports.");

			floodRiseHour = Bind(cfg, FloodSection, "Rise start hour", 8f,
				"Hour the water starts rising (8.5 = 8:30 AM).",
				new AcceptableValueRange<float>(0f, 24f));
			floodPeakHour = Bind(cfg, FloodSection, "Peak hour", 0f,
				"Hour the water is highest (0 = midnight).",
				new AcceptableValueRange<float>(0f, 24f));
			floodDrainedHour = Bind(cfg, FloodSection, "Drained hour", 6f,
				"Hour the water is back down.",
				new AcceptableValueRange<float>(0f, 24f));

			intervalSetting = Bind(cfg, ClockSection, "Clock update interval", 3f,
				"Seconds between HUD clock updates.",
				new AcceptableValueRange<float>(0.1f, 60f));
			clockIcons = Bind(cfg, ClockSection, "Clock icons", ClockIcons.Original,
				"HUD icon and day phase schedule: Original or DayCycle.");

			MigrateOldEntries(cfg);
			cfg.Save();
			cfg.SaveOnConfigSet = saveOnSet;

			if (Chainloader.PluginInfos.ContainsKey(LethalConfigGuid)) { RegisterLethalConfig(); }
			return cfg;
		}

		private static ConfigEntry<T> Bind<T>(ConfigFile cfg, string section, string key, T defaultValue, string description, AcceptableValueBase? range = null)
		{
			ConfigEntry<T> entry = cfg.Bind(section, key, defaultValue, new ConfigDescription(description, range));
			all.Add(entry);
			return entry;
		}

		#region migration
		// renamed entries keep their values; every other leftover is dropped
		private static void MigrateOldEntries(ConfigFile cfg)
		{
			Dictionary<ConfigDefinition, ConfigEntryBase> renamed = new Dictionary<ConfigDefinition, ConfigEntryBase>
			{
				{ new ConfigDefinition("Other", "Clock Update Interval"), intervalSetting },
			};

			// BepInEx keeps unbound entries here and writes them back on save; it isn't public
			PropertyInfo? prop = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			if (!(prop?.GetValue(cfg) is Dictionary<ConfigDefinition, string> orphans))
			{
				P.Fail("Could not read orphaned config entries; old entries were not migrated", LLV.Warning);
				return;
			}
			foreach (KeyValuePair<ConfigDefinition, ConfigEntryBase> kv in renamed)
			{
				if (orphans.TryGetValue(kv.Key, out string value))
				{
					kv.Value.SetSerializedValue(value);
					P.Log($"Config: moved \"[{kv.Key.Section}] {kv.Key.Key}\" to \"[{kv.Value.Definition.Section}] {kv.Value.Definition.Key}\"");
				}
			}

			List<ConfigDefinition> old = orphans.Keys.ToList();
			foreach (ConfigDefinition def in old) { orphans.Remove(def); }
			if (old.Count > 0) { P.Log($"Config: removed {old.Count} old entries"); }
		}
		#endregion

		#region LethalConfig
		// Reflection only (soft dependency); explicit items so they apply live instead of auto-generated "requires restart".
		private static Assembly? lethalConfig;
		private static MethodInfo? lethalConfigAdd;

		private static void RegisterLethalConfig()
		{
			try
			{
				lethalConfig = Chainloader.PluginInfos[LethalConfigGuid].Instance.GetType().Assembly;
				Type baseItem = lethalConfig.GetType("LethalConfig.ConfigItems.BaseConfigItem", true);
				lethalConfigAdd = lethalConfig.GetType("LethalConfig.LethalConfigManager", true).GetMethod("AddConfigItem", new[] { baseItem, typeof(Assembly) });
				foreach (ConfigEntryBase entry in all) { AddLethalConfigItem(entry); }
				P.Log("Config: registered with LethalConfig");
			}
			catch (Exception e)
			{
				lethalConfigAdd = null;
				P.Fail($"LethalConfig registration failed, using its default menu: {e.GetType().Name}: {e.Message}", LLV.Warning);
			}
		}

		private static void AddLethalConfigItem(ConfigEntryBase entry)
		{
			Type? itemType = LethalConfigItemType(lethalConfig!, entry.SettingType);
			if (itemType == null) { return; } // falls back to LethalConfig's auto-generation
			object item = Activator.CreateInstance(itemType, entry, false); // (ConfigEntry<T>, bool requiresRestart)
			lethalConfigAdd!.Invoke(null, new[] { item, typeof(ModConfig).Assembly });
		}

		private static Type? LethalConfigItemType(Assembly lc, Type setting)
		{
			if (setting.IsEnum) { return lc.GetType("LethalConfig.ConfigItems.EnumDropDownConfigItem`1")?.MakeGenericType(setting); }
			if (setting == typeof(bool)) { return lc.GetType("LethalConfig.ConfigItems.BoolCheckBoxConfigItem"); }
			if (setting == typeof(float)) { return lc.GetType("LethalConfig.ConfigItems.FloatSliderConfigItem"); }
			if (setting == typeof(int)) { return lc.GetType("LethalConfig.ConfigItems.IntSliderConfigItem"); }
			if (setting == typeof(string)) { return lc.GetType("LethalConfig.ConfigItems.TextInputFieldConfigItem"); }
			return null;
		}
		#endregion
	}
}
