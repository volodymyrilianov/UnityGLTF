using System;
using System.Collections.Generic;
using System.Globalization;
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
	/// Batch-mode export of the per-AVATAR baked reflection probes of a Unity logic scene, in the
	/// format the web's per-avatar probe channel consumes (<c>entry.avatar.probe = { objectId,
	/// intensity, rgbmRange }</c>): one RGBM equirect PNG per avatar plus a JSON manifest. No
	/// manual steps — the probe, its baked cubemap and its intensity are resolved from the scene.
	///
	/// Usage:
	/// <code>
	/// Unity.exe -batchmode -quit -projectPath &lt;proj&gt; \
	///   -executeMethod Immersion.Export.AvatarProbeExporter.ExportFromScene \
	///   -scene "Assets/_Project/Scenes/Logic/Logic_Salesforce_ERT_Evacuation.unity" \
	///   -out "C:/exports" [-range 64] [-webready true] [-avatars "MetaCoach;Lisa"] [-prefix Name]
	/// </code>
	///
	/// Resolution, per avatar (= a GameObject carrying an <see cref="Animator"/>):
	/// 1. every Renderer below it whose <c>probeAnchor</c> is set (Unity <c>m_ProbeAnchor</c>; the
	///    Optimized avatars point every renderer at their own <c>OptimizedReflectionProbe</c>);
	/// 2. the anchor's <see cref="ReflectionProbe"/> — the component on the anchor itself, else the
	///    baked probe whose box contains the anchor position (highest importance, then smallest
	///    box: what Unity's non-blended probe selection picks); the probe most renderers resolve to
	///    wins;
	/// 3. its baked cubemap (<c>customBakedTexture</c> in Custom mode, <c>bakedTexture</c> in Baked
	///    mode) — the RAW <c>.exr</c> asset (a 6-face horizontal strip, +X −X +Y −Y +Z −Z, linear
	///    HDR), read uncompressed through a temporary importer copy (the project's own import is
	///    BC6H-compressed), and <c>ReflectionProbe.intensity</c>.
	///
	/// Encode = <c>HelperTools/probe-tools/exr2equirect.py --rgbm --webready --range 64</c>, ported
	/// 1:1 (same direction mapping, bilinear face sampling, RGBM quantisation, web-ready vertical
	/// flip + 180° yaw): <c>4w × 2w</c> RGBA8 PNG, <c>hdr = rgb · (a/255) · range</c> (linear).
	/// <c>intensity</c> in the manifest (and its <c>avatarProbe</c> fragment) is the multiplier Unity's shaders apply,
	/// <c>textureHDRDecodeValues.x</c> (the Inspector value gamma → linear in a linear project; Inspector value kept as
	/// <c>inspectorIntensity</c>).
	/// Output name <c>&lt;prefix&gt;_&lt;avatar&gt;_Equirect_RGBM&lt;range&gt;[_webready].png</c> — the editor
	/// Avatar Probe card prefills <c>rgbmRange</c> from the <c>_RGBM&lt;n&gt;</c> token and picks the
	/// all-default orientation for <c>_webready</c>. Manifest <c>&lt;prefix&gt;_avatar_probes.json</c>
	/// lists avatar → file, intensity, rgbmRange, source probe/cubemap.
	///
	/// Per-renderer probes: Unity anchors probes per renderer, and a renderer whose anchor resolves to another probe
	/// than its avatar's (1019: the <c>Organica</c> eyes and teeth on <c>RP_Avatars_Eyes</c>) is exported too, once per
	/// probe, as <c>&lt;prefix&gt;_&lt;probe&gt;_Equirect_RGBM&lt;range&gt;[_webready].png</c>, and listed under the avatar's
	/// <c>renderers</c> (renderer name → file, intensity, probe) — the web's <c>entry.avatar.probe.renderers</c>.
	///
	/// Exit code 0 = every avatar with an anchored probe exported; 1 = a failure (lines start with
	/// "[AvatarProbeExporter]").
	/// </summary>
	public static class AvatarProbeExporter
	{
		private const string TempFolder = "Assets/__ImmersionAvatarProbeTmp";

		public static void ExportFromScene()
		{
			var scenePath = CliArgs.Get("-scene");
			var outDir = CliArgs.Get("-out");
			if (string.IsNullOrEmpty(scenePath) || string.IsNullOrEmpty(outDir))
			{
				Debug.LogError("[AvatarProbeExporter] usage: -scene \"Assets/.../Logic.unity\" -out <dir> [-range 64] [-webready true] [-avatars \"A;B\"] [-prefix Name]");
				CliArgs.Exit(1);
				return;
			}

			var rangeArg = CliArgs.Get("-range");
			var range = string.IsNullOrEmpty(rangeArg) ? 64.0 : double.Parse(rangeArg, CultureInfo.InvariantCulture);
			var webready = !string.Equals(CliArgs.Get("-webready"), "false", StringComparison.OrdinalIgnoreCase);
			var only = new HashSet<string>(CliArgs.Split(CliArgs.Get("-avatars")).Where(s => s.Length > 0));

			int failures;
			try
			{
				Directory.CreateDirectory(outDir);
				var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
				if (!scene.IsValid()) throw new Exception("scene not found/openable at '" + scenePath + "'");
				var prefix = CliArgs.Get("-prefix");
				if (string.IsNullOrEmpty(prefix)) prefix = scene.name;
				failures = Export(scene.GetRootGameObjects(), scenePath, prefix, outDir, range, webready, only);
			}
			catch (Exception e)
			{
				failures = 1;
				Debug.LogError("[AvatarProbeExporter] FAIL: " + e);
			}
			finally
			{
				if (AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.DeleteAsset(TempFolder);
			}

			CliArgs.Exit(failures > 0 ? 1 : 0);
		}

		private sealed class AvatarProbe
		{
			public GameObject Avatar;
			public ReflectionProbe Probe;
			public string Resolution;
			public int Renderers;
			public int RenderersOnOtherProbes;
			public Dictionary<string, ReflectionProbe> OtherRenderers;
		}

		private static int Export(GameObject[] roots, string scenePath, string prefix, string outDir,
			double range, bool webready, HashSet<string> only)
		{
			var allProbes = roots.SelectMany(r => r.GetComponentsInChildren<ReflectionProbe>(true)).ToArray();

			// avatar -> (probe -> renderer count), avatar -> (renderer name -> probe)
			var votes = new Dictionary<GameObject, Dictionary<ReflectionProbe, int>>();
			var byRenderer = new Dictionary<GameObject, Dictionary<string, ReflectionProbe>>();
			var resolutions = new Dictionary<ReflectionProbe, string>();
			var others = new JArray();
			foreach (var renderer in roots.SelectMany(r => r.GetComponentsInChildren<Renderer>(true)))
			{
				if (renderer.probeAnchor == null) continue;
				var probe = ResolveAnchorProbe(renderer.probeAnchor, allProbes, out var how);
				if (probe == null) continue;
				resolutions[probe] = how;

				var animator = renderer.GetComponentInParent<Animator>(true);
				if (animator == null)
				{
					others.Add(new JObject { ["renderer"] = PathOf(renderer.transform), ["probe"] = PathOf(probe.transform) });
					continue;
				}
				var avatar = animator.gameObject;
				if (!votes.TryGetValue(avatar, out var perProbe)) votes[avatar] = perProbe = new Dictionary<ReflectionProbe, int>();
				perProbe[probe] = perProbe.TryGetValue(probe, out var n) ? n + 1 : 1;
				if (!byRenderer.TryGetValue(avatar, out var names)) byRenderer[avatar] = names = new Dictionary<string, ReflectionProbe>();
				names[renderer.name] = probe;
			}

			var avatars = votes
				.Select(kv =>
				{
					var best = kv.Value.OrderByDescending(p => p.Value).First();
					return new AvatarProbe
					{
						Avatar = kv.Key, Probe = best.Key, Resolution = resolutions[best.Key],
						Renderers = best.Value, RenderersOnOtherProbes = kv.Value.Values.Sum() - best.Value,
						OtherRenderers = byRenderer[kv.Key].Where(r => r.Value != best.Key).ToDictionary(r => r.Key, r => r.Value),
					};
				})
				.Where(a => only.Count == 0 || only.Contains(a.Avatar.name))
				.OrderBy(a => a.Avatar.name, StringComparer.Ordinal)
				.ToList();

			var rangeToken = FormatRange(range);
			var entries = new JArray();
			var failures = 0;
			var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var rendererFiles = new Dictionary<ReflectionProbe, JObject>();
			foreach (var a in avatars)
			{
				var label = a.Avatar.name;
				try
				{
					var rp = a.Probe;
					var cube = (rp.mode == ReflectionProbeMode.Custom ? rp.customBakedTexture : rp.bakedTexture) ?? rp.texture;
					var cubePath = cube != null ? AssetDatabase.GetAssetPath(cube) : null;
					if (string.IsNullOrEmpty(cubePath)) throw new Exception("probe '" + PathOf(rp.transform) + "' has no baked cubemap asset");
					if (!cubePath.EndsWith(".exr", StringComparison.OrdinalIgnoreCase)) throw new Exception("baked cubemap is not an .exr: " + cubePath);

					var strip = ReadExrStrip(cubePath, out var face, out var stripWidth, out var hdrMax);
					var png = EncodeEquirect(strip, face, stripWidth, range, webready, out var clippedPercent);

					var baseName = Sanitize(prefix + "_" + label);
					while (!usedNames.Add(baseName)) baseName += "_";
					var fileName = baseName + "_Equirect_RGBM" + rangeToken + (webready ? "_webready" : "") + ".png";
					File.WriteAllBytes(Path.Combine(outDir, fileName), png);

					var renderers = new JObject();
					foreach (var other in a.OtherRenderers.OrderBy(r => r.Key, StringComparer.Ordinal))
					{
						var rp2 = other.Value;
						if (!rendererFiles.TryGetValue(rp2, out var entry2))
						{
							var cube2 = (rp2.mode == ReflectionProbeMode.Custom ? rp2.customBakedTexture : rp2.bakedTexture) ?? rp2.texture;
							var cubePath2 = cube2 != null ? AssetDatabase.GetAssetPath(cube2) : null;
							if (string.IsNullOrEmpty(cubePath2) || !cubePath2.EndsWith(".exr", StringComparison.OrdinalIgnoreCase))
								throw new Exception("renderer '" + other.Key + "': probe '" + PathOf(rp2.transform) + "' has no baked .exr cubemap");
							var strip2 = ReadExrStrip(cubePath2, out var face2, out var stripWidth2, out var hdrMax2);
							var png2 = EncodeEquirect(strip2, face2, stripWidth2, range, webready, out var clipped2);
							var name2 = Sanitize(prefix + "_" + rp2.name);
							while (!usedNames.Add(name2)) name2 += "_";
							var file2 = name2 + "_Equirect_RGBM" + rangeToken + (webready ? "_webready" : "") + ".png";
							File.WriteAllBytes(Path.Combine(outDir, file2), png2);
							entry2 = new JObject
							{
								["file"] = file2,
								["intensity"] = WebIntensity(rp2),
								["inspectorIntensity"] = rp2.intensity,
								["avatarProbe"] = new JObject { ["intensity"] = WebIntensity(rp2), ["rgbmRange"] = range },
								["probe"] = new JObject
								{
									["path"] = PathOf(rp2.transform), ["mode"] = rp2.mode.ToString(), ["cubemap"] = cubePath2,
									["faceSize"] = face2, ["hdrMax"] = hdrMax2, ["clippedTexelPercent"] = clipped2,
								},
							};
							rendererFiles[rp2] = entry2;
							Debug.Log("[AvatarProbeExporter] OK probe " + PathOf(rp2.transform) + " -> " + file2 + " (" + cubePath2
								+ ", intensity " + rp2.intensity.ToString(CultureInfo.InvariantCulture) + ")");
						}
						renderers[other.Key] = entry2.DeepClone();
					}

					entries.Add(new JObject
					{
						["avatar"] = label,
						["path"] = PathOf(a.Avatar.transform),
						["active"] = a.Avatar.activeInHierarchy,
						["source"] = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(a.Avatar) ?? "",
						["file"] = fileName,
						["width"] = face * 4,
						["height"] = face * 2,
						["intensity"] = WebIntensity(rp),
						["inspectorIntensity"] = rp.intensity,
						["rgbmRange"] = range,
						["webready"] = webready,
						// the scene-config fragment for this avatar (objectId = the uploaded PNG's object)
						["avatarProbe"] = new JObject { ["intensity"] = WebIntensity(rp), ["rgbmRange"] = range },
						["probe"] = new JObject
						{
							["path"] = PathOf(rp.transform),
							["resolvedBy"] = a.Resolution,
							["mode"] = rp.mode.ToString(),
							["cubemap"] = cubePath,
							["faceSize"] = face,
							["hdrMax"] = hdrMax,
							["clippedTexelPercent"] = clippedPercent,
						},
						["renderersOnProbe"] = a.Renderers,
						["renderersOnOtherProbes"] = a.RenderersOnOtherProbes,
						// renderer name -> its own probe (the scene-config `entry.avatar.probe.renderers` fragment)
						["renderers"] = renderers,
					});
					Debug.Log("[AvatarProbeExporter] OK " + label + " -> " + fileName + " (probe " + PathOf(rp.transform)
						+ " [" + a.Resolution + "], " + cubePath + ", intensity " + rp.intensity.ToString(CultureInfo.InvariantCulture)
						+ ", " + face + "px faces, hdrMax " + hdrMax.ToString("0.##", CultureInfo.InvariantCulture)
						+ ", clipped " + clippedPercent.ToString("0.###", CultureInfo.InvariantCulture) + "%)");
				}
				catch (Exception e)
				{
					failures++;
					Debug.LogError("[AvatarProbeExporter] FAIL " + label + ": " + e);
				}
			}

			var manifest = new JObject
			{
				["version"] = 1,
				["scene"] = scenePath,
				["encoding"] = "rgbm",
				["rgbmRange"] = range,
				["webready"] = webready,
				["decode"] = "hdr = rgb * (a / 255) * rgbmRange, linear; " + (webready
					? "web-ready orientation (vertical flip + 180 deg yaw baked in): default import settings"
					: "exporter orientation: import settings Flip Y ON / Rotate Y 180"),
				["avatars"] = entries,
				["otherAnchoredRenderers"] = others,
			};
			var manifestName = Sanitize(prefix) + "_avatar_probes.json";
			File.WriteAllText(Path.Combine(outDir, manifestName), manifest.ToString());
			Debug.Log("[AvatarProbeExporter] done: " + (avatars.Count - failures) + "/" + avatars.Count + " avatar probes -> " + Path.Combine(outDir, manifestName));
			if (avatars.Count == 0)
			{
				Debug.LogError("[AvatarProbeExporter] no avatar renderer with a probe anchor found in " + scenePath);
				failures++;
			}
			return failures;
		}

		// The multiplier Unity's shaders apply to this probe: unity_SpecCube0_HDR.x = textureHDRDecodeValues.x, which in a
		// linear-colour project is the Inspector intensity taken gamma -> linear (1019: 0.82 -> 0.638, 1.13 -> 1.3085). The
		// web multiplies by entry.avatar.probe.intensity as is, so this is the value it gets.
		private static double WebIntensity(ReflectionProbe rp) => Math.Round(rp.textureHDRDecodeValues.x, 5);

		// The probe an anchor stands for: the component on the anchor (the Optimized avatars
		// anchor at their own OptimizedReflectionProbe), else the baked probe whose box contains
		// the anchor position — highest importance first, then the smallest box.
		private static ReflectionProbe ResolveAnchorProbe(Transform anchor, ReflectionProbe[] probes, out string how)
		{
			var own = anchor.GetComponent<ReflectionProbe>();
			if (own != null) { how = "anchor component"; return own; }

			how = "box containing the anchor";
			var p = anchor.position;
			return probes
				.Where(r => r.bounds.Contains(p))
				.OrderByDescending(r => r.importance)
				.ThenBy(r => r.bounds.size.x * r.bounds.size.y * r.bounds.size.z)
				.FirstOrDefault();
		}

		// ───────────────────── EXR read (raw, uncompressed) ─────────────────────

		/// <summary>
		/// The cubemap EXR as float RGB, rows TOP-FIRST (image order, like OpenCV), via a
		/// temporary uncompressed RGBAFloat 2D import of a copy (the project asset itself is left
		/// untouched; the temp folder is deleted afterwards).
		/// </summary>
		private static float[] ReadExrStrip(string assetPath, out int face, out int width, out double hdrMax)
		{
			if (!AssetDatabase.IsValidFolder(TempFolder))
				AssetDatabase.CreateFolder("Assets", TempFolder.Substring("Assets/".Length));
			var tmpPath = TempFolder + "/" + Guid.NewGuid().ToString("N") + ".exr";
			File.Copy(Path.GetFullPath(assetPath), Path.GetFullPath(tmpPath), true);
			AssetDatabase.ImportAsset(tmpPath, ImportAssetOptions.ForceSynchronousImport);
			try
			{
				var importer = (TextureImporter)AssetImporter.GetAtPath(tmpPath);
				importer.textureType = TextureImporterType.Default;
				importer.textureShape = TextureImporterShape.Texture2D;
				importer.sRGBTexture = false;
				importer.mipmapEnabled = false;
				importer.isReadable = true;
				importer.npotScale = TextureImporterNPOTScale.None;
				importer.alphaSource = TextureImporterAlphaSource.None;
				importer.maxTextureSize = 16384;
				importer.textureCompression = TextureImporterCompression.Uncompressed;
				var platform = importer.GetDefaultPlatformTextureSettings();
				platform.format = TextureImporterFormat.RGBAFloat;
				platform.maxTextureSize = 16384;
				platform.textureCompression = TextureImporterCompression.Uncompressed;
				importer.SetPlatformTextureSettings(platform);
				foreach (var target in new[] { "Standalone", "Android", "iPhone", "WebGL" })
					importer.ClearPlatformTextureSettings(target);
				importer.SaveAndReimport();

				var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(tmpPath);
				if (tex == null) throw new Exception("temporary import of " + assetPath + " failed");
				width = tex.width;
				face = tex.height;
				if (width != face * 6) throw new Exception("not a 6-face horizontal strip: " + width + "x" + face + " (" + assetPath + ")");

				var pixels = tex.GetPixels(); // Unity rows are bottom-first
				var rgb = new float[width * face * 3];
				hdrMax = 0;
				for (int y = 0; y < face; y++)
				{
					int src = (face - 1 - y) * width;
					for (int x = 0; x < width; x++)
					{
						var c = pixels[src + x];
						int o = (y * width + x) * 3;
						rgb[o] = c.r; rgb[o + 1] = c.g; rgb[o + 2] = c.b;
						hdrMax = Math.Max(hdrMax, Math.Max(c.r, Math.Max(c.g, c.b)));
					}
				}
				return rgb;
			}
			finally
			{
				AssetDatabase.DeleteAsset(tmpPath);
			}
		}

		// ───────────────────── Equirect encode (exr2equirect.py port) ─────────────────────

		private static byte[] EncodeEquirect(float[] strip, int w, int stripWidth, double range, bool webready, out double clippedPercent)
		{
			int outW = w * 4, outH = w * 2;
			var hdr = new double[outW * outH * 3];
			for (int py = 0; py < outH; py++)
			{
				double theta = (py + 0.5) / outH * Math.PI; // row 0 = PNG top = up
				double sinT = Math.Sin(theta), cosT = Math.Cos(theta);
				for (int px = 0; px < outW; px++)
				{
					double phi = ((px + 0.5) / outW * 2.0 - 1.0) * Math.PI;
					double dx = sinT * Math.Sin(phi), dy = cosT, dz = sinT * Math.Cos(phi);
					double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
					SampleCube(strip, w, stripWidth, dx / len, dy / len, dz / len, hdr, (py * outW + px) * 3);
				}
			}

			var tex = new Texture2D(outW, outH, TextureFormat.RGBA32, false, true);
			var outPixels = new Color32[outW * outH];
			long clipped = 0;
			for (int py = 0; py < outH; py++)
			{
				// web-ready: vertical flip + half-width roll (np.flipud + np.roll(outW/2, axis=1))
				int sy = webready ? outH - 1 - py : py;
				for (int px = 0; px < outW; px++)
				{
					int sx = webready ? ((px - outW / 2) % outW + outW) % outW : px;
					int s = (sy * outW + sx) * 3;
					double r = hdr[s] / range, g = hdr[s + 1] / range, b = hdr[s + 2] / range;
					double mx = Math.Max(r, Math.Max(g, b));
					if (mx > 1.0) clipped++;
					double m = Math.Min(Math.Max(mx, 1.0 / 255.0), 1.0);
					m = Math.Ceiling(m * 255.0) / 255.0;
					// Texture2D row 0 is the BOTTOM of the PNG EncodeToPNG writes
					outPixels[(outH - 1 - py) * outW + px] = new Color32(
						Q(r / m), Q(g / m), Q(b / m), Q(m));
				}
			}
			tex.SetPixels32(outPixels);
			tex.Apply(false, false);
			var png = tex.EncodeToPNG();
			UnityEngine.Object.DestroyImmediate(tex);
			clippedPercent = 100.0 * clipped / (outW * (double)outH);
			return png;
		}

		// numpy: np.round(clip(x, 0, 1) * 255) — round half to even
		private static byte Q(double v) => (byte)Math.Round(Math.Min(Math.Max(v, 0.0), 1.0) * 255.0, MidpointRounding.ToEven);

		// D3D/Unity cube face selection + per-face (sc, tc), bilinear with edge clamp — exactly
		// exr2equirect.sample_cubemap_bilinear.
		private static void SampleCube(float[] strip, int w, int stripWidth, double x, double y, double z, double[] outRgb, int o)
		{
			double ax = Math.Abs(x), ay = Math.Abs(y), az = Math.Abs(z);
			int f; double sc, tc, ma;
			if (ax >= ay && ax >= az) { if (x > 0) { f = 0; sc = -z; tc = -y; } else { f = 1; sc = z; tc = -y; } ma = ax; }
			else if (ay > ax && ay >= az) { if (y > 0) { f = 2; sc = x; tc = z; } else { f = 3; sc = x; tc = -z; } ma = ay; }
			else { if (z > 0) { f = 4; sc = x; tc = -y; } else { f = 5; sc = -x; tc = -y; } ma = az; }
			ma = Math.Max(ma, 1e-9);
			double u = (sc / ma + 1.0) * 0.5 * w - 0.5;
			double v = (tc / ma + 1.0) * 0.5 * w - 0.5;
			int u0 = Clamp((int)Math.Floor(u), 0, w - 1), v0 = Clamp((int)Math.Floor(v), 0, w - 1);
			int u1 = Clamp(u0 + 1, 0, w - 1), v1 = Clamp(v0 + 1, 0, w - 1);
			double fu = Math.Min(Math.Max(u - u0, 0.0), 1.0), fv = Math.Min(Math.Max(v - v0, 0.0), 1.0);
			for (int c = 0; c < 3; c++)
			{
				double c00 = strip[(v0 * stripWidth + f * w + u0) * 3 + c];
				double c10 = strip[(v0 * stripWidth + f * w + u1) * 3 + c];
				double c01 = strip[(v1 * stripWidth + f * w + u0) * 3 + c];
				double c11 = strip[(v1 * stripWidth + f * w + u1) * 3 + c];
				outRgb[o + c] = (c00 * (1 - fu) + c10 * fu) * (1 - fv) + (c01 * (1 - fu) + c11 * fu) * fv;
			}
		}

		private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

		private static string FormatRange(double range) =>
			Math.Abs(range - Math.Round(range)) < 1e-9
				? ((long)Math.Round(range)).ToString(CultureInfo.InvariantCulture)
				: range.ToString("0.###", CultureInfo.InvariantCulture);

		private static string Sanitize(string s)
		{
			var invalid = Path.GetInvalidFileNameChars();
			return new string(s.Select(ch => invalid.Contains(ch) || ch == ' ' ? '_' : ch).ToArray());
		}

		private static string PathOf(Transform t)
		{
			var parts = new List<string>();
			for (var c = t; c != null; c = c.parent) parts.Add(c.name);
			parts.Reverse();
			return string.Join("/", parts);
		}
	}
}
