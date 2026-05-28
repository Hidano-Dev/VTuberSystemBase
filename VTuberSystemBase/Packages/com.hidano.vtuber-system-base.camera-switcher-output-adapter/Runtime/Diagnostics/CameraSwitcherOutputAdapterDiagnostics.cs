#nullable enable
using System.Collections.Generic;
using VTuberSystemBase.CameraSwitcherOutputAdapter.Abstractions;
using VTuberSystemBase.CameraSwitcherOutputAdapter.Domain;
using VTuberSystemBase.CameraSwitcherTab.Contracts;

using CameraSwitcherOutputAdapterCore = VTuberSystemBase.CameraSwitcherOutputAdapter.Domain.CameraSwitcherOutputAdapter;
namespace VTuberSystemBase.CameraSwitcherOutputAdapter.Runtime.Diagnostics
{
    /// <summary>
    /// Aggregates diagnostic state from the adapter, registry, OSC host and
    /// failure aggregator into a single snapshot (Requirement 14.x).
    /// </summary>
    public sealed class CameraSwitcherOutputAdapterDiagnostics
    {
        private readonly CameraSwitcherOutputAdapterCore _adapter;
        private readonly IOscReceiverHost _oscHost;
        private readonly IpcHandlerRegistration _registration;

        public CameraSwitcherOutputAdapterDiagnostics(
            CameraSwitcherOutputAdapterCore adapter,
            IOscReceiverHost oscHost,
            IpcHandlerRegistration registration)
        {
            _adapter = adapter;
            _oscHost = oscHost;
            _registration = registration;
        }

        public Snapshot GetSnapshot()
        {
            var failureSnapshot = _adapter.Failures.GetSnapshot();
            var camerasIds = new List<string>();
            foreach (var entry in _adapter.Registry.Enumerate())
            {
                camerasIds.Add(entry.CameraId.Value);
            }
            return new Snapshot
            {
                AdapterStatus = _adapter.Status,
                CameraCount = _adapter.CameraCount,
                ActiveCameraId = _adapter.ActiveCameraId.HasValue ? _adapter.ActiveCameraId.Value.Value : null,
                Cameras = camerasIds,
                OscReceiverStatus = _oscHost.Status,
                IpcStaticHandlerCount = _registration.RegisteredHandlerCount,
                Failures = failureSnapshot,
                OscReceiveHost = _adapter.OscReceiveHost,
                OscReceivePort = _adapter.OscReceivePort,
                OscFramesReceived = _adapter.OscFramesReceived,
                OscFramesApplied = _adapter.OscFramesApplied,
                LastAppliedCameraId = _adapter.LastAppliedCameraId,
                LastAppliedAtUnixMs = _adapter.LastAppliedAtUnixMs,
            };
        }

        public readonly struct Snapshot
        {
            public AdapterStatus AdapterStatus { get; init; }
            public int CameraCount { get; init; }
            public string? ActiveCameraId { get; init; }
            public IReadOnlyList<string> Cameras { get; init; }
            public OscReceiverHostStatus OscReceiverStatus { get; init; }
            public int IpcStaticHandlerCount { get; init; }
            public FailureAggregator.Snapshot Failures { get; init; }

            /// <summary>Configured OSC receive host (target an emitter at this).</summary>
            public string OscReceiveHost { get; init; }
            /// <summary>Configured OSC receive port (target an emitter at this).</summary>
            public int OscReceivePort { get; init; }
            /// <summary>OSC frames that reached the adapter since start.</summary>
            public long OscFramesReceived { get; init; }
            /// <summary>OSC frames successfully applied to a camera since start.</summary>
            public long OscFramesApplied { get; init; }
            /// <summary>CameraId of the most recently applied OSC frame, or null.</summary>
            public string? LastAppliedCameraId { get; init; }
            /// <summary>Unix-ms timestamp of the most recently applied OSC frame, or 0.</summary>
            public long LastAppliedAtUnixMs { get; init; }
        }
    }
}
