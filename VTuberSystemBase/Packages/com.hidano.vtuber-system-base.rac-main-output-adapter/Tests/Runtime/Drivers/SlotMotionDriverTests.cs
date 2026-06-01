using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using RealtimeAvatarController.Core;
using UniRx;
using UnityEngine;
using UnityEngine.TestTools;
using VTuberSystemBase.RacMainOutputAdapter.Defaults;
using VTuberSystemBase.RacMainOutputAdapter.Drivers;
using VTuberSystemBase.RacMainOutputAdapter.Tests.Doubles;

namespace VTuberSystemBase.RacMainOutputAdapter.Tests.Drivers
{
    public sealed class SlotMotionDriverTests
    {
        private RacRegistryFixture _fixture;
        private SlotManager _slotManager;
        private SlotMotionDriver _driver;
        private GameObject _driverObject;

        [SetUp]
        public void SetUp()
        {
            _fixture = new RacRegistryFixture();
            _fixture.SetUp(providerTypeId: "UnusedProvider", moCapTypeId: StubMoCapSourceConfigFactory.StubTypeId);
            _fixture.ProviderRegistry.Register(TestAvatarProviderFactory.TypeId, new TestAvatarProviderFactory());

            _slotManager = new SlotManager(
                _fixture.ProviderRegistry,
                _fixture.MoCapSourceRegistry,
                _fixture.ErrorChannel);

            _driverObject = new GameObject("SlotMotionDriverTests.Driver");
            _driver = _driverObject.AddComponent<SlotMotionDriver>();
            _driver.Attach(_slotManager);
        }

        [TearDown]
        public void TearDown()
        {
            if (_driverObject != null)
            {
                UnityEngine.Object.DestroyImmediate(_driverObject);
            }

            _slotManager?.Dispose();
            _slotManager = null;
            _fixture?.TearDown();
            _fixture = null;
        }

        [UnityTest]
        public IEnumerator ActiveHumanoidSlot_BuildsPipeline_LateUpdateDrivesAndDisposedTearsDown()
            => UniTask.ToCoroutine(async () =>
            {
                await _slotManager.AddSlotAsync(CreateSettings("humanoid", humanoid: true));

                Assert.That(_driver.HasPipeline("humanoid"), Is.True);
                Assert.That(_driver.ActivePipelineCount, Is.EqualTo(1));

                var before = _driver.ApplyAttemptCount;
                await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);

                Assert.That(_driver.ApplyAttemptCount, Is.GreaterThan(before));

                await _slotManager.RemoveSlotAsync("humanoid");

                Assert.That(_driver.HasPipeline("humanoid"), Is.False);
                Assert.That(_driver.ActivePipelineCount, Is.EqualTo(0));
            });

