using System;
using System.Collections.Generic;
using Hidano.FacialControl.Adapters.Playable;
using Hidano.FacialControl.Adapters.ScriptableObject.Serializable;
using RealtimeAvatarController.Avatar.Builtin;
using RealtimeAvatarController.Core;
using UnityEngine;
using VTuberSystemBase.AvatarMocapFacialIntegration.Catalog;

namespace VTuberSystemBase.AvatarMocapFacialIntegration.Facial
{
    public sealed class FacialControllerAttacher
    {
        private readonly HashSet<string> _attachedSlots = new();
        private IDisposable _subscription;
        private SlotManager _slotManager;
        private AvatarCatalog _catalog;

        internal int AttachedSlotCount => _attachedSlots.Count;
        internal bool HasAttachedSlot(string slotId) => _attachedSlots.Contains(slotId);

        public void Attach(SlotManager slotManager, AvatarCatalog catalog)
        {
            if (ReferenceEquals(_slotManager, slotManager) && ReferenceEquals(_catalog, catalog)) return;

            Detach();
            _slotManager = slotManager;
            _catalog = catalog;
            if (_slotManager == null || _catalog == null) return;

            _subscription = _slotManager.OnSlotStateChanged.Subscribe(new SlotStateObserver(this));

            foreach (var handle in _slotManager.GetSlots())
            {
                if (handle?.State == SlotState.Active)
                {
                    AttachToSlot(handle.SlotId);
                }
            }
        }

        public void Detach()
        {
            _subscription?.Dispose();
            _subscription = null;
            _slotManager = null;
            _catalog = null;
            _attachedSlots.Clear();
        }

        private void OnSlotStateChanged(SlotStateChangedEvent e)
        {
            if (e == null) return;

            if (e.NewState == SlotState.Active)
            {
                AttachToSlot(e.SlotId);
            }
            else if (e.NewState == SlotState.Disposed)
            {
                _attachedSlots.Remove(e.SlotId);
            }
        }

        private void AttachToSlot(string slotId)
        {
            if (string.IsNullOrEmpty(slotId)) return;
            if (_attachedSlots.Contains(slotId)) return;
            if (_slotManager == null || _catalog == null) return;

            if (!_slotManager.TryGetSlotResources(slotId, out _, out var avatar) || avatar == null)
            {
                Debug.LogWarning($"[FacialControllerAttacher] slotId='{slotId}' has no active avatar; facial setup skipped.");
                return;
            }

            var profile = ResolveProfile(slotId);
            if (profile == null)
            {
                Debug.LogWarning($"[FacialControllerAttacher] slotId='{slotId}' has no FacialCharacterProfileSO; facial setup skipped.");
                return;
            }

            if (!HasBlendShapeRenderer(avatar))
            {
                Debug.LogWarning($"[FacialControllerAttacher] slotId='{slotId}' avatar '{avatar.name}' has no BlendShape SkinnedMeshRenderer; facial setup skipped.");
                return;
            }

            var controller = avatar.GetComponent<FacialController>() ?? avatar.AddComponent<FacialController>();
            controller.CharacterSO = profile;

            if (!controller.IsInitialized)
            {
                controller.Initialize();
            }

            if (controller.IsInitialized)
            {
                _attachedSlots.Add(slotId);
            }
            else
            {
                Debug.LogWarning($"[FacialControllerAttacher] slotId='{slotId}' FacialController did not initialize.");
            }
        }

        private FacialCharacterProfileSO ResolveProfile(string slotId)
        {
            var handle = _slotManager.GetSlot(slotId);
            var config = handle?.Settings?.avatarProviderDescriptor?.Config as BuiltinAvatarProviderConfig;
            var prefab = config != null ? config.avatarPrefab : null;

            if (prefab != null)
            {
                foreach (var entry in _catalog.Entries)
                {
                    if (entry == null) continue;
                    if (ReferenceEquals(entry.AvatarPrefab, prefab))
                    {
                        return entry.FacialProfile as FacialCharacterProfileSO;
                    }
                }
            }

            return _catalog.TryGetEntry(slotId, out var fallbackEntry)
                ? fallbackEntry.FacialProfile as FacialCharacterProfileSO
                : null;
        }

        private static bool HasBlendShapeRenderer(GameObject avatar)
        {
            var renderers = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                var mesh = renderers[i] != null ? renderers[i].sharedMesh : null;
                if (mesh != null && mesh.blendShapeCount > 0)
                {
                    return true;
                }
            }

            return false;
        }

        private sealed class SlotStateObserver : IObserver<SlotStateChangedEvent>
        {
            private readonly FacialControllerAttacher _owner;

            public SlotStateObserver(FacialControllerAttacher owner)
            {
                _owner = owner;
            }

            public void OnNext(SlotStateChangedEvent value)
            {
                _owner.OnSlotStateChanged(value);
            }

            public void OnError(Exception error)
            {
            }

            public void OnCompleted()
            {
            }
        }
    }
}
