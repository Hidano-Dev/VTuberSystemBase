#nullable enable
using System;
using UnityEngine;
using VTuberSystemBase.CameraSwitcherOutputAdapter.Abstractions;
using VTuberSystemBase.CameraSwitcherOutputAdapter.Adapters.Allocator;
using VTuberSystemBase.CameraSwitcherOutputAdapter.Adapters.Osc;
using VTuberSystemBase.CameraSwitcherOutputAdapter.Adapters.Volume;
using VTuberSystemBase.CameraSwitcherOutputAdapter.Domain;
using VTuberSystemBase.CameraSwitcherOutputAdapter.Runtime.Diagnostics;
using VTuberSystemBase.CoreIpc.Abstractions;
using VTuberSystemBase.OutputRendererShell.Abstractions;
using VTuberSystemBase.OutputRendererShell.Scene;
using CameraSwitcherOutputAdapterCore = VTuberSystemBase.CameraSwitcherOutputAdapter.Domain.CameraSwitcherOutputAdapter;

namespace VTuberSystemBase.CameraSwitcherOutputAdapter.Runtime
{
    /// <summary>
    /// MonoBehaviour Composition Root for the camera-switcher output adapter
    /// (Requirement 1.3 / 1.5〜1.7 / 11.x). Wires the concrete adapters in
    /// <see cref="Awake"/> after the shell's <c>OutputSceneBootstrapper</c>
    /// finishes initialising, and tears them down on
    /// <see cref="OnApplicationQuit"/> / <see cref="OnDestroy"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CameraSwitcherOutputAdapterBootstrapper : MonoBehaviour
    {
        [Header("Configuration")]
        [SerializeField] private CameraSwitcherOutputAdapterConfig? _config;

        [Header("Optional Bus Override (testing)")]
        [SerializeField] private bool _autoStart = true;

        private CameraSwitcherOutputAdapterCore? _adapter;
        private IpcHandlerRegistration? _ipcRegistration;
        private CameraSwitcherOutputAdapterDiagnostics? _diagnostics;
        private UoscReceiverHostAdapter? _oscHost;
        private bool _disposed;

        public CameraSwitcherOutputAdapterCore? Adapter => _adapter;
        public CameraSwitcherOutputAdapterDiagnostics? Diagnostics => _diagnostics;

        // Test-side injection points (set before Awake / EnsureInitialized).
        private ICoreIpcBus? _injectedBus;
        private IOutputCommandDispatcher? _injectedDispatcher;
        private IOutputSceneRoots? _injectedSceneRoots;

        public void InjectForTesting(ICoreIpcBus bus, IOutputCommandDispatcher dispatcher, IOutputSceneRoots sceneRoots)
        {
            _injectedBus = bus;
            _injectedDispatcher = dispatcher;
            _injectedSceneRoots = sceneRoots;
        }

        private void Awake()
        {
            MainThreadGuard.Initialize();
            if (!_autoStart) return;
            if (!Application.isPlaying) return; // D-9: PlayMode only.
            if (_config == null)
            {
                _config = ScriptableObject.CreateInstance<CameraSwitcherOutputAdapterConfig>();
            }
            TryStart();
        }

        private void TryStart()
        {
            try
            {
                var dispatcher = _injectedDispatcher ?? FindDispatcher();
                var sceneRoots = _injectedSceneRoots ?? FindSceneRoots();
                if (dispatcher == null || sceneRoots == null)
                {
                    Debug.LogWarning("[CameraSwitcherOutputAdapter] OutputSceneBootstrapper not initialized yet; deferring.");
                    enabled = true;
                    return;
                }

                ICoreIpcBus? bus = _injectedBus;
                // No bus is acceptable; FailureAggregator/Publisher tolerate null via fakes.
                var allocator = new SequentialCameraIdAllocator();
                _oscHost = new UoscReceiverHostAdapter();
                var volumeBinder = new GlobalEnabledLocalVolumeBinder(
                    new VolumeComponentTypeResolver(),
                    new VolumeParameterValueWriter());
                var schemaResolver = new ReflectionVolumeOverrideSchemaResolver();
                var factory = new CameraGameObjectFactory(volumeBinder, _config!.DefaultSensorSize);

                _adapter = new CameraSwitcherOutputAdapterCore(
                    dispatcher, sceneRoots, allocator, _oscHost, volumeBinder, schemaResolver,
                    factory, bus!, new SystemUtcClock(), _config);

                // The static IPC handlers (camera/command, camera/preview/command,
                // camera/preset/command) are registered by _adapter.InitializeAsync() below.
                // Do NOT also call _ipcRegistration.RegisterAll(...) here: the dispatcher rejects
                // duplicate (topic, kind) registrations (HandlerRegistry throws), and because
                // InitializeAsync runs fire-and-forget the duplicate would silently fault the
                // init before OSC startup, leaving Status stuck at Initializing and OSC stopped.
                _ipcRegistration = new IpcHandlerRegistration();
                _diagnostics = new CameraSwitcherOutputAdapterDiagnostics(_adapter, _oscHost, _ipcRegistration);

                _ = _adapter.InitializeAsync();
                Debug.Log("[CameraSwitcherOutputAdapter] Camera Switcher Output Adapter ready");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                Cleanup();
            }
        }

        private void OnApplicationQuit() => Cleanup();
        private void OnDestroy() => Cleanup();

        private void Cleanup()
        {
            if (_disposed) return;
            _disposed = true;
            try { _ipcRegistration?.Dispose(); } catch { /* defensive */ }
            try { _adapter?.Dispose(); } catch { /* defensive */ }
            try { _oscHost?.Dispose(); } catch { /* defensive */ }
            _ipcRegistration = null;
            _adapter = null;
            _oscHost = null;
            _diagnostics = null;
            MainThreadGuard.Reset();
        }

        private static IOutputCommandDispatcher? FindDispatcher()
        {
#if UNITY_2022_2_OR_NEWER
            var shell = FindAnyObjectByType<OutputSceneBootstrapper>();
#else
            var shell = FindObjectOfType<OutputSceneBootstrapper>();
#endif
            return shell?.Dispatcher;
        }

        private static IOutputSceneRoots? FindSceneRoots()
        {
#if UNITY_2022_2_OR_NEWER
            var shell = FindAnyObjectByType<OutputSceneBootstrapper>();
#else
            var shell = FindObjectOfType<OutputSceneBootstrapper>();
#endif
            return shell?.Roots;
        }
    }

    internal sealed class SystemUtcClock : ICameraSwitcherOutputAdapterClock
    {
        public long UnixMillisecondsNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
