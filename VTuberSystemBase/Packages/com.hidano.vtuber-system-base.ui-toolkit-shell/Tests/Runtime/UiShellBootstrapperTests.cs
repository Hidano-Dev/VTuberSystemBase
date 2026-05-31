#nullable enable
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using VTuberSystemBase.CoreIpc.Abstractions;
using VTuberSystemBase.UiToolkitShell.AssetLoading;
using VTuberSystemBase.UiToolkitShell.Bootstrap;
using VTuberSystemBase.UiToolkitShell.Diagnostics;
using VTuberSystemBase.UiToolkitShell.Panels;
using VTuberSystemBase.UiToolkitShell.Skin;
using VTuberSystemBase.UiToolkitShell.Tests.TestSupport;

namespace VTuberSystemBase.UiToolkitShell.Tests.Runtime
{
    /// <summary>
    /// Task 10.1: <see cref="UiShellBootstrapper"/> Composition Root の初期化順序、
    /// 失敗パスの BootstrapErrorCode 返却、StopShell の逆順 Dispose、
    /// IPC 接続未確立時の起動完遂（Requirement 9.1）を結合テストで固定する
    /// （design.md §Bootstrap §UiShellBootstrapper）。
    /// </summary>
    [TestFixture]
    public sealed class UiShellBootstrapperTests
    {
        private RecordingDiagnosticsLogger _logger = null!;
        private FakeIpcClient _bus = null!;
        private FakeRootUiDocumentFactory _rootFactory = null!;
        private FakeTabMountStrategy _tabMount = null!;
        private FakeAddressablesInitializer _addressables = null!;
        private UiToolkitShellSkinProfile _skin = null!;
        private List<UnityEngine.Object> _disposables = null!;

        [SetUp]
        public void SetUp()
        {
            _logger = new RecordingDiagnosticsLogger();
            _bus = new FakeIpcClient();
            _rootFactory = new FakeRootUiDocumentFactory();
            _tabMount = new FakeTabMountStrategy();
            _addressables = new FakeAddressablesInitializer();
            _skin = ScriptableObject.CreateInstance<UiToolkitShellSkinProfile>();
            _skin.RootVisualTreeAsset = ScriptableObject.CreateInstance<UnityEngine.UIElements.VisualTreeAsset>();
            _disposables = new List<UnityEngine.Object>
            {
                _skin,
                _skin.RootVisualTreeAsset,
            };
        }

        [TearDown]
        public void TearDown()
        {
            for (var i = _disposables.Count - 1; i >= 0; i--)
            {
                if (_disposables[i] != null) UnityEngine.Object.DestroyImmediate(_disposables[i]);
            }
            _disposables.Clear();
        }

        // ---- helpers ----------------------------------------------------

        private UiShellConfig MakeConfig()
        {
            return new UiShellConfig
            {
                SkinProfile = _skin,
                IpcBus = _bus,
                TabMountStrategy = _tabMount,
                AddressablesInitializer = _addressables,
                DiagnosticsLogger = _logger,
            };
        }

        private UiShellBootstrapper MakeBootstrapper()
        {
            return new UiShellBootstrapper(_rootFactory);
        }

        // ---- Success path -----------------------------------------------

        [Test]
        [Description("有効な UiShellConfig で StartShell すると BootstrapResult.Success==true を返す")]
        public void StartShell_ValidConfig_ReturnsSuccess()
        {
            var bootstrapper = MakeBootstrapper();

            var result = bootstrapper.StartShell(MakeConfig());

            Assert.That(result.Success, Is.True, $"StartShell failed: {result.Error} {result.Detail}");
            Assert.That(result.Error, Is.Null);
            Assert.That(bootstrapper.IsRunning, Is.True);
            bootstrapper.StopShell();
        }