        [UnityTest]
        public IEnumerator NonHumanoidSlot_IsSkippedAndOtherActiveSlotContinues()
            => UniTask.ToCoroutine(async () =>
            {
                await _slotManager.AddSlotAsync(CreateSettings("good", humanoid: true));
                await _slotManager.AddSlotAsync(CreateSettings("nonhumanoid", humanoid: false));

                Assert.That(_driver.HasPipeline("good"), Is.True);
                Assert.That(_driver.HasPipeline("nonhumanoid"), Is.False);
                Assert.That(_driver.ActivePipelineCount, Is.EqualTo(1));

                var before = _driver.ApplyAttemptCount;
                await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate);

                Assert.That(_driver.ApplyAttemptCount, Is.GreaterThan(before));
                Assert.That(_driver.HasPipeline("good"), Is.True);
                Assert.That(_driver.HasPipeline("nonhumanoid"), Is.False);
            });

        private static SlotSettings CreateSettings(string slotId, bool humanoid)
        {
            var providerConfig = ScriptableObject.CreateInstance<TestAvatarProviderConfig>();
            providerConfig.Humanoid = humanoid;
            providerConfig.name = $"TestAvatarProviderConfig_{slotId}";

            var mocapConfig = ScriptableObject.CreateInstance<StubMoCapSourceConfig>();
            mocapConfig.name = $"StubMoCapSourceConfig_{slotId}";

            var settings = ScriptableObject.CreateInstance<SlotSettings>();
            settings.slotId = slotId;
            settings.displayName = slotId;
            settings.weight = 1f;
            settings.avatarProviderDescriptor = new AvatarProviderDescriptor
            {
                ProviderTypeId = TestAvatarProviderFactory.TypeId,
                Config = providerConfig,
            };
            settings.moCapSourceDescriptor = new MoCapSourceDescriptor
            {
                SourceTypeId = StubMoCapSourceConfigFactory.StubTypeId,
                Config = mocapConfig,
            };
            return settings;
        }

        private sealed class TestAvatarProviderConfig : ProviderConfigBase
        {
            public bool Humanoid;
        }

        private sealed class TestAvatarProviderFactory : IAvatarProviderFactory
        {
            public const string TypeId = "SlotMotionDriverTestAvatar";

            public IAvatarProvider Create(ProviderConfigBase config)
            {
                return new TestAvatarProvider(((TestAvatarProviderConfig)config).Humanoid);
            }
        }

        private sealed class TestAvatarProvider : IAvatarProvider
        {
            private readonly bool _humanoid;

            public TestAvatarProvider(bool humanoid)
            {
                _humanoid = humanoid;
            }

            public string ProviderType => TestAvatarProviderFactory.TypeId;

            public GameObject RequestAvatar(ProviderConfigBase config)
            {
                return _humanoid ? HumanoidAvatarBuilder.Create("HumanoidAvatar") : CreateNonHumanoidAvatar();
            }

            public UniTask<GameObject> RequestAvatarAsync(ProviderConfigBase config, CancellationToken cancellationToken = default)
            {
                return UniTask.FromResult(RequestAvatar(config));
            }

            public void ReleaseAvatar(GameObject avatar)
            {
                if (avatar != null)
                {
                    UnityEngine.Object.DestroyImmediate(avatar);
                }
            }

            public void Dispose()
            {
            }

            private static GameObject CreateNonHumanoidAvatar()
            {
                var avatar = new GameObject("NonHumanoidAvatar");
                avatar.AddComponent<Animator>();
                return avatar;
            }
        }

        private static class HumanoidAvatarBuilder
        {
            public static GameObject Create(string name)
            {
                var root = new GameObject(name);
                var bones = CreateBones(root.transform);

                var animator = root.AddComponent<Animator>();
                animator.avatar = AvatarBuilder.BuildHumanAvatar(root, CreateHumanDescription(bones));
                Assert.That(animator.avatar.isValid, Is.True);
                Assert.That(animator.avatar.isHuman, Is.True);
                Assert.That(animator.isHuman, Is.True);

                return root;
            }

            private static Dictionary<string, Transform> CreateBones(Transform root)
            {
                var bones = new Dictionary<string, Transform>();
                bones["Hips"] = Bone("Hips", root, new Vector3(0f, 1f, 0f));
                bones["Spine"] = Bone("Spine", bones["Hips"], new Vector3(0f, 0.2f, 0f));
                bones["Chest"] = Bone("Chest", bones["Spine"], new Vector3(0f, 0.2f, 0f));
                bones["Neck"] = Bone("Neck", bones["Chest"], new Vector3(0f, 0.15f, 0f));
                bones["Head"] = Bone("Head", bones["Neck"], new Vector3(0f, 0.15f, 0f));

                bones["LeftUpperLeg"] = Bone("LeftUpperLeg", bones["Hips"], new Vector3(-0.1f, -0.25f, 0f));
                bones["LeftLowerLeg"] = Bone("LeftLowerLeg", bones["LeftUpperLeg"], new Vector3(0f, -0.35f, 0f));
                bones["LeftFoot"] = Bone("LeftFoot", bones["LeftLowerLeg"], new Vector3(0f, -0.35f, 0.08f));
                bones["RightUpperLeg"] = Bone("RightUpperLeg", bones["Hips"], new Vector3(0.1f, -0.25f, 0f));
                bones["RightLowerLeg"] = Bone("RightLowerLeg", bones["RightUpperLeg"], new Vector3(0f, -0.35f, 0f));
                bones["RightFoot"] = Bone("RightFoot", bones["RightLowerLeg"], new Vector3(0f, -0.35f, 0.08f));

                bones["LeftShoulder"] = Bone("LeftShoulder", bones["Chest"], new Vector3(-0.1f, 0.12f, 0f));
                bones["LeftUpperArm"] = Bone("LeftUpperArm", bones["LeftShoulder"], new Vector3(-0.25f, 0f, 0f));
                bones["LeftLowerArm"] = Bone("LeftLowerArm", bones["LeftUpperArm"], new Vector3(-0.25f, 0f, 0f));
                bones["LeftHand"] = Bone("LeftHand", bones["LeftLowerArm"], new Vector3(-0.2f, 0f, 0f));
                bones["RightShoulder"] = Bone("RightShoulder", bones["Chest"], new Vector3(0.1f, 0.12f, 0f));
                bones["RightUpperArm"] = Bone("RightUpperArm", bones["RightShoulder"], new Vector3(0.25f, 0f, 0f));
                bones["RightLowerArm"] = Bone("RightLowerArm", bones["RightUpperArm"], new Vector3(0.25f, 0f, 0f));
                bones["RightHand"] = Bone("RightHand", bones["RightLowerArm"], new Vector3(0.2f, 0f, 0f));
                return bones;
            }

            private static Transform Bone(string name, Transform parent, Vector3 localPosition)
            {
                var bone = new GameObject(name).transform;
                bone.SetParent(parent, false);
                bone.localPosition = localPosition;
                bone.localRotation = Quaternion.identity;
                return bone;
            }

            private static HumanDescription CreateHumanDescription(Dictionary<string, Transform> bones)
            {
                var human = new List<HumanBone>();
                foreach (var name in bones.Keys)
                {
                    human.Add(new HumanBone
                    {
                        humanName = name,
                        boneName = name,
                        limit = new HumanLimit { useDefaultValues = true },
                    });
                }

                var skeleton = new List<SkeletonBone>();
                foreach (var pair in bones)
                {
                    skeleton.Add(new SkeletonBone
                    {
                        name = pair.Key,
                        position = pair.Value.localPosition,
                        rotation = pair.Value.localRotation,
                        scale = Vector3.one,
                    });
                }

                return new HumanDescription
                {
                    human = human.ToArray(),
                    skeleton = skeleton.ToArray(),
                    upperArmTwist = 0.5f,
                    lowerArmTwist = 0.5f,
                    upperLegTwist = 0.5f,
                    lowerLegTwist = 0.5f,
                    armStretch = 0.05f,
                    legStretch = 0.05f,
                    feetSpacing = 0f,
                    hasTranslationDoF = false,
                };
            }
        }
    }
}
