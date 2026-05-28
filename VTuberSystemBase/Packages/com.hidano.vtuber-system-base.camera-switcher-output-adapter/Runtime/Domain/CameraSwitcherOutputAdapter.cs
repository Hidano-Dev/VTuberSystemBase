#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VTuberSystemBase.CameraSwitcherOutputAdapter.Abstractions;
using VTuberSystemBase.CameraSwitcherOutputAdapter.Adapters.Ucapi;
using VTuberSystemBase.CameraSwitcherTab.Contracts;
using VTuberSystemBase.CoreIpc.Abstractions;
using VTuberSystemBase.OutputRendererShell.Abstractions;

using CameraType = VTuberSystemBase.CameraSwitcherTab.Contracts.CameraType;
namespace VTuberSystemBase.CameraSwitcherOutputAdapter.Domain
{
    public enum AdapterStatus
    {
        Initializing,
        Ready,
        Disposing,
        Disposed,
    }

    /// <summary>
    /// Central state machine for the camera-switcher main-output adapter
    /// (Requirement 2.x / 3.x / 5.x / 6.x / 9.x / 11.1〜11.2).
    /// Owns the registry, active-camera gate, publisher, fallback controller and
    /// failure aggregator; coordinates IPC handler registration and OSC frame
    /// dispatch.
    /// </summary>
    public sealed class CameraSwitcherOutputAdapter : IDisposable
    {
        private readonly IOutputCommandDispatcher _dispatcher;
        private readonly IOutputSceneRoots _sceneRoots;
        private readonly ICameraIdAllocator _allocator;
        private readonly IOscReceiverHost _oscHost;
        private readonly ILocalVolumeBinder _volumeBinder;
        private readonly IVolumeOverrideSchemaResolver _schemaResolver;
        private readonly ICameraGameObjectFactory _factory;
        private readonly CameraSwitcherOutputAdapterConfig _config;
        private readonly Ucapi4UnityFlatRecordApplier _applier;

        private readonly CameraEntryRegistry _registry = new();
        private readonly ActiveCameraGate _gate;
        private readonly DefaultCameraFallbackController _fallback;
        private readonly FailureAggregator _failures;
        private readonly CamerasListPublisher _publisher;
        private readonly OscMessageRouter _router;
        private readonly List<IDisposable> _registrations = new();
        private readonly Dictionary<CameraId, List<IDisposable>> _perCameraRegistrations = new();
        private bool _disposed;
        private int _allocOrderCounter;

        // OSC receive diagnostics. Incremented on the Unity main thread inside
        // OnOscMessageReceived, so non-atomic ++ is sufficient. These exist so
        // an external tool can distinguish a genuine round-trip from a UDP "false
        // success" (a Send that returns OK while the packet never arrived because of a
        // port mismatch): if OscFramesReceived stays 0 while the receiver is Running,
        // nothing actually arrived.
        private long _oscFramesReceived;
        private long _oscFramesApplied;
        private string? _lastAppliedCameraId;
        private long _lastAppliedAtUnixMs;

        public CameraSwitcherOutputAdapter(
            IOutputCommandDispatcher dispatcher,
            IOutputSceneRoots sceneRoots,
            ICameraIdAllocator allocator,
            IOscReceiverHost oscHost,
            ILocalVolumeBinder volumeBinder,
            IVolumeOverrideSchemaResolver schemaResolver,
            ICameraGameObjectFactory factory,
            ICoreIpcBus bus,
            ICameraSwitcherOutputAdapterClock clock,
            CameraSwitcherOutputAdapterConfig config)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _sceneRoots = sceneRoots ?? throw new ArgumentNullException(nameof(sceneRoots));
            _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
            _oscHost = oscHost ?? throw new ArgumentNullException(nameof(oscHost));
            _volumeBinder = volumeBinder ?? throw new ArgumentNullException(nameof(volumeBinder));
            _schemaResolver = schemaResolver ?? throw new ArgumentNullException(nameof(schemaResolver));
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _config = config ?? throw new ArgumentNullException(nameof(config));

