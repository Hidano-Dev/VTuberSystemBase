using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Cysharp.Threading.Tasks;
using Hidano.FacialControl.Adapters.Playable;
using Hidano.FacialControl.Adapters.ScriptableObject.Serializable;
using Hidano.FacialControl.Domain.Models;
using NUnit.Framework;
using RealtimeAvatarController.Avatar.Builtin;
using RealtimeAvatarController.Core;
using UnityEngine;
using UnityEngine.TestTools;
using VTuberSystemBase.AvatarMocapFacialIntegration.Catalog;
using VTuberSystemBase.AvatarMocapFacialIntegration.Facial;

namespace VTuberSystemBase.AvatarMocapFacialIntegration.Tests.PlayMode.Facial
{
    public sealed class FacialControllerAttacherTests
    {
        private SlotManager _slotManager;
        private FacialControllerAttacher _attacher;
        private AvatarCatalog _catalog;
        private GameObject _avatarPrefab;
        private TestProfileSO _profile;
        private TestProviderRegistry _providerRegistry;
        private TestMoCapSourceRegistry _moCapRegistry;
        private readonly List<UnityEngine.Object> _assets = new();

        [SetUp]
        public void SetUp()
        {
            _providerRegistry = new TestProviderRegistry();
            _providerRegistry.Register(BuiltinAvatarProviderFactory.BuiltinProviderTypeId, new BuiltinAvatarProviderFactory());
            _moCapRegistry = new TestMoCapSourceRegistry();
            _moCapRegistry.Register(TestMoCapSourceFactory.TypeId, new TestMoCapSourceFactory());
            _slotManager = new SlotManager(_providerRegistry, _moCapRegistry, new TestSlotErrorChannel());

            _attacher = new FacialControllerAttacher();
            _catalog = ScriptableObject.CreateInstance<AvatarCatalog>();
            _profile = ScriptableObject.CreateInstance<TestProfileSO>();
            _avatarPrefab = CreateAvatarPrefab("FacialAvatarPrefab", withBlendShape: true);
            _assets.Add(_catalog);
            _assets.Add(_profile);
            _assets.Add(_avatarPrefab);
        }

        [TearDown]
        public void TearDown()
        {
            _attacher?.Detach();
            _slotManager?.Dispose();

            for (var i = _assets.Count - 1; i >= 0; i--)
            {
                if (_assets[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(_assets[i]);
                }
            }
            _assets.Clear();
        }

        [UnityTest]
        public IEnumerator ActiveSlot_AddsProfileAndInitializesFacialController()
            => UniTask.ToCoroutine(async () =>
            {
                AddCatalogEntry("avatar-a", _avatarPrefab, _profile);
                _attacher.Attach(_slotManager, _catalog);

                await _slotManager.AddSlotAsync(CreateSettings("slot-a", _avatarPrefab));

                Assert.That(_slotManager.TryGetSlotResources("slot-a", out _, out var avatar), Is.True);
                var controller = avatar.GetComponent<FacialController>();

                Assert.That(controller, Is.Not.Null);
                Assert.That(controller.CharacterSO, Is.SameAs(_profile));
                Assert.That(controller.IsInitialized, Is.True);
                Assert.That(HasAttachedSlot("slot-a"), Is.True);
            });

        [UnityTest]
        public IEnumerator MissingProfile_SkipsFacialController()
            => UniTask.ToCoroutine(async () =>
            {
                AddCatalogEntry("avatar-a", _avatarPrefab, null);
                _attacher.Attach(_slotManager, _catalog);

                LogAssert.Expect(LogType.Warning, "[FacialControllerAttacher] slotId='slot-a' has no FacialCharacterProfileSO; facial setup skipped.");
                await _slotManager.AddSlotAsync(CreateSettings("slot-a", _avatarPrefab));

                Assert.That(_slotManager.TryGetSlotResources("slot-a", out _, out var avatar), Is.True);
                Assert.That(avatar.GetComponent<FacialController>(), Is.Null);
                Assert.That(HasAttachedSlot("slot-a"), Is.False);
            });

        [UnityTest]
        public IEnumerator DisposedSlot_RemovesTracking()
            => UniTask.ToCoroutine(async () =>
            {
                AddCatalogEntry("avatar-a", _avatarPrefab, _profile);
                _attacher.Attach(_slotManager, _catalog);

                await _slotManager.AddSlotAsync(CreateSettings("slot-a", _avatarPrefab));
                Assert.That(HasAttachedSlot("slot-a"), Is.True);

                await _slotManager.RemoveSlotAsync("slot-a");

                Assert.That(HasAttachedSlot("slot-a"), Is.False);
                Assert.That(AttachedSlotCount(), Is.EqualTo(0));
            });

        private bool HasAttachedSlot(string slotId)
        {
            var method = typeof(FacialControllerAttacher).GetMethod("HasAttachedSlot", BindingFlags.NonPublic | BindingFlags.Instance);
            return (bool)method.Invoke(_attacher, new object[] { slotId });
        }

        private int AttachedSlotCount()
        {
            var property = typeof(FacialControllerAttacher).GetProperty("AttachedSlotCount", BindingFlags.NonPublic | BindingFlags.Instance);
            return (int)property.GetValue(_attacher);
        }

        private void AddCatalogEntry(string avatarKey, GameObject prefab, UnityEngine.Object profile)
        {
            var entry = new AvatarCatalogEntryAsset();
            SetField(entry, "_avatarKey", avatarKey);
            SetField(entry, "_displayName", avatarKey);
            SetField(entry, "_avatarPrefab", prefab);
            SetField(entry, "_facialProfile", profile);

            var entriesField = typeof(AvatarCatalog).GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance);
            var entries = (List<AvatarCatalogEntryAsset>)entriesField.GetValue(_catalog);
            entries.Add(entry);
        }

