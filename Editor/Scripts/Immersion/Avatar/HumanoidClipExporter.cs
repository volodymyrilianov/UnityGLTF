using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityGLTF.Extensions;

namespace Immersion.Export
{
	/// <summary>
	/// Batch-mode export of RIG-FREE humanoid clips: a Unity humanoid clip as Unity's own muscle data (95 muscle
	/// curves, RootT/RootQ, IK goals, MotionT/Q, blendshape curves) sampled once, with the clip settings and the
	/// Animator-state options that change its playback. No avatar is involved: the web runtime plays the file on
	/// any avatar whose humanoid descriptor carries Unity's muscle model (docs
	/// <c>HumanoidRetarget_WIP/DESIGN.md</c>, "Rig-free humanoid clips").
	///
	/// Usage:
	/// <code>
	/// Unity.exe -batchmode -quit -nographics -projectPath &lt;proj&gt; \
	///   -executeMethod Immersion.Export.HumanoidClipExporter.ExportClips \
	///   -clipList "C:/work/clip-list.json" -out "C:/work/humanoid-clips" [-evalRig "Assets/A/Avatar.prefab"]
	/// </code>
	///
	/// - <c>-clipList</c>: JSON <c>{ "clips": [ { "name": "argument-decline-m", "source": "Assets/…/x.anim" |
	///   "Assets/…/X.fbx::Take", "states": ["&lt;controller&gt;::&lt;layer&gt;/&lt;state&gt;", …], "footIk": "" | "0" | "1" } ] }</c>.
	///   <c>states</c>: every Animator state known to play the clip (HelperTools <c>anim-library/pool-clips.json</c>
	///   <c>playingState</c> of its rows); their options are recorded, never baked. <c>footIk</c>: explicit Foot IK
	///   when no state is known.
	/// - <c>-out</c>: output directory; writes <c>&lt;name&gt;.humanoid.glb</c> per clip.
	/// - <c>-evalRig</c>: optional diagnostic. A humanoid rig asset the clip is ALSO played on, through the same
	///   AnimationMode sampling as the per-rig pool bake (ExporterAnimationHumanoid), with the first state's Foot IK;
	///   per sample the Animator root's local TRS and <see cref="HumanPoseHandler.GetHumanPose"/> (bodyPosition,
	///   bodyRotation, muscles) go to <c>&lt;name&gt;.unity-eval.json</c>. Not part of the format: it lets the parity
	///   harness separate "raw curves vs what Unity plays" from "web muscle model vs Unity's".
	///
	/// The file (format version 1): a GLB with one empty scene and no nodes, meshes or animations. Every curve is a
	/// FLOAT accessor (SCALAR per muscle / blendshape / animator curve, VEC3 positions, VEC4 rotations x,y,z,w), all
	/// sampled on the per-rig bake's grid (<c>BakeGrid</c>: length / ceil(length · fps − 0.001) steps, plus the clip
	/// end) at the clip's own frame rate, so every key coincides with a key of the pool bake. The JSON header is the
	/// top-level <c>extras.IMMERSION_humanoidClip</c>. Space: positions and rotations are converted to glTF
	/// (fork <see cref="SchemaExtensions"/>: position (−x,y,z), rotation (x,−y,−z,w)); muscle values are unitless.
	/// RootT / goal positions stay normalized (Unity divides them by the Avatar's humanScale). Nothing is quantized.
	///
	/// Curve sources: <see cref="AnimationUtility.GetCurveBindings"/> + <see cref="AnimationUtility.GetEditorCurve"/>
	/// (works for .anim assets and FBX sub-clips alike), evaluated per sample; a quaternion is read per component and
	/// normalized, as Unity does. A muscle the clip has no curve for is absent from the file (the runtime plays 0).
	///
	/// Exit code 0 = every clip written; 1 = at least one failed. One line per clip:
	/// <c>[HumanoidClipExporter] OK|FAIL &lt;name&gt;</c>.
	/// </summary>
	public static class HumanoidClipExporter
	{
		private const string Tag = "[HumanoidClipExporter]";
		private const int FormatVersion = 1;
		// The pool bake samples a LOOPING clip's last key just inside its end (ExporterAnimationHumanoid).
		private const float LoopEndSampleEpsilon = 1e-4f;

