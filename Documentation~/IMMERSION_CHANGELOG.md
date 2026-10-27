# IMMERSION fork changelog

Fork-specific history (upstream's `CHANGELOG.md` is left untouched so upstream merges stay
clean). Current layout and concern map: `IMMERSION.md`; lighting contract: `IMMERSION_lighting.md`.

## 2026-10-27 — per-renderer avatar probes (1019 eyes), RendererProbeDump

- `AvatarProbeExporter`: a renderer whose anchor resolves to another probe than its avatar's (1019: `Organica`, the eyes
  and teeth, on `RP_Avatars_Eyes` ×0.82 / Axona `RP_Avatars_Eyes_Axona` ×1.13 while body, clothes and hair sit on
  `RP_Avatars`) is exported too, once per probe (`<prefix>_<probe>_Equirect_RGBM<range>[_webready].png`), and listed in
  the manifest under the avatar's `renderers` map (the web's `entry.avatar.probe.renderers`, runtime 0.1.74). The
  per-avatar renderer count moved to `renderersOnProbe`.
- `Diagnostics/RendererProbeDump.cs`: what `Renderer.GetClosestReflectionProbes` returns for every avatar / anchored
  renderer of a scene (`-additive` loads the play-mode set next to it), plus each probe's box, importance, intensity and
  cubemap. Settled that each 1019 `Organica` binds its anchor's own probe, though both eye probes share one position and
  box (the play-mode capture agrees).

## 2026-10-26 — ProbeMipDump (Unity's probe mips as the GPU reads them)

- `Diagnostics/ProbeMipDump.cs`: blits an imported cubemap through `Hidden/CubemapToEquirect` at each `-lods` value
  (`SAMPLE_TEXTURECUBE_LOD`) into linear float equirects + `probe-mips.json`. `-synthetic` imports known cubes (white, a
  2×2 point light anywhere) with the `-cube` importer's settings into a temp folder (deleted afterwards). Found the
  importer's per-mip rules the web runtime now rebuilds (runtime 0.1.73): mips 4–5 solid-angle GGX, mip 6 a
  per-source-texel lobe ((cos t + 0.7) / 1.7)², mip 7 a 2×2 box of mip 6.
- `CubemapToEquirect.shader`: `_Lod` (default −1 = the plain sample every exporter path keeps).

## 2026-10-23 — Axon flipbook + engine flame materials, scene timelines

- `GltfCustomData`: `Axona/Flipbook Emissive URP` → `customShader.shader = "flipbook"` (atlas rows / columns / frame
  count / fps / start frame / loop / manual frame, `_Color`, raw HDR emission, inset, alpha clip; `_MainTex` as the
  colour texture) and `Axon/Mobile Engine Flame` → `"engineFlame"` (raw HDR core / tail colours, intensity, speed).
  Both used to export as plain PBR (black base colour: the 1040 console screens rendered dark).
- `DirectorBakeProbe` (play mode): records every transform + activeSelf under each play-on-awake PlayableDirector's
  bound objects at a fixed step, on the director's time axis, including what the bound Animator's controller does
  after a wrap-None director stops.
- `SceneBatchExporter -animations false` (no controller clips) and `-activate <paths>` (export objects a timeline
  switches on).

## 2026-10-14 — Clip bake: the key before the clip end, and a looping clip's last key

204 `A_Talk_Relaxed_2` (looping, not seamless: its end pose is ~75 mm / 10 deg from its start at the fingertips)
failed the parity gate on its last 6 frames. Two bake errors, both fixed:
- `BakePropertyAnimation` looped `i < nbSamples` and moved the last key to `length`, dropping the grid key at
  `(nbSamples - 1) * deltaTime`, so every baked clip's final interval was two frames wide. It now writes
  `nbSamples + 1` keys (the grid `BakeGrid` documents); a zero-length clip keeps one key.
- `CollectClipCurvesBySampling` sampled a looping clip's playable at `t = length`, which wraps to frame 0, so the
  last key held the START pose. It now samples `length - 1e-4 s` (key still at `length`): the curve end Unity
  shows until the wrap. Seamless / loop-blended clips are unchanged.
- Result: `A_Talk_Relaxed_2` key 0.030 mm / 0.028 deg, between 0.89 mm / 0.59 deg (was 0.37 mm / 52.8 mm, 7.3 deg).
  Clips exported before this keep a two-frame last interval (they passed the between-key bound); a re-export
  changes their sha256.

