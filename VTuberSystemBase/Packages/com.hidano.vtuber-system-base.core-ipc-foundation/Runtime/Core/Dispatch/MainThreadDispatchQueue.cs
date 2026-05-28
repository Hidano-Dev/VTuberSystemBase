#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using VTuberSystemBase.CoreIpc.Abstractions;

namespace VTuberSystemBase.CoreIpc.Core.Dispatch
{
    public sealed class MainThreadDispatchQueue
    {
        private readonly ConcurrentDictionary<string, MessageEnvelope> _stateSlots = new();
        private readonly ConcurrentQueue<MessageEnvelope> _eventQueue = new();
        private readonly ConcurrentQueue<MessageEnvelope> _requestQueue = new();
        private readonly ConcurrentDictionary<string, int> _eventQueueDepthByTopic = new();

        private readonly int _eventQueueWarningThresholdPerTopic;
        private readonly Action<string>? _logWarning;
        private readonly Action<string, Exception>? _logError;

        private IDispatchHandlerLookup? _handlerLookup;

        public MainThreadDispatchQueue()
            : this(new CoreIpcOptions())
        {
        }

        public MainThreadDispatchQueue(
            CoreIpcOptions options,
            Action<string>? logWarning = null,
            Action<string, Exception>? logError = null)
        {
            if (options is null) throw new ArgumentNullException(nameof(options));
            if (options.EventQueueWarningThresholdPerTopic < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "CoreIpcOptions.EventQueueWarningThresholdPerTopic must be non-negative.");
            }

            _eventQueueWarningThresholdPerTopic = options.EventQueueWarningThresholdPerTopic;
            _logWarning = logWarning;
            _logError = logError;
        }

        public int StateSlotCount => _stateSlots.Count;

        public int EventQueueCount => _eventQueue.Count;

        public int RequestQueueCount => _requestQueue.Count;

        public void SetHandlerLookup(IDispatchHandlerLookup? lookup)
        {
            _handlerLookup = lookup;
        }

        public void Enqueue(MessageEnvelope envelope)
        {
            if (envelope.Topic is null)
            {
                _logWarning?.Invoke("Dispatch queue dropped envelope with null topic.");
                return;
            }

            switch (envelope.Kind)
            {
                case MessageKind.State:
                    _stateSlots[envelope.Topic] = envelope;
                    break;

                case MessageKind.Event:
                    _eventQueue.Enqueue(envelope);
                    var newDepth = _eventQueueDepthByTopic.AddOrUpdate(
                        envelope.Topic,
                        addValue: 1,
                        updateValueFactory: (_, current) => current + 1);
                    if (newDepth == _eventQueueWarningThresholdPerTopic + 1)
                    {
                        _logWarning?.Invoke(
                            "Event queue depth for topic '" + envelope.Topic +
                            "' exceeded threshold " + _eventQueueWarningThresholdPerTopic +
                            " (no messages dropped).");
                    }
                    break;

                case MessageKind.Request:
                    _requestQueue.Enqueue(envelope);
                    break;

                case MessageKind.Response:
                    _logWarning?.Invoke(
                        "Dispatch queue dropped envelope of kind " + envelope.Kind +
                        " for topic '" + envelope.Topic +
                        "' (response is delivered via correlation registry).");
                    break;

                default:
                    _logWarning?.Invoke(
                        "Dispatch queue dropped envelope of unknown kind " + (int)envelope.Kind +
                        " for topic '" + envelope.Topic + "'.");
                    break;
            }
        }

        public void Flush()
        {
            var stateBatch = SnapshotAndClearStateSlots();
            var eventBatch = DrainEventQueue();
            var requestBatch = DrainRequestQueue();

            var lookup = _handlerLookup;
            if (lookup is null)
            {
                return;
            }

            for (int i = 0; i < stateBatch.Count; i++)
            {
                DispatchTo(lookup, stateBatch[i]);
                NotifyInboundObservers(stateBatch[i]);
            }

            for (int i = 0; i < eventBatch.Count; i++)
            {
                DispatchTo(lookup, eventBatch[i]);
                NotifyInboundObservers(eventBatch[i]);
            }

            for (int i = 0; i < requestBatch.Count; i++)
            {
                DispatchTo(lookup, requestBatch[i]);
                NotifyInboundObservers(requestBatch[i]);
            }
        }

