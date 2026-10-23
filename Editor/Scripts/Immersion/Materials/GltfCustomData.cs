// Revolution material export: extras.customShader for the Immersion/Web/* shaders.
// The lighting half of this plugin (lightmap pages, per-renderer customData, probe equirects)
// lives in ../Lighting/GltfCustomData.Lighting.cs (same partial class).
//
// Contract VERSION 2 (2026-09-24) — a superset of the art team's Revolution exporter v2
// (Assets/_Project/Revolution/Editor/GltfCustomData.cs @ 3f86b8dd01): every art key with the
// same name, type and meaning (texture refs = glTF texture INDICES, hsv {h,s,v} + isHsv always,
// uvRotation, isPbr, alphaTest, alphaCutoff, skin, hair _SpecColor LINEAR, normal maps NOT
// green-flipped, ranges 8), plus the keys Unity still renders that art v2 drops
// (reflectionContribution, _DETAIL maps, glass colour, cull mode, the OcclusionMask shader) and
// an optional textureNames {slot: name} map for name-based tooling. Every customShader object
// carries "version": 2 — the web runtime dispatches on it (absent = the v1 contract).
// Full key list: Documentation~/IMMERSION_lighting.md ("Contract v2").

using System;
using System.Collections.Generic;
using GLTF.Schema;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityGLTF;
using UnityGLTF.Plugins;

public class GltfCustomData : GLTFExportPlugin
{
    /// <summary>
    /// Global override for the serialized <c>embedFullLightmapPages</c> field, for batch/CLI
    /// exports that run on <c>GLTFSettings.GetDefaultSettings()</c> (e.g.
    /// <c>Immersion.Export.SceneBatchExporter</c>) and therefore never see the settings asset.
    /// Set it to true to restore the pre-sidecar behaviour.
    /// </summary>
    public static bool EmbedFullLightmapPages = false;

    /// <summary>
    /// Global overrides of the serialized size caps for batch/CLI exports (-1 = use the
    /// serialized value, 0 = unlimited, N = longest side capped at N px). The CLIs set them from
    /// <c>-maxTextureSize</c> / <c>-maxLightmapSize</c> and default to 0: web exports ship full
    /// resolution and are sized afterwards in the Editor's Texture Tools.
    /// </summary>
    public static int MaxTextureSizeOverride = -1;
    public static int MaxLightmapSizeOverride = -1;

    [SerializeField]
    [Tooltip("Embed the full-resolution RGBM8 lightmap pages inside the GLB (legacy behaviour). " +
             "Off (default): every page ships as a lossless '<name>_Lightmap-<i>_RGBM8.png' sidecar " +
             "next to the GLB and only a 4x4 black placeholder page stays in the GLB — so lm_index " +
             "and the lightmap page order/names are unchanged, but the GLB no longer carries the pixels.")]
    private bool embedFullLightmapPages = false;

    [Tooltip("Maximum width/height of textures exported by this plugin (custom shader textures, reflection " +
             "probe equirects and UnityGLTF's standard material textures). Larger textures are downsampled. " +
             "0 = unlimited (default: web exports are sized after export in the Editor's Texture Tools).")]
    [SerializeField] private int _maxTextureSize = 0;

    [Tooltip("Maximum width/height of exported lightmap pages (resampled in HDR before the RGBM encode, " +
             "aspect preserved). 0 = unlimited (default).")]
    [SerializeField] private int _maxLightmapSize = 0;

    public override string DisplayName => "Gltf Custom Shaders Export";
    public override string Description => "Exports custom shaders and textures to glTF";
    public override bool EnabledByDefault => true;
    public override bool AlwaysEnabled => false;

    public int MaxTextureSize
    {
        get => _maxTextureSize;
        set => _maxTextureSize = Mathf.Max(0, value);
    }

    public int MaxLightmapSize
    {
        get => _maxLightmapSize;
        set => _maxLightmapSize = Mathf.Max(0, value);
    }

    public override GLTFExportPluginContext CreateInstance(ExportContext context)
    {
        return new GltfCustomDataExporter(
            EmbedFullLightmapPages || embedFullLightmapPages,
            MaxTextureSizeOverride >= 0 ? MaxTextureSizeOverride : _maxTextureSize,
            MaxLightmapSizeOverride >= 0 ? MaxLightmapSizeOverride : _maxLightmapSize);
    }
}

