#nullable enable
using System;
using System.Collections.Generic;
using NUnit.Framework;
using VTuberSystemBase.CoreIpc.Abstractions;
using VTuberSystemBase.UiToolkitShell.Commands;
using VTuberSystemBase.UiToolkitShell.Tests.TestSupport;

namespace VTuberSystemBase.UiToolkitShell.Tests.Runtime
{
    /// <summary>
    /// Task 4.1 (Red): <c>IConnectionStatus</c> 契約と状態遷移、メインスレッド発火、
    /// <c>ConnectionStatusEvent</c> の振る舞いを TDD で固定する。 4.2 で
    /// <c>IConnectionStatus</c> / <c>ConnectionStatus</c> / <c>ConnectionStatusEvent</c>
    /// を実装するまでは「型未定義」（CS0246）で失敗する。
    /// design.md §Commands §IConnectionStatus 参照（Requirements 5.9, 9.3, 9.5, 11.6）。
    /// 期待する状態遷移:
    /// <c>Initializing → Connecting → Connected → Disconnected → Reconnecting → FailedPermanently</c>。
    /// core-ipc-foundation の <c>IConnectionDiagnostics.ConnectionStateChanged</c> を
    /// 起点に、UI 側の 6 状態へ一方向変換するアダプタ契約を検証する。
    /// </summary>
    [TestFixture]
    public sealed class ConnectionStatusContractTests
    {
        [SetUp]
        public void SetUp()
        {
            MainThreadAffinity.Capture();
        }

        [TearDown]
        public void TearDown()
        {
            MainThreadAffinity.Reset();
        }

        [Test]
        [Description("構築直後の状態は Initializing で IsConnected=false（design.md §IConnectionStatus State Management; PlayMode 開始のたびに Initializing から）")]
        public void InitialState_IsInitializing_AndNotConnected()
        {
            var fake = new FakeIpcClient();

            IConnectionStatus status = new ConnectionStatus(fake);

            Assert.That(status.CurrentStatus, Is.EqualTo(ConnectionStatusCode.Initializing));
            Assert.That(status.IsConnected, Is.False);
        }

        [Test]
        [Description("core-ipc Disconnected→Connecting で UI 側は Initializing→Connecting に遷移し、OnStatusChanged が発火する")]
        public void Transition_FromInitializingToConnecting_FiresEventAndUpdatesCurrentStatus()
        {
            var fake = new FakeIpcClient();
            IConnectionStatus status = new ConnectionStatus(fake);
            ConnectionStatusEvent? observed = null;
            status.OnStatusChanged += e => observed = e;

            fake.SetConnectionState(ConnectionState.Connecting);

            Assert.That(observed.HasValue, Is.True, "OnStatusChanged must fire on first observed core-ipc transition");
            Assert.That(observed!.Value.From, Is.EqualTo(ConnectionStatusCode.Initializing));
            Assert.That(observed.Value.To, Is.EqualTo(ConnectionStatusCode.Connecting));
            Assert.That(status.CurrentStatus, Is.EqualTo(ConnectionStatusCode.Connecting));
            Assert.That(status.IsConnected, Is.False);
        }

        [Test]
        [Description("core-ipc Connecting→Connected で UI 側は Connecting→Connected に遷移し、IsConnected が true になる")]
        public void Transition_ToConnected_SetsIsConnectedTrue()
        {
            var fake = new FakeIpcClient();
            IConnectionStatus status = new ConnectionStatus(fake);
            ConnectionStatusEvent? observed = null;

            fake.SetConnectionState(ConnectionState.Connecting);
            status.OnStatusChanged += e => observed = e;
            fake.SetConnectionState(ConnectionState.Connected);

            Assert.That(observed.HasValue, Is.True);
            Assert.That(observed!.Value.From, Is.EqualTo(ConnectionStatusCode.Connecting));
            Assert.That(observed.Value.To, Is.EqualTo(ConnectionStatusCode.Connected));
            Assert.That(status.CurrentStatus, Is.EqualTo(ConnectionStatusCode.Connected));
            Assert.That(status.IsConnected, Is.True);
        }