        // ----- Raw inbound observers ---------------------------------------
        // Observers receive every inbound non-Response envelope after the typed
        // topic handlers run, on the Unity main thread. Used to bridge the bus to
        // an OutputCommandDispatcher in same-process composition roots.
        private readonly object _observerSync = new();
        private readonly List<Action<MessageEnvelope>> _inboundObservers = new();

        public IDisposable AddInboundObserver(Action<MessageEnvelope> observer)
        {
            if (observer is null) throw new ArgumentNullException(nameof(observer));
            lock (_observerSync)
            {
                _inboundObservers.Add(observer);
            }
            return new ObserverToken(this, observer);
        }

        private void RemoveInboundObserver(Action<MessageEnvelope> observer)
        {
            lock (_observerSync)
            {
                _inboundObservers.Remove(observer);
            }
        }

        private void NotifyInboundObservers(MessageEnvelope envelope)
        {
            Action<MessageEnvelope>[] snapshot;
            lock (_observerSync)
            {
                if (_inboundObservers.Count == 0) return;
                snapshot = _inboundObservers.ToArray();
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                try
                {
                    snapshot[i](envelope);
                }
                catch (Exception ex)
                {
                    _logError?.Invoke(
                        "Inbound observer threw for topic '" + envelope.Topic + "'.", ex);
                }
            }
        }

        private sealed class ObserverToken : IDisposable
        {
            private MainThreadDispatchQueue? _owner;
            private readonly Action<MessageEnvelope> _observer;

            public ObserverToken(MainThreadDispatchQueue owner, Action<MessageEnvelope> observer)
            {
                _owner = owner;
                _observer = observer;
            }

            public void Dispose()
            {
                _owner?.RemoveInboundObserver(_observer);
                _owner = null;
            }
        }

        private List<MessageEnvelope> SnapshotAndClearStateSlots()
        {
            var batch = new List<MessageEnvelope>(_stateSlots.Count);
            foreach (var key in _stateSlots.Keys)
            {
                if (_stateSlots.TryRemove(key, out var envelope))
                {
                    batch.Add(envelope);
                }
            }
            return batch;
        }

        private List<MessageEnvelope> DrainEventQueue()
        {
            var batch = new List<MessageEnvelope>();
            while (_eventQueue.TryDequeue(out var envelope))
            {
                batch.Add(envelope);
                _eventQueueDepthByTopic.AddOrUpdate(
                    envelope.Topic,
                    addValue: 0,
                    updateValueFactory: (_, current) => current > 0 ? current - 1 : 0);
            }
            return batch;
        }

        private List<MessageEnvelope> DrainRequestQueue()
        {
            var batch = new List<MessageEnvelope>();
            while (_requestQueue.TryDequeue(out var envelope))
            {
                batch.Add(envelope);
            }
            return batch;
        }

        private void DispatchTo(IDispatchHandlerLookup lookup, MessageEnvelope envelope)
        {
            if (!lookup.TryGetHandlers(envelope.Topic, envelope.Kind, out var handlers) || handlers is null)
            {
                return;
            }

            for (int i = 0; i < handlers.Count; i++)
            {
                var handler = handlers[i];
                if (handler is null) continue;

                try
                {
                    handler(envelope);
                }
                catch (Exception ex)
                {
                    var error = new CoreIpcError.HandlerException(
                        "topic='" + envelope.Topic + "', kind=" + envelope.Kind +
                        ", error=" + ex.GetType().Name + ": " + ex.Message);
                    _logError?.Invoke(error.Message, ex);
                }
            }
        }
    }
}
