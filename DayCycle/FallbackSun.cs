using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace DayCycle
{
	// fallback suns: night pose is the clip's last frame and 6 AM its first, can't just move the time value,
	// so blend two frozen frames 3 -> 6 AM and land on the animator's pose at 6 AM
	public static class FallbackSun
	{
		private const float DawnStartHour = 3f;
		private const float DawnEndHour = 6f;

		// true: transforms (sun direction) stay at the night pose, only lights/colours/sky/fog blend
		private static readonly bool HoldSunDirection = false;

		private const float NightPose = 0.99f; // vanilla's cap

		private static PlayableGraph graph;
		private static AnimationLayerMixerPlayable mixer;
		private static Animator? blendedSun;
		private static bool warnedNoClip;

		internal static void Patch()
		{
			DayCycle.Hook("TimeOfDay.Update (fallback dawn)", () => On.TimeOfDay.Update += TimeOfDay_Update);
		}

		private static void TimeOfDay_Update(On.TimeOfDay.orig_Update orig, TimeOfDay self)
		{
			orig(self);

			Animator? sun = DayCycle.fallbackSun;
			bool dawn = !MoonBlacklist.Active && sun != null && self.sunAnimator == sun && self.currentDayTimeStarted && InDawnWindow(self, out _);
			if (!dawn)
			{
				Stop();
				return;
			}
			if (blendedSun != sun) { Start(sun!); }
			if (graph.IsValid())
			{
				InDawnWindow(self, out float progress);
				mixer.SetInputWeight(1, progress);
			}
		}

		private static bool InDawnWindow(TimeOfDay tod, out float progress)
		{
			float clock = ((tod.normalizedTimeOfDay % 1f + 1f) % 1f * 24f + 6f) % 24f;
			progress = Mathf.Clamp01((clock - DawnStartHour) / (DawnEndHour - DawnStartHour));
			return clock >= DawnStartHour && clock < DawnEndHour;
		}

		private static void Start(Animator sun)
		{
			Stop();
			blendedSun = sun;

			// the weather variant playing when the blend starts
			AnimationClip? clip = null;
			float best = -1f;
			foreach (AnimatorClipInfo info in sun.GetCurrentAnimatorClipInfo(0))
			{
				if (info.clip != null && info.weight > best) { clip = info.clip; best = info.weight; }
			}
			if (clip == null)
			{
				if (!warnedNoClip) { warnedNoClip = true; P.Fail($"Sun: no clip to blend for {sun.runtimeAnimatorController?.name}; dawn will switch at 6 AM", LLV.Warning); }
				return;
			}

			graph = PlayableGraph.Create("DayCycle fallback dawn");
			graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
			mixer = AnimationLayerMixerPlayable.Create(graph, 2);
			graph.Connect(HeldPose(clip, NightPose), 0, mixer, 0);
			graph.Connect(HeldPose(clip, 0f), 0, mixer, 1);
			mixer.SetInputWeight(0, 1f);
			mixer.SetInputWeight(1, 0f);
			if (HoldSunDirection) { mixer.SetLayerMaskFromAvatarMask(1, TransformsExcluded(sun.transform)); }

			AnimationPlayableOutput output = AnimationPlayableOutput.Create(graph, "Sun", sun);
			output.SetSourcePlayable(mixer);
			graph.Play();
			P.Log($"Sun: dawn blend started for {sun.runtimeAnimatorController?.name} ({clip.name})");
		}

		private static AnimationClipPlayable HeldPose(AnimationClip clip, float normalizedTime)
		{
			AnimationClipPlayable pose = AnimationClipPlayable.Create(graph, clip);
			pose.SetApplyFootIK(false);
			pose.SetTime(normalizedTime * clip.length);
			pose.SetSpeed(0);
			return pose;
		}

		private static AvatarMask TransformsExcluded(Transform root)
		{
			AvatarMask mask = new AvatarMask();
			mask.AddTransformPath(root, true);
			for (int i = 0; i < mask.transformCount; i++) { mask.SetTransformActive(i, false); }
			return mask;
		}

		private static void Stop()
		{
			if (graph.IsValid()) { graph.Destroy(); }
			blendedSun = null;
		}
	}
}
