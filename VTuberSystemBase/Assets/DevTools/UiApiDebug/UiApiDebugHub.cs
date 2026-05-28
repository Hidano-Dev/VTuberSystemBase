#nullable enable
using System;
using System.Linq;
using System.Text;
using UnityEngine;
using VTuberSystemBase.CoreIpc.Core;
using VTuberSystemBase.CoreIpc.Core.Lifecycle;
using VTuberSystemBase.IntegratedDemo;
using VTuberSystemBase.UiToolkitShell.Bootstrap;
using VTuberSystemBase.UiToolkitShell.Commands;
using VTuberSystemBase.UiToolkitShell.Panels;

namespace VtsApiDebug
{
    /// <summary>
    /// docs/ui-api-reference.md の逆引き項目を「1 メソッド = 1 操作」で外部から実行するための
    /// デバッグ用ファサード。EditorWindow のボタンと uloop execute-dynamic-code の両方から
    /// 同じ static メソッドを呼ぶ。
    ///
    /// Editor 専用アセンブリ（VtsApiDebug.asmdef, includePlatforms=[Editor]）に属するため
    /// player ビルドには含まれない。PlayMode 中の Editor から呼ぶことを想定している。
    ///
    /// 返り値は人間/Claude が読みやすい 1 行サマリ文字列。検証の主役はスクリーンショットや
    /// Console であり、返り値は補助情報。すべての操作は [VtsApiDebug] プレフィックス付きで
    /// Debug.Log にも残すので uloop get-logs から後追いできる。
    /// </summary>
    public static partial class UiApiDebugHub
    {
        // ===== §A UI シェルの起動・停止 =========================================

        /// <summary>シェルの稼働状態・起動/停止回数・現在の bootstrapper 型を返す。</summary>
        public static string ShellStatus()
        {
            var bootstrapper = ActiveShell();
            var sb = new StringBuilder();
            sb.Append("IsRunning=").Append(UiShellLifecycleDriver.IsRunning);
            sb.Append(", Start#=").Append(UiShellLifecycleDriver.StartInvocationCount);
            sb.Append(", Stop#=").Append(UiShellLifecycleDriver.StopInvocationCount);
            sb.Append(", Bootstrapper=").Append(bootstrapper == null ? "<null>" : bootstrapper.GetType().Name);
            return Report("ShellStatus", true, sb.ToString());
        }

        /// <summary>登録済みの config provider を使ってシェルを起動する（稼働中なら no-op）。</summary>
        public static string StartShell()
        {
            if (!RequirePlayMode(out var guard)) return guard;
            UiShellLifecycleDriver.StartShell();
            return Report("StartShell", UiShellLifecycleDriver.IsRunning,
                $"IsRunning={UiShellLifecycleDriver.IsRunning}");
        }

        /// <summary>稼働中のシェルを停止し、購読・UIDocument 等を破棄する。</summary>
        public static string StopShell()
        {
            UiShellLifecycleDriver.StopShell();
            return Report("StopShell", !UiShellLifecycleDriver.IsRunning,
                $"IsRunning={UiShellLifecycleDriver.IsRunning}");
        }

        /// <summary>直近の起動で到達した初期化ステップ列を返す（どこまで進んだか）。</summary>
        public static string DumpInitSteps()
        {
            var bootstrapper = ActiveShell();
            if (bootstrapper == null)
            {
                return Report("DumpInitSteps", false, "shell bootstrapper is null (shell not running).");
            }
            var steps = string.Join(" -> ", bootstrapper.InitializationSteps.Select(s => s.ToString()));
            return Report("DumpInitSteps", true, steps.Length == 0 ? "<no steps>" : steps);
        }

        // ===== §D タブの切り替え ================================================

        public static string SwitchToCharacter() => SwitchTo(TabId.Character);
        public static string SwitchToStage() => SwitchTo(TabId.StageLighting);
        public static string SwitchToCamera() => SwitchTo(TabId.CameraSwitcher);

