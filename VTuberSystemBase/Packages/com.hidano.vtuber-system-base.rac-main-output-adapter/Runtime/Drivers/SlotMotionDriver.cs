using System;
using System.Collections.Generic;
using RealtimeAvatarController.Core;
using RealtimeAvatarController.Motion;
using UniRx;
using UnityEngine;

namespace VTuberSystemBase.RacMainOutputAdapter.Drivers
{
    /// <summary>
    /// Drives RAC slot motion application from Unity's LateUpdate loop.
    /// </summary>
    public sealed class SlotMotionDriver : MonoBehaviour
    {
        private readonly Dictionary<string, Pipeline> _pipelines = new();
        private readonly List<string> _iterationBuffer = new();
        private IDisposable _subscription;
        private SlotManager _slotManager;

        internal int ActivePipelineCount => _pipelines.Count;
        internal int ApplyAttemptCount { get; private set; }
        internal bool HasPipeline(string slotId) => _pipelines.ContainsKey(slotId);

        public void Attach(SlotManager slotManager)
        {
            if (ReferenceEquals(_slotManager, slotManager)) return;

            Detach();
            _slotManager = slotManager;
            if (_slotManager == null) return;

            _subscription = _slotManager.OnSlotStateChanged.Subscribe(OnSlotStateChanged);

            foreach (var handle in _slotManager.GetSlots())
            {
                if (handle?.State == SlotState.Active)
                {
                    BuildPipeline(handle.SlotId);
                }
            }
        }

        public void Detach()
        {
            _subscription?.Dispose();
            _subscription = null;
            _slotManager = null;
            TeardownAllPipelines();
        }

        private void OnSlotStateChanged(SlotStateChangedEvent e)
        {
            if (e == null) return;

            if (e.NewState == SlotState.Active)
            {
                BuildPipeline(e.SlotId);
            }
            else if (e.NewState == SlotState.Disposed)
            {
                TeardownPipeline(e.SlotId);
            }
        }

        private void BuildPipeline(string slotId)
        {
            if (string.IsNullOrEmpty(slotId)) return;
            if (_pipelines.ContainsKey(slotId)) return;
            if (_slotManager == null) return;
            if (!_slotManager.TryGetSlotResources(slotId, out var source, out var avatar)) return;

            var cache = new MotionCache();
            HumanoidMotionApplier applier = null;

            try
            {
                cache.SetSource(source);
                applier = new HumanoidMotionApplier(slotId);
                applier.SetAvatar(avatar);
                _pipelines[slotId] = new Pipeline(cache, applier);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SlotMotionDriver] slotId='{slotId}' pipeline setup skipped: {ex.Message}");
                applier?.Dispose();
                cache.Dispose();
            }
        }

        private void LateUpdate()
        {
            if (_slotManager == null || _pipelines.Count == 0) return;

            _iterationBuffer.Clear();
            foreach (var slotId in _pipelines.Keys)
            {
                _iterationBuffer.Add(slotId);
            }

            for (var i = 0; i < _iterationBuffer.Count; i++)
            {
                var slotId = _iterationBuffer[i];
                if (!_pipelines.TryGetValue(slotId, out var pipeline)) continue;

                var handle = _slotManager.GetSlot(slotId);
                var settings = handle?.Settings;
                if (settings == null) continue;

                var frame = pipeline.Cache.LatestFrame;
                var weight = settings.weight;
                var capturedApplier = pipeline.Applier;

                ApplyAttemptCount++;
                _slotManager.ApplyWithFallback(slotId, () => capturedApplier.Apply(frame, weight, settings));
            }
        }

        private void OnDestroy()
        {
            Detach();
        }

        private void TeardownPipeline(string slotId)
        {
            if (!_pipelines.TryGetValue(slotId, out var pipeline)) return;

            pipeline.Dispose();
            _pipelines.Remove(slotId);
        }

        private void TeardownAllPipelines()
        {
            foreach (var pipeline in _pipelines.Values)
            {
                pipeline.Dispose();
            }
            _pipelines.Clear();
            _iterationBuffer.Clear();
        }

        private readonly struct Pipeline : IDisposable
        {
            public Pipeline(MotionCache cache, HumanoidMotionApplier applier)
            {
                Cache = cache;
                Applier = applier;
            }

            public MotionCache Cache { get; }
            public HumanoidMotionApplier Applier { get; }

            public void Dispose()
            {
                Cache?.Dispose();
                Applier?.Dispose();
            }
        }
    }
}