		[Serializable]
		private class ClipEntry
		{
			public string name;
			public string source;
			public string[] states;
			public string footIk;
		}

		[Serializable]
		private class ClipList
		{
			public ClipEntry[] clips;
		}

		private static readonly string[] GoalNames = { "LeftFoot", "RightFoot", "LeftHand", "RightHand" };

		public static void ExportClips()
		{
			var listPath = CliArgs.Get("-clipList");
			var outDir = CliArgs.Get("-out");
			var evalRigPath = CliArgs.Get("-evalRig");
			if (string.IsNullOrEmpty(listPath) || string.IsNullOrEmpty(outDir))
			{
				Debug.LogError(Tag + " usage: -clipList <clip-list.json> -out <dir> [-evalRig <prefab|fbx>]");
				CliArgs.Exit(1);
				return;
			}

			ClipList list;
			try
			{
				list = JsonUtility.FromJson<ClipList>(File.ReadAllText(listPath));
			}
			catch (Exception e)
			{
				Debug.LogError(Tag + " FAIL cannot read clip list '" + listPath + "': " + e.Message);
				CliArgs.Exit(1);
				return;
			}
			if (list?.clips == null || list.clips.Length == 0)
			{
				Debug.LogError(Tag + " FAIL clip list '" + listPath + "' has no clips");
				CliArgs.Exit(1);
				return;
			}

			GameObject evalRig = null;
			if (!string.IsNullOrEmpty(evalRigPath))
			{
				evalRig = AssetDatabase.LoadAssetAtPath<GameObject>(evalRigPath);
				if (!evalRig)
				{
					Debug.LogError(Tag + " FAIL eval rig not found at '" + evalRigPath + "'");
					CliArgs.Exit(1);
					return;
				}
			}

			Directory.CreateDirectory(outDir);
			EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
			var failures = 0;
			foreach (var entry in list.clips)
			{
				var name = string.IsNullOrEmpty(entry.name) ? Path.GetFileNameWithoutExtension(entry.source ?? "") : entry.name;
				try
				{
					var clip = ClipBatchExporter.LoadClip(entry.source ?? "");
					if (!clip) throw new Exception("clip not found at '" + entry.source + "'");
					if (!clip.isHumanMotion) throw new Exception("'" + clip.name + "' is not a humanoid (muscle) clip");
					var states = (entry.states ?? new string[0]).Where(s => !string.IsNullOrEmpty(s)).Distinct().Select(ResolveState).ToList();
					bool? footIk = null;
					if (entry.footIk == "0" || entry.footIk == "1") footIk = entry.footIk == "1";
					else if (!string.IsNullOrEmpty(entry.footIk)) throw new Exception("footIk must be '', '0' or '1', got '" + entry.footIk + "'");
					else if (states.Count > 0 && states.All(s => s.iKOnFeet == states[0].iKOnFeet)) footIk = states[0].iKOnFeet;

					var glbPath = Path.Combine(outDir, name + ".humanoid.glb");
					var summary = WriteClip(name, entry.source, clip, states, footIk, glbPath, out var grid);
					if (evalRig)
					{
						var evalFootIk = footIk ?? (states.Count > 0 && states[0].iKOnFeet);
						File.WriteAllText(Path.Combine(outDir, name + ".unity-eval.json"), EvalOnRig(evalRig, evalRigPath, clip, grid, evalFootIk), new UTF8Encoding(false));
						summary += ", eval on '" + evalRig.name + "'";
					}
					Debug.Log(Tag + " OK " + name + " -> " + glbPath + " (" + summary + ")");
				}
				catch (Exception e)
				{
					failures++;
					Debug.LogError(Tag + " FAIL " + name + ": " + e);
				}
			}
			Debug.Log(Tag + " done: " + (list.clips.Length - failures) + "/" + list.clips.Length + " exported to " + outDir);
			CliArgs.Exit(failures > 0 ? 1 : 0);
		}