## 2026-10-14 — ClipParityReference: state parameters, additive layers, hips under the Root mask part

Found on the 204 LuxMed controllers (`metaCoach_male`, `man_conflict_from_vr_no_hands`), which the ERT/Disney
`DynamicAnimator` cases never exercised:
- `Case.parameters` (`"talk=1;level=4"`, by the controller's parameter types): a state whose transitions leave on a
  parameter condition (`talk IfNot`, `level NotEqual`) ended at its first frame, so the trace had no coverage.
- `Case.additiveAsOverride`: an additive layer is traced as override through a non-persistent controller clone (the
  asset is untouched), so the trace on the layer's masked bones is the clip itself — the gate checks the bake; the
  additive composition is the runtime's and is not compared. Meta records `layerBlending` + `evaluatedAs` + `parameters`.
- `BodyPart(Hips)` is `Root`, not `Body`: the hips carry the humanoid body transform, which a mask's Root part gates.
  With Body on and Root off (204 `UpperBodyMask`) Unity leaves the hips to the layers below; the old mapping listed the
  hips as driven and failed three correct bakes by 120-370 mm. No gated ERT/Disney state has such a mask (DynamicAnimator Body = `Mask_BodyNoHead`, Root on;
  `Mask_Arms` / `Mask_Head` have no Body), so their results are unchanged.

## 2026-09-26 — LowerBodyTraceProbe (Lisa lower-body question) + IkHandFrameProbe.CaptureScene internal

- `Diagnostics/LowerBodyTraceProbe.cs` (`-executeMethod Immersion.Export.LowerBodyTraceProbe.Run -scene <logic scene>
  -out <dir> [-rigs "Lisa"] [-seconds 7]`): reuses IkHandFrameProbe's scene capture, then in play mode (fixed 60 fps)
  rebuilds each CharacterIK rig five times side by side (scene controller + CharacterIK mirror; IK off; applyRootMotion
  off; the state clip through a manual `AnimationClipPlayable` with foot IK on / off at the same clip time) and
  records the Animator GameObject plus every body bone's world pose per frame (`lower-body-trace.json`).
- Result (531 Lisa, `OnKnees`): Unity's legs are locally frozen (0.000 deg articulation), the lower body only rides
  the hips (<= 4.8 mm / 0.6 deg, model units). UnityGLTF's humanoid bake (`CollectClipCurvesBySampling`,
  `AnimationClipPlayable` default foot IK ON) reproduces the foot-IK variant to 0 mm, while the controller state has
  `IKOnFeet 0`: the baked legs pin the feet and flex the knees (the web shows leg motion Unity does not have).
- `IkHandFrameProbe.CaptureScene` is now `internal` (shared by LowerBodyTraceProbe).

## 2026-09-26 — AnimatorSettingsProbe (effective applyRootMotion of logic-scene characters)

- `Diagnostics/AnimatorSettingsProbe.cs` (`-executeMethod Immersion.Export.AnimatorSettingsProbe.Run
  -scenes "a.unity;b.unity" -out <dir> [-seconds 3] [-playClips "Name=clip.anim;..."]`): edit mode reads every
  Animator of each logic scene through the API (`applyRootMotion`, `updateMode`, `cullingMode`, `isHuman`,
  `hasRootMotion`, avatar, controller, source model + its own Animator values, prefab-instance overrides on the
  Animator, `OnAnimatorMove` implementers, `KeepPositionRotation` values). Play mode (fixed 60 fps) rebuilds each
  rig from its nearest prefab asset at the scene pose without touching `applyRootMotion`, reads the values again,
  and with `-playClips` plays each clip once through Playables (with and without the rig's `KeepPositionRotation`)
  and records the GameObject's visible yaw and XZ travel. Writes `animator-settings.json`.
- Result (531 ERT + 316 Disney, all 8 Animators): `applyRootMotion` true everywhere (FBX default, no override), no
  script takes root motion over; the GameObject turns by the clip's full root yaw (Dismisser `inplace_impatientlywaiting`
  36.7 deg), clamped by `KeepPositionRotation` on Camilla.

## 2026-09-26 — ClipBatchExporter (one GLB per clip, web clip pool)

