#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VTuberSystemBase.CoreIpc.Abstractions;
using VTuberSystemBase.OutputRendererShell.Abstractions;
using VTuberSystemBase.OutputRendererShell.Diagnostics;
using VTuberSystemBase.OutputRendererShell.Dispatch;
using LogLevel = VTuberSystemBase.OutputRendererShell.Diagnostics.LogLevel;

namespace VTuberSystemBase.OutputRendererShell.EditModeTests
{
    /// <summary>
    /// Task 4.2: <see cref="OutputCommandDispatcher"/> の (a) 受信 → invoke 到達, (b) 未登録破棄, (c) kind 不整合破棄,
    /// (d) ハンドラ例外捕捉, (e) request → response 相関 ID 一致 を検証する（Req 3.5 / 3.6 / 4.5 / 4.6 / 4.7 / 5.5 / 9.4 / 9.5）。
    /// </summary>
    [TestFixture]
    public class OutputCommandDispatcherTests
    {
        private static MessageEnvelope MakeEnvelope(MessageKind kind, string topic, object? payload, string? correlationId = null)
        {
            JsonElement json;
            if (payload is null)
            {
                using var nullDoc = JsonDocument.Parse("null");
                json = nullDoc.RootElement.Clone();
            }
            else
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
                using var doc = JsonDocument.Parse(bytes);
                json = doc.RootElement.Clone();
            }
            return new MessageEnvelope(
                ProtocolVersion: "1.0",
                Kind: kind,
                Topic: topic,
                CorrelationId: correlationId,
                TimestampUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Payload: json);
        }

        [Test]
        [Description("(a) state ハンドラを登録 → 受信シミュレーションで invoke が到達すること（Req 3.2 / 4.5）")]
        public void RegisterStateHandler_ThenReceive_HandlerIsInvokedWithPayload()
        {
            var logger = new OutputShellLogger(LogLevel.Verbose);
            using var sut = new OutputCommandDispatcher(logger);

            string? captured = null;
            using var token = sut.RegisterStateHandler<string>("topic.state", cmd => captured = cmd.Payload);

            sut.OnEnvelopeReceived(MakeEnvelope(MessageKind.State, "topic.state", "hello"));

            Assert.AreEqual("hello", captured);
            Assert.AreEqual(1, sut.RegisteredHandlerCount);
        }

        [Test]
        [Description("event ハンドラを登録 → 受信シミュレーションで invoke が到達すること")]
        public void RegisterEventHandler_ThenReceive_HandlerIsInvoked()
        {
            var logger = new OutputShellLogger(LogLevel.Verbose);
            using var sut = new OutputCommandDispatcher(logger);

            int captured = 0;
            using var token = sut.RegisterEventHandler<int>("topic.event", cmd => captured = cmd.Payload);

            sut.OnEnvelopeReceived(MakeEnvelope(MessageKind.Event, "topic.event", 42));

            Assert.AreEqual(42, captured);
        }

        [Test]
        [Description("(b) 未登録 topic への受信は破棄され、警告ログが出ること（Req 3.5 / 9.4）")]
        public void OnEnvelopeReceived_UnregisteredTopic_LogsWarningAndDrops()
        {
            var logger = new OutputShellLogger(LogLevel.Verbose);
            using var sut = new OutputCommandDispatcher(logger);

            LogAssert.Expect(LogType.Warning,
                new Regex(@"no handler registered for topic kind=State"));

            sut.OnEnvelopeReceived(MakeEnvelope(MessageKind.State, "topic.unknown", "payload"));

            // 例外が伝搬しないこと
            Assert.AreEqual(0, sut.RegisteredHandlerCount);
        }

        [Test]
        [Description("(c) topic は登録済みだが kind が不一致な envelope は破棄され、kind mismatch 警告が出ること（Req 4.6）")]
        public void OnEnvelopeReceived_KindMismatch_LogsWarningAndDrops()
        {
            var logger = new OutputShellLogger(LogLevel.Verbose);
            using var sut = new OutputCommandDispatcher(logger);

            using var stateToken = sut.RegisterStateHandler<string>("topic.mixed", _ =>
            {
                Assert.Fail("state handler must not be invoked when envelope kind=Event");
            });

            LogAssert.Expect(LogType.Warning,
                new Regex(@"kind mismatch: received kind=Event"));

            sut.OnEnvelopeReceived(MakeEnvelope(MessageKind.Event, "topic.mixed", "payload"));
        }