public partial class GltfCustomDataExporter : GLTFExportPluginContext
{
    /// <summary>
    /// Version stamped on every <c>extras.customShader</c> and <c>extras.customData</c> object.
    /// 2 = texture refs are glTF texture indices, probe binding by uuid, RGBM/probe range 8,
    /// normal maps unflipped, hair spec colour linear, hsv {h,s,v}. Absent = the v1 contract.
    /// </summary>
    public const int CONTRACT_VERSION = 2;

    /// <summary>RGBM range of the lightmap pages: <c>hdr = rgb * a * LIGHTMAP_RGBM_RANGE</c>.</summary>
    public const float LIGHTMAP_RGBM_RANGE = ImmersionLightmapPages.RgbmRange;

    /// <summary>Divisor of the linear probe equirects: <c>hdr = rgb * PROBE_RANGE</c> (8-bit, alpha 255).</summary>
    public const float PROBE_RANGE = 8f;

    private const string LIGHTMAP_PACKED_SHADER = "Hidden/RGBMEncode";
    private const string CUBEMAP_PACKED_SHADER = "Hidden/CubemapToEquirect";

    // Size of the black stand-in page embedded in the GLB when the real pages ship as sidecars.
    private const int LIGHTMAP_PLACEHOLDER_SIZE = 4;

    // Texture slot that maps to { linear = true, alphaMode = Always } in
    // GLTFSceneExporter.GetExportSettingsForSlot — RGBM is linear data and MUST keep its alpha
    // (the multiplier channel), and unlike "linearWithAlpha" this slot skips the TextureImporter
    // sRGB probe that only makes sense for on-disk assets.
    private const string RGBM_TEXTURE_SLOT = "rgbm";

    private readonly List<Texture2D> _tempTexturesToDestroy = new();
    private readonly bool _embedFullLightmapPages;

    /// <summary>Maximum texture dimension for non-lightmap textures. 0 = unlimited.</summary>
    private readonly int _maxTextureSize;

    /// <summary>Maximum lightmap page dimension. 0 = unlimited.</summary>
    private readonly int _maxLightmapSize;

    /// <summary>Pages generated by <see cref="ExportLightmaps"/> (full pages + placeholders): they use the lightmap cap.</summary>
    private readonly HashSet<Texture> _lightmapTextures = new();

    /// <summary>
    /// Decoded normal maps, one per (source texture, size, flip). A material-by-material decode
    /// makes a NEW Texture2D every time, which UnityGLTF cannot dedupe — the art v2 demo GLB
    /// carried 13 byte-identical normal images (13.2 MB). Reusing the decoded object makes
    /// <c>ExportTexture</c> return the same glTF texture index.
    /// </summary>
    private readonly Dictionary<(int, int, int, bool), Texture2D> _decodedNormals = new();

    /// <summary>Dithered linear emission maps, one per (source texture, width, height) — see <see cref="DitherLinearEmission"/>.</summary>
    private readonly Dictionary<(int, int, int), Texture2D> _ditheredEmission = new();

    /// <summary>Fixed dither seed: the same source always exports the same bytes.</summary>
    private const int EMISSION_DITHER_SEED = 0x5EED;

    public GltfCustomDataExporter(bool embedFullLightmapPages = false, int maxTextureSize = 0, int maxLightmapSize = 0)
    {
        _embedFullLightmapPages = embedFullLightmapPages;
        _maxTextureSize = Mathf.Max(0, maxTextureSize);
        _maxLightmapSize = Mathf.Max(0, maxLightmapSize);
    }

    // ───────────────────────── Export hooks ─────────────────────────

    // Called for every texture UnityGLTF exports. Clamping MaxSize makes the exporter itself
    // downsample the texture when it encodes it (a plain bilinear 8-bit blit — fine for colour,
    // never for RGBM, which is why lightmap pages are pre-sized in HDR and use their own cap).
    public override void BeforeTextureExport(
        GLTFSceneExporter exporter, ref GLTFSceneExporter.UniqueTexture texture, string textureSlot)
    {
        int limit = _lightmapTextures.Contains(texture.Texture) ? _maxLightmapSize : _maxTextureSize;
        if (limit > 0 && texture.MaxSize > limit)
            texture.MaxSize = limit;

        base.BeforeTextureExport(exporter, ref texture, textureSlot);
    }

