#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine.UIElements;
using VTuberSystemBase.CoreIpc.Abstractions;
using VTuberSystemBase.UiToolkitShell.AssetLoading;
using VTuberSystemBase.UiToolkitShell.Commands;
using VTuberSystemBase.UiToolkitShell.Diagnostics;
using VTuberSystemBase.UiToolkitShell.FailsafeAndConnection;
using VTuberSystemBase.UiToolkitShell.Panels;
using VTuberSystemBase.UiToolkitShell.Skin;
using LogLevel = VTuberSystemBase.UiToolkitShell.Diagnostics.LogLevel;
using LogCategory = VTuberSystemBase.UiToolkitShell.Diagnostics.LogCategory;
using StyleSheet = UnityEngine.UIElements.StyleSheet;

namespace VTuberSystemBase.UiToolkitShell.Bootstrap
{
    /// <summary>
    /// Composition root of the UI Toolkit Shell (design.md §Bootstrap §UiShellBootstrapper;
    /// Requirements 1.1, 1.4, 3.1, 5.1, 8.1, 8.2, 9.1, 9.7). Builds every shell subsystem in
    /// the order recorded by <see cref="BootstrapStep"/> and disposes them in reverse on
    /// <see cref="StopShell"/>. Failures surface as <see cref="BootstrapErrorCode"/> values
    /// instead of exceptions so the lifecycle driver can abort startup safely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Initialisation order.</b> The class walks the design-defined sequence:
    /// (1) validate the supplied <see cref="UiShellConfig"/>, (2) prepare the diagnostics
    /// logger, (3) invoke the optional CommonUi registration callback, (4) materialise the
    /// shared <see cref="PanelSettings"/> + root UIDocument via <see cref="IRootUiDocumentFactory"/>,
    /// (5) build the empty <see cref="TabPanelRegistry"/>, (6) call the supplied
    /// <see cref="ITabMountStrategy"/> to attach the three tab UIDocuments, (7) construct
    /// <see cref="TabBarController"/>, (8) run <see cref="SkinValidator"/> against the
    /// post-mount tree, (9) construct the asset loader, (10) initialise Addressables,
    /// (11) construct the IPC facades (<see cref="ConnectionStatus"/>,
    /// <see cref="UiCommandClient"/>, <see cref="UiSubscriptionClient"/>), (12) construct
    /// the notification controllers (<see cref="NotificationBarController"/> +
    /// <see cref="MainOutputStatusWatcher"/>), and (13) record that the IPC connection
    /// attempt was kicked off. The IPC connection is treated as "attempted" rather than
    /// "completed" so an unconnected bus never blocks the shell from coming up
    /// (Requirement 9.1).
    /// </para>
    /// <para>
    /// <b>Reverse-order disposal.</b> <see cref="StopShell"/> walks the stack of <see cref="IDisposable"/>
    /// records the bootstrap accumulated and disposes them in LIFO order so subsystems with
    /// inbound dependencies (e.g. <see cref="MainOutputStatusWatcher"/> consuming
    /// <see cref="UiSubscriptionClient"/>) are torn down before their dependencies. Each
    /// <see cref="IDisposable.Dispose"/> is wrapped in try/catch so a misbehaving subsystem
    /// can't strand the rest of the stack.
    /// </para>
    /// <para>
    /// <b>Re-entrancy.</b> <see cref="StartShell"/> is idempotent — calling it while the
    /// shell is already running is a no-op that returns <see cref="BootstrapResult.Ok"/>.
    /// This matches the design contract that the lifecycle driver is the single owner of
    /// Start/Stop transitions.
    /// </para>
    /// </remarks>
    public sealed class UiShellBootstrapper : IUiShellBootstrapper, IDisposable
    {
        private readonly IRootUiDocumentFactory _rootUiDocumentFactory;
        private readonly Stack<DisposalRecord> _disposalStack = new Stack<DisposalRecord>();
        private readonly List<BootstrapStep> _steps = new List<BootstrapStep>();

        private bool _running;

        public UiShellBootstrapper()
            : this(new DefaultRootUiDocumentFactory())
        {
        }

        public UiShellBootstrapper(IRootUiDocumentFactory rootUiDocumentFactory)
        {
            _rootUiDocumentFactory = rootUiDocumentFactory
                ?? throw new ArgumentNullException(nameof(rootUiDocumentFactory));
        }

