#nullable enable
using System;
using System.Collections.Generic;
using VTuberSystemBase.CameraSwitcherOutputAdapter.Runtime;
using VTuberSystemBase.CameraSwitcherTab.Contracts;
using VTuberSystemBase.IntegratedDemo;

namespace VtsApiDebug
{
    /// <summary>
    /// §O Camera タブ → camera-switcher-output-adapter の IPC 操作。
    /// 操作はシェルの CommandClient で documented topic（CameraIpcTopics）に payload を publish/event 送信。
    /// 検証は出力側アダプタ診断（CameraHost.Diagnostics.GetSnapshot）を同期読み取りして返す。
    /// 非同期反映があるため「操作」と「Dump 検証」は別メソッドにしてある（操作後に DumpCameraAdapter を呼ぶ）。
    /// </summary>
    public static partial class UiApiDebugHub
    {
        // ===== 操作（フル引数版。Window / uloop --parameters 用） =====

        /// <summary>カメラを追加（event camera/command, op=add）。type は "Perspective"/"Orthographic"。</summary>
        public static string AddCamera(string type, string displayName)
        {
            if (!RequirePlayMode(out var guard)) return guard;
            var cmd = Cmd();
            if (cmd == null) return Report("AddCamera", false, "CommandClient is null (shell not running).");

            var reqId = Guid.NewGuid().ToString("N");
            var payload = new CameraCommandPayload
            {
                Op = CameraCommandOps.Add,
                ClientRequestId = reqId,
                Type = string.IsNullOrEmpty(type) ? CameraTypeNames.Perspective : type,
                DisplayName = string.IsNullOrEmpty(displayName) ? null : displayName,
            };
            var r = cmd.PublishEvent(CameraIpcTopics.CameraCommand, payload);
            return Report("AddCamera", r.Success,
                r.Success
                    ? $"sent (type={payload.Type}, name={payload.DisplayName ?? "<auto>"}, reqId={reqId}). Verify with DumpCameraAdapter."
                    : $"send failed: {r.Error}");
        }

        /// <summary>カメラを削除（event camera/command, op=delete）。</summary>
        public static string DeleteCamera(string cameraId)
        {
            if (!RequirePlayMode(out var guard)) return guard;
            var cmd = Cmd();
            if (cmd == null) return Report("DeleteCamera", false, "CommandClient is null.");
            if (string.IsNullOrEmpty(cameraId)) return Report("DeleteCamera", false, "cameraId is empty.");

            var payload = new CameraCommandPayload
            {
                Op = CameraCommandOps.Delete,
                ClientRequestId = Guid.NewGuid().ToString("N"),
                CameraId = cameraId,
            };
            var r = cmd.PublishEvent(CameraIpcTopics.CameraCommand, payload);
            return Report("DeleteCamera", r.Success, r.Success ? $"sent (id={cameraId})." : $"send failed: {r.Error}");
        }

        /// <summary>アクティブカメラを切替（event camera/command, op=active-set）。</summary>
        public static string SetActiveCamera(string cameraId)
        {
            if (!RequirePlayMode(out var guard)) return guard;
            var cmd = Cmd();
            if (cmd == null) return Report("SetActiveCamera", false, "CommandClient is null.");
            if (string.IsNullOrEmpty(cameraId)) return Report("SetActiveCamera", false, "cameraId is empty.");

            var payload = new CameraCommandPayload
            {
                Op = CameraCommandOps.ActiveSet,
                ClientRequestId = Guid.NewGuid().ToString("N"),
                CameraId = cameraId,
            };
            var r = cmd.PublishEvent(CameraIpcTopics.CameraCommand, payload);
            return Report("SetActiveCamera", r.Success, r.Success ? $"sent (id={cameraId})." : $"send failed: {r.Error}");
        }

        /// <summary>カメラ単位 Volume の有効/無効（state camera/{id}/volume/enabled, bool）。</summary>
        public static string SetVolumeEnabled(string cameraId, bool enabled)
        {
            if (!RequirePlayMode(out var guard)) return guard;
            var cmd = Cmd();
            if (cmd == null) return Report("SetVolumeEnabled", false, "CommandClient is null.");
            if (string.IsNullOrEmpty(cameraId)) return Report("SetVolumeEnabled", false, "cameraId is empty.");

            var r = cmd.PublishState(CameraIpcTopics.VolumeEnabled(cameraId), enabled);
            return Report("SetVolumeEnabled", r.Success, r.Success ? $"sent (id={cameraId}, enabled={enabled})." : $"send failed: {r.Error}");
        }