    public override void AfterTextureExport(
        GLTFSceneExporter exporter, GLTFSceneExporter.UniqueTexture texture,
        int index, GLTFTexture tex)
    {
        CleanupTempTexturesDelayed();
        base.AfterTextureExport(exporter, texture, index, tex);
    }

    // Exports custom material properties
    public override void AfterMaterialExport(
        GLTFSceneExporter exporter, GLTFRoot gltfRoot,
        Material material, GLTFMaterial materialNode)
    {
        var extras = materialNode.Extras as JObject ?? new JObject();

        switch (material.shader.name)
        {
            case "Immersion/Web/SimpleLitGi":
                ExportSimpleLitGi(exporter, material, materialNode, extras);
                break;
            case "Immersion/Web/GlassLitGi":
                ExportGlassLitGi(exporter, material, extras);
                break;
            case "Immersion/Web/HairShader":
                ExportHairShader(exporter, material, extras);
                break;
            case "Immersion/Web/OcclusionMask":
                ExportOcclusionMask(exporter, material, materialNode, extras);
                break;
            case "Bakery/Light":
                ExportBakeryLight(exporter, material, materialNode);
                break;
            case "Axona/Flipbook Emissive URP":
                ExportAxonaFlipbook(exporter, material, materialNode, extras);
                break;
            case "Axon/Mobile Engine Flame":
                ExportEngineFlame(material, materialNode, extras);
                break;
        }

        materialNode.Extras = extras;
        base.AfterMaterialExport(exporter, gltfRoot, material, materialNode);
    }

    // ───────────────────── Material exporters ──────────────────────

    // Immersion/Web/SimpleLitGi (Shared/Shaders/Immersion/SimpleLitGi.shader + Includes/Gi*.hlsl).
    private void ExportSimpleLitGi(GLTFSceneExporter exporter, Material material, GLTFMaterial materialNode, JObject extras)
    {
        Vector2 scale = material.mainTextureScale;
        Vector2 offset = material.mainTextureOffset;
        Vector4 rmaMul = GetVector(material, "_RMAMul", new Vector4(1, 1, 1, 0));
        Vector3 emission = GetVector(material, "_EmissionColor", Vector4.zero) * GetFloat(material, "_EmissiveMapEnabled", 0f);

        // _Hue is only meaningful with the HSV keyword; art v2 writes {0,0,0} when it is off.
        bool isHsv = GetFloat(material, "_Hsv", 0f) > 0.5f;
        Vector4 hsv = isHsv ? GetVector(material, "_Hue", Vector4.zero) : Vector4.zero;
        int cullMode = Mathf.RoundToInt(GetFloat(material, "_CullMode", (float)CullMode.Back));

        var cs = new JObject
        {
            // art v2 keys — same names, order and meaning
            ["shader"] = "simpleLit",
            ["roughness"] = rmaMul.x,
            ["metallic"] = rmaMul.y,
            ["ao"] = rmaMul.z,
            ["emission"] = new JObject { ["r"] = emission.x, ["g"] = emission.y, ["b"] = emission.z },
            ["scaleOffset"] = new JObject { ["x"] = scale.x, ["y"] = scale.y, ["z"] = offset.x, ["w"] = offset.y },
            ["uvRotation"] = GetFloat(material, "_UvRotation", 0f),
            ["isHsv"] = isHsv,
            ["hsv"] = new JObject { ["h"] = hsv.x, ["s"] = hsv.y, ["v"] = hsv.z },
            ["isPbr"] = GetFloat(material, "_PBR", 0f) > 0.5f,
            ["alphaTest"] = GetFloat(material, "_AlphaTest", 0f) > 0.5f,
            ["alphaCutoff"] = GetFloat(material, "_Cutoff", 0.5f),
            ["skin"] = GetFloat(material, "_Skin", 0f) > 0.5f,
            // fork additions
            ["version"] = CONTRACT_VERSION,
            // Unity CullMode enum (0 Off, 1 Front, 2 Back) — SimpleLitGi's Cull [_CullMode].
            // UnityGLTF only derives doubleSided from a property named _Cull, so Off is mirrored
            // into the glTF material below (the Dismisser's double-sided suit).
            ["cullMode"] = cullMode,
        };
        extras["customShader"] = cs;
        if (cullMode == (int)CullMode.Off) materialNode.DoubleSided = true;

        // Unity gates the reflection term by saturate(diffuse + _ReflectionContribution)
        // (GiFragment.hlsl:82); the web decoder needs the authored value for parity.
        if (material.HasProperty("_ReflectionContribution"))
            cs["reflectionContribution"] = material.GetFloat("_ReflectionContribution");

        // _DETAIL keyword: tiled detail albedo/normal + mask (GiFunctions
        // ApplyDetailAlbedo/ApplyDetailNormal — CC skin pore detail lives here).
        // detailUv = uv * _DetailAlbedoMap_ST.xy + .zw for BOTH detail maps.
        var slots = new List<string> { "_BumpMap", "_RMAMap", "_EmissionMap" };
        if (material.IsKeywordEnabled("_DETAIL"))
        {
            Vector2 dScale = material.GetTextureScale("_DetailAlbedoMap");
            Vector2 dOffset = material.GetTextureOffset("_DetailAlbedoMap");
            cs["detailScaleOffset"] = new JObject { ["x"] = dScale.x, ["y"] = dScale.y, ["z"] = dOffset.x, ["w"] = dOffset.y };
            cs["detailAlbedoScale"] = GetFloat(material, "_DetailAlbedoMapScale", 1f);
            cs["detailNormalScale"] = GetFloat(material, "_DetailNormalMapScale", 1f);
            slots.Add("_DetailMask");
            slots.Add("_DetailAlbedoMap");
            slots.Add("_DetailNormalMap");
        }

        CollectAndExportTextures(
            exporter, material, cs, slots,
            (name, tex) => name == "_BumpMap" || name == "_DetailNormalMap" ? DecodeNormal(tex)
                : name == "_EmissionMap" ? DitherLinearEmission(tex)
                : tex);
    }

