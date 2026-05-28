#nullable enable
using System.Collections.Generic;
using System.Linq;
using VTuberSystemBase.StageLightingVolumeTab.Contracts;

namespace VtsApiDebug
{
    /// <summary>
    /// §N Stage / Light / Volume タブ → stage-lighting-volume-output-adapter の IPC 操作。
    /// 送信はシェルの CommandClient で documented topic（StageLightingTopics）に publish/event。
    /// アダプタは IOutputCommandDispatcher 経由でハンドラ登録しており、IntegratedDemoBootstrap の
    /// バス→Dispatcher ブリッジ（bug#2 修正）に乗って届く。
    ///
    /// Light の id はアダプタ側が採番するため、UI 側では <see cref="SubscribeStage"/> で
    /// lights/list / stage/current を購読キャッシュし、削除・プロパティ操作の対象 id を解決する。
    /// 検証は出力側の <see cref="DumpStageAdapter"/>（LightCount 等）と本クラスの <see cref="DumpStageState"/>。
    /// </summary>
    public static partial class UiApiDebugHub
    {
        private static readonly List<LightListItemDto> _stageLights = new List<LightListItemDto>();
        private static string? _stageCurrentKey;
        private static bool _stageSubscribed;

        // ===== 購読（id 解決用。操作の前に 1 度呼ぶ） ============================

        /// <summary>lights/list・stage/current・light/added・light/error を購読し UI 側状態をキャッシュする。</summary>
        public static string SubscribeStage()
        {
            if (!RequirePlayMode(out var guard)) return guard;
            var sub = Sub();
            if (sub == null) return Report("SubscribeStage", false, "SubscriptionClient is null (shell not running).");
            if (_stageSubscribed) return Report("SubscribeStage", true, "already subscribed.");

            sub.Subscribe<LightListDto>(StageLightingTopics.LightsList,
                VTuberSystemBase.UiToolkitShell.Commands.MessageKind.State, env =>
                {
                    _stageLights.Clear();
                    if (env.Payload.Items != null) _stageLights.AddRange(env.Payload.Items);
                });
            sub.Subscribe<StageCurrentDto>(StageLightingTopics.StageCurrent,
                VTuberSystemBase.UiToolkitShell.Commands.MessageKind.State, env =>
                {
                    _stageCurrentKey = env.Payload.AddressableKey;
                });
            sub.Subscribe<LightAddedDto>(StageLightingTopics.LightAdded,
                VTuberSystemBase.UiToolkitShell.Commands.MessageKind.Event, env =>
                {
                    Report("light/added", true, $"id={env.Payload.LightId}");
                });
            sub.Subscribe<LightErrorDto>(StageLightingTopics.LightError,
                VTuberSystemBase.UiToolkitShell.Commands.MessageKind.Event, env =>
                {
                    Report("light/error", false, $"id={env.Payload.LightId}, code={env.Payload.ErrorCode}");
                });

            _stageSubscribed = true;
            return Report("SubscribeStage", true, "subscribed (lights/list, stage/current, light/added, light/error).");
        }

        /// <summary>UI 側にキャッシュした Stage 状態（現在ステージ・ライト一覧）を読む。</summary>
        public static string DumpStageState()
        {
            var lights = string.Join(", ", _stageLights.Select(l => $"{l.LightId}({l.Type})"));
            return Report("DumpStageState", true,
                $"Subscribed={_stageSubscribed}, CurrentStage={_stageCurrentKey ?? "<none>"}, " +
                $"Lights({_stageLights.Count})=[{lights}]");
        }

        // ===== Stage（読込・解除） ==============================================

