# IMMERSION lighting export

Two export plugins (enabled by default, configurable in **Project Settings ▸ UnityGLTF ▸ Export**)
capture Unity's baked lighting so the Immersion web editor / runtime (three.js) can reproduce
the Unity look.

**The primary output is sidecar files written next to the exported `.glb`/`.gltf`** — the web
side loads loose PNGs + JSON, not data embedded inside a GLB:

```
Bank.glb
Bank_Lightmap-0_RGBM8.png       one per baked lightmap page (lossless RGBM8, full bake res)
Bank_Lightmap-1_RGBM8.png
Bank_lightmap_offsets.json      lightmap manifest (web editor schema)
Bank_reflection.png             6x1 cube-face atlas (main probe, or skybox fallback)
```

A scene has exactly **one file per lightmap page**: the RGBM8 sidecar. All sidecar names are
prefixed with the export's base name, so several scenes can coexist in the same folder or
asset store. Upload them to the editor's project assets together with the GLB; Scene Settings ▸
Load picks them up by name (`{scene}_lightmap_offsets.json`, `colorName` matching for lightmap
pages, `*reflection*` for the environment).

| Plugin | Sidecar output | glTF payload |
|---|---|---|
| `Gltf Custom Shaders Export` (`GltfCustomData`) | `<name>_Lightmap-<i>_RGBM8.png` (the lightmap pages) | `extras.customData` on renderer + probe nodes, `extras.customShader` on `Immersion/Web/*` materials, embedded per-probe equirect |
| `IMMERSION_lightmaps` (`LightmapExport`) | `<name>_lightmap_offsets.json` (no page pixels) | `IMMERSION_lightmaps` (root), `IMMERSION_lightmap` (node) |
| `IMMERSION_reflection_probes` (`ReflectionProbeExport`) | `<name>_reflection.png` (6×1 cube atlas) | — |

The two lightmap plugins are split by responsibility: the custom-shaders plugin writes the page
PIXELS, `IMMERSION_lightmaps` writes the page NAMES + tiling (offsets JSON and both extensions).
Their callback order is undefined, so the page file name is *derived* by both from
`ImmersionLightmapPages.PageFileName(i)` rather than handed over. Disabling the custom-shaders
plugin therefore leaves a scene whose extensions name page files that don't exist; disabling
`IMMERSION_lightmaps` leaves the pages declared (minimal root extension, fallback) but drops the
offsets JSON and the per-node tiling.