    // Immersion/Web/GlassLitGi: albedo(_BaseTex) * _BaseColor, probe reflection * _ReflectionStrength,
    // Blend One OneMinusSrcAlpha, Cull [_Cull].
    private void ExportGlassLitGi(GLTFSceneExporter exporter, Material material, JObject extras)
    {
        var cs = new JObject
        {
            ["shader"] = "glass",
            ["roughness"] = GetFloat(material, "_Roughness", 0.5f),
            ["reflectionStrength"] = GetFloat(material, "_ReflectionStrength", 1f),
            ["version"] = CONTRACT_VERSION,
            // _BaseColor: rgb LINEAR (what the shader multiplies with in linear colour space),
            // alpha raw (the pane's coverage in the premultiplied blend).
            ["color"] = ColorJson(GetColor(material, "_BaseColor", Color.white), true),
            ["cullMode"] = Mathf.RoundToInt(GetFloat(material, "_Cull", (float)CullMode.Off)),
        };
        extras["customShader"] = cs;

        CollectColorTextures(exporter, material, cs, "_BaseTex");
    }

    private void ExportHairShader(GLTFSceneExporter exporter, Material material, JObject extras)
    {
        Vector4 param = GetVector(material, "_Params", new Vector4(0, 1, 0, 0));
        // LINEAR, as art v2: the shader multiplies _SpecColor in linear colour space.
        Color specColor = GetColor(material, "_SpecColor", Color.white).linear;

        var cs = new JObject
        {
            ["shader"] = "hair",
            ["_Params"] = new JObject { ["x"] = param.x, ["y"] = param.y, ["z"] = param.z, ["w"] = param.w },
            ["_SpecColor"] = new JObject { ["r"] = specColor.r, ["g"] = specColor.g, ["b"] = specColor.b },
            ["_SpecIntensity"] = GetFloat(material, "_SpecIntensity", 1f),
            ["_Roughness"] = GetFloat(material, "_Roughness", 0.35f),
            ["_Anisotropy"] = GetFloat(material, "_Anisotropy", 0.85f),
            ["_SpecShift"] = GetFloat(material, "_SpecShift", 0.2f),
            ["_AO"] = GetFloat(material, "_AO", 1f),
            ["version"] = CONTRACT_VERSION,
        };
        extras["customShader"] = cs;

        CollectAndExportTextures(exporter, material, cs, new List<string> { "_HairIdMap", "_HairAoMap" });
    }

