using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Immersion.Export
{
	/// <summary>
	/// Which reflection probe Unity itself binds to each renderer of a scene: <c>Renderer.GetClosestReflectionProbes</c>
	/// (the blend list Unity builds from the renderer bounds re-centred on its <c>probeAnchor</c>, highest weight first;
	/// the shaders that read only <c>unity_SpecCube0</c> get the first), plus every probe's box, importance, intensity,
	/// HDR decode values (<c>textureHDRDecodeValues</c> = <c>unity_SpecCube0_HDR</c>) and cubemap. Settles overlapping equal probes that YAML cannot (e.g. an eye anchor inside three 10 m boxes).
	/// <c>-additive</c> loads more scenes next to <c>-scene</c> (the play-mode set, e.g. Main + the enviro), whose probes
	/// compete for the same renderers.
	///
	/// <code>
	/// Unity.exe -batchmode -quit -projectPath &lt;proj&gt; \
	///   -executeMethod Immersion.Export.RendererProbeDump.Run \
	///   -scene "Assets/.../Logic.unity" -out "C:/dump/renderer-probes.json" [-additive "Assets/.../Main.unity;..."]
	///   [-filter "Organica;Body"]
	/// </code>
	///
	/// Renderers dumped: every renderer with a <c>probeAnchor</c> or below an <see cref="Animator"/>, optionally only
	/// those whose name contains one of the <c>-filter</c> tokens.
	/// </summary>
	public static class RendererProbeDump
	{
		public static void Run()
		{
			var scenePath = CliArgs.Get("-scene");
			var outPath = CliArgs.Get("-out");
			if (string.IsNullOrEmpty(scenePath) || string.IsNullOrEmpty(outPath))
			{
				Debug.LogError("[RendererProbeDump] usage: -scene \"Assets/.../Logic.unity\" -out <file.json> [-additive \"A.unity;B.unity\"] [-filter \"Organica;Body\"]");
				CliArgs.Exit(1);
				return;
			}

			var code = 0;
			try
			{
				var filter = CliArgs.Split(CliArgs.Get("-filter")).Where(s => s.Length > 0).ToList();
				var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
				if (!scene.IsValid()) throw new Exception("scene not found/openable at '" + scenePath + "'");
				foreach (var extra in CliArgs.Split(CliArgs.Get("-additive")).Where(x => x.Length > 0))
				{
					var added = EditorSceneManager.OpenScene(extra, OpenSceneMode.Additive);
					if (!added.IsValid()) throw new Exception("additive scene not found/openable at '" + extra + "'");
				}
				var roots = Enumerable.Range(0, EditorSceneManager.sceneCount)
					.SelectMany(i => EditorSceneManager.GetSceneAt(i).GetRootGameObjects()).ToArray();

				var probes = new JArray();
				foreach (var rp in roots.SelectMany(r => r.GetComponentsInChildren<ReflectionProbe>(true)))
				{
					var cube = (rp.mode == ReflectionProbeMode.Custom ? rp.customBakedTexture : rp.bakedTexture) ?? rp.texture;
					probes.Add(new JObject
					{
						["scene"] = rp.gameObject.scene.name,
						["path"] = PathOf(rp.transform),
						["active"] = rp.isActiveAndEnabled,
						["mode"] = rp.mode.ToString(),
						["position"] = V(rp.transform.position),
						["boundsMin"] = V(rp.bounds.min),
						["boundsMax"] = V(rp.bounds.max),
						["importance"] = rp.importance,
						["intensity"] = rp.intensity,
						// what unity_SpecCube0_HDR carries for this probe (DecodeHDREnvironment: x = the multiplier the shader applies)
						["hdrDecode"] = new JArray(Math.Round(rp.textureHDRDecodeValues.x, 5), Math.Round(rp.textureHDRDecodeValues.y, 5),
							Math.Round(rp.textureHDRDecodeValues.z, 5), Math.Round(rp.textureHDRDecodeValues.w, 5)),
						["blendDistance"] = rp.blendDistance,
						["boxProjection"] = rp.boxProjection,
						["cubemap"] = cube != null ? AssetDatabase.GetAssetPath(cube) : null,
					});
				}

				var renderers = new JArray();
				foreach (var renderer in roots.SelectMany(r => r.GetComponentsInChildren<Renderer>(true)))
				{
					if (renderer.probeAnchor == null && renderer.GetComponentInParent<Animator>(true) == null) continue;
					if (filter.Count > 0 && !filter.Any(f => renderer.name.Contains(f))) continue;
					var blend = new List<ReflectionProbeBlendInfo>();
					renderer.GetClosestReflectionProbes(blend);
					renderers.Add(new JObject
					{
						["path"] = PathOf(renderer.transform),
						["active"] = renderer.enabled && renderer.gameObject.activeInHierarchy,
						["usage"] = renderer.reflectionProbeUsage.ToString(),
						["anchor"] = renderer.probeAnchor != null ? PathOf(renderer.probeAnchor) : null,
						["anchorPosition"] = renderer.probeAnchor != null ? V(renderer.probeAnchor.position) : null,
						["boundsCenter"] = V(renderer.bounds.center),
						["materials"] = new JArray(renderer.sharedMaterials.Select(m => m != null ? m.name + " : " + m.shader.name : null)),
						["probes"] = new JArray(blend.Where(b => b.probe != null).Select(b => new JObject
						{
							["probe"] = PathOf(b.probe.transform),
							["weight"] = Math.Round(b.weight, 4),
							["intensity"] = b.probe.intensity,
						})),
					});
				}

				Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
				File.WriteAllText(outPath, new JObject { ["scene"] = scenePath, ["probes"] = probes, ["renderers"] = renderers }.ToString());
				Debug.Log("[RendererProbeDump] " + renderers.Count + " renderers, " + probes.Count + " probes -> " + outPath);
			}
			catch (Exception e)
			{
				code = 1;
				Debug.LogError("[RendererProbeDump] FAIL: " + e);
			}

			CliArgs.Exit(code);
		}

		private static JArray V(Vector3 v) => new JArray(Math.Round(v.x, 4), Math.Round(v.y, 4), Math.Round(v.z, 4));

		private static string PathOf(Transform t)
		{
			var parts = new List<string>();
			for (var c = t; c != null; c = c.parent) parts.Add(c.name);
			parts.Reverse();
			return string.Join("/", parts);
		}
	}
}