        [Test]
        [Description("StartShell は initialisation 順序を BootstrapStep で記録する (design.md initialisation 順序)")]
        public void StartShell_RecordsInitializationStepsInDesignOrder()
        {
            var bootstrapper = MakeBootstrapper();

            bootstrapper.StartShell(MakeConfig());
            var steps = bootstrapper.InitializationSteps;

            // 重要な順序契約をスポット検証する
            int IdxOf(BootstrapStep s)
            {
                for (var i = 0; i < steps.Count; i++)
                {
                    if (steps[i] == s) return i;
                }
                return -1;
            }
            Assert.That(IdxOf(BootstrapStep.PanelSettingsCreated), Is.LessThan(IdxOf(BootstrapStep.RootUiDocumentBuilt)));
            Assert.That(IdxOf(BootstrapStep.RootUiDocumentBuilt), Is.LessThan(IdxOf(BootstrapStep.TabUiDocumentsMounted)));
            Assert.That(IdxOf(BootstrapStep.TabUiDocumentsMounted), Is.LessThan(IdxOf(BootstrapStep.TabBarControllerReady)));
            Assert.That(IdxOf(BootstrapStep.TabBarControllerReady), Is.LessThan(IdxOf(BootstrapStep.SkinValidated)));
            Assert.That(IdxOf(BootstrapStep.SkinValidated), Is.LessThan(IdxOf(BootstrapStep.AssetLoaderReady)));
            Assert.That(IdxOf(BootstrapStep.AssetLoaderReady), Is.LessThan(IdxOf(BootstrapStep.AddressablesInitialized)));
            Assert.That(IdxOf(BootstrapStep.AddressablesInitialized), Is.LessThan(IdxOf(BootstrapStep.UiCommandClientReady)));
            Assert.That(IdxOf(BootstrapStep.UiCommandClientReady), Is.LessThan(IdxOf(BootstrapStep.UiSubscriptionClientReady)));
            Assert.That(IdxOf(BootstrapStep.UiSubscriptionClientReady), Is.LessThan(IdxOf(BootstrapStep.MainOutputStatusWatcherReady)));
            Assert.That(IdxOf(BootstrapStep.MainOutputStatusWatcherReady), Is.LessThan(IdxOf(BootstrapStep.IpcConnectionAttempted)));
            Assert.That(steps[steps.Count - 1], Is.EqualTo(BootstrapStep.ShellRunning));

            bootstrapper.StopShell();
        }

        [Test]
        [Description("StartShell 後にすべてのサブシステムアクセサが非 null になる (Requirement 1.4, 5.1)")]
        public void StartShell_PopulatesAllSubsystemAccessors()
        {
            var bootstrapper = MakeBootstrapper();

            bootstrapper.StartShell(MakeConfig());

            Assert.That(bootstrapper.DiagnosticsLogger, Is.SameAs(_logger));
            Assert.That(bootstrapper.PanelSettings, Is.Not.Null);
            Assert.That(bootstrapper.RootVisualElement, Is.Not.Null);
            Assert.That(bootstrapper.TabPanelRegistry, Is.Not.Null);
            Assert.That(bootstrapper.TabBarController, Is.Not.Null);
            Assert.That(bootstrapper.AssetLoader, Is.Not.Null);
            Assert.That(bootstrapper.ConnectionStatus, Is.Not.Null);
            Assert.That(bootstrapper.CommandClient, Is.Not.Null);
            Assert.That(bootstrapper.SubscriptionClient, Is.Not.Null);
            Assert.That(bootstrapper.NotificationBar, Is.Not.Null);
            Assert.That(bootstrapper.OutputStatusWatcher, Is.Not.Null);

            bootstrapper.StopShell();
        }

        [Test]
        [Description("CommonUiRegistrationCallback は StartShell 中に 1 回だけ呼ばれる")]
        public void StartShell_InvokesCommonUiRegistrationCallback_Once()
        {
            var bootstrapper = MakeBootstrapper();
            var config = MakeConfig();
            var calls = 0;
            config.CommonUiRegistrationCallback = () => calls++;

            bootstrapper.StartShell(config);

            Assert.That(calls, Is.EqualTo(1));
            bootstrapper.StopShell();
        }

        [Test]
        [Description("CommonUiRegistrationCallback が例外を投げても起動は完遂する（非致命扱い）")]
        public void StartShell_CommonUiRegistrationThrows_StillSucceeds()
        {
            var bootstrapper = MakeBootstrapper();
            var config = MakeConfig();
            config.CommonUiRegistrationCallback = () => throw new InvalidOperationException("expected");

            var result = bootstrapper.StartShell(config);

            Assert.That(result.Success, Is.True);
            bootstrapper.StopShell();
        }

        // ---- Re-entrancy / Idempotency ----------------------------------

        [Test]
        [Description("StartShell の二重呼び出しは no-op で Success を返す")]
        public void StartShell_WhenAlreadyRunning_IsNoOp()
        {
            var bootstrapper = MakeBootstrapper();
            bootstrapper.StartShell(MakeConfig());
            var firstStepCount = bootstrapper.InitializationSteps.Count;

            var second = bootstrapper.StartShell(MakeConfig());

            Assert.That(second.Success, Is.True);
            Assert.That(bootstrapper.InitializationSteps.Count, Is.EqualTo(firstStepCount),
                "Re-entrant StartShell must not re-record the step list.");
            bootstrapper.StopShell();
        }

