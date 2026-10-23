using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;

namespace Immersion.Export
{
	/// <summary>
	/// Play-mode recording of what a scene's play-on-awake <see cref="PlayableDirector"/>s really do to
	/// their bound objects — the input of the web "scene timeline" bake (HelperTools
	/// <c>glb-timeline/glb-bake-timeline.mjs</c> turns it into glTF animations on the scene GLB).
	///
	/// Why play mode and not <c>director.Evaluate()</c> in edit mode: what the trainee sees is the
	/// runtime composition of the timeline (clip extrapolation, the scene default values of properties
	/// a clip does not animate, the director's wrap mode) AND of the bound Animator's own controller,
	/// which keeps running underneath and takes over once a wrap-None director has finished. Play mode
	/// records that composition as it is, no re-implementation of either.
	///
	/// The scene is opened alone and played with <c>Time.captureFramerate = -hz</c> (fixed game-time
	/// step, frames run as fast as the machine allows; -nographics is fine). Every frame, after
	/// the animation update (LateUpdate), the local TRS and activeSelf of EVERY transform under each
	/// recorded director's bound Animator GameObjects (inactive ones included) is sampled. The time
	/// axis is director time while the director plays and continues in game time after it stops, so
	/// t = 0 is the director's first evaluated frame = the moment the scene was loaded.
	///
	/// Output <c>-out/&lt;scene&gt;.director-bake.json</c>: meta (unity version, hz, per director: path,
	/// asset, duration, wrapMode, initialState, updateMode, bound objects, stop time), the time axis,
	/// per node its hierarchy path (relative to the bound object's parent, i.e. starting with the
	/// bound object's name — the glTF node names), and per channel either a constant or one value per
	/// frame. Unity space (left-handed); the bake converts.
	///
	/// Unity.exe -batchmode -nographics -projectPath &lt;proj&gt; -executeMethod Immersion.Export.DirectorBakeProbe.Run
	///   -scene "Assets/…/S01_mostek.unity" -out "C:/dir" [-hz 60] [-tail 45] [-seconds N] [-directors "SM_EoY_SpaceShip"]
	/// (no -quit — the probe exits by itself; exit 0 = written, 1 = failed). Default length = the
	/// longest director duration + <c>-tail</c> seconds (to observe what runs after a wrap-None end).
	/// </summary>
	public static class DirectorBakeProbe
	{
		private const string Flag = "Immersion.DirectorBakeProbe.pending";

		[Serializable]
		public class Pending
		{
			public string scene;
			public string outDir;
			public int hz = 60;
			public float tail = 45f;
			public float seconds;
			public string directors = "";
		}