		// ---- state options -----------------------------------------------------------------------------------

		private sealed class StateInfo
		{
			public string spec;
			public bool iKOnFeet;
			public bool writeDefaults;
			public float speed;
			public string speedParameter;
			public bool mirror;
			public string mirrorParameter;
			public float cycleOffset;
			public string cycleOffsetParameter;
			public string timeParameter;
			public bool layerIkPass;
			public string layerBlending;
			public string layerMask;
		}

		/// <summary><c>&lt;controller&gt;::&lt;layer&gt;/&lt;state&gt;</c> → the state's playback options (recorded, never refused).</summary>
		private static StateInfo ResolveState(string spec)
		{
			var sep = spec.IndexOf("::", StringComparison.Ordinal);
			var slash = sep < 0 ? -1 : spec.IndexOf('/', sep + 2);
			if (sep < 0 || slash < 0) throw new Exception("state must be '<controller>::<layer>/<state>', got '" + spec + "'");
			var controllerPath = spec.Substring(0, sep);
			var layerName = spec.Substring(sep + 2, slash - sep - 2);
			var stateName = spec.Substring(slash + 1);
			var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
			if (!controller) throw new Exception("controller not found at '" + controllerPath + "'");
			var layer = controller.layers.FirstOrDefault(l => l.name == layerName);
			if (layer == null) throw new Exception("layer '" + layerName + "' not in " + controllerPath);
			var state = ClipBatchExporter.AllStates(layer.stateMachine).FirstOrDefault(s => s.name == stateName);
			if (!state) throw new Exception("state '" + stateName + "' not in layer '" + layerName + "' of " + controllerPath);
			return new StateInfo
			{
				spec = spec,
				iKOnFeet = state.iKOnFeet,
				writeDefaults = state.writeDefaultValues,
				speed = state.speed,
				speedParameter = state.speedParameterActive ? state.speedParameter : "",
				mirror = state.mirror,
				mirrorParameter = state.mirrorParameterActive ? state.mirrorParameter : "",
				cycleOffset = state.cycleOffset,
				cycleOffsetParameter = state.cycleOffsetParameterActive ? state.cycleOffsetParameter : "",
				timeParameter = state.timeParameterActive ? state.timeParameter : "",
				layerIkPass = layer.iKPass,
				layerBlending = layer.blendingMode.ToString(),
				layerMask = layer.avatarMask ? AssetDatabase.GetAssetPath(layer.avatarMask) : "",
			};
		}

		// ---- curves ------------------------------------------------------------------------------------------

		/// <summary>The sample grid of the per-rig bake (GLTFSceneExporter.BakeGrid) plus the clip end.</summary>
		internal static float[] Grid(float length, float fps)
		{
			var n = Mathf.Max(1, Mathf.CeilToInt(length * fps - 0.001f));
			var dt = length / n;
			var times = new float[n + 1];
			for (var i = 0; i < n; i++) times[i] = i * dt;
			times[n] = length;
			return times;
		}

		/// <summary><see cref="HumanTrait.MuscleName"/> → the clip curve's property name (fingers differ: "Left Thumb 1 Stretched" → "LeftHand.Thumb.1 Stretched").</summary>
		internal static string MuscleCurveName(string muscleName)
		{
			var m = Regex.Match(muscleName, @"^(Left|Right) (Thumb|Index|Middle|Ring|Little) (.+)$");
			return m.Success ? m.Groups[1].Value + "Hand." + m.Groups[2].Value + "." + m.Groups[3].Value : muscleName;
		}

		private sealed class Channel
		{
			public string json;   // header entry without the accessor index
			public int width;     // floats per sample
			public float[] data;  // frames * width
		}

