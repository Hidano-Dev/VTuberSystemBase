#nullable enable
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VTuberSystemBase.OutputRendererShell.Abstractions;
using VTuberSystemBase.OutputRendererShell.Diagnostics;
using VTuberSystemBase.OutputRendererShell.Display;
using VTuberSystemBase.OutputRendererShell.EditModeTests.Fakes;

namespace VTuberSystemBase.OutputRendererShell.EditModeTests
{
    /// <summary>
    /// Wave 3e: <see cref="RuntimeDisplaySelectorRoutingService"/> の単体テスト。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IRuntimeDisplaySelectorBridge"/> を経由するため、RDS Facade
    /// （<c>RuntimeDisplaySelector.Current</c>）がシーンに存在しない EditMode でも検証可能。
    /// </para>
    /// </remarks>
    [TestFixture]
    public class RuntimeDisplaySelectorRoutingServiceTests
    {
        private GameObject? _cameraGo;
        private Camera _camera = null!;
        private OutputShellLogger _logger = null!;

        [SetUp]
        public void SetUp()
        {
            _cameraGo = new GameObject("RdsRoutingTest_Camera");
            _camera = _cameraGo.AddComponent<Camera>();
            _logger = new OutputShellLogger(LogLevel.Verbose);
        }

        [TearDown]
        public void TearDown()
        {
            if (_cameraGo != null)
            {
                UnityEngine.Object.DestroyImmediate(_cameraGo);
                _cameraGo = null;
            }
        }

        [Test]
        [Description("RDS が利用可能: AssignRenderTextureToDisplay が呼ばれ、カメラの targetTexture が張り替えられ、IsFallbackActive=false")]
        public void Activate_WhenRdsAvailable_NoSpout_CallsBridgeAndSucceeds()
        {
            var bridge = new FakeRuntimeDisplaySelectorBridge { IsAvailable = true };
            var probe = new StubDisplayProbe { DisplayCount = 2, IsEditor = false };
            var sut = new RuntimeDisplaySelectorRoutingService(_logger, bridge, probe);

            var info = sut.Activate(_camera, new DisplayRoutingConfig
            {
                TargetDisplayIndex = 1,
                SuppressEditorWarning = true,
                SpoutSenderName = null,
            });

            Assert.AreEqual(0, bridge.Calls.Count, "RT 経路では camera-capture（AssignCameraToDisplay）は呼ばない");
            Assert.AreEqual(1, bridge.RenderTextureCalls.Count);
            Assert.AreEqual(1, bridge.RenderTextureCalls[0].DisplayIndex);
            Assert.IsNotNull(bridge.RenderTextureCalls[0].RenderTexture);
            Assert.AreEqual(1, info.RequestedDisplayIndex);
            Assert.AreEqual(1, info.EffectiveDisplayIndex);
            Assert.IsFalse(info.IsFallbackActive);
            Assert.IsNotNull(_camera.targetTexture, "出力カメラは RenderTexture に張り替えられる（ディスプレイ非依存描画）");
            Assert.AreSame(bridge.RenderTextureCalls[0].RenderTexture, _camera.targetTexture,
                "Spout 送出元 RT とカメラの描画先 RT は同一インスタンス");

            sut.Dispose();
            Assert.IsNull(_camera.targetTexture, "Dispose で targetTexture は元（null）に戻る");
        }

        [Test]
        [Description("Spout 名指定時: 診断メッセージにセンダー名が反映され、RT 経路で成功する")]
        public void Activate_WithSpoutSenderName_PropagatesNameToBridge()
        {
            var bridge = new FakeRuntimeDisplaySelectorBridge { IsAvailable = true };
            var probe = new StubDisplayProbe { DisplayCount = 2, IsEditor = false };
            var sut = new RuntimeDisplaySelectorRoutingService(_logger, bridge, probe);

            var info = sut.Activate(_camera, new DisplayRoutingConfig
            {
                TargetDisplayIndex = 1,
                SuppressEditorWarning = true,
                SpoutSenderName = "VTuberMainOutput",
            });

            Assert.AreEqual(1, bridge.RenderTextureCalls.Count);
            Assert.IsFalse(info.IsFallbackActive);
            Assert.IsNotNull(info.DiagnosticMessage);
            StringAssert.Contains("VTuberMainOutput", info.DiagnosticMessage!);

            sut.Dispose();
        }

        [Test]
        [Description("RDS Facade 未配置時: 警告ログを残しつつ物理ディスプレイ経路に fallback、IsFallbackActive=true")]
        public void Activate_WhenRdsUnavailable_FallsBackToBuiltInPath()
        {
            var bridge = new FakeRuntimeDisplaySelectorBridge { IsAvailable = false };
            var probe = new StubDisplayProbe { DisplayCount = 2, IsEditor = false };
            var sut = new RuntimeDisplaySelectorRoutingService(_logger, bridge, probe);

            LogAssert.Expect(LogType.Warning,
                new System.Text.RegularExpressions.Regex("RuntimeDisplaySelector.Current is unavailable"));

            var info = sut.Activate(_camera, new DisplayRoutingConfig
            {
                TargetDisplayIndex = 1,
                SuppressEditorWarning = true,
            });

            Assert.AreEqual(0, bridge.Calls.Count, "RDS 不在時は bridge.Assign を呼ばない");
            Assert.IsTrue(info.IsFallbackActive);
            Assert.AreEqual(1, info.EffectiveDisplayIndex,
                "DisplayCount=2 の範囲内なので fallback でも要求 index がそのまま使われる");
            Assert.AreEqual(1, _camera.targetDisplay);
            Assert.Contains(1, probe.ActivatedIndices);
        }

