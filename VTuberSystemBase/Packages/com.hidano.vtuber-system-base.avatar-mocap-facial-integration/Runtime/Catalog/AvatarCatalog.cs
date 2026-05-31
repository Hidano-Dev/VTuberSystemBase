using System.Collections.Generic;
using UnityEngine;

namespace VTuberSystemBase.AvatarMocapFacialIntegration.Catalog
{
    [CreateAssetMenu(
        fileName = "AvatarCatalog",
        menuName = "VTuberSystemBase/Avatar Mocap Facial Integration/Avatar Catalog")]
    public sealed class AvatarCatalog : ScriptableObject
    {
        [SerializeField] private List<AvatarCatalogEntryAsset> _entries = new();

        public IReadOnlyList<AvatarCatalogEntryAsset> Entries => _entries;

        public bool TryGetEntry(string avatarKey, out AvatarCatalogEntryAsset entry)
        {
            if (string.IsNullOrWhiteSpace(avatarKey))
            {
                entry = null;
                return false;
            }

            foreach (var candidate in _entries)
            {
                if (candidate == null) continue;
                if (candidate.AvatarKey == avatarKey)
                {
                    entry = candidate;
                    return true;
                }
            }

            entry = null;
            return false;
        }

        private void OnValidate()
        {
            if (_entries == null) return;

            var seenKeys = new HashSet<string>();
            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry == null)
                {
                    Debug.LogWarning(
                        $"AvatarCatalog '{name}' entry at index {i} is null.",
                        this);
                    continue;
                }

                var avatarKey = entry.AvatarKey;
                if (string.IsNullOrWhiteSpace(avatarKey))
                {
                    Debug.LogWarning(
                        $"AvatarCatalog '{name}' entry at index {i} has an empty avatarKey.",
                        this);
                }
                else if (!seenKeys.Add(avatarKey))
                {
                    Debug.LogWarning(
                        $"AvatarCatalog '{name}' contains duplicate avatarKey '{avatarKey}' at index {i}.",
                        this);
                }

                if (entry.AvatarPrefab == null)
                {
                    Debug.LogWarning(
                        $"AvatarCatalog '{name}' entry '{avatarKey}' at index {i} has no AvatarPrefab.",
                        this);
                }
            }
        }
    }
}
