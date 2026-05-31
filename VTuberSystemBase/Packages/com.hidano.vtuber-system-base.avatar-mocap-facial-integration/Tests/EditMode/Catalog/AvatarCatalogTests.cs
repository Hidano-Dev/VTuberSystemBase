using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VTuberSystemBase.AvatarMocapFacialIntegration.Catalog;

namespace VTuberSystemBase.AvatarMocapFacialIntegration.Tests.EditMode.Catalog
{
    public sealed class AvatarCatalogTests
    {
        private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public void TryGetEntry_ReturnsMatchingEntry()
        {
            var catalog = ScriptableObject.CreateInstance<AvatarCatalog>();
            var prefab = new GameObject("AvatarPrefab");
            var entry = Entry("avatars/alice", "Alice", prefab);
            SetEntries(catalog, new List<AvatarCatalogEntryAsset> { entry });

            try
            {
                Assert.IsTrue(catalog.TryGetEntry("avatars/alice", out var result));
                Assert.AreSame(entry, result);
            }
            finally
            {
                Object.DestroyImmediate(prefab);
                Object.DestroyImmediate(catalog);
            }
        }

        [Test]
        public void OnValidate_LogsDuplicateAvatarKey()
        {
            var catalog = ScriptableObject.CreateInstance<AvatarCatalog>();
            catalog.name = "TestCatalog";
            var firstPrefab = new GameObject("FirstAvatarPrefab");
            var secondPrefab = new GameObject("SecondAvatarPrefab");
            SetEntries(catalog, new List<AvatarCatalogEntryAsset>
            {
                Entry("avatars/alice", "Alice", firstPrefab),
                Entry("avatars/alice", "Alice Duplicate", secondPrefab),
            });

            try
            {
                LogAssert.Expect(LogType.Warning, "AvatarCatalog 'TestCatalog' contains duplicate avatarKey 'avatars/alice' at index 1.");

                InvokeOnValidate(catalog);
            }
            finally
            {
                Object.DestroyImmediate(firstPrefab);
                Object.DestroyImmediate(secondPrefab);
                Object.DestroyImmediate(catalog);
            }
        }

        [Test]
        public void OnValidate_LogsMissingPrefab()
        {
            var catalog = ScriptableObject.CreateInstance<AvatarCatalog>();
            catalog.name = "TestCatalog";
            SetEntries(catalog, new List<AvatarCatalogEntryAsset>
            {
                Entry("avatars/alice", "Alice", null),
            });

            try
            {
                LogAssert.Expect(LogType.Warning, "AvatarCatalog 'TestCatalog' entry 'avatars/alice' at index 0 has no AvatarPrefab.");

                InvokeOnValidate(catalog);
            }
            finally
            {
                Object.DestroyImmediate(catalog);
            }
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

        private static void InvokeOnValidate(AvatarCatalog catalog)
        {
            typeof(AvatarCatalog).GetMethod("OnValidate", InstancePrivate).Invoke(catalog, null);
        }
    }
}
