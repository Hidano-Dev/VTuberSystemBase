#nullable enable
using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using VTuberSystemBase.CoreIpc.Abstractions;
using VTuberSystemBase.OutputRendererShell.Abstractions;
using VTuberSystemBase.OutputRendererShell.Diagnostics;

namespace VTuberSystemBase.OutputRendererShell.Dispatch
{
    /// <summary>
    /// <see cref="IOutputCommandDispatcher"/> の実装。
    /// 内部に <see cref="HandlerRegistry"/> を持ち、登録時に <c>(topic, kind)</c> 重複を Fail-Fast で検出し、
    /// 受信エンベロープに対して kind 二重検証 → ハンドラ呼び出し（<c>try/catch</c> ラップ） → 例外時の診断ログ
    /// を実行する（Req 3.2 / 3.3 / 3.5 / 3.6 / 4.5 / 4.6 / 5.5 / 9.3 / 9.4 / 9.5）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 受信側のキューイング・coalesce・FIFO 並べ替え・相関 ID 解決は本クラスでは行わず、すべて上流
    /// <c>core-ipc-foundation</c>（D-7 / D-10）契約をそのまま継承する（Req 4.2 / 4.3 / 4.7 / 4.8 / 4.9）。
    /// </para>
    /// <para>
    /// 受信エントリポイントは <see cref="OnEnvelopeReceived"/>。本 spec のブートストラッパー（Task 6.2）が
    /// 上流 <c>ICoreIpcBus</c> またはその直下の envelope 受信パスをこのメソッドへ繋ぎ込む。テストでは本メソッドを
    /// 直接呼び出してハンドラ呼び出しを検証する。
    /// </para>
    /// <para>
    /// <strong>スレッディング</strong>: Unity メインスレッド前提（D-3）。<see cref="HandlerRegistry"/> は
    /// スレッドセーフでない。登録／受信／Dispose はメインスレッドからのみ呼び出すこと。
    /// </para>
    /// </remarks>
    public sealed class OutputCommandDispatcher : IOutputCommandDispatcher
    {
        private const string ProtocolVersion = "1.0";
        private const string ComponentName = "OutputCommandDispatcher";

        private readonly HandlerRegistry _registry = new();
        private readonly OutputShellLogger _logger;
        private readonly Action<MessageEnvelope>? _responseSink;
        private readonly JsonSerializerOptions _serializerOptions;
        private bool _disposed;

        /// <summary>
        /// <see cref="OutputCommandDispatcher"/> を生成する。
        /// </summary>
        /// <param name="logger">診断ログ出力先（Req 9.3 / 9.4 / 9.5）。null 不可。</param>
        /// <param name="responseSink">
        /// request ハンドラの戻り値を <see cref="MessageKind.Response"/> エンベロープへ詰めて送信するためのシンク。
        /// null の場合、Request ハンドラ呼び出し後の応答送信は警告ログを残して抑止される。
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="logger"/> が null。</exception>
        public OutputCommandDispatcher(OutputShellLogger logger, Action<MessageEnvelope>? responseSink = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _responseSink = responseSink;
            _serializerOptions = CreateSerializerOptions();
        }

        /// <inheritdoc />
        public int RegisteredHandlerCount => _registry.Count;

        /// <summary>
        /// True when at least one handler (any kind) is registered for <paramref name="topic"/>.
        /// A same-process bus→dispatcher bridge uses this to forward only envelopes this
        /// dispatcher can handle, avoiding "no handler" warnings for unrelated traffic.
        /// </summary>
        public bool HasHandlerFor(string topic)
            => !string.IsNullOrEmpty(topic) && _registry.HasAnyForTopic(topic);

        /// <inheritdoc />
        public OutputCommandHandlerRegistration RegisterStateHandler<TPayload>(string topic, Action<StateCommand<TPayload>> handler)
        {
            ThrowIfDisposed();
            if (handler is null) throw new ArgumentNullException(nameof(handler));

            Action<MessageEnvelope> wrapped = env => InvokeStateHandler(env, handler);
            var token = _registry.Register(topic, OutputCommandKind.State, wrapped);
            _logger.Verbose("registered state handler", ComponentName, topic);
            return token;
        }

        /// <inheritdoc />
        public OutputCommandHandlerRegistration RegisterEventHandler<TPayload>(string topic, Action<EventCommand<TPayload>> handler)
        {
            ThrowIfDisposed();
            if (handler is null) throw new ArgumentNullException(nameof(handler));

            Action<MessageEnvelope> wrapped = env => InvokeEventHandler(env, handler);
            var token = _registry.Register(topic, OutputCommandKind.Event, wrapped);
            _logger.Verbose("registered event handler", ComponentName, topic);
            return token;
        }

        /// <inheritdoc />
        public OutputCommandHandlerRegistration RegisterRequestHandler<TRequest, TResponse>(string topic, Func<RequestCommand<TRequest>, TResponse> handler)
        {
            ThrowIfDisposed();
            if (handler is null) throw new ArgumentNullException(nameof(handler));

            Action<MessageEnvelope> wrapped = env => InvokeRequestHandler(env, handler);
            var token = _registry.Register(topic, OutputCommandKind.Request, wrapped);
            _logger.Verbose("registered request handler", ComponentName, topic);
            return token;
        }

