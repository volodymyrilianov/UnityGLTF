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
	/// <c>muscles</c>: Unity's muscle → pose map of the Avatar (zero pose, one axis + linear range per DoF, twist
	/// distribution), measured with <see cref="HumanPoseHandler"/>; see <see cref="ProbeMuscles"/>. <c>muscles.com</c>: Unity's centre of
	/// mass of the Avatar (per body bone a vector u, COM = hips + Σ R_bone · u), see <see cref="ProbeCenterOfMass"/>. Optional
	/// <c>-checkOut &lt;file&gt;</c> writes the random check poses every rig was posed with, for the web-side replay.
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
			var checks = new List<string>();
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

					descriptors.Add(Describe(rig, instance, out var summary, out var check));
					checks.Add(check);
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

			var checkPath = CliArgs.Get("-checkOut");
			if (!string.IsNullOrEmpty(checkPath))
			{
				var cdir = Path.GetDirectoryName(Path.GetFullPath(checkPath));
				if (!string.IsNullOrEmpty(cdir)) Directory.CreateDirectory(cdir);
				File.WriteAllText(checkPath, "{\n  \"version\": 1,\n  \"rigs\": [\n" + string.Join(",\n", checks) + "\n  ]\n}\n", new UTF8Encoding(false));
			}

			Debug.Log(Tag + " done: " + (list.rigs.Length - failures) + "/" + list.rigs.Length + " written to " + outPath);
			CliArgs.Exit(failures > 0 ? 1 : 0);
		}

		private static string Describe(RigEntry rig, GameObject instance, out string summary, out string check)
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
				.Append(", \"hasTranslationDoF\": ").Append(hd.hasTranslationDoF ? "true" : "false").Append(" },\n");
			// Last: the probe poses the instance (SetHumanPose); everything above reads the skeleton / rest.
			sb.Append("      \"muscles\": ").Append(ProbeMuscles(rig, animator, out var muscleSummary, out check)).Append("\n");
			sb.Append("    }");

			summary = "avatar '" + avatar.name + "', root '" + animator.transform.name + "'"
				+ (animator.transform == instance.transform ? "" : " (export root '" + instance.name + "')")
				+ ", " + bones.Count + " bones, " + transforms.Count + " nodes (" + fromInstance + " not in skeleton, skeleton "
				+ hd.skeleton.Length + (duplicateSkeleton > 0 ? ", " + duplicateSkeleton + " duplicate skeleton names" : "")
				+ (rootBone.HasValue ? ", skeleton root '" + rootBone.Value.name + "'" : ", NO (Clone) skeleton root") + ")"
				+ (duplicates.Count > 0 ? ", duplicate node names: " + string.Join(", ", duplicates) : "")
				+ ", humanScale " + F(humanScale) + "; " + muscleSummary;
			return sb.ToString();
		}

		// ---- Unity's muscle model, probed -------------------------------------------------------

		/// <summary>Unity → glTF rotation (fork <see cref="SchemaExtensions"/>), held in a Unity Quaternion for the maths.</summary>
		private static Quaternion Gl(Quaternion u)
		{
			var g = u.ToGltfQuaternionConvert();
			return new Quaternion(g.X, g.Y, g.Z, g.W);
		}

		private static float AngleDeg(Quaternion q)
		{
			return 2f * Mathf.Atan2(new Vector3(q.x, q.y, q.z).magnitude, Mathf.Abs(q.w)) * Mathf.Rad2Deg;
		}

		/// <summary>Rotation axis of q (the w ≥ 0 representative), unit; zero vector for identity.</summary>
		private static Vector3 AxisOf(Quaternion q)
		{
			var v = new Vector3(q.x, q.y, q.z) * (q.w < 0 ? -1f : 1f);
			return v.sqrMagnitude < 1e-20f ? Vector3.zero : v.normalized;
		}

		/// <summary>Signed angle (deg) of q about unit axis a, and the angle left after removing it.</summary>
		private static float SignedAbout(Quaternion q, Vector3 a, out float offAxis)
		{
			if (q.w < 0) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
			var signed = 2f * Mathf.Atan2(Vector3.Dot(new Vector3(q.x, q.y, q.z), a), q.w) * Mathf.Rad2Deg;
			var half = signed * 0.5f * Mathf.Deg2Rad;
			var about = new Quaternion(a.x * Mathf.Sin(half), a.y * Mathf.Sin(half), a.z * Mathf.Sin(half), Mathf.Cos(half));
			offAxis = AngleDeg(q * Quaternion.Inverse(about));
			return signed;
		}

		/// <summary>
		/// Unity's muscle → pose map for this Avatar, measured with <see cref="HumanPoseHandler"/> (docs
		/// HumanoidRetarget_WIP/DESIGN.md "Descriptor: muscles"). Every value is glTF space, rotations local to the
		/// node's parent:
		/// <list type="bullet">
		/// <item><c>zero</c>: the bone's local rotation in the all-muscles-0 pose (bodyRotation identity; for the hips
		/// it is the rotation that bodyRotation then turns).</item>
		/// <item><c>muscles</c> / <c>axes</c> / <c>range</c> per DoF x (twist), y, z: Unity's muscle index, the
		/// rotation axis of muscle +1 in the bone's zero-pose frame (L = zero · R(axis, angle)), and the angle (deg) at
		/// muscle −1 / +1, signed about that axis. Angles are linear in the muscle value (checked at ±0.5).</item>
		/// <item><c>twist</c>: the parent's twist muscle (DoF x) also turns this bone about the PARENT's twist axis,
		/// in the parent's frame (L = R(parent axes[0], angle) · own), by <c>range</c> at −1 / +1 (Unity's twist
		/// distribution: armTwist / foreArmTwist / upperLegTwist / legTwist).</item>
		/// </list>
		/// How the DoF of one bone combine (Unity's swing-twist) is the web code's job; it is checked against
		/// <paramref name="check"/>: four random muscle poses (fixed seed, so the same muscles and bodyRotation on every
		/// rig) with the local rotation of every mapped bone and the hips rotation relative to the model root, as
		/// Unity poses them. HelperTools export-humanoid-descriptors.mjs replays them through @metacoach/shared.
		/// Throws when a muscle is not one fixed axis per DoF, not linear, or turns a bone other than its own / its
		/// twist child (the hips excepted: they follow bodyRotation).
		/// </summary>
		private static string ProbeMuscles(RigEntry rig, Animator animator, out string summary, out string check)
		{
			var inv = CultureInfo.InvariantCulture;
			string F(float v) => v.ToString("R", inv);
			string V(Vector3 v) => "[" + F(v.x) + ", " + F(v.y) + ", " + F(v.z) + "]";
			string Qs(Quaternion q) => "[" + F(q.x) + ", " + F(q.y) + ", " + F(q.z) + ", " + F(q.w) + "]";

			var bones = new List<(HumanBodyBones bone, Transform t)>();
			for (var b = 0; b < (int)HumanBodyBones.LastBone; b++)
			{
				var t = animator.GetBoneTransform((HumanBodyBones)b);
				if (t) bones.Add(((HumanBodyBones)b, t));
			}
			var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
			var root = animator.transform;
			var count = HumanTrait.MuscleCount;

			var handler = new HumanPoseHandler(animator.avatar, root);
			try
			{
				var pose = new HumanPose();
				handler.GetHumanPose(ref pose);
				var bodyPos = pose.bodyPosition;
				void Set(float[] m, Quaternion bodyRot)
				{
					pose.bodyPosition = bodyPos;
					pose.bodyRotation = bodyRot;
					pose.muscles = m;
					handler.SetHumanPose(ref pose);
				}
				float[] One(int m, float v)
				{
					var a = new float[count];
					a[m] = v;
					return a;
				}

				Set(new float[count], Quaternion.identity);
				var zero = bones.ToDictionary(b => b.bone, b => Gl(b.t.localRotation));

				var axes = new Dictionary<HumanBodyBones, Vector3?[]>();
				var ranges = new Dictionary<HumanBodyBones, Vector2?[]>();
				var twist = new Dictionary<HumanBodyBones, (HumanBodyBones from, Vector2 range)>();
				float maxOff = 0, maxLinear = 0, maxStray = 0;
				string worstStray = null;
				foreach (var (bone, _) in bones)
				{
					if (bone == HumanBodyBones.Hips) continue;
					axes[bone] = new Vector3?[3];
					ranges[bone] = new Vector2?[3];
				}

				foreach (var (bone, _) in bones)
				{
					if (bone == HumanBodyBones.Hips) continue;
					for (var dof = 0; dof < 3; dof++)
					{
						var m = HumanTrait.MuscleFromBone((int)bone, dof);
						if (m < 0) continue;
						var at = new Dictionary<float, Dictionary<HumanBodyBones, Quaternion>>();
						foreach (var v in new[] { 1f, -1f, 0.5f, -0.5f })
						{
							Set(One(m, v), Quaternion.identity);
							at[v] = bones.ToDictionary(b => b.bone, b => Gl(b.t.localRotation));
						}
						var zi = Quaternion.Inverse(zero[bone]);
						var axis = AxisOf(zi * at[1f][bone]);
						if (axis == Vector3.zero) throw new Exception("muscle " + HumanTrait.MuscleName[m] + " does not move " + bone);
						var hi = SignedAbout(zi * at[1f][bone], axis, out var off1);
						var lo = SignedAbout(zi * at[-1f][bone], axis, out var off2);
						var h5 = SignedAbout(zi * at[0.5f][bone], axis, out var off3);
						var l5 = SignedAbout(zi * at[-0.5f][bone], axis, out var off4);
						maxOff = Mathf.Max(maxOff, Mathf.Max(off1, off2, off3, off4));
						maxLinear = Mathf.Max(maxLinear, Mathf.Abs(h5 - hi * 0.5f), Mathf.Abs(l5 - lo * 0.5f));
						axes[bone][dof] = axis;
						ranges[bone][dof] = new Vector2(lo, hi);

						// Any other bone this muscle turns: the human child via the twist distribution (DoF x), nothing else.
						foreach (var (other, _) in bones)
						{
							if (other == bone || other == HumanBodyBones.Hips) continue; // hips follow bodyRotation (the body frame)
							var moved = AngleDeg(Quaternion.Inverse(zero[other]) * at[1f][other]);
							if (moved < 1e-3f) continue;
							if (dof == 0 && HumanTrait.GetParentBone((int)other) == (int)bone)
							{
								// In the child's parent frame (= this bone's own frame), about this bone's twist axis.
								var zo = Quaternion.Inverse(zero[other]);
								var hiC = SignedAbout(at[1f][other] * zo, axis, out var c1);
								var loC = SignedAbout(at[-1f][other] * zo, axis, out var c2);
								var h5C = SignedAbout(at[0.5f][other] * zo, axis, out var c3);
								var l5C = SignedAbout(at[-0.5f][other] * zo, axis, out var c4);
								maxOff = Mathf.Max(maxOff, Mathf.Max(c1, c2, c3, c4));
								maxLinear = Mathf.Max(maxLinear, Mathf.Abs(h5C - hiC * 0.5f), Mathf.Abs(l5C - loC * 0.5f));
								twist[other] = (bone, new Vector2(loC, hiC));
							}
							else if (moved > maxStray)
							{
								maxStray = moved;
								worstStray = HumanTrait.MuscleName[m] + " -> " + other;
							}
						}
					}
				}
				if (maxOff > 0.01f) throw new Exception("a muscle turns its bone off one fixed axis by " + F(maxOff) + " deg");
				if (maxLinear > 0.01f) throw new Exception("muscle angles are not linear (+-0.5 vs +-1 off by " + F(maxLinear) + " deg)");
				if (maxStray > 1e-3f) throw new Exception("muscle " + worstStray + " turns a bone that is neither its own nor its twist child (" + F(maxStray) + " deg)");

				var com = ProbeCenterOfMass(animator, root, hips, Set, bodyPos, count, out var comResidualMm);

				var sb = new StringBuilder();
				sb.Append("{\n        \"version\": 1,\n        \"bones\": {");
				var firstBone = true;
				foreach (var (bone, _) in bones)
				{
					sb.Append(firstBone ? "\n" : ",\n");
					firstBone = false;
					sb.Append("          \"").Append(bone).Append("\": { \"zero\": ").Append(Qs(zero[bone]));
					if (bone != HumanBodyBones.Hips)
					{
						var mus = Enumerable.Range(0, 3).Select(d => HumanTrait.MuscleFromBone((int)bone, d)).ToArray();
						sb.Append(", \"muscles\": [").Append(string.Join(", ", mus.Select(x => x < 0 ? "null" : x.ToString(inv)))).Append(']');
						sb.Append(", \"axes\": [").Append(string.Join(", ", axes[bone].Select(a => a.HasValue ? V(a.Value) : "null"))).Append(']');
						sb.Append(", \"range\": [").Append(string.Join(", ", ranges[bone].Select(r => r.HasValue ? "[" + F(r.Value.x) + ", " + F(r.Value.y) + "]" : "null"))).Append(']');
						if (twist.TryGetValue(bone, out var tw))
							sb.Append(", \"twist\": { \"from\": \"").Append(tw.from).Append("\", \"range\": [").Append(F(tw.range.x)).Append(", ").Append(F(tw.range.y)).Append("] }");
					}
					sb.Append(" }");
				}
				sb.Append("\n        },\n        \"com\": {");
				var firstCom = true;
				foreach (var kv in com)
				{
					sb.Append(firstCom ? " " : ", ").Append('"').Append(kv.Key).Append("\": ").Append(V(kv.Value));
					firstCom = false;
				}
				sb.Append(" }\n      }");

				// Check poses: random muscles in [-0.8, 0.8] + a random bodyRotation, the same sequence on every rig.
				var rng = new System.Random(20261028);
				var cb = new StringBuilder();
				cb.Append("    { \"rigKey\": \"").Append(rig.rigKey).Append("\", \"poses\": [");
				for (var r = 0; r < 4; r++)
				{
					var ms = new float[count];
					for (var m = 0; m < count; m++) ms[m] = (float)(rng.NextDouble() * 1.6 - 0.8);
					var br = Quaternion.Euler((float)(rng.NextDouble() * 40 - 20), (float)(rng.NextDouble() * 360 - 180), (float)(rng.NextDouble() * 40 - 20));
					Set(ms, br);
					cb.Append(r == 0 ? "\n" : ",\n");
					cb.Append("      { \"muscles\": [").Append(string.Join(", ", ms.Select(F))).Append("], \"bodyRotation\": ").Append(Qs(Gl(br)));
					cb.Append(", \"hips\": ").Append(Qs(Gl(Quaternion.Inverse(root.rotation) * hips.rotation)));
					cb.Append(", \"local\": {").Append(string.Join(", ", bones.Select(b => "\"" + b.bone + "\": " + Qs(Gl(b.t.localRotation))))).Append("} }");
				}
				cb.Append("\n    ] }");
				check = cb.ToString();

				summary = "muscles: " + axes.Values.Sum(a => a.Count(x => x.HasValue)) + " DoF on " + axes.Count + " bones, " + twist.Count
					+ " twist shares, off-axis max " + F(maxOff) + " deg, linearity max " + F(maxLinear) + " deg"
					+ ", centre of mass on " + com.Count + " bones (fit residual max " + F(comResidualMm) + " mm)";
				return sb.ToString();
			}
			finally
			{
				handler.Dispose();
			}
		}

		/// <summary>The body bones Unity's centre of mass is made of (measured: adding toes, eyes, jaw or fingers changes nothing).</summary>
		private static readonly HumanBodyBones[] ComBones =
		{
			HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest, HumanBodyBones.Neck, HumanBodyBones.Head,
			HumanBodyBones.LeftShoulder, HumanBodyBones.RightShoulder, HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm,
			HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm, HumanBodyBones.LeftHand, HumanBodyBones.RightHand,
			HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg,
			HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
		};

		/// <summary>
		/// Unity's centre of mass of this Avatar as data (descriptor <c>muscles.com</c>): per body bone b a constant vector
		/// u_b (glTF space, metres, in the bone's own frame) with
		/// <c>COM = hips position + Σ_b R_b · u_b</c> (positions and rotations relative to the model root). Unity places the
		/// body so that COM = bodyPosition · humanScale; the model is Σ mass_b · (bone point + R_b · centre_b) rewritten
		/// along the hierarchy, so it is exact in this form. Measured: 64 random poses (muscles in [−1, 1], random
		/// bodyRotation) set with <see cref="HumanPoseHandler.SetHumanPose"/>, u solved by least squares against the
		/// bodyPosition Unity was given. Throws when the fit leaves more than 0.05 mm.
		/// </summary>
		private static Dictionary<HumanBodyBones, Vector3> ProbeCenterOfMass(Animator animator, Transform root, Transform hips, Action<float[], Quaternion> set, Vector3 bodyPosition, int muscleCount, out float residualMm)
		{
			var bones = ComBones.Select(b => (bone: b, t: animator.GetBoneTransform(b))).Where(x => x.t).ToList();
			var n = bones.Count * 3;
			var ata = new double[n, n];
			var atb = new double[n];
			var rows = new List<(double[] row, double rhs)>();
			var rng = new System.Random(20261029);
			var scale = animator.humanScale;
			for (var s = 0; s < 64; s++)
			{
				var ms = new float[muscleCount];
				for (var m = 0; m < muscleCount; m++) ms[m] = (float)(rng.NextDouble() * 2 - 1);
				var br = Quaternion.Euler((float)(rng.NextDouble() * 60 - 30), (float)(rng.NextDouble() * 360 - 180), (float)(rng.NextDouble() * 60 - 30));
				set(ms, br);
				// The body position SetHumanPose was given (set() keeps it fixed), × humanScale, root frame → glTF.
				var target = Gl3(bodyPosition * scale);
				var hipsP = Gl3(root.InverseTransformPoint(hips.position));
				var rot = bones.Select(b => Matrix4x4.Rotate(Gl(Quaternion.Inverse(root.rotation) * b.t.rotation))).ToList();
				for (var axis = 0; axis < 3; axis++)
				{
					var row = new double[n];
					for (var j = 0; j < bones.Count; j++)
						for (var c = 0; c < 3; c++) row[j * 3 + c] = rot[j][axis, c];
					var rhs = (double)target[axis] - hipsP[axis];
					rows.Add((row, rhs));
					for (var i = 0; i < n; i++)
					{
						if (row[i] == 0) continue;
						for (var k = 0; k < n; k++) ata[i, k] += row[i] * row[k];
						atb[i] += row[i] * rhs;
					}
				}
			}
			var u = SolveSymmetric(ata, atb);
			residualMm = 0;
			for (var e = 0; e < rows.Count; e += 3)
			{
				double d2 = 0;
				for (var k = 0; k < 3; k++)
				{
					double pred = 0;
					for (var i = 0; i < n; i++) pred += rows[e + k].row[i] * u[i];
					d2 += (pred - rows[e + k].rhs) * (pred - rows[e + k].rhs);
				}
				residualMm = Mathf.Max(residualMm, (float)(Math.Sqrt(d2) * 1000));
			}
			if (residualMm > 0.05f) throw new Exception("centre-of-mass model does not fit Unity's poses (residual " + residualMm.ToString("R", CultureInfo.InvariantCulture) + " mm)");
			var outMap = new Dictionary<HumanBodyBones, Vector3>();
			for (var j = 0; j < bones.Count; j++) outMap[bones[j].bone] = new Vector3((float)u[j * 3], (float)u[j * 3 + 1], (float)u[j * 3 + 2]);
			return outMap;
		}

		private static Vector3 Gl3(Vector3 u) => new Vector3(-u.x, u.y, u.z);

		/// <summary>Solves A·x = b (A symmetric positive definite, the normal equations) by Gaussian elimination with partial pivoting.</summary>
		private static double[] SolveSymmetric(double[,] a, double[] b)
		{
			var n = b.Length;
			var m = new double[n, n + 1];
			for (var i = 0; i < n; i++)
			{
				for (var j = 0; j < n; j++) m[i, j] = a[i, j];
				m[i, n] = b[i];
			}
			for (var c = 0; c < n; c++)
			{
				var p = c;
				for (var r = c + 1; r < n; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[p, c])) p = r;
				if (Math.Abs(m[p, c]) < 1e-12) throw new Exception("centre-of-mass fit is singular");
				if (p != c) for (var k = 0; k <= n; k++) { var t = m[c, k]; m[c, k] = m[p, k]; m[p, k] = t; }
				for (var r = 0; r < n; r++)
				{
					if (r == c) continue;
					var f = m[r, c] / m[c, c];
					if (f == 0) continue;
					for (var k = c; k <= n; k++) m[r, k] -= f * m[c, k];
				}
			}
			var x = new double[n];
			for (var i = 0; i < n; i++) x[i] = m[i, n] / m[i, i];
			return x;
		}

		private static void Collect(Transform t, List<Transform> into)
		{
			into.Add(t);
			for (var i = 0; i < t.childCount; i++) Collect(t.GetChild(i), into);
		}
	}
}
