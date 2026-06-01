using System;
using System.Reflection;
using NUnit.Framework;
using RealtimeAvatarController.Core;
using UnityEngine;
using VTuberSystemBase.AvatarMocapFacialIntegration.Composition;
using VTuberSystemBase.AvatarMocapFacialIntegration.Mocap;
using VTuberSystemBase.AvatarMocapFacialIntegration.Resolution;
using VTuberSystemBase.CoreIpc.Abstractions;
using VTuberSystemBase.OutputRendererShell.Abstractions;
using VTuberSystemBase.OutputRendererShell.Diagnostics;
using VTuberSystemBase.OutputRendererShell.Dispatch;
using VTuberSystemBase.RacMainOutputAdapter.Bootstrapper;
using VTuberSystemBase.RacMainOutputAdapter.Diagnostics;
using VTuberSystemBase.RacMainOutputAdapter.Drivers;
using VTuberSystemBase.RacMainOutputAdapter.Internal;
using ShellLogLevel = VTuberSystemBase.OutputRendererShell.Diagnostics.LogLevel;

namespace VTuberSystemBase.AvatarMocapFacialIntegration.Tests.PlayMode.Composition
{
    public sealed class AmfiCompositionRootTests
    {
        private GameObject _gameObject;
        private AmfiCompositionRoot _root;
        private OutputCommandDispatcher _dispatcher;
        private NoOpMessageSink _sink;
        private RecordingLogger _logger;

        [SetUp]
        public void SetUp()
        {
            _gameObject = new GameObject("AmfiCompositionRootTests.Root");
            _root = _gameObject.AddComponent<AmfiCompositionRoot>();
            _dispatcher = new OutputCommandDispatcher(new OutputShellLogger(ShellLogLevel.Error));
            _sink = new NoOpMessageSink();
            _logger = new RecordingLogger();
        }

        [TearDown]
        public void TearDown()
        {
            _root?.Shutdown();
            _dispatcher?.Dispose();

            if (_gameObject != null)
            {
                UnityEngine.Object.DestroyImmediate(_gameObject);
            }
        }

        [Test]
        public void Initialize_OverridesAdapterServicesAndAttachesDriverToSameSlotManager()
        {
            _root.OverrideServices(
                dispatcher: _dispatcher,
                messageSink: _sink,
                logger: _logger);

            _root.Initialize();

            Assert.That(_root.IsRunning, Is.True);
            Assert.That(_root.Bootstrapper, Is.Not.Null);
            Assert.That(_root.Bootstrapper.SlotManager, Is.Not.Null);
            Assert.That(_root.Driver, Is.Not.Null);

            Assert.That(GetPrivateField(_root.Bootstrapper, "_keyResolver"), Is.TypeOf<CatalogAvatarKeyResolver>());
            Assert.That(GetPrivateField(_root.Bootstrapper, "_schemaProvider"), Is.TypeOf<InMemoryAvatarSchemaProvider>());
            Assert.That(GetPrivateField(_root.Bootstrapper, "_mocapFactory"), Is.TypeOf<VmcMoCapSourceConfigFactory>());

            var driverSlotManager = GetPrivateField(_root.Driver, "_slotManager");
            Assert.That(driverSlotManager, Is.SameAs(_root.Bootstrapper.SlotManager));
        }

        [Test]
        public void Shutdown_DetachesDriverAndShutsDownBootstrapper()
        {
            _root.OverrideServices(
                dispatcher: _dispatcher,
                messageSink: _sink,
                logger: _logger);
            _root.Initialize();

            var driver = _root.Driver;
            Assert.That(GetPrivateField(driver, "_slotManager"), Is.Not.Null);

            _root.Shutdown();

            Assert.That(_root.IsRunning, Is.False);
            Assert.That(_root.Bootstrapper, Is.Null);
            Assert.That(GetPrivateField(driver, "_slotManager"), Is.Null);
        }

        private static object GetPrivateField(object target, string fieldName)
        {
            var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, $"Field '{fieldName}' should exist on {target.GetType().Name}.");
            return field.GetValue(target);
        }

        private sealed class NoOpMessageSink : IAdapterMessageSink
        {
            public void PublishState<TPayload>(string topic, TPayload payload)
            {
            }

            public void PublishEvent<TPayload>(string topic, TPayload payload)
            {
            }
        }

        private sealed class RecordingLogger : IDiagnosticsLogger
        {
            public AdapterLogLevel MinimumLevel { get; set; } = AdapterLogLevel.Trace;
            public int WarningCount { get; private set; }

            public void Log(AdapterLogLevel level, string category, string message, Exception exception = null)
            {
                if (level >= AdapterLogLevel.Warning)
                {
                    WarningCount++;
                }
            }
        }
    }
}