        // ---- Failure paths: BootstrapErrorCode ---------------------------

        [Test]
        [Description("SkinProfile が null だと SkinProfileMissing を返す")]
        public void StartShell_NullSkinProfile_ReturnsSkinProfileMissing()
        {
            var bootstrapper = MakeBootstrapper();
            var config = MakeConfig();
            config.SkinProfile = null;

            var result = bootstrapper.StartShell(config);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Is.EqualTo(BootstrapErrorCode.SkinProfileMissing));
            Assert.That(bootstrapper.IsRunning, Is.False);
        }

        [Test]
        [Description("SkinProfile.RootVisualTreeAsset が null だと SkinProfileMissing を返す")]
        public void StartShell_SkinProfileMissingRootVta_ReturnsSkinProfileMissing()
        {
            var bootstrapper = MakeBootstrapper();
            var emptySkin = ScriptableObject.CreateInstance<UiToolkitShellSkinProfile>();
            _disposables.Add(emptySkin);
            var config = MakeConfig();
            config.SkinProfile = emptySkin;

            var result = bootstrapper.StartShell(config);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Is.EqualTo(BootstrapErrorCode.SkinProfileMissing));
        }

        [Test]
        [Description("IpcBus が null だと IpcAbstractionUnavailable を返す")]
        public void StartShell_NullIpcBus_ReturnsIpcAbstractionUnavailable()
        {
            var bootstrapper = MakeBootstrapper();
            var config = MakeConfig();
            config.IpcBus = null;

            var result = bootstrapper.StartShell(config);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Is.EqualTo(BootstrapErrorCode.IpcAbstractionUnavailable));
            Assert.That(bootstrapper.IsRunning, Is.False);
        }

        [Test]
        [Description("RootUiDocumentFactory が例外を投げると PanelSettingsAssignFailed を返す")]
        public void StartShell_RootFactoryThrows_ReturnsPanelSettingsAssignFailed()
        {
            _rootFactory.ShouldThrow = true;
            _rootFactory.ThrowException = new InvalidOperationException("panel settings unavailable");
            var bootstrapper = MakeBootstrapper();

            var result = bootstrapper.StartShell(MakeConfig());

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Is.EqualTo(BootstrapErrorCode.PanelSettingsAssignFailed));
            Assert.That(bootstrapper.IsRunning, Is.False);
        }

        [Test]
        [Description("ITabMountStrategy.MountTabs が false を返すと TabUxmlAttachFailed を返す")]
        public void StartShell_TabMountReturnsFalse_ReturnsTabUxmlAttachFailed()
        {
            _tabMount.ReturnFalse = true;
            var bootstrapper = MakeBootstrapper();

            var result = bootstrapper.StartShell(MakeConfig());

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Is.EqualTo(BootstrapErrorCode.TabUxmlAttachFailed));
            Assert.That(bootstrapper.IsRunning, Is.False);
        }

        [Test]
        [Description("ITabMountStrategy.MountTabs が例外を投げても TabUxmlAttachFailed を返す")]
        public void StartShell_TabMountThrows_ReturnsTabUxmlAttachFailed()
        {
            _tabMount.ShouldThrow = true;
            var bootstrapper = MakeBootstrapper();

            var result = bootstrapper.StartShell(MakeConfig());

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Is.EqualTo(BootstrapErrorCode.TabUxmlAttachFailed));
        }

        [Test]
        [Description("ITabMountStrategy が null の場合は TabUxmlAttachFailed を返す（必須依存）")]
        public void StartShell_NullTabMountStrategy_ReturnsTabUxmlAttachFailed()
        {
            var bootstrapper = MakeBootstrapper();
            var config = MakeConfig();
            config.TabMountStrategy = null;

            var result = bootstrapper.StartShell(config);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Is.EqualTo(BootstrapErrorCode.TabUxmlAttachFailed));
        }

        [Test]
        [Description("Addressables 初期化失敗は非致命：シェルは起動を完遂し IsRunning=true（fail-safe, Req 9.1 と同方針）")]
        public void StartShell_AddressablesInitFails_IsNonFatalAndShellRuns()
        {
            _addressables.StagedResult = AddressablesInitResult.Fail(detail: "deliberate failure");
            var bootstrapper = MakeBootstrapper();

            var result = bootstrapper.StartShell(MakeConfig());

            Assert.That(result.Success, Is.True,
                "Addressables 初期化失敗でもシェルは起動を完遂する（コンテンツ未ビルドの開発環境でも UI を立ち上げる）");
            Assert.That(bootstrapper.IsRunning, Is.True);
            bootstrapper.StopShell();
        }

        // ---- Connection-independent startup (Requirement 9.1, 9.7) ------

        [Test]
        [Description("IPC 接続が未確立でも StartShell は完遂する（Requirement 9.1）")]
        public void StartShell_IpcDisconnected_StartupCompletes()
        {
            // Bus stays in Disconnected state — never transitioned to Connected.
            var bootstrapper = MakeBootstrapper();

            var result = bootstrapper.StartShell(MakeConfig());

            Assert.That(result.Success, Is.True, $"StartShell must succeed regardless of IPC state: {result.Error}");
            Assert.That(bootstrapper.IsRunning, Is.True);
            bootstrapper.StopShell();
        }

        [Test]
        [Description("接続未確立中の PublishState は SendErrorCode.NotConnected を即時返却する（Requirement 9.4）")]
        public void StartShell_IpcDisconnected_PublishStateReturnsNotConnected()
        {
            var bootstrapper = MakeBootstrapper();
            bootstrapper.StartShell(MakeConfig());

            var sendResult = bootstrapper.CommandClient!.PublishState("ui/test", new { x = 1 });

            Assert.That(sendResult.Success, Is.False);
            Assert.That(sendResult.Error!.Value.Code, Is.EqualTo(VTuberSystemBase.UiToolkitShell.Commands.SendErrorCode.NotConnected));
            bootstrapper.StopShell();
        }

        // ---- StopShell / Dispose -----------------------------------------

        [Test]
        [Description("StopShell 後 IsRunning が false になりサブシステムが解放される")]
        public void StopShell_AfterStart_DisposesSubsystems()
        {
            var bootstrapper = MakeBootstrapper();
            bootstrapper.StartShell(MakeConfig());

            bootstrapper.StopShell();

            Assert.That(bootstrapper.IsRunning, Is.False);
            Assert.That(bootstrapper.TabBarController, Is.Null);
            Assert.That(bootstrapper.TabPanelRegistry, Is.Null);
            Assert.That(bootstrapper.AssetLoader, Is.Null);
            Assert.That(bootstrapper.ConnectionStatus, Is.Null);
            Assert.That(bootstrapper.NotificationBar, Is.Null);
            Assert.That(bootstrapper.OutputStatusWatcher, Is.Null);
            Assert.That(_rootFactory.DisposeInvocationCount, Is.GreaterThanOrEqualTo(1),
                "Root UIDocument disposal action must run during StopShell");
        }

        [Test]
        [Description("StopShell の二重呼び出しは安全に no-op")]
        public void StopShell_CalledTwice_IsSafe()
        {
            var bootstrapper = MakeBootstrapper();
            bootstrapper.StartShell(MakeConfig());

            bootstrapper.StopShell();
            Assert.DoesNotThrow(() => bootstrapper.StopShell());
        }

        [Test]
        [Description("致命的失敗パス（タブ mount 失敗）の後はリソースが解放されている。Addressables は非致命化したので致命トリガをタブ mount に変更")]
        public void StartShell_FailureRollsBackPartialState()
        {
            _tabMount.ReturnFalse = true;
            var bootstrapper = MakeBootstrapper();

            var result = bootstrapper.StartShell(MakeConfig());

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Is.EqualTo(BootstrapErrorCode.TabUxmlAttachFailed));
            Assert.That(bootstrapper.IsRunning, Is.False);
            Assert.That(_rootFactory.DisposeInvocationCount, Is.GreaterThanOrEqualTo(1),
                "Failure path must roll back the root UIDocument it already created");
        }

        [Test]
        [Description("Dispose は StopShell と等価")]
        public void Dispose_StopsShell()
        {
            var bootstrapper = MakeBootstrapper();
            bootstrapper.StartShell(MakeConfig());

            bootstrapper.Dispose();

            Assert.That(bootstrapper.IsRunning, Is.False);
        }

        // ---- Misc -------------------------------------------------------

        [Test]
        [Description("StartShell は config が null のとき ArgumentNullException を投げる")]
        public void StartShell_NullConfig_Throws()
        {
            var bootstrapper = MakeBootstrapper();
            Assert.Throws<ArgumentNullException>(() => bootstrapper.StartShell(null!));
        }

        [Test]
        [Description("デフォルトコンストラクタは production の root factory で構築される（smoke）")]
        public void DefaultConstructor_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => { using var _ = new UiShellBootstrapper(); });
        }
    }
}
