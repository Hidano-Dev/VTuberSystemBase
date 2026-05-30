#nullable enable
using System;
using VTuberSystemBase.CoreIpc.Abstractions;
using VTuberSystemBase.UiToolkitShell.AssetLoading;
using VTuberSystemBase.UiToolkitShell.Diagnostics;
using VTuberSystemBase.UiToolkitShell.Panels;
using VTuberSystemBase.UiToolkitShell.Skin;
using LogLevel = VTuberSystemBase.UiToolkitShell.Diagnostics.LogLevel;

namespace VTuberSystemBase.UiToolkitShell.Bootstrap
{
    /// <summary>
    /// Mutable configuration handed to <c>UiShellBootstrapper.StartShell</c> (design.md
    /// §Bootstrap §UiShellBootstrapper). The bootstrapper validates the required fields up
    /// front and surfaces missing dependencies as <see cref="BootstrapErrorCode"/> values
    /// rather than throwing, so the lifecycle driver can abort startup safely without
    /// bringing the host process down.
    /// </summary>
    /// <remarks>
    /// Required fields are <see cref="SkinProfile"/>, <see cref="IpcBus"/>, and
    /// <see cref="TabMountStrategy"/>. Optional fields default to production-grade
    /// implementations: <see cref="DiagnosticsLogger"/> defaults to a fresh
    /// <c>DiagnosticsLogger</c>, <see cref="AddressablesInitializer"/> defaults to the
    /// real Addressables-backed initializer, and <see cref="DisplayAssignmentStrategy"/>
    /// defaults to <see cref="FixedDisplayZeroStrategy"/> until the runtime-display-selector
    /// integration spec replaces it.
    /// </remarks>
    public sealed class UiShellConfig
    {
        /// <summary>Required (Requirement 6.3, 6.4, 6.7). Null surfaces as <see cref="BootstrapErrorCode.SkinProfileMissing"/>.</summary>
        public UiToolkitShellSkinProfile? SkinProfile { get; set; }

        /// <summary>Required (Requirement 5.1, 5.10). Null surfaces as <see cref="BootstrapErrorCode.IpcAbstractionUnavailable"/>.</summary>
        public ICoreIpcBus? IpcBus { get; set; }

        /// <summary>
        /// Required for tests; production omits and lets the bootstrapper construct a default
        /// strategy that builds tab UIDocument GameObjects from <see cref="SkinProfile"/>.
        /// Returning <c>false</c> from <see cref="ITabMountStrategy.MountTabs"/> or throwing
        /// surfaces as <see cref="BootstrapErrorCode.TabUxmlAttachFailed"/>.
        /// </summary>
        public ITabMountStrategy? TabMountStrategy { get; set; }

        /// <summary>
        /// Optional. Defaults to <see cref="VTuberSystemBase.UiToolkitShell.AssetLoading.AddressablesInitializer"/>
        /// (the real Addressables wrapper). Async failure surfaces as
        /// <see cref="BootstrapErrorCode.AddressablesInitFailed"/>.
        /// </summary>
        public IAddressablesInitializer? AddressablesInitializer { get; set; }

        /// <summary>
        /// Optional. Defaults to a fresh <c>DiagnosticsLogger</c> seeded with
        /// <see cref="MinimumLogLevel"/>. Tests inject a recording logger to assert the
        /// per-step log emission contract.
        /// </summary>
        public IDiagnosticsLogger? DiagnosticsLogger { get; set; }

        /// <summary>
        /// Optional. Defaults to <see cref="FixedDisplayZeroStrategy"/> until the
        /// runtime-display-selector integration replaces it (Requirement 1.6).
        /// </summary>
        public IDisplayAssignmentStrategy? DisplayAssignmentStrategy { get; set; }

        /// <summary>
        /// Optional. When non-null, the bootstrapper asks this factory to create a clear-only
        /// "presenter camera" on the resolved <see cref="DisplayAssignmentStrategy"/> display so
        /// the UI Toolkit overlay panel has a stable, cleared compositing surface. Defaults to
        /// <c>null</c> = disabled — hosts opt in by supplying
        /// <see cref="DefaultOperatorUiPresenterCameraFactory"/> (or a custom implementation), or
        /// leave it null to run without a presenter camera. The returned handle is disposed on
        /// <c>StopShell</c> in reverse order with every other subsystem.
        /// </summary>
        public IOperatorUiPresenterCameraFactory? PresenterCameraFactory { get; set; }

        /// <summary>
        /// Optional callback invoked once during <c>StartShell</c> after the diagnostics
        /// logger is ready and before the root UIDocument is built. Production wiring
        /// passes <c>() =&gt; CommonUiRegistration.RegisterAll()</c>; tests pass a no-op or
        /// a probe lambda. The callback runs in a try/catch so a thrown exception is
        /// recorded but does not abort startup — UxmlFactory registration is not on the
        /// fatal path (Unity auto-registers nested UxmlFactory types on assembly load).
        /// </summary>
        public Action? CommonUiRegistrationCallback { get; set; }

        /// <summary>
        /// Initial requested target display. Forced to 0 by the bootstrapper /
        /// <see cref="Panels.RootUiDocumentBuilder"/> per Requirement 1.7 — the field is
        /// preserved only so a future display-selector integration can observe what was
        /// originally requested before the override.
        /// </summary>
        public int RequestedTargetDisplay { get; set; } = 0;

        /// <summary>Minimum log level applied when this config supplies the default <c>DiagnosticsLogger</c>.</summary>
        public LogLevel MinimumLogLevel { get; set; } = LogLevel.Info;

        /// <summary>Initial active tab the bootstrapper hands to <c>TabBarController</c>.</summary>
        public TabId InitialTab { get; set; } = TabId.Character;
    }
}
