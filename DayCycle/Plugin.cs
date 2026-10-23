global using LLV = BepInEx.Logging.LogLevel;
using BepInEx;
using BepInEx.Configuration;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DayCycle
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
	[BepInDependency(ModConfig.LethalConfigGuid, BepInDependency.DependencyFlags.SoftDependency)]
	public class P : BaseUnityPlugin
	{
		internal static P Instance;
		public static ConfigFile config;
		internal static AssetBundle assets;

		private void Awake()
		{
			Instance = this;
			config = ModConfig.Load();
			assets = AssetBundle.LoadFromFile(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "daycycle"));

			// without the bundle the rest still works; the suns keep their 18-hour animations
			if (assets == null) { Fail("asset bundle \"daycycle\" not found next to DayCycle.dll, suns are not replaced"); }
			else
			{
				API.sunAnimators["SunAnimContainer"] = assets.LoadAsset<RuntimeAnimatorController>("Assets/DayCycle/SunAnimContainer_24.controller");
				API.sunAnimators["SunAnimContainer 1"] = assets.LoadAsset<RuntimeAnimatorController>("Assets/DayCycle/SunAnimContainer 1_24.controller");
				API.sunAnimators["BlizzardSunAnimContainer"] = assets.LoadAsset<RuntimeAnimatorController>("Assets/DayCycle/BlizzardSunAnimContainer_24.controller");
			}

			DayCycle.Patch();
			FallbackSun.Patch();

			Logger.LogInfo($"{MyPluginInfo.PLUGIN_GUID} [{MyPluginInfo.PLUGIN_VERSION}] loaded");
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		public static string Loc([CallerMemberName] string name = "", [CallerLineNumber] int ln = 0)
		{
			return $"[{name}, ln: {ln}]";
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		public static void Log(object data, LLV lvl = LLV.Message)
		{
			Instance.Logger.Log(lvl, data);
		}

		// only with "Debug logging" on
		[MethodImpl(MethodImplOptions.NoInlining)]
		public static void LogDebug(string data)
		{
			if (DebugOn) { Instance.Logger.Log(LLV.Info, "[Debug] " + data); }
		}

		internal static bool DebugOn => ModConfig.debugLogging != null && ModConfig.debugLogging.Value;

		[MethodImpl(MethodImplOptions.NoInlining)]
		public static void Fail(object data, LLV lvl = LLV.Error, [CallerMemberName] string name = "", [CallerLineNumber] int ln = 0)
		{
			Instance.Logger.Log(lvl, P.Loc(name, ln) + " " + data);
		}
	}
}
