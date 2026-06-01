# VTuberSystemBase Avatar Mocap Facial Integration

AMFI wires the VTuberSystemBase RAC output path to a local avatar catalog, VMC
mocap, and optional FacialControl setup. The package is intentionally split into
Phase 1 and Phase 2 so avatar display and body mocap can be validated before the
facial stack is enabled.

## Setup

### 1. Package dependencies

Use the Unity Package Manager manifest at `VTuberSystemBase/Packages/manifest.json`
as the source of truth. The AMFI setup requires these entries:

- `com.hidano.realtimeavatarcontroller.mocap-vmc`
  - `git@github.com:Hidano-Dev/RealtimeAvatarController.git?path=RealtimeAvatarController/Packages/com.hidano.realtimeavatarcontroller.mocap-vmc#main`
- FacialControl core and adapter packages, all pinned to
  `#feature/hidano/generate-prototype`
  - `com.hidano.facialcontrol`
  - `com.hidano.facialcontrol.lipsync`
  - `com.hidano.facialcontrol.osc`
  - `com.hidano.facialcontrol.inputsystem`
- `jp.hadashikick.vcontainer` version `1.16.6`

The git dependencies use SSH URLs, so the machine running Unity must have a
GitHub SSH key registered and available to `git`. VContainer is resolved from
OpenUPM; keep the `OpenUPM` scoped registry and include the `jp.hadashikick`
scope. After changing the manifest, open Unity and confirm Package Manager
resolves the RAC, VMC, FacialControl, and VContainer packages without compile
errors.

### 2. AvatarCatalog

Create an `AvatarCatalog` asset from the Unity asset menu:

`Create > VTuberSystemBase > Avatar Mocap Facial Integration > Avatar Catalog`

For each avatar entry:

- Set `AvatarKey` to the key assigned from the Character tab.
- Set `DisplayName` to the label shown in the avatar catalog UI.
- Assign `AvatarPrefab` to an FBX prefab with a Humanoid `Animator`.
- Leave `FacialProfile` empty for Phase 1.
- In Phase 2, assign a `FacialCharacterProfileSO` to `FacialProfile`.

AMFI resolves avatars through `CatalogAvatarKeyResolver`, not Addressables. The
prefab reference in this catalog becomes the `BuiltinAvatarProviderConfig` used
by RAC. `AvatarCatalog.OnValidate` logs duplicate keys and missing prefabs, so
fix those warnings before testing the scene.

### 3. Scene wiring

Use `AmfiCompositionRoot` for the AMFI RAC path. It creates the
`RacMainOutputAdapterBootstrapper`, replaces the default Addressables resolver
with the catalog resolver, injects the VMC mocap factory, and attaches
`SlotMotionDriver` to the bootstrapper's `SlotManager`.

In an IntegratedDemo scene, assign the `AvatarCatalog` to
`IntegratedDemoBootstrap` and keep the AMFI path enabled. AMFI and the legacy
`RacMainOutputAdapterHost` are mutually exclusive because both create a
`SlotManager`; when AMFI starts successfully the legacy host must not be started.

The VMC source uses typeId `VMC` with the default config:

- port: `39539`
- bind address: `0.0.0.0`

Send VMC data from VSeeFace or another VMC sender to that port. If packets stop,
the motion driver keeps the last pose instead of tearing down the avatar.

### 4. FacialControl profile and Adapter Bindings

For Phase 2, create a `FacialCharacterProfileSO`:

`Create > FacialControl > Facial Character Profile`

Configure the profile for the assigned FBX avatar:

- Add expression clips and any required overlay slots in the profile inspector.
- Use the Adapter Bindings section to add the performer-driven inputs you need.
  The installed packages provide bindings such as `OSC`, `Input System`, and
  `uLipSync`; the inspector discovers bindings from packages that use the
  `FacialAdapterBinding` attribute.
- For ARKit / PerfectSync OSC, configure the OSC binding and port expected by the
  performer tool.
- For microphone lip sync, use the uLipSync binding. If no analyzer profile is
  assigned, the lip sync package can fall back to its bundled default profile.
- For keyboard, controller, or analog controls, use the Input System binding and
  assign the relevant `InputActionAsset`.

AMFI only attaches `FacialController`, assigns the profile, and calls
`Initialize()`. It does not route VTSB IPC or Character tab commands into
`Activate()` / `Deactivate()`; facial animation is performer-driven by the
FacialControl adapter bindings.

### 5. Phase switching

The Facial assembly is separated by the `AMFI_FACIAL` scripting define.

- Phase 1: leave `AMFI_FACIAL` undefined. `Facial/VTuberSystemBase.AvatarMocapFacialIntegration.Facial.asmdef` remains outside the compilation target, so avatar display and VMC mocap continue without FacialControl.
- Phase 2: add `AMFI_FACIAL` to Player Settings > Scripting Define Symbols. The Facial asmdef becomes part of compilation and can reference `Hidano.FacialControl.Adapters`.

This project enables `AMFI_FACIAL` for the Standalone build target in `ProjectSettings/ProjectSettings.asset`.

When switching back to Phase 1, remove `AMFI_FACIAL` and disable facial setup on
`AmfiCompositionRoot`. Avatar display and VMC body mocap should continue to work
without compiling the Facial assembly.

## Validation checklist

- Package Manager resolves all git+ssh packages and `jp.hadashikick.vcontainer`.
- The `AvatarCatalog` contains at least one unique key with a Humanoid FBX
  prefab.
- Phase 1 PlayMode shows the selected avatar and applies VMC body motion.
- Phase 2 has `AMFI_FACIAL` defined, `AmfiCompositionRoot` facial setup enabled,
  and the catalog entry's `FacialProfile` assigned.
- In Phase 2, performer input through FacialControl Adapter Bindings changes the
  avatar face while avatar display and VMC body motion continue to run.
