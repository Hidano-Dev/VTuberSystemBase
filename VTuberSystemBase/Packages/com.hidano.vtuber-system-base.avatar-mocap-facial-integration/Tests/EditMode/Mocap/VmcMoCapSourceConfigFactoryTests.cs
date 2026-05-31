using NUnit.Framework;
using RealtimeAvatarController.MoCap.VMC;
using UnityEngine;
using VTuberSystemBase.AvatarMocapFacialIntegration.Mocap;

namespace VTuberSystemBase.AvatarMocapFacialIntegration.Tests.EditMode.Mocap
{
    public sealed class VmcMoCapSourceConfigFactoryTests
    {
        [Test]
        public void Build_ReturnsVmcDescriptorWithDefaultConfig()
        {
            var factory = new VmcMoCapSourceConfigFactory();

            var descriptor = factory.Build("slot-a");

            try
            {
                Assert.IsNotNull(descriptor);
                Assert.AreEqual(VMCMoCapSourceFactory.VmcSourceTypeId, descriptor.SourceTypeId);

                var config = descriptor.Config as VMCMoCapSourceConfig;
                Assert.IsNotNull(config);
                Assert.AreEqual(39539, config.port);
                Assert.AreEqual("0.0.0.0", config.bindAddress);
            }
            finally
            {
                if (descriptor?.Config != null)
                {
                    Object.DestroyImmediate(descriptor.Config);
                }
            }
        }
    }
}