        [Test]
        [Description("Connected→Disconnected で UI 側は Connected→Disconnected に遷移し、IsConnected が false に戻る")]
        public void Transition_ConnectedToDisconnected_DropsIsConnected()
        {
            var fake = new FakeIpcClient();
            IConnectionStatus status = new ConnectionStatus(fake);
            fake.SetConnectionState(ConnectionState.Connecting);
            fake.SetConnectionState(ConnectionState.Connected);

            ConnectionStatusEvent? observed = null;
            status.OnStatusChanged += e => observed = e;
            fake.SetConnectionState(ConnectionState.Disconnected);

            Assert.That(observed.HasValue, Is.True);
            Assert.That(observed!.Value.From, Is.EqualTo(ConnectionStatusCode.Connected));
            Assert.That(observed.Value.To, Is.EqualTo(ConnectionStatusCode.Disconnected));
            Assert.That(status.CurrentStatus, Is.EqualTo(ConnectionStatusCode.Disconnected));
            Assert.That(status.IsConnected, Is.False);
        }

        [Test]
        [Description("Disconnected→Reconnecting と Reconnecting→PermanentlyDisconnected を UI 側 Reconnecting / FailedPermanently に変換し、5 件の遷移が記録される")]
        public void Transition_FullPath_RecordsFiveTransitions_EndingAtFailedPermanently()
        {
            var fake = new FakeIpcClient();
            IConnectionStatus status = new ConnectionStatus(fake);
            var transitions = new List<ConnectionStatusEvent>();
            status.OnStatusChanged += e => transitions.Add(e);

            fake.SetConnectionState(ConnectionState.Connecting);
            fake.SetConnectionState(ConnectionState.Connected);
            fake.SetConnectionState(ConnectionState.Disconnected);
            fake.SetConnectionState(ConnectionState.Reconnecting);
            fake.SetConnectionState(ConnectionState.PermanentlyDisconnected);

            Assert.That(transitions.Count, Is.EqualTo(5),
                "Initializing→Connecting→Connected→Disconnected→Reconnecting→FailedPermanently で 5 件の遷移が観測される");
            Assert.That(transitions[0].From, Is.EqualTo(ConnectionStatusCode.Initializing));
            Assert.That(transitions[0].To, Is.EqualTo(ConnectionStatusCode.Connecting));
            Assert.That(transitions[1].From, Is.EqualTo(ConnectionStatusCode.Connecting));
            Assert.That(transitions[1].To, Is.EqualTo(ConnectionStatusCode.Connected));
            Assert.That(transitions[2].From, Is.EqualTo(ConnectionStatusCode.Connected));
            Assert.That(transitions[2].To, Is.EqualTo(ConnectionStatusCode.Disconnected));
            Assert.That(transitions[3].From, Is.EqualTo(ConnectionStatusCode.Disconnected));
            Assert.That(transitions[3].To, Is.EqualTo(ConnectionStatusCode.Reconnecting));
            Assert.That(transitions[4].From, Is.EqualTo(ConnectionStatusCode.Reconnecting));
            Assert.That(transitions[4].To, Is.EqualTo(ConnectionStatusCode.FailedPermanently));
            Assert.That(status.CurrentStatus, Is.EqualTo(ConnectionStatusCode.FailedPermanently));
            Assert.That(status.IsConnected, Is.False);
        }

        [Test]
        [Description("OnStatusChanged はメインスレッド上で発火する（D-3 の継承; design.md §IConnectionStatus Concurrency strategy）")]
        public void OnStatusChanged_FiresOnMainThread()
        {
            var fake = new FakeIpcClient();
            IConnectionStatus status = new ConnectionStatus(fake);
            var recorder = new MainThreadAffinity.Recorder();
            status.OnStatusChanged += _ => recorder.Record();

            fake.SetConnectionState(ConnectionState.Connecting);

            Assert.That(recorder.WasInvoked, Is.True);
            Assert.That(recorder.Matches(MainThreadAffinity.CapturedThreadId), Is.True,
                "IConnectionStatus.OnStatusChanged must dispatch on the captured main thread");
        }