		private static string WriteClip(string name, string source, AnimationClip clip, List<StateInfo> states, bool? footIk, string glbPath, out float[] times)
		{
			var inv = CultureInfo.InvariantCulture;
			string F(float v) => v.ToString("R", inv);
			string Q(string v) => "\"" + (v ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
			string B(bool v) => v ? "true" : "false";

			var fps = clip.frameRate > 0f ? clip.frameRate : 30f;
			times = Grid(clip.length, fps);
			var frames = times.Length;
			var ts = times;

			var bindings = AnimationUtility.GetCurveBindings(clip);
			var animatorCurves = new Dictionary<string, AnimationCurve>();
			var blendShapes = new List<(string path, string shape, AnimationCurve curve)>();
			var ignored = new List<string>();
			foreach (var b in bindings)
			{
				if (b.type == typeof(Animator) && string.IsNullOrEmpty(b.path)) animatorCurves[b.propertyName] = AnimationUtility.GetEditorCurve(clip, b);
				else if (b.type == typeof(SkinnedMeshRenderer) && b.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
					blendShapes.Add((b.path, b.propertyName.Substring("blendShape.".Length), AnimationUtility.GetEditorCurve(clip, b)));
				else ignored.Add(b.path + ":" + b.type.Name + "." + b.propertyName);
			}
			foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip)) ignored.Add(b.path + ":" + b.type.Name + "." + b.propertyName + " (object reference)");

			float[] Sample(AnimationCurve c, float fallback)
			{
				var a = new float[frames];
				for (var i = 0; i < frames; i++) a[i] = c != null ? c.Evaluate(ts[i]) : fallback;
				return a;
			}
			AnimationCurve Take(string property)
			{
				if (!animatorCurves.TryGetValue(property, out var c)) return null;
				animatorCurves.Remove(property);
				return c;
			}

			var channels = new List<Channel>();
			// Muscles.
			var muscleCount = 0;
			for (var m = 0; m < HumanTrait.MuscleCount; m++)
			{
				var curve = Take(MuscleCurveName(HumanTrait.MuscleName[m]));
				if (curve == null) continue;
				muscleCount++;
				channels.Add(new Channel { json = "{ \"kind\": \"muscle\", \"index\": " + m + ", \"name\": " + Q(HumanTrait.MuscleName[m]), width = 1, data = Sample(curve, 0f) });
			}
			// Positions / rotations (Unity → glTF).
			Channel Vec3(string kind, string prefix, string extra)
			{
				var cx = Take(prefix + ".x");
				var cy = Take(prefix + ".y");
				var cz = Take(prefix + ".z");
				if (cx == null && cy == null && cz == null) return null;
				var x = Sample(cx, 0f);
				var y = Sample(cy, 0f);
				var z = Sample(cz, 0f);
				var data = new float[frames * 3];
				for (var i = 0; i < frames; i++)
				{
					var g = new Vector3(x[i], y[i], z[i]).ToGltfVector3Convert();
					data[i * 3] = g.X;
					data[i * 3 + 1] = g.Y;
					data[i * 3 + 2] = g.Z;
				}
				return new Channel { json = "{ \"kind\": " + Q(kind) + extra + ", \"curve\": " + Q(prefix), width = 3, data = data };
			}
			Channel Quat(string kind, string prefix, string extra)
			{
				var cx = Take(prefix + ".x");
				var cy = Take(prefix + ".y");
				var cz = Take(prefix + ".z");
				var cw = Take(prefix + ".w");
				if (cx == null && cy == null && cz == null && cw == null) return null;
				var x = Sample(cx, 0f);
				var y = Sample(cy, 0f);
				var z = Sample(cz, 0f);
				var w = Sample(cw, 1f);
				var data = new float[frames * 4];
				for (var i = 0; i < frames; i++)
				{
					var q = new Quaternion(x[i], y[i], z[i], w[i]);
					var len = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
					if (len > 1e-12f) q = new Quaternion(q.x / len, q.y / len, q.z / len, q.w / len);
					else q = Quaternion.identity;
					var g = q.ToGltfQuaternionConvert();
					data[i * 4] = g.X;
					data[i * 4 + 1] = g.Y;
					data[i * 4 + 2] = g.Z;
					data[i * 4 + 3] = g.W;
				}
				return new Channel { json = "{ \"kind\": " + Q(kind) + extra + ", \"curve\": " + Q(prefix), width = 4, data = data };
			}
			void Add(Channel c)
			{
				if (c != null) channels.Add(c);
			}
			Add(Vec3("rootT", "RootT", ""));
			Add(Quat("rootQ", "RootQ", ""));
			Add(Vec3("motionT", "MotionT", ""));
			Add(Quat("motionQ", "MotionQ", ""));
			foreach (var goal in GoalNames)
			{
				Add(Vec3("goalT", goal + "T", ", \"goal\": " + Q(goal)));
				Add(Quat("goalQ", goal + "Q", ", \"goal\": " + Q(goal)));
			}
			// Blendshapes: per mesh path, by morph name (not humanoid; the runtime binds them by name or drops them).
			foreach (var (path, shape, curve) in blendShapes)
				channels.Add(new Channel { json = "{ \"kind\": \"blendShape\", \"path\": " + Q(path) + ", \"shape\": " + Q(shape), width = 1, data = Sample(curve, 0f) });
			// Any other Animator float curve (controller parameters driven by the clip): kept by name.
			foreach (var kv in animatorCurves.OrderBy(k => k.Key, StringComparer.Ordinal))
				channels.Add(new Channel { json = "{ \"kind\": \"animator\", \"name\": " + Q(kv.Key), width = 1, data = Sample(kv.Value, 0f) });

			// ---- header ----
			var s = AnimationUtility.GetAnimationClipSettings(clip);
			var sourcePath = (source ?? "").Split(new[] { "::" }, StringSplitOptions.None)[0];
			var avgSpeed = clip.averageSpeed.ToGltfVector3Convert();
			var h = new StringBuilder();
			h.Append("{\"version\": ").Append(FormatVersion)
				.Append(", \"name\": ").Append(Q(name))
				.Append(", \"space\": \"gltf\"")
				.Append(", \"unity\": {\"source\": ").Append(Q(source))
				.Append(", \"guid\": ").Append(Q(AssetDatabase.AssetPathToGUID(sourcePath)))
				.Append(", \"clipName\": ").Append(Q(clip.name))
				.Append(", \"unityVersion\": ").Append(Q(Application.unityVersion))
				.Append(", \"exporter\": \"Immersion.Export.HumanoidClipExporter\"}")
				.Append(", \"fps\": ").Append(F(fps))
				.Append(", \"length\": ").Append(F(clip.length))
				.Append(", \"frames\": ").Append(frames)
				.Append(", \"muscleCount\": ").Append(muscleCount)
				.Append(", \"settings\": {")
				.Append("\"loopTime\": ").Append(B(s.loopTime))
				.Append(", \"loopBlend\": ").Append(B(s.loopBlend))
				.Append(", \"loopBlendOrientation\": ").Append(B(s.loopBlendOrientation))
				.Append(", \"loopBlendPositionY\": ").Append(B(s.loopBlendPositionY))
				.Append(", \"loopBlendPositionXZ\": ").Append(B(s.loopBlendPositionXZ))
				.Append(", \"keepOriginalOrientation\": ").Append(B(s.keepOriginalOrientation))
				.Append(", \"keepOriginalPositionY\": ").Append(B(s.keepOriginalPositionY))
				.Append(", \"keepOriginalPositionXZ\": ").Append(B(s.keepOriginalPositionXZ))
				.Append(", \"heightFromFeet\": ").Append(B(s.heightFromFeet))
				.Append(", \"mirror\": ").Append(B(s.mirror))
				.Append(", \"cycleOffset\": ").Append(F(s.cycleOffset))
				.Append(", \"level\": ").Append(F(s.level))
				.Append(", \"orientationOffsetY\": ").Append(F(s.orientationOffsetY))
				.Append(", \"startTime\": ").Append(F(s.startTime))
				.Append(", \"stopTime\": ").Append(F(s.stopTime))
				.Append(", \"hasAdditiveReferencePose\": ").Append(B(s.hasAdditiveReferencePose))
				.Append("}")
				.Append(", \"clip\": {")
				.Append("\"isLooping\": ").Append(B(clip.isLooping))
				.Append(", \"hasRootCurves\": ").Append(B(clip.hasRootCurves))
				.Append(", \"hasMotionCurves\": ").Append(B(clip.hasMotionCurves))
				.Append(", \"hasGenericRootTransform\": ").Append(B(clip.hasGenericRootTransform))
				.Append(", \"averageSpeed\": [").Append(F(avgSpeed.X)).Append(", ").Append(F(avgSpeed.Y)).Append(", ").Append(F(avgSpeed.Z)).Append("]")
				.Append(", \"averageAngularSpeed\": ").Append(F(clip.averageAngularSpeed))
				.Append(", \"averageDuration\": ").Append(F(clip.averageDuration))
				.Append(", \"apparentSpeed\": ").Append(F(clip.apparentSpeed))
				.Append("}");
			h.Append(", \"footIk\": ").Append(footIk.HasValue ? B(footIk.Value) : "null");
			h.Append(", \"states\": [");
			for (var i = 0; i < states.Count; i++)
			{
				var st = states[i];
				h.Append(i == 0 ? "" : ", ").Append("{\"spec\": ").Append(Q(st.spec))
					.Append(", \"iKOnFeet\": ").Append(B(st.iKOnFeet))
					.Append(", \"writeDefaults\": ").Append(B(st.writeDefaults))
					.Append(", \"speed\": ").Append(F(st.speed))
					.Append(", \"speedParameter\": ").Append(Q(st.speedParameter))
					.Append(", \"mirror\": ").Append(B(st.mirror))
					.Append(", \"mirrorParameter\": ").Append(Q(st.mirrorParameter))
					.Append(", \"cycleOffset\": ").Append(F(st.cycleOffset))
					.Append(", \"cycleOffsetParameter\": ").Append(Q(st.cycleOffsetParameter))
					.Append(", \"timeParameter\": ").Append(Q(st.timeParameter))
					.Append(", \"layerIkPass\": ").Append(B(st.layerIkPass))
					.Append(", \"layerBlending\": ").Append(Q(st.layerBlending))
					.Append(", \"layerMask\": ").Append(Q(st.layerMask)).Append("}");
			}
			h.Append("]");
			var events = AnimationUtility.GetAnimationEvents(clip);
			h.Append(", \"events\": [");
			for (var i = 0; i < events.Length; i++)
			{
				var e = events[i];
				h.Append(i == 0 ? "" : ", ").Append("{\"time\": ").Append(F(e.time))
					.Append(", \"functionName\": ").Append(Q(e.functionName))
					.Append(", \"stringParameter\": ").Append(Q(e.stringParameter))
					.Append(", \"floatParameter\": ").Append(F(e.floatParameter))
					.Append(", \"intParameter\": ").Append(e.intParameter.ToString(inv)).Append("}");
			}
			h.Append("]");
			h.Append(", \"ignoredCurves\": [").Append(string.Join(", ", ignored.Select(Q))).Append("]");
			// Accessor 0 = sample times; then one per channel, in order.
			h.Append(", \"time\": 0, \"channels\": [");
			for (var i = 0; i < channels.Count; i++) h.Append(i == 0 ? "" : ", ").Append(channels[i].json).Append(", \"accessor\": ").Append(i + 1).Append("}");
			h.Append("]}");

			WriteGlb(glbPath, name, h.ToString(), times, channels);
			return F(clip.length) + " s @ " + F(fps) + " fps, " + frames + " samples, " + muscleCount + " muscles, " + channels.Count + " channels"
				+ (blendShapes.Count > 0 ? ", " + blendShapes.Count + " blendshape curve(s)" : "")
				+ (ignored.Count > 0 ? ", " + ignored.Count + " ignored curve(s)" : "")
				+ ", loop " + B(s.loopTime) + "/" + B(s.loopBlend)
				+ ", footIk " + (footIk.HasValue ? B(footIk.Value) : "unknown");
		}