        [Test]
        [Description("(d) ハンドラ例外は try/catch で捕捉され、診断ログが出てディスパッチャは継続すること（Req 3.6 / 5.5 / 9.5）")]
        public void HandlerThrows_DispatcherCatchesAndContinues()
        {
            var logger = new OutputShellLogger(LogLevel.Verbose);
            using var sut = new OutputCommandDispatcher(logger);

            int invocationCount = 0;
            using var token = sut.RegisterEventHandler<int>("topic.boom", cmd =>
            {
                invocationCount++;
                throw new InvalidOperationException("boom");
            });

            LogAssert.Expect(LogType.Error,
                new Regex(@"event handler threw; dispatcher continues\."));
            sut.OnEnvelopeReceived(MakeEnvelope(MessageKind.Event, "topic.boom", 1));

            // ディスパッチャ継続: 2 回目も呼べる
            LogAssert.Expect(LogType.Error,
                new Regex(@"event handler threw; dispatcher continues\."));
            sut.OnEnvelopeReceived(MakeEnvelope(MessageKind.Event, "topic.boom", 2));

            Assert.AreEqual(2, invocationCount, "ディスパッチャは例外後も次の受信を処理すること");
        }

        [Test]
        [Description("(e) request 受信 → ハンドラ呼び出し → 同一 correlationId の Response エンベロープが応答シンクへ送出されること（Req 3.8 / 4.7）")]
        public void RegisterRequestHandler_OnReceive_RespondsWithCorrelatedResponse()
        {
            var logger = new OutputShellLogger(LogLevel.Verbose);
            var sentResponses = new List<MessageEnvelope>();
            using var sut = new OutputCommandDispatcher(logger, env => sentResponses.Add(env));

            using var token = sut.RegisterRequestHandler<int, string>("topic.req", req =>
            {
                Assert.AreEqual("corr-1", req.CorrelationId);
                Assert.AreEqual(7, req.Payload);
                return $"echo:{req.Payload}";
            });

            sut.OnEnvelopeReceived(MakeEnvelope(MessageKind.Request, "topic.req", 7, correlationId: "corr-1"));

            Assert.AreEqual(1, sentResponses.Count, "応答シンクに 1 件送出されること");
            var resp = sentResponses[0];
            Assert.AreEqual(MessageKind.Response, resp.Kind);
            Assert.AreEqual("topic.req", resp.Topic);
            Assert.AreEqual("corr-1", resp.CorrelationId, "request と同一 correlationId で対応付けられること（Req 4.7）");
            Assert.AreEqual("echo:7", resp.Payload.GetString());
        }

        [Test]
        [Description("登録後トークンを Dispose すると RegisteredHandlerCount が減少し、以降の同 topic 受信は未登録扱いとなること（Req 3.3）")]
        public void TokenDispose_RemovesHandler_AndSubsequentReceiveIsDropped()
        {
            var logger = new OutputShellLogger(LogLevel.Verbose);
            using var sut = new OutputCommandDispatcher(logger);

            int invocationCount = 0;
            var token = sut.RegisterStateHandler<string>("topic.s", _ => invocationCount++);
            Assert.AreEqual(1, sut.RegisteredHandlerCount);

            token.Dispose();
            Assert.AreEqual(0, sut.RegisteredHandlerCount);

            LogAssert.Expect(LogType.Warning, new Regex(@"no handler registered for topic kind=State"));
            sut.OnEnvelopeReceived(MakeEnvelope(MessageKind.State, "topic.s", "v"));

            Assert.AreEqual(0, invocationCount);
        }

        [Test]
        [Description("Dispose 後の登録は InvalidOperationException を送出すること")]
        public void Register_AfterDispose_Throws()
        {
            var logger = new OutputShellLogger(LogLevel.Verbose);
            var sut = new OutputCommandDispatcher(logger);
            sut.Dispose();

            Assert.Throws<InvalidOperationException>(() =>
                sut.RegisterStateHandler<string>("topic.late", _ => { }));
        }

