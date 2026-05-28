#nullable enable
using System;
using UnityEngine;
using VTuberSystemBase.OutputRendererShell.Abstractions;
using VTuberSystemBase.StageLightingVolumeOutputAdapter.Diagnostics;
using VTuberSystemBase.StageLightingVolumeOutputAdapter.Internal;
using VTuberSystemBase.StageLightingVolumeTab.Contracts;
using Object = UnityEngine.Object;

namespace VTuberSystemBase.StageLightingVolumeOutputAdapter.Lights
{
    /// <summary>
    /// Owns lifetime of every dynamic <see cref="UnityEngine.Light"/> driven by the adapter.
    /// Subscribes to <c>light/command</c> events for add / remove, allocates lightIds as
    /// 32-character GUID hex strings, registers per-property state handlers when a light is
    /// added, and publishes <c>lights/list</c>, <c>light/added</c>, and <c>light/error</c>.
    /// </summary>
    internal sealed class LightHandler : IDisposable
    {
        private readonly IOutputCommandDispatcher _dispatcher;
        private readonly IOutputSceneRoots _roots;
        private readonly IAdapterMessageSink _sink;
        private readonly AdapterErrorReporter _errorReporter;
        private readonly AdapterLogger _logger;
        private readonly StageLightingVolumeOutputAdapterDiagnostics _diagnostics;
        private readonly LightRegistry _registry = new();
        private readonly LightPropertyApplier _applier;
        private readonly HandlerRegistrationToken _tokens = new();
        private readonly Func<string> _idFactory;
        private bool _disposed;

        // Number of dispatcher handlers this LightHandler has contributed to the
        // (process-shared) diagnostics RegisteredHandlerCount. Tracked so teardown can
        // subtract exactly its own share — the counter is shared with the stage / volume
        // / preview handlers, so resetting it to 0 here would corrupt their counts.
        private int _ownedHandlerCount;

        public LightHandler(
            IOutputCommandDispatcher dispatcher,
            IOutputSceneRoots roots,
            IAdapterMessageSink sink,
            AdapterErrorReporter errorReporter,
            AdapterLogger logger,
            StageLightingVolumeOutputAdapterDiagnostics diagnostics,
            Func<string>? idFactory = null)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _roots = roots ?? throw new ArgumentNullException(nameof(roots));
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _applier = new LightPropertyApplier(_registry, _logger);
            _idFactory = idFactory ?? (() => Guid.NewGuid().ToString("N"));
        }

        internal LightRegistry Registry => _registry;
        internal LightPropertyApplier Applier => _applier;

        public void Start()
        {
            _tokens.Add(_dispatcher.RegisterEventHandler<LightCommandDto>(
                StageLightingTopics.LightCommand, OnLightCommand));
            AddOwnedHandlers(1);
            // Initial empty list publish so the UI can render an empty section.
            _sink.PublishState(StageLightingTopics.LightsList, new LightListDto(System.Array.Empty<LightListItemDto>()));
        }

        private void OnLightCommand(EventCommand<LightCommandDto> cmd)
        {
            try
            {
                var op = cmd.Payload.Op;
                if (string.Equals(op, "add", StringComparison.Ordinal))
                {
                    HandleAdd(cmd.Payload);
                }
                else if (string.Equals(op, "remove", StringComparison.Ordinal))
                {
                    HandleRemove(cmd.Payload);
                }
                else
                {
                    _logger.Warning("LightHandler", "unknown_op", context: op,
                        topic: StageLightingTopics.LightCommand);
                }
            }
            catch (Exception ex)
            {
                _errorReporter.ReportLightError(cmd.Payload.LightId, "internal_error", ex.Message);
            }
        }

        private void HandleAdd(LightCommandDto cmd)
        {
            if (cmd.Initial is null)
            {
                _errorReporter.ReportLightError(null, "internal_error", "add command missing Initial payload");
                return;
            }
            var initial = cmd.Initial.Value;
            string lightId;
            try
            {
                lightId = _idFactory();
            }
            catch (Exception ex)
            {
                _errorReporter.ReportLightError(null, "internal_error", ex.Message);
                return;
            }

            GameObject? go = null;
            try
            {
                go = new GameObject($"Light_{lightId}");
                go.transform.SetParent(_roots.Lights, worldPositionStays: false);
                var light = go.AddComponent<Light>();
                var entry = new LightEntry(lightId, go, light, initial);
                _applier.ApplyInitial(entry, initial);
                _registry.Add(lightId, entry);

                // Register dynamic per-property state handlers.
                RegisterPropertyHandlers(entry);

                _diagnostics.SetLightCount(_registry.Count);
                _sink.PublishEvent(StageLightingTopics.LightAdded, new LightAddedDto(lightId, initial));
                _sink.PublishState(StageLightingTopics.LightsList, _registry.ToListDto());
            }
            catch (Exception ex)
            {
                if (go != null)
                {
                    try { Object.DestroyImmediate(go); } catch { /* ignore */ }
                }
                _errorReporter.ReportLightError(null, "internal_error", ex.Message);
            }
        }