        public bool IsRunning => _running;

        public IReadOnlyList<BootstrapStep> InitializationSteps => _steps;

        // ---- Subsystem accessors (used by tests and the lifecycle driver) ------

        public IDiagnosticsLogger? DiagnosticsLogger { get; private set; }
        public PanelSettings? PanelSettings { get; private set; }
        public VisualElement? RootVisualElement { get; private set; }
        public TabPanelRegistry? TabPanelRegistry { get; private set; }
        public TabBarController? TabBarController { get; private set; }
        public AddressablesAssetLoader? AssetLoader { get; private set; }
        public ConnectionStatus? ConnectionStatus { get; private set; }
        public UiCommandClient? CommandClient { get; private set; }
        public UiSubscriptionClient? SubscriptionClient { get; private set; }
        public NotificationBarController? NotificationBar { get; private set; }
        public MainOutputStatusWatcher? OutputStatusWatcher { get; private set; }
        public SkinValidationReport? SkinValidationReport { get; private set; }

        /// <summary>
        /// The optional clear-only presenter camera handle created via
        /// <see cref="UiShellConfig.PresenterCameraFactory"/> for the current run, or <c>null</c>
        /// when no factory was supplied (or it declined to create one — e.g. Edit mode). Disposed
        /// on <see cref="StopShell"/> alongside the rest of the subsystem stack.
        /// </summary>
        public IDisposable? PresenterCameraHandle { get; private set; }

        /// <summary>
        /// The <see cref="IDisplayAssignmentStrategy"/> applied during the most recent
        /// <see cref="StartShell"/> (Requirement 1.6, design.md §UiShellBootstrapper.DisplayAssignmentHook).
        /// Surfaces <see cref="FixedDisplayZeroStrategy.Instance"/> when the config omits one.
        /// </summary>
        public IDisplayAssignmentStrategy? DisplayAssignmentStrategy { get; private set; }

        /// <summary>
        /// The target display the bootstrapper resolved through
        /// <see cref="DisplayAssignmentStrategy"/> for the current run. Tests assert this
        /// to confirm a swapped-in strategy actually overrides the default Display 0 pin
        /// (Requirement 1.6).
        /// </summary>
        public int? EffectiveTargetDisplay { get; private set; }

        // ---- StartShell --------------------------------------------------------

