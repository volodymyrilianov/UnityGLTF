using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityGLTF.Extensions;

namespace Immersion.Export
{
	/// <summary>
	/// Batch-mode export of one HUMANOID DESCRIPTOR per avatar rig: the live Unity Avatar's bone map +
	/// its T-pose (<c>humanDescription.skeleton</c>), in glTF space, under the node names the fork writes
	/// into the rig's GLBs. Feeds the web Unity-like humanoid retarget (docs
	/// <c>HumanoidRetarget_WIP/DESIGN.md</c>, "Descriptor").
	///
	/// Usage:
	/// <code>
	/// Unity.exe -batchmode -quit -nographics -projectPath &lt;proj&gt; \
	///   -executeMethod Immersion.Export.HumanoidDescriptorExporter.ExportDescriptors \
	///   -rigList "C:/work/rig-list.json" -out "C:/work/unity-descriptors.json"
	/// </code>
	///
	/// - <c>-rigList</c>: JSON <c>{ "rigs": [ { "rigKey": "camilla", "asset": "Assets/…/X.prefab" } ] }</c>,
	///   the same rig assets <see cref="ClipBatchExporter"/> bakes pool clips on (HelperTools
	///   <c>anim-library/pool-clips.json</c> <c>rigs.&lt;key&gt;.asset</c>).
	/// - <c>-out</c>: output file, <c>{ "version": 1, "descriptors": [ … ] }</c>.
	///
	/// Per rig the asset is instantiated exactly like the pool-clip export (InstantiatePrefab, unpacked,
	/// active), so node names and the hierarchy are the GLB's: the export root keeps the asset name (no
	/// "(Clone)"), every transform below it becomes a node of the same name. The bone map and the T-pose
	/// come from the LIVE <c>Animator.avatar</c> (never the .meta text: auto-mapped / copied avatars have
	/// an empty map there). <c>humanName</c> is written as the <see cref="HumanBodyBones"/> enum name
	/// (<c>"Left Thumb Proximal"</c> → <c>LeftThumbProximal</c>).
	///
	/// <c>nodes</c>: every transform from the export root down (pre-order), parent from the instantiated
	/// hierarchy, local T/R/S from <c>humanDescription.skeleton</c> matched by name (the skeleton's root
	/// entry, Unity's "&lt;model&gt;(Clone)", matches the Animator's transform); a transform absent from the
	/// skeleton keeps its instantiated local TRS and is marked <c>"fromSkeleton": false</c>. Unity → glTF
	/// via the fork's <see cref="SchemaExtensions"/> (position (−x,y,z), rotation (x,−y,−z,w)).
	///
	/// Exit code 0 = every rig written; 1 = at least one failed (the file still holds the others). One line
	/// per rig: <c>[HumanoidDescriptorExporter] OK|FAIL &lt;rigKey&gt;</c>.
	/// </summary>
	public static class HumanoidDescriptorExporter
	{
		private const string Tag = "[HumanoidDescriptorExporter]";

		[Serializable]
		private class RigEntry
		{
			public string rigKey;
			public string asset;
		}

		[Serializable]
		private class RigList
		{
			public RigEntry[] rigs;
		}

		public static void ExportDescriptors()
		{
			var listPath = CliArgs.Get("-rigList");
			var outPath = CliArgs.Get("-out");
			if (string.IsNullOrEmpty(listPath) || string.IsNullOrEmpty(outPath))
			{
				Debug.LogError(Tag + " usage: -rigList <rig-list.json> -out <descriptors.json>");
				CliArgs.Exit(1);
				return;
			}

			RigList list;
			try
			{
				list = JsonUtility.FromJson<RigList>(File.ReadAllText(listPath));
			}
			catch (Exception e)
			{
				Debug.LogError(Tag + " FAIL cannot read rig list '" + listPath + "': " + e.Message);
				CliArgs.Exit(1);
				return;
			}
			if (list?.rigs == null || list.rigs.Length == 0)
			{
				Debug.LogError(Tag + " FAIL rig list '" + listPath + "' has no rigs");
				CliArgs.Exit(1);
				return;
			}

			EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
			var descriptors = new List<string>();
			var failures = 0;
			foreach (var rig in list.rigs)
			{
				GameObject instance = null;
				try
				{
					var asset = AssetDatabase.LoadAssetAtPath<GameObject>(rig.asset);
					if (!asset) throw new Exception("asset not found at '" + rig.asset + "'");
					instance = (GameObject)PrefabUtility.InstantiatePrefab(asset);
					instance.transform.localPosition = Vector3.zero;
					if (PrefabUtility.IsPartOfPrefabInstance(instance))
						PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
					if (!instance.activeSelf) instance.SetActive(true);

					descriptors.Add(Describe(rig, instance, out var summary));
					Debug.Log(Tag + " OK " + rig.rigKey + " " + summary);
				}
				catch (Exception e)
				{
					failures++;
					Debug.LogError(Tag + " FAIL " + rig.rigKey + ": " + e);
				}
				finally
				{
					if (instance) UnityEngine.Object.DestroyImmediate(instance);
				}
			}

			var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
			if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
			var sb = new StringBuilder();
			sb.Append("{\n  \"version\": 1,\n  \"descriptors\": [");
			for (var i = 0; i < descriptors.Count; i++) sb.Append(i == 0 ? "\n" : ",\n").Append(descriptors[i]);
			sb.Append(descriptors.Count > 0 ? "\n  ]\n}\n" : "]\n}\n");
			File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));