- `Avatar/ClipBatchExporter.cs` (`-executeMethod Immersion.Export.ClipBatchExporter.ExportClips
  -rig <prefab|fbx> -clips "a.anim;b.fbx::Take" -out <dir> [-names] [-poseController] [-keepRenderers 1]`):
  bakes each clip on the reference rig through an in-memory single-state AnimatorController (never
  saved; built from plain objects, because AddLayer/AddState register undo creations that the
  humanoid sampler's `Undo.PerformUndo` reverts mid-export). Renderers are disabled, not destroyed:
  the node hierarchy stays the avatar export's and blendshape curves still resolve their renderer.
  The rest pose (node TRS = the web rig profile) comes from `-poseController`'s default state at t=0
  via `AvatarBatchExporter.PoseToDefaultState` (now `internal`, unchanged); the posed locals are
  re-applied after the controller swap (the rebind resets the skeleton to bind). Writes
  `<name>.glb` + `<name>.clip.json` (length, frame rate, loop, bake-into-pose / keep-original
  flags, humanoid bone map). `AvatarBatchExporter`'s behaviour is unchanged.
- Verified on 26 clips (531 Dismisser, 316 Camilla): after the HelperTools library strip, clips
  shared with the live avatar exports match them to < 1e-6 deg on every bone at 25 times, rest
  pose identical, twist helpers 0.000 deg from bind.

## 2026-09-25 — IkHandFrameProbe (IK hand-frame calibration)

- `Diagnostics/IkHandFrameProbe.cs`: batchmode play-mode probe. Opens a logic scene, records every
  `CharacterIK` rig (source model, controller, avatar, root/bone/target transforms), rebuilds the rigs
  in an empty scene and drives `OnAnimatorIK` with a line-for-line mirror of `CharacterIK`. Per clip
  time it samples an IK-off frame and IK-on frames with the scene target rotation plus 5 perturbed
  ones, and dumps target vs `CC_Base_*_Hand` world rotations, arm bones, the Animator's goal
  readback and the Avatar's internal pre/post rotations to `-out/ik-hand-frame.json`. The play-mode
  MonoBehaviours share the file with the static entry class on purpose (Unity refuses AddComponent
  for an Editor-folder MonoBehaviour that owns a MonoScript). Result (531 Lisa/Bert): the goal-to-hand
  offset is constant (spread < 1e-4 deg) and equals `R_side · inverse(Avatar.GetPostRotation(hand))`.

## 2026-09-24 — avatars export with their logic-scene renderer state

- `AvatarBatchExporter -logicScene <scene> [-logicInstances "A;B"]` (`Avatar/LogicSceneRendererOverrides.cs`):
  each avatar is exported with the renderer materials, material-slot count, mesh and enabled state of
  its instance in the logic scene instead of the prefab defaults. Instance = the named Animator object
  (aligned with `-avatars`), else the single instance of the prefab's original source asset (FBX /
  base prefab); renderers match by original source object, else by hierarchy path. State travels as
  `GlobalObjectId`s across the switch to the empty export scene; the instance is unpacked before the
  change (the humanoid sampler's undo pair re-merges prefab instances). Every change is logged as a
  `submesh[indexCount] -> material` pairing against the prefab's. Why: the ERT prefabs pair the
  Organica eyes/teeth submesh with `T_EyeOcc` (MetaCoach: OcclusionMask → black eyes on the web)
  while the logic scene draws it with `T_Organica` and leaves the shell submesh undrawn.
- `ExporterMeshes.ExportPrimitive` (tagged): a submesh without a material slot is not exported —
  Unity does not draw it; upstream wrapped the slot index and drew it with slot 0's material.
- Batchmode compile of `70826523` verified clean (no change needed).

## 2026-09-24 — integration check fixes (compile verified clean afterwards)

- `Bakery/Light` materials (Bakery area-light meshes) export as `KHR_materials_unlit` with
  `_Color · intensity` (linear, clamped to 1): Unity shows them as bright unlit panels; before,
  they came out as lit PBR and rendered as black squares on the web (Home: 88 ceiling panels).
- Lights whose `lightmapBakeType` is `Baked` are no longer exported (`KHR_lights_punctual`): they
  have no runtime effect in Unity (OfficeBuilding shipped 8, incl. a π directional).
- Needs a re-export of `SalesForce_Debranded2` (panels) and `SalesForce_OfficeBuilding` (lights).

## 2026-09-24 — Revolution exporter contract v2 (art v2 ported as a superset)

- **Contract v2** in `GltfCustomData` (material + lighting halves): every key of the art team's
  Revolution exporter v2 (`Assets/_Project/Revolution` @ `3f86b8dd01`, 09-23) with the same
  name/type/meaning — `customShader.textures` = glTF texture **indices**, `hsv {h,s,v}` + `isHsv`
  always, `uvRotation`, `isPbr`, `alphaTest`, `alphaCutoff`, `skin`, hair `_SpecColor` linear,
  normal maps decoded **without** the green flip, probe binding `reflection_probe` (uuid) on
  renderers and probe nodes, probe `texture` = index, lightmap RGBM range and probe equirect
  range **5 → 8** — plus `"version": 2` on every `customShader` / `customData`, and what Unity
  still renders but art v2 drops: `reflectionContribution`, the `_DETAIL` maps, glass
  `color`/`cullMode`/`_BaseTex`, simpleLit `cullMode` (`_CullMode` 0 → glTF `doubleSided`), the new
  `Immersion/Web/OcclusionMask` shader (`shader: "occlusionMask"`), `textureNames {slot: name}`,
  renderer `lm_scale_offset` (numeric) + `reflection_probe_node`, probe `range` +
  `bounds_center`/`bounds_extents`. `lm_uv_scale_offset` is culture-invariant.
- Probe uuid = `GlobalObjectId.targetObjectId` (as art) with a deterministic FNV-1a fallback for
  unsaved scenes (avatar CLI) / collisions; cached per probe.
- Decoded normal maps are cached per source texture: no duplicate images (the art v2 GLB had 13).
  The generic normal fallback in `ExporterMaterials` skips `Immersion/Web/*` (no extra glTF
  `normalTexture` next to the decoded copy).
- Lightmaps: sidecar names unchanged (`<name>_Lightmap-<i>_RGBM8.png`); the GLB placeholder / full
  page is named `<lm>_<i>_RGBM8lightmap` (art v2 name); `IMMERSION_lightmaps` payload **version
  3** with `rgbmRange: 8` at the root and per page; the offsets JSON carries `rgbmRange` too.
- Optional caps `MaxTextureSize` / `MaxLightmapSize` on the plugin (default 0 = unlimited; CLI
  `-maxTextureSize` / `-maxLightmapSize`, applied by `ImmersionExportSettings.ApplyDefaults`);
  lightmaps are resampled in HDR before the encode. `SceneBatchExporter -embedLightmaps true`.
- New CLI `AvatarProbeExporter.ExportFromScene`: per-avatar baked probes resolved from a logic
  scene (`probeAnchor` → probe → baked `.exr` + intensity) → `exr2equirect.py --rgbm --webready`-
  identical RGBM64 PNGs + `<prefix>_avatar_probes.json`.
- The consuming project must drop its `Assets/_Project/Revolution` copy (same global classes and
  `Hidden/*` shaders).
- **Fix — scene CLI settings:** `SceneBatchExporter` built its fresh settings before `OpenScene`;
  the Single-mode load unloaded that unreferenced ScriptableObject and `ExportContext` silently
  fell back to the project's settings asset, so no `ImmersionExportSettings` value (transmission
  off, strip opt-out) ever reached a scene export. Settings are now built per scene after the load;
  both CLIs throw if the context does not hold their settings object.
