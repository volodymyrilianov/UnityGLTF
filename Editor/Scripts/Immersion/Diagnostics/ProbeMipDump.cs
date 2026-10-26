using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Immersion.Export
{
	/// <summary>
	/// Ground truth for what Unity's avatar shaders read from a probe cubemap: the imported cube (as the
	/// project imports it — GGX-convolved mips, BC6H, seamless filtering) sampled on the GPU with
	/// <c>SAMPLE_TEXTURECUBE_LOD</c> at each requested LOD, written as linear float equirects. SimpleLitGi
	/// reads GI at LOD 5.8 and reflections at <c>roughness × 8</c>; HairShader2 GI at LOD 5. The web
	/// rebuilds those mips itself (Editor <c>unity-probe-mips.ts</c>), and this dump is what it is checked
	/// against.
	///
	/// <code>
	/// Unity.exe -batchmode -quit -projectPath &lt;proj&gt; \
	///   -executeMethod Immersion.Export.ProbeMipDump.Run \
	///   -cube "Assets/.../Probe.exr" -out "C:/dump" [-lods "0;1;2;3;4;5;5.8;6;7"] [-width 256]
	/// </code>
	///
	/// Output per LOD: <c>lod&lt;L&gt;.f32</c> = width × width/2 RGB float32, row-major, row 0 = straight
	/// UP, column 0 = phi −π; direction (sinθ·sinφ, cosθ, sinθ·cosφ) with θ = (row + 0.5)/height · π,
	/// φ = ((col + 0.5)/width · 2 − 1) · π — the exr2equirect.py / CubemapToEquirect layout. Raw cube
	/// values: no probe intensity, decode (1, 1, 0, 0). Plus <c>probe-mips.json</c> (cube format, size,
	/// mip count, the files). No <c>-nographics</c>: the dump renders on the GPU.
	/// </summary>
	public static class ProbeMipDump
	{
		public static void Run()
		{
			var cubePath = CliArgs.Get("-cube");
			var outDir = CliArgs.Get("-out");
			if (string.IsNullOrEmpty(cubePath) || string.IsNullOrEmpty(outDir))
			{
				Debug.LogError("[ProbeMipDump] usage: -cube \"Assets/.../Probe.exr\" -out <dir> [-lods \"0;5.8\"] [-width 256]");
				CliArgs.Exit(1);
				return;
			}

			var code = 0;
			try
			{
				var lodsArg = CliArgs.Get("-lods");
				var lods = (string.IsNullOrEmpty(lodsArg) ? "0;1;2;3;4;5;5.8;6;7" : lodsArg)
					.Split(';').Where(s => s.Trim().Length > 0)
					.Select(s => float.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
				var width = CliArgs.GetInt("-width", 256);
				Directory.CreateDirectory(outDir);

				var synthetic = CliArgs.Get("-synthetic");
				if (string.IsNullOrEmpty(synthetic))
				{
					Dump(cubePath, outDir, lods, width);
				}
				else
				{
					foreach (var kind in CliArgs.Split(synthetic).Where(k => k.Length > 0))
					{
						var sub = Path.Combine(outDir, kind.Replace(':', '_'));
						Directory.CreateDirectory(sub);
						Dump(ImportSynthetic(cubePath, kind), sub, lods, width);
					}
				}
			}
			catch (Exception e)
			{
				code = 1;
				Debug.LogError("[ProbeMipDump] FAIL: " + e);
			}
			finally
			{
				if (AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.DeleteAsset(TempFolder);
			}

			CliArgs.Exit(code);
		}

		private static void Dump(string cubePath, string outDir, float[] lods, int width)
		{
			var height = width / 2;
			var cube = AssetDatabase.LoadAssetAtPath<Cubemap>(cubePath);
			if (cube == null) throw new Exception("no Cubemap at '" + cubePath + "'");
			var shader = Shader.Find("Hidden/CubemapToEquirect");
			if (shader == null) throw new Exception("shader Hidden/CubemapToEquirect not found");
			var material = new Material(shader);
			material.SetTexture("_Cube", cube);
			material.SetVector("_CubeDecode", new Vector4(1, 1, 0, 0));
			material.SetFloat("_DynamicRange", 1f);

			var rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
			var readback = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
			var files = new JArray();
			foreach (var lod in lods)
			{
				material.SetFloat("_Lod", lod);
				Graphics.Blit(null, rt, material);
				var previous = RenderTexture.active;
				RenderTexture.active = rt;
				readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
				readback.Apply(false);
				RenderTexture.active = previous;

				// GetPixels row 0 = uv.y 0 = θ π (down); write row 0 = up.
				var px = readback.GetPixels();
				var bytes = new byte[width * height * 3 * 4];
				var o = 0;
				for (var row = 0; row < height; row++)
				{
					var src = (height - 1 - row) * width;
					for (var col = 0; col < width; col++)
					{
						var c = px[src + col];
						Buffer.BlockCopy(BitConverter.GetBytes(c.r), 0, bytes, o, 4);
						Buffer.BlockCopy(BitConverter.GetBytes(c.g), 0, bytes, o + 4, 4);
						Buffer.BlockCopy(BitConverter.GetBytes(c.b), 0, bytes, o + 8, 4);
						o += 12;
					}
				}
				var name = "lod" + lod.ToString("0.###", CultureInfo.InvariantCulture) + ".f32";
				File.WriteAllBytes(Path.Combine(outDir, name), bytes);
				files.Add(new JObject { ["lod"] = lod, ["file"] = name, ["mean"] = px.Average(c => (c.r + c.g + c.b) / 3f) });
			}

			var manifest = new JObject
			{
				["cube"] = cubePath,
				["format"] = cube.format.ToString(),
				["size"] = cube.width,
				["mipCount"] = cube.mipmapCount,
				["filterMode"] = cube.filterMode.ToString(),
				["width"] = width,
				["height"] = height,
				["layout"] = "RGB float32, row 0 = up, dir (sinT sinP, cosT, sinT cosP), T = (row+0.5)/h*pi, P = ((col+0.5)/w*2-1)*pi",
				["files"] = files,
			};
			File.WriteAllText(Path.Combine(outDir, "probe-mips.json"), manifest.ToString());
			Debug.Log("[ProbeMipDump] " + cubePath + " -> " + outDir);
			UnityEngine.Object.DestroyImmediate(readback);
			rt.Release();
			UnityEngine.Object.DestroyImmediate(material);
		}

		private const string TempFolder = "Assets/__ImmersionProbeMipTmp";

		/// <summary>
		/// <c>-synthetic "white;delta;delta:face:x:y;…"</c> (each dumped to <c>-out/&lt;kind&gt;/</c>): a known input cube imported with the SAME importer settings as
		/// <c>-cube</c> (so its convolution, compression and filtering are what the project does), to read the
		/// importer's per-mip kernel. <c>white</c> = every texel 1 (energy per mip); <c>delta</c> = one 2×2
		/// texel spot of 1000 at the centre of the strip's 5th face (+Z), elsewhere 0 (the lobe per mip);
		/// <c>delta:face:x:y</c> puts the spot's top-left texel at (x, y) of strip face <c>face</c> (0–5).
		/// Imported into a temporary folder that is deleted afterwards.
		/// </summary>
		private static string ImportSynthetic(string templatePath, string kind)
		{
			var template = AssetImporter.GetAtPath(templatePath) as TextureImporter;
			if (template == null) throw new Exception("no TextureImporter at '" + templatePath + "'");
			const int w = 128;
			var strip = new Texture2D(w * 6, w, TextureFormat.RGBAFloat, false, true);
			var px = new Color[w * 6 * w];
			if (kind == "white")
			{
				for (var i = 0; i < px.Length; i++) px[i] = Color.white;
			}
			else if (kind == "delta" || kind.StartsWith("delta:", StringComparison.Ordinal))
			{
				// delta[:face:x:y] — a 2×2 spot whose top-left texel is (x, y) of strip face `face` (default +Z centre).
				var parts = kind.Split(':');
				var face = parts.Length == 4 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 4;
				var sx = parts.Length == 4 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : w / 2 - 1;
				var sy = parts.Length == 4 ? int.Parse(parts[3], CultureInfo.InvariantCulture) : w / 2 - 1;
				for (var i = 0; i < px.Length; i++) px[i] = Color.black;
				for (var y = sy; y <= sy + 1; y++)
				for (var x = sx; x <= sx + 1; x++)
					px[y * w * 6 + face * w + x] = new Color(1000f, 1000f, 1000f, 1f);
			}
			else
			{
				throw new Exception("-synthetic must be white or delta, got '" + kind + "'");
			}
			strip.SetPixels(px);
			strip.Apply(false);
			if (!AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.CreateFolder("Assets", TempFolder.Substring("Assets/".Length));
			var path = TempFolder + "/" + kind.Replace(':', '_') + ".exr";
			File.WriteAllBytes(path, strip.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat));
			UnityEngine.Object.DestroyImmediate(strip);
			AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
			var importer = (TextureImporter)AssetImporter.GetAtPath(path);
			var settings = new TextureImporterSettings();
			template.ReadTextureSettings(settings);
			importer.SetTextureSettings(settings);
			importer.textureCompression = template.textureCompression;
			importer.SaveAndReimport();
			Debug.Log("[ProbeMipDump] synthetic " + kind + " cube imported with the settings of " + templatePath);
			return path;
		}
	}
}