    // Immersion/Web/OcclusionMask (Shared/Shaders/Immersion/OcclusionMask.shader, new 09-07): a
    // multiply-blended overlay (Blend DstColor Zero, ZWrite Off, Cull [_Cull]) —
    //   col = tex2D(_BaseTex, uv * _BaseTex_ST.xy + _BaseTex_ST.zw) * _BaseColor;
    //   col.rgb += 1 - col.a;   → framebuffer *= col.rgb
    // The 531 coach's T_EyeOcc: _BaseTex = T_Organica_Diffuse (its ALPHA is the occlusion mask),
    // _BaseColor = (0, 0, 0, 0.8) → the eye area is darkened by 0.8 · mask.
    private void ExportOcclusionMask(GLTFSceneExporter exporter, Material material, GLTFMaterial materialNode, JObject extras)
    {
        Vector2 scale = material.HasProperty("_BaseTex") ? material.GetTextureScale("_BaseTex") : Vector2.one;
        Vector2 offset = material.HasProperty("_BaseTex") ? material.GetTextureOffset("_BaseTex") : Vector2.zero;
        int cullMode = Mathf.RoundToInt(GetFloat(material, "_Cull", (float)CullMode.Off));

        var cs = new JObject
        {
            ["shader"] = "occlusionMask",
            ["version"] = CONTRACT_VERSION,
            ["color"] = ColorJson(GetColor(material, "_BaseColor", Color.white), true),
            ["scaleOffset"] = new JObject { ["x"] = scale.x, ["y"] = scale.y, ["z"] = offset.x, ["w"] = offset.y },
            ["cullMode"] = cullMode,
        };
        extras["customShader"] = cs;
        if (cullMode == (int)CullMode.Off) materialNode.DoubleSided = true;

        CollectColorTextures(exporter, material, cs, "_BaseTex");
    }

    // Axona/Flipbook Emissive URP (Axon/AxonaFlipbook/AxonaFlipbook.shader + AxonaFlipbookCommon.hlsl):
    // unlit, Queue Transparent, Blend SrcAlpha OneMinusSrcAlpha, ZWrite Off, Cull Off. The UV is
    // remapped onto ONE cell of a Columns x Rows atlas, the cell index stepping with _Time.y:
    //   index = floor(_StartFrame) + floor(t * AnimationSpeed), wrapped (positive modulo) over
    //   count = _FrameCount > 0 ? clamp(floor(_FrameCount), 1, cells) : cells when _Loop, else clamped;
    //   _ManualMode uses floor(_ManualFrame). Row-major from the TOP-LEFT cell; per-cell inset of
    //   _InsetPixels texels.
    //   rgb = tex.rgb * _Color.rgb + tex.rgb * _EmissionColor.rgb * max(_EmissionIntensity, 0)
    //   a   = saturate(tex.a * _Color.a)   (+ clip(a - _Cutoff) when _AlphaClip)
    // Emission is written RAW (no gamma conversion), like simpleLit's _EmissionColor: a Unity frame
    // of the 1040 console screens measures 2.4-2.7x the atlas texels in linear light, which is
    // 10.42 x 0.24 = 2.50 (a GammaToLinear of the HDR value would give ~42x).
    private static void ExportAxonaFlipbook(GLTFSceneExporter exporter, Material material, GLTFMaterial materialNode, JObject extras)
    {
        Color emission = GetColor(material, "_EmissionColor", Color.white) * Mathf.Max(GetFloat(material, "_EmissionIntensity", 1f), 0f);
        var cs = new JObject
        {
            ["shader"] = "flipbook",
            ["version"] = CONTRACT_VERSION,
            ["rows"] = GetFloat(material, "Rows_Number", 6f),
            ["columns"] = GetFloat(material, "Columns_Number", 6f),
            ["frameCount"] = GetFloat(material, "_FrameCount", 36f),
            ["fps"] = GetFloat(material, "AnimationSpeed", 20f),
            ["startFrame"] = GetFloat(material, "_StartFrame", 0f),
            ["loop"] = GetFloat(material, "_Loop", 1f) > 0.5f,
            ["manualMode"] = GetFloat(material, "_ManualMode", 0f) > 0.5f,
            ["manualFrame"] = GetFloat(material, "_ManualFrame", 0f),
            // _Color: rgb LINEAR (a plain colour property), alpha raw (opacity)
            ["color"] = ColorJson(GetColor(material, "_Color", Color.white), true),
            ["emission"] = new JObject { ["r"] = emission.r, ["g"] = emission.g, ["b"] = emission.b },
            ["insetPixels"] = GetFloat(material, "_InsetPixels", 0.5f),
            ["alphaClip"] = GetFloat(material, "_AlphaClip", 0f) > 0.5f,
            ["cutoff"] = GetFloat(material, "_Cutoff", 0.01f),
            ["cullMode"] = (int)CullMode.Off,
        };
        extras["customShader"] = cs;
        materialNode.DoubleSided = true;
        materialNode.AlphaMode = AlphaMode.BLEND;
        CollectColorTextures(exporter, material, cs, "_MainTex");
    }

