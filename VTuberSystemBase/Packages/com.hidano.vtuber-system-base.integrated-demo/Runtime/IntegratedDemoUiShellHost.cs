#nullable enable
using System;
using VTuberSystemBase.CoreIpc.Abstractions;
using VTuberSystemBase.UiToolkitShell.Bootstrap;
using VTuberSystemBase.UiToolkitShell.Diagnostics;
using VTuberSystemBase.UiToolkitShell.Panels;
using LogLevel = VTuberSystemBase.UiToolkitShell.Diagnostics.LogLevel;

namespace VTuberSystemBase.IntegratedDemo
{
    /// <summary>
    /// UI shell の起動を <see cref="UiShellLifecycleDriver"/> に登録する静的ヘルパ。
    /// <see cref="IntegratedDemoBootstrap"/> から <see cref="Configure"/> を呼んだ後に
    /// <see cref="UiShellLifecycleDriver.StartShell"/> を呼ぶと、shell が立ち上がる。
    /// shell 起動完了後、<see cref="LaunchTabBootstrappers"/> を呼ぶと 3 タブ Bootstrapper が
    /// 順次構築される（design.md L676 の規約: タブ spec は shell 起動完了後に RegisterTab する）。
    /// </summary>
    public static class IntegratedDemoUiShellHost
    {
        private static IntegratedDemoConfig? s_config;
        private static ICoreIpcBus? s_bus;
        private static IntegratedTabMountStrategy? s_currentMountStrategy;
        private static IntegratedTabBootstrapperLauncher? s_currentLauncher;

        /// <summary>
        /// 駆動を開始する前に呼び出して config と bus を登録する。
        /// </summary>
        public static void Configure(IntegratedDemoConfig config, ICoreIpcBus bus)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (bus == null) throw new ArgumentNullException(nameof(bus));
            s_config = config;
            s_bus = bus;

            UiShellLifecycleDriver.Configure(BuildConfig);
        }

        /// <summary>テスト/再構成のために登録された state をリセットする。</summary>
        public static void Reset()
        {
            try { s_currentLauncher?.Dispose(); } catch { /* ignored */ }
            s_currentLauncher = null;
            s_currentMountStrategy = null;
            s_config = null;
            s_bus = null;
        }

        /// <summary>現在の Mount Strategy（テスト用）。</summary>
        public static IntegratedTabMountStrategy? CurrentMountStrategy => s_currentMountStrategy;

        /// <summary>現在のタブ Bootstrapper Launcher（テスト用）。</summary>
        public static IntegratedTabBootstrapperLauncher? CurrentLauncher => s_currentLauncher;

        /// <summary>
        /// shell 起動完了後に呼ぶ。3 タブ Bootstrapper を構築する。
        /// 既に shell が dormant な場合は no-op。
        /// </summary>
        public static void LaunchTabBootstrappers()
        {
            var driverBootstrapper = UiShellLifecycleDriver.Current as UiShellBootstrapper;
            if (driverBootstrapper == null || !driverBootstrapper.IsRunning)
            {
                UnityEngine.Debug.LogWarning(
                    "[IntegratedDemoUiShellHost] LaunchTabBootstrappers called but UI shell is not running.");
                return;
            }
            if (s_currentMountStrategy == null)
            {
                UnityEngine.Debug.LogWarning(
                    "[IntegratedDemoUiShellHost] No mount strategy registered; tab bootstrappers cannot be launched.");
                return;
            }
            if (s_config == null)
            {
                UnityEngine.Debug.LogWarning(
                    "[IntegratedDemoUiShellHost] No config registered; tab bootstrappers cannot be launched.");
                return;
            }
            if (driverBootstrapper.TabPanelRegistry == null)
            {
                UnityEngine.Debug.LogWarning(
                    "[IntegratedDemoUiShellHost] UI shell has no TabPanelRegistry yet.");
                return;
            }
            try { s_currentLauncher?.Dispose(); } catch { /* ignored */ }
            s_currentLauncher = new IntegratedTabBootstrapperLauncher();
            var access = new UiShellAccess(
                commandClient: driverBootstrapper.CommandClient,
                subscriptionClient: driverBootstrapper.SubscriptionClient,
                connectionStatus: driverBootstrapper.ConnectionStatus,
                assetLoader: driverBootstrapper.AssetLoader,
                logger: driverBootstrapper.DiagnosticsLogger);
            s_currentLauncher.LaunchAll(
                s_currentMountStrategy,
                driverBootstrapper.TabPanelRegistry,
                access,
                s_config.CameraOscHost,
                s_config.CameraOscPort,
                s_config.CameraPresetPath);
        }

        // ---- private --------------------------------------------------------

        private static UiShellConfig BuildConfig()
        {
            var config = s_config ?? throw new InvalidOperationException(
                "IntegratedDemoUiShellHost.Configure() was not called.");
            var bus = s_bus ?? throw new InvalidOperationException(
                "IntegratedDemoUiShellHost has no ICoreIpcBus.");

            var skin = config.SkinProfile ?? throw new InvalidOperationException(
                "IntegratedDemoConfig.SkinProfile is null; UI shell cannot be configured.");

            // 起動毎に新しい strategy を構築する（PlayMode 開始/停止サイクルでもリーク回避）。
            try { s_currentLauncher?.Dispose(); } catch { /* ignored */ }
            s_currentLauncher = null;
            var mountStrategy = new IntegratedTabMountStrategy(skin);
            s_currentMountStrategy = mountStrategy;

            // presenter カメラは optional：トグル ON のときだけ既定実装を渡す。
            // OFF のときは null＝従来どおり presenter カメラ無しで起動する。
            IOperatorUiPresenterCameraFactory? presenterFactory =
                config.CreateOperatorUiPresenterCamera
                    ? new DefaultOperatorUiPresenterCameraFactory(
                        config.OperatorUiPresenterCameraBackgroundColor)
                    : null;

            return new UiShellConfig
            {
                SkinProfile = skin,
                IpcBus = bus,
                TabMountStrategy = mountStrategy,
                RequestedTargetDisplay = config.UiTargetDisplay,
                MinimumLogLevel = LogLevel.Info,
                InitialTab = TabId.Character,
                PresenterCameraFactory = presenterFactory,
            };
        }
    }
}
