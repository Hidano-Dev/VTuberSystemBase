using System;
using UnityEngine;

namespace VTuberSystemBase.AvatarMocapFacialIntegration.Catalog
{
    [Serializable]
    public sealed class AvatarCatalogEntryAsset
    {
        [SerializeField] private string _avatarKey = string.Empty;
        [SerializeField] private string _displayName = string.Empty;
        [SerializeField] private GameObject _avatarPrefab;
        [SerializeField] private UnityEngine.Object _facialProfile;

        public string AvatarKey => _avatarKey ?? string.Empty;
        public string DisplayName => _displayName ?? string.Empty;
        public GameObject AvatarPrefab => _avatarPrefab;
        public UnityEngine.Object FacialProfile => _facialProfile;
    }
}
