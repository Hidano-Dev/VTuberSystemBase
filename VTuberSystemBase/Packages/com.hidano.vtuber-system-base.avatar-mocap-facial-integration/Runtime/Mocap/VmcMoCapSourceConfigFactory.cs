using RealtimeAvatarController.Core;
using RealtimeAvatarController.MoCap.VMC;
using UnityEngine;
using VTuberSystemBase.RacMainOutputAdapter.ExtensionPoints;

namespace VTuberSystemBase.AvatarMocapFacialIntegration.Mocap
{
    /// <summary>
    /// Builds VMC mocap source descriptors for AMFI slots.
    /// </summary>
    public sealed class VmcMoCapSourceConfigFactory : IMoCapSourceConfigFactory
    {
        public MoCapSourceDescriptor Build(string slotId)
        {
            var config = ScriptableObject.CreateInstance<VMCMoCapSourceConfig>();
            config.name = $"VMCMoCapSourceConfig_{slotId}";
            config.port = 39539;
            config.bindAddress = "0.0.0.0";

            // VMC source factory registration is owned by the mocap-vmc package.
            // AMFI only emits descriptors; SlotManager Resolve success verifies registration.
            return new MoCapSourceDescriptor
            {
                SourceTypeId = VMCMoCapSourceFactory.VmcSourceTypeId,
                Config = config,
            };
        }
    }
}
