using System;
using VTuberSystemBase.AvatarMocapFacialIntegration.Catalog;
using VTuberSystemBase.CharacterSelectionTab.Contracts;
using VTuberSystemBase.RacMainOutputAdapter.ExtensionPoints;

namespace VTuberSystemBase.AvatarMocapFacialIntegration.Resolution
{
    /// <summary>
    /// Provides an empty, non-facial settings schema for avatar keys registered in an
    /// in-project <see cref="AvatarCatalog"/>.
    /// </summary>
    public sealed class InMemoryAvatarSchemaProvider : IAvatarSchemaProvider
    {
        private readonly AvatarCatalog _catalog;

        public InMemoryAvatarSchemaProvider(AvatarCatalog catalog)
        {
            _catalog = catalog;
        }

        public AvatarSettingsSchemaPayload Resolve(string avatarKey)
        {
            if (_catalog == null) return null;
            if (string.IsNullOrWhiteSpace(avatarKey)) return null;
            if (!_catalog.TryGetEntry(avatarKey, out _)) return null;

            return new AvatarSettingsSchemaPayload
            {
                AvatarKey = avatarKey,
                Settings = Array.Empty<SettingSchemaEntry>(),
            };
        }
    }
}