            _failures = new FailureAggregator(bus);
            _publisher = new CamerasListPublisher(bus, clock);
            _gate = new ActiveCameraGate(_registry, onUnknownCameraId: id =>
                _failures.RecordUnknownCameraIdOnIpc("active-set", id));
            _fallback = new DefaultCameraFallbackController(_sceneRoots.DefaultCamera);
            _router = new OscMessageRouter(
                tryResolve: id => _registry.TryGet(id, out var entry) ? entry : null,
                onUnknownCameraId: id => _failures.RecordUnknownCameraIdOnOsc(id));
            _applier = new Ucapi4UnityFlatRecordApplier(onDecodeFailure: (id, ex) =>
                _failures.RecordOscDecodeFailure(id.Value, ex));
        }

        public AdapterStatus Status { get; private set; } = AdapterStatus.Initializing;
        public int CameraCount => _registry.Count;
        public CameraId? ActiveCameraId => _gate.Active;
        public FailureAggregator Failures => _failures;
        public CameraEntryRegistry Registry => _registry;

        /// <summary>OSC frames that reached the adapter (address + blob already decoded).</summary>
        public long OscFramesReceived => _oscFramesReceived;
        /// <summary>OSC frames successfully applied to a registered camera.</summary>
        public long OscFramesApplied => _oscFramesApplied;
        /// <summary>CameraId of the most recently applied OSC frame, or null.</summary>
        public string? LastAppliedCameraId => _lastAppliedCameraId;
        /// <summary>Unix-ms timestamp of the most recently applied OSC frame, or 0.</summary>
        public long LastAppliedAtUnixMs => _lastAppliedAtUnixMs;
        /// <summary>Configured OSC receive host (what an emitter must target).</summary>
        public string OscReceiveHost => _config.OscHost;
        /// <summary>Configured OSC receive port (what an emitter must target).</summary>
        public int OscReceivePort => _config.OscPort;

        public async Task InitializeAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            // Register the static (non per-camera) IPC handlers.
            _registrations.Add(_dispatcher.RegisterEventHandler<CameraCommandPayload>(
                CameraIpcTopics.CameraCommand, OnCameraCommand));
            _registrations.Add(_dispatcher.RegisterEventHandler<PreviewCommandPayload>(
                CameraIpcTopics.PreviewCommand, OnPreviewCommand));
            _registrations.Add(_dispatcher.RegisterEventHandler<PresetCommandPayload>(
                CameraIpcTopics.PresetCommand, OnPresetCommandObservation));

            _oscHost.MessageReceived += OnOscMessageReceived;

            // Start OSC; failure is non-fatal (Req 1.4 / 12.4).
            var startResult = await _oscHost.StartAsync(_config.OscHost, _config.OscPort, ct);
            if (!startResult.Success)
            {
                _failures.RecordOscStartupFailure(startResult.FailureDetail ?? "unknown");
            }