        public BootstrapResult StartShell(UiShellConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (_running) return BootstrapResult.Ok();

            _steps.Clear();

            var validationError = ValidateConfig(config);
            if (validationError.HasValue)
            {
                return BootstrapResult.Fail(validationError.Value, "UiShellConfig validation failed");
            }
            _steps.Add(BootstrapStep.ConfigValidated);

            var skinProfile = config.SkinProfile!;
            var bus = config.IpcBus!;
            var tabMount = config.TabMountStrategy!;

            var logger = config.DiagnosticsLogger ?? CreateDefaultLogger(config.MinimumLogLevel);
            DiagnosticsLogger = logger;
            _steps.Add(BootstrapStep.DiagnosticsLoggerReady);

            try
            {
                config.CommonUiRegistrationCallback?.Invoke();
            }
            catch (Exception ex)
            {
                logger.Log(LogLevel.Warning, LogCategory.Lifecycle,
                    $"CommonUiRegistrationCallback threw: {ex.Message}", ex);
            }
            _steps.Add(BootstrapStep.CommonUiRegistered);

            // ---- Display assignment hook (Requirement 1.6) ----------------
            // The strategy is the authoritative source for the final targetDisplay so the
            // future runtime-display-selector-integration spec (#7) can swap in display
            // selection without re-shaping the bootstrap. The default
            // FixedDisplayZeroStrategy keeps the "Display 1 only" guarantee for now.
            var displayStrategy = config.DisplayAssignmentStrategy ?? FixedDisplayZeroStrategy.Instance;
            DisplayAssignmentStrategy = displayStrategy;
            int effectiveDisplay;
            try
            {
                effectiveDisplay = displayStrategy.ResolveTargetDisplay(config.RequestedTargetDisplay);
            }
            catch (Exception ex)
            {
                logger.Log(LogLevel.Warning, LogCategory.Lifecycle,
                    $"IDisplayAssignmentStrategy.ResolveTargetDisplay threw; falling back to Display 0: {ex.Message}", ex);
                effectiveDisplay = 0;
            }
            EffectiveTargetDisplay = effectiveDisplay;

            // ---- Root UIDocument ------------------------------------------
            RootUiDocumentArtifacts rootArtifacts;
            try
            {
                rootArtifacts = _rootUiDocumentFactory.Create(skinProfile, effectiveDisplay, logger);
            }
            catch (Exception ex)
            {
                logger.Log(LogLevel.Error, LogCategory.Lifecycle,
                    $"Root UIDocument creation failed: {ex.Message}", ex);
                Rollback();
                return BootstrapResult.Fail(BootstrapErrorCode.PanelSettingsAssignFailed,
                    $"RootUIDocument creation threw: {ex.Message}");
            }
            PanelSettings = rootArtifacts.PanelSettings;
            RootVisualElement = rootArtifacts.RootVisualElement;
            PushDisposable("RootUiDocumentArtifacts", rootArtifacts);
            _steps.Add(BootstrapStep.PanelSettingsCreated);
            _steps.Add(BootstrapStep.RootUiDocumentBuilt);

            // ---- Operator UI presenter camera (optional, Display-clear surface) ----
            // The UI Toolkit overlay panel composites directly onto the target display without a
            // camera; when no camera clears that display each frame the overlay smears over stale
            // buffer contents. An opt-in factory (config.PresenterCameraFactory, default null)
            // places a clear-only camera on the resolved display to stabilise that surface. It is
            // never on the fatal path — a missing or throwing factory just leaves the surface
            // un-managed, which is the pre-existing behaviour.
            if (config.PresenterCameraFactory != null)
            {
                try
                {
                    var presenterHandle = config.PresenterCameraFactory.Create(effectiveDisplay, logger);
                    if (presenterHandle != null)
                    {
                        PresenterCameraHandle = presenterHandle;
                        PushDisposable("OperatorUiPresenterCamera", presenterHandle);
                    }
                }
                catch (Exception ex)
                {
                    logger.Log(LogLevel.Warning, LogCategory.Lifecycle,
                        $"PresenterCameraFactory.Create threw; shell continues without a presenter camera: {ex.Message}", ex);
                }
            }

            // ---- TabPanelRegistry (must exist before mounting) -----------
            var registry = new TabPanelRegistry(logger);
            TabPanelRegistry = registry;
            _steps.Add(BootstrapStep.TabPanelRegistryReady);

            // ---- Mount the 3 tab UIDocuments -----------------------------
            var mountContext = new TabMountContext(
                registry,
                rootArtifacts.PanelSettings,
                skinProfile,
                rootArtifacts.RootVisualElement,
                logger);
            try
            {
                var mounted = tabMount.MountTabs(mountContext);
                if (!mounted)
                {
                    Rollback();
                    return BootstrapResult.Fail(BootstrapErrorCode.TabUxmlAttachFailed,
                        "ITabMountStrategy.MountTabs returned false");
                }
            }
            catch (Exception ex)
            {
                logger.Log(LogLevel.Error, LogCategory.Lifecycle,
                    $"Tab mount strategy threw: {ex.Message}", ex);
                Rollback();
                return BootstrapResult.Fail(BootstrapErrorCode.TabUxmlAttachFailed,
                    $"ITabMountStrategy.MountTabs threw: {ex.Message}");
            }
            _steps.Add(BootstrapStep.TabUiDocumentsMounted);

            // ---- Apply skin USS to root + bound tab roots (task 11.1) ----
            // RootStyleSheets are applied first, then CommonUiStyleSheets so that user-
            // supplied common UI overrides win over the package defaults (Requirement 6.3,
            // 6.4 — "後ろほど優先" ordering contract). Per-tab StyleSheets are applied to
            // the corresponding bound tab root in declaration order. Style-sheet
            // application is best-effort: a skin profile that omits any of the lists is
            // valid (the bootstrapper still boots), so missing collections short-circuit
            // without raising errors.
            try
            {
                ApplySkinStyleSheets(rootArtifacts.RootVisualElement, registry, skinProfile, logger);
            }
            catch (Exception ex)
            {
                logger.Log(LogLevel.Warning, LogCategory.Skin,
                    $"ApplySkinStyleSheets threw; continuing without full USS application: {ex.Message}", ex);
            }

            // ---- TabBarController ----------------------------------------
            try
            {
                TabBarController = new TabBarController(
                    registry,
                    rootArtifacts.RootVisualElement,
                    logger,
                    config.InitialTab);
                PushDisposable("TabBarController", TabBarController);
            }
            catch (Exception ex)
            {
                logger.Log(LogLevel.Error, LogCategory.Lifecycle,
                    $"TabBarController construction failed: {ex.Message}", ex);
                Rollback();
                return BootstrapResult.Fail(BootstrapErrorCode.TabUxmlAttachFailed,
                    $"TabBarController construction threw: {ex.Message}");
            }
            _steps.Add(BootstrapStep.TabBarControllerReady);

            // ---- SkinValidator -------------------------------------------
            try
            {
                var validator = new SkinValidator(logger);
                var tabRoots = ResolveTabRoots(registry);
                SkinValidationReport = validator.Validate(rootArtifacts.RootVisualElement, tabRoots);
                if (!SkinValidationReport.Value.AllValid)
                {
                    foreach (var issue in SkinValidationReport.Value.Issues)
                    {
                        if (issue.TabId.HasValue)
                        {
                            // SkinValidator runs after NotifyTabMounted, so the tab is
                            // already in Mounted state. Use the skin-validation-specific
                            // entry point that explicitly allows Mounted -> Failed
                            // downgrade (Requirement 6.6, task 11.1).
                            registry.MarkTabFailedFromSkinValidation(issue.TabId.Value,
                                $"missing '{issue.MissingSelector}'");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Log(LogLevel.Warning, LogCategory.Skin,
                    $"SkinValidator threw: {ex.Message}", ex);
            }
            _steps.Add(BootstrapStep.SkinValidated);

            // ---- AddressablesAssetLoader ---------------------------------
            var assetLoader = new AddressablesAssetLoader(logger);
            AssetLoader = assetLoader;
            PushDisposable("AddressablesAssetLoader", assetLoader);
            _steps.Add(BootstrapStep.AssetLoaderReady);

            // ---- Addressables initialization (runs Sync via callback) ----
            // 非致命扱い（fail-safe, Requirement 9.1 の「IPC 未接続でも起動完遂」と同じ方針）:
            // Addressables の設定/コンテンツが未ビルドでも、あるいは init が失敗しても、UI タブ自体は
            // 描画できる（addressable 由来のサムネ/アバター等だけが遅延的に失敗する）。シェル全体を
            // 巻き戻すと「コンテンツ未ビルドの開発環境では UI がまったく起動しない」状態になり配信
            // 運用上致命的なので、ここでは警告ログを残して起動を継続する。
            var initializer = config.AddressablesInitializer ?? new AddressablesInitializer();
            try
            {
                var initBootstrap = new AddressablesBootstrap(initializer, logger);
                initBootstrap.Initialize(result =>
                {
                    if (!result.Success)
                    {
                        logger.Log(LogLevel.Warning, LogCategory.Lifecycle,
                            $"Addressables initialization failed ({result.Detail}); shell continues without addressable content. " +
                            "Addressable-backed assets (thumbnails/avatars) stay unavailable until content is built.");
                    }
                });
            }
            catch (Exception ex)
            {
                // Initialize 自体が同期 throw した場合も同様に非致命扱いで継続する。
                logger.Log(LogLevel.Warning, LogCategory.Lifecycle,
                    $"AddressablesBootstrap threw synchronously ({ex.Message}); shell continues without addressable content.", ex);
            }
            _steps.Add(BootstrapStep.AddressablesInitialized);

            // ---- IPC Facades --------------------------------------------
            ConnectionStatus connectionStatus;
            try
            {
                connectionStatus = new ConnectionStatus(bus);
                ConnectionStatus = connectionStatus;
                PushDisposable("ConnectionStatus", connectionStatus);
            }
            catch (Exception ex)
            {
                logger.Log(LogLevel.Error, LogCategory.Lifecycle,
                    $"ConnectionStatus construction failed: {ex.Message}", ex);
                Rollback();
                return BootstrapResult.Fail(BootstrapErrorCode.IpcAbstractionUnavailable,
                    $"ConnectionStatus threw: {ex.Message}");
            }
            _steps.Add(BootstrapStep.ConnectionStatusReady);

            CommandClient = new UiCommandClient(bus, connectionStatus, logger);
            _steps.Add(BootstrapStep.UiCommandClientReady);

            SubscriptionClient = new UiSubscriptionClient(bus, logger);
            _steps.Add(BootstrapStep.UiSubscriptionClientReady);

            // ---- NotificationBarController ------------------------------
            NotificationBar = new NotificationBarController(
                rootArtifacts.NotificationBarHost,
                connectionStatus,
                registry,
                logger);
            PushDisposable("NotificationBarController", NotificationBar);
            _steps.Add(BootstrapStep.NotificationBarControllerReady);

            // ---- MainOutputStatusWatcher --------------------------------
            try
            {
                OutputStatusWatcher = new MainOutputStatusWatcher(
                    SubscriptionClient,
                    NotificationBar,
                    logger);
                PushDisposable("MainOutputStatusWatcher", OutputStatusWatcher);
            }
            catch (Exception ex)
            {
                logger.Log(LogLevel.Warning, LogCategory.Lifecycle,
                    $"MainOutputStatusWatcher construction failed; shell continues without fallback subscription: {ex.Message}", ex);
            }
            _steps.Add(BootstrapStep.MainOutputStatusWatcherReady);

            // ---- IPC connection attempt (non-blocking) ------------------
            // The shell does not require the bus to be connected before declaring success
            // (Requirement 9.1). Recording the step is enough — actual connect work is
            // owned by the bus implementation that the host application provides.
            _steps.Add(BootstrapStep.IpcConnectionAttempted);

            _running = true;
            _steps.Add(BootstrapStep.ShellRunning);
            logger.Log(LogLevel.Info, LogCategory.Lifecycle,
                "UiShellBootstrapper: shell running.");
            return BootstrapResult.Ok();
        }

        // ---- StopShell ---------------------------------------------------

        public void StopShell()
        {
            if (!_running && _disposalStack.Count == 0) return;
            _running = false;

            // Backstop sweep — force-dispose every live ITabLifecycleHandle so
            // subscriptions tracked via Track(IDisposable) and asset scopes
            // tracked via TrackAssetScope(IAsyncAssetLoader) are released even
            // when a tab spec forgot to call Dispose. Must run BEFORE Rollback
            // so the IPC bus, UiSubscriptionClient, and AddressablesAssetLoader
            // are still alive to acknowledge the disposal
            // (Requirement 2.8, 5.7; design.md §TabPanelRegistry §Risks; task 10.4).
            if (TabPanelRegistry != null)
            {
                try
                {
                    TabPanelRegistry.DisposeAllHandles();
                }
                catch (Exception ex)
                {
                    DiagnosticsLogger?.Log(LogLevel.Warning, LogCategory.Lifecycle,
                        $"TabPanelRegistry.DisposeAllHandles threw during StopShell: {ex.Message}", ex);
                }
            }

            Rollback();
            DiagnosticsLogger?.Log(LogLevel.Info, LogCategory.Lifecycle,
                "UiShellBootstrapper: shell stopped.");
        }

        public void Dispose() => StopShell();

        // ---- helpers -----------------------------------------------------

        private void Rollback()
        {
            while (_disposalStack.Count > 0)
            {
                var record = _disposalStack.Pop();
                try
                {
                    record.Disposable.Dispose();
                }
                catch (Exception ex)
                {
                    DiagnosticsLogger?.Log(LogLevel.Warning, LogCategory.Lifecycle,
                        $"Disposal failed for {record.Name}: {ex.Message}", ex);
                }
            }

            TabBarController = null;
            TabPanelRegistry = null;
            AssetLoader = null;
            ConnectionStatus = null;
            CommandClient = null;
            SubscriptionClient = null;
            NotificationBar = null;
            OutputStatusWatcher = null;
            PanelSettings = null;
            RootVisualElement = null;
            SkinValidationReport = null;
            PresenterCameraHandle = null;
            DisplayAssignmentStrategy = null;
            EffectiveTargetDisplay = null;
        }

        private void PushDisposable(string name, IDisposable disposable)
        {
            _disposalStack.Push(new DisposalRecord(name, disposable));
        }

        private static IDiagnosticsLogger CreateDefaultLogger(LogLevel minimumLevel)
        {
            return new DiagnosticsLogger { MinimumLevel = minimumLevel };
        }

        private static IReadOnlyDictionary<TabId, VisualElement> ResolveTabRoots(TabPanelRegistry registry)
        {
            // Tab roots are tracked inside the registry via NotifyTabMounted(VisualElement).
            // The registry exposes a defensive copy through SnapshotTabRoots so the
            // SkinValidator can walk the per-tab subtrees and surface missing required
            // selectors (Requirement 6.5, 6.6; task 11.1). Strategies that bypass element
            // binding (older tests / synthetic mounts) cause the snapshot to be empty,
            // in which case the validator only inspects the root panel.
            return registry.SnapshotTabRoots();
        }

        private static void ApplySkinStyleSheets(
            VisualElement rootVisualElement,
            TabPanelRegistry registry,
            UiToolkitShellSkinProfile skinProfile,
            IDiagnosticsLogger logger)
        {
            if (rootVisualElement == null || skinProfile == null) return;

            // Root sheets — package-shipped defaults first, then user "additional USS"
            // through CommonUiStyleSheets. Later sheets override earlier ones so the
            // user's overrides take precedence (design.md §UiToolkitShellSkinProfile).
            ApplyStyleSheets(rootVisualElement, skinProfile.RootStyleSheets, logger,
                "skin.RootStyleSheets[root]");
            ApplyStyleSheets(rootVisualElement, skinProfile.CommonUiStyleSheets, logger,
                "skin.CommonUiStyleSheets[root]");

            // Per-tab sheets — only applied to bound tab roots. Tabs that haven't yet
            // been bound (e.g. failed mount) are skipped silently because there is no
            // VisualElement to attach the sheets to.
            var snapshot = registry.SnapshotTabRoots();
            ApplyTabStyleSheets(snapshot, TabId.Character,
                skinProfile.CharacterTabStyleSheets, logger);
            ApplyTabStyleSheets(snapshot, TabId.StageLighting,
                skinProfile.StageLightingTabStyleSheets, logger);
            ApplyTabStyleSheets(snapshot, TabId.CameraSwitcher,
                skinProfile.CameraSwitcherTabStyleSheets, logger);
        }

        private static void ApplyTabStyleSheets(
            IReadOnlyDictionary<TabId, VisualElement> tabRoots,
            TabId tabId,
            List<StyleSheet>? sheets,
            IDiagnosticsLogger logger)
        {
            if (sheets == null || sheets.Count == 0) return;
            if (!tabRoots.TryGetValue(tabId, out var tabRoot) || tabRoot == null) return;
            ApplyStyleSheets(tabRoot, sheets, logger, $"skin.{tabId}TabStyleSheets[tab/{tabId}]");
        }

        private static void ApplyStyleSheets(
            VisualElement target,
            List<StyleSheet>? sheets,
            IDiagnosticsLogger logger,
            string scopeLabel)
        {
            if (sheets == null || sheets.Count == 0) return;
            for (var i = 0; i < sheets.Count; i++)
            {
                var sheet = sheets[i];
                if (sheet == null) continue;
                target.styleSheets.Add(sheet);
            }
            logger.Log(LogLevel.Debug, LogCategory.Skin,
                $"Applied {sheets.Count} StyleSheet(s) to {scopeLabel}.");
        }

        private readonly struct DisposalRecord
        {
            public DisposalRecord(string name, IDisposable disposable)
            {
                Name = name;
                Disposable = disposable;
            }

            public string Name { get; }
            public IDisposable Disposable { get; }
        }

        private static BootstrapErrorCode? ValidateConfig(UiShellConfig config)
        {
            var skinError = UiToolkitShellSkinProfile.Validate(config.SkinProfile);
            if (skinError.HasValue) return skinError.Value;
            if (config.IpcBus == null) return BootstrapErrorCode.IpcAbstractionUnavailable;
            if (config.TabMountStrategy == null) return BootstrapErrorCode.TabUxmlAttachFailed;
            return null;
        }
    }
}