			Debug.Log(Tag + " done: " + (list.rigs.Length - failures) + "/" + list.rigs.Length + " written to " + outPath);
			CliArgs.Exit(failures > 0 ? 1 : 0);
		}

		private static string Describe(RigEntry rig, GameObject instance, out string summary)
		{
			var animator = instance.GetComponentInChildren<Animator>(true);
			if (!animator) throw new Exception("no Animator under '" + instance.name + "'");
			var avatar = animator.avatar;
			if (!avatar) throw new Exception("Animator on '" + animator.name + "' has no Avatar");
			if (!avatar.isValid || !avatar.isHuman) throw new Exception("Avatar '" + avatar.name + "' is not a valid humanoid Avatar");
			var hd = avatar.humanDescription;

			// Bone map: humanName (HumanTrait.BoneName, spaced for fingers) → HumanBodyBones enum name.
			var traitNames = HumanTrait.BoneName;
			var bones = new List<(string human, string bone)>();
			foreach (var hb in hd.human)
			{
				var index = Array.IndexOf(traitNames, hb.humanName);
				if (index < 0 || index >= (int)HumanBodyBones.LastBone)
					throw new Exception("unknown humanName '" + hb.humanName + "' (bone '" + hb.boneName + "')");
				bones.Add((((HumanBodyBones)index).ToString(), hb.boneName));
			}
			bones.Sort((a, b) => ((int)Enum.Parse(typeof(HumanBodyBones), a.human)).CompareTo((int)Enum.Parse(typeof(HumanBodyBones), b.human)));

			// Cross-check with the Animator's own resolution (the live map Unity animates with).
			animator.Rebind();
			var mismatches = new List<string>();
			foreach (var (human, bone) in bones)
			{
				var t = animator.GetBoneTransform((HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), human));
				if (!t || t.name != bone) mismatches.Add(human + ": map '" + bone + "' vs animator '" + (t ? t.name : "<none>") + "'");
			}
			if (mismatches.Count > 0) throw new Exception("humanDescription map disagrees with Animator.GetBoneTransform: " + string.Join("; ", mismatches));
			// Avatar.humanScale is the Animator's (UnityEngine.Avatar exposes none); valid once the Animator is bound.
			var humanScale = animator.humanScale;
			if (!(humanScale > 0f) || float.IsInfinity(humanScale)) throw new Exception("Animator.humanScale is " + humanScale);

			// T-pose by name. The skeleton root entry is Unity's "<model>(Clone)": it stands for the Animator's transform.
			var skeleton = new Dictionary<string, SkeletonBone>();
			var duplicateSkeleton = 0;
			foreach (var sk in hd.skeleton)
			{
				if (skeleton.ContainsKey(sk.name)) duplicateSkeleton++;
				else skeleton[sk.name] = sk;
			}
			SkeletonBone? rootBone = null;
			if (hd.skeleton.Length > 0 && hd.skeleton[0].name.EndsWith("(Clone)", StringComparison.Ordinal)) rootBone = hd.skeleton[0];

			var transforms = new List<Transform>();
			Collect(instance.transform, transforms);
			var names = new HashSet<string>();
			var duplicates = new SortedSet<string>();
			foreach (var t in transforms)
				if (!names.Add(t.name)) duplicates.Add(t.name);
			foreach (var (_, bone) in bones)
				if (duplicates.Contains(bone)) throw new Exception("mapped bone '" + bone + "' is not unique in the hierarchy");

			var inv = CultureInfo.InvariantCulture;
			string F(float v) => v.ToString("R", inv);
			string Q(string v) => "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

			var sb = new StringBuilder();
			sb.Append("    {\n");
			sb.Append("      \"version\": 1,\n");
			sb.Append("      \"rigKey\": ").Append(Q(rig.rigKey)).Append(",\n");
			sb.Append("      \"source\": \"unity\",\n");
			sb.Append("      \"unity\": { \"asset\": ").Append(Q(rig.asset)).Append(", \"avatar\": ").Append(Q(avatar.name)).Append(" },\n");
			sb.Append("      \"humanScale\": ").Append(F(humanScale)).Append(",\n");
			sb.Append("      \"root\": ").Append(Q(animator.transform.name)).Append(",\n");
			sb.Append("      \"bones\": {");
			for (var i = 0; i < bones.Count; i++)
				sb.Append(i == 0 ? "\n        " : ",\n        ").Append(Q(bones[i].human)).Append(": ").Append(Q(bones[i].bone));
			sb.Append(bones.Count > 0 ? "\n      },\n" : "},\n");

			sb.Append("      \"nodes\": [");
			var fromInstance = 0;
			for (var i = 0; i < transforms.Count; i++)
			{
				var t = transforms[i];
				SkeletonBone sk;
				bool found;
				if (t == animator.transform && rootBone.HasValue) { sk = rootBone.Value; found = true; }
				else found = skeleton.TryGetValue(t.name, out sk);
				Vector3 p = found ? sk.position : t.localPosition;
				Quaternion r = found ? sk.rotation : t.localRotation;
				Vector3 s = found ? sk.scale : t.localScale;
				if (!found) fromInstance++;
				var gp = p.ToGltfVector3Convert();
				var gr = r.ToGltfQuaternionConvert();
				var parent = t == instance.transform ? null : t.parent;
				sb.Append(i == 0 ? "\n        " : ",\n        ");
				sb.Append("{ \"name\": ").Append(Q(t.name))
					.Append(", \"parent\": ").Append(parent ? Q(parent.name) : "null")
					.Append(", \"t\": [").Append(F(gp.X)).Append(", ").Append(F(gp.Y)).Append(", ").Append(F(gp.Z)).Append("]")
					.Append(", \"r\": [").Append(F(gr.X)).Append(", ").Append(F(gr.Y)).Append(", ").Append(F(gr.Z)).Append(", ").Append(F(gr.W)).Append("]")
					.Append(", \"s\": [").Append(F(s.x)).Append(", ").Append(F(s.y)).Append(", ").Append(F(s.z)).Append("]");
				if (!found) sb.Append(", \"fromSkeleton\": false");
				sb.Append(" }");
			}
			sb.Append(transforms.Count > 0 ? "\n      ],\n" : "],\n");

			sb.Append("      \"settings\": { \"armTwist\": ").Append(F(hd.upperArmTwist))
				.Append(", \"foreArmTwist\": ").Append(F(hd.lowerArmTwist))
				.Append(", \"upperLegTwist\": ").Append(F(hd.upperLegTwist))
				.Append(", \"legTwist\": ").Append(F(hd.lowerLegTwist))
				.Append(", \"armStretch\": ").Append(F(hd.armStretch))
				.Append(", \"legStretch\": ").Append(F(hd.legStretch))
				.Append(", \"feetSpacing\": ").Append(F(hd.feetSpacing))
				.Append(", \"hasTranslationDoF\": ").Append(hd.hasTranslationDoF ? "true" : "false").Append(" }\n");
			sb.Append("    }");

			summary = "avatar '" + avatar.name + "', root '" + animator.transform.name + "'"
				+ (animator.transform == instance.transform ? "" : " (export root '" + instance.name + "')")
				+ ", " + bones.Count + " bones, " + transforms.Count + " nodes (" + fromInstance + " not in skeleton, skeleton "
				+ hd.skeleton.Length + (duplicateSkeleton > 0 ? ", " + duplicateSkeleton + " duplicate skeleton names" : "")
				+ (rootBone.HasValue ? ", skeleton root '" + rootBone.Value.name + "'" : ", NO (Clone) skeleton root") + ")"
				+ (duplicates.Count > 0 ? ", duplicate node names: " + string.Join(", ", duplicates) : "")
				+ ", humanScale " + F(humanScale);
			return sb.ToString();
		}

		private static void Collect(Transform t, List<Transform> into)
		{
			into.Add(t);
			for (var i = 0; i < t.childCount; i++) Collect(t.GetChild(i), into);
		}
	}
}