        [Test]
        [Description("ConnectionStatusEvent は From / To / At / Detail フィールドを保持し、At は通知時刻近傍の値となる")]
        public void ConnectionStatusEvent_FieldsPopulatedWithFromToAtAndDetail()
        {
            var fake = new FakeIpcClient();
            IConnectionStatus status = new ConnectionStatus(fake);
            ConnectionStatusEvent? observed = null;
            status.OnStatusChanged += e => observed = e;

            var before = DateTimeOffset.UtcNow.AddSeconds(-1);
            fake.SetConnectionState(ConnectionState.Connecting);
            var after = DateTimeOffset.UtcNow.AddSeconds(1);

            Assert.That(observed.HasValue, Is.True);
            Assert.That(observed!.Value.From, Is.EqualTo(ConnectionStatusCode.Initializing));
            Assert.That(observed.Value.To, Is.EqualTo(ConnectionStatusCode.Connecting));
            Assert.That(observed.Value.At, Is.GreaterThanOrEqualTo(before));
            Assert.That(observed.Value.At, Is.LessThanOrEqualTo(after));
            // Detail はオプション (string?). 未指定時に null を許容する型シグネチャであれば良い。
            string? detail = observed.Value.Detail;
            _ = detail;
        }

        [Test]
        [Description("同一状態への重複通知では OnStatusChanged が発火しない（state ノイズ抑制）")]
        public void Transition_DuplicateState_DoesNotFireEvent()
        {
            var fake = new FakeIpcClient();
            IConnectionStatus status = new ConnectionStatus(fake);
            fake.SetConnectionState(ConnectionState.Connecting);
            fake.SetConnectionState(ConnectionState.Connected);

            var fireCount = 0;
            status.OnStatusChanged += _ => fireCount++;

            // 同一の Connected を再度設定しても、core-ipc 側で同状態は無視されるため
            // UI 側 OnStatusChanged も発火しないこと。
            fake.SetConnectionState(ConnectionState.Connected);

            Assert.That(fireCount, Is.EqualTo(0));
            Assert.That(status.CurrentStatus, Is.EqualTo(ConnectionStatusCode.Connected));
        }

        [Test]
        [Description("ICoreIpcBus を null で渡した場合は ArgumentNullException")]
        public void Constructor_NullBus_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new ConnectionStatus(null!));
        }

        [Test]
        [Description("購読開始前に既に Connected まで遷移済み（loopback で一瞬で接続）の場合、構築時に latched 状態を反映して Initializing に取り残されない（接続バッジ永久 Initializing バグの回帰防止）")]
        public void Construction_WhenAlreadyConnected_ReflectsLatchedState()
        {
            var fake = new FakeIpcClient();
            fake.SetConnectionState(ConnectionState.Connecting);
            fake.SetConnectionState(ConnectionState.Connected);

            IConnectionStatus status = new ConnectionStatus(fake);

            Assert.That(status.CurrentStatus, Is.EqualTo(ConnectionStatusCode.Connected),
                "購読前に完了していた Connected 遷移を latched 反映で拾うこと");
            Assert.That(status.IsConnected, Is.True);
        }

        [Test]
        [Description("CurrentState=Disconnected（未接続の初期値）で構築した場合は Initializing grace を維持する（Disconnected と初期値は区別できないため）")]
        public void Construction_WhenDisconnected_StaysInitializing()
        {
            var fake = new FakeIpcClient(); // デフォルト CurrentState = Disconnected

            IConnectionStatus status = new ConnectionStatus(fake);

            Assert.That(status.CurrentStatus, Is.EqualTo(ConnectionStatusCode.Initializing));
            Assert.That(status.IsConnected, Is.False);
        }

        [Test]
        [Description("購読前に Reconnecting 等の非 Disconnected 状態だった場合も latched 反映される")]
        public void Construction_WhenReconnecting_ReflectsLatchedState()
        {
            var fake = new FakeIpcClient();
            fake.SetConnectionState(ConnectionState.Connecting);
            fake.SetConnectionState(ConnectionState.Connected);
            fake.SetConnectionState(ConnectionState.Disconnected);
            fake.SetConnectionState(ConnectionState.Reconnecting);

            IConnectionStatus status = new ConnectionStatus(fake);

            Assert.That(status.CurrentStatus, Is.EqualTo(ConnectionStatusCode.Reconnecting));
            Assert.That(status.IsConnected, Is.False);
        }
    }
}
