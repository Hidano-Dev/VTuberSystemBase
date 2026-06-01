using System;
using RealtimeAvatarController.Core;
using VTuberSystemBase.OutputRendererShell.Abstractions;
using VTuberSystemBase.RacMainOutputAdapter.Defaults;
using VTuberSystemBase.RacMainOutputAdapter.Diagnostics;
using VTuberSystemBase.RacMainOutputAdapter.ExtensionPoints;
using VTuberSystemBase.RacMainOutputAdapter.Internal;
using VTuberSystemBase.RacMainOutputAdapter.Receivers;
using VTuberSystemBase.RacMainOutputAdapter.Senders;

namespace VTuberSystemBase.RacMainOutputAdapter.Bootstrapper
{
    /// <summary>
    /// 本 spec の Composition Root（POCO、MonoBehaviour 非継承）。
    /// <see cref="RacMainOutputAdapterHost"/> が PlayMode 上で本クラスのライフサイクルを駆動する
    /// （Requirement 1.4, 1.5, 1.7, 1.8, 8.5, 8.7, 8.8）。
    /// </summary>
    public sealed class RacMainOutputAdapterBootstrapper : IDisposable
    {
        private readonly RacMainOutputAdapterConfig _config;

        // OverrideServices で差し込まれる依存（null なら Initialize 時に既定実装を使う）
        private IOutputCommandDispatcher _dispatcher;
        private IOutputSceneRoots _sceneRoots;
        private IAdapterMessageSink _messageSink;
        private IAvatarKeyResolver _keyResolver;
        private IAvatarSchemaProvider _schemaProvider;
        private IAvatarSettingsAdapter _settingsAdapter;
        private IMoCapSourceConfigFactory _mocapFactory;
        private IClock _clock;
        private IDiagnosticsLogger _logger;

        // 生成済みコンポーネント
        private SlotManager _slotManager;
        private SlotStatusPublisher _statusPublisher;
        private SlotErrorTranslator _errorTranslator;
        private SlotCatalogPublisher _slotCatalogPublisher;
        private AvatarCatalogPublisher _avatarCatalogPublisher;
        private SlotAssignmentApplier _assignmentApplier;
        private SlotSettingsApplier _settingsApplier;
        private SlotCommandApplier _commandApplier;
        private AvatarSchemaResponder _schemaResponder;
        private PendingPublishQueue _pendingQueue;
        private RacMainOutputAdapterDiagnostics _diagnostics;

        private bool _initialized;
        private bool _shutdown;

        /// <summary>本 Bootstrapper を生成する。</summary>
        public RacMainOutputAdapterBootstrapper(RacMainOutputAdapterConfig config = null)
        {
            _config = config ?? new RacMainOutputAdapterConfig();
        }

        /// <summary><see cref="Initialize"/> 前にテスト用ダブルを差し込む。</summary>
        public void OverrideServices(
            IOutputCommandDispatcher dispatcher = null,
            IOutputSceneRoots sceneRoots = null,
            IAdapterMessageSink messageSink = null,
            IAvatarKeyResolver keyResolver = null,
            IAvatarSchemaProvider schemaProvider = null,
            IAvatarSettingsAdapter settingsAdapter = null,
            IMoCapSourceConfigFactory mocapFactory = null,
            IClock clock = null,
            IDiagnosticsLogger logger = null)
        {
            _dispatcher = dispatcher ?? _dispatcher;
            _sceneRoots = sceneRoots ?? _sceneRoots;
            _messageSink = messageSink ?? _messageSink;
            _keyResolver = keyResolver ?? _keyResolver;
            _schemaProvider = schemaProvider ?? _schemaProvider;
            _settingsAdapter = settingsAdapter ?? _settingsAdapter;
            _mocapFactory = mocapFactory ?? _mocapFactory;
            _clock = clock ?? _clock;
            _logger = logger ?? _logger;
        }

        /// <summary>診断スナップショット API。</summary>
        public IRacMainOutputAdapterDiagnostics Diagnostics => _diagnostics;

        /// <summary>同一 SlotManager を外部の駆動ループへ渡すための read-only 参照。</summary>
        public SlotManager SlotManager => _slotManager;

        /// <summary>初期化済みか（二重 Initialize 防止）。</summary>
        public bool IsRunning => _initialized && !_shutdown;

