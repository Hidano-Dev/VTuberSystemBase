using System;
using System.Collections.Generic;
using System.Linq;
using RealtimeAvatarController.Core;
using UnityEngine;
using VTuberSystemBase.RacMainOutputAdapter.Diagnostics;

namespace VTuberSystemBase.AvatarMocapFacialIntegration.Diagnostics
{
    /// <summary>
    /// Logs the registered mocap source typeIds once during startup.
    /// </summary>
    public static class MoCapRegistryProbe
    {
        public const string Category = "MoCapRegistry";
        public const string LogPrefix = "[AMFI/MoCapRegistryProbe]";

        private static bool s_logged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStartupState()
        {
            s_logged = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void LogRegisteredTypeIdsOnStartup()
        {
            if (s_logged) return;
            s_logged = true;

            LogRegisteredTypeIds();
        }

        public static IReadOnlyList<string> GetRegisteredTypeIds(IMoCapSourceRegistry registry = null)
        {
            registry ??= RegistryLocator.MoCapSourceRegistry;
            return registry.GetRegisteredTypeIds()
                .Where(typeId => !string.IsNullOrWhiteSpace(typeId))
                .OrderBy(typeId => typeId, StringComparer.Ordinal)
                .ToArray();
        }

        public static void LogRegisteredTypeIds(
            IMoCapSourceRegistry registry = null,
            IDiagnosticsLogger logger = null)
        {
            logger ??= new UnityConsoleDiagnosticsLogger();

            IReadOnlyList<string> typeIds;
            try
            {
                typeIds = GetRegisteredTypeIds(registry);
            }
            catch (Exception ex)
            {
                logger.Log(
                    AdapterLogLevel.Warning,
                    Category,
                    $"{LogPrefix} Failed to read registered mocap source typeIds.",
                    ex);
                return;
            }

            var list = typeIds.Count == 0 ? "(none)" : string.Join(", ", typeIds);
            logger.Log(
                AdapterLogLevel.Info,
                Category,
                $"{LogPrefix} Registered mocap source typeIds: {list}");
        }

#if UNITY_INCLUDE_TESTS
        public static void ResetForTest()
        {
            ResetStartupState();
        }
#endif
    }
}