        private void HandleRemove(LightCommandDto cmd)
        {
            var lightId = cmd.LightId;
            if (string.IsNullOrEmpty(lightId) || !_registry.TryGet(lightId!, out var entry))
            {
                _errorReporter.ReportLightError(lightId, "not_found", "no such light id");
                _sink.PublishState(StageLightingTopics.LightsList, _registry.ToListDto());
                return;
            }
            // Dispose property handlers, destroy GameObject, remove from registry, republish list.
            var removedHandlers = entry.PropertyHandlers.Count;
            foreach (var d in entry.PropertyHandlers)
            {
                try { d.Dispose(); } catch { /* ignore */ }
            }
            entry.PropertyHandlers.Clear();
            AddOwnedHandlers(-removedHandlers);
            try { Object.DestroyImmediate(entry.GameObject); } catch { /* ignore */ }
            _registry.Remove(lightId!);
            _diagnostics.SetLightCount(_registry.Count);
            _sink.PublishState(StageLightingTopics.LightsList, _registry.ToListDto());
        }

        private void RegisterPropertyHandlers(LightEntry entry)
        {
            var id = entry.LightId;
            entry.PropertyHandlers.Add(_dispatcher.RegisterStateHandler<float>(
                StageLightingTopics.LightProperty(id, StageLightingTopics.PropertyIntensity),
                cmd => _applier.ApplyIntensity(id, cmd)));
            entry.PropertyHandlers.Add(_dispatcher.RegisterStateHandler<ColorDto>(
                StageLightingTopics.LightProperty(id, StageLightingTopics.PropertyColor),
                cmd => _applier.ApplyColor(id, cmd)));
            entry.PropertyHandlers.Add(_dispatcher.RegisterStateHandler<Vector3Dto>(
                StageLightingTopics.LightProperty(id, StageLightingTopics.PropertyRotation),
                cmd => _applier.ApplyRotation(id, cmd)));
            entry.PropertyHandlers.Add(_dispatcher.RegisterStateHandler<LightTypeDto>(
                StageLightingTopics.LightProperty(id, StageLightingTopics.PropertyType),
                cmd => _applier.ApplyType(id, cmd)));
            entry.PropertyHandlers.Add(_dispatcher.RegisterStateHandler<float>(
                StageLightingTopics.LightProperty(id, StageLightingTopics.PropertyRange),
                cmd => _applier.ApplyRange(id, cmd)));
            entry.PropertyHandlers.Add(_dispatcher.RegisterStateHandler<float>(
                StageLightingTopics.LightProperty(id, StageLightingTopics.PropertySpotAngle),
                cmd => _applier.ApplySpotAngle(id, cmd)));
            entry.PropertyHandlers.Add(_dispatcher.RegisterStateHandler<string>(
                StageLightingTopics.LightProperty(id, StageLightingTopics.PropertyDisplayName),
                cmd => _applier.ApplyDisplayName(id, cmd)));
            AddOwnedHandlers(entry.PropertyHandlers.Count);
        }

        // Mirrors every diagnostics handler-count change with a local tally so Dispose
        // can subtract exactly this handler's contribution from the shared counter.
        private void AddOwnedHandlers(int delta)
        {
            _ownedHandlerCount += delta;
            _diagnostics.IncrementHandlerCount(delta);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Tear down per-light state.
            foreach (var id in _registry.AllLightIds)
            {
                if (_registry.TryGet(id, out var e))
                {
                    foreach (var d in e.PropertyHandlers)
                    {
                        try { d.Dispose(); } catch { /* ignore */ }
                    }
                    e.PropertyHandlers.Clear();
                    try { Object.DestroyImmediate(e.GameObject); } catch { /* ignore */ }
                }
            }
            _registry.Clear();
            _diagnostics.SetLightCount(0);
            try { _tokens.Dispose(); } catch { /* ignore */ }
            // Return our remaining share (the Start command handler + any per-light
            // handlers not already removed) to the shared diagnostics counter.
            AddOwnedHandlers(-_ownedHandlerCount);
        }
    }
}
