# IMMERSION additions to UnityGLTF — what is where

This fork (`volodymyrilianov/UnityGLTF`, branch `Immersion_vova_branch`) is upstream UnityGLTF plus
the Unity → web export toolchain of the Immersion MetaCoach project. It is consumed as a UPM
package from the git URL; nothing Immersion-specific should live in a consuming project.

Everything Immersion-specific sits under `Editor/Scripts/Immersion/` (Editor assembly — export
plugins are discovered via `TypeCache` in any assembly, and nobody exports at runtime), grouped
by concern, plus the glTF schema classes next to upstream's and a handful of tagged hunks in
upstream files (`// IMMERSION:`).

```
Editor/Scripts/Immersion/
  Avatar/       AvatarBatchExporter   CLI: avatar prefab -> GLB (poses the rig, bakes clips)
                ClipBatchExporter     CLI: one GLB per clip on a reference rig (web clip-pool objects)
                AvatarMaterialMaps    per-shader export table for the CC/Reallusion avatar shaders
                AnimatorExtrasExport  scenes[].extras.IMMERSION_animator (flattened web animator)
                LogicSceneRendererOverrides  -logicScene: export with the logic-scene instance's renderer state
  Materials/    GltfCustomData        Revolution plugin, material half: extras.customShader
                NormalMapBlit         DXT5nm/BC5 normal -> RGB decode blit
  Lighting/     GltfCustomData.Lighting  Revolution plugin, lighting half: customData, RGBM8 pages, probe equirects
                LightmapExport        IMMERSION_lightmaps: offsets JSON + page names/tiling
                ReflectionProbeExport IMMERSION_reflection_probes: <name>_reflection.png strip
                LightingExportUtils   cube -> strip blit, skybox bake, temp-texture pool
  Cli/          SceneBatchExporter    CLI: whole scenes -> GLB + sidecars (-animations false, -activate for timeline bakes)
                DirectorBakeProbe     play mode: what a scene's play-on-awake PlayableDirectors do to their bound objects, 60 Hz
                AvatarProbeExporter   CLI: logic scene -> one RGBM equirect PNG per avatar probe + manifest
                ImmersionExportSettings  the plugin defaults every web export needs
                CliArgs               -executeMethod argument helpers
  Diagnostics/  TwistProbe            play-mode twist-bone ground truth (closed investigation)
                IkHandFrameProbe      play-mode IK hand frame: IK goal rotation -> hand bone world rotation
                AnimatorSettingsProbe logic-scene Animators: applyRootMotion/modes/avatar/controller + measured root-motion yaw per clip
                LowerBodyTraceProbe   play-mode per-frame body-bone trace of CharacterIK rigs (controller / IK off / no root motion / clip playable with and without foot IK)
                SceneStatistics       material/shader audit window (art helper)
Editor/Shaders/Immersion/             RGBMEncode, CubemapToEquirect, NormalDecode, ChannelPack (URP, Shader.Find)
Runtime/Resources/UnityGLTFCubemapToFaces.shader   6x1 strip blit (Resources.Load — must stay here)
Runtime/Plugins/GLTFSerialization/Extensions/IMMERSION_lightmaps.cs   root + node schema
Documentation~/IMMERSION_lighting.md  the lightmap / probe contract
Documentation~/IMMERSION_CHANGELOG.md fork history (upstream's CHANGELOG is left alone)
```

## "I want to change X" → where the core is

Most of the work is done by **upstream** code; the fork hooks into it. Start in the upstream
file, then look for the plugin.