    // Axon/Mobile Engine Flame (S01_mostek/VFX/MobileEngineFlame.shader): textureless additive
    // fumes (Blend SrcAlpha One, ZWrite Off, Cull Back, Queue Transparent).
    //   phase = _Time.y * _Speed + dot(objectOriginWS, (1.7, 0.3, 0.7))       (Unity world space)
    //   positionOS.xy *= 0.96 + 0.04 sin(7 phase); positionOS.z *= 0.96 + 0.04 sin(5 phase)
    //   t = saturate(uv.y); rgb = lerp(_CoreColor, _TailColor, smoothstep(0.02, 0.45, t))
    //         * _Intensity * (0.85 + 0.15 sin(32 t - 6 phase))
    //   a = facing^2 * (1 - smoothstep(0.55, 1, t)) * 0.75, facing = saturate(dot(N, V))
    // The [HDR] colours are written RAW, the same rule as the flipbook / simpleLit emission.
    private static void ExportEngineFlame(Material material, GLTFMaterial materialNode, JObject extras)
    {
        Color core = GetColor(material, "_CoreColor", new Color(2.2f, 2.8f, 3.2f, 1f));
        Color tail = GetColor(material, "_TailColor", new Color(0.03f, 0.35f, 1.4f, 1f));
        extras["customShader"] = new JObject
        {
            ["shader"] = "engineFlame",
            ["version"] = CONTRACT_VERSION,
            ["coreColor"] = new JObject { ["r"] = core.r, ["g"] = core.g, ["b"] = core.b },
            ["tailColor"] = new JObject { ["r"] = tail.r, ["g"] = tail.g, ["b"] = tail.b },
            ["intensity"] = GetFloat(material, "_Intensity", 0.8f),
            ["speed"] = GetFloat(material, "_Speed", 2.5f),
            ["cullMode"] = (int)CullMode.Back,
        };
        materialNode.AlphaMode = AlphaMode.BLEND;
    }

    // ───────────────────── Texture collection ──────────────────────

    /// <summary>
    /// Collects textures by property name, optionally preprocesses them, exports them LINEAR
    /// (slot = texture name → UnityGLTF's "unknown" linear/alpha-heuristic settings, as art v2)
    /// and records their glTF texture INDICES under <c>customShader.textures</c> plus their names
    /// under <c>customShader.textureNames</c>. Size limiting happens in <see cref="BeforeTextureExport"/>.
    /// </summary>
    private void CollectAndExportTextures(
        GLTFSceneExporter exporter, Material material, JObject customShader,
        List<string> slots,
        Func<string, Texture2D, Texture2D> preprocess = null)
    {
        var texturesJson = customShader["textures"] as JObject ?? new JObject();
        var namesJson = customShader["textureNames"] as JObject ?? new JObject();
        var seen = new HashSet<Texture2D>();
        var exportSettings = new GLTFSceneExporter.TextureExportSettings { linear = true };

        foreach (string slot in slots)
        {
            if (!material.HasProperty(slot)) continue;

            var source = material.GetTexture(slot) as Texture2D;
            // one slot per source texture per material, as art v2 (a map bound to two slots
            // keeps the first)
            if (source == null || !seen.Add(source)) continue;

            var texture = preprocess != null ? preprocess(slot, source) : source;
            if (texture == null) continue;

            var texId = exporter.ExportTexture(texture, texture.name, exportSettings);
            texturesJson[slot] = texId.Id;
            namesJson[slot] = source.name;
        }

        customShader["textures"] = texturesJson;
        customShader["textureNames"] = namesJson;
    }