        private static void SetField(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            field.SetValue(target, value);
        }

        private static SlotSettings CreateSettings(string slotId, GameObject prefab)
        {
            var providerConfig = ScriptableObject.CreateInstance<BuiltinAvatarProviderConfig>();
            providerConfig.avatarPrefab = prefab;

            var mocapConfig = ScriptableObject.CreateInstance<TestMoCapSourceConfig>();
            var settings = ScriptableObject.CreateInstance<SlotSettings>();
            settings.slotId = slotId;
            settings.displayName = slotId;
            settings.weight = 1f;
            settings.avatarProviderDescriptor = new AvatarProviderDescriptor
            {
                ProviderTypeId = BuiltinAvatarProviderFactory.BuiltinProviderTypeId,
                Config = providerConfig,
            };
            settings.moCapSourceDescriptor = new MoCapSourceDescriptor
            {
                SourceTypeId = TestMoCapSourceFactory.TypeId,
                Config = mocapConfig,
            };
            return settings;
        }

        private static GameObject CreateAvatarPrefab(string name, bool withBlendShape)
        {
            var root = new GameObject(name);
            root.AddComponent<Animator>();

            var meshObject = new GameObject("Face");
            meshObject.transform.SetParent(root.transform, false);
            var renderer = meshObject.AddComponent<SkinnedMeshRenderer>();
            var mesh = new Mesh();
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, 0f, 0f),
                new Vector3(0.5f, 0f, 0f),
                new Vector3(0f, 1f, 0f),
            };
            mesh.triangles = new[] { 0, 1, 2 };
            if (withBlendShape)
            {
                mesh.AddBlendShapeFrame(
                    "Smile",
                    100f,
                    new[] { Vector3.zero, Vector3.zero, Vector3.zero },
                    null,
                    null);
            }
            renderer.sharedMesh = mesh;
            return root;
        }

        private sealed class TestProfileSO : FacialCharacterProfileSO
        {
            public override FacialProfile LoadProfile()
            {
                return BuildFallbackProfile();
            }
        }

        private sealed class TestProviderRegistry : IProviderRegistry
        {
            private readonly Dictionary<string, IAvatarProviderFactory> _factories = new();

            public void Register(string providerTypeId, IAvatarProviderFactory factory)
            {
                _factories.Add(providerTypeId, factory);
            }

            public IAvatarProvider Resolve(AvatarProviderDescriptor descriptor)
            {
                return _factories[descriptor.ProviderTypeId].Create(descriptor.Config);
            }

            public IReadOnlyList<string> GetRegisteredTypeIds() => new List<string>(_factories.Keys);
        }

        private sealed class TestMoCapSourceRegistry : IMoCapSourceRegistry
        {
            private readonly Dictionary<string, IMoCapSourceFactory> _factories = new();

            public void Register(string sourceTypeId, IMoCapSourceFactory factory)
            {
                _factories.Add(sourceTypeId, factory);
            }

            public IMoCapSource Resolve(MoCapSourceDescriptor descriptor)
            {
                return _factories[descriptor.SourceTypeId].Create(descriptor.Config);
            }

            public void Release(IMoCapSource source)
            {
                source?.Shutdown();
                source?.Dispose();
            }

            public IReadOnlyList<string> GetRegisteredTypeIds() => new List<string>(_factories.Keys);
        }

        private sealed class TestMoCapSourceFactory : IMoCapSourceFactory
        {
            public const string TypeId = "AMFI_Facial_Attacher_Test";

            public IMoCapSource Create(MoCapSourceConfigBase config)
            {
                return new TestMoCapSource();
            }
        }

        private sealed class TestMoCapSourceConfig : MoCapSourceConfigBase
        {
        }

        private sealed class TestMoCapSource : IMoCapSource
        {
            private static readonly EmptyMotionObservable Empty = new();

            public string SourceType => TestMoCapSourceFactory.TypeId;
            public IObservable<MotionFrame> MotionStream => Empty;
            public void Initialize(MoCapSourceConfigBase config) { }
            public void Shutdown() { }
            public void Dispose() { }
        }

        private sealed class EmptyMotionObservable : IObservable<MotionFrame>
        {
            public IDisposable Subscribe(IObserver<MotionFrame> observer)
            {
                return EmptyDisposable.Instance;
            }
        }

        private sealed class TestSlotErrorChannel : ISlotErrorChannel
        {
            private static readonly EmptySlotErrorObservable Empty = new();

            public IObservable<SlotError> Errors => Empty;

            public void Publish(SlotError error)
            {
            }
        }

        private sealed class EmptySlotErrorObservable : IObservable<SlotError>
        {
            public IDisposable Subscribe(IObserver<SlotError> observer)
            {
                return EmptyDisposable.Instance;
            }
        }

        private sealed class EmptyDisposable : IDisposable
        {
            public static readonly EmptyDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
