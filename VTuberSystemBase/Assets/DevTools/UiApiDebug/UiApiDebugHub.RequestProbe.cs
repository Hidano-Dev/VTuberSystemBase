#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using VTuberSystemBase.CameraSwitcherTab.Contracts;
using VTuberSystemBase.CoreIpc.Abstractions;
using VTuberSystemBase.CoreIpc.Core;

namespace VtsApiDebug
{
    /// <summary>
    /// request/response（往復）が同一プロセス（WebSocket loopback）で実際に成立するかを
    /// 実証するためのプローブ。production を一切変更せず、バス（ICoreIpcBus）に直接
    /// RegisterRequestHandler した handler を RequestAsync で叩いて往復を確認する。
    ///
    /// これが往復すれば「バスは _outbound 経由で response を送り返せる」＝ responseSink を
    /// 結線すれば Dispatcher 経由の往復も復活できる、という確証になる。
    ///
    /// dispatchQueue 配送が player loop（main thread）で回るため、main thread を塞ぐ同期待ちは
    /// デッドロックする。よって「投げる（ProbeBusRequestResponse）→ 少し待つ → 結果確認
    /// （DumpProbeResult）」の 2 段構えにしている（AddCamera→DumpCameraAdapter と同じ流儀）。
    /// </summary>
    public static partial class UiApiDebugHub
    {
        private const string ProbeTopic = "vtsdebug/probe/echo";

        private static IDisposable? _probeReg;
        private static string _probeResult = "(not run)";
        private static int _probeHandlerHits;

        /// <summary>バスに echo handler を登録し、自分で request を投げる（結果は DumpProbeResult で確認）。</summary>
        public static string ProbeBusRequestResponse()
        {
            if (!RequirePlayMode(out var guard)) return guard;
            var bus = CoreIpcRuntime.Current?.Bus;
            if (bus == null) return Report("ProbeBusRequestResponse", false, "Bus is null (runtime not initialized).");

            _probeResult = "(pending)";
            Interlocked.Exchange(ref _probeHandlerHits, 0);
            try
            {
                _probeReg?.Dispose();
                _probeReg = bus.RegisterRequestHandler<string, string>(ProbeTopic, (req, ct) =>
                {
                    Interlocked.Increment(ref _probeHandlerHits);
                    return Task.FromResult("echo:" + req);
                });
            }
            catch (Exception ex)
            {
                return Report("ProbeBusRequestResponse", false, "RegisterRequestHandler threw: " + ex.Message);
            }

            // 投げっぱなし: main thread を塞がない（dispatchQueue は player loop で回る）。
            _ = RunProbeAsync(bus);
            return Report("ProbeBusRequestResponse", true,
                $"request sent on '{ProbeTopic}'. Wait ~1s then call DumpProbeResult.");
        }

        private static async Task RunProbeAsync(ICoreIpcBus bus)
        {
            try
            {
                var r = await bus.RequestAsync<string, string>(
                    ProbeTopic, "ping", new RequestOptions(TimeSpan.FromSeconds(5)));
                _probeResult = r.Success
                    ? $"OK resp='{r.Value}' handlerHits={Volatile.Read(ref _probeHandlerHits)}"
                    : $"FAIL error={r.Error} handlerHits={Volatile.Read(ref _probeHandlerHits)}";
            }
            catch (Exception ex)
            {
                _probeResult = "EXCEPTION " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        /// <summary>直近の request/response プローブの結果を読む。</summary>
        public static string DumpProbeResult()
        {
            var ok = _probeResult.StartsWith("OK", StringComparison.Ordinal);
            return Report("DumpProbeResult", ok, _probeResult);
        }

        /// <summary>プローブの echo handler を解除する。</summary>
        public static string CleanupProbe()
        {
            try { _probeReg?.Dispose(); } catch { /* defensive */ }
            _probeReg = null;
            return Report("CleanupProbe", true, "echo handler disposed.");
        }

        // ===== responseSink 経由（Dispatcher 経由）の往復実証 =====
        // 上の echo プローブはバス直結なので responseSink を通らない。こちらは出力側アダプタが
        // IOutputCommandDispatcher.RegisterRequestHandler で登録した実機能（camera の volume schema
        // 取得）を UI から叩き、responseSink 結線が機能して応答が往復するかを確認する。

        private static string _volumeMetaResult = "(not run)";

        /// <summary>
        /// 直近カメラの volume override schema を request する（camera/{id}/volume/overrides/metadata）。
        /// 事前に AddPerspectiveCamera 等でカメラを 1 つ用意しておくこと。結果は DumpVolumeMetaResult で確認。
        /// </summary>
        public static string RequestVolumeMetadataOnLastCamera()
        {
            if (!RequirePlayMode(out var guard)) return guard;
            var cmd = Cmd();
            if (cmd == null) return Report("RequestVolumeMetadataOnLastCamera", false, "CommandClient is null (shell not running).");
            var id = LastCameraId();
            if (id == null) return Report("RequestVolumeMetadataOnLastCamera", false, "no cameras; run AddPerspectiveCamera first.");

            _volumeMetaResult = "(pending)";
            var topic = CameraIpcTopics.VolumeOverridesMetadata(id);
            _ = RunVolumeMetaAsync(cmd, topic, id);
            return Report("RequestVolumeMetadataOnLastCamera", true,
                $"request sent on '{topic}'. Wait ~1s then call DumpVolumeMetaResult.");
        }

        private static async Task RunVolumeMetaAsync(
            VTuberSystemBase.UiToolkitShell.Commands.UiCommandClient cmd, string topic, string cameraId)
        {
            try
            {
                var req = new VolumeMetadataRequest { CameraId = cameraId };
                var r = await cmd.RequestAsync<VolumeMetadataRequest, VolumeMetadataResponse>(
                    topic, req, TimeSpan.FromSeconds(5));
                if (r.Success)
                {
                    var overrides = r.Response.Overrides;
                    _volumeMetaResult = $"OK overrideCount={(overrides?.Count ?? 0)} (round-trip via responseSink confirmed).";
                }
                else
                {
                    _volumeMetaResult = $"FAIL code={r.Error?.Code} detail={r.Error?.Detail}";
                }
            }
            catch (Exception ex)
            {
                _volumeMetaResult = "EXCEPTION " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        /// <summary>直近の volume metadata request の結果を読む。</summary>
        public static string DumpVolumeMetaResult()
        {
            var ok = _volumeMetaResult.StartsWith("OK", StringComparison.Ordinal);
            return Report("DumpVolumeMetaResult", ok, _volumeMetaResult);
        }
    }
}