    /// <summary>
    /// Colour (albedo-type) textures of the custom shaders: exported through UnityGLTF's
    /// baseColorTexture slot (sRGB, alpha heuristic), so the index is the SAME image the
    /// standard material path exported for the glTF baseColorTexture when it picked the texture
    /// up too (UnityGLTF dedupes by texture + settings).
    /// </summary>
    private static void CollectColorTextures(GLTFSceneExporter exporter, Material material, JObject customShader, params string[] slots)
    {
        var texturesJson = customShader["textures"] as JObject ?? new JObject();
        var namesJson = customShader["textureNames"] as JObject ?? new JObject();
        foreach (var slot in slots)
        {
            if (!material.HasProperty(slot)) continue;
            var tex = material.GetTexture(slot) as Texture2D;
            if (tex == null) continue;
            var texId = exporter.ExportTexture(tex, GLTFSceneExporter.TextureMapType.BaseColor);
            texturesJson[slot] = texId.Id;
            namesJson[slot] = tex.name;
        }
        customShader["textures"] = texturesJson;
        customShader["textureNames"] = namesJson;
    }

    /// <summary>
    /// Decodes a DXT5nm/BC5 normal map to plain RGB — NOT green-flipped (art v2: the result
    /// equals the authored OpenGL/glTF-convention source PNG, MAE ~5 vs 14 for a flip) — at the
    /// capped size, with a mip chain, reusing one decode per source texture.
    /// </summary>
    private Texture2D DecodeNormal(Texture2D source)
    {
        ClampSize(source.width, source.height, _maxTextureSize, out int w, out int h);
        var key = (source.GetInstanceID(), w, h, false);
        if (_decodedNormals.TryGetValue(key, out var cached) && cached != null) return cached;

        var decoded = NormalMapBlitExporter.DecodeNormalToTexture2D(source, flipGreen: false, w, h);
        if (decoded == null) return null;
        _decodedNormals[key] = decoded;
        // temporaries: kept alive until the exporter has encoded them, then destroyed
        _tempTexturesToDestroy.Add(decoded);
        return decoded;
    }

    /// <summary>
    /// The simpleLit <c>_EmissionMap</c> ships LINEAR-encoded 8-bit (the runtime samples every
    /// extras texture as linear data), but emission maps are authored sRGB. A plain 8-bit linear
    /// blit keeps only ~4 levels for sRGB 0–20 (linear 0..2), so a dark emissive gradient — the
    /// 1040 <c>BG_Sphere</c> space sky — bands into flat steps (one linear step at black is
    /// ~13 sRGB display levels). Here the sRGB texture is blitted into a FLOAT linear target and
    /// quantised with ±0.5 LSB dither (one noise value per pixel, shared by r/g/b so the grain
    /// stays colourless): the mip chain and the GPU filter average it back to the true gradient.
    /// Linear-imported textures are returned unchanged (their plain blit is already exact).
    /// The image must then stay lossless downstream (glb-optimize / the editor's texture pass
    /// keep emission-only surfaces lossless).
    /// </summary>
    private Texture2D DitherLinearEmission(Texture2D source)
    {
        if (!UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(source.graphicsFormat)) return source;

        ClampSize(source.width, source.height, _maxTextureSize, out int w, out int h);
        var key = (source.GetInstanceID(), w, h);
        if (_ditheredEmission.TryGetValue(key, out var cached) && cached != null) return cached;

        var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGBFloat, 0)
        {
            msaaSamples = 1,
            depthBufferBits = 0,
            sRGB = false,
            useMipMap = false,
            autoGenerateMips = false
        };

        var rt = RenderTexture.GetTemporary(desc);
        var prev = RenderTexture.active;
        Texture2D linear = null;

        try
        {
            // sRGB texture sampled in a linear-colour-space project: the GPU decodes to linear
            // floats; the float target keeps the precision the 8-bit target loses.
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;
            linear = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
            linear.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            linear.Apply(false, false);

            Color[] src = linear.GetPixels();
            var dst = new Color32[src.Length];
            var rng = new System.Random(EMISSION_DITHER_SEED);
            for (int i = 0; i < src.Length; i++)
            {
                float noise = (float)rng.NextDouble() - 0.5f;
                Color c = src[i];
                dst[i] = new Color32(
                    QuantizeDithered(c.r, noise), QuantizeDithered(c.g, noise), QuantizeDithered(c.b, noise),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(c.a * 255f), 0, 255));
            }