        /// <summary>
        /// 全構成要素を生成し、ハンドラ登録 + 購読を開始する（Flow 1）。
        /// </summary>
        public void Initialize()
        {
            if (_initialized && !_shutdown)
            {
                _logger?.Log(AdapterLogLevel.Warning, AdapterLogCategories.Bootstrap,
                    "Initialize() called twice; ignoring second call.");
                return;
            }
            _shutdown = false;
            _initialized = false;

            // Step 1: 既定実装フォールバック
            _logger ??= new UnityConsoleDiagnosticsLogger();
            _clock ??= new DefaultClock();
            _keyResolver ??= new AddressablesAvatarKeyResolver(_logger);
            _schemaProvider ??= new AddressablesAvatarSchemaProvider(_logger);
            _settingsAdapter ??= new NoOpAvatarSettingsAdapter();
            _mocapFactory ??= new StubMoCapSourceConfigFactory();

            if (_dispatcher == null)
                throw new InvalidOperationException(
                    "IOutputCommandDispatcher must be provided via OverrideServices before Initialize().");
            if (_messageSink == null)
                throw new InvalidOperationException(
                    "IAdapterMessageSink must be provided via OverrideServices before Initialize() (CoreIpcBusMessageSink in production).");

            _logger.Log(AdapterLogLevel.Info, AdapterLogCategories.Bootstrap, "Initialize start");

            // Step 2: RAC レジストリ取得
            var providerReg = RegistryLocator.ProviderRegistry;
            var sourceReg = RegistryLocator.MoCapSourceRegistry;
            var errorChannel = RegistryLocator.ErrorChannel;

            // Step 3: SlotManager 生成
            _slotManager = new SlotManager(providerReg, sourceReg, errorChannel);

            // Step 4: 送信ヘルパ
            _statusPublisher = new SlotStatusPublisher(_messageSink, _clock, _logger);
            _errorTranslator = new SlotErrorTranslator(_messageSink, errorChannel, _statusPublisher, _config, _logger);
            _pendingQueue = new PendingPublishQueue(_config.PendingPublishQueueCapacity, _logger);
            _slotCatalogPublisher = new SlotCatalogPublisher(_messageSink, _slotManager, _pendingQueue, _logger);
            _avatarCatalogPublisher = new AvatarCatalogPublisher(_messageSink, _keyResolver, _pendingQueue, _logger);

            // Step 5: 受信層
            _settingsApplier = new SlotSettingsApplier(_dispatcher, _slotManager, _settingsAdapter, _errorTranslator, _logger);
            _assignmentApplier = new SlotAssignmentApplier(
                _dispatcher, _slotManager, _keyResolver, _mocapFactory, _statusPublisher, _errorTranslator, _logger);
            _commandApplier = new SlotCommandApplier(
                _dispatcher, _slotManager, _assignmentApplier, _statusPublisher, _errorTranslator, _logger);
            _schemaResponder = new AvatarSchemaResponder(_dispatcher, _schemaProvider, _config, _settingsApplier, _logger);

            // Diagnostics（生成順序の都合で Func 経由）
            _diagnostics = new RacMainOutputAdapterDiagnostics(
                () => _dispatcher?.RegisteredHandlerCount ?? 0,
                () => _slotManager,
                () => _keyResolver,
                () => _errorTranslator);
            _diagnostics.PhaseName = "Initializing";

            // Step 6/7/8: 購読開始
            _errorTranslator.StartObserving();
            _slotCatalogPublisher.StartObserving();
            _avatarCatalogPublisher.StartObserving();

            // Slot 増減 → Applier の動的登録/解除をフック
            _slotCatalogPublisher.OnSlotAdded += OnSlotAdded;
            _slotCatalogPublisher.OnSlotRemoved += OnSlotRemoved;
            _slotCatalogPublisher.OnSlotStateChanged += OnSlotStateChanged;
            _avatarCatalogPublisher.OnAvatarAdded += OnAvatarAdded;
            _avatarCatalogPublisher.OnAvatarRemoved += OnAvatarRemoved;

            // assignment による AvatarKey 変化を SettingsApplier / SchemaResponder に伝播
            _assignmentApplier.OnAvatarKeyChanged += OnAvatarKeyChanged;

            // 既存 Slot に対して動的登録（PlayMode 開始直後で 0 件、catalog 受信時 trigger される）
            foreach (var handle in _slotManager.GetSlots())
            {
                _assignmentApplier.RegisterDynamic(handle.SlotId);
                _commandApplier.RegisterDynamic(handle.SlotId);
            }
            // 既存 Avatar に対して schema responder の動的登録
            foreach (var entry in _keyResolver.AvatarKeys)
            {
                _schemaResponder.RegisterDynamic(entry.AvatarKey);
            }

            // Step 9: Pending publish flush
            _pendingQueue.Flush(_messageSink);

            _initialized = true;
            _diagnostics.PhaseName = "Ready";
            _logger.Log(AdapterLogLevel.Info, AdapterLogCategories.Bootstrap, "Initialize complete");
        }

