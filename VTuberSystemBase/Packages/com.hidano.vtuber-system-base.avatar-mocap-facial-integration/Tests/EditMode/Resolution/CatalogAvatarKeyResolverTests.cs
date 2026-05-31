using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using RealtimeAvatarController.Avatar.Builtin;
using UnityEngine;
using VTuberSystemBase.AvatarMocapFacialIntegration.Catalog;
using VTuberSystemBase.AvatarMocapFacialIntegration.Resolution;
using VTuberSystemBase.RacMainOutputAdapter.Diagnostics;

namespace VTuberSystemBase.AvatarMocapFacialIntegration.Tests.EditMode.Resolution
{
    public sealed class CatalogAvatarKeyResolverTests
    {
        private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public void Resolve_ReturnsBuiltinDescriptorWithCatalogPrefab()
        {
            var catalog = ScriptableObject.CreateInstance<AvatarCatalog>();
            var prefab = new GameObject("AvatarPrefab");
            SetEntries(catalog, new List<AvatarCatalogEntryAsset>
            {
                Entry("avatars/alice", "Alice", prefab),
            });

            try
            {
                var resolver = new CatalogAvatarKeyResolver(catalog, new RecordingLogger());

                var descriptor = resolver.Resolve("avatars/alice");

                Assert.IsNotNull(descriptor);
                Assert.AreEqual(BuiltinAvatarProviderFactory.BuiltinProviderTypeId, descriptor.ProviderTypeId);
                var config = descriptor.Config as BuiltinAvatarProviderConfig;
                Assert.IsNotNull(config);
                Assert.AreSame(prefab, config.avatarPrefab);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(prefab);
                UnityEngine.Object.DestroyImmediate(catalog);
            }
        }

        [Test]
        public void Resolve_ReturnsNullAndLogsWarningWhenKeyIsMissing()
        {
            var catalog = ScriptableObject.CreateInstance<AvatarCatalog>();
            catalog.name = "TestCatalog";
            var logger = new RecordingLogger();

            try
            {
                var resolver = new CatalogAvatarKeyResolver(catalog, logger);

                var descriptor = resolver.Resolve("avatars/missing");

                Assert.IsNull(descriptor);
                Assert.That(logger.Messages, Has.Some.Contains("could not find avatarKey 'avatars/missing'"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(catalog);
            }
        }

        [Test]
        public void AvatarKeys_ReturnsCatalogEntriesWithoutAddressables()
        {
            var catalog = ScriptableObject.CreateInstance<AvatarCatalog>();
            var prefab = new GameObject("AvatarPrefab");
            SetEntries(catalog, new List<AvatarCatalogEntryAsset>
            {
                Entry("avatars/alice", "Alice", prefab),
                Entry("avatars/bob", "", prefab),
            });

            try
            {
                var resolver = new CatalogAvatarKeyResolver(catalog, new RecordingLogger());

                var keys = resolver.AvatarKeys;

                Assert.AreEqual(2, keys.Count);
                Assert.AreEqual("avatars/alice", keys[0].AvatarKey);
                Assert.AreEqual("Alice", keys[0].DisplayName);
                Assert.AreEqual("avatars/bob", keys[1].AvatarKey);
                Assert.AreEqual("avatars/bob", keys[1].DisplayName);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(prefab);
                UnityEngine.Object.DestroyImmediate(catalog);
            }
        }

        [Test]
        public void Refresh_RaisesAvatarKeysChanged()
        {
            var resolver = new CatalogAvatarKeyResolver(null, new RecordingLogger());
            var raised = false;
            resolver.OnAvatarKeysChanged += () => raised = true;

            resolver.Refresh().GetAwaiter().GetResult();

            Assert.IsTrue(raised);
        }

        private static AvatarCatalogEntryAsset Entry(string avatarKey, string displayName, GameObject prefab)
        {
            var entry = new AvatarCatalogEntryAsset();
            typeof(AvatarCatalogEntryAsset).GetField("_avatarKey", InstancePrivate).SetValue(entry, avatarKey);
            typeof(AvatarCatalogEntryAsset).GetField("_displayName", InstancePrivate).SetValue(entry, displayName);
            typeof(AvatarCatalogEntryAsset).GetField("_avatarPrefab", InstancePrivate).SetValue(entry, prefab);
            return entry;
        }

        private static void SetEntries(AvatarCatalog catalog, List<AvatarCatalogEntryAsset> entries)
        {
            typeof(AvatarCatalog).GetField("_entries", InstancePrivate).SetValue(catalog, entries);
        }

        private sealed class RecordingLogger : IDiagnosticsLogger
        {
            public AdapterLogLevel MinimumLevel { get; set; } = AdapterLogLevel.Trace;
            public List<string> Messages { get; } = new();

            public void Log(AdapterLogLevel level, string category, string message, Exception exception = null)
            {
                Messages.Add(message);
            }
        }
    }
}
