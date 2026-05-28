#nullable enable
using VTuberSystemBase.CameraSwitcherTab.Adapters.Osc;
using VTuberSystemBase.CameraSwitcherTab.Adapters.Ucapi;
using VTuberSystemBase.CameraSwitcherTab.Contracts;
using VTuberSystemBase.CameraSwitcherTab.Contracts.Results;

namespace VtsApiDebug
{
    /// <summary>
    /// §O-8 OSC (UCAPI Flat Record) 送信 → camera-switcher-output-adapter の受信。
    /// UI 側 emitter（<see cref="UoscFlatRecordEmitter"/>）+ serializer
    /// （<see cref="Ucapi4UnityFlatRecordSerializer"/>）を直接駆動し、UCAPI blob を
    /// <c>/ucapi/camera/{id}/flat</c> へ UDP 送信する。
    ///
    /// 偽成功の罠（UDP はポート不一致でも Send OK を返す）を避けるため、emitter の
    /// 送信先ポートは推測せず <see cref="DumpCameraAdapter"/> が露出する出力アダプタの
    /// 実際の受信 host/port（診断 snapshot 由来）に必ず一致させる。検証は
    /// <c>OscFramesReceived</c>/<c>OscFramesApplied</c>/<c>LastAppliedCameraId</c> の
    /// 読み戻しで往復を確認する（送信成功表示だけでは不十分）。
    /// </summary>
    public static partial class UiApiDebugHub
    {
        private static UoscFlatRecordEmitter? _oscEmitter;
        private static readonly Ucapi4UnityFlatRecordSerializer _oscSerializer = new();
        private static int _oscStartedPort = -1;

        // ===== emitter ライフサイクル =====

        /// <summary>
        /// emitter を（必要なら作り直して）出力アダプタの実際の受信 host/port へ向けて起動する。
        /// ポートを推測しないことが偽成功回避の肝。
        /// </summary>
        public static string StartOscEmitter()
        {
            return EnsureOscEmitterStarted(out _);
        }

        /// <summary>emitter を停止し hidden GameObject / socket を破棄する。</summary>
        public static string StopOscEmitter()
        {
            if (_oscEmitter == null) return Report("StopOscEmitter", true, "emitter was not started (no-op).");
            try { _oscEmitter.Dispose(); }
            catch (System.Exception ex) { return Report("StopOscEmitter", false, "dispose threw: " + ex.Message); }
            _oscEmitter = null;
            _oscStartedPort = -1;
            return Report("StopOscEmitter", true, "disposed.");
        }

        // ===== 送信（フル引数版） =====

        /// <summary>
        /// 指定カメラへ 1 フレーム分の <see cref="CameraSnapshot"/> を OSC 送信する。
        /// position は world m。rotation は identity、その他は妥当な既定値で組む。
        /// </summary>
        public static string SendOscToCamera(string cameraId, float posX, float posY, float posZ)
        {
            if (!RequirePlayMode(out var guard)) return guard;
            if (string.IsNullOrEmpty(cameraId)) return Report("SendOscToCamera", false, "cameraId is empty.");

            var startMsg = EnsureOscEmitterStarted(out var started);
            if (!started) return startMsg;

            if (!CameraId.TryCreate(cameraId, out var id))
                return Report("SendOscToCamera", false, $"invalid cameraId char class: '{cameraId}'.");

            var snapshot = new CameraSnapshot
            {
                CameraId = id,
                CameraType = CameraType.Perspective,
                PositionX = posX,
                PositionY = posY,
                PositionZ = posZ,
                RotationX = 0f,
                RotationY = 0f,
                RotationZ = 0f,
                RotationW = 1f,
                FocalLengthMm = 50f,
                SensorWidthMm = 36f,
                SensorHeightMm = 24f,
                NearClipM = 0.3f,
                FarClipM = 1000f,
                Aperture = 5.6f,
                FocusDistanceM = 10f,
                FrameCounter = 0u,
            };

            var sr = _oscSerializer.Serialize(snapshot);
            if (!sr.Success)
                return Report("SendOscToCamera", false, $"serialize failed: {sr.FailureReason} ({sr.FailureDetail}).");

            var address = OscAddressBuilder.BuildFlatAddress(cameraId);
            var er = _oscEmitter!.Send(address, sr.Record);
            if (!er.Success)
            {
                var f = er.Failure;
                return Report("SendOscToCamera", false, $"emit failed: {f?.Kind} ({f?.Detail}).");
            }

            return Report("SendOscToCamera", true,
                $"sent {address} pos=({posX},{posY},{posZ}). NOTE: UDP send-OK is not arrival; " +
                "verify round-trip with DumpCameraAdapter (OscFramesReceived/Applied should increase).");
        }

        // ===== 無引数の便利メソッド（uloop からの quote-free 実行用） =====

        /// <summary>
        /// 直近に追加されたカメラへ特徴的な position で OSC を 1 フレーム送る。
        /// 事前に AddPerspectiveCamera 等でカメラを 1 つ用意しておくこと。
        /// </summary>
        public static string SendOscToLastCameraDemo()
        {
            var id = LastCameraId();
            if (id == null) return Report("SendOscToLastCameraDemo", false, "no cameras; run AddPerspectiveCamera first.");
            return SendOscToCamera(id, 12.34f, 5.67f, -8.9f);
        }

        // ===== 内部ヘルパ =====

        private static string EnsureOscEmitterStarted(out bool started)
        {
            started = false;
            if (!RequirePlayMode(out var guard)) return guard;

            var snap = Demo()?.CameraHost?.Diagnostics?.GetSnapshot();
            if (snap == null) return Report("StartOscEmitter", false, "Camera adapter / diagnostics is null (scene not running?).");
            var host = snap.Value.OscReceiveHost;
            var port = snap.Value.OscReceivePort;

            if (_oscEmitter != null && _oscEmitter.State == OscEmitterState.Running && _oscStartedPort == port)
            {
                started = true;
                return Report("StartOscEmitter", true, $"already running -> {host}:{port} (recvStatus={snap.Value.OscReceiverStatus}).");
            }

            try { _oscEmitter?.Dispose(); } catch { /* defensive */ }
            _oscEmitter = new UoscFlatRecordEmitter("[VtsApiDebug.UoscClient]");

            // StartAsync は本実装では同期完結（Task.FromResult）なので GetResult はデッドロックしない。
            var r = _oscEmitter.StartAsync(host, port).GetAwaiter().GetResult();
            if (!r.Success)
            {
                var f = r.Failure;
                _oscEmitter = null;
                return Report("StartOscEmitter", false, $"start failed at {host}:{port}: {f?.Kind} ({f?.Detail}).");
            }

            _oscStartedPort = port;
            started = true;
            return Report("StartOscEmitter", true, $"started -> {host}:{port} (recvStatus={snap.Value.OscReceiverStatus}).");
        }
    }
}