            // Initial publishes.
            _publisher.PublishCamerasList(_registry.Enumerate());
            _publisher.PublishCamerasActive(null);
            Status = AdapterStatus.Ready;
        }

        public void OnOscMessageReceived(OscReceivedMessage message)
        {
            if (_disposed) return;
            _oscFramesReceived++;
            _router.Route(in message, (entry, blob) =>
            {
                if (entry.CameraComponent == null) return;
                if (_applier.Apply(entry.CameraId, blob, entry.CameraComponent))
                {
                    _oscFramesApplied++;
                    _lastAppliedCameraId = entry.CameraId.Value;
                    _lastAppliedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
            });
        }

        public void OnCameraCommand(EventCommand<CameraCommandPayload> cmd)
        {
            if (_disposed) return;
            var payload = cmd.Payload;
            var op = payload.Op ?? string.Empty;
            switch (op)
            {
                case CameraCommandOps.Add: HandleAdd(payload); break;
                case CameraCommandOps.Delete: HandleDelete(payload); break;
                case CameraCommandOps.ActiveSet: HandleActiveSet(payload); break;
                default:
                    _failures.RecordCameraOperationFailure(op, payload.CameraId, "InvalidOp", $"unknown op: {op}",
                        payload.ClientRequestId);
                    break;
            }
        }

        public void OnPreviewCommand(EventCommand<PreviewCommandPayload> cmd)
        {
            if (_disposed) return;
            var payload = cmd.Payload;
            var op = payload.Op ?? string.Empty;
            // Placeholder: publish empty preview/handle for each affected cameraId (CSO-13).
            if (payload.CameraIds == null) return;
            foreach (var cameraIdString in payload.CameraIds)
            {
                if (!CameraId.TryCreate(cameraIdString, out var cameraId)) continue;
                if (op == PreviewCommandOps.Attach || op == PreviewCommandOps.Detach)
                {
                    // Empty payload as a placeholder (Req 8.7 / 9.2).
                    _publisher.PublishVolumeEnabledForAll(_registry.Enumerate(), _gate.Active);
                    // Note: actual handle publishing happens via the publisher; we keep
                    // this minimal here per CSO-13.
                }
            }
        }

        public void OnPresetCommandObservation(EventCommand<PresetCommandPayload> cmd)
        {
            // Observation only (CSO-12).
            var payload = cmd.Payload;
            Debug.Log($"[CameraSwitcherOutputAdapter] preset/command op={payload.Op} name={payload.Name}");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Status = AdapterStatus.Disposing;

            try
            {
                _oscHost.MessageReceived -= OnOscMessageReceived;
            }
            catch { /* defensive */ }

            try
            {
                _ = _oscHost.StopAsync();
            }
            catch { /* defensive */ }

            // Dispose per-camera registrations first.
            foreach (var list in _perCameraRegistrations.Values)
            {
                foreach (var d in list)
                {
                    try { d.Dispose(); } catch { /* defensive */ }
                }
            }
            _perCameraRegistrations.Clear();

            // Dispose static registrations.
            for (var i = _registrations.Count - 1; i >= 0; i--)
            {
                try { _registrations[i].Dispose(); } catch { /* defensive */ }
            }
            _registrations.Clear();

            // Destroy each camera GameObject.
            foreach (var entry in _registry.Enumerate())
            {
                try { _factory.Destroy(entry); } catch { /* defensive */ }
            }
            _registry.Clear();

            _fallback.RestoreFallback();
            Status = AdapterStatus.Disposed;
        }

        // ---- internal handlers ----

        private void HandleAdd(CameraCommandPayload payload)
        {
            if (_registry.Count >= _config.MaxCameras)
            {
                _failures.RecordCameraOperationFailure(CameraCommandOps.Add, null,
                    CameraErrorReasons.ResourceExhausted,
                    $"max cameras reached: {_config.MaxCameras}",
                    payload.ClientRequestId);
                return;
            }

            var typeWire = payload.Type ?? CameraTypeNames.Perspective;
            var type = CameraTypeNames.Parse(typeWire);
            if (type == CameraType.Unknown)
            {
                _failures.RecordCameraOperationFailure(CameraCommandOps.Add, null,
                    CameraErrorReasons.InvalidType, typeWire, payload.ClientRequestId);
                return;
            }

            var cameraId = _allocator.Allocate();
            var displayName = string.IsNullOrEmpty(payload.DisplayName) ? cameraId.Value : payload.DisplayName!;
            var defaultTransform = new CameraDefaultTransform
            {
                Position = new[] { _config.DefaultPosition.x, _config.DefaultPosition.y, _config.DefaultPosition.z },
                Rotation = new[] { _config.DefaultRotation.x, _config.DefaultRotation.y, _config.DefaultRotation.z, _config.DefaultRotation.w },
                FocalLengthMm = _config.DefaultFocalLengthMm,
            };
            var allocOrder = ++_allocOrderCounter;

            CameraEntry entry;
            try
            {
                entry = _factory.Create(_sceneRoots.Cameras, cameraId, displayName, type, defaultTransform, allocOrder);
            }
            catch (Exception ex)
            {
                _failures.RecordCameraOperationFailure(CameraCommandOps.Add, cameraId.Value,
                    CameraErrorReasons.ResourceExhausted, ex.Message, payload.ClientRequestId);
                return;
            }

            _registry.Upsert(entry);
            _fallback.NotifyCameraCountChanged(_registry.Count);

            // Per-camera handler registration.
            RegisterPerCameraHandlers(cameraId);

            _publisher.PublishCameraCreated(payload.ClientRequestId ?? string.Empty, entry);
            _publisher.PublishCamerasList(_registry.Enumerate());
        }

        private void HandleDelete(CameraCommandPayload payload)
        {
            if (string.IsNullOrEmpty(payload.CameraId))
            {
                _failures.RecordCameraOperationFailure(CameraCommandOps.Delete, null,
                    "InvalidPayload", "cameraId missing", payload.ClientRequestId);
                return;
            }
            if (!CameraId.TryCreate(payload.CameraId, out var cameraId))
            {
                _failures.RecordCameraOperationFailure(CameraCommandOps.Delete, payload.CameraId,
                    "InvalidCameraId", "cameraId character class violated", payload.ClientRequestId);
                return;
            }
            if (!_registry.TryGet(cameraId, out var entry))
            {
                _failures.RecordUnknownCameraIdOnIpc(CameraCommandOps.Delete, cameraId.Value, payload.ClientRequestId);
                return;
            }

            UnregisterPerCameraHandlers(cameraId);
            _registry.Remove(cameraId);
            _gate.OnCameraRemoved(cameraId);
            try { _factory.Destroy(entry); } catch { /* defensive */ }
            _fallback.NotifyCameraCountChanged(_registry.Count);

            _publisher.PublishCamerasList(_registry.Enumerate());
            if (_gate.Active == null)
            {
                _publisher.PublishCamerasActive(null);
            }
        }

        private void HandleActiveSet(CameraCommandPayload payload)
        {
            CameraId? target = null;
            if (!string.IsNullOrEmpty(payload.CameraId))
            {
                if (!CameraId.TryCreate(payload.CameraId, out var parsed))
                {
                    _failures.RecordCameraOperationFailure(CameraCommandOps.ActiveSet, payload.CameraId,
                        "InvalidCameraId", "cameraId character class violated", payload.ClientRequestId);
                    return;
                }
                target = parsed;
            }

            var beforeActive = _gate.Active;
            _gate.SetActive(target);
            if (target.HasValue && _gate.Active == null)
            {
                // Gate refused (unknown cameraId already recorded by gate).
                return;
            }
            _publisher.PublishCamerasActive(_gate.Active);
            _publisher.PublishVolumeEnabledForAll(_registry.Enumerate(), _gate.Active);
        }

        private void RegisterPerCameraHandlers(CameraId cameraId)
        {
            if (_perCameraRegistrations.ContainsKey(cameraId)) return;
            var list = new List<IDisposable>();
            try
            {
                list.Add(_dispatcher.RegisterStateHandler<CameraMetadataStatePayload>(
                    CameraIpcTopics.CameraMetadata(cameraId, CameraMetadataKeys.DisplayName),
                    cmd => OnCameraMetadata(cmd, cameraId, CameraMetadataKeys.DisplayName)));
                list.Add(_dispatcher.RegisterStateHandler<CameraMetadataStatePayload>(
                    CameraIpcTopics.CameraMetadata(cameraId, CameraMetadataKeys.Type),
                    cmd => OnCameraMetadata(cmd, cameraId, CameraMetadataKeys.Type)));
                list.Add(_dispatcher.RegisterStateHandler<CameraMetadataStatePayload>(
                    CameraIpcTopics.CameraMetadata(cameraId, CameraMetadataKeys.DefaultTransform),
                    cmd => OnCameraMetadata(cmd, cameraId, CameraMetadataKeys.DefaultTransform)));
                list.Add(_dispatcher.RegisterEventHandler<VolumeCommandPayload>(
                    CameraIpcTopics.VolumeCommand(cameraId),
                    cmd => OnVolumeCommand(cmd, cameraId)));
                list.Add(_dispatcher.RegisterStateHandler<VolumeEnabledStatePayload>(
                    CameraIpcTopics.VolumeEnabled(cameraId),
                    cmd => OnVolumeEnabled(cmd, cameraId)));
                list.Add(_dispatcher.RegisterRequestHandler<VolumeMetadataRequest, VolumeMetadataResponse>(
                    CameraIpcTopics.VolumeOverridesMetadata(cameraId),
                    req => OnVolumeMetadataRequest(req, cameraId)));
            }
            catch (Exception ex)
            {
                Debug.Log($"[CameraSwitcherOutputAdapter] per-camera handler registration failed: {ex.Message}");
            }
            _perCameraRegistrations[cameraId] = list;
        }

        private void UnregisterPerCameraHandlers(CameraId cameraId)
        {
            if (!_perCameraRegistrations.TryGetValue(cameraId, out var list)) return;
            for (var i = list.Count - 1; i >= 0; i--)
            {
                try { list[i].Dispose(); } catch { /* defensive */ }
            }
            _perCameraRegistrations.Remove(cameraId);
        }

        public void OnCameraMetadata(StateCommand<CameraMetadataStatePayload> cmd, CameraId cameraId, string key)
        {
            if (!_registry.TryGet(cameraId, out var entry)) return;
            var json = cmd.Payload.Value;
            try
            {
                switch (key)
                {
                    case CameraMetadataKeys.DisplayName:
                        if (json.ValueKind == JsonValueKind.String)
                        {
                            entry.DisplayName = json.GetString() ?? entry.DisplayName;
                            if (entry.GameObject != null) entry.GameObject.name = $"Camera-{cameraId.Value}-{entry.DisplayName}";
                            _publisher.PublishCamerasList(_registry.Enumerate());
                        }
                        break;
                    case CameraMetadataKeys.Type:
                        if (json.ValueKind == JsonValueKind.String)
                        {
                            var parsed = CameraTypeNames.Parse(json.GetString());
                            if (parsed != CameraType.Unknown)
                            {
                                entry.Type = parsed;
                                if (entry.CameraComponent != null)
                                {
                                    entry.CameraComponent.orthographic = parsed == CameraType.Orthographic;
                                }
                                _publisher.PublishCamerasList(_registry.Enumerate());
                            }
                        }
                        break;
                    case CameraMetadataKeys.DefaultTransform:
                        if (json.ValueKind == JsonValueKind.Object)
                        {
                            var transform = JsonSerializer.Deserialize<CameraDefaultTransform>(json.GetRawText());
                            entry.DefaultTransform = transform;
                            ApplyDefaultTransform(entry);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                _failures.RecordReflectionFailed($"metadata.{key}", ex);
            }
        }

        public void OnVolumeCommand(EventCommand<VolumeCommandPayload> cmd, CameraId cameraId)
        {
            if (!_registry.TryGet(cameraId, out var entry) || entry.LocalVolume == null) return;
            var payload = cmd.Payload;
            VolumeBindResult result;
            switch (payload.Op)
            {
                case VolumeCommandOps.OverrideAdd:
                    result = _volumeBinder.AddOverride(entry.LocalVolume, payload.OverrideType);
                    break;
                case VolumeCommandOps.OverrideRemove:
                    result = _volumeBinder.RemoveOverride(entry.LocalVolume, payload.OverrideType);
                    break;
                default:
                    result = VolumeBindResult.Error("InvalidOp", payload.Op);
                    break;
            }
            if (!result.Success)
            {
                _failures.RecordVolumeBindFailed(payload.Op, cameraId.Value, payload.OverrideType,
                    result.Reason ?? "Unknown", result.Detail);
            }
        }

        public void OnVolumeEnabled(StateCommand<VolumeEnabledStatePayload> cmd, CameraId cameraId)
        {
            if (!_registry.TryGet(cameraId, out var entry) || entry.LocalVolume == null) return;
            _volumeBinder.SetVolumeEnabled(entry.LocalVolume, cmd.Payload.Enabled);
        }

        public VolumeMetadataResponse OnVolumeMetadataRequest(RequestCommand<VolumeMetadataRequest> req, CameraId cameraId)
        {
            try
            {
                return _schemaResolver.GetSchema();
            }
            catch (Exception ex)
            {
                _failures.RecordReflectionFailed("volume/overrides/metadata", ex);
                return new VolumeMetadataResponse { Overrides = Array.Empty<VolumeOverrideSchema>() };
            }
        }

        private static void ApplyDefaultTransform(CameraEntry entry)
        {
            if (entry.GameObject == null) return;
            var t = entry.GameObject.transform;
            var p = entry.DefaultTransform.Position;
            if (p != null && p.Length >= 3) t.position = new Vector3(p[0], p[1], p[2]);
            var r = entry.DefaultTransform.Rotation;
            if (r != null && r.Length >= 4) t.rotation = new Quaternion(r[0], r[1], r[2], r[3]);
            if (entry.CameraComponent != null && entry.DefaultTransform.FocalLengthMm > 0f)
                entry.CameraComponent.focalLength = entry.DefaultTransform.FocalLengthMm;
        }
    }
}
