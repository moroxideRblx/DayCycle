using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
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

		public const string EnemyCycleSection = "2. Enemy Cycle";
		public const string EnemyCycleDaytimeSection = "2.1 Enemy Cycle (Daytime)";
		public const string EnemyCycleOutsideSection = "2.2 Enemy Cycle (Outside)";
		public const string EnemyCycleInsideSection = "2.3 Enemy Cycle (Inside)";
		private const string CycleHoursGroup = EnemyCycleSection + " > Enemy cycle hours";
		private const string OutsidePercentGroup = EnemyCycleSection + " > Outside enemy despawn percent";
		private const string IndoorPercentGroup = EnemyCycleSection + " > Indoor enemy despawn percent";
		private const string DeadlineDaysGroup = "3. Deadline > Deadline days";
		public const string DeadlineSection = "3. Deadline";
		public const string DaysModeSection = "3.1 Days Mode";
		public const string DayAndHourModeSection = "3.2 DayAndHour Mode";
		public const string DeadlineMoonsSection = "3.3 Deadline Moons";
		public const string LandingSection = "6. Landing";
		public const string LandingMoonsSection = "6.1 Landing Moons";
		private const string LandingSettingsGroup = "6. Landing > random landing settings";

		public static ConfigEntry<string> moonBlacklist;
		public static ConfigEntry<bool> debugLogging;
		public static ConfigEntry<bool> dawnFadeEnabled;
		public static ConfigEntry<float> dawnStartHour;
		public static ConfigEntry<float> dawnEndHour;
		public static ConfigEntry<bool> daytimeCycleEnabled;
		public static ConfigEntry<bool> indoorDawnFade;
		public static ConfigEntry<int> outsideDespawnPercent;
		public static ConfigEntry<int> indoorDawnShare;
		private static readonly Dictionary<string, ConfigEntry<EnemyCycleRule>> enemyRules = new Dictionary<string, ConfigEntry<EnemyCycleRule>>();

		public static ConfigEntry<DeadlineMode> deadlineMode;
		public static ConfigEntry<DeadlineDaysSource> deadlineDaysSource;
		public static ConfigEntry<int> deadlineDays;
		public static ConfigEntry<float> countingHours;
		public static ConfigEntry<float> giftHours;
		public static ConfigEntry<float> alertLeadHours;
		public static ConfigEntry<float> dayAndHourAlertLeadHours;
		public static ConfigEntry<bool> landingPenaltyEnabled;
		public static ConfigEntry<float> landingPenaltyHours;
		public static ConfigEntry<bool> travelPenaltyEnabled;
		public static ConfigEntry<float> companyVisitHours;
		public static ConfigEntry<float> floodRiseHour;
		public static ConfigEntry<float> floodPeakHour;
		public static ConfigEntry<float> floodDrainedHour;
		public static ConfigEntry<float> intervalSetting;
		public static ConfigEntry<ClockIcons> clockIcons;

		public static ConfigEntry<bool> notifyAdvanceWarning;
		public static ConfigEntry<bool> notifyLowDeadline;
		public static ConfigEntry<bool> notifyDayDeducted;
		public static ConfigEntry<bool> notifyTravelCost;
		public static ConfigEntry<bool> notifyLandingCost;
		public static ConfigEntry<bool> notifyZeroWarning;
		public static ConfigEntry<bool> notifyZeroWarningDayAndHour;

		public static ConfigEntry<bool> teamWipePenaltyEnabled;
		public static ConfigEntry<float> teamWipePenaltyHours;
		private static readonly Dictionary<string, ConfigEntry<float>> moonTravelHours = new Dictionary<string, ConfigEntry<float>>();

		public static ConfigEntry<bool> randomLanding;
		public static ConfigEntry<float> earliestLandingHour;
		public static ConfigEntry<float> latestLandingHour;
		public static ConfigEntry<bool> enemiesPresentOnLateLanding;
		public static ConfigEntry<bool> showMoonHourOnTerminal;
		public static ConfigEntry<bool> showMoonHourOnShipScreen;
		private static readonly Dictionary<string, ConfigEntry<bool>> moonRandomLanding = new Dictionary<string, ConfigEntry<bool>>();

		// in display order, for LethalConfig
		private static readonly List<ConfigEntryBase> all = new List<ConfigEntryBase>();

		public static ConfigFile Load()
		{
			ConfigFile cfg = P.Instance.Config;
			bool saveOnSet = cfg.SaveOnConfigSet;
			cfg.SaveOnConfigSet = false;
			LoadSaved();
			orphanEntries = Orphans(cfg);

			moonBlacklist = Bind(cfg, DayCycleSection, "Moon blacklist", "Gordion",
				"Moons that use the vanilla day. Comma-separated, e.g. Gordion, Embrion.");
			debugLogging = Bind(cfg, DayCycleSection, "Debug logging", false,
				"Extra log lines for bug reports.");

			dawnFadeEnabled = Bind(cfg, EnemyCycleSection, "Enable outside enemy cycle", false,
				"Outside night enemies leave in the morning and return at night.");
			daytimeCycleEnabled = Bind(cfg, EnemyCycleSection, "Enable daytime enemy cycle", false,
				"Daytime enemies return each morning after leaving at nightfall.");
			indoorDawnFade = Bind(cfg, EnemyCycleSection, "Enable indoor enemy cycle", false,
				"Some indoor enemies leave during the enemy cycle hours.");
			// each percent only exists while its group is on, the hours while any group is
			BindSetting(cfg, EnemyCycleSection, "Outside enemy despawn percent", 100, e => outsideDespawnPercent = e,
				"Percentage of outside enemies that leave each cycle.",
				new AcceptableValueRange<int>(0, 100), group: OutsidePercentGroup);
			BindSetting(cfg, EnemyCycleSection, "Indoor enemy despawn percent", 50, e => indoorDawnShare = e,
				"Percentage of indoor enemies that leave each cycle.",
				new AcceptableValueRange<int>(0, 100), group: IndoorPercentGroup);
			BindSetting(cfg, EnemyCycleSection, "Enemy cycle start hour", 4f, e => dawnStartHour = e,
				"Hour enemies start leaving (4.5 = 4:30 AM).",
				new AcceptableValueRange<float>(0f, 24f), group: CycleHoursGroup);
			BindSetting(cfg, EnemyCycleSection, "Enemy cycle end hour", 7f, e => dawnEndHour = e,
				"Hour enemies stop leaving; daytime enemies return after it.",
				new AcceptableValueRange<float>(0f, 24f), group: CycleHoursGroup);

			deadlineMode = Bind(cfg, DeadlineSection, "Deadline mode", DeadlineMode.OneDayExtended,
				"OneDayExtended = 1 day per visit, Days = 1 day per 18 hours on a moon, DayAndHour = per hour.");
			deadlineDaysSource = Bind(cfg, DeadlineSection, "Deadline days source", DeadlineDaysSource.GameOrOtherMods,
				"Deadline length from the game (and quota mods) or \"Deadline days\".");
			BindSetting(cfg, DeadlineSection, "Deadline days", 3, e => deadlineDays = e,
				"Days per deadline.",
				new AcceptableValueRange<int>(1, 30), group: DeadlineDaysGroup);
			// a mode's settings only exist while it's chosen; its notification toggles are per player and follow the host's mode
			BindSetting(cfg, DaysModeSection, "Counting hours", 18f, e => countingHours = e,
				"Hours on a moon before a day is taken.",
				new AcceptableValueRange<float>(1f, 48f));
			BindSetting(cfg, DaysModeSection, "Gift hours", 6f, e => giftHours = e,
				"Free hours after each day taken.",
				new AcceptableValueRange<float>(0f, 48f));
			BindSetting(cfg, DaysModeSection, "Alert lead time", 2f, e => alertLeadHours = e,
				"Hours of warning before a day is taken.",
				new AcceptableValueRange<float>(0f, 12f));
			BindSetting(cfg, DaysModeSection, "Advance warning", true, e => notifyAdvanceWarning = e,
				"Warning before a day is taken.");
			BindSetting(cfg, DaysModeSection, "Low deadline alert", true, e => notifyLowDeadline = e,
				"Alert when 1 day or less is left.");
			BindSetting(cfg, DaysModeSection, "Day deducted notice", true, e => notifyDayDeducted = e,
				"Notice when a day is taken.");
			BindSetting(cfg, DaysModeSection, "Deadline zero warning", true, e => notifyZeroWarning = e,
				"Warning before the deadline runs out.");
			BindSetting(cfg, DayAndHourModeSection, "Landing penalty enabled", false, e => landingPenaltyEnabled = e,
				"Each landing costs extra hours.");
			BindSetting(cfg, DayAndHourModeSection, "Landing penalty hours", 1f, e => landingPenaltyHours = e,
				"Hours taken per landing.",
				new AcceptableValueRange<float>(0f, 24f));
			BindSetting(cfg, DayAndHourModeSection, "Travel penalty enabled", false, e => travelPenaltyEnabled = e,
				"Each moon costs travel hours (set in 3.3).",
				isSwitch: true);
			BindSetting(cfg, DayAndHourModeSection, "Company visit cost", 0f, e => companyVisitHours = e,
				"Hours taken per Company visit.",
				new AcceptableValueRange<float>(0f, 24f));
			BindSetting(cfg, DayAndHourModeSection, "Team wipe penalty enabled", true, e => teamWipePenaltyEnabled = e,
				"A team wipe costs extra hours.");
			BindSetting(cfg, DayAndHourModeSection, "Team wipe penalty hours", 6f, e => teamWipePenaltyHours = e,
				"Hours taken per team wipe.",
				new AcceptableValueRange<float>(0f, 24f));
			BindSetting(cfg, DayAndHourModeSection, "Alert lead time", 2f, e => dayAndHourAlertLeadHours = e,
				"Hours of warning before the deadline runs out.",
				new AcceptableValueRange<float>(0f, 12f));
			BindSetting(cfg, DayAndHourModeSection, "Travel cost notice", true, e => notifyTravelCost = e,
				"Notice when travel or Company hours are taken.");
			BindSetting(cfg, DayAndHourModeSection, "Landing cost notice", true, e => notifyLandingCost = e,
				"Notice when landing hours are taken.");
			BindSetting(cfg, DayAndHourModeSection, "Deadline zero warning", true, e => notifyZeroWarningDayAndHour = e,
				"Warning before the deadline runs out.");

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

			randomLanding = Bind(cfg, LandingSection, "Random landing time", false,
				"Land at a random hour on each moon.");
			// the rest only exists while random landing is on
			BindSetting(cfg, LandingSection, "Earliest landing hour", 0f, e => earliestLandingHour = e,
				"Earliest random landing hour (6.5 = 6:30 AM).",
				new AcceptableValueRange<float>(0f, 24f), group: LandingSettingsGroup);
			BindSetting(cfg, LandingSection, "Latest landing hour", 24f, e => latestLandingHour = e,
				"Latest random landing hour.",
				new AcceptableValueRange<float>(0f, 24f), group: LandingSettingsGroup);
			BindSetting(cfg, LandingSection, "Enemies already present on late landing", true, e => enemiesPresentOnLateLanding = e,
				"Late landings find outdoor enemies already there.",
				group: LandingSettingsGroup);
			// per player, display only
			BindSetting(cfg, LandingSection, "Show moon hour on terminal", true, e => showMoonHourOnTerminal = e,
				"Show landing times on the terminal.",
				group: LandingSettingsGroup);
			BindSetting(cfg, LandingSection, "Show moon hour on ship screen", true, e => showMoonHourOnShipScreen = e,
				"Show the landing time on the ship screen.",
				group: LandingSettingsGroup);

			SetUpItemSections();
			MigrateOldEntries(cfg);
			foreach (ItemSection s in itemSections.Where(x => x.Settings != null && !x.On)) { DisableSection(s); }
			KeepFileOrder(cfg);
			cfg.Save();
			WriteSaved();
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

		private const string MoonKeyPrefix = "Travel hours - ";
		private const string RandomLandingPrefix = "Random landing - ";

		private static string MoonKey(string prefix, string planetName)
		{
			return prefix + new string(planetName.Where(c => "=\n\t\\\"'[]".IndexOf(c) < 0).ToArray()).Trim();
		}

		// null while the feature is off
		public static ConfigEntry<bool>? RandomLandingFor(string planetName)
		{
			string key = MoonKey(RandomLandingPrefix, planetName);
			if (moonRandomLanding.TryGetValue(key, out ConfigEntry<bool> entry)) { return entry; }
			ConfigEntry<bool>? created = BindItem(LandingMoonsSection, key, true, new ConfigDescription(
				$"Random landing hour on {planetName}."));
			if (created != null) { moonRandomLanding[key] = created; }
			return created;
		}

		// null while the feature is off
		public static ConfigEntry<float>? MoonTravelHours(string planetName, float defaultHours, int routePrice)
		{
			string key = MoonKey(MoonKeyPrefix, planetName);
			if (moonTravelHours.TryGetValue(key, out ConfigEntry<float> entry)) { return entry; }
			ConfigEntry<float>? created = BindItem(DeadlineMoonsSection, key, defaultHours, new ConfigDescription(
				$"Travel hours to {planetName}.",
				new AcceptableValueRange<float>(0f, 24f)));
			if (created != null) { moonTravelHours[key] = created; }
			return created;
		}

		#region per-item sections
		// sections / entries that only exist while they apply; hidden non-default values go to DayCycle.saved.json
		private class ItemSection
		{
			public string Name = "";
			public string? InSection; // a group of entries inside another section
			public string Section => InSection ?? Name;
			public Func<bool> Applies = () => true;
			public Func<string> OffReason = () => "";
			public Action Rebuild = () => { };
			public Action ClearCache = () => { };
			public List<Setting>? Settings; // setting section: bound again when it comes back
			public bool Shown;
			public bool On => Applies();
		}

		// BepInEx can't re-add a removed entry, so bind it again and repoint the field
		private abstract class Setting
		{
			public string Section = "", Key = "";
			public bool IsSwitch; // other sections depend on it
			public abstract ConfigEntryBase Bind(ConfigFile cfg, string? restore);
		}

		private class Setting<T> : Setting
		{
			public T Default = default!;
			public ConfigDescription Description = null!;
			public Action<ConfigEntry<T>> Assign = _ => { };

			public override ConfigEntryBase Bind(ConfigFile cfg, string? restore)
			{
				ConfigEntry<T> entry = cfg.Bind(Section, Key, Default, Description);
				if (restore != null) { entry.SetSerializedValue(restore); } // before the handler, so it doesn't fire here
				Assign(entry);
				if (IsSwitch) { entry.SettingChanged += (_, _) => RefreshSections(); }
				return entry;
			}
		}

		private static readonly List<ItemSection> itemSections = new List<ItemSection>();
		private static readonly Dictionary<string, List<Setting>> sectionSettings = new Dictionary<string, List<Setting>>();
		private static Dictionary<string, Dictionary<string, string>> saved = new Dictionary<string, Dictionary<string, string>>();
		private static string SavedPath => Path.Combine(Paths.ConfigPath, "DayCycle.saved.json");

		// BepInEx keeps unbound entries here and writes them back on save; it isn't public
		private static Dictionary<ConfigDefinition, string>? Orphans(ConfigFile cfg)
		{
			PropertyInfo? prop = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			return prop?.GetValue(cfg) as Dictionary<ConfigDefinition, string>;
		}

		// an entry of a setting section; a remembered value only applies when the file has none
		private static void BindSetting<T>(ConfigFile cfg, string section, string key, T defaultValue, Action<ConfigEntry<T>> assign, string description, AcceptableValueBase? range = null, bool isSwitch = false, string? group = null)
		{
			Setting<T> setting = new Setting<T> { Section = section, Key = key, Default = defaultValue, Description = new ConfigDescription(description, range), Assign = assign, IsSwitch = isSwitch };
			if (!sectionSettings.TryGetValue(group ?? section, out List<Setting>? list)) { list = sectionSettings[group ?? section] = new List<Setting>(); }
			list.Add(setting);
			all.Add(setting.Bind(cfg, TakeSaved(cfg, section, key)));
		}

		private static string? TakeSaved(ConfigFile cfg, string section, string key)
		{
			ConfigDefinition def = new ConfigDefinition(section, key);
			bool inFile = cfg.ContainsKey(def) || (orphanEntries?.ContainsKey(def) ?? false);
			if (!saved.TryGetValue(section, out Dictionary<string, string>? sectionSaved) || !sectionSaved.TryGetValue(key, out string? value)) { return null; }
			Forget(section, key);
			return inFile ? null : value;
		}

		private static void SetUpItemSections()
		{
			string modeIs() => $"{deadlineMode.Definition.Key} is {deadlineMode.Value}";
			itemSections.Add(new ItemSection { Name = EnemyCycleDaytimeSection, Applies = () => daytimeCycleEnabled.Value, OffReason = () => $"{daytimeCycleEnabled.Definition.Key} off", Rebuild = EnemyCycleRules.RegisterAll, ClearCache = () => ClearEnemyRules(EnemyCycleDaytimeSection) });
			itemSections.Add(new ItemSection { Name = EnemyCycleOutsideSection, Applies = () => dawnFadeEnabled.Value, OffReason = () => $"{dawnFadeEnabled.Definition.Key} off", Rebuild = EnemyCycleRules.RegisterAll, ClearCache = () => ClearEnemyRules(EnemyCycleOutsideSection) });
			itemSections.Add(new ItemSection { Name = EnemyCycleInsideSection, Applies = () => indoorDawnFade.Value, OffReason = () => $"{indoorDawnFade.Definition.Key} off", Rebuild = EnemyCycleRules.RegisterAll, ClearCache = () => ClearEnemyRules(EnemyCycleInsideSection) });
			itemSections.Add(new ItemSection { Name = OutsidePercentGroup, InSection = EnemyCycleSection, Applies = () => dawnFadeEnabled.Value, OffReason = () => $"{dawnFadeEnabled.Definition.Key} off", Settings = sectionSettings[OutsidePercentGroup] });
			itemSections.Add(new ItemSection { Name = CycleHoursGroup, InSection = EnemyCycleSection, Applies = () => dawnFadeEnabled.Value || daytimeCycleEnabled.Value || indoorDawnFade.Value, OffReason = () => "all enemy cycles off", Settings = sectionSettings[CycleHoursGroup] });
			itemSections.Add(new ItemSection { Name = IndoorPercentGroup, InSection = EnemyCycleSection, Applies = () => indoorDawnFade.Value, OffReason = () => $"{indoorDawnFade.Definition.Key} off", Settings = sectionSettings[IndoorPercentGroup] });
			itemSections.Add(new ItemSection { Name = DeadlineDaysGroup, InSection = DeadlineSection, Applies = () => deadlineDaysSource.Value == DeadlineDaysSource.DayCycle, OffReason = () => $"{deadlineDaysSource.Definition.Key} is {deadlineDaysSource.Value}", Settings = sectionSettings[DeadlineDaysGroup] });
			itemSections.Add(new ItemSection { Name = DaysModeSection, Applies = () => deadlineMode.Value == DeadlineMode.Days, OffReason = modeIs, Settings = sectionSettings[DaysModeSection] });
			itemSections.Add(new ItemSection { Name = DayAndHourModeSection, Applies = () => deadlineMode.Value == DeadlineMode.DayAndHour, OffReason = modeIs, Settings = sectionSettings[DayAndHourModeSection] });
			itemSections.Add(new ItemSection { Name = DeadlineMoonsSection, Applies = () => deadlineMode.Value == DeadlineMode.DayAndHour && travelPenaltyEnabled.Value,
				OffReason = () => deadlineMode.Value != DeadlineMode.DayAndHour ? modeIs() : $"{travelPenaltyEnabled.Definition.Key} off", Rebuild = Deadline.RebuildMoonList, ClearCache = moonTravelHours.Clear });
			itemSections.Add(new ItemSection { Name = LandingSettingsGroup, InSection = LandingSection, Applies = () => randomLanding.Value, OffReason = () => $"{randomLanding.Definition.Key} off", Settings = sectionSettings[LandingSettingsGroup] });
			itemSections.Add(new ItemSection { Name = LandingMoonsSection, Applies = () => randomLanding.Value, OffReason = () => $"{randomLanding.Definition.Key} off", Rebuild = Landing.CreateMoonEntries, ClearCache = moonRandomLanding.Clear });
			foreach (ItemSection s in itemSections) { s.Shown = s.On; }
			foreach (ConfigEntry<bool> toggle in new[] { daytimeCycleEnabled, dawnFadeEnabled, indoorDawnFade, randomLanding }) { toggle.SettingChanged += (_, _) => RefreshSections(); }
			deadlineMode.SettingChanged += (_, _) => RefreshSections();
			deadlineDaysSource.SettingChanged += (_, _) => RefreshSections();
		}

		private static bool SectionOn(string section)
		{
			ItemSection? s = itemSections.FirstOrDefault(x => x.Name == section);
			return s == null || s.On;
		}

		// a saved choice only applies when the file has no value yet
		private static ConfigEntry<T>? BindItem<T>(string section, string key, T defaultValue, ConfigDescription description)
		{
			ConfigDefinition def = new ConfigDefinition(section, key);
			bool inFile = P.Instance.Config.ContainsKey(def) || (orphanEntries?.ContainsKey(def) ?? false);
			saved.TryGetValue(section, out Dictionary<string, string>? sectionSaved);
			string? savedChoice = null;
			sectionSaved?.TryGetValue(key, out savedChoice);

			if (!SectionOn(section)) { return null; }

			ConfigEntry<T> entry = P.Instance.Config.Bind(section, key, defaultValue, description);
			if (!inFile && savedChoice != null) { entry.SetSerializedValue(savedChoice); }
			if (savedChoice != null)
			{
				sectionSaved!.Remove(key);
				if (sectionSaved.Count == 0) { saved.Remove(section); }
				WriteSaved();
			}
			RegisterLateLethalConfigItem(entry);
			return entry;
		}

		// in order, so a section shown again (3.2) is in place before the ones that depend on it (3.3)
		private static void RefreshSections()
		{
			foreach (ItemSection s in itemSections)
			{
				bool on = s.On;
				if (on == s.Shown) { continue; }
				s.Shown = on;
				if (on) { ShowSection(s); } else { DisableSection(s); }
			}
		}

		private static void ShowSection(ItemSection s)
		{
			if (s.Settings != null)
			{
				ConfigFile cfg = P.Instance.Config;
				foreach (Setting setting in s.Settings) { RegisterLateLethalConfigItem(setting.Bind(cfg, TakeSaved(cfg, setting.Section, setting.Key))); }
				KeepFileOrder(cfg);
				cfg.Save();
				WriteSaved();
			}
			// in a lobby now (LethalConfig shows it when the page is reopened), otherwise at the next lobby load
			else if (StartOfRound.Instance != null) { s.Rebuild(); }
			P.Log($"Config: \"{s.Name}\" shown again");
		}

		// at startup those entries are still orphans
		private static void StashDisabledSections(Dictionary<ConfigDefinition, string> orphans)
		{
			foreach (ItemSection s in itemSections.Where(x => !x.On && x.Settings == null))
			{
				List<ConfigDefinition> defs = orphans.Keys.Where(d => d.Section == s.Name).ToList();
				foreach (ConfigDefinition d in defs) { Remember(s.Name, d.Key, orphans[d]); orphans.Remove(d); }
				if (defs.Count > 0) { P.Log($"Config: \"{s.Name}\" hidden ({s.OffReason()}), {defs.Count} values remembered"); }
			}
		}

		// e.g. turned off from the main menu
		internal static void HideDisabledSections()
		{
			foreach (ItemSection s in itemSections.Where(x => !x.On)) { DisableSection(s); }
		}

		private static void DisableSection(ItemSection s)
		{
			ConfigFile cfg = P.Instance.Config;
			List<ConfigEntryBase> entries = cfg.Where(kv => kv.Key.Section == s.Section && (s.Settings == null || s.Settings.Any(x => x.Key == kv.Key.Key))).Select(kv => kv.Value).ToList();
			List<ConfigDefinition> orphans = s.Settings != null ? new List<ConfigDefinition>() : orphanEntries?.Keys.Where(d => d.Section == s.Name).ToList() ?? new List<ConfigDefinition>();
			if (entries.Count == 0 && orphans.Count == 0) { return; }

			int remembered = orphans.Count;
			foreach (ConfigEntryBase entry in entries)
			{
				if (Equals(entry.BoxedValue, entry.DefaultValue)) { Forget(s.Section, entry.Definition.Key); }
				else { Remember(s.Section, entry.Definition.Key, entry.GetSerializedValue()); remembered++; }
				cfg.Remove(entry.Definition);
				RemoveLethalConfigItem(entry);
			}
			foreach (ConfigDefinition d in orphans) { Remember(s.Name, d.Key, orphanEntries![d]); orphanEntries.Remove(d); }
			s.ClearCache();
			KeepFileOrder(cfg);
			cfg.Save();
			WriteSaved();
			P.Log($"Config: \"{s.Name}\" hidden ({s.OffReason()}){(remembered > 0 ? $", {remembered} values remembered in DayCycle.saved.json" : "")}");
		}

		// order: as bound in Load, then later entries in creation order
		private static int Rank(ConfigDefinition def, int index)
		{
			int i = all.FindIndex(e => e.Definition.Equals(def));
			return i >= 0 ? i : all.Count + index;
		}

		// an entry bound again lands in a freed slot of BepInEx's table, so put them back in order
		private static void KeepFileOrder(ConfigFile cfg)
		{
			PropertyInfo? prop = typeof(ConfigFile).GetProperty("Entries", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			if (!(prop?.GetValue(cfg) is Dictionary<ConfigDefinition, ConfigEntryBase> entries)) { return; }
			List<KeyValuePair<ConfigDefinition, ConfigEntryBase>> sorted = entries.Select((kv, index) => (kv, index)).OrderBy(x => Rank(x.kv.Key, x.index)).Select(x => x.kv).ToList();
			entries.Clear();
			foreach (KeyValuePair<ConfigDefinition, ConfigEntryBase> kv in sorted) { entries.Add(kv.Key, kv.Value); }
		}

		private static void Remember(string section, string key, string value)
		{
			if (!saved.TryGetValue(section, out Dictionary<string, string>? keys)) { keys = saved[section] = new Dictionary<string, string>(); }
			keys[key] = value;
		}

		private static void Forget(string section, string key)
		{
			if (saved.TryGetValue(section, out Dictionary<string, string>? keys) && keys.Remove(key) && keys.Count == 0) { saved.Remove(section); }
		}

		private static void LoadSaved()
		{
			try
			{
				if (File.Exists(SavedPath))
				{
					saved = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(SavedPath))
						?? new Dictionary<string, Dictionary<string, string>>();
				}
			}
			catch (Exception e)
			{
				P.Fail($"Could not read DayCycle.saved.json, remembered choices ignored: {e.Message}", LLV.Warning);
				saved = new Dictionary<string, Dictionary<string, string>>();
			}
		}

		private static void WriteSaved()
		{
			try
			{
				if (saved.Count == 0) { if (File.Exists(SavedPath)) { File.Delete(SavedPath); } return; }
				File.WriteAllText(SavedPath, JsonConvert.SerializeObject(saved, Formatting.Indented));
			}
			catch (Exception e)
			{
				P.Fail($"Could not write DayCycle.saved.json: {e.Message}", LLV.Warning);
			}
		}

		private static void ClearEnemyRules(string section)
		{
			foreach (string id in enemyRules.Keys.Where(k => k.StartsWith(section + "|")).ToList()) { enemyRules.Remove(id); }
		}
		#endregion

		#region dawn enemy rules
		// Default Vanilla: Giant Sapsucker, Kidnapper Fox, Circuit Bees (a new hive each return), Butler Bees, Cadaver Bloom
		private static readonly string[] builtInVanilla = { "giantkiwi", "bushwolf" };
		private static Dictionary<ConfigDefinition, string>? orphanEntries;

		public static ConfigEntry<EnemyCycleRule>? EnemyRule(EnemyType type)
		{
			EnemyCycleGroup group = EnemyCycleRules.GroupOf(type);
			string section = group == EnemyCycleGroup.Daytime ? EnemyCycleDaytimeSection : group == EnemyCycleGroup.Outside ? EnemyCycleOutsideSection : EnemyCycleInsideSection;
			string key = new string(type.enemyName.Where(c => "=\n\t\\\"'[]".IndexOf(c) < 0).ToArray()).Trim();
			string id = section + "|" + key;
			if (enemyRules.TryGetValue(id, out ConfigEntry<EnemyCycleRule> entry)) { return entry; }

			bool oldBird = EnemyCycleRules.IsOldBird(type);
			bool bees = group == EnemyCycleGroup.Daytime && EnemyCycleRules.IsCircuitBees(type);
			bool butlerBees = EnemyCycleRules.IsButlerBees(type);
			bool bloom = EnemyCycleRules.IsCadaverBloom(type);
			EnemyCycleRule defaultRule = bees || butlerBees || bloom || EnemyCycleRules.MatchesAnyName(type, builtInVanilla) ? EnemyCycleRule.Vanilla : EnemyCycleRule.Cycled;
			string what = group == EnemyCycleGroup.Daytime
				? "Cycled returns each morning, Vanilla doesn't return"
				: group == EnemyCycleGroup.Outside
					? (oldBird ? "Cycled powers down during the enemy cycle, Vanilla stays awake"
						: "Cycled leaves during the enemy cycle, Vanilla stays")
					: EnemyCycleRules.NeverRemovedIndoors(type)
						? "never removed, whatever the setting"
						: "Cycled may leave during the enemy cycle, Vanilla stays";
			ConfigEntry<EnemyCycleRule>? created = BindItem(section, key, defaultRule, new ConfigDescription(
				$"{type.enemyName}: {what}."));
			if (created != null) { enemyRules[id] = created; }
			return created;
		}
		#endregion

		#region migration
		// renamed entries keep their values; every other leftover is dropped
		private static void MigrateOldEntries(ConfigFile cfg)
		{
			Dictionary<ConfigDefinition, ConfigEntryBase> renamed = new Dictionary<ConfigDefinition, ConfigEntryBase>
			{
				{ new ConfigDefinition("Other", "Clock Update Interval"), intervalSetting },
			};

			if (!(orphanEntries is Dictionary<ConfigDefinition, string> orphans))
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

			// per-item entries are unbound (orphans) at startup
			StashDisabledSections(orphans);

			List<ConfigDefinition> old = orphans.Keys.Where(d => !(d.Section == DeadlineMoonsSection && d.Key.StartsWith(MoonKeyPrefix))
				&& !(d.Section == LandingMoonsSection && d.Key.StartsWith(RandomLandingPrefix))
				&& d.Section != EnemyCycleDaytimeSection && d.Section != EnemyCycleOutsideSection && d.Section != EnemyCycleInsideSection).ToList();
			foreach (ConfigDefinition def in old) { orphans.Remove(def); }
			if (old.Count > 0) { P.Log($"Config: removed {old.Count} old entries"); }
		}
		#endregion

		#region LethalConfig
		// Reflection only (soft dependency); explicit items so they apply live instead of auto-generated "requires restart".
		private static Assembly? lethalConfig;
		private static MethodInfo? lethalConfigAdd;
		private static object? lethalConfigLastItem;

		private static void RegisterLethalConfig()
		{
			try
			{
				lethalConfig = Chainloader.PluginInfos[LethalConfigGuid].Instance.GetType().Assembly;
				Type baseItem = lethalConfig.GetType("LethalConfig.ConfigItems.BaseConfigItem", true);
				lethalConfigAdd = lethalConfig.GetType("LethalConfig.LethalConfigManager", true).GetMethod("AddConfigItem", new[] { baseItem, typeof(Assembly) });
				// a hidden section's entries come in when it's shown
				foreach (ConfigEntryBase entry in all.Where(e => P.Instance.Config.ContainsKey(e.Definition) && ReferenceEquals(P.Instance.Config[e.Definition], e))) { AddLethalConfigItem(entry); }
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
			lethalConfigLastItem = item;
		}

		// LethalConfig orders sections by first item added; per-item entries come later, so re-sort by section
		private static void RegisterLateLethalConfigItem(ConfigEntryBase entry)
		{
			if (lethalConfigAdd == null) { return; }
			try
			{
				AddLethalConfigItem(entry);
				if (!(LethalConfigItems() is System.Collections.IList items)) { return; }

				PropertyInfo? sectionProp = null;
				PropertyInfo? entryProp = null;
				List<object> sorted = items.Cast<object>()
					.Select((item, index) => (item, index))
					.OrderBy(x => (string?)(sectionProp ??= x.item.GetType().GetProperty("Section", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))?.GetValue(x.item) ?? "", StringComparer.Ordinal)
					.ThenBy(x => (entryProp ??= x.item.GetType().GetProperty("BaseConfigEntry", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))?.GetValue(x.item) is ConfigEntryBase e ? Rank(e.Definition, x.index) : all.Count + x.index)
					.Select(x => x.item)
					.ToList();
				items.Clear();
				foreach (object item in sorted) { items.Add(item); }
			}
			catch (Exception e)
			{
				P.Fail($"LethalConfig: could not add {entry.Definition.Key}: {e.GetType().Name}: {e.Message}", LLV.Warning);
			}
		}

		// Mod.ConfigItems, reached through any item we registered
		private static System.Collections.IList? LethalConfigItems()
		{
			const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
			object? owner = lethalConfigLastItem?.GetType().GetProperty("Owner", flags)?.GetValue(lethalConfigLastItem);
			return owner?.GetType().GetProperty("ConfigItems", flags)?.GetValue(owner) as System.Collections.IList;
		}

		// LethalConfig copies its list before applying changes, so removing items from a setting-changed handler is safe
		private static void RemoveLethalConfigItem(ConfigEntryBase entry)
		{
			if (lethalConfigAdd == null) { return; }
			try
			{
				if (!(LethalConfigItems() is System.Collections.IList items)) { return; }
				PropertyInfo? entryProp = null;
				for (int i = items.Count - 1; i >= 0; i--)
				{
					object item = items[i];
					entryProp ??= item.GetType().GetProperty("BaseConfigEntry", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
					if (ReferenceEquals(entryProp?.GetValue(item), entry)) { items.RemoveAt(i); }
				}
			}
			catch (Exception e)
			{
				P.Fail($"LethalConfig: could not remove {entry.Definition.Key}: {e.GetType().Name}: {e.Message}", LLV.Warning);
			}
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
