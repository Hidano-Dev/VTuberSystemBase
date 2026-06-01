using System;
using System.Collections.Generic;
using NUnit.Framework;
using RealtimeAvatarController.Core;
using VTuberSystemBase.AvatarMocapFacialIntegration.Diagnostics;
using VTuberSystemBase.RacMainOutputAdapter.Diagnostics;

namespace VTuberSystemBase.AvatarMocapFacialIntegration.Tests.EditMode.Diagnostics
{
    public sealed class MoCapRegistryProbeTests
    {
        [Test]
        public void GetRegisteredTypeIds_ReturnsSortedNonEmptyTypeIds()
        {
            var registry = new StubMoCapSourceRegistry("VMC", "", "Stub");

            var typeIds = MoCapRegistryProbe.GetRegisteredTypeIds(registry);

            CollectionAssert.AreEqual(new[] { "Stub", "VMC" }, typeIds);
        }

        [Test]
        public void LogRegisteredTypeIds_LogsRegisteredTypeIdList()
        {
            var registry = new StubMoCapSourceRegistry("VMC", "Stub");
            var logger = new RecordingLogger();

            MoCapRegistryProbe.LogRegisteredTypeIds(registry, logger);

            Assert.AreEqual(1, logger.Entries.Count);
            Assert.AreEqual(AdapterLogLevel.Info, logger.Entries[0].Level);
            Assert.AreEqual(MoCapRegistryProbe.Category, logger.Entries[0].Category);
            StringAssert.Contains("Registered mocap source typeIds: Stub, VMC", logger.Entries[0].Message);
        }

        [Test]
        public void LogRegisteredTypeIds_WhenNoRegisteredTypeIds_LogsNone()
        {
            var registry = new StubMoCapSourceRegistry();
            var logger = new RecordingLogger();

            MoCapRegistryProbe.LogRegisteredTypeIds(registry, logger);

            Assert.AreEqual(1, logger.Entries.Count);
            StringAssert.Contains("Registered mocap source typeIds: (none)", logger.Entries[0].Message);
        }

        private sealed class StubMoCapSourceRegistry : IMoCapSourceRegistry
        {
            private readonly IReadOnlyList<string> _typeIds;

            public StubMoCapSourceRegistry(params string[] typeIds)
            {
                _typeIds = typeIds;
            }

            public void Register(string sourceTypeId, IMoCapSourceFactory factory)
            {
                throw new NotSupportedException();
            }

            public IMoCapSource Resolve(MoCapSourceDescriptor descriptor)
            {
                throw new NotSupportedException();
            }

            public void Release(IMoCapSource source)
            {
            }

            public IReadOnlyList<string> GetRegisteredTypeIds()
            {
                return _typeIds;
            }
        }

        private sealed class RecordingLogger : IDiagnosticsLogger
        {
            public readonly List<Entry> Entries = new();

            public AdapterLogLevel MinimumLevel { get; set; } = AdapterLogLevel.Trace;

            public void Log(AdapterLogLevel level, string category, string message, Exception exception = null)
            {
                if (level < MinimumLevel) return;
                Entries.Add(new Entry(level, category, message, exception));
            }
        }

        private readonly struct Entry
        {
            public Entry(AdapterLogLevel level, string category, string message, Exception exception)
            {
                Level = level;
                Category = category;
                Message = message;
                Exception = exception;
            }

            public AdapterLogLevel Level { get; }
            public string Category { get; }
            public string Message { get; }
            public Exception Exception { get; }
        }
    }
}
