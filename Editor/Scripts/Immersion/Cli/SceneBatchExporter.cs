using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityGLTF;
using UnityGLTF.Plugins;

namespace Immersion.Export
{
	/// <summary>
	/// Batch-mode GLB export of whole Unity scenes — the scriptable equivalent of the
	/// "UnityGLTF → Export active scene" menu item. Each scene is opened (Single mode, so its
	/// baked LightingData binds), every root GameObject is exported into one GLB named after
	/// the scene, and the IMMERSION export plugins emit their usual sidecars next to it
	/// (RGBM8 lightmap pages + offsets JSON, reflection strip).
	///
	/// Usage:
	/// <code>
	/// Unity.exe -batchmode -quit -projectPath &lt;proj&gt; \
	///   -executeMethod Immersion.Export.SceneBatchExporter.ExportScenes \
	///   -scenes "Assets/Scenes/EnvA.unity;Assets/Scenes/EnvB.unity" \
	///   -out "C:/exports"
	/// </code>
	///
	/// - <c>-scenes</c>: semicolon-separated scene asset paths (required).
	/// - <c>-out</c>: output directory for the GLBs (required; created if missing).
	/// - <c>-maxTextureSize N</c> / <c>-maxLightmapSize N</c>: optional caps of the Revolution
	///   plugin (longest side in px); default 0 = full resolution.
	/// - <c>-embedLightmaps true</c>: also embed the full RGBM lightmap pages in the GLB (art-demo
	///   parity; the sidecars are written either way).
	/// - <c>-animations false</c>: export no animation clips (default true: UnityGLTF bakes the clips
	///   of every Animator's controller). Used when a PlayableDirector's timeline is baked into the GLB
	///   afterwards from <see cref="DirectorBakeProbe"/> (HelperTools <c>glb-timeline</c>): the
	///   controller clips are not what Unity plays while the timeline runs.
	/// - <c>-activate "Root/Child;Root/Other"</c>: GameObjects (scene hierarchy paths) switched ON
	///   in memory before the export (the scene is never saved). For objects a timeline activates
	///   (an <c>m_IsActive</c> curve): an inactive GameObject is not exported at all, and the bake
	///   folds the activation into the node's scale (0 = inactive).
	///
	/// Exit code 0 = every scene exported; 1 = at least one failed (details on stdout, each
	/// scene logs a line starting with "[SceneBatchExporter]").
	/// </summary>
	public static class SceneBatchExporter
	{
		public static void ExportScenes()
		{
			var scenePaths = CliArgs.Split(CliArgs.Get("-scenes"));
			var outDir = CliArgs.Get("-out");

			if (scenePaths.Count == 0 || string.IsNullOrEmpty(outDir))
			{
				Debug.LogError("[SceneBatchExporter] usage: -scenes \"Assets/A.unity;Assets/B.unity\" -out <dir>");
				CliArgs.Exit(1);
				return;
			}

			Directory.CreateDirectory(outDir);

			var maxTextureSize = Math.Max(0, CliArgs.GetInt("-maxTextureSize", 0));
			var maxLightmapSize = Math.Max(0, CliArgs.GetInt("-maxLightmapSize", 0));
			var embedLightmaps = string.Equals(CliArgs.Get("-embedLightmaps"), "true", StringComparison.OrdinalIgnoreCase);
			var exportAnimations = !string.Equals(CliArgs.Get("-animations"), "false", StringComparison.OrdinalIgnoreCase);
			var activate = CliArgs.Split(CliArgs.Get("-activate")).Where(s => s.Length > 0).ToList();
			var previousEmbed = GltfCustomData.EmbedFullLightmapPages;
			GltfCustomData.EmbedFullLightmapPages = embedLightmaps;
			Debug.Log("[SceneBatchExporter] contract v" + GltfCustomDataExporter.CONTRACT_VERSION
				+ ", lightmap RGBM range " + GltfCustomDataExporter.LIGHTMAP_RGBM_RANGE
				+ ", probe range " + GltfCustomDataExporter.PROBE_RANGE
				+ ", maxTextureSize " + (maxTextureSize > 0 ? maxTextureSize.ToString() : "unlimited")
				+ ", maxLightmapSize " + (maxLightmapSize > 0 ? maxLightmapSize.ToString() : "unlimited")
				+ ", embedLightmaps " + embedLightmaps
				+ ", animations " + exportAnimations
				+ (activate.Count > 0 ? ", activate " + string.Join(";", activate) : ""));

			var failures = 0;
			foreach (var scenePath in scenePaths)
			{
				var name = Path.GetFileNameWithoutExtension(scenePath);
				try
				{
					var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
					if (!scene.IsValid()) throw new Exception("scene not found/openable at '" + scenePath + "'");

					var roots = scene.GetRootGameObjects();
					foreach (var path in activate)
					{
						var go = FindByPath(roots, path);
						if (!go) throw new Exception("-activate: no GameObject '" + path + "' in " + scenePath);
						go.SetActive(true);
						Debug.Log("[SceneBatchExporter] activated " + path);
					}
					var transforms = Array.ConvertAll(roots, gameObject => gameObject.transform);
					if (transforms.Length == 0) throw new Exception("scene has no root GameObjects");

					// Fresh in-memory settings with every plugin registered at its EnabledByDefault
					// state (all IMMERSION plugins are EnabledByDefault) — the persisted project
					// settings asset can lose its plugin sub-assets in batch mode. Built AFTER
					// OpenScene, per scene: a Single-mode scene load unloads unreferenced objects, which
					// destroyed settings created up front, and ExportContext then silently fell back to
					// the project's settings asset (its JPEG heuristic, cache and plugin values — none of
					// ImmersionExportSettings applied; every scene export before 2026-09-24 ran that way).
					var settings = GLTFSettings.GetDefaultSettings();
					ImmersionExportSettings.ApplyDefaults(settings, isObjectExport: false, maxTextureSize, maxLightmapSize);
					settings.ExportAnimations = exportAnimations;
					var context = new ExportContext(settings);
					if (!ReferenceEquals(context.settings, settings))
						throw new Exception("export settings were replaced by the project settings asset");
					var exporter = new GLTFSceneExporter(transforms, context);
					exporter.SaveGLB(outDir, scene.name);

					// Any loose file the export plugins wrote for this scene: one RGBM8 lightmap
					// page per lightmap (<scene>_Lightmap-<i>_RGBM8.png — these hold the lighting,
					// the GLB only carries 4x4 black placeholders), the lightmap offsets JSON and
					// the reflection strip.
					var sidecarPrefixes = new[] { "_Lightmap-", "_lightmap_offsets.json", "_reflection.png" }
						.Select(suffix => scene.name + suffix).ToArray();
					var sidecars = Directory.GetFiles(outDir)
						.Select(Path.GetFileName)
						.Where(f => sidecarPrefixes.Any(prefix => f.StartsWith(prefix, StringComparison.Ordinal)))
						.OrderBy(f => f, StringComparer.Ordinal)
						.ToArray();
					var rgbmPages = ImmersionLightmapPages.Pages.Count;
					Debug.Log("[SceneBatchExporter] OK " + name + " -> " + Path.Combine(outDir, scene.name + ".glb")
						+ " (RGBM lightmap pages: " + rgbmPages + ", range " + GltfCustomDataExporter.LIGHTMAP_RGBM_RANGE + ")"
						+ (sidecars.Length > 0 ? " (sidecars: " + string.Join(", ", sidecars) + ")" : ""));
				}
				catch (Exception e)
				{
					failures++;
					Debug.LogError("[SceneBatchExporter] FAIL " + name + ": " + e);
				}
			}

			GltfCustomData.EmbedFullLightmapPages = previousEmbed;
			Debug.Log("[SceneBatchExporter] done: " + (scenePaths.Count - failures) + "/" + scenePaths.Count + " exported to " + outDir);
			CliArgs.Exit(failures > 0 ? 1 : 0);
		}

		/// <summary>"Root/Child/Leaf" under the scene roots (inactive objects included); null when absent.</summary>
		internal static GameObject FindByPath(GameObject[] roots, string path)
		{
			var parts = path.Split('/');
			var root = roots.FirstOrDefault(r => r.name == parts[0]);
			if (!root) return null;
			var t = root.transform;
			for (var i = 1; i < parts.Length && t; i++)
			{
				Transform next = null;
				foreach (Transform child in t)
					if (child.name == parts[i]) { next = child; break; }
				t = next;
			}
			return t ? t.gameObject : null;
		}
	}
}
