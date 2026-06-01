using System;
using UnityEngine;
using VTuberSystemBase.AvatarMocapFacialIntegration.Catalog;
using VTuberSystemBase.AvatarMocapFacialIntegration.Mocap;
using VTuberSystemBase.AvatarMocapFacialIntegration.Resolution;
using VTuberSystemBase.CoreIpc.Abstractions;
using VTuberSystemBase.OutputRendererShell.Abstractions;
using VTuberSystemBase.OutputRendererShell.Scene;
using VTuberSystemBase.RacMainOutputAdapter.Bootstrapper;
using VTuberSystemBase.RacMainOutputAdapter.Diagnostics;
using VTuberSystemBase.RacMainOutputAdapter.Drivers;
using VTuberSystemBase.RacMainOutputAdapter.Internal;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace VTuberSystemBase.AvatarMocapFacialIntegration.Composition
{
    /// <summary>
    /// AMFI runtime composition root for avatar catalog resolution, VMC source configuration,
    /// RAC adapter startup, and SlotMotionDriver wiring.
    /// </summary>
    [DefaultExecutionOrder(110)]
    [DisallowMultipleComponent]
    public sealed class AmfiCompositionRoot : MonoBehaviour
    {
        [Header("AMFI")]
        [SerializeField] private AvatarCatalog _avatarCatalog;
        [SerializeField] private SlotMotionDriver _slotMotionDriver;

        [Header("Output Renderer Shell")]
        [SerializeField] private OutputSceneBootstrapper _outputSceneBootstrapper;

        [Header("IPC Bus Provider")]
        [SerializeField] private MonoBehaviour _coreIpcBusProviderBehaviour;

        [Header("Lifecycle")]
        [SerializeField] private bool _autoStart = true;

        [Header("Diagnostics")]
        [SerializeField] private AdapterLogLevel _minLogLevel = AdapterLogLevel.Info;

        private IOutputCommandDispatcher _injectedDispatcher;
        private IOutputSceneRoots _injectedSceneRoots;
        private IAdapterMessageSink _injectedMessageSink;
        private IDiagnosticsLogger _injectedLogger;
        private SlotMotionDriver _injectedDriver;

        private RacMainOutputAdapterBootstrapper _bootstrapper;
        private bool _started;

        public RacMainOutputAdapterBootstrapper Bootstrapper => _bootstrapper;
        public SlotMotionDriver Driver => _slotMotionDriver;
        public bool IsRunning => _bootstrapper?.IsRunning == true;

        public void OverrideServices(
            IOutputCommandDispatcher dispatcher = null,
            IOutputSceneRoots sceneRoots = null,
            IAdapterMessageSink messageSink = null,
            IDiagnosticsLogger logger = null,
            SlotMotionDriver slotMotionDriver = null)
        {
            _injectedDispatcher = dispatcher ?? _injectedDispatcher;
            _injectedSceneRoots = sceneRoots ?? _injectedSceneRoots;
            _injectedMessageSink = messageSink ?? _injectedMessageSink;
            _injectedLogger = logger ?? _injectedLogger;
            _injectedDriver = slotMotionDriver ?? _injectedDriver;
        }

        private void OnEnable()
        {
#if UNITY_EDITOR
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
#endif
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
#endif
        }

        private void Start()
        {
            if (!_autoStart) return;
            if (!Application.isPlaying) return;

            Initialize();
        }

        public void Initialize()
        {
            if (_started && IsRunning) return;

            var logger = _injectedLogger ?? new UnityConsoleDiagnosticsLogger { MinimumLevel = _minLogLevel };
            var dispatcher = _injectedDispatcher ?? ResolveDispatcher();
            var messageSink = _injectedMessageSink ?? ResolveMessageSink();

            if (dispatcher == null)
            {
                logger.Log(
                    AdapterLogLevel.Warning,
                    AdapterLogCategories.Bootstrap,
                    "AmfiCompositionRoot Initialize aborted because no IOutputCommandDispatcher is available.");
                return;
            }

            if (messageSink == null)
            {
                logger.Log(
                    AdapterLogLevel.Warning,
                    AdapterLogCategories.Bootstrap,
                    "AmfiCompositionRoot Initialize aborted because no IAdapterMessageSink is available.");
                return;
            }

            var keyResolver = new CatalogAvatarKeyResolver(_avatarCatalog, logger);
            var schemaProvider = new InMemoryAvatarSchemaProvider(_avatarCatalog);
            var mocapFactory = new VmcMoCapSourceConfigFactory();

            _bootstrapper = new RacMainOutputAdapterBootstrapper();
            _bootstrapper.OverrideServices(
                dispatcher: dispatcher,
                sceneRoots: _injectedSceneRoots ?? ResolveSceneRoots(),
                messageSink: messageSink,
                keyResolver: keyResolver,
                schemaProvider: schemaProvider,
                mocapFactory: mocapFactory,
                logger: logger);
            _bootstrapper.Initialize();

            var slotManager = _bootstrapper.SlotManager;
            if (slotManager == null)
            {
                logger.Log(
                    AdapterLogLevel.Warning,
                    AdapterLogCategories.Bootstrap,
                    "AmfiCompositionRoot initialized RAC adapter but SlotManager was null; SlotMotionDriver was not attached.");
                return;
            }

            EnsureDriver().Attach(slotManager);
            _started = true;
        }

        public void Shutdown()
        {
            try
            {
                _slotMotionDriver?.Detach();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AmfiCompositionRoot] SlotMotionDriver.Detach threw: {ex}");
            }

            try
            {
                _bootstrapper?.Shutdown();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AmfiCompositionRoot] Bootstrapper.Shutdown threw: {ex}");
            }
            finally
            {
                _bootstrapper = null;
                _started = false;
            }
        }

        private void OnDestroy()
        {
            Shutdown();
        }

        private IOutputCommandDispatcher ResolveDispatcher()
        {
            if (_outputSceneBootstrapper == null)
            {
                _outputSceneBootstrapper = FindAnyObjectByType<OutputSceneBootstrapper>();
            }

            return _outputSceneBootstrapper?.Dispatcher;
        }

        private IOutputSceneRoots ResolveSceneRoots()
        {
            if (_outputSceneBootstrapper == null)
            {
                _outputSceneBootstrapper = FindAnyObjectByType<OutputSceneBootstrapper>();
            }

            return _outputSceneBootstrapper?.Roots;
        }

        private IAdapterMessageSink ResolveMessageSink()
        {
            var bus = ResolveBus();
            return bus == null ? null : new CoreIpcBusMessageSink(bus);
        }

        private ICoreIpcBus ResolveBus()
        {
            if (_coreIpcBusProviderBehaviour is ICoreIpcBusProvider provider)
            {
                return provider.CoreIpcBus;
            }

            return null;
        }

        private SlotMotionDriver EnsureDriver()
        {
            if (_injectedDriver != null)
            {
                _slotMotionDriver = _injectedDriver;
                return _slotMotionDriver;
            }

            if (_slotMotionDriver != null) return _slotMotionDriver;

            _slotMotionDriver = GetComponent<SlotMotionDriver>();
            if (_slotMotionDriver == null)
            {
                _slotMotionDriver = gameObject.AddComponent<SlotMotionDriver>();
            }

            return _slotMotionDriver;
        }

#if UNITY_EDITOR
        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                Shutdown();
            }
        }
#endif
    }
}