- CLI exports write lossless **PNG only** (the JPEG heuristic followed the active build target's
  imported format) and bypass UnityGLTF's persistent image cache.

## 2026-09-23 — cleanup wave

- **Layout by concern.** All Immersion code moved under `Editor/Scripts/Immersion/{Avatar,
  Materials, Lighting, Cli, Diagnostics}`; the former Runtime plugins (`LightmapExport`,
  `ReflectionProbeExport`, `LightingExportUtils`, `AnimatorExtrasExport`) now live in the Editor
  assembly. `GltfCustomData` is a partial class split into its material and lighting halves.
  Encode shaders under `Editor/Shaders/Immersion/`. Shared CLI helpers in `Cli/CliArgs.cs`.
- **`AvatarMaterialMaps` replaces `AvatarMaterialFixExport`.** One declarative per-shader table
  (SG_FakeSSSSkin / SG_FakeSSS / SG_HairAnisoFakeSSS / SG_AvatarsClothes / RL_CorneaShaderBasic /
  RL_TearlineShader graphs + Reallusion Amplify skin/hair/teeth/tongue/cornea) exports base
  colour, normal, alpha, culling and a GPU-packed glTF ORM texture (`Hidden/IMMERSION_ChannelPack`)
  straight from the material. Skin/teeth/clothes gain occlusion + per-texel roughness/metallic
  (the old plugin stripped AO and left roughness 1), hair clips at `_AlphaClip` (the property the
  graph actually uses) and follows the material's `_Cull`, the orphan mask images are gone from
  the GLB.
