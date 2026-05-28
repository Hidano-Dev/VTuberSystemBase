#nullable enable
using System.Linq;
using System.Text;

namespace VtsApiDebug
{
    /// <summary>
    /// Phase2 Inspection: docs/ui-api-reference.md §B/C/E/F/H/K/L の「読み取り専用」診断ダンプ。
    /// IPC コマンド経路に依存しない（送信しない）ため低リスクで、シェル / 出力シーン / 各アダプタの
    /// 現在状態をその場で同期読み取りして 1 行サマリで返す。
    ///
    /// 取得元:
    /// - シェル側（§B/C/F/H）: <see cref="UiApiDebugHub.ActiveShell"/>（UiShellBootstrapper）の公開アクセサ。
    /// - 出力側（§K/L）: シーン上の IntegratedDemoBootstrap → OutputScene / RacHost / StageHost 診断。
    /// Camera アダプタ（§L-4）は UiApiDebugHub.Camera.cs の DumpCameraAdapter を参照。
    /// </summary>
    public static partial class UiApiDebugHub
    {
        // ===== §B / §C シェル構成・スキン資産 ====================================

        /// <summary>UI シェル構成（SkinProfile / 表示先 / バス / スキン UXML 資産）を読む。</summary>
        public static string DumpShellConfig()
        {
            var demo = Demo();
            var shell = ActiveShell();
            var sb = new StringBuilder();

            var config = demo?.Config;
            var profile = config?.SkinProfile;
            sb.Append("SkinProfile=").Append(profile == null ? "<null>" : profile.name);
            sb.Append(", UiTargetDisplay=").Append(config == null ? "<n/a>" : config.UiTargetDisplay.ToString());
            sb.Append(", EffectiveTargetDisplay=")
              .Append(shell?.EffectiveTargetDisplay?.ToString() ?? "<n/a>");
            sb.Append(", DisplayStrategy=")
              .Append(shell?.DisplayAssignmentStrategy == null ? "<null>" : shell.DisplayAssignmentStrategy.GetType().Name);
            sb.Append(", IpcBus=").Append(demo?.BusProvider?.Bus == null ? "<null>" : "ok");
            sb.Append(", PanelSettings=").Append(shell?.PanelSettings == null ? "<null>" : "ok");
            sb.Append(", RootVE=").Append(shell?.RootVisualElement == null ? "<null>" : "ok");

            if (profile != null)
            {
                sb.Append(" | Skin{Root=").Append(AssetName(profile.RootVisualTreeAsset));
                sb.Append(", Char=").Append(AssetName(profile.CharacterTabVisualTreeAsset));
                sb.Append(", Stage=").Append(AssetName(profile.StageLightingTabVisualTreeAsset));
                sb.Append(", Cam=").Append(AssetName(profile.CameraSwitcherTabVisualTreeAsset)).Append('}');
            }
            sb.Append(" | OSC ").Append(config?.CameraOscHost ?? "<n/a>").Append(':').Append(config?.CameraOscPort.ToString() ?? "?");

            return Report("DumpShellConfig", demo != null, sb.ToString());
        }

        /// <summary>起動時のスキン検証結果（必須 USS クラスの欠落一覧）を読む（§C-3）。</summary>
        public static string DumpSkinValidation()
        {
            var shell = ActiveShell();
            if (shell == null) return Report("DumpSkinValidation", false, "shell bootstrapper is null (shell not running).");
            var report = shell.SkinValidationReport;
            if (!report.HasValue) return Report("DumpSkinValidation", false, "SkinValidationReport is null (skin validation not run).");

            var r = report.Value;
            if (r.AllValid) return Report("DumpSkinValidation", true, "AllValid=true (no missing selectors).");

            var issues = string.Join(", ", r.Issues.Select(i =>
                $"{i.MissingSelector}[{(i.TabId.HasValue ? i.TabId.Value.ToString() : "root")}]"));
            return Report("DumpSkinValidation", false, $"AllValid=false, missing=[{issues}]");
        }

        // ===== §E / §D タブのライフサイクル状態 ==================================

        /// <summary>タブの状態（アクティブ / プリロード進捗 / 失敗タブ）を読む。</summary>
        public static string DumpTabStates()
        {
            var registry = ActiveRegistry();
            if (registry == null) return Report("DumpTabStates", false, "TabPanelRegistry is null (shell not running).");

            var sb = new StringBuilder();
            sb.Append("ActiveTab=").Append(registry.ActiveTab?.ToString() ?? "<none>");
            sb.Append(", TotalTabCount=").Append(registry.TotalTabCount);
            sb.Append(", IsPreloadComplete=").Append(registry.IsPreloadComplete);
            try
            {
                var p = registry.GetPreloadProgress();
                sb.Append(", Loaded=").Append(p.LoadedCount).Append('/').Append(p.TotalCount);
                sb.Append(", Failed=[").Append(string.Join(",", p.FailedTabs.Select(t => t.ToString()))).Append(']');
            }
            catch { sb.Append(", (preload detail unavailable)"); }

            return Report("DumpTabStates", true, sb.ToString());
        }

        // ===== §F Addressables ローダー =========================================

