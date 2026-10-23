using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// Batch entry point: Unity.exe -batchmode -quit -projectPath UnityProject -executeMethod DayCycleBundleBuilder.Build
public static class DayCycleBundleBuilder
{
	const string AssetDir = "Assets/DayCycle";
	const string BundleName = "daycycle";
	const string OutputDir = "AssetBundles";

	// Plugin.cs loads the first three by path; CompanyLevel ships alongside them
	static readonly string[] Controllers =
	{
		"SunAnimContainer_24",
		"SunAnimContainer 1_24",
		"BlizzardSunAnimContainer_24",
		"SunAnimContainerCompanyLevel_24",
	};

	// MonoScripts the clips' curves are bound to
	static readonly Dictionary<string, string> RequiredScripts = new Dictionary<string, string>
	{
		{ "7a68c43fe1f2a47cfa234b5eeaa98012", "HDAdditionalLightData" },
		{ "172515602e62fb746b5d573b38a5fe58", "Volume" },
	};

	static void Log(string msg) => Debug.Log("[DayCycle] " + msg);

	public static void Build()
	{
		var errors = new List<string>();

		foreach (var kv in RequiredScripts)
		{
			string path = AssetDatabase.GUIDToAssetPath(kv.Key);
			var script = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<MonoScript>(path);
			string cls = script != null && script.GetClass() != null ? script.GetClass().Name : "<none>";
			Log($"Script guid {kv.Key} -> {(string.IsNullOrEmpty(path) ? "<unresolved>" : path)} (class {cls})");
			if (cls != kv.Value)
				errors.Add($"guid {kv.Key} should resolve to {kv.Value}, got {cls}");
		}

		// Rewrite clips so derived binding data matches the hand-stripped curves
		string[] clipPaths = AssetDatabase.FindAssets("t:AnimationClip", new[] { AssetDir }).Select(AssetDatabase.GUIDToAssetPath).ToArray();
		AssetDatabase.ForceReserializeAssets(clipPaths);

		foreach (string name in Controllers)
		{
			string path = $"{AssetDir}/{name}.controller";
			var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
			if (controller == null)
			{
				errors.Add($"controller not found: {path}");
				continue;
			}
			Log($"Controller {name}");
			foreach (var layer in controller.layers)
				foreach (var state in AllStates(layer.stateMachine))
				{
					var clip = state.motion as AnimationClip;
					if (clip == null)
						errors.Add($"{name}: state '{state.name}' has no clip");
					else
						Log($"  state '{state.name}' -> {AssetDatabase.GetAssetPath(clip)}");
				}
			AssetImporter.GetAtPath(path).SetAssetBundleNameAndVariant(BundleName, "");
		}

		int broken = 0;
		foreach (string clipPath in clipPaths)
		{
			var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
			var bindings = AnimationUtility.GetCurveBindings(clip);
			var refBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
			foreach (var b in bindings)
			{
				// A curve bound to a missing script comes back as plain MonoBehaviour (or no type at all)
				if (b.type == null || b.type == typeof(MonoBehaviour))
				{
					broken++;
					Debug.LogWarning($"[DayCycle] Broken curve in {Path.GetFileName(clipPath)}: path '{b.path}' property '{b.propertyName}' type {b.type?.Name ?? "<null>"}");
				}
			}
			foreach (var b in refBindings)
				Debug.LogWarning($"[DayCycle] Object reference curve left in {Path.GetFileName(clipPath)}: {b.path}.{b.propertyName}");
			Log($"Clip {Path.GetFileName(clipPath)}: {bindings.Length} curves, {refBindings.Length} object-reference curves");
		}
		Log($"Broken curves: {broken}");

		if (errors.Count > 0)
		{
			foreach (string e in errors)
				Debug.LogError("[DayCycle] " + e);
			throw new Exception($"DayCycle bundle build aborted: {errors.Count} error(s)");
		}

		AssetDatabase.SaveAssets();
		Directory.CreateDirectory(OutputDir);
		var manifest = BuildPipeline.BuildAssetBundles(OutputDir, BuildAssetBundleOptions.None, BuildTarget.StandaloneWindows64);
		if (manifest == null)
			throw new Exception("BuildAssetBundles failed");

		string bundlePath = Path.Combine(OutputDir, BundleName);
		Log($"Bundle {bundlePath}: {new FileInfo(bundlePath).Length} bytes");
		foreach (string asset in AssetDatabase.GetAssetPathsFromAssetBundle(BundleName))
			Log($"  explicit asset: {asset}");
		var bundle = AssetBundle.LoadFromFile(bundlePath);
		foreach (string asset in bundle.GetAllAssetNames())
			Log($"  in bundle: {asset}");
		bundle.Unload(true);
	}

	static IEnumerable<AnimatorState> AllStates(AnimatorStateMachine machine)
	{
		foreach (var s in machine.states)
			yield return s.state;
		foreach (var sub in machine.stateMachines)
			foreach (var s in AllStates(sub.stateMachine))
				yield return s;
	}
}