        [Test]
        [Description("Dispose 後の OnEnvelopeReceived は no-op（描画継続優先、Req 5.5）")]
        public void OnEnvelopeReceived_AfterDispose_IsNoOp()
        {
            var logger = new OutputShellLogger(LogLevel.Verbose);
            var sut = new OutputCommandDispatcher(logger);
            sut.Dispose();

            Assert.DoesNotThrow(() =>
                sut.OnEnvelopeReceived(MakeEnvelope(MessageKind.State, "topic.x", "v")));
        }

        [Test]
        [Description("Constructor で logger が null なら ArgumentNullException を送出する")]
        public void Constructor_NullLogger_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new OutputCommandDispatcher(logger: null!));
        }

        [Test]
        [Description("RegisterStateHandler は handler が null なら ArgumentNullException を送出する")]
        public void RegisterStateHandler_NullHandler_Throws()
        {
            using var sut = new OutputCommandDispatcher(new OutputShellLogger(LogLevel.Verbose));
            Assert.Throws<ArgumentNullException>(() =>
                sut.RegisterStateHandler<string>("topic.x", null!));
        }

        [Test]
        [Description("同一 (topic, kind) の重複登録は HandlerRegistry 由来で InvalidOperationException（Req 4.5）")]
        public void RegisterStateHandler_DuplicateTopicAndKind_Throws()
        {
            using var sut = new OutputCommandDispatcher(new OutputShellLogger(LogLevel.Verbose));
            using var first = sut.RegisterStateHandler<string>("topic.dup", _ => { });
            Assert.Throws<InvalidOperationException>(() =>
                sut.RegisterStateHandler<string>("topic.dup", _ => { }));
        }

        [Test]
        [Description("request handler 登録時に応答シンク未注入の場合、応答送信は警告で抑止される")]
        public void RegisterRequestHandler_WithoutResponseSink_LogsWarningWhenResponding()
        {
            var logger = new OutputShellLogger(LogLevel.Verbose);
            using var sut = new OutputCommandDispatcher(logger, responseSink: null);

            using var token = sut.RegisterRequestHandler<int, int>("topic.q", req => req.Payload * 2);

            LogAssert.Expect(LogType.Warning, new Regex(@"response sink is not configured"));
            sut.OnEnvelopeReceived(MakeEnvelope(MessageKind.Request, "topic.q", 3, correlationId: "c-q"));
        }

        [Test]
        [Description("responseSink:null で生成後に SetResponseSink で後付け注入すると、以降の request 応答がそのシンクへ送出される（composition root の後結線パスを担保）")]
        public void SetResponseSink_AfterNullConstruction_RoutesResponseToInjectedSink()
        {
            var logger = new OutputShellLogger(LogLevel.Verbose);
            using var sut = new OutputCommandDispatcher(logger, responseSink: null);

            var sentResponses = new List<MessageEnvelope>();
            sut.SetResponseSink(env => sentResponses.Add(env));

            using var token = sut.RegisterRequestHandler<int, string>("topic.late", req => $"echo:{req.Payload}");
            sut.OnEnvelopeReceived(MakeEnvelope(MessageKind.Request, "topic.late", 9, correlationId: "c-late"));

            Assert.AreEqual(1, sentResponses.Count, "後付けシンクに 1 件送出されること");
            var resp = sentResponses[0];
            Assert.AreEqual(MessageKind.Response, resp.Kind);
            Assert.AreEqual("topic.late", resp.Topic);
            Assert.AreEqual("c-late", resp.CorrelationId);
            Assert.AreEqual("echo:9", resp.Payload.GetString());
        }

        [Test]
        [Description("SetResponseSink(null) でシンクを外すと、以降の request 応答は警告で抑止される")]
        public void SetResponseSink_Null_SuppressesResponseWithWarning()
        {
            var logger = new OutputShellLogger(LogLevel.Verbose);
            var sentResponses = new List<MessageEnvelope>();
            using var sut = new OutputCommandDispatcher(logger, env => sentResponses.Add(env));

            sut.SetResponseSink(null);

            using var token = sut.RegisterRequestHandler<int, int>("topic.off", req => req.Payload * 2);

            LogAssert.Expect(LogType.Warning, new Regex(@"response sink is not configured"));
            sut.OnEnvelopeReceived(MakeEnvelope(MessageKind.Request, "topic.off", 4, correlationId: "c-off"));

            Assert.AreEqual(0, sentResponses.Count, "シンクを外した後は送出されないこと");
        }
    }
}
