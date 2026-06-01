# VTuberSystemBase Avatar Mocap Facial Integration

## Facial phase define

The Facial assembly is separated by the `AMFI_FACIAL` scripting define.

- Phase 1: leave `AMFI_FACIAL` undefined. `Facial/VTuberSystemBase.AvatarMocapFacialIntegration.Facial.asmdef` remains outside the compilation target, so avatar display and VMC mocap continue without FacialControl.
- Phase 2: add `AMFI_FACIAL` to Player Settings > Scripting Define Symbols. The Facial asmdef becomes part of compilation and can reference `Hidano.FacialControl.Adapters`.

This project enables `AMFI_FACIAL` for the Standalone build target in `ProjectSettings/ProjectSettings.asset`.
