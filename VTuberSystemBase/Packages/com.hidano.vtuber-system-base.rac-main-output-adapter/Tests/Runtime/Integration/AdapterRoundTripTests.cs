using System.Collections;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using RealtimeAvatarController.Avatar.Builtin;
using RealtimeAvatarController.Core;
using UnityEngine;
using UnityEngine.TestTools;
using VTuberSystemBase.CharacterSelectionTab.Contracts;
using VTuberSystemBase.CoreIpc.Abstractions;
using VTuberSystemBase.RacMainOutputAdapter.Bootstrapper;
using VTuberSystemBase.RacMainOutputAdapter.Tests.Doubles;

namespace VTuberSystemBase.RacMainOutputAdapter.Tests.Integration
{
    /// <summary>
    /// <see cref="RacMainOutputAdapterBootstrapper"/> を <see cref="InMemoryDispatcher"/> +
    /// <see cref="InMemoryProviderRegistry"/> + <see cref="InMemoryMoCapSourceRegistry"/> で組み立てた状態で
    /// assignment 経路の往復を検証する（Requirement 11.1, 11.2, 11.5）。
    /// </summary>
    public sealed class AdapterRoundTripTests
    {
        private RacRegistryFixture _fixture;
        private InMemoryDispatcher _dispatcher;
        private RecordingMessageSink _sink;
        private InMemoryAvatarKeyResolver _keyResolver;
        private InMemoryAvatarSchemaProvider _schemaProvider;
        private RecordingAvatarSettingsAdapter _settingsAdapter;
        private RacMainOutputAdapterBootstrapper _boot;

        [SetUp]
        public void SetUp()
        {
            _fixture = new RacRegistryFixture();
            _fixture.SetUp(providerTypeId: BuiltinAvatarProviderFactory.BuiltinProviderTypeId, moCapTypeId: "Stub");

            _dispatcher = new InMemoryDispatcher();
            _sink = new RecordingMessageSink(_dispatcher);
            _keyResolver = new InMemoryAvatarKeyResolver();
            _schemaProvider = new InMemoryAvatarSchemaProvider();
            _settingsAdapter = new RecordingAvatarSettingsAdapter();

            // 事前: avatarKey "miku" / "rin" を登録
            var mikuConfig = ScriptableObject.CreateInstance<BuiltinAvatarProviderConfig>();
            mikuConfig.name = "miku";
            mikuConfig.avatarPrefab = null; // StubAvatarProvider は config を見ないので null OK
            var rinConfig = ScriptableObject.CreateInstance<BuiltinAvatarProviderConfig>();
            rinConfig.name = "rin";
            rinConfig.avatarPrefab = null;

            _keyResolver.SetEntries(new System.Collections.Generic.Dictionary<string, AvatarProviderDescriptor>
            {
                ["miku"] = new AvatarProviderDescriptor { ProviderTypeId = BuiltinAvatarProviderFactory.BuiltinProviderTypeId, Config = mikuConfig },
                ["rin"] = new AvatarProviderDescriptor { ProviderTypeId = BuiltinAvatarProviderFactory.BuiltinProviderTypeId, Config = rinConfig },
            });

            _boot = new RacMainOutputAdapterBootstrapper();
            _boot.OverrideServices(
                dispatcher: _dispatcher,
                messageSink: _sink,
                keyResolver: _keyResolver,
                schemaProvider: _schemaProvider,
                settingsAdapter: _settingsAdapter,
                mocapFactory: new VTuberSystemBase.RacMainOutputAdapter.Defaults.StubMoCapSourceConfigFactory(),
                logger: new FakeDiagnosticsLogger());
            _boot.Initialize();
        }

        [TearDown]
        public void TearDown()
        {
            _boot?.Dispose();
            _dispatcher?.Dispose();
            _fixture?.TearDown();
        }

        [Test]
        public void SlotManager_PropertyExposesInitializedInstanceAndClearsOnShutdown()
        {
            Assert.That(_boot.SlotManager, Is.Not.Null);

            _boot.Shutdown();

            Assert.That(_boot.SlotManager, Is.Null);
        }