        /// <summary>Volume override を追加（event camera/{id}/volume/command, op=override-add）。type 例: "Bloom"。</summary>
        public static string AddVolumeOverride(string cameraId, string overrideType)
        {
            if (!RequirePlayMode(out var guard)) return guard;
            var cmd = Cmd();
            if (cmd == null) return Report("AddVolumeOverride", false, "CommandClient is null.");
            if (string.IsNullOrEmpty(cameraId) || string.IsNullOrEmpty(overrideType))
                return Report("AddVolumeOverride", false, "cameraId/overrideType is empty.");

            var payload = new VolumeCommandPayload { Op = VolumeCommandOps.OverrideAdd, OverrideType = overrideType };
            var r = cmd.PublishEvent(CameraIpcTopics.VolumeCommand(cameraId), payload);
            return Report("AddVolumeOverride", r.Success, r.Success ? $"sent (id={cameraId}, type={overrideType})." : $"send failed: {r.Error}");
        }

        /// <summary>Volume override を削除（event camera/{id}/volume/command, op=override-remove）。</summary>
        public static string RemoveVolumeOverride(string cameraId, string overrideType)
        {
            if (!RequirePlayMode(out var guard)) return guard;
            var cmd = Cmd();
            if (cmd == null) return Report("RemoveVolumeOverride", false, "CommandClient is null.");
            if (string.IsNullOrEmpty(cameraId) || string.IsNullOrEmpty(overrideType))
                return Report("RemoveVolumeOverride", false, "cameraId/overrideType is empty.");

            var payload = new VolumeCommandPayload { Op = VolumeCommandOps.OverrideRemove, OverrideType = overrideType };
            var r = cmd.PublishEvent(CameraIpcTopics.VolumeCommand(cameraId), payload);
            return Report("RemoveVolumeOverride", r.Success, r.Success ? $"sent (id={cameraId}, type={overrideType})." : $"send failed: {r.Error}");
        }

        // ---- Preset（event camera/preset/command） ----

        public static string CreateCameraPreset(string name)
            => PublishPreset("CreateCameraPreset", new PresetCommandPayload { Op = PresetCommandOps.Create, Name = name });

        public static string ActivateCameraPreset(string name)
            => PublishPreset("ActivateCameraPreset", new PresetCommandPayload { Op = PresetCommandOps.Activate, Name = name });

        public static string DeleteCameraPreset(string name)
            => PublishPreset("DeleteCameraPreset", new PresetCommandPayload { Op = PresetCommandOps.Delete, Name = name });

        public static string RenameCameraPreset(string oldName, string newName)
            => PublishPreset("RenameCameraPreset", new PresetCommandPayload { Op = PresetCommandOps.Rename, Name = oldName, NewName = newName });

        public static string DuplicateCameraPreset(string sourceName, string newName)
            => PublishPreset("DuplicateCameraPreset", new PresetCommandPayload { Op = PresetCommandOps.Duplicate, SourceName = sourceName, Name = newName });

        // ---- Preview（event camera/preview/command） ----

        /// <summary>指定カメラ群のプレビューを開始（attach）。</summary>
        public static string StartPreview(IReadOnlyList<string> cameraIds, int width, int height, int fps)
        {
            if (!RequirePlayMode(out var guard)) return guard;
            var cmd = Cmd();
            if (cmd == null) return Report("StartPreview", false, "CommandClient is null.");
            if (cameraIds == null || cameraIds.Count == 0) return Report("StartPreview", false, "no cameraIds.");

            var payload = new PreviewCommandPayload
            {
                Op = PreviewCommandOps.Attach,
                CameraIds = cameraIds,
                Size = new[] { width, height },
                Fps = fps,
            };
            var r = cmd.PublishEvent(CameraIpcTopics.PreviewCommand, payload);
            return Report("StartPreview", r.Success, r.Success ? $"sent (ids=[{string.Join(",", cameraIds)}], {width}x{height}@{fps})." : $"send failed: {r.Error}");
        }