		public static void Run()
		{
			var p = new Pending
			{
				scene = CliArgs.Get("-scene"),
				outDir = CliArgs.Get("-out"),
				hz = Math.Max(1, CliArgs.GetInt("-hz", 60)),
				directors = CliArgs.Get("-directors") ?? "",
			};
			if (float.TryParse(CliArgs.Get("-tail"), NumberStyles.Float, CultureInfo.InvariantCulture, out var tail)) p.tail = tail;
			if (float.TryParse(CliArgs.Get("-seconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) p.seconds = seconds;
			if (string.IsNullOrEmpty(p.scene) || string.IsNullOrEmpty(p.outDir))
			{
				Debug.LogError("[DirectorBakeProbe] usage: -scene <Assets/…/X.unity> -out <dir> [-hz 60] [-tail 45] [-seconds N] [-directors \"a;b\"]");
				CliArgs.Exit(1);
				return;
			}
			try
			{
				var scene = EditorSceneManager.OpenScene(p.scene, OpenSceneMode.Single);
				if (!scene.IsValid()) throw new Exception("scene not openable: " + p.scene);
			}
			catch (Exception e)
			{
				Debug.LogError("[DirectorBakeProbe] FAIL open: " + e);
				CliArgs.Exit(1);
				return;
			}
			SessionState.SetString(Flag, JsonUtility.ToJson(p));
			EditorApplication.EnterPlaymode();
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
				new GameObject("DirectorBakeProbeHost").AddComponent<DirectorBakeProbeRunner>().Begin(JsonUtility.FromJson<Pending>(raw));
			};
		}

		internal static string PathOf(Transform t, Transform stopAbove)
		{
			var parts = new List<string>();
			for (var c = t; c && c != stopAbove; c = c.parent) parts.Add(c.name);
			parts.Reverse();
			return string.Join("/", parts);
		}
	}

	/// <summary>Play-mode half of <see cref="DirectorBakeProbe"/>.</summary>
	public class DirectorBakeProbeRunner : MonoBehaviour
	{
		private const int Channels = 11; // px py pz qx qy qz qw sx sy sz active
		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

		private class Rec
		{
			public PlayableDirector director;
			public string path;
			public readonly List<GameObject> bound = new List<GameObject>();
			public double stopT = -1;
			public bool wasPlaying;
			public double offset; // game time - director time at the last playing frame
		}

		private DirectorBakeProbe.Pending pending;
		private readonly List<Rec> recs = new List<Rec>();
		private readonly List<Transform> nodes = new List<Transform>();
		private readonly List<string> nodePaths = new List<string>();
		private readonly List<float[]> samples = new List<float[]>(); // per frame: nodes*Channels
		private readonly List<double> times = new List<double>();
		private readonly List<string> controllerState = new List<string>();
		private double length;
		private double gameT;
		private bool done;
		private Animator primaryAnimator;

		public void Begin(DirectorBakeProbe.Pending p)
		{
			pending = p;
			Time.captureFramerate = p.hz;
			var filter = CliArgs.Split(p.directors).Where(s => s.Length > 0).ToList();
			var all = new List<PlayableDirector>();
			for (var i = 0; i < SceneManager.sceneCount; i++)
				foreach (var root in SceneManager.GetSceneAt(i).GetRootGameObjects())
					all.AddRange(root.GetComponentsInChildren<PlayableDirector>(true));
			foreach (var d in all)
			{
				var path = DirectorBakeProbe.PathOf(d.transform, null);
				if (filter.Count > 0 && !filter.Contains(path) && !filter.Contains(d.name)) continue;
				if (!d.playableAsset || !d.playOnAwake || !d.isActiveAndEnabled)
				{
					Debug.Log($"[DirectorBakeProbe] skip {path}: asset {(d.playableAsset ? d.playableAsset.name : "none")}, playOnAwake {d.playOnAwake}, active {d.isActiveAndEnabled}");
					continue;
				}
				var rec = new Rec { director = d, path = path };
				foreach (var output in d.playableAsset.outputs)
				{
					if (!output.sourceObject) continue;
					var binding = d.GetGenericBinding(output.sourceObject);
					var go = binding is Component c ? c.gameObject : binding as GameObject;
					if (go && !rec.bound.Contains(go)) rec.bound.Add(go);
					if (binding is Animator a && !primaryAnimator) primaryAnimator = a;
				}
				recs.Add(rec);
			}
			if (recs.Count == 0)
			{
				Debug.LogError("[DirectorBakeProbe] FAIL: no play-on-awake PlayableDirector with a timeline in " + p.scene);
				EditorApplication.Exit(1);
				return;
			}
			foreach (var rec in recs)
				foreach (var go in rec.bound)
					foreach (var t in go.GetComponentsInChildren<Transform>(true))
						if (!nodes.Contains(t))
						{
							nodes.Add(t);
							nodePaths.Add(DirectorBakeProbe.PathOf(t, go.transform.parent));
						}
			length = p.seconds > 0 ? p.seconds : recs.Max(r => r.director.duration) + p.tail;
			Debug.Log($"[DirectorBakeProbe] play: {recs.Count} director(s) [{string.Join(", ", recs.Select(r => r.path + " -> " + r.director.playableAsset.name + " " + r.director.duration.ToString("0.###", Inv) + " s wrap " + r.director.extrapolationMode + " bound " + string.Join("+", r.bound.Select(b => b.name))))}], {nodes.Count} nodes, {length:0.###} s at {p.hz} Hz");
		}

		private void LateUpdate()
		{
			if (done || pending == null) return;
			// t = director time while it plays, then game time continued from its last playing frame
			var lead = recs[0];
			var d = lead.director;
			var playing = d.state == PlayState.Playing;
			if (playing)
			{
				lead.offset = Time.timeAsDouble - d.time;
				gameT = d.time;
				lead.wasPlaying = true;
			}
			else
			{
				if (lead.wasPlaying && lead.stopT < 0) lead.stopT = Time.timeAsDouble - lead.offset;
				gameT = Time.timeAsDouble - lead.offset;
			}
			foreach (var r in recs.Skip(1))
				if (r.director.state != PlayState.Playing && r.wasPlaying && r.stopT < 0) r.stopT = gameT;
				else if (r.director.state == PlayState.Playing) r.wasPlaying = true;

			var frame = new float[nodes.Count * Channels];
			for (var i = 0; i < nodes.Count; i++)
			{
				var t = nodes[i];
				var o = i * Channels;
				var lp = t.localPosition; var lq = t.localRotation; var ls = t.localScale;
				frame[o] = lp.x; frame[o + 1] = lp.y; frame[o + 2] = lp.z;
				frame[o + 3] = lq.x; frame[o + 4] = lq.y; frame[o + 5] = lq.z; frame[o + 6] = lq.w;
				frame[o + 7] = ls.x; frame[o + 8] = ls.y; frame[o + 9] = ls.z;
				frame[o + 10] = t.gameObject.activeSelf ? 1f : 0f;
			}
			samples.Add(frame);
			times.Add(gameT);
			controllerState.Add(ControllerState(playing, d.time));
			if (samples.Count % (pending.hz * 60) == 0) Debug.Log($"[DirectorBakeProbe] t {gameT:0.###} s, director {d.state} {d.time:0.###}");
			if (gameT >= length) Finish();
		}

		private string ControllerState(bool playing, double dirT)
		{
			if (!primaryAnimator || !primaryAnimator.runtimeAnimatorController) return (playing ? "P" : "S") + dirT.ToString("0.###", Inv);
			var st = primaryAnimator.GetCurrentAnimatorStateInfo(0);
			var clips = primaryAnimator.GetCurrentAnimatorClipInfo(0);
			return (playing ? "P" : "S") + dirT.ToString("0.###", Inv) + "|" + (clips.Length > 0 ? clips[0].clip.name : "-") + "@" + st.normalizedTime.ToString("0.####", Inv);
		}

		private void Finish()
		{
			done = true;
			try
			{
				Directory.CreateDirectory(pending.outDir);
				var name = Path.GetFileNameWithoutExtension(pending.scene);
				var file = Path.Combine(pending.outDir, name + ".director-bake.json");
				File.WriteAllText(file, Json());
				Debug.Log($"[DirectorBakeProbe] OK wrote {file}: {samples.Count} frames, {nodes.Count} nodes, director stop t = {string.Join(", ", recs.Select(r => r.stopT.ToString("0.###", Inv)))}");
				EditorApplication.Exit(0);
			}
			catch (Exception e)
			{
				Debug.LogError("[DirectorBakeProbe] FAIL write: " + e);
				EditorApplication.Exit(1);
			}
		}

		private static string S(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
		private static string F(double v) => Math.Round(v, 6).ToString("0.######", Inv);

		private string Json()
		{
			var sb = new StringBuilder(1 << 24);
			sb.Append("{\n\"meta\":{\"unity\":").Append(S(Application.unityVersion))
				.Append(",\"scene\":").Append(S(pending.scene))
				.Append(",\"hz\":").Append(pending.hz)
				.Append(",\"frames\":").Append(samples.Count)
				.Append(",\"convention\":\"Unity local space (left-handed): p = localPosition, q = localRotation (x,y,z,w), s = localScale, a = activeSelf; t = director time while playing, then game time continued\"")
				.Append(",\"directors\":[");
			for (var i = 0; i < recs.Count; i++)
			{
				var r = recs[i];
				var d = r.director;
				sb.Append(i > 0 ? "," : "").Append("{\"path\":").Append(S(r.path))
					.Append(",\"asset\":").Append(S(d.playableAsset.name))
					.Append(",\"duration\":").Append(F(d.duration))
					.Append(",\"wrapMode\":").Append(S(d.extrapolationMode.ToString()))
					.Append(",\"playOnAwake\":").Append(d.playOnAwake ? "true" : "false")
					.Append(",\"updateMode\":").Append(S(d.timeUpdateMode.ToString()))
					.Append(",\"initialTime\":").Append(F(d.initialTime))
					.Append(",\"stopT\":").Append(F(r.stopT))
					.Append(",\"bound\":[").Append(string.Join(",", r.bound.Select(b => S(DirectorBakeProbe.PathOf(b.transform, null))))).Append("]")
					.Append(",\"controller\":").Append(S(primaryAnimator && primaryAnimator.runtimeAnimatorController ? primaryAnimator.runtimeAnimatorController.name : ""))
					.Append("}");
			}
			sb.Append("]},\n\"t\":[");
			for (var k = 0; k < times.Count; k++) sb.Append(k > 0 ? "," : "").Append(F(times[k]));
			sb.Append("],\n\"controllerState\":[");
			// every 30th frame is enough to read the controller's phase
			var first = true;
			for (var k = 0; k < controllerState.Count; k += 30) { sb.Append(first ? "" : ",").Append(S(F(times[k]) + " " + controllerState[k])); first = false; }
			sb.Append("],\n\"nodes\":[");
			string[] names = { "px", "py", "pz", "qx", "qy", "qz", "qw", "sx", "sy", "sz", "a" };
			for (var i = 0; i < nodes.Count; i++)
			{
				sb.Append(i > 0 ? ",\n" : "\n").Append("{\"path\":").Append(S(nodePaths[i]));
				for (var c = 0; c < Channels; c++)
				{
					var o = i * Channels + c;
					var v0 = samples[0][o];
					var constant = samples.All(f => Math.Abs(f[o] - v0) < 1e-7f);
					sb.Append(",\"").Append(names[c]).Append("\":");
					if (constant) { sb.Append(F(v0)); continue; }
					sb.Append('[');
					for (var k = 0; k < samples.Count; k++) sb.Append(k > 0 ? "," : "").Append(F(samples[k][o]));
					sb.Append(']');
				}
				sb.Append('}');
			}
			sb.Append("\n]\n}\n");
			return sb.ToString();
		}
	}
}
