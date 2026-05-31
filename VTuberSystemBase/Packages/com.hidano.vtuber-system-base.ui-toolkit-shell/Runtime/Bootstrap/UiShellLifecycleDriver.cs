#nullable enable
using System;
using UnityEngine;
using VTuberSystemBase.UiToolkitShell.Diagnostics;
using LogLevel = VTuberSystemBase.UiToolkitShell.Diagnostics.LogLevel;
using LogCategory = VTuberSystemBase.UiToolkitShell.Diagnostics.LogCategory;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace VTuberSystemBase.UiToolkitShell.Bootstrap
{
    /// <summary>
    /// Static façade that bridges Unity's lifecycle (PlayMode start/stop, Standalone quit,
    /// Editor PlayMode state changes) to <see cref="IUiShellBootstrapper"/> Start/Stop calls
    /// (design.md §Bootstrap §UiShellLifecycleDriver; Requirements 8.1, 8.2, 8.3, 8.4, 8.5,
    /// 8.6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Standalone / PlayMode start.</b>
    /// <see cref="OnRuntimeStart"/> is annotated with
    /// <c>[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]</c> so Unity invokes it once per
    /// run for both Standalone builds (Requirement 8.1) and Editor PlayMode entry
    /// (Requirement 8.2). It dispatches to <see cref="StartShell"/>, which constructs a
    /// fresh <see cref="IUiShellBootstrapper"/> via the registered factory and calls
    /// <see cref="IUiShellBootstrapper.StartShell"/>.
    /// </para>
    /// <para>
    /// <b>PlayMode exit.</b> In the Editor, <see cref="OnPlayModeStateChanged"/> reacts to
    /// <see cref="PlayModeStateChange.ExitingPlayMode"/> and tears the shell down so no
    /// UIDocument, subscription, or asset handle survives back into Edit mode
    /// (Requirements 8.3, 8.5). The hook is registered from
    /// <see cref="EnsureEditorHooksRegistered"/> via <c>[InitializeOnLoadMethod]</c> after
    /// every domain reload — it is intentionally a no-op outside <see cref="UNITY_EDITOR"/>.
    /// </para>
    /// <para>
    /// <b>Standalone quit.</b> <see cref="Application.quitting"/> is wired during the first
    /// successful <see cref="StartShell"/> call so a Standalone process disposes the shell
    /// before the runtime tears down (Requirement 8.3 in the Standalone branch).
    /// </para>
    /// <para>
    /// <b>Edit-mode dormancy.</b> The driver never starts the shell in Edit mode
    /// (Requirement 8.5). The PlayMode-state hook also rejects spurious
    /// <see cref="PlayModeStateChange.EnteredEditMode"/> as a defensive teardown only —
    /// it never starts the shell from an editor transition.
    /// </para>
    /// <para>
    /// <b>No cross-domain state.</b> All fields are plain <c>private static</c>, which Unity
    /// resets on every domain reload (Requirement 8.6). Nothing is persisted via
    /// <see cref="ScriptableSingleton{T}"/>, <see cref="UnityEditor.SessionState"/>, or
    /// serialized assets.
    /// </para>
    /// <para>
    /// <b>Configuration seam.</b> The driver does not know how to build a
    /// <see cref="UiShellConfig"/> on its own — the host application registers a provider via
    /// <see cref="Configure"/> from its own <c>[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]</c>
    /// or static initializer. If no provider is registered when <see cref="StartShell"/>
    /// runs, the call is a no-op (Edit-mode-style dormancy) so test runners and headless
    /// builds that do not opt into the shell are unaffected.
    /// </para>
    /// </remarks>
    public static class UiShellLifecycleDriver
    {
        private static Func<UiShellConfig>? _configProvider;
        private static Func<IUiShellBootstrapper>? _bootstrapperFactory;
        private static Func<IDiagnosticsLogger>? _diagnosticsLoggerFactory;
        private static IUiShellBootstrapper? _activeBootstrapper;
        private static bool _applicationQuittingHooked;
        private static bool _editorHooksRegistered;
        private static int _startInvocationCount;
        private static int _stopInvocationCount;

        /// <summary>
        /// True while the driver holds a running <see cref="IUiShellBootstrapper"/>.
        /// </summary>
        public static bool IsRunning => _activeBootstrapper != null && _activeBootstrapper.IsRunning;

        /// <summary>
        /// The bootstrapper instance most recently started by the driver, or <c>null</c> when
        /// the driver is dormant. Tests use this to assert subsystem state after a simulated
        /// PlayMode entry.
        /// </summary>
        public static IUiShellBootstrapper? Current => _activeBootstrapper;

        /// <summary>
        /// Cumulative count of <see cref="StartShell"/> invocations since the most recent
        /// domain reload. Driven by tests asserting "PlayMode Start/Stop を 5 回繰り返しても
        /// リーク兆候なし" — paired with <see cref="StopInvocationCount"/> the test can
        /// confirm the StartShell ↔ StopShell pairing is balanced.
        /// </summary>
        public static int StartInvocationCount => _startInvocationCount;

        /// <summary>
        /// Cumulative count of <see cref="StopShell"/> invocations since the most recent
        /// domain reload (counts every call, including no-op stops issued before any start).
        /// </summary>
        public static int StopInvocationCount => _stopInvocationCount;

        /// <summary>
        /// Registers the host-supplied factories that build the runtime <see cref="UiShellConfig"/>
        /// and (optionally) a custom <see cref="IUiShellBootstrapper"/> + diagnostics logger.
        /// </summary>
        /// <param name="configProvider">
        /// Required. Invoked on every <see cref="StartShell"/> so the host can supply a fresh
        /// config (e.g. with a per-PlayMode <c>FakeIpcBus</c> for tests).
        /// </param>
        /// <param name="bootstrapperFactory">
        /// Optional. Defaults to <c>() =&gt; new UiShellBootstrapper()</c>. Tests inject a fake
        /// to observe Start/Stop ordering without touching real Addressables / IPC layers.
        /// </param>
        /// <param name="diagnosticsLoggerFactory">
        /// Optional. Used solely to log Start/Stop/dormancy decisions emitted by the driver
        /// itself. Defaults to a fresh <see cref="DiagnosticsLogger"/>.
        /// </param>
        public static void Configure(
            Func<UiShellConfig> configProvider,
            Func<IUiShellBootstrapper>? bootstrapperFactory = null,
            Func<IDiagnosticsLogger>? diagnosticsLoggerFactory = null)
        {
            _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
            _bootstrapperFactory = bootstrapperFactory;
            _diagnosticsLoggerFactory = diagnosticsLoggerFactory;
        }

        /// <summary>
        /// Removes any provider registration and tears down the active bootstrapper. Used by
        /// tests to make every fixture independent of leftover state from a previous test (or
        /// from a previous PlayMode session, since the driver is static).
        /// </summary>
        public static void ResetForTests()
        {
            StopShellInternal(reason: "ResetForTests");
            _configProvider = null;
            _bootstrapperFactory = null;
            _diagnosticsLoggerFactory = null;
            _startInvocationCount = 0;
            _stopInvocationCount = 0;
        }

        // ----- Unity entry points -----------------------------------------

        /// <summary>
        /// Unity invokes this once per Standalone run and once per PlayMode entry through the
        /// <see cref="RuntimeInitializeOnLoadMethod"/> attribute. Tests should never call it
        /// directly — they exercise <see cref="StartShell"/> instead.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void OnRuntimeStart()
        {
#if UNITY_EDITOR
            // RuntimeInitializeOnLoadMethod also fires during PlayMode-test runs in Edit mode
            // workflows; the Editor-side guard below short-circuits the dormant case where
            // Unity is *not* about to enter PlayMode (Requirement 8.5).
            if (!Application.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }
#endif
            StartShell();
        }

#if UNITY_EDITOR
        /// <summary>
        /// Editor-only registration of the <see cref="EditorApplication.playModeStateChanged"/>
        /// hook. Re-registered on every domain reload via
        /// <c>[InitializeOnLoadMethod]</c>; the inner unsubscribe-then-subscribe avoids
        /// double-fire when the assembly is recompiled mid-session.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void EnsureEditorHooksRegistered()
        {
            if (_editorHooksRegistered) return;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            _editorHooksRegistered = true;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            switch (change)
            {
                case PlayModeStateChange.ExitingPlayMode:
                    StopShellInternal(reason: "PlayModeStateChange.ExitingPlayMode");
                    ClearHostRegistration();
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    // Defensive teardown — should already be a no-op because ExitingPlayMode
                    // tore the shell down. Required to honour Requirement 8.5 even if the
                    // ExitingPlayMode hook missed.
                    StopShellInternal(reason: "PlayModeStateChange.EnteredEditMode");
                    ClearHostRegistration();
                    break;
            }
        }

        /// <summary>
        /// Drops the host-supplied factory registration when leaving PlayMode. Under the
        /// normal "Reload Domain" setting these statics are cleared by the domain reload
        /// (Requirement 8.6); but when Enter Play Mode Options has <c>DisableDomainReload</c>
        /// enabled, the statics survive across PlayMode sessions. Without this clear, the next
        /// PlayMode entry's <see cref="OnRuntimeStart"/> (BeforeSceneLoad) would call
        /// <see cref="StartShell"/> with the <em>previous</em> session's config provider —
        /// which captures a now-disposed IPC bus — and start a stale shell before the host
        /// (e.g. IntegratedDemoBootstrap) re-registers a fresh provider. Clearing here makes
        /// the driver dormant again so the next run reconfigures cleanly. Invocation counts are
        /// intentionally left untouched (diagnostic, not lifecycle).
        /// </summary>
        private static void ClearHostRegistration()
        {
            _configProvider = null;
            _bootstrapperFactory = null;
            _diagnosticsLoggerFactory = null;
        }

        /// <summary>
        /// Test seam that simulates the Editor PlayMode-state callback without requiring an
        /// actual PlayMode transition. Production code never calls this.
        /// </summary>
        public static void SimulatePlayModeStateChangeForTests(PlayModeStateChange change)
            => OnPlayModeStateChanged(change);
#endif

        // ----- Public Start / Stop ---------------------------------------

        /// <summary>
        /// Starts the shell using the registered <see cref="Configure"/> factories. Idempotent:
        /// if a bootstrapper is already running the call is a no-op. If no provider is
        /// registered the call is also a no-op — the driver remains dormant so tests / headless
        /// runs that do not opt into the shell are unaffected.
        /// </summary>
        public static void StartShell()
        {
            _startInvocationCount++;

            if (IsRunning)
            {
                return;
            }

            var configProvider = _configProvider;
            if (configProvider == null)
            {
                // Dormant — no host has wired the driver yet. Silent return so unrelated
                // PlayMode tests / batch jobs never get an unexpected shell.
                return;
            }

            UiShellConfig config;
            try
            {
                config = configProvider();
            }
            catch (Exception ex)
            {
                ResolveDriverLogger().Log(LogLevel.Error, LogCategory.Lifecycle,
                    $"UiShellLifecycleDriver: config provider threw — shell will remain dormant: {ex.Message}", ex);
                return;
            }
            if (config == null)
            {
                ResolveDriverLogger().Log(LogLevel.Warning, LogCategory.Lifecycle,
                    "UiShellLifecycleDriver: config provider returned null — shell will remain dormant.");
                return;
            }

            var bootstrapperFactory = _bootstrapperFactory ?? (() => new UiShellBootstrapper());
            IUiShellBootstrapper bootstrapper;
            try
            {
                bootstrapper = bootstrapperFactory();
            }
            catch (Exception ex)
            {
                ResolveDriverLogger().Log(LogLevel.Error, LogCategory.Lifecycle,
                    $"UiShellLifecycleDriver: bootstrapper factory threw — shell will remain dormant: {ex.Message}", ex);
                return;
            }

            BootstrapResult result;
            try
            {
                result = bootstrapper.StartShell(config);
            }
            catch (Exception ex)
            {
                ResolveDriverLogger().Log(LogLevel.Error, LogCategory.Lifecycle,
                    $"UiShellLifecycleDriver: bootstrapper.StartShell threw: {ex.Message}", ex);
                TryDisposeBootstrapper(bootstrapper);
                return;
            }

            if (!result.Success)
            {
                ResolveDriverLogger().Log(LogLevel.Error, LogCategory.Lifecycle,
                    $"UiShellLifecycleDriver: StartShell failed: {result.Error} {result.Detail}");
                TryDisposeBootstrapper(bootstrapper);
                return;
            }

            _activeBootstrapper = bootstrapper;
            HookApplicationQuittingIfNeeded();
        }

        /// <summary>
        /// Stops any active shell and clears the cached bootstrapper. Safe to call repeatedly.
        /// </summary>
        public static void StopShell()
        {
            StopShellInternal(reason: "StopShell()");
        }

        // ----- internals --------------------------------------------------

        private static void StopShellInternal(string reason)
        {
            _stopInvocationCount++;

            var bootstrapper = _activeBootstrapper;
            if (bootstrapper == null) return;

            _activeBootstrapper = null;
            try
            {
                bootstrapper.StopShell();
            }
            catch (Exception ex)
            {
                ResolveDriverLogger().Log(LogLevel.Warning, LogCategory.Lifecycle,
                    $"UiShellLifecycleDriver: StopShell ({reason}) threw: {ex.Message}", ex);
            }
            TryDisposeBootstrapper(bootstrapper);
        }

        private static void HookApplicationQuittingIfNeeded()
        {
            if (_applicationQuittingHooked) return;
            Application.quitting += OnApplicationQuitting;
            _applicationQuittingHooked = true;
        }

        private static void OnApplicationQuitting()
        {
            StopShellInternal(reason: "Application.quitting");
        }

        private static void TryDisposeBootstrapper(IUiShellBootstrapper bootstrapper)
        {
            if (bootstrapper is IDisposable disposable)
            {
                try { disposable.Dispose(); }
                catch (Exception ex)
                {
                    ResolveDriverLogger().Log(LogLevel.Warning, LogCategory.Lifecycle,
                        $"UiShellLifecycleDriver: bootstrapper.Dispose threw: {ex.Message}", ex);
                }
            }
        }

        private static IDiagnosticsLogger ResolveDriverLogger()
        {
            var factory = _diagnosticsLoggerFactory;
            if (factory != null)
            {
                try { return factory(); }
                catch
                {
                    // Fall through to the default below.
                }
            }
            return new DiagnosticsLogger { MinimumLevel = LogLevel.Info };
        }
    }
}