- **Transmission is configuration.** `MaterialExtensionsExport.KHR_materials_transmission`
  defaults to off (tagged upstream hunk) and both batch exporters force it off via
  `ImmersionExportSettings`; the "preserve specular" path made URP transparent shells invisible
  glass on the web.
- **`ReflectionProbeExport`** writes only the `<name>_reflection.png` strip; the unread
  `IMMERSION_reflection_probe` node extension, the embed toggle and the texture-scale hooks are
  gone. The skybox fallback is opt-out (`skyboxFallback`) and off for avatar exports.
- **`GltfCustomData`**: `extras.customData` is written once per renderer node. The old loop also
  stamped the parent's lightmap index/tiling onto every child node after the children's own
  callbacks, so nested renderers lost their own binding and probe. Debug.Log noise removed;
  decoded normal-map temporaries are now destroyed after the export.
- **Removed** (no consumer on the web side): `DefaultPoseExport` (re-posed skinned meshes to bind
  pose on every export, including scene batches — default-state posing in `AvatarBatchExporter`
  is the one pose path), `AnimatorControllerExport` + `IMMERSION_animator_controller` (the
  flattened `scenes[].extras.IMMERSION_animator` is the contract), `SceneSettingsExport` +
  `IMMERSION_scene_settings`, the LDR/Photopea lightmap decode (`UnityGLTFLightmapDecode.shader`,
  `LightmapExport` embed/scale/cap — superseded on 2026-09-02 by the lossless RGBM8 pages),
  `UnityGLTFCubemapToEquirect.shader`, `ExportEnviroProbes` (531 one-off), the global
  *Export Texture Scale / Max Size* mechanism (`GLTFSettings`, `ExporterTextures`,
  `UniqueTexture.Scale` back to upstream — texture sizing happens after export in the Editor's
  Texture Tools), the Reallusion-specific albedo name list in `ExporterMaterials` (the generic
  Albedo/Main/Color/Base/Diffuse matcher stays), the `Invisible.mat` serializer bump and the
  fork's hijack of upstream's `CHANGELOG.md`.
- Upstream-file hunks are tagged `// IMMERSION:` (`ExporterAnimation`, `ExporterMaterials`,
  `GLTFSceneExporter` sidecar API, `MaterialExtensionsExport`).

## 2026-09-02

- Lightmap pages ship as lossless RGBM8 sidecars (`<name>_Lightmap-<i>_RGBM8.png`), the GLB keeps
  4×4 black placeholders; `IMMERSION_lightmaps` payload version 2 (resolved names, no `rgbmPages`);
  `GLTFSceneExporter.SidecarBaseName`. Decoded normal maps keep a mip chain.

## 2026-08-29

- The Revolution export pipeline moved from the app repo into the package (`GltfCustomData`,
  encode shaders, `ExportEnviroProbes`, `SceneStatistics`).

## 2026-07 … 2026-08

- `AvatarBatchExporter` (default-state posing via `Animator.Play` + `Update`, prefab unpack /
  activate, fresh settings), `SceneBatchExporter`, `TwistProbe` (proved Unity's runtime leaves
  unmapped `*Twist*` bones at bind rotation — no twist solve is applied anywhere),
  `AnimatorExtrasExport`, RL material fallbacks in `ExporterMaterials`, metallic 0 / roughness 1
  for unknown shaders, spec-gloss → metallic-roughness fallback.

## 2026-06-25

- `IMMERSION_animator_controller` (since removed), blend-tree / sub-state-machine clip baking in
  `ExporterAnimation`, texture downscale settings (since removed).