        /// <summary>表示タブを切り替える（style.display の付け替えのみ。再クローンはしない）。</summary>
        public static string SwitchTo(TabId target)
        {
            if (!RequirePlayMode(out var guard)) return guard;
            var registry = ActiveRegistry();
            if (registry == null)
            {
                return Report($"SwitchTo({target})", false, "TabPanelRegistry is null (shell not running).");
            }
            var result = registry.SwitchTo(target);
            var detail = result.Success
                ? $"ActiveTab={registry.ActiveTab}"
                : $"rejected: {result.Error}";
            return Report($"SwitchTo({target})", result.Success, detail);
        }

        /// <summary>現在表示中のタブを返す。</summary>
        public static string ActiveTab()
        {
            var registry = ActiveRegistry();
            if (registry == null)
            {
                return Report("ActiveTab", false, "TabPanelRegistry is null.");
            }
            return Report("ActiveTab", true, registry.ActiveTab?.ToString() ?? "<none>");
        }

        /// <summary>3 タブのプリロード完了状況を返す。</summary>
        public static string PreloadProgress()
        {
            var registry = ActiveRegistry();
            if (registry == null)
            {
                return Report("PreloadProgress", false, "TabPanelRegistry is null.");
            }
            string progress;
            try { progress = registry.GetPreloadProgress().ToString(); }
            catch { progress = "(progress detail unavailable)"; }
            return Report("PreloadProgress", true,
                $"IsPreloadComplete={registry.IsPreloadComplete}, TotalTabCount={registry.TotalTabCount}, {progress}");
        }

        // ===== §G / §J IPC ランタイム ===========================================

        /// <summary>IPC ランタイムが起動済みで Bus を取り出せるかを返す。</summary>
        public static string RuntimeStatus()
        {
            var runtime = CoreIpcRuntime.Current;
            var bus = runtime?.Bus;
            var sb = new StringBuilder();
            sb.Append("RuntimeBootstrap.IsBootstrapped=").Append(RuntimeBootstrap.IsBootstrapped);
            sb.Append(", CoreIpcRuntime.Current=").Append(runtime == null ? "<null>" : "ok");
            sb.Append(", Bus=").Append(bus == null ? "<null>" : "ok");
            return Report("RuntimeStatus", bus != null, sb.ToString());
        }

        /// <summary>バスの現在の接続状態（Connected / Reconnecting など）を返す。</summary>
        public static string ConnectionStatus()
        {
            var bus = CoreIpcRuntime.Current?.Bus;
            if (bus == null)
            {
                return Report("ConnectionStatus", false, "Bus is null.");
            }
            string state;
            try { state = bus.Diagnostics.CurrentState.ToString(); }
            catch (Exception ex) { return Report("ConnectionStatus", false, "Diagnostics threw: " + ex.Message); }
            return Report("ConnectionStatus", true, "CurrentState=" + state);
        }

        // ===== helpers ==========================================================

        private static UiShellBootstrapper? ActiveShell()
            => UiShellLifecycleDriver.Current as UiShellBootstrapper;

        private static ITabPanelRegistry? ActiveRegistry()
            => ActiveShell()?.TabPanelRegistry;

        /// <summary>実行中シェルの IPC 送信窓口（CommandClient）。未起動なら null。</summary>
        private static UiCommandClient? Cmd() => ActiveShell()?.CommandClient;

        /// <summary>実行中シェルの IPC 購読窓口（SubscriptionClient）。未起動なら null。</summary>
        private static UiSubscriptionClient? Sub() => ActiveShell()?.SubscriptionClient;

        /// <summary>検証用にシーン上の統合 Bootstrap（出力アダプタ診断の入口）を取得。</summary>
        private static IntegratedDemoBootstrap? Demo()
            => UnityEngine.Object.FindAnyObjectByType<IntegratedDemoBootstrap>();

        private static bool RequirePlayMode(out string message)
        {
            if (Application.isPlaying)
            {
                message = string.Empty;
                return true;
            }
            message = Report("(guard)", false,
                "PlayMode required. Enter PlayMode first (uloop control-play-mode --action Play).");
            return false;
        }

        private static string Report(string op, bool ok, string detail)
        {
            var line = $"[VtsApiDebug] {(ok ? "OK " : "NG ")}{op}: {detail}";
            if (ok) Debug.Log(line);
            else Debug.LogWarning(line);
            return line;
        }
    }
}