        /// <summary>ステージを読み込む（event stage/command, op=load）。catalog 未ビルド時は load-failed になる。</summary>
        public static string LoadStage(string addressableKey)
        {
            if (!RequirePlayMode(out var guard)) return guard;
            var cmd = Cmd();
            if (cmd == null) return Report("LoadStage", false, "CommandClient is null.");
            if (string.IsNullOrEmpty(addressableKey)) return Report("LoadStage", false, "addressableKey is empty.");

            var r = cmd.PublishEvent(StageLightingTopics.StageCommand, new StageCommandDto("load", addressableKey));
            return Report("LoadStage", r.Success, r.Success ? $"sent (key={addressableKey})." : $"send failed: {r.Error}");
        }

        /// <summary>ステージを解除する（event stage/command, op=unload）。</summary>
        public static string UnloadStage()
        {
            if (!RequirePlayMode(out var guard)) return guard;
            var cmd = Cmd();
            if (cmd == null) return Report("UnloadStage", false, "CommandClient is null.");

            var r = cmd.PublishEvent(StageLightingTopics.StageCommand, new StageCommandDto("unload", null));
            return Report("UnloadStage", r.Success, r.Success ? "sent (unload)." : $"send failed: {r.Error}");
        }

        // ===== Light（追加・削除・プロパティ） ==================================

        /// <summary>ライトを追加（event light/command, op=add）。id はアダプタが採番し lights/list に反映される。</summary>
        public static string AddLight(LightTypeDto type, string displayName, float intensity)
        {
            if (!RequirePlayMode(out var guard)) return guard;
            var cmd = Cmd();
            if (cmd == null) return Report("AddLight", false, "CommandClient is null.");

            var initial = new LightInitialDto(
                Type: type,
                Rotation: new Vector3Dto(50f, -30f, 0f),
                Color: new ColorDto(1f, 1f, 1f, 1f),
                Intensity: intensity < 0f ? 0f : intensity,
                Range: 10f,
                SpotAngle: 30f,
                DisplayName: string.IsNullOrWhiteSpace(displayName) ? "VtsDebugLight" : displayName);

            var r = cmd.PublishEvent(StageLightingTopics.LightCommand, new LightCommandDto("add", null, initial));
            return Report("AddLight", r.Success,
                r.Success ? $"sent (type={type}, name={initial.DisplayName}). Verify with DumpStageAdapter / DumpStageState." : $"send failed: {r.Error}");
        }

        /// <summary>ライトを削除（event light/command, op=remove）。</summary>
        public static string RemoveLight(string lightId)
        {
            if (!RequirePlayMode(out var guard)) return guard;
            var cmd = Cmd();
            if (cmd == null) return Report("RemoveLight", false, "CommandClient is null.");
            if (string.IsNullOrEmpty(lightId)) return Report("RemoveLight", false, "lightId is empty.");

            var r = cmd.PublishEvent(StageLightingTopics.LightCommand, new LightCommandDto("remove", lightId, null));
            return Report("RemoveLight", r.Success, r.Success ? $"sent (id={lightId})." : $"send failed: {r.Error}");
        }

        public static string SetLightIntensity(string lightId, float intensity)
            => PublishLightState("SetLightIntensity", lightId, StageLightingTopics.PropertyIntensity, intensity);

        public static string SetLightColor(string lightId, float r, float g, float b, float a)
            => PublishLightState("SetLightColor", lightId, StageLightingTopics.PropertyColor, new ColorDto(r, g, b, a));

        public static string SetLightRotation(string lightId, float x, float y, float z)
            => PublishLightState("SetLightRotation", lightId, StageLightingTopics.PropertyRotation, new Vector3Dto(x, y, z));

        public static string SetLightType(string lightId, LightTypeDto type)
            => PublishLightState("SetLightType", lightId, StageLightingTopics.PropertyType, type);

        public static string SetLightRange(string lightId, float range)
            => PublishLightState("SetLightRange", lightId, StageLightingTopics.PropertyRange, range);

        public static string SetLightSpotAngle(string lightId, float angle)
            => PublishLightState("SetLightSpotAngle", lightId, StageLightingTopics.PropertySpotAngle, angle);

