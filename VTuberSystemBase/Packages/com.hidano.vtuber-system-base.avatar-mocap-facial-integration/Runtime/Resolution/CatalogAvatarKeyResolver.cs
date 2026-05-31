using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using RealtimeAvatarController.Avatar.Builtin;
using RealtimeAvatarController.Core;
using UnityEngine;
using VTuberSystemBase.AvatarMocapFacialIntegration.Catalog;
using VTuberSystemBase.CharacterSelectionTab.Contracts;
using VTuberSystemBase.RacMainOutputAdapter.Diagnostics;
using VTuberSystemBase.RacMainOutputAdapter.ExtensionPoints;

namespace VTuberSystemBase.AvatarMocapFacialIntegration.Resolution
{
    /// <summary>
    /// Resolves avatar keys from an in-project catalog without using Addressables.
    /// </summary>
    public sealed class CatalogAvatarKeyResolver : IAvatarKeyResolver
    {
        private readonly AvatarCatalog _catalog;
        private readonly IDiagnosticsLogger _logger;

        public CatalogAvatarKeyResolver(
            AvatarCatalog catalog,
            IDiagnosticsLogger logger = null)
        {
            _catalog = catalog;
            _logger = logger ?? new UnityConsoleDiagnosticsLogger();
        }

        public IReadOnlyList<AvatarCatalogEntry> AvatarKeys
        {
            get
            {
                if (_catalog == null) return Array.Empty<AvatarCatalogEntry>();

                var catalogEntries = _catalog.Entries;
                var entries = new List<AvatarCatalogEntry>(catalogEntries.Count);
                for (var i = 0; i < catalogEntries.Count; i++)
                {
                    var entry = catalogEntries[i];
                    if (entry == null) continue;

                    var avatarKey = entry.AvatarKey;
                    if (string.IsNullOrWhiteSpace(avatarKey)) continue;

                    var displayName = !string.IsNullOrWhiteSpace(entry.DisplayName)
                        ? entry.DisplayName
                        : avatarKey;

                    entries.Add(new AvatarCatalogEntry
                    {
                        AvatarKey = avatarKey,
                        DisplayName = displayName,
                    });
                }

                return entries;
            }
        }

        public event Action OnAvatarKeysChanged;

        public AvatarProviderDescriptor Resolve(string avatarKey)
        {
            if (_catalog == null)
            {
                _logger.Log(
                    AdapterLogLevel.Warning,
                    AdapterLogCategories.Adapter,
                    $"CatalogAvatarKeyResolver cannot resolve '{avatarKey}' because no AvatarCatalog is assigned.");
                return null;
            }

            if (string.IsNullOrWhiteSpace(avatarKey))
            {
                _logger.Log(
                    AdapterLogLevel.Warning,
                    AdapterLogCategories.Adapter,
                    "CatalogAvatarKeyResolver received an empty avatarKey.");
                return null;
            }

            if (!_catalog.TryGetEntry(avatarKey, out var entry))
            {
                _logger.Log(
                    AdapterLogLevel.Warning,
                    AdapterLogCategories.Adapter,
                    $"CatalogAvatarKeyResolver could not find avatarKey '{avatarKey}' in AvatarCatalog '{_catalog.name}'.");
                return null;
            }

            if (entry.AvatarPrefab == null)
            {
                _logger.Log(
                    AdapterLogLevel.Warning,
                    AdapterLogCategories.Adapter,
                    $"CatalogAvatarKeyResolver found avatarKey '{avatarKey}' but AvatarPrefab is not assigned.");
                return null;
            }

            var config = ScriptableObject.CreateInstance<BuiltinAvatarProviderConfig>();
            config.name = $"BuiltinAvatarProviderConfig_{avatarKey}";
            config.avatarPrefab = entry.AvatarPrefab;

            return new AvatarProviderDescriptor
            {
                ProviderTypeId = BuiltinAvatarProviderFactory.BuiltinProviderTypeId,
                Config = config,
            };
        }

        public UniTask Refresh()
        {
            OnAvatarKeysChanged?.Invoke();
            return UniTask.CompletedTask;
        }
    }
}
