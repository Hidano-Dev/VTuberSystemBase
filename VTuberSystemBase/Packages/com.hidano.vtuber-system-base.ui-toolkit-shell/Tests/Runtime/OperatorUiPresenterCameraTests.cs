#nullable enable
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using VTuberSystemBase.UiToolkitShell.Bootstrap;
using VTuberSystemBase.UiToolkitShell.Diagnostics;
using VTuberSystemBase.UiToolkitShell.Skin;
using VTuberSystemBase.UiToolkitShell.Tests.TestSupport;

namespace VTuberSystemBase.UiToolkitShell.Tests.Runtime
{
    /// <summary>
    /// オプションの presenter カメラ配線（<see cref="UiShellConfig.PresenterCameraFactory"/> /
    /// <see cref="UiShellBootstrapper"/> / <see cref="DefaultOperatorUiPresenterCameraFactory"/>）の
    /// 契約を EditMode で固定する。実カメラ生成は PlayMode/URP に依存するため、配線検証は fake
    /// factory で行い、既定実装は「Edit モードでは生成しない」ことのみ検証する。
    /// </summary>
    [TestFixture]
    public sealed class OperatorUiPresenterCameraTests
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
            _disposables = new List<UnityEngine.Object> { _skin, _skin.RootVisualTreeAsset };
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

        private UiShellConfig MakeConfig(
            IOperatorUiPresenterCameraFactory? presenterFactory,
            IDisplayAssignmentStrategy? displayStrategy = null)
        {
            return new UiShellConfig
            {
                SkinProfile = _skin,
                IpcBus = _bus,
                TabMountStrategy = _tabMount,
                AddressablesInitializer = _addressables,
                DiagnosticsLogger = _logger,
                PresenterCameraFactory = presenterFactory,
                DisplayAssignmentStrategy = displayStrategy,
            };
        }

        [Test]
        [Description("PresenterCameraFactory を渡すと EffectiveTargetDisplay で Create が呼ばれ、handle が公開される")]
        public void StartShell_WithFactory_InvokesWithEffectiveDisplay()
        {
            var factory = new FakePresenterCameraFactory();
            var bootstrapper = new UiShellBootstrapper(_rootFactory);

            var result = bootstrapper.StartShell(MakeConfig(factory));

            Assert.That(result.Success, Is.True, $"{result.Error} {result.Detail}");
            Assert.That(factory.CreateInvocationCount, Is.EqualTo(1));
            Assert.That(factory.LastTargetDisplay, Is.EqualTo(bootstrapper.EffectiveTargetDisplay));
            Assert.That(factory.LastTargetDisplay, Is.EqualTo(0), "default strategy pins Display 0");
            Assert.That(bootstrapper.PresenterCameraHandle, Is.Not.Null);

            bootstrapper.StopShell();
        }

        [Test]
        [Description("presenter は解決後のディスプレイ（DisplayAssignmentStrategy の結果）に追従する")]
        public void StartShell_FollowsResolvedDisplay()
        {
            var factory = new FakePresenterCameraFactory();
            var bootstrapper = new UiShellBootstrapper(_rootFactory);

            bootstrapper.StartShell(MakeConfig(factory, new FixedStrategy(2)));

            Assert.That(bootstrapper.EffectiveTargetDisplay, Is.EqualTo(2));
            Assert.That(factory.LastTargetDisplay, Is.EqualTo(2));

            bootstrapper.StopShell();
        }

        [Test]
        [Description("StopShell で presenter handle が Dispose され、アクセサが null に戻る")]
        public void StopShell_DisposesPresenterCamera()
        {
            var factory = new FakePresenterCameraFactory();
            var bootstrapper = new UiShellBootstrapper(_rootFactory);
            bootstrapper.StartShell(MakeConfig(factory));

            Assert.That(factory.LastHandle, Is.Not.Null);
            Assert.That(factory.LastHandle!.DisposeCount, Is.EqualTo(0));

            bootstrapper.StopShell();

            Assert.That(factory.LastHandle!.DisposeCount, Is.EqualTo(1));
            Assert.That(bootstrapper.PresenterCameraHandle, Is.Null);
        }

        [Test]
        [Description("PresenterCameraFactory 未指定（null）のときは presenter カメラを作らない")]
        public void StartShell_NullFactory_NoPresenterCamera()
        {
            var bootstrapper = new UiShellBootstrapper(_rootFactory);

            var result = bootstrapper.StartShell(MakeConfig(presenterFactory: null));

            Assert.That(result.Success, Is.True);
            Assert.That(bootstrapper.PresenterCameraHandle, Is.Null);

            bootstrapper.StopShell();
        }

        [Test]
        [Description("factory が null を返す（生成しない）場合でも shell は成功し handle は null のまま")]
        public void StartShell_FactoryReturnsNull_ShellStillSucceeds()
        {
            var factory = new FakePresenterCameraFactory { ReturnNull = true };
            var bootstrapper = new UiShellBootstrapper(_rootFactory);

            var result = bootstrapper.StartShell(MakeConfig(factory));

            Assert.That(result.Success, Is.True);
            Assert.That(factory.CreateInvocationCount, Is.EqualTo(1));
            Assert.That(bootstrapper.PresenterCameraHandle, Is.Null);

            bootstrapper.StopShell();
        }

        [Test]
        [Description("factory が例外を投げても起動は致命化せず presenter 無しで続行する")]
        public void StartShell_FactoryThrows_ShellStillSucceeds()
        {
            var factory = new FakePresenterCameraFactory { Throw = true };
            var bootstrapper = new UiShellBootstrapper(_rootFactory);

            var result = bootstrapper.StartShell(MakeConfig(factory));

            Assert.That(result.Success, Is.True);
            Assert.That(bootstrapper.PresenterCameraHandle, Is.Null);

            bootstrapper.StopShell();
        }

        [Test]
        [Description("既定実装 DefaultOperatorUiPresenterCameraFactory は Edit モードでは null を返す（カメラを作らない）")]
        public void DefaultFactory_InEditMode_ReturnsNull()
        {
            var factory = new DefaultOperatorUiPresenterCameraFactory();

            var handle = factory.Create(0, _logger);

            Assert.That(handle, Is.Null, "Edit モードでは Application.isPlaying=false でスキップされる");
        }

        // ---- test doubles ----------------------------------------------------

        private sealed class FixedStrategy : IDisplayAssignmentStrategy
        {
            private readonly int _value;
            public FixedStrategy(int value) => _value = value;
            public int ResolveTargetDisplay(int requested) => _value;
        }

        private sealed class FakePresenterCameraFactory : IOperatorUiPresenterCameraFactory
        {
            public bool ReturnNull { get; set; }
            public bool Throw { get; set; }
            public int CreateInvocationCount { get; private set; }
            public int LastTargetDisplay { get; private set; } = -1;
            public FakeHandle? LastHandle { get; private set; }

            public IDisposable? Create(int targetDisplay, IDiagnosticsLogger logger)
            {
                CreateInvocationCount++;
                LastTargetDisplay = targetDisplay;
                if (Throw) throw new InvalidOperationException("FakePresenterCameraFactory configured to throw");
                if (ReturnNull) return null;
                LastHandle = new FakeHandle();
                return LastHandle;
            }
        }

        private sealed class FakeHandle : IDisposable
        {
            public int DisposeCount { get; private set; }
            public void Dispose() => DisposeCount++;
        }
    }
}