        /// <summary>
        /// 受信エンベロープをディスパッチする。<c>(topic, kind)</c> ルックアップ → kind 二重検証 → ハンドラ呼び出し
        /// （内部で <c>try/catch</c> 済み）→ 未登録／kind 不一致の警告ログを実施する（Req 3.5 / 4.6 / 9.4）。
        /// </summary>
        /// <param name="envelope">上流 <c>core-ipc-foundation</c> から受信したメッセージエンベロープ。</param>
        /// <remarks>
        /// Dispose 済みディスパッチャに対する呼び出しは何もしない（描画継続優先、Req 5.5）。
        /// 本メソッドは PlayMode テストの「受信シミュレーション」エントリでもある。
        /// </remarks>
        public void OnEnvelopeReceived(MessageEnvelope envelope)
        {
            if (_disposed) return;

            if (string.IsNullOrEmpty(envelope.Topic))
            {
                _logger.Warning("dropped envelope: empty topic.", ComponentName, topic: null, correlationId: envelope.CorrelationId);
                return;
            }

            var kind = MapKind(envelope.Kind);
            if (_registry.TryGet(envelope.Topic!, kind, out var handler))
            {
                ((Action<MessageEnvelope>)handler).Invoke(envelope);
                return;
            }

            if (_registry.HasAnyForTopic(envelope.Topic!))
            {
                _logger.Warning(
                    $"kind mismatch: received kind={envelope.Kind} but no handler is registered for that kind on this topic; dropping.",
                    ComponentName, envelope.Topic, envelope.CorrelationId);
            }
            else
            {
                _logger.Warning(
                    $"no handler registered for topic kind={envelope.Kind}; dropping.",
                    ComponentName, envelope.Topic, envelope.CorrelationId);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _registry.Clear();
        }

        private void InvokeStateHandler<TPayload>(MessageEnvelope env, Action<StateCommand<TPayload>> handler)
        {
            if (!TryDeserializePayload<TPayload>(env, out var payload)) return;
            var cmd = new StateCommand<TPayload>
            {
                Topic = env.Topic,
                Payload = payload,
                ReceivedAtTicks = DateTime.UtcNow.Ticks,
            };
            try
            {
                handler(cmd);
            }
            catch (Exception ex)
            {
                _logger.Error("state handler threw; dispatcher continues.", ex, ComponentName, env.Topic, env.CorrelationId);
            }
        }

        private void InvokeEventHandler<TPayload>(MessageEnvelope env, Action<EventCommand<TPayload>> handler)
        {
            if (!TryDeserializePayload<TPayload>(env, out var payload)) return;
            var cmd = new EventCommand<TPayload>
            {
                Topic = env.Topic,
                Payload = payload,
                ReceivedAtTicks = DateTime.UtcNow.Ticks,
            };
            try
            {
                handler(cmd);
            }
            catch (Exception ex)
            {
                _logger.Error("event handler threw; dispatcher continues.", ex, ComponentName, env.Topic, env.CorrelationId);
            }
        }

        private void InvokeRequestHandler<TRequest, TResponse>(MessageEnvelope env, Func<RequestCommand<TRequest>, TResponse> handler)
        {
            if (!TryDeserializePayload<TRequest>(env, out var payload)) return;
            var cmd = new RequestCommand<TRequest>
            {
                Topic = env.Topic,
                CorrelationId = env.CorrelationId,
                Payload = payload,
                ReceivedAtTicks = DateTime.UtcNow.Ticks,
            };

            TResponse response;
            try
            {
                response = handler(cmd);
            }
            catch (Exception ex)
            {
                _logger.Error("request handler threw; dispatcher continues, response not sent.", ex, ComponentName, env.Topic, env.CorrelationId);
                return;
            }

            if (_responseSink is null)
            {
                _logger.Warning("response sink is not configured; response is dropped.", ComponentName, env.Topic, env.CorrelationId);
                return;
            }

            JsonElement responsePayload;
            try
            {
                responsePayload = SerializeAsJsonElement(response);
            }
            catch (Exception ex)
            {
                _logger.Error("response serialization failed; response not sent.", ex, ComponentName, env.Topic, env.CorrelationId);
                return;
            }

            var responseEnvelope = new MessageEnvelope(
                ProtocolVersion: ProtocolVersion,
                Kind: MessageKind.Response,
                Topic: env.Topic,
                CorrelationId: env.CorrelationId,
                TimestampUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Payload: responsePayload);

            try
            {
                _responseSink(responseEnvelope);
            }
            catch (Exception ex)
            {
                _logger.Error("response sink threw while sending response.", ex, ComponentName, env.Topic, env.CorrelationId);
            }
        }

        private bool TryDeserializePayload<T>(MessageEnvelope env, out T? payload)
        {
            try
            {
                if (env.Payload.ValueKind == JsonValueKind.Undefined || env.Payload.ValueKind == JsonValueKind.Null)
                {
                    payload = default;
                    return true;
                }

                payload = env.Payload.Deserialize<T>(_serializerOptions);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error("payload deserialization failed; dropping command.", ex, ComponentName, env.Topic, env.CorrelationId);
                payload = default;
                return false;
            }
        }

        private JsonElement SerializeAsJsonElement<T>(T value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, _serializerOptions);
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.Clone();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new InvalidOperationException($"{nameof(OutputCommandDispatcher)} is disposed.");
            }
        }

        private static OutputCommandKind MapKind(MessageKind kind) => kind switch
        {
            MessageKind.State => OutputCommandKind.State,
            MessageKind.Event => OutputCommandKind.Event,
            MessageKind.Request => OutputCommandKind.Request,
            MessageKind.Response => OutputCommandKind.Response,
            _ => OutputCommandKind.State,
        };

        private static JsonSerializerOptions CreateSerializerOptions()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = false,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
            return options;
        }
    }
}