            // mipChain: true, linear: true — as DecodeNormal (the sampler's minFilter follows mipmapCount)
            var outTex = new Texture2D(w, h, TextureFormat.RGBA32, true, true)
            {
                name = source.name,
                wrapMode = source.wrapMode,
                filterMode = source.filterMode,
                anisoLevel = source.anisoLevel
            };
            outTex.SetPixels32(dst);
            outTex.Apply(true, false);

            _ditheredEmission[key] = outTex;
            // temporaries: kept alive until the exporter has encoded them, then destroyed
            _tempTexturesToDestroy.Add(outTex);
            return outTex;
        }
        finally
        {
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            if (linear != null) UnityEngine.Object.DestroyImmediate(linear);
        }
    }

    private static byte QuantizeDithered(float value, float noise) =>
        (byte)Mathf.Clamp(Mathf.FloorToInt(value * 255f + 0.5f + noise), 0, 255);

    // Bakery/Light (Assets/Bakery/ftLight.shader): `_Color * intensity * _MainTex`, no lighting.
    // Bakery area-light meshes stay visible in Unity as bright unlit panels (URP runs the
    // built-in pass as SRPDefaultUnlit); exported plainly they became a lit PBR material and
    // rendered as black squares on the web (SalesForce_Debranded2: 88 ceiling panels). Exported
    // as KHR_materials_unlit with the colour × intensity clamped to 1 — the panel saturates to
    // white on an LDR target either way. No extras: the web keeps it a vanilla (unlit) material.
    private static void ExportBakeryLight(GLTFSceneExporter exporter, Material material, GLTFMaterial materialNode)
    {
        Color lin = GetColor(material, "_Color", Color.white).linear * GetFloat(material, "intensity", 1f);
        if (materialNode.PbrMetallicRoughness == null) materialNode.PbrMetallicRoughness = new PbrMetallicRoughness();
        materialNode.PbrMetallicRoughness.BaseColorFactor = new GLTF.Math.Color(
            Mathf.Clamp01(lin.r), Mathf.Clamp01(lin.g), Mathf.Clamp01(lin.b), 1f);
        materialNode.PbrMetallicRoughness.MetallicFactor = 0;
        if (materialNode.Extensions == null || !materialNode.Extensions.ContainsKey(KHR_MaterialsUnlitExtensionFactory.EXTENSION_NAME))
            materialNode.AddExtension(KHR_MaterialsUnlitExtensionFactory.EXTENSION_NAME, new KHR_MaterialsUnlitExtension());
        exporter.DeclareExtensionUsage(KHR_MaterialsUnlitExtensionFactory.EXTENSION_NAME, false);
    }

    // ───────────────────── Small helpers ───────────────────────────

    private static float GetFloat(Material m, string name, float fallback) =>
        m.HasProperty(name) ? m.GetFloat(name) : fallback;

    private static Vector4 GetVector(Material m, string name, Vector4 fallback) =>
        m.HasProperty(name) ? m.GetVector(name) : fallback;

    private static Color GetColor(Material m, string name, Color fallback) =>
        m.HasProperty(name) ? m.GetColor(name) : fallback;

    /// <summary>{r,g,b,a}; rgb converted to linear when <paramref name="linear"/>, alpha always raw.</summary>
    private static JObject ColorJson(Color c, bool linear)
    {
        var rgb = linear ? c.linear : c;
        return new JObject { ["r"] = rgb.r, ["g"] = rgb.g, ["b"] = rgb.b, ["a"] = c.a };
    }

    /// <summary>
    /// Clamps width/height so neither exceeds <paramref name="maxSize"/>, preserving aspect
    /// ratio (0 = unlimited). Returns true if the size changed.
    /// </summary>
    internal static bool ClampSize(int width, int height, int maxSize, out int newWidth, out int newHeight)
    {
        newWidth = width;
        newHeight = height;

        if (maxSize <= 0) return false;
        if (width <= maxSize && height <= maxSize) return false;

        float scale = (float)maxSize / Mathf.Max(width, height);
        newWidth = Mathf.Max(1, Mathf.RoundToInt(width * scale));
        newHeight = Mathf.Max(1, Mathf.RoundToInt(height * scale));
        return true;
    }
}