        /// <summary>アセットローダーの稼働カウンタ（pending / completed / failed）を読む。</summary>
        public static string DumpAssetLoader()
        {
            var shell = ActiveShell();
            var loader = shell?.AssetLoader;
            if (loader == null) return Report("DumpAssetLoader", false, "AssetLoader is null (shell not running).");

            var s = loader.GetSnapshot();
            var byScope = string.Join(", ", s.PendingByScope.Select(kv => $"{kv.Key}={kv.Value}"));
            return Report("DumpAssetLoader", true,
                $"Pending={s.PendingCount}, Completed={s.CompletedCount}, Failed={s.FailedCount}, PendingByScope=[{byScope}]");
        }

        // ===== §H 接続状態（UI 側ファサード） ==================================

        /// <summary>UI 側 IConnectionStatus（接続中か / 現在のステータスコード）を読む。</summary>
        public static string DumpConnection()
        {
            var shell = ActiveShell();
            var status = shell?.ConnectionStatus;
            if (status == null) return Report("DumpConnection", false, "ConnectionStatus is null (shell not running).");

            return Report("DumpConnection", true,
                $"IsConnected={status.IsConnected}, CurrentStatus={status.CurrentStatus}");
        }

        // ===== §K 出力シーン診断 ================================================

        /// <summary>出力シーン（Display 2+）の初期化フェーズ・表示割当・ハンドラ数・直近エラーを読む。</summary>
        public static string DumpOutputScene()
        {
            var diag = Demo()?.OutputScene?.Diagnostics;
            if (diag == null) return Report("DumpOutputScene", false, "OutputScene diagnostics is null (scene not running?).");

            var da = diag.CurrentDisplayAssignment;
            var sb = new StringBuilder();
            sb.Append("Phase=").Append(diag.CurrentPhase);
            sb.Append(", Display{req=").Append(da.RequestedDisplayIndex)
              .Append(", eff=").Append(da.EffectiveDisplayIndex)
              .Append(", fallback=").Append(da.IsFallbackActive)
              .Append(", editorLimited=").Append(da.IsEditorLimitedMode).Append('}');
            sb.Append(", Handlers=").Append(diag.RegisteredHandlerCount);
            sb.Append(", LastError=").Append(string.IsNullOrEmpty(diag.LastErrorMessage) ? "<none>" : diag.LastErrorMessage);
            return Report("DumpOutputScene", diag.CurrentPhase != VTuberSystemBase.OutputRendererShell.Abstractions.OutputSceneInitPhase.Failed, sb.ToString());
        }

        // ===== §L 各アダプタ診断 ================================================

        /// <summary>RAC（Character）アダプタの稼働状態・スロット数・カタログ数を読む（§L-1）。</summary>
        public static string DumpRacAdapter()
        {
            var bootstrapper = Demo()?.RacHost?.Bootstrapper;
            if (bootstrapper == null) return Report("DumpRacAdapter", false, "RAC adapter Bootstrapper is null (not initialized?).");

            var s = bootstrapper.Diagnostics.Capture();
            return Report("DumpRacAdapter", bootstrapper.IsRunning,
                $"IsRunning={bootstrapper.IsRunning}, Phase={s.PhaseName}, Handlers={s.RegisteredHandlerCount}, " +
                $"ActiveSlots={s.ActiveSlotCount}, ErrorSlots={s.ErrorSlotCount}, AvatarCatalog={s.AvatarCatalogSize}, " +
                $"LastError={(string.IsNullOrEmpty(s.LastErrorMessage) ? "<none>" : s.LastErrorMessage)}");
        }

        /// <summary>Stage/Light/Volume アダプタの ready 状態・ライト数・ステージキーを読む（§L-3）。</summary>
        public static string DumpStageAdapter()
        {
            var diag = Demo()?.StageHost?.Diagnostics;
            if (diag == null) return Report("DumpStageAdapter", false, "Stage adapter diagnostics is null (not initialized?).");

            var s = diag.Capture();
            return Report("DumpStageAdapter", s.IsReady,
                $"IsReady={s.IsReady}, Handlers={s.RegisteredHandlerCount}, Stage={s.CurrentStageAddressableKey ?? "<none>"}, " +
                $"Lights={s.LightCount}, VolumeOverrides={s.VolumeOverrideTypeCount}, PreviewHost={s.PreviewHostReady}, " +
                $"LastError={(string.IsNullOrEmpty(s.LastErrorMessage) ? "<none>" : s.LastErrorMessage)}");
        }

        // ===== まとめ（uloop からの 1 発取得用） ================================

        /// <summary>Phase2 の全 Inspection を順に実行して結合した結果を返す。</summary>
        public static string DumpAllDiagnostics()
        {
            var lines = new[]
            {
                DumpShellConfig(),
                DumpSkinValidation(),
                DumpTabStates(),
                DumpAssetLoader(),
                DumpConnection(),
                DumpOutputScene(),
                DumpRacAdapter(),
                DumpStageAdapter(),
                DumpCameraAdapter(),
            };
            return string.Join("\n", lines);
        }

        // ===== helpers ==========================================================

        private static string AssetName(UnityEngine.Object? asset) => asset == null ? "<null>" : asset.name;
    }
}