		/// <summary>A GLB with one empty scene, no nodes: accessor 0 = times, then one FLOAT accessor per channel, one buffer view each.</summary>
		private static void WriteGlb(string path, string name, string header, float[] times, List<Channel> channels)
		{
			var inv = CultureInfo.InvariantCulture;
			var bin = new MemoryStream();
			var views = new StringBuilder();
			var accessors = new StringBuilder();
			var viewCount = 0;
			void Put(float[] data, int width, int count)
			{
				var offset = bin.Length;
				var bytes = new byte[data.Length * 4];
				Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
				bin.Write(bytes, 0, bytes.Length);
				var index = viewCount++;
				views.Append(views.Length == 0 ? "" : ",").Append("{\"buffer\":0,\"byteOffset\":").Append(offset.ToString(inv)).Append(",\"byteLength\":").Append(bytes.Length.ToString(inv)).Append("}");
				var type = width == 1 ? "SCALAR" : width == 3 ? "VEC3" : "VEC4";
				accessors.Append(accessors.Length == 0 ? "" : ",").Append("{\"bufferView\":").Append(index.ToString(inv)).Append(",\"componentType\":5126,\"count\":").Append(count.ToString(inv)).Append(",\"type\":\"").Append(type).Append("\"");
				if (width == 1 && data.Length > 0) accessors.Append(",\"min\":[").Append(data.Min().ToString("R", inv)).Append("],\"max\":[").Append(data.Max().ToString("R", inv)).Append("]");
				accessors.Append("}");
			}
			Put(times, 1, times.Length);
			foreach (var c in channels) Put(c.data, c.width, times.Length);

			var json = "{\"asset\":{\"version\":\"2.0\",\"generator\":\"UnityGLTF fork Immersion.Export.HumanoidClipExporter\"},"
				+ "\"extras\":{\"IMMERSION_humanoidClip\":" + header + "},"
				+ "\"scene\":0,\"scenes\":[{\"name\":\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}],"
				+ "\"buffers\":[{\"byteLength\":" + bin.Length.ToString(inv) + "}],"
				+ "\"bufferViews\":[" + views + "],"
				+ "\"accessors\":[" + accessors + "]}";
			var jsonBytes = Encoding.UTF8.GetBytes(json);
			var jsonPad = (4 - jsonBytes.Length % 4) % 4;
			var binBytes = bin.ToArray();
			var binPad = (4 - binBytes.Length % 4) % 4;
			var total = 12 + 8 + jsonBytes.Length + jsonPad + 8 + binBytes.Length + binPad;
			using (var w = new BinaryWriter(File.Create(path)))
			{
				w.Write(0x46546C67u);
				w.Write(2u);
				w.Write((uint)total);
				w.Write((uint)(jsonBytes.Length + jsonPad));
				w.Write(0x4E4F534Au);
				w.Write(jsonBytes);
				for (var i = 0; i < jsonPad; i++) w.Write((byte)0x20);
				w.Write((uint)(binBytes.Length + binPad));
				w.Write(0x004E4942u);
				w.Write(binBytes);
				for (var i = 0; i < binPad; i++) w.Write((byte)0);
			}
		}