        /// <summary>本 Bootstrapper を解放する（Flow 7）。冪等。</summary>
        public void Shutdown()
        {
            if (_shutdown) return;
            _shutdown = true;
            _diagnostics?.PhaseName.ToString(); // ToString to avoid CS0029 with null
            if (_diagnostics != null) _diagnostics.PhaseName = "ShuttingDown";

            try { if (_assignmentApplier != null) _assignmentApplier.OnAvatarKeyChanged -= OnAvatarKeyChanged; } catch { }
            try
            {
                if (_slotCatalogPublisher != null)
                {
                    _slotCatalogPublisher.OnSlotAdded -= OnSlotAdded;
                    _slotCatalogPublisher.OnSlotRemoved -= OnSlotRemoved;
                    _slotCatalogPublisher.OnSlotStateChanged -= OnSlotStateChanged;
                }
            }
            catch { }
            try
            {
                if (_avatarCatalogPublisher != null)
                {
                    _avatarCatalogPublisher.OnAvatarAdded -= OnAvatarAdded;
                    _avatarCatalogPublisher.OnAvatarRemoved -= OnAvatarRemoved;
                }
            }
            catch { }

            // 解放（逆順）
            SafeDispose(_schemaResponder); _schemaResponder = null;
            SafeDispose(_commandApplier); _commandApplier = null;
            SafeDispose(_assignmentApplier); _assignmentApplier = null;
            SafeDispose(_settingsApplier); _settingsApplier = null;
            SafeDispose(_slotCatalogPublisher); _slotCatalogPublisher = null;
            SafeDispose(_avatarCatalogPublisher); _avatarCatalogPublisher = null;
            SafeDispose(_errorTranslator); _errorTranslator = null;

            try { _slotManager?.Dispose(); } catch (Exception ex)
            {
                _logger?.Log(AdapterLogLevel.Warning, AdapterLogCategories.Bootstrap, "SlotManager.Dispose threw.", ex);
            }
            _slotManager = null;

            _initialized = false;
            if (_diagnostics != null) _diagnostics.PhaseName = "Shutdown";
            _logger?.Log(AdapterLogLevel.Info, AdapterLogCategories.Bootstrap, "Shutdown complete");
        }

        /// <inheritdoc/>
        public void Dispose() => Shutdown();

        // === イベントハンドラ ===

        private void OnSlotAdded(string slotId)
        {
            _assignmentApplier?.RegisterDynamic(slotId);
            _commandApplier?.RegisterDynamic(slotId);
        }

        private void OnSlotRemoved(string slotId)
        {
            _assignmentApplier?.UnregisterDynamic(slotId);
            _commandApplier?.UnregisterDynamic(slotId);
        }

        private void OnSlotStateChanged(string slotId, SlotState prev, SlotState next, string avatarKey)
        {
            _settingsApplier?.OnSlotStateChanged(slotId, prev, next, avatarKey);
        }

        private void OnAvatarAdded(string avatarKey)
        {
            _schemaResponder?.RegisterDynamic(avatarKey);
        }

        private void OnAvatarRemoved(string avatarKey)
        {
            _schemaResponder?.UnregisterDynamic(avatarKey);
        }

        private void OnAvatarKeyChanged(string slotId, string oldAvatarKey, string newAvatarKey)
        {
            _settingsApplier?.OnAvatarKeyChanged(slotId, oldAvatarKey, newAvatarKey);
            if (!string.IsNullOrEmpty(newAvatarKey))
            {
                _schemaResponder?.NotifySlotActiveForAvatar(slotId, newAvatarKey);
            }
        }

        private static void SafeDispose(IDisposable d)
        {
            try { d?.Dispose(); } catch { }
        }
    }
}
