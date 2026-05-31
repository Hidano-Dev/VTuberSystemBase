using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using VTuberSystemBase.AvatarMocapFacialIntegration.Catalog;
using VTuberSystemBase.AvatarMocapFacialIntegration.Resolution;

namespace VTuberSystemBase.AvatarMocapFacialIntegration.Tests.EditMode.Resolution
{
    public sealed class InMemoryAvatarSchemaProviderTests
    {
        private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public void Resolve_ReturnsEmptySchemaForCatalogAvatarKey()
        {
            var catalog = ScriptableObject.CreateInstance<AvatarCatalog>();
            var prefab = new GameObject("AvatarPrefab");
            SetEntries(catalog, new List<AvatarCatalogEntryAsset>
            {
                Entry("avatars/alice", prefab),
            });

            try
            {
                var provider = new InMemoryAvatarSchemaProvider(catalog);

                var schema = provider.Resolve("avatars/alice");

                Assert.IsNotNull(schema);
                Assert.AreEqual("avatars/alice", schema.AvatarKey);
                Assert.IsNotNull(schema.Settings);
                Assert.AreEqual(0, schema.Settings.Count);
            }
            finally
            {
                Object.DestroyImmediate(prefab);
                Object.DestroyImmediate(catalog);
            }
        }

        [Test]
        public void Resolve_ReturnsNullForUnknownAvatarKey()
        {
            var catalog = ScriptableObject.CreateInstance<AvatarCatalog>();

            try
            {
                var provider = new InMemoryAvatarSchemaProvider(catalog);

                var schema = provider.Resolve("avatars/missing");

                Assert.IsNull(schema);
            }
            finally
            {
                Object.DestroyImmediate(catalog);
            }
        }

        [Test]
        public void Resolve_ReturnsNullWhenCatalogIsMissing()
        {
            var provider = new InMemoryAvatarSchemaProvider(null);

            var schema = provider.Resolve("avatars/alice");

            Assert.IsNull(schema);
        }

        private static AvatarCatalogEntryAsset Entry(string avatarKey, GameObject prefab)
        {
            var entry = new AvatarCatalogEntryAsset();
            typeof(AvatarCatalogEntryAsset).GetField("_avatarKey", InstancePrivate).SetValue(entry, avatarKey);
            typeof(AvatarCatalogEntryAsset).GetField("_avatarPrefab", InstancePrivate).SetValue(entry, prefab);
            return entry;
        }

        private static void SetEntries(AvatarCatalog catalog, List<AvatarCatalogEntryAsset> entries)
        {
            typeof(AvatarCatalog).GetField("_entries", InstancePrivate).SetValue(catalog, entries);
        }
    }
}