        /// <summary>指定カメラ群のプレビューを停止（detach）。</summary>
        public static string StopPreview(IReadOnlyList<string> cameraIds)
        {
            if (!RequirePlayMode(out var guard)) return guard;
            var cmd = Cmd();
            if (cmd == null) return Report("StopPreview", false, "CommandClient is null.");
            if (cameraIds == null || cameraIds.Count == 0) return Report("StopPreview", false, "no cameraIds.");

            var payload = new PreviewCommandPayload { Op = PreviewCommandOps.Detach, CameraIds = cameraIds };
            var r = cmd.PublishEvent(CameraIpcTopics.PreviewCommand, payload);
            return Report("StopPreview", r.Success, r.Success ? $"sent (ids=[{string.Join(",", cameraIds)}])." : $"send failed: {r.Error}");
        }

        // ===== 検証（出力アダプタ診断の同期読み取り） =====

        /// <summary>camera-switcher-output-adapter の診断（カメラ数・アクティブ・OSC 受信状態）を読む。</summary>
        public static string DumpCameraAdapter()
        {
            var snap = Demo()?.CameraHost?.Diagnostics?.GetSnapshot();
            if (snap == null) return Report("DumpCameraAdapter", false, "Camera adapter / diagnostics is null (scene not running?).");
            var s = snap.Value;
            return Report("DumpCameraAdapter", true,
                $"Status={s.AdapterStatus}, CameraCount={s.CameraCount}, Active={s.ActiveCameraId ?? "<none>"}, " +
                $"Cameras=[{string.Join(",", s.Cameras)}], Osc={s.OscReceiverStatus}@{s.OscReceiveHost}:{s.OscReceivePort}, " +
                $"OscFramesReceived={s.OscFramesReceived}, OscFramesApplied={s.OscFramesApplied}, " +
                $"LastApplied={s.LastAppliedCameraId ?? "<none>"}, IpcHandlers={s.IpcStaticHandlerCount}");
        }

        // ===== 無引数の便利メソッド（uloop からの quote-free 実行用） =====

        public static string AddPerspectiveCamera() => AddCamera(CameraTypeNames.Perspective, "VtsDebugCam");
        public static string AddOrthographicCamera() => AddCamera(CameraTypeNames.Orthographic, "VtsDebugOrtho");

        public static string DeleteLastCamera()
        {
            var id = LastCameraId();
            return id == null ? Report("DeleteLastCamera", false, "no cameras to delete.") : DeleteCamera(id);
        }

        public static string ActivateLastCamera()
        {
            var id = LastCameraId();
            return id == null ? Report("ActivateLastCamera", false, "no cameras to activate.") : SetActiveCamera(id);
        }

        public static string CreateCameraPresetDemo() => CreateCameraPreset("VtsDebugPreset");

        public static string StartPreviewAll()
        {
            var ids = AllCameraIds();
            return ids.Count == 0 ? Report("StartPreviewAll", false, "no cameras.") : StartPreview(ids, 320, 180, 15);
        }

        public static string StopPreviewAll()
        {
            var ids = AllCameraIds();
            return ids.Count == 0 ? Report("StopPreviewAll", false, "no cameras.") : StopPreview(ids);
        }

        public static string AddBloomToLastCamera()
        {
            var id = LastCameraId();
            return id == null ? Report("AddBloomToLastCamera", false, "no cameras.") : AddVolumeOverride(id, "Bloom");
        }

        public static string EnableVolumeOnLastCamera()
        {
            var id = LastCameraId();
            return id == null ? Report("EnableVolumeOnLastCamera", false, "no cameras.") : SetVolumeEnabled(id, true);
        }

        // ===== 内部ヘルパ =====

        private static string PublishPreset(string op, PresetCommandPayload payload)
        {
            if (!RequirePlayMode(out var guard)) return guard;
            var cmd = Cmd();
            if (cmd == null) return Report(op, false, "CommandClient is null.");
            var r = cmd.PublishEvent(CameraIpcTopics.PresetCommand, payload);
            return Report(op, r.Success, r.Success ? $"sent (op={payload.Op}, name={payload.Name})." : $"send failed: {r.Error}");
        }

        private static IReadOnlyList<string> AllCameraIds()
        {
            var snap = Demo()?.CameraHost?.Diagnostics?.GetSnapshot();
            return snap?.Cameras ?? (IReadOnlyList<string>)Array.Empty<string>();
        }

        private static string? LastCameraId()
        {
            var ids = AllCameraIds();
            return ids.Count > 0 ? ids[ids.Count - 1] : null;
        }
    }
}