        [UnityTest]
        public IEnumerator Assignment_AssignsSlot_PublishesAssigningAndAssigned() => UniTask.ToCoroutine(async () =>
        {
            // 事前条件: catalog 初回 publish が flush 済（assignment ハンドラはまだ動的登録されていない）
            // 本テストでは Slot は動的登録されてから受信するパスを検証するため、
            // RegisterDynamic を直接呼び（Slot 登録の発火の代用）assignment を流す。
            _boot.GetType(); // sanity

            // 既存 SlotManager に Slot を仮追加することで動的登録イベントを起動
            // → 代わりに、Bootstrapper の挙動として「assignment topic を直接登録」を直接やる：
            //   InMemoryDispatcher は実は動的登録を待たず登録できる。assignment は OnSlotAdded 経由で登録される。
            //   そこで slotId を assignment 経由で初発するシナリオは「未登録 slotId への assignment は受信できない」。
            //   そこで本テストでは: SlotCatalogPublisher が Slot を観測した後（つまり1度 Slot を作って消す）assignment を投げる。

            // 簡略化: Slot を 1 件追加して RegisterDynamic を発火させる
            var providerReg = _fixture.ProviderRegistry;
            var sourceReg = _fixture.MoCapSourceRegistry;
            // RegisterDynamic は Bootstrapper.OnSlotAdded で起動するため、SlotManager に slot を追加する。
            var sm = GetSlotManager();
            var settings = ScriptableObject.CreateInstance<SlotSettings>();
            settings.slotId = "A1";
            settings.displayName = "A1";
            settings.weight = 1f;
            settings.avatarProviderDescriptor = _keyResolver.Resolve("miku");
            settings.moCapSourceDescriptor = new VTuberSystemBase.RacMainOutputAdapter.Defaults.StubMoCapSourceConfigFactory().Build("A1");
            await sm.AddSlotAsync(settings);

            // OnSlotAdded → assignmentApplier.RegisterDynamic("A1") が起動済
            // 確認: dispatcher に slot/A1/assignment ハンドラが登録されている
            Assert.That(_dispatcher.HasHandler(CharacterTopics.SlotAssignment("A1"), MessageKind.State), Is.True);

            // 一旦削除して empty 状態に戻す
            _sink.PublishState("dummy", "ignore"); // sink ガード（空送信はテスト無関係）
            await sm.RemoveSlotAsync("A1");

            // 動的登録は Disposed 通知で UnregisterDynamic され、次の Add で再登録される
            // 改めて「UI から assignment」シナリオを再現するため、単純に InMemoryDispatcher.EmitState する
            // 動的登録復活のために既存 Slot を再登録 → RegisterDynamic("A1") を強制
            _boot.GetType(); // dummy

            // Slot 再登録（assignment 経路をテストするため）
            settings = ScriptableObject.CreateInstance<SlotSettings>();
            settings.slotId = "A1";
            settings.displayName = "A1";
            settings.weight = 1f;
            settings.avatarProviderDescriptor = _keyResolver.Resolve("miku");
            settings.moCapSourceDescriptor = new VTuberSystemBase.RacMainOutputAdapter.Defaults.StubMoCapSourceConfigFactory().Build("A1");
            await sm.AddSlotAsync(settings);
            Assert.That(_dispatcher.HasHandler(CharacterTopics.SlotAssignment("A1"), MessageKind.State), Is.True);

            // sink の履歴をクリアし、assignment を流して確認
            int beforeCount = _sink.Entries.Count;
            _dispatcher.EmitState(CharacterTopics.SlotAssignment("A1"), new SlotAssignmentPayload { AvatarKey = "rin" });

            // assignment の HandleStateAsync は Forget() で非同期実行されるので少し待つ
            for (int i = 0; i < 30 && _sink.Entries.Count - beforeCount < 2; i++)
            {
                await UniTask.Yield();
            }

            // status: Assigning → Assigned （差替なので）
            var statusTopic = CharacterTopics.SlotStatus("A1");
            int statusCount = 0;
            string lastStatus = null;
            foreach (var e in _sink.Entries)
            {
                if (e.Topic == statusTopic && e.Payload is SlotStatusPayload sp)
                {
                    statusCount++;
                    lastStatus = sp.Status;
                }
            }
            Assert.That(statusCount, Is.GreaterThanOrEqualTo(2));
            Assert.That(lastStatus, Is.EqualTo("Assigned"));
        });

        [UnityTest]
        public IEnumerator Assignment_UnknownAvatarKey_PublishesError() => UniTask.ToCoroutine(async () =>
        {
            var sm = GetSlotManager();
            var settings = ScriptableObject.CreateInstance<SlotSettings>();
            settings.slotId = "B1";
            settings.displayName = "B1";
            settings.weight = 1f;
            settings.avatarProviderDescriptor = _keyResolver.Resolve("miku");
            settings.moCapSourceDescriptor = new VTuberSystemBase.RacMainOutputAdapter.Defaults.StubMoCapSourceConfigFactory().Build("B1");
            await sm.AddSlotAsync(settings);

            // 動的登録復活のため一旦削除して再追加
            await sm.RemoveSlotAsync("B1");
            settings = ScriptableObject.CreateInstance<SlotSettings>();
            settings.slotId = "B1";
            settings.displayName = "B1";
            settings.weight = 1f;
            settings.avatarProviderDescriptor = _keyResolver.Resolve("miku");
            settings.moCapSourceDescriptor = new VTuberSystemBase.RacMainOutputAdapter.Defaults.StubMoCapSourceConfigFactory().Build("B1");
            await sm.AddSlotAsync(settings);

            int before = _sink.Entries.Count;
            _dispatcher.EmitState(CharacterTopics.SlotAssignment("B1"), new SlotAssignmentPayload { AvatarKey = "unknown" });
            for (int i = 0; i < 30 && _sink.Entries.Count - before < 2; i++)
            {
                await UniTask.Yield();
            }

            var errorTopic = CharacterTopics.SlotError("B1");
            bool errorPublished = false;
            foreach (var e in _sink.Entries)
            {
                if (e.Topic == errorTopic && e.Kind == MessageKind.Event && e.Payload is SlotErrorPayload sp
                    && sp.ErrorCode == "KeyNotFound")
                {
                    errorPublished = true;
                    break;
                }
            }
            Assert.That(errorPublished, Is.True, "slot/B1/error{KeyNotFound} should be published.");
        });

        private SlotManager GetSlotManager()
        {
            // Bootstrapper の internal フィールドを reflection で取得（Tests.Runtime は InternalsVisibleTo されている）
            var field = typeof(RacMainOutputAdapterBootstrapper).GetField("_slotManager",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (SlotManager)field!.GetValue(_boot);
        }
    }
}