By default nothing here resizes textures (web exports ship full resolution; sizes are set after
export, in the Editor's Texture Tools). The custom-shaders plugin has two optional caps, **Max
Texture Size** / **Max Lightmap Size** (0 = unlimited, the default; CLI flags `-maxTextureSize` /
`-maxLightmapSize`): lightmap pages are resampled in HDR *before* the RGBM encode, probe
equirects and decoded normals are rendered at the capped size, every other texture has
UnityGLTF's `UniqueTexture.MaxSize` clamped (bilinear 8-bit blit — never used on RGBM pages).
The reflection strip's face size is capped by its own plugin's **Max Face Size**, default 512.

## Contract v2 — `extras.customShader` (materials)

Since 2026-09-24 the plugin writes contract **version 2**: a superset of the art team's Revolution
exporter v2 (`Assets/_Project/Revolution/Editor/GltfCustomData.cs` @ `3f86b8dd01`, which the fork
replaces — never install both, they declare the same global classes and `Hidden/*` shaders).
Every art key has the same name, type and meaning; the fork adds what Unity still renders but
art v2 drops. Every `customShader` / `customData` object carries `"version": 2`; a consumer treats
an absent version as the v1 contract (texture names, range 5, green-flipped normals, gamma hair
spec colour, `hsv {x,y,z}`).

| shader (`customShader.shader`) | Unity shader | keys |
|---|---|---|
| `simpleLit` | `Immersion/Web/SimpleLitGi` | art: `roughness` `metallic` `ao` (`_RMAMul.xyz`), `emission {r,g,b}` (`_EmissionColor × _EmissiveMapEnabled`, HDR), `scaleOffset {x,y,z,w}` (`_BaseMap_ST`), `uvRotation` (°), `isHsv`, `hsv {h,s,v}` (`_Hue`, `{0,0,0}` when off), `isPbr`, `alphaTest`, `alphaCutoff`, `skin`, `textures {_BumpMap,_RMAMap,_EmissionMap}`. Fork: `version`, `cullMode` (`_CullMode`, 0 Off → glTF `doubleSided`), `reflectionContribution`, `_DETAIL` → `detailScaleOffset` `detailAlbedoScale` `detailNormalScale` + `textures {_DetailMask,_DetailAlbedoMap,_DetailNormalMap}`, `textureNames` |
| `glass` | `Immersion/Web/GlassLitGi` | art: `roughness`, `reflectionStrength`. Fork: `version`, `color {r,g,b,a}` (`_BaseColor`, rgb linear, a raw), `cullMode` (`_Cull`), `textures {_BaseTex}` (colour texture), `textureNames` |
| `hair` | `Immersion/Web/HairShader` | art: `_Params {x,y,z,w}`, `_SpecColor {r,g,b}` **linear**, `_SpecIntensity` `_Roughness` `_Anisotropy` `_SpecShift` `_AO`, `textures {_HairIdMap,_HairAoMap}`. Fork: `version`, `textureNames` |
| `occlusionMask` (fork only) | `Immersion/Web/OcclusionMask` (multiply overlay: `Blend DstColor Zero`, `col = tex(_BaseTex)·_BaseColor; col.rgb += 1 − col.a`) | `version`, `color {r,g,b,a}` (`_BaseColor`, rgb linear, a raw), `scaleOffset` (`_BaseTex_ST`), `cullMode` (`_Cull`), `textures {_BaseTex}` (colour texture), `textureNames` |
| `flipbook` (fork only) | `Axona/Flipbook Emissive URP` (unlit atlas flipbook: `Blend SrcAlpha OneMinusSrcAlpha`, ZWrite Off, Cull Off; cell index = `floor(_StartFrame) + floor(_Time.y · AnimationSpeed)`, row-major from the top-left cell, wrapped over the frame count when `_Loop`; `rgb = tex·_Color + tex·_EmissionColor·_EmissionIntensity`, `a = tex.a·_Color.a`) | `version`, `rows` (`Rows_Number`), `columns` (`Columns_Number`), `frameCount` (`_FrameCount`, 0 = all cells), `fps` (`AnimationSpeed`), `startFrame`, `loop`, `manualMode`, `manualFrame`, `color {r,g,b,a}` (`_Color`, rgb linear, a raw), `emission {r,g,b}` (`_EmissionColor × max(_EmissionIntensity, 0)`, **raw HDR** like `simpleLit` emission), `insetPixels`, `alphaClip`, `cutoff`, `cullMode` 0, `textures {_MainTex}` (colour texture), `textureNames`; glTF `doubleSided`, alphaMode BLEND |
| `engineFlame` (fork only) | `Axon/Mobile Engine Flame` (textureless additive fumes: `Blend SrcAlpha One`, ZWrite Off, Cull Back; per-object phase from `_Time.y · _Speed` + the object's world origin) | `version`, `coreColor {r,g,b}` / `tailColor {r,g,b}` (`_CoreColor` / `_TailColor`, **raw HDR**), `intensity` (`_Intensity`), `speed` (`_Speed`), `cullMode` 2; glTF alphaMode BLEND |

HDR colours (`[HDR]` properties) are written raw, without a gamma→linear conversion: a Unity frame of the
1040 console screens measures 2.4–2.7× the atlas texels in linear light, i.e. `10.42 × 0.24 = 2.50`, where a
`GammaToLinear` of the stored HDR value would give ~42×.

- `textures` values are glTF **texture indices**. Linear data maps (normals, RMA, emission, detail,
  hair) are exported with UnityGLTF's linear "unknown" settings, as art v2; colour maps
  (`_BaseTex`) through the `baseColorTexture` slot (sRGB) — the same image as the glTF
  `baseColorTexture` when UnityGLTF picked that texture up too.
- `textureNames {slot: name}` = the source Unity texture names, for name-based tooling
  (`glb-optimize`, editor inventory / material packs) and index cross-checks after optimizers.
- `_EmissionMap` (simpleLit) is sampled by the web as linear data but authored sRGB: an
  sRGB-imported emission map is blitted into a float linear target and quantised to 8-bit with
  ±0.5 LSB dither (one noise value per pixel shared by r/g/b, fixed seed), with a mip chain. A plain
  8-bit linear blit keeps ~4 levels for sRGB 0–20, which banded the 1040 space sky. The image must
  stay lossless downstream (`glb-optimize` / the editor texture pass keep emission-only surfaces
  lossless).
- Normal maps (`_BumpMap`, `_DetailNormalMap`) are decoded to plain RGB, **not green-flipped**
  (equals the authored OpenGL-convention source PNG), with a mip chain, and decoded ONCE per
  source texture per export (the art v2 GLB carried 13 duplicate normal images).
- The generic normal fallback of `ExporterMaterials` is skipped for `Immersion/Web/*` shaders — no
  extra glTF `normalTexture` next to the decoded copy.
- Not exported (both sides): stencil, `_QueueOffset`, `_Translucent` (declared, unused by the
  fragment), directional lightmaps / shadowmasks.

## Lightmaps

### RGBM8 pages

The `Gltf Custom Shaders Export` plugin encodes every baked lightmap **unclamped** to RGBM8
(`Hidden/RGBMEncode`, `_MaxRange = 8`, decode `hdr = rgb * a * 8` in linear space; range 5 until
2026-09-23 — the range is declared as `rgbmRange` in the extension payload and the offsets JSON,
because the file name does not change) — that is
what the custom `Immersion/Web/*` shaders sample and, decoded to linear HDR, what the vanilla
`material.lightMap` slots get. It is **the only lightmap page a scene exports**, as a loose
sidecar rather than inside the GLB:

- `<name>_Lightmap-<i>_RGBM8.png` — lossless 8-bit RGBA PNG, full bake resolution (unless the
  lightmap cap is set), no tone curve. Same pixel/row orientation as every other exported PNG.
- **the `_RGBM8` suffix is part of the contract**: the web runtime detects the encoding by file
  name (`/_?rgbm8?(\.|$)/i`). Any other name is treated as a legacy LDR page (old scenes). The
  "8" is the 8-bit RGBM *encoding* marker, not the range.
- the GLB keeps a **4×4 black RGBM page** per lightmap named `<unityLightmapName>_<i>_RGBM8lightmap`
  (art v2's embedded-page name; `_RGBM8` until 2026-09-23 — the runtime's embedded-page test
  matches "lightmap" either way), so `extras.customData.lm_index` stays a valid index into the
  model's lightmap page list and page ordering is unchanged. A consumer that ignores the sidecars
  therefore renders visibly unlit rather than subtly wrong.
- the root extension names them (payload **version 3**):

```json
{
  "version": 3,
  "rgbmRange": 8,
  "lightmaps": [ { "lightmapIndex": 0, "image": "Bank_Lightmap-0_RGBM8.png", "rgbmRange": 8 } ]
}
```

Version 2 had the same shape without `rgbmRange` (pages at range 5).

`image` is the resolved sidecar file name, fetchable relative to the GLB as-is. Version 1
pointed `image` at a tone-curve LDR page (in `{name}` token form) and listed the RGBM8 pages in
a separate `rgbmPages` array; both are gone.

Set `GltfCustomData.EmbedFullLightmapPages = true` (or tick **Embed Full Lightmap Pages** on the
plugin) to embed the full-resolution RGBM8 pages in the GLB as well; the sidecars are written
either way.

### Per-renderer binding

Every renderer node gets `extras.customData` (contract **version 2**, 2026-09-24):

```json
{ "version": 2, "lm_index": 0, "lm_uv_scale_offset": "[sx, sy, ox, oy]",
  "lm_scale_offset": [sx, sy, ox, oy], "reflection_probe": "764549533", "reflection_probe_node": 2 }
```

- `lm_index` is -1 for unlit renderers (`lm_uv_scale_offset` is then `""` and `lm_scale_offset`
  absent); the scale/offset is the raw Unity `Renderer.lightmapScaleOffset` — the string is art
  v2's key (culture-invariant), the array carries the same numbers.
- `reflection_probe` is the uuid of the probe with the highest blend weight for that renderer
  (`""` if none): `GlobalObjectId.targetObjectId` as a decimal string, exactly as art v2 — or,
  when that is 0 (objects of an unsaved scene, e.g. the avatar CLI) or already taken by another
  probe in the same export, a deterministic 63-bit FNV-1a hash of scene path + sibling-indexed
  hierarchy path. It matches the probe node's own `customData.reflection_probe`.
- `reflection_probe_node` (fork addition) is that probe's glTF node index when the probe is part
  of the export.
- One `customData` per renderer transform — nothing is propagated to child nodes (art v2 still
  copies the parent's binding onto children).

Version 1 (absent `version`) wrote `reflection_probe_texture` = the probe cubemap's name instead.

### Offsets manifest

`<name>_lightmap_offsets.json` uses the web editor's existing schema
(`packages/shared/src/scene-core.ts` — `buildOffsetLookup` / `applyLightmapOffsetsToScene`):

```json
{
  "lightmaps": [
    { "index": 0, "colorName": "Bank_Lightmap-0", "rgbmRange": 8 }
  ],
  "renderers": [
    {
      "path": "Building/Wall",
      "lightmapIndex": 0,
      "tilingX": 1.0, "tilingY": 1.0,
      "offsetX": 0.0, "offsetY": 0.0
    }
  ]
}
```

- `colorName` is matched case-insensitively against uploaded file names (without extension),
  exact match first, then prefix. It stays `<name>_Lightmap-<i>` — the *prefix* of the RGBM8
  page file (`Bank_Lightmap-0` → `Bank_Lightmap-0_RGBM8.png`). Deliberately unchanged so
  existing projects and uploads keep matching. (Caveat of prefix matching: a scene with ≥11
  pages could resolve `…_Lightmap-1` to `…_Lightmap-10_RGBM8.png` depending on file order;
  real enviro scenes have one or two pages.)
- `rgbmRange` (since 2026-09-24) is the page's RGBM decode range — the editor persists it into
  the scene config so a range-8 page is never decoded at the old implicit 5.
- `path` is the node name path from the export root (the editor matches full path first, then
  leaf name).
- `tilingX/Y`, `offsetX/Y` are the **raw Unity** `Renderer.lightmapScaleOffset` values — the
  editor applies the bottom-left-origin V adjustment itself (`1 - tilingY - offsetY`).
- Lightmap UVs are the mesh UV2, exported as glTF `TEXCOORD_1`.

The glTF node extension `IMMERSION_lightmap` carries the same data in-file:

```json
{
  "lightmapIndex": 0,
  "image": "Bank_Lightmap-0_RGBM8.png",
  "scaleOffset": [sx, sy, ox, oy],
  "scaleOffsetGltf": [sx, sy, ox, oy]
}
```

**Name form — one rule everywhere:** every lightmap file name that reaches an output file is
**resolved**, with no `{name}` token left in it. The token only lives inside the exporter
(`GLTFSceneExporter.SidecarNameToken`): sidecar file names and text sidecars are substituted on
write, and both lightmap extensions substitute it themselves via
`GLTFSceneExporter.SidecarBaseName` before serialization.

(`scaleOffsetGltf` is pre-converted for glTF TEXCOORD_1 with flipY=false textures:
`uv * xy + zw`.)

## Reflection probes

Two consumers, two outputs:

1. **Revolution shaders (per renderer)** — `Gltf Custom Shaders Export` embeds one equirect per
   probe in the GLB (`<probeTexture>_Equirect`, `4w × 2w`, linear, divided by
   `_DynamicRange = 8` — 5 before contract v2 —, 8-bit PNG with alpha 255 (not RGBM),
   `Hidden/CubemapToEquirect` with the probe's own HDR decode values — `unity_SpecCube0_HDR`
   is zero in an editor blit, which used to export all-black probes) and writes on the probe node

   ```json
   { "rp_intensity": 1, "box_projection": true,
     "bounds": "Center: (1.31, 1.83, -10.35), Extents: (12.28, 5.00, 16.12)",
     "texture": 0, "reflection_probe": "764549533",
     "version": 2, "range": 8, "bounds_center": [1.31, 1.83, -10.35], "bounds_extents": [12.28, 5.0, 16.12] }
   ```

   `texture` is the glTF texture **index** of the equirect (-1 if the encode shader is missing;
   v1: the cubemap name), `bounds` is Unity world space at export time (no Unity→glTF −x flip).
   Renderers point at their probe through `reflection_probe` (above). An unbaked probe is
   skipped with a warning.
2. **Web editor scene environment** — `IMMERSION_reflection_probes` writes the **main probe**
   (highest `importance`, then largest volume) as `<name>_reflection.png`, a horizontal 6×1
   cube-face atlas (width = 6 × height, square faces, order +X, −X, +Y, −Y, +Z, −Z in three.js
   `CubeTexture` orientation, sRGB, face size capped by **Max Face Size**). If the export has
   **no baked probe but the scene has a skybox**, the skybox is baked into the strip instead —
   unless the exporter opted out (`ReflectionProbeExport.skyboxFallback`; the avatar batch
   exporter does, so an avatar GLB is not accompanied by the empty scene's procedural sky).

## Gotchas

- **Bake first.** Lightmaps and probes must be baked (Lighting window) before export; unbaked
  probes are skipped with a console warning.
- **Sidecar files need a real file export** — they're written by `SaveGLB` / `SaveGLTFandBin`
  (the editor menu does this). Stream/byte-array exports have no output folder, so no sidecars.
- **git-URL package consumers:** every file needs a committed `.meta` — Unity silently ignores
  files without one inside immutable packages.
- **Directional lightmaps / shadowmasks** are not exported (color lightmap only).
- **URP only:** the encode shaders (`Hidden/RGBMEncode`, `Hidden/CubemapToEquirect`,
  `Hidden/NormalDecodeBlit`, `Hidden/IMMERSION_ChannelPack`) include the URP shader library.
- **Scriptable render pipelines:** the skybox fallback uses `Camera.RenderToCubemap`, which can
  be limited in URP/HDRP; if the bake fails or comes out black, the fallback is skipped with a
  warning. Baked reflection probes and lightmaps are unaffected.