        [Test]
        [Description("RDS Assign が例外を投げた場合: ログを残しつつ fallback 経路で targetDisplay を直接設定")]
        public void Activate_WhenBridgeThrows_FallsBackAndLogsError()
        {
            var bridge = new FakeRuntimeDisplaySelectorBridge
            {
                IsAvailable = true,
                ThrowOnAssign = new InvalidOperationException("simulated RDS failure"),
            };
            var probe = new StubDisplayProbe { DisplayCount = 2, IsEditor = false };
            var sut = new RuntimeDisplaySelectorRoutingService(_logger, bridge, probe);

            LogAssert.Expect(LogType.Error,
                new System.Text.RegularExpressions.Regex("RDS AssignRenderTextureToDisplay threw"));

            var info = sut.Activate(_camera, new DisplayRoutingConfig
            {
                TargetDisplayIndex = 1,
                SuppressEditorWarning = true,
            });

            Assert.AreEqual(1, bridge.RenderTextureCalls.Count, "RT Assign は試行される（履歴は残る）");
            Assert.IsTrue(info.IsFallbackActive);
            Assert.AreEqual(1, info.EffectiveDisplayIndex);
            Assert.IsNull(_camera.targetTexture, "fallback では張り替えた targetTexture を解除して物理ディスプレイ経路に降りる");
            Assert.AreEqual(1, _camera.targetDisplay);
        }

        [Test]
        [Description("fallback 経路でも要求 index が DisplayCount を超える場合は Display 0 にさらに fallback")]
        public void Activate_WhenRdsUnavailable_AndIndexOutOfRange_FallsBackToZero()
        {
            var bridge = new FakeRuntimeDisplaySelectorBridge { IsAvailable = false };
            var probe = new StubDisplayProbe { DisplayCount = 1, IsEditor = false };
            var sut = new RuntimeDisplaySelectorRoutingService(_logger, bridge, probe);

            LogAssert.Expect(LogType.Warning,
                new System.Text.RegularExpressions.Regex("RuntimeDisplaySelector.Current is unavailable"));

            var info = sut.Activate(_camera, new DisplayRoutingConfig
            {
                TargetDisplayIndex = 5,
                SuppressEditorWarning = true,
            });

            Assert.IsTrue(info.IsFallbackActive);
            Assert.AreEqual(0, info.EffectiveDisplayIndex);
            Assert.AreEqual(0, _camera.targetDisplay);
        }

        [Test]
        [Description("Editor PlayMode 上では IsEditorLimitedMode=true / SuppressEditorWarning=false で Info ログ")]
        public void Activate_InEditor_RecordsEditorLimitedMode()
        {
            var bridge = new FakeRuntimeDisplaySelectorBridge { IsAvailable = true };
            var probe = new StubDisplayProbe { DisplayCount = 2, IsEditor = true };
            var sut = new RuntimeDisplaySelectorRoutingService(_logger, bridge, probe);

            LogAssert.Expect(LogType.Log,
                new System.Text.RegularExpressions.Regex("Display routing in Editor PlayMode is limited"));

            var info = sut.Activate(_camera, new DisplayRoutingConfig
            {
                TargetDisplayIndex = 1,
                SuppressEditorWarning = false,
            });

            Assert.IsTrue(info.IsEditorLimitedMode);
        }

        [Test]
        [Description("GetAssignment は直近 Activate 結果を返し、Activate 前は default")]
        public void GetAssignment_BeforeActivate_ReturnsDefault()
        {
            var bridge = new FakeRuntimeDisplaySelectorBridge { IsAvailable = true };
            var probe = new StubDisplayProbe { DisplayCount = 2, IsEditor = false };
            var sut = new RuntimeDisplaySelectorRoutingService(_logger, bridge, probe);

            Assert.AreEqual(default(DisplayAssignmentInfo), sut.GetAssignment());

            var info = sut.Activate(_camera, new DisplayRoutingConfig
            {
                TargetDisplayIndex = 1,
                SuppressEditorWarning = true,
            });

            Assert.AreEqual(info, sut.GetAssignment());
        }

        [Test]
        [Description("Dispose は冪等で例外を投げない")]
        public void Dispose_IsIdempotent()
        {
            var bridge = new FakeRuntimeDisplaySelectorBridge { IsAvailable = true };
            var probe = new StubDisplayProbe { DisplayCount = 2, IsEditor = false };
            var sut = new RuntimeDisplaySelectorRoutingService(_logger, bridge, probe);

            Assert.DoesNotThrow(() => sut.Dispose());
            Assert.DoesNotThrow(() => sut.Dispose(),
                "Dispose は複数回呼ばれても例外を投げない");
        }

        [Test]
        [Description("camera が null の場合は ArgumentNullException")]
        public void Activate_NullCamera_Throws()
        {
            var bridge = new FakeRuntimeDisplaySelectorBridge { IsAvailable = true };
            var probe = new StubDisplayProbe { DisplayCount = 2, IsEditor = false };
            var sut = new RuntimeDisplaySelectorRoutingService(_logger, bridge, probe);

            Assert.Throws<ArgumentNullException>(() =>
                sut.Activate(null!, new DisplayRoutingConfig()));
        }

        [Test]
        [Description("logger が null の場合はコンストラクタで ArgumentNullException")]
        public void Constructor_NullLogger_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new RuntimeDisplaySelectorRoutingService(null!));
        }

        private sealed class StubDisplayProbe : IDisplayProbe
        {
            public int DisplayCount { get; set; } = 1;
            public bool IsEditor { get; set; }
            public List<int> ActivatedIndices { get; } = new();
            public List<FullScreenMode> AppliedFullScreenModes { get; } = new();

            public void ActivateDisplay(int index) => ActivatedIndices.Add(index);
            public void SetFullScreenMode(FullScreenMode mode) => AppliedFullScreenModes.Add(mode);
        }
    }
}