        public static string SetLightDisplayName(string lightId, string name)
            => PublishLightState("SetLightDisplayName", lightId, StageLightingTopics.PropertyDisplayName, name);

        // ===== Volume override ==================================================

        /// <summary>Volume override 効果の有効/無効（state volume/override/{type}/enabled, bool）。</summary>
        public static string SetVolumeOverrideEnabled(string typeFullName, bool enabled)
        {
            if (!RequirePlayMode(out var guard)) return guard;
            var cmd = Cmd();
            if (cmd == null) return Report("SetVolumeOverrideEnabled", false, "CommandClient is null.");
            if (string.IsNullOrEmpty(typeFullName)) return Report("SetVolumeOverrideEnabled", false, "typeFullName is empty.");

            var r = cmd.PublishState(StageLightingTopics.VolumeOverrideEnabled(typeFullName), enabled);
            return Report("SetVolumeOverrideEnabled", r.Success, r.Success ? $"sent (type={typeFullName}, enabled={enabled})." : $"send failed: {r.Error}");
        }

        /// <summary>Volume override の float パラメータ（state volume/override/{type}/{param}）。</summary>
        public static string SetVolumeOverrideFloat(string typeFullName, string paramName, float value)
        {
            if (!RequirePlayMode(out var guard)) return guard;
            var cmd = Cmd();
            if (cmd == null) return Report("SetVolumeOverrideFloat", false, "CommandClient is null.");
            if (string.IsNullOrEmpty(typeFullName) || string.IsNullOrEmpty(paramName))
                return Report("SetVolumeOverrideFloat", false, "typeFullName/paramName is empty.");

            var dto = new VolumeOverrideParamValueDto(ParamKind.Float, null, null, value, null, null, null);
            var r = cmd.PublishState(StageLightingTopics.VolumeOverrideParam(typeFullName, paramName), dto);
            return Report("SetVolumeOverrideFloat", r.Success, r.Success ? $"sent (type={typeFullName}, param={paramName}, value={value})." : $"send failed: {r.Error}");
        }

        // ===== 無引数の便利メソッド（uloop からの quote-free 実行用） ============

        public static string AddDirectionalLight() => AddLight(LightTypeDto.Directional, "VtsDebugDir", 1f);
        public static string AddPointLight() => AddLight(LightTypeDto.Point, "VtsDebugPoint", 1f);
        public static string AddSpotLight() => AddLight(LightTypeDto.Spot, "VtsDebugSpot", 1f);

        public static string RemoveLastLight()
        {
            var id = LastStageLightId();
            return id == null ? Report("RemoveLastLight", false, "no cached lights (call SubscribeStage and AddLight first).") : RemoveLight(id);
        }

        public static string SetLastLightIntensityHigh()
        {
            var id = LastStageLightId();
            return id == null ? Report("SetLastLightIntensityHigh", false, "no cached lights.") : SetLightIntensity(id, 4f);
        }

        public static string SetLastLightColorRed()
        {
            var id = LastStageLightId();
            return id == null ? Report("SetLastLightColorRed", false, "no cached lights.") : SetLightColor(id, 1f, 0f, 0f, 1f);
        }

        // ===== 内部ヘルパ =======================================================

        private static string PublishLightState<TPayload>(string op, string lightId, string property, TPayload value)
        {
            if (!RequirePlayMode(out var guard)) return guard;
            var cmd = Cmd();
            if (cmd == null) return Report(op, false, "CommandClient is null.");
            if (string.IsNullOrEmpty(lightId)) return Report(op, false, "lightId is empty.");

            var r = cmd.PublishState(StageLightingTopics.LightProperty(lightId, property), value);
            return Report(op, r.Success, r.Success ? $"sent (id={lightId}, {property}={value})." : $"send failed: {r.Error}");
        }

        private static string? LastStageLightId()
            => _stageLights.Count > 0 ? _stageLights[_stageLights.Count - 1].LightId : null;
    }
}
