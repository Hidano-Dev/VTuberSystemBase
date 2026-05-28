#nullable enable
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using VTuberSystemBase.StageLightingVolumeOutputAdapter.Diagnostics;
using VTuberSystemBase.StageLightingVolumeOutputAdapter.Lights;
using VTuberSystemBase.StageLightingVolumeTab.Contracts;

namespace VTuberSystemBase.StageLightingVolumeOutputAdapter.Tests.Editor
{
    public sealed class LightHandlerTests
    {
        private FakeOutputCommandDispatcher _dispatcher = null!;
        private FakeOutputSceneRoots _roots = null!;
        private RecordingMessageSink _sink = null!;
        private AdapterLogger _logger = null!;
        private StageLightingVolumeOutputAdapterDiagnostics _diag = null!;
        private AdapterErrorReporter _reporter = null!;
        private LightHandler _sut = null!;

        private static readonly LightInitialDto Initial = new(
            LightTypeDto.Point, new Vector3Dto(0, 0, 0),
            new ColorDto(1, 1, 1, 1), 1f, 10f, 30f, "L");

        [SetUp]
        public void SetUp()
        {
            _dispatcher = new FakeOutputCommandDispatcher();
            _roots = new FakeOutputSceneRoots();
            _sink = new RecordingMessageSink();
            _logger = new AdapterLogger();
            _diag = new StageLightingVolumeOutputAdapterDiagnostics();
            _reporter = new AdapterErrorReporter(_sink, _logger, _diag, () => 0);
            int counter = 0;
            _sut = new LightHandler(_dispatcher, _roots, _sink, _reporter, _logger, _diag,
                idFactory: () => $"id{++counter:00000000000000000000000000000000}");
        }

        [TearDown]
        public void TearDown() { _sut.Dispose(); _roots.Dispose(); _dispatcher.Dispose(); }

        [Test]
        public void Start_RegistersCommand_AndPublishesEmptyList()
        {
            _sut.Start();
            Assert.That(_dispatcher.HasEventHandler(StageLightingTopics.LightCommand), Is.True);
            var listMsgs = _sink.PublishedStates.Where(p => p.Topic == StageLightingTopics.LightsList).ToList();
            Assert.That(listMsgs.Count, Is.EqualTo(1));
            var dto = (LightListDto)listMsgs[0].Payload!;
            Assert.That(dto.Items.Count, Is.EqualTo(0));
        }

        [Test]
        public void Add_PublishesAddedAndList_AndRegistersPropertyHandlers()
        {
            _sut.Start();
            _sink.Clear();

            _dispatcher.EmitEvent(StageLightingTopics.LightCommand,
                new LightCommandDto("add", null, Initial));

            Assert.That(_sut.Registry.Count, Is.EqualTo(1));
            var lightId = _sut.Registry.AllLightIds.Single();

            var addedEvents = _sink.PublishedEvents.Where(p => p.Topic == StageLightingTopics.LightAdded).ToList();
            Assert.That(addedEvents.Count, Is.EqualTo(1));
            var added = (LightAddedDto)addedEvents[0].Payload!;
            Assert.That(added.LightId, Is.EqualTo(lightId));

            // 7 property handlers registered for the light.
            Assert.That(_dispatcher.HasStateHandler(StageLightingTopics.LightProperty(lightId, StageLightingTopics.PropertyIntensity)), Is.True);
            Assert.That(_dispatcher.HasStateHandler(StageLightingTopics.LightProperty(lightId, StageLightingTopics.PropertyColor)), Is.True);
            Assert.That(_dispatcher.HasStateHandler(StageLightingTopics.LightProperty(lightId, StageLightingTopics.PropertyRotation)), Is.True);
            Assert.That(_dispatcher.HasStateHandler(StageLightingTopics.LightProperty(lightId, StageLightingTopics.PropertyType)), Is.True);
            Assert.That(_dispatcher.HasStateHandler(StageLightingTopics.LightProperty(lightId, StageLightingTopics.PropertyRange)), Is.True);
            Assert.That(_dispatcher.HasStateHandler(StageLightingTopics.LightProperty(lightId, StageLightingTopics.PropertySpotAngle)), Is.True);
            Assert.That(_dispatcher.HasStateHandler(StageLightingTopics.LightProperty(lightId, StageLightingTopics.PropertyDisplayName)), Is.True);

            // lights/list re-published with one entry.
            var lists = _sink.PublishedStates.Where(p => p.Topic == StageLightingTopics.LightsList).ToList();
            var lastList = (LightListDto)lists.Last().Payload!;
            Assert.That(lastList.Items.Count, Is.EqualTo(1));
            Assert.That(lastList.Items[0].LightId, Is.EqualTo(lightId));
        }

        [Test]
        public void Remove_DisposesHandlers_DestroysGameObject_PublishesList()
        {
            _sut.Start();
            _dispatcher.EmitEvent(StageLightingTopics.LightCommand, new LightCommandDto("add", null, Initial));
            var lightId = _sut.Registry.AllLightIds.Single();
            _sink.Clear();

            _dispatcher.EmitEvent(StageLightingTopics.LightCommand, new LightCommandDto("remove", lightId, null));

            Assert.That(_sut.Registry.Count, Is.EqualTo(0));
            Assert.That(_dispatcher.HasStateHandler(StageLightingTopics.LightProperty(lightId, StageLightingTopics.PropertyIntensity)), Is.False);
            var lists = _sink.PublishedStates.Where(p => p.Topic == StageLightingTopics.LightsList).ToList();
            Assert.That(lists.Count, Is.EqualTo(1));
            Assert.That(((LightListDto)lists[0].Payload!).Items.Count, Is.EqualTo(0));
            // Diagnostics handler count returns to the post-Start baseline: the 7
            // per-light handlers were released, only the light/command handler remains.
            Assert.That(_diag.RegisteredHandlerCount, Is.EqualTo(1));
        }

        [Test]
        public void Remove_UnknownId_ReportsError()
        {
            _sut.Start();
            _sink.Clear();
            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("light_error.*lightId=ghost"));
            _dispatcher.EmitEvent(StageLightingTopics.LightCommand, new LightCommandDto("remove", "ghost", null));
            var errs = _sink.PublishedEvents.Where(p => p.Topic == StageLightingTopics.LightError).ToList();
            Assert.That(errs.Count, Is.EqualTo(1));
            var dto = (LightErrorDto)errs[0].Payload!;
            Assert.That(dto.ErrorCode, Is.EqualTo("not_found"));
        }

        [Test]
        public void Dispose_TearsDownAllLights_AndUnregistersPropertyHandlers()
        {
            _sut.Start();
            _dispatcher.EmitEvent(StageLightingTopics.LightCommand, new LightCommandDto("add", null, Initial));
            _dispatcher.EmitEvent(StageLightingTopics.LightCommand, new LightCommandDto("add", null, Initial));
            Assert.That(_sut.Registry.Count, Is.EqualTo(2));
            _sut.Dispose();
            Assert.That(_sut.Registry.Count, Is.EqualTo(0));
            Assert.That(_dispatcher.RegisteredHandlerCount, Is.EqualTo(0));
            // LightHandler subtracts exactly its own contribution from the shared
            // diagnostics counter on teardown (here it is the only contributor).
            Assert.That(_diag.RegisteredHandlerCount, Is.EqualTo(0));
        }
    }
}
