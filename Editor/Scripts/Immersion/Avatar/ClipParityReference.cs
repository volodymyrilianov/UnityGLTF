using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Immersion.Export
{
	/// <summary>
	/// Unity half of the clip-export PARITY GATE: what Unity shows, bone by bone, while a logic-scene
	/// character plays one clip through the Animator state that plays it in the module. HelperTools
	/// <c>anim-library/clip-gate.mjs</c> compares this trace with the exported GLB played the way the
	/// web runtime plays it and fails the export above the gate thresholds.
	///
	/// Per case (spec JSON, see <see cref="Spec"/>):
	/// <list type="bullet">
	/// <item>Edit mode opens the case's logic scene (nothing is saved) and records the character at
	///   <c>animatorPath</c>: its nearest prefab asset, the local TRS of every transform under that
	///   prefab instance (scene overrides included), the Animator's avatar / controller /
	///   <c>applyRootMotion</c> / update mode, and its <c>CharacterIK</c> goals (targets, weights,
	///   <c>ikActive</c>) when it has one.</item>
	/// <item>Play mode (empty scene, <c>Time.captureFramerate</c> = <c>fps</c>) rebuilds the character
	///   from that asset at the scene pose (Animator added if the scene added it), with the scene's
	///   avatar, controller and <c>applyRootMotion</c>, culling AlwaysAnimate, and the CharacterIK
	///   mirror <see cref="IkHandFrameDriver"/> with the scene goals. The case's clip is put into the
	///   case's state through an <c>AnimatorOverrideController</c> when the state holds a placeholder
	///   (the project's AnimationController does the same), that state is started with
	///   <c>Animator.Play(state, layer, 0)</c>, the state's layer runs at weight 1 and every OTHER
	///   non-base layer at weight 0 (other tags are not this clip's business; the base layer keeps its
	///   default state, which only reaches bones outside the layer's mask).</item>
	/// <item>After every LateUpdate (Animator evaluation, IK pass and root motion done) it writes the
	///   state's clip time and every transform under the Animator GameObject relative to that
	///   GameObject's CURRENT world pose (root motion factored out, the GameObject's scale removed:
	///   model units), in glTF convention (position x → −x, rotation (x, −y, −z, w)). Recording runs
	///   from the first frame to one clip length of clip time (a looping clip: one full cycle).</item>
	/// </list>
	/// The meta file names the bones the case's layer drives (humanoid bones of the mask's active body
	/// parts; <c>all</c> for an unmasked layer) and the IK-driven chains (upper arm down, per goal with a
	/// non-zero weight while <c>ikActive</c>), which the gate excludes.
	///
	/// Writes <c>-out/&lt;id&gt;.json</c> (meta) + <c>-out/&lt;id&gt;.bin</c> (float32 LE frames:
	/// clip time, normalized time, then 7 floats per bone px py pz qx qy qz qw) per case and
	/// <c>-out/reference.json</c> (case list + status), then exits.
	///
	/// Unity.exe -batchmode -nographics -projectPath &lt;proj&gt; -executeMethod Immersion.Export.ClipParityReference.Run
	///   -spec "C:/dir/gate-spec.json" -out "C:/dir/reference" -logFile "C:/dir/unity-gate.log"
	/// (no -quit — the probe exits by itself; exit 0 = every case written, 1 = a case failed)
	/// </summary>
	public static class ClipParityReference
	{
		private const string Flag = "Immersion.ClipParityReference.pending";
		private const string Tag = "[ClipParityReference]";

		[Serializable]
		public class Spec
		{
			public int fps = 60;
			public List<Case> cases = new List<Case>();
		}

		[Serializable]
		public class Case
		{
			/// <summary>File-safe id (output file names).</summary>
			public string id;
			/// <summary>Logic scene path.</summary>
			public string scene;
			/// <summary>Hierarchy path of the character's Animator GameObject in that scene.</summary>
			public string animatorPath;
			/// <summary>Clip source: <c>a.anim</c> or <c>model.fbx::Take</c>.</summary>
			public string clip;
			/// <summary>Controller to run; empty = the scene Animator's own.</summary>
			public string controller;
			public string layer;
			public string state;
			/// <summary>False = run with the CharacterIK goals zeroed (diagnostics only; the gate runs with IK).</summary>
			public bool ik = true;
			/// <summary>
			/// Animator parameters that hold the case's state the way the module holds it while the clip plays
			/// ("talk=1;level=4"; Bool 0/1/true/false, Int, Float; a Trigger is set when truthy). Without them a state
			/// whose transitions leave on a parameter condition (talk IfNot, level NotEqual) ends at its first frame.
			/// </summary>
			public string parameters;
			/// <summary>
			/// An ADDITIVE layer is evaluated as an OVERRIDE layer (an in-memory clone of the controller; the asset
			/// is not touched): the trace is then the clip itself on the layer's masked bones, so the gate checks the
			/// bake. The additive composition (clip relative to its frame 0 over the layers below) is the runtime's
			/// job and is not compared. Ignored on override layers.
			/// </summary>
			public bool additiveAsOverride;
		}

		[Serializable]
		public class Captured
		{
			public Case c;
			public string error;
			public string instanceAssetPath;
			public string animatorPathInInstance;
			public bool animatorOnAsset;
			public IkHandFrameProbe.Trs instanceWorld;
			public List<string> localPaths = new List<string>();
			public List<IkHandFrameProbe.Trs> locals = new List<IkHandFrameProbe.Trs>();
			public string avatarPath;
			public string avatarName;
			public string controllerPath;
			public bool applyRootMotion;
			public int updateMode;
			public bool hasCharacterIk;
			public bool ikActive;
			public List<IkHandFrameProbe.Goal> goals = new List<IkHandFrameProbe.Goal>();
			public string components;
		}

		[Serializable]
		public class Pending
		{
			public string outDir;
			public int fps;
			public List<Captured> captured = new List<Captured>();
		}

		public static void Run()
		{
			var specPath = CliArgs.Get("-spec");
			var outDir = CliArgs.Get("-out");
			if (string.IsNullOrEmpty(specPath) || !File.Exists(specPath) || string.IsNullOrEmpty(outDir))
			{
				Debug.LogError(Tag + " usage: -spec <gate-spec.json> -out <dir>");
				CliArgs.Exit(1);
				return;
			}
			Pending pending;
			try
			{
				var spec = JsonUtility.FromJson<Spec>(File.ReadAllText(specPath));
				if (spec.cases.Count == 0) throw new Exception("spec has no cases");
				var ids = new HashSet<string>();
				foreach (var c in spec.cases)
					if (string.IsNullOrEmpty(c.id) || !ids.Add(c.id)) throw new Exception("case id missing or duplicate: '" + c.id + "'");
				pending = new Pending { outDir = outDir, fps = spec.fps > 0 ? spec.fps : 60 };
				foreach (var group in spec.cases.GroupBy(c => c.scene))
				{
					// IkHandFrameProbe.CaptureScene opens the scene (Single) and reads every CharacterIK in it.
					var ikCapture = IkHandFrameProbe.CaptureScene(group.Key);
					var scene = EditorSceneManager.GetActiveScene();
					var animators = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Animator>(true)).ToList();
					foreach (var c in group) pending.captured.Add(Capture(c, animators, ikCapture));
				}
			}
			catch (Exception e)
			{
				Debug.LogError(Tag + " FAIL capture: " + e);
				CliArgs.Exit(1);
				return;
			}
			Directory.CreateDirectory(outDir);
			var json = JsonUtility.ToJson(pending, true);
			File.WriteAllText(Path.Combine(outDir, "capture.json"), json);
			SessionState.SetString(Flag, json);
			EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); // logic scenes closed unsaved
			EditorApplication.EnterPlaymode();
		}

		private static Captured Capture(Case c, List<Animator> animators, IkHandFrameProbe.Capture ikCapture)
		{
			var cap = new Captured { c = c };
			var animator = animators.FirstOrDefault(a => IkHandFrameProbe.HierarchyPath(a.transform) == c.animatorPath);
			if (!animator) { cap.error = "no Animator at '" + c.animatorPath + "' in " + c.scene; Debug.LogError(Tag + " " + c.id + ": " + cap.error); return cap; }
			var go = animator.gameObject;
			var instanceRoot = PrefabUtility.GetNearestPrefabInstanceRoot(go);
			var assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
			if (!instanceRoot || string.IsNullOrEmpty(assetPath))
			{
				// Unpacked avatar (no prefab link left in the scene): rebuild from the model its Avatar comes from, rooted
				// at the Animator GameObject; the scene's own locals below still override the model's.
				var modelPath = animator.avatar ? AssetDatabase.GetAssetPath(animator.avatar) : "";
				if (string.IsNullOrEmpty(modelPath) || !AssetDatabase.LoadAssetAtPath<GameObject>(modelPath))
				{
					cap.error = "the Animator GameObject is not part of a prefab instance and its Avatar has no source model";
					Debug.LogError(Tag + " " + c.id + ": " + cap.error);
					return cap;
				}
				instanceRoot = go;
				assetPath = modelPath;
				Debug.Log(Tag + " " + c.id + ": unpacked in the scene, rebuilding from the Avatar's model " + modelPath);
			}
			cap.instanceAssetPath = assetPath;
			cap.animatorPathInInstance = go == instanceRoot ? "" : AnimationUtility.CalculateTransformPath(go.transform, instanceRoot.transform);
			var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
			var assetHost = !asset ? null : string.IsNullOrEmpty(cap.animatorPathInInstance) ? asset.transform : asset.transform.Find(cap.animatorPathInInstance);
			cap.animatorOnAsset = assetHost && assetHost.GetComponent<Animator>();
			cap.instanceWorld = IkHandFrameProbe.WorldTrs(instanceRoot.transform);
			foreach (var t in instanceRoot.GetComponentsInChildren<Transform>(true))
			{
				if (t == instanceRoot.transform) continue;
				cap.localPaths.Add(AnimationUtility.CalculateTransformPath(t, instanceRoot.transform));
				cap.locals.Add(IkHandFrameProbe.LocalTrs(t));
			}
			cap.avatarPath = animator.avatar ? AssetDatabase.GetAssetPath(animator.avatar) : "";
			cap.avatarName = animator.avatar ? animator.avatar.name : "";
			cap.controllerPath = !string.IsNullOrEmpty(c.controller) ? c.controller
				: animator.runtimeAnimatorController ? AssetDatabase.GetAssetPath(animator.runtimeAnimatorController) : "";
			cap.applyRootMotion = animator.applyRootMotion;
			cap.updateMode = (int)animator.updateMode;
			cap.components = string.Join(";", go.GetComponents<Component>().Select(x => x ? x.GetType().Name : "<missing>"));
			var ikRig = ikCapture.rigs.FirstOrDefault(r => r.hierarchyPath == c.animatorPath);
			if (ikRig != null)
			{
				cap.hasCharacterIk = true;
				cap.ikActive = ikRig.ikActive;
				cap.goals = ikRig.goals;
			}
			Debug.Log($"{Tag} captured {c.id}: {c.animatorPath} asset={assetPath} animatorPath='{cap.animatorPathInInstance}' animatorOnAsset={cap.animatorOnAsset} ctrl={cap.controllerPath} avatar={cap.avatarName} applyRootMotion={cap.applyRootMotion} characterIK={cap.hasCharacterIk}/{cap.ikActive} goals={cap.goals.Count}");
			return cap;
		}

		[InitializeOnLoadMethod]
		private static void Hook()
		{
			if (!Application.isBatchMode) return;
			EditorApplication.playModeStateChanged += state =>
			{
				if (state != PlayModeStateChange.EnteredPlayMode) return;
				var raw = SessionState.GetString(Flag, null);
				if (string.IsNullOrEmpty(raw)) return;
				SessionState.EraseString(Flag);
				new GameObject("ClipParityReferenceHost").AddComponent<ClipParityReferenceRunner>().Begin(JsonUtility.FromJson<Pending>(raw));
			};
		}

		/// <summary>Humanoid bone → the AvatarMask body part that gates it.</summary>
		internal static AvatarMaskBodyPart? BodyPart(HumanBodyBones b)
		{
			switch (b)
			{
				// The hips carry the humanoid body transform (position + orientation), which the mask's ROOT part
				// gates, not BODY: a layer with Body on and Root off (204 UpperBodyMask) takes its spine/chest
				// muscles from the clip but leaves the hips to the layers below.
				case HumanBodyBones.Hips:
					return AvatarMaskBodyPart.Root;
				case HumanBodyBones.Spine: case HumanBodyBones.Chest: case HumanBodyBones.UpperChest:
					return AvatarMaskBodyPart.Body;
				case HumanBodyBones.Neck: case HumanBodyBones.Head: case HumanBodyBones.LeftEye: case HumanBodyBones.RightEye: case HumanBodyBones.Jaw:
					return AvatarMaskBodyPart.Head;
				case HumanBodyBones.LeftUpperLeg: case HumanBodyBones.LeftLowerLeg: case HumanBodyBones.LeftFoot: case HumanBodyBones.LeftToes:
					return AvatarMaskBodyPart.LeftLeg;
				case HumanBodyBones.RightUpperLeg: case HumanBodyBones.RightLowerLeg: case HumanBodyBones.RightFoot: case HumanBodyBones.RightToes:
					return AvatarMaskBodyPart.RightLeg;
				case HumanBodyBones.LeftShoulder: case HumanBodyBones.LeftUpperArm: case HumanBodyBones.LeftLowerArm: case HumanBodyBones.LeftHand:
					return AvatarMaskBodyPart.LeftArm;
				case HumanBodyBones.RightShoulder: case HumanBodyBones.RightUpperArm: case HumanBodyBones.RightLowerArm: case HumanBodyBones.RightHand:
					return AvatarMaskBodyPart.RightArm;
				case HumanBodyBones.LastBone:
					return null;
				default:
					var n = b.ToString();
					if (n.StartsWith("Left", StringComparison.Ordinal)) return AvatarMaskBodyPart.LeftFingers;
					if (n.StartsWith("Right", StringComparison.Ordinal)) return AvatarMaskBodyPart.RightFingers;
					return null;
			}
		}
	}

	// Shares the file with the static entry class on purpose (see IkHandFrameProbe.cs: the file's MonoScript
	// binds to the static class, so AddComponent accepts this Editor-folder type).

	/// <summary>Play-mode half of <see cref="ClipParityReference"/>: rebuild, play, trace, dump, exit.</summary>
	public class ClipParityReferenceRunner : MonoBehaviour
	{
		private const string Tag = "[ClipParityReference]";
		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

		private class Live
		{
			public ClipParityReference.Captured cap;
			public Animator animator;
			public int layer;
			public AnimationClip clip;
			public AnimatorState state;
			public float recordClipSeconds;
			public float clipSecondsSoFar;
			public float lastClipTime = -1f;
			public bool finished;
			public string error;
			public string endedBy = "clip end";
			public readonly List<Transform> bones = new List<Transform>();
			public BinaryWriter bin;
			public int frames;
			public readonly StringBuilder meta = new StringBuilder();
			public string evaluatedAs;
			public string parametersApplied = "";
		}

		/// <summary>Sets "name=value;…" on the Animator by the controller's parameter types; returns what was set.</summary>
		private static string ApplyParameters(Animator animator, AnimatorController controller, string spec)
		{
			if (string.IsNullOrWhiteSpace(spec)) return "";
			var applied = new List<string>();
			foreach (var part in spec.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
			{
				var eq = part.IndexOf('=');
				if (eq <= 0) throw new Exception("parameter '" + part + "' is not name=value");
				var name = part.Substring(0, eq).Trim();
				var value = part.Substring(eq + 1).Trim();
				var p = controller.parameters.FirstOrDefault(x => x.name == name);
				if (p == null) throw new Exception("parameter '" + name + "' not in " + controller.name);
				var truthy = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
				switch (p.type)
				{
					case AnimatorControllerParameterType.Bool: animator.SetBool(name, truthy); break;
					case AnimatorControllerParameterType.Int: animator.SetInteger(name, int.Parse(value, Inv)); break;
					case AnimatorControllerParameterType.Float: animator.SetFloat(name, float.Parse(value, Inv)); break;
					case AnimatorControllerParameterType.Trigger: if (truthy) animator.SetTrigger(name); break;
				}
				applied.Add(name + "=" + value);
			}
			return string.Join(";", applied);
		}

		private ClipParityReference.Pending pending;
		private readonly List<Live> lives = new List<Live>();
		private int frame;
		private bool done;

		public void Begin(ClipParityReference.Pending p)
		{
			pending = p;
			Time.captureFramerate = p.fps; // fixed dt
			foreach (var cap in p.captured)
			{
				var live = new Live { cap = cap };
				lives.Add(live);
				if (!string.IsNullOrEmpty(cap.error)) { live.error = cap.error; live.finished = true; continue; }
				try { Build(live); }
				catch (Exception e)
				{
					live.error = e.GetType().Name + ": " + e.Message;
					live.finished = true;
					Debug.LogError(Tag + " FAIL build " + cap.c.id + ": " + e);
				}
			}
			Debug.Log(Tag + " play: " + lives.Count(l => !l.finished) + "/" + lives.Count + " cases built at " + p.fps + " fps");
		}

		private void Build(Live live)
		{
			var cap = live.cap;
			var c = cap.c;
			var model = AssetDatabase.LoadAssetAtPath<GameObject>(cap.instanceAssetPath);
			if (!model) throw new Exception("asset not loadable: " + cap.instanceAssetPath);
			var inst = Instantiate(model);
			inst.name = c.id + " (" + model.name + ")";
			if (!inst.activeSelf) inst.SetActive(true);
			inst.transform.SetPositionAndRotation(cap.instanceWorld.pos, cap.instanceWorld.rot);
			inst.transform.localScale = cap.instanceWorld.scale;
			for (var i = 0; i < cap.localPaths.Count; i++)
			{
				var t = inst.transform.Find(cap.localPaths[i]);
				if (!t) continue;
				var l = cap.locals[i];
				t.localPosition = l.pos; t.localRotation = l.rot; t.localScale = l.scale;
			}
			var host = string.IsNullOrEmpty(cap.animatorPathInInstance) ? inst.transform : inst.transform.Find(cap.animatorPathInInstance);
			if (!host) throw new Exception("Animator GameObject '" + cap.animatorPathInInstance + "' not in " + cap.instanceAssetPath);
			var animator = host.GetComponent<Animator>();
			if (!animator) animator = host.gameObject.AddComponent<Animator>(); // scene-added Animator
			var avatar = string.IsNullOrEmpty(cap.avatarPath) ? null : AssetDatabase.LoadAllAssetsAtPath(cap.avatarPath).OfType<Avatar>().FirstOrDefault(a => a.name == cap.avatarName);
			if (!avatar) throw new Exception("avatar not found: " + cap.avatarPath + "#" + cap.avatarName);
			animator.avatar = avatar;
			animator.applyRootMotion = cap.applyRootMotion;
			animator.updateMode = (AnimatorUpdateMode)cap.updateMode;
			animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; // no camera in batch mode

			var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(cap.controllerPath);
			if (!controller) throw new Exception("controller not found: " + cap.controllerPath);
			live.layer = Array.FindIndex(controller.layers, l => l.name == c.layer);
			if (live.layer < 0) throw new Exception("layer '" + c.layer + "' not in " + cap.controllerPath);
			var layerDef = controller.layers[live.layer];
			live.state = AllStates(layerDef.stateMachine).FirstOrDefault(s => s.name == c.state);
			if (!live.state) throw new Exception("state '" + c.state + "' not in layer '" + c.layer + "'");
			live.clip = AnimatorSettingsProbeRunner.LoadClip(c.clip);
			if (!live.clip) throw new Exception("clip not found: " + c.clip);
			var stateClip = live.state.motion as AnimationClip;
			if (!stateClip) throw new Exception("state '" + c.state + "' holds no clip (blend trees are not supported)");
			// An additive layer evaluated as override (Case.additiveAsOverride): a non-persistent clone of the
			// controller with that one layer switched; its state machines are the asset's own (shared, unchanged).
			var runController = controller;
			var evaluatedAs = layerDef.blendingMode.ToString();
			if (c.additiveAsOverride && layerDef.blendingMode == AnimatorLayerBlendingMode.Additive)
			{
				runController = UnityEngine.Object.Instantiate(controller);
				runController.hideFlags = HideFlags.DontSave;
				runController.name = controller.name + " (gate override " + c.id + ")";
				var layers = runController.layers;
				layers[live.layer].blendingMode = AnimatorLayerBlendingMode.Override;
				runController.layers = layers;
				evaluatedAs = AnimatorLayerBlendingMode.Override.ToString();
			}
			live.evaluatedAs = evaluatedAs;
			RuntimeAnimatorController run = runController;
			var overridden = stateClip != live.clip;
			if (overridden)
			{
				var aoc = new AnimatorOverrideController(runController) { name = controller.name + " (gate " + c.id + ")" };
				aoc[stateClip] = live.clip;
				run = aoc;
			}
			animator.runtimeAnimatorController = run;
			animator.Rebind(); // defaults = the scene locals applied above
			live.parametersApplied = ApplyParameters(animator, runController, c.parameters);
			for (var l = 1; l < controller.layers.Length; l++) animator.SetLayerWeight(l, l == live.layer ? 1f : 0f);
			animator.Play(live.state.nameHash, live.layer, 0f);

			IkHandFrameDriver driver = null;
			var ikExcluded = new List<string>();
			if (cap.hasCharacterIk)
			{
				driver = host.gameObject.AddComponent<IkHandFrameDriver>();
				driver.Setup(animator);
				driver.ikActive = c.ik && cap.ikActive;
				foreach (var g in cap.goals)
				{
					var tgo = new GameObject("IK target " + g.goal + " (" + g.targetName + ") " + c.id);
					Transform tp;
					if (g.parentInAvatar) tp = string.IsNullOrEmpty(g.parentPathInAvatar) ? host : host.Find(g.parentPathInAvatar);
					else
					{
						tp = new GameObject("proxy target parent: " + g.parentKey + " " + c.id).transform;
						tp.SetPositionAndRotation(g.parentWorld.pos, g.parentWorld.rot);
						tp.localScale = g.parentWorld.scale;
					}
					if (!tp) throw new Exception("IK target parent not found for " + g.goal);
					tgo.transform.SetParent(tp, false);
					tgo.transform.localPosition = g.local.pos;
					tgo.transform.localRotation = g.local.rot;
					tgo.transform.localScale = g.local.scale;
					var goal = (AvatarIKGoal)Enum.Parse(typeof(AvatarIKGoal), g.goal);
					driver.goals.Add(new IkHandFrameDriver.Goal { ikGoal = goal, target = tgo.transform, positionWeight = g.positionWeight, rotationWeight = g.rotationWeight });
					if (driver.ikActive && (g.positionWeight > 0f || g.rotationWeight > 0f))
					{
						var upper = goal == AvatarIKGoal.LeftHand ? HumanBodyBones.LeftUpperArm
							: goal == AvatarIKGoal.RightHand ? HumanBodyBones.RightUpperArm
							: goal == AvatarIKGoal.LeftFoot ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg;
						var bt = animator.GetBoneTransform(upper);
						if (bt) ikExcluded.Add(bt.name);
					}
				}
			}

			foreach (var t in host.GetComponentsInChildren<Transform>(true))
				if (t != host) live.bones.Add(t);
			live.animator = animator;
			var speed = live.state.speed * (live.state.speedParameterActive ? animator.GetFloat(live.state.speedParameter) : 1f);
			live.recordClipSeconds = live.clip.length;

			// humanoid bones this layer writes: the mask's active body parts (no mask / base layer = every bone)
			var mask = live.layer == 0 ? null : layerDef.avatarMask;
			var driven = new List<string>();
			if (mask && animator.isHuman)
			{
				foreach (HumanBodyBones hb in Enum.GetValues(typeof(HumanBodyBones)))
				{
					var part = ClipParityReference.BodyPart(hb);
					if (!part.HasValue || !mask.GetHumanoidBodyPartActive(part.Value)) continue;
					var bt = animator.GetBoneTransform(hb);
					if (bt) driven.Add(bt.name);
				}
			}
			var human = new List<string>();
			if (animator.isHuman)
				foreach (HumanBodyBones hb in Enum.GetValues(typeof(HumanBodyBones)))
				{
					if (hb == HumanBodyBones.LastBone) continue;
					var bt = animator.GetBoneTransform(hb);
					if (bt) human.Add("\"" + hb + "\":\"" + Esc(bt.name) + "\"");
				}
			var s = live.state;
			var m = live.meta;
			m.Append("{\n  \"id\": \"").Append(Esc(c.id)).Append("\",\n");
			m.Append("  \"case\": ").Append(JsonUtility.ToJson(c)).Append(",\n");
			m.Append("  \"unity\": \"").Append(Application.unityVersion).Append("\",\n  \"fps\": ").Append(pending.fps).Append(",\n");
			m.Append("  \"asset\": \"").Append(Esc(cap.instanceAssetPath)).Append("\", \"animatorPathInInstance\": \"").Append(Esc(cap.animatorPathInInstance)).Append("\", \"animatorOnAsset\": ").Append(B(cap.animatorOnAsset)).Append(",\n");
			m.Append("  \"animatorGameObject\": \"").Append(Esc(host.name)).Append("\", \"sceneComponents\": \"").Append(Esc(cap.components)).Append("\",\n");
			m.Append("  \"avatar\": \"").Append(Esc(cap.avatarName)).Append("\", \"controller\": \"").Append(Esc(cap.controllerPath)).Append("\", \"overridden\": ").Append(B(overridden)).Append(",\n");
			m.Append("  \"applyRootMotion\": ").Append(B(animator.applyRootMotion)).Append(", \"isHuman\": ").Append(B(animator.isHuman)).Append(", \"humanScale\": ").Append(F(animator.isHuman ? animator.humanScale : 0f)).Append(",\n");
			m.Append("  \"characterIk\": ").Append(B(cap.hasCharacterIk)).Append(", \"ikActive\": ").Append(B(driver != null && driver.ikActive)).Append(",\n");
			m.Append("  \"clip\": { \"name\": \"").Append(Esc(live.clip.name)).Append("\", \"length\": ").Append(F(live.clip.length)).Append(", \"loop\": ").Append(B(live.clip.isLooping)).Append(", \"humanMotion\": ").Append(B(live.clip.isHumanMotion)).Append(" },\n");
			m.Append("  \"state\": { \"layer\": \"").Append(Esc(c.layer)).Append("\", \"layerIndex\": ").Append(live.layer).Append(", \"name\": \"").Append(Esc(s.name)).Append("\", \"motion\": \"").Append(Esc(stateClip.name))
				.Append("\", \"iKOnFeet\": ").Append(B(s.iKOnFeet)).Append(", \"writeDefaults\": ").Append(B(s.writeDefaultValues)).Append(", \"speed\": ").Append(F(s.speed))
				.Append(", \"speedParameter\": \"").Append(s.speedParameterActive ? Esc(s.speedParameter) : "").Append("\", \"effectiveSpeed\": ").Append(F(speed))
				.Append(", \"mirror\": ").Append(B(s.mirror)).Append(", \"mirrorParameter\": \"").Append(s.mirrorParameterActive ? Esc(s.mirrorParameter) : "")
				.Append("\", \"cycleOffset\": ").Append(F(s.cycleOffset)).Append(", \"cycleOffsetParameter\": \"").Append(s.cycleOffsetParameterActive ? Esc(s.cycleOffsetParameter) : "")
				.Append("\", \"timeParameter\": \"").Append(s.timeParameterActive ? Esc(s.timeParameter) : "")
				.Append("\", \"layerIkPass\": ").Append(B(layerDef.iKPass)).Append(", \"layerBlending\": \"").Append(layerDef.blendingMode).Append("\", \"evaluatedAs\": \"").Append(live.evaluatedAs)
				.Append("\", \"parameters\": \"").Append(Esc(live.parametersApplied)).Append("\", \"layerMask\": \"").Append(mask ? Esc(AssetDatabase.GetAssetPath(mask)) : "").Append("\" },\n");
			m.Append("  \"driven\": ").Append(mask ? "[" + string.Join(",", driven.Select(n => "\"" + Esc(n) + "\"")) + "]" : "\"all\"").Append(",\n");
			m.Append("  \"ikExcludedRoots\": [").Append(string.Join(",", ikExcluded.Select(n => "\"" + Esc(n) + "\""))).Append("],\n");
			// mesh-carrying nodes (Renderer GameObjects): not skeleton, never animated; the gate skips them
			m.Append("  \"rendererNodes\": [").Append(string.Join(",", live.bones.Where(b => b.GetComponent<Renderer>()).Select(b => "\"" + Esc(b.name) + "\""))).Append("],\n");
			m.Append("  \"human\": {").Append(string.Join(",", human)).Append("},\n");
			m.Append("  \"bones\": [").Append(string.Join(",", live.bones.Select(b => "\"" + Esc(b.name) + "\""))).Append("],\n");
			m.Append("  \"parents\": [").Append(string.Join(",", live.bones.Select(b => b.parent == host ? -1 : live.bones.IndexOf(b.parent)))).Append("],\n");
			m.Append("  \"paths\": [").Append(string.Join(",", live.bones.Select(b => "\"" + Esc(AnimationUtility.CalculateTransformPath(b, host)) + "\""))).Append("],\n");
			m.Append("  \"frameLayout\": \"float32 LE per frame: clipTime, normalizedTime, then per bone px py pz qx qy qz qw (glTF convention, Animator GameObject frame, model units)\",\n");

			Directory.CreateDirectory(pending.outDir);
			live.bin = new BinaryWriter(File.Open(Path.Combine(pending.outDir, c.id + ".bin"), FileMode.Create));
			Debug.Log($"{Tag} built {c.id}: {host.name} state {c.layer}/{c.state} (motion '{stateClip.name}' → '{live.clip.name}', overridden {overridden}) footIK {s.iKOnFeet} speed {speed} blending {layerDef.blendingMode}→{live.evaluatedAs} params [{live.parametersApplied}] mask {(mask ? mask.name : "none")} driven {(mask ? driven.Count.ToString() : "all")} ik {(driver != null && driver.ikActive)} excluded [{string.Join(",", ikExcluded)}] applyRootMotion {animator.applyRootMotion}");
		}

		private static IEnumerable<AnimatorState> AllStates(AnimatorStateMachine machine)
		{
			if (!machine) yield break;
			foreach (var child in machine.states) yield return child.state;
			foreach (var sub in machine.stateMachines)
				foreach (var s in AllStates(sub.stateMachine))
					yield return s;
		}

		private void LateUpdate()
		{
			if (done) return;
			try
			{
				frame++;
				foreach (var r in lives)
				{
					if (r.finished) continue;
					var info = r.animator.GetCurrentAnimatorStateInfo(r.layer);
					if (info.shortNameHash != r.state.nameHash)
					{
						if (r.frames == 0 && frame < 5) continue; // Play() lands on the next Animator update
						End(r, "layer " + r.layer + " is not in state " + r.state.name + " at frame " + frame);
						continue;
					}
					if (r.animator.IsInTransition(r.layer))
					{
						// an exit-time transition of the controller blends the next state in: the frames from here
						// on are not this clip alone, so the trace ends (not an error; the meta records it)
						r.endedBy = "transition at clip time " + F(r.lastClipTime);
						End(r, null);
						continue;
					}
					var nt = info.normalizedTime;
					var len = r.clip.length;
					var ct = r.clip.isLooping ? (nt - Mathf.Floor(nt)) * len : Mathf.Min(nt, 1f) * len;
					if (r.lastClipTime >= 0f)
					{
						var step = ct - r.lastClipTime;
						if (step < 0f) step += len; // loop wrap
						r.clipSecondsSoFar += step;
					}
					r.lastClipTime = ct;
					var root = r.animator.transform;
					var inv = root.worldToLocalMatrix;
					var invRot = Quaternion.Inverse(root.rotation);
					var w = r.bin;
					w.Write(ct);
					w.Write(nt);
					foreach (var b in r.bones)
					{
						var p = inv.MultiplyPoint3x4(b.position);
						var q = invRot * b.rotation;
						w.Write(-p.x); w.Write(p.y); w.Write(p.z);
						w.Write(q.x); w.Write(-q.y); w.Write(-q.z); w.Write(q.w);
					}
					r.frames++;
					var ended = r.clip.isLooping ? r.clipSecondsSoFar >= r.recordClipSeconds : nt >= 1f;
					if (ended) End(r, null);
				}
				if (lives.All(l => l.finished)) Finish();
				else if (frame > pending.fps * 600) throw new Exception("a case did not end within 600 s");
			}
			catch (Exception e)
			{
				Debug.LogError(Tag + " FAIL trace: " + e);
				foreach (var l in lives.Where(l => !l.finished)) End(l, e.Message);
				Finish();
			}
		}

		private void End(Live r, string error)
		{
			r.finished = true;
			if (error != null) r.error = error;
			if (r.bin != null) { r.bin.Flush(); r.bin.Dispose(); r.bin = null; }
			if (r.meta.Length == 0) return;
			r.meta.Append("  \"frames\": ").Append(r.frames).Append(",\n  \"endedBy\": \"").Append(Esc(r.endedBy)).Append("\",\n  \"error\": ").Append(r.error == null ? "null" : "\"" + Esc(r.error) + "\"").Append("\n}\n");
			File.WriteAllText(Path.Combine(pending.outDir, r.cap.c.id + ".json"), r.meta.ToString(), new UTF8Encoding(false));
		}

		private void Finish()
		{
			done = true;
			var sb = new StringBuilder("{\n  \"unity\": \"" + Application.unityVersion + "\",\n  \"fps\": " + pending.fps + ",\n  \"cases\": [\n");
			for (var i = 0; i < lives.Count; i++)
			{
				var l = lives[i];
				sb.Append("    { \"id\": \"").Append(Esc(l.cap.c.id)).Append("\", \"frames\": ").Append(l.frames).Append(", \"error\": ")
					.Append(l.error == null ? "null" : "\"" + Esc(l.error) + "\"").Append(" }").Append(i + 1 < lives.Count ? ",\n" : "\n");
			}
			sb.Append("  ]\n}\n");
			File.WriteAllText(Path.Combine(pending.outDir, "reference.json"), sb.ToString(), new UTF8Encoding(false));
			var failed = lives.Count(l => l.error != null);
			Debug.Log(Tag + " wrote " + lives.Count + " case(s) to " + pending.outDir + ", " + failed + " failed");
			EditorApplication.Exit(failed > 0 ? 1 : 0);
		}

		private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
		private static string B(bool v) => v ? "true" : "false";
		private static string F(float v) => v.ToString("R", Inv);
	}
}
