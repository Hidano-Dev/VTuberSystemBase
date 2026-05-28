#nullable enable
using System.Collections.Generic;
using System.Linq;
using VTuberSystemBase.CharacterSelectionTab.Contracts;

namespace VtsApiDebug
{
    /// <summary>
    /// §M Character タブ → rac-main-output-adapter の IPC 操作。
    /// 送信はシェルの CommandClient で documented topic（CharacterTopics）に publish/event。
    /// slot/{id}/assignment・slot/{id}/command は RAC が IOutputCommandDispatcher へ登録するため、
    /// IntegratedDemoBootstrap のバス→Dispatcher ブリッジ（bug#2 修正）に乗って届く（動的 slot トピック検証）。
    ///
    /// slotId / avatarKey は slots/catalog・avatars/catalog 由来。<see cref="SubscribeCharacter"/> で
    /// それらと slot 状態（status / error）を購読キャッシュしてから操作する。
    /// 検証は <see cref="DumpCharacterState"/>（UI 側キャッシュ）と <see cref="DumpRacAdapter"/>（出力側）。
    /// </summary>
    public static partial class UiApiDebugHub
    {
        private static readonly List<SlotCatalogEntry> _charSlots = new List<SlotCatalogEntry>();
        private static readonly List<AvatarCatalogEntry> _charAvatars = new List<AvatarCatalogEntry>();
        private static readonly Dictionary<string, string> _charSlotStatus = new Dictionary<string, string>();
        private static readonly HashSet<string> _charSlotSubscribed = new HashSet<string>();
        private static bool _charSubscribed;

        // ===== 購読（slotId / avatarKey と状態の取得用） ========================

        /// <summary>slots/catalog・avatars/catalog と各 slot の status/error を購読キャッシュする。</summary>
        public static string SubscribeCharacter()
        {
            if (!RequirePlayMode(out var guard)) return guard;
            var sub = Sub();
            if (sub == null) return Report("SubscribeCharacter", false, "SubscriptionClient is null (shell not running).");
            if (_charSubscribed) return Report("SubscribeCharacter", true, "already subscribed.");

            sub.Subscribe<SlotCatalogPayload>(CharacterTopics.SlotsCatalog,
                VTuberSystemBase.UiToolkitShell.Commands.MessageKind.State, env =>
                {
                    _charSlots.Clear();
                    if (env.Payload?.Slots != null) _charSlots.AddRange(env.Payload.Slots);
                    foreach (var slot in _charSlots) EnsureSlotSubscription(slot.SlotId);
                });
            sub.Subscribe<AvatarCatalogPayload>(CharacterTopics.AvatarsCatalog,
                VTuberSystemBase.UiToolkitShell.Commands.MessageKind.State, env =>
                {
                    _charAvatars.Clear();
                    if (env.Payload?.Avatars != null) _charAvatars.AddRange(env.Payload.Avatars);
                });

            _charSubscribed = true;
            return Report("SubscribeCharacter", true, "subscribed (slots/catalog, avatars/catalog; per-slot status/error on catalog arrival).");
        }

        private static void EnsureSlotSubscription(string slotId)
        {
            if (string.IsNullOrEmpty(slotId) || _charSlotSubscribed.Contains(slotId)) return;
            var sub = Sub();
            if (sub == null) return;

            sub.Subscribe<SlotStatusPayload>(CharacterTopics.SlotStatus(slotId),
                VTuberSystemBase.UiToolkitShell.Commands.MessageKind.State, env =>
                {
                    if (env.Payload != null) _charSlotStatus[slotId] = env.Payload.Status;
                });
            sub.Subscribe<SlotErrorPayload>(CharacterTopics.SlotError(slotId),
                VTuberSystemBase.UiToolkitShell.Commands.MessageKind.Event, env =>
                {
                    Report($"slot/{slotId}/error", false, $"code={env.Payload?.ErrorCode}, detail={env.Payload?.Detail}");
                });
            _charSlotSubscribed.Add(slotId);
        }

        /// <summary>UI 側にキャッシュした Character 状態（slot 一覧・状態・avatar 一覧）を読む。</summary>
        public static string DumpCharacterState()
        {
            var slots = string.Join(", ", _charSlots.Select(s =>
                $"{s.SlotId}({(_charSlotStatus.TryGetValue(s.SlotId, out var st) ? st : "?")})"));
            var avatars = string.Join(", ", _charAvatars.Select(a => a.AvatarKey));
            return Report("DumpCharacterState", true,
                $"Subscribed={_charSubscribed}, Slots({_charSlots.Count})=[{slots}], Avatars({_charAvatars.Count})=[{avatars}]");
        }

        // ===== Slot 割当・解除 ==================================================