| Concern | Upstream core | Fork hooks |
|---|---|---|
| Mesh, rig, skin, blendshapes | `GLTFSceneExporter.SaveGLB → ExportScene → ExportNode`, `SceneExporter/ExporterMeshes.cs`, `ExporterSkinning.cs` | one tagged hunk in `ExporterMeshes.ExportPrimitive`: a submesh without a material slot is not exported (Unity does not draw it; upstream wrapped the slot index). Node TRS is captured from the **current pose**, which is why `AvatarBatchExporter` poses first |
| Avatar materials as the module renders them | — | `Avatar/LogicSceneRendererOverrides.cs` (`-logicScene` / `-logicInstances`): materials, slot count, mesh and enabled state of the avatar's logic-scene instance replace the prefab defaults |
| Animation clip baking | `SceneExporter/ExporterAnimation.cs` (`ExportAnimationFromNode → ExportAnimationClips → ConvertClipToGLTFAnimation → GenerateMissingCurves/BakePropertyAnimation`), `ExporterAnimationHumanoid.cs` | two tagged hunks (sub-state-machine + blend-tree clips), `AvatarBatchExporter.PoseToDefaultState` (undriven bones bake CONSTANT curves from the current pose), `AnimatorExtrasExport` (authored flattened animator, set via `-animator`) |
| One clip per GLB (clip pool) | same as above | `Avatar/ClipBatchExporter.cs`: in-memory single-state controller per clip, renderers disabled (hierarchy kept), rest pose from `-poseController`; the library post-process (HelperTools `anim-library/export-pool-clips.mjs`) strips it to rig + one clip |
| Standard materials + textures | `SceneExporter/ExporterMaterials.cs` (`ExportMaterial`, `ExportPBRMetallicRoughness`), `ExporterTextures.cs` | tagged hunks: generic name matchers for custom shaders, metallic 0 default, spec-gloss fallback |
| CC / Reallusion avatar materials | (taken over before the generic path) | `Avatar/AvatarMaterialMaps.cs` — the table; `Shaders/Immersion/ChannelPack.shader` packs the mask channels into glTF ORM |
| Revolution shaders (`Immersion/Web/*`) + the Axon flipbook / engine-flame shaders | — | `Materials/GltfCustomData.cs` → `extras.customShader` contract v2 (+ `NormalMapBlit`); key list in `IMMERSION_lighting.md` ▸ Contract v2 |
| Scene timelines (PlayableDirector) | — | `Cli/DirectorBakeProbe.cs` records the play-mode result (timeline + the bound Animator's controller, director time axis); HelperTools `glb-timeline/glb-bake-timeline.mjs` writes it into the scene GLB as one animation with `extras.IMMERSION_playback` (scene clock); export the scene with `-animations false` and `-activate` for objects the timeline switches on |
| Per-avatar baked probes (scene-config channel) | — | `Cli/AvatarProbeExporter.cs` |
| Lightmaps | — | `Lighting/GltfCustomData.Lighting.cs` (RGBM8 pages + `customData.lm_index`), `Lighting/LightmapExport.cs` (offsets JSON + names) — see `IMMERSION_lighting.md` |
| Reflection probes | — | `Lighting/GltfCustomData.Lighting.cs` (per-probe equirect + `customData`), `Lighting/ReflectionProbeExport.cs` (scene strip) |
| Loose files next to the GLB | `GLTFSceneExporter` — `AddSidecarFile`, `SidecarNameToken`, `SidecarBaseName` (tagged) | every lighting plugin |
| Transmission / KHR material extensions | `Plugins/MaterialExtensionsExport.cs` | `KHR_materials_transmission` default off (tagged) + forced off by `ImmersionExportSettings` |

## Command lines

```
Unity.exe -batchmode -quit -projectPath <proj> -executeMethod Immersion.Export.AvatarBatchExporter.ExportAvatars
  -avatars "Assets/A/Foo.prefab;Assets/B/Bar.prefab" -out "C:/exports"
  [-animator "C:/exports/foo.animator.json;;"] [-controller "Assets/A/Alt.controller;;"]
  [-logicScene "Assets/_Project/Scenes/Logic/Logic_X.unity" [-logicInstances "MetaCoach;;Lisa"]]

Unity.exe -batchmode -quit -projectPath <proj> -executeMethod Immersion.Export.ClipBatchExporter.ExportClips
  -rig "Assets/A/Avatar.fbx" -clips "Assets/Clips/a.anim;Assets/Anims/Take.fbx::Idle01" -out "C:/exports"
  [-names "a;Idle01"] [-poseController "Assets/A/Base.controller"] [-keepRenderers 1]

Unity.exe -batchmode -quit -projectPath <proj> -executeMethod Immersion.Export.SceneBatchExporter.ExportScenes
  -scenes "Assets/Scenes/EnvA.unity;Assets/Scenes/EnvB.unity" -out "C:/exports"
  [-embedLightmaps true] [-animations false] [-activate "Root/Child;Root/Other"]

Unity.exe -batchmode -nographics -projectPath <proj> -executeMethod Immersion.Export.DirectorBakeProbe.Run
  -scene "Assets/Scenes/EnvA.unity" -out "C:/exports" [-hz 60] [-tail 45] [-seconds N] [-directors "Root"]   (no -quit)

Unity.exe -batchmode -quit -projectPath <proj> -executeMethod Immersion.Export.AvatarProbeExporter.ExportFromScene
  -scene "Assets/_Project/Scenes/Logic/Logic_X.unity" -out "C:/exports"
  [-range 64] [-webready false] [-avatars "MetaCoach;Lisa"] [-prefix Name]

Unity.exe -batchmode -projectPath <proj> -executeMethod Immersion.Export.TwistProbe.Run
  -avatars "a.prefab" -controller "a.controller" -out "C:/dir"        (no -quit)

Unity.exe -batchmode -projectPath <proj> -executeMethod Immersion.Export.IkHandFrameProbe.Run
  -scene "Assets/_Project/Scenes/Logic/Logic_X.unity" -out "C:/dir"
  [-times "0.13;0.47;0.81"] [-settle 4]                                (no -quit)

Unity.exe -batchmode -projectPath <proj> -executeMethod Immersion.Export.AnimatorSettingsProbe.Run
  -scenes "Assets/_Project/Scenes/Logic/Logic_X.unity;Assets/.../Logic_Y.unity" -out "C:/dir"
  [-seconds 3] [-playClips "Dismisser=Assets/.../a.anim;Camilla=Assets/.../b.fbx::Take"]   (no -quit)

Unity.exe -batchmode -projectPath <proj> -executeMethod Immersion.Export.LowerBodyTraceProbe.Run
  -scene "Assets/_Project/Scenes/Logic/Logic_X.unity" -out "C:/dir" [-rigs "Lisa"] [-seconds 7]   (no -quit)
```

Scene and avatar exporters take optional `-maxTextureSize N` / `-maxLightmapSize N` (longest side
in px) — default 0 = full resolution; web assets are sized after export in the Editor's Texture
Tools; textures are always written as lossless PNG and UnityGLTF's image cache is bypassed.
Lists align by index; an empty segment means "none / keep the prefab's own". Both exporters
build their settings from `GLTFSettings.GetDefaultSettings()` (the persisted settings asset can
lose its plugin sub-assets in batch mode) and then apply `ImmersionExportSettings.ApplyDefaults`
— so a project's settings asset never decides what a web export contains. Exit code 0 = all
exported, 1 = at least one failed (per-item lines start with `[AvatarBatchExporter]` /
`[ClipBatchExporter]` / `[SceneBatchExporter]`).

## Rules

- The consuming project must NOT also contain the art team's Revolution copy
  (`Assets/_Project/Revolution`): it declares the same global classes (`GltfCustomData`,
  `GltfCustomDataExporter`, `NormalMapBlitExporter`) and `Hidden/*` encode shaders — two plugins
  would register and `Shader.Find` becomes ambiguous. Its v2 contract is ported here as a superset.

- Every file needs a committed `.meta` — a git-URL package silently drops files without one.
- After a fork push, consuming projects pin the commit in `Packages/packages-lock.json`: bump via
  Package Manager ▸ Update, or delete the lock entry.
- The three encode shaders under `Editor/Shaders/Immersion/` are URP-only (they include the URP
  shader library) and are resolved with `Shader.Find` at export time.
- Verify avatar exports numerically (quaternion deltas on `*Twist01` bones against a known-good
  GLB), never by eye; an ad-hoc `Export selected` from a T-posed prefab bakes T-pose statics.