		// ---- diagnostic: what Unity plays on one rig -----------------------------------------------------------

		private static MethodInfo _addTransformTRS;

		/// <summary>
		/// Plays <paramref name="clip"/> on an instance of <paramref name="rigAsset"/> through the pool bake's sampling
		/// path (AnimationClipPlayable + AnimationMode, root reset to identity, looping clip end sampled 1e-4 s inside)
		/// on <paramref name="times"/>; per sample the Animator root's local TRS and HumanPoseHandler's pose (glTF space).
		/// </summary>
		private static string EvalOnRig(GameObject rigAsset, string rigPath, AnimationClip clip, float[] times, bool footIk)
		{
			var inv = CultureInfo.InvariantCulture;
			string F(float v) => v.ToString("R", inv);
			var instance = (GameObject)PrefabUtility.InstantiatePrefab(rigAsset);
			HumanPoseHandler handler = null;
			var graph = default(PlayableGraph);
			try
			{
				instance.transform.localPosition = Vector3.zero;
				if (PrefabUtility.IsPartOfPrefabInstance(instance))
					PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
				if (!instance.activeSelf) instance.SetActive(true);
				var animator = instance.GetComponentInChildren<Animator>(true);
				if (!animator || !animator.avatar || !animator.avatar.isHuman) throw new Exception("eval rig '" + rigPath + "' has no humanoid Animator");
				var root = animator.gameObject;
				handler = new HumanPoseHandler(animator.avatar, root.transform);

				graph = PlayableGraph.Create();
				var playable = AnimationClipPlayable.Create(graph, clip);
				playable.SetApplyFootIK(footIk);
				var output = AnimationPlayableOutput.Create(graph, "Animation", animator);
				output.SetSourcePlayable(playable);
				graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);

				var driver = ScriptableObject.CreateInstance<AnimationModeDriver>();
				AnimationMode.StartAnimationMode(driver);
				AnimationMode.BeginSampling();
				Undo.RegisterFullObjectHierarchyUndo(root, "Humanoid clip eval");
				if (_addTransformTRS == null) _addTransformTRS = typeof(AnimationMode).GetMethod("AddTransformTRS", (BindingFlags)(-1));
				_addTransformTRS?.Invoke(null, new object[] { root, "" });
				root.transform.localPosition = Vector3.zero;
				root.transform.localRotation = Quaternion.identity;

				var rp = new StringBuilder();
				var rq = new StringBuilder();
				var bp = new StringBuilder();
				var bq = new StringBuilder();
				var ms = new StringBuilder();
				var pose = new HumanPose();
				for (var i = 0; i < times.Length; i++)
				{
					var t = times[i];
					if (i == times.Length - 1 && clip.isLooping) t = Mathf.Max(t - LoopEndSampleEpsilon, i > 0 ? times[i - 1] : 0f);
					AnimationMode.SamplePlayableGraph(graph, 0, t);
					handler.GetHumanPose(ref pose);
					var sep = i == 0 ? "" : ",";
					var p = root.transform.localPosition.ToGltfVector3Convert();
					var q = root.transform.localRotation.ToGltfQuaternionConvert();
					var b = pose.bodyPosition.ToGltfVector3Convert();
					var c = pose.bodyRotation.ToGltfQuaternionConvert();
					rp.Append(sep).Append(F(p.X)).Append(",").Append(F(p.Y)).Append(",").Append(F(p.Z));
					rq.Append(sep).Append(F(q.X)).Append(",").Append(F(q.Y)).Append(",").Append(F(q.Z)).Append(",").Append(F(q.W));
					bp.Append(sep).Append(F(b.X)).Append(",").Append(F(b.Y)).Append(",").Append(F(b.Z));
					bq.Append(sep).Append(F(c.X)).Append(",").Append(F(c.Y)).Append(",").Append(F(c.Z)).Append(",").Append(F(c.W));
					ms.Append(sep).Append(string.Join(",", pose.muscles.Select(F)));
				}
				AnimationMode.EndSampling();
				AnimationMode.StopAnimationMode(driver);
				Undo.FlushUndoRecordObjects();
				Undo.PerformUndo();

				var sb = new StringBuilder();
				sb.Append("{\"rig\": \"").Append(rigPath.Replace("\\", "/")).Append("\", \"humanScale\": ").Append(F(animator.humanScale))
					.Append(", \"footIk\": ").Append(footIk ? "true" : "false")
					.Append(", \"times\": [").Append(string.Join(",", times.Select(F))).Append("]")
					.Append(", \"rootP\": [").Append(rp).Append("], \"rootQ\": [").Append(rq).Append("]")
					.Append(", \"bodyP\": [").Append(bp).Append("], \"bodyQ\": [").Append(bq).Append("]")
					.Append(", \"muscles\": [").Append(ms).Append("]}\n");
				return sb.ToString();
			}
			finally
			{
				handler?.Dispose();
				if (graph.IsValid()) graph.Destroy();
				if (instance) UnityEngine.Object.DestroyImmediate(instance);
			}
		}
	}
}