        /// <summary>スロットにアバターを割り当てる（state slot/{id}/assignment）。catalog 未ビルド時は KeyNotFound エラー応答になる。</summary>
        public static string AssignAvatar(string slotId, string avatarKey)
        {
            if (!RequirePlayMode(out var guard)) return guard;
            var cmd = Cmd();
            if (cmd == null) return Report("AssignAvatar", false, "CommandClient is null.");
            if (string.IsNullOrEmpty(slotId)) return Report("AssignAvatar", false, "slotId is empty.");

            var r = cmd.PublishState(CharacterTopics.SlotAssignment(slotId),
                new SlotAssignmentPayload { AvatarKey = string.IsNullOrEmpty(avatarKey) ? null : avatarKey });
            return Report("AssignAvatar", r.Success,
                r.Success ? $"sent (slot={slotId}, avatar={avatarKey}). Verify with DumpCharacterState." : $"send failed: {r.Error}");
        }

        /// <summary>スロットを空にする（state slot/{id}/assignment, AvatarKey=null）。</summary>
        public static string ClearSlot(string slotId)
        {
            if (!RequirePlayMode(out var guard)) return guard;
            var cmd = Cmd();
            if (cmd == null) return Report("ClearSlot", false, "CommandClient is null.");
            if (string.IsNullOrEmpty(slotId)) return Report("ClearSlot", false, "slotId is empty.");

            var r = cmd.PublishState(CharacterTopics.SlotAssignment(slotId), new SlotAssignmentPayload { AvatarKey = null });
            return Report("ClearSlot", r.Success, r.Success ? $"sent (slot={slotId}, cleared)." : $"send failed: {r.Error}");
        }

        // ===== Slot コマンド（Reset / Reload / PresetApply） =====================

        /// <summary>スロットへ離散コマンドを送る（event slot/{id}/command）。kind = Reset / Reload / PresetApply。</summary>
        public static string SendSlotCommand(string slotId, string kind, string? argument = null)
        {
            if (!RequirePlayMode(out var guard)) return guard;
            var cmd = Cmd();
            if (cmd == null) return Report("SendSlotCommand", false, "CommandClient is null.");
            if (string.IsNullOrEmpty(slotId)) return Report("SendSlotCommand", false, "slotId is empty.");

            var r = cmd.PublishEvent(CharacterTopics.SlotCommand(slotId),
                new SlotCommandPayload { Kind = kind, Argument = argument });
            return Report("SendSlotCommand", r.Success, r.Success ? $"sent (slot={slotId}, kind={kind})." : $"send failed: {r.Error}");
        }

        public static string ResetSlot(string slotId) => SendSlotCommand(slotId, "Reset");
        public static string ReloadSlot(string slotId) => SendSlotCommand(slotId, "Reload");

        // ===== 無引数の便利メソッド（uloop からの quote-free 実行用） ============

        /// <summary>先頭スロットに存在しないアバターキーを割り当てる（KeyNotFound 応答で経路検証）。</summary>
        public static string AssignBogusToFirstSlot()
        {
            var slot = FirstSlotId();
            return slot == null
                ? Report("AssignBogusToFirstSlot", false, "no cached slots (call SubscribeCharacter first).")
                : AssignAvatar(slot, "vts-debug-missing-avatar");
        }

        /// <summary>avatars/catalog の先頭アバターを先頭スロットに割り当てる（catalog が空なら NG）。</summary>
        public static string AssignFirstAvatarToFirstSlot()
        {
            var slot = FirstSlotId();
            if (slot == null) return Report("AssignFirstAvatarToFirstSlot", false, "no cached slots.");
            if (_charAvatars.Count == 0) return Report("AssignFirstAvatarToFirstSlot", false, "no cached avatars (catalog empty).");
            return AssignAvatar(slot, _charAvatars[0].AvatarKey);
        }

        public static string ClearFirstSlot()
        {
            var slot = FirstSlotId();
            return slot == null ? Report("ClearFirstSlot", false, "no cached slots.") : ClearSlot(slot);
        }

        public static string ResetFirstSlot()
        {
            var slot = FirstSlotId();
            return slot == null ? Report("ResetFirstSlot", false, "no cached slots.") : ResetSlot(slot);
        }

        /// <summary>
        /// 合成 slotId へ assignment を 1 件送り、UI→bus の送信パスが生きているかだけを確認する。
        /// MainDemo は MoCap スロットが 0 個で往復検証ができないため、送信成否（SendResult）の確認用。
        /// ハンドラ未登録の topic なので bridge では HasHandlerFor=false で破棄され、往復はしない。
        /// </summary>
        public static string ProbeSlotSend()
        {
            return AssignAvatar("vts-probe-slot", "vts-debug-missing-avatar");
        }

        // ===== 内部ヘルパ =======================================================

        private static string? FirstSlotId() => _charSlots.Count > 0 ? _charSlots[0].SlotId : null;
    }
}
