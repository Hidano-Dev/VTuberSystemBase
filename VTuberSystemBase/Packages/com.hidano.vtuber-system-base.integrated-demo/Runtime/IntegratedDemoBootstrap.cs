#nullable enable
using System;
using System.Collections;
using UnityEngine;
using VTuberSystemBase.CoreIpc.Abstractions;
using VTuberSystemBase.CoreIpc.Core;
using VTuberSystemBase.CoreIpc.Core.Configuration;
using VTuberSystemBase.CoreIpc.Core.Lifecycle;
using VTuberSystemBase.OutputRendererShell.Dispatch;
using VTuberSystemBase.OutputRendererShell.Scene;
using VTuberSystemBase.RacMainOutputAdapter.Bootstrapper;
using VTuberSystemBase.CameraSwitcherOutputAdapter.Runtime;
using VTuberSystemBase.StageLightingVolumeOutputAdapter.Bootstrap;

namespace VTuberSystemBase.IntegratedDemo
{
    /// <summary>
    /// MainDemo シーン相当の Wave 3d 統合 Bootstrap MonoBehaviour。
    /// シーンに 1 つだけ配置すると <see cref="Awake"/> で全コンポーネントを構築する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>結線対象</b>:
    /// </para>
    /// <list type="bullet">
    ///   <item><see cref="OutputSceneBootstrapper"/>（Display 2+ メイン出力）</item>
    ///   <item><see cref="CoreIpcBusProvider"/>（同一プロセスループバック ICoreIpcBus を 3 アダプタに供給）</item>
    ///   <item><see cref="RacMainOutputAdapterHost"/>（character-selection-tab IPC → RAC）</item>
    ///   <item><see cref="StageLightingVolumeOutputAdapterBootstrapper"/>（stage-lighting-volume-tab IPC → URP Light/Volume/Stage）</item>
    ///   <item><see cref="CameraSwitcherOutputAdapterBootstrapper"/>（camera-switcher-tab OSC → URP Camera）</item>
    ///   <item><see cref="IntegratedDemoUiShellHost"/>（UiShellLifecycleDriver 経由で UI shell を起動し、3 タブを mount）</item>
    /// </list>
    /// <para>
    /// <b>ライフサイクル順序</b>:
    /// </para>
    /// <list type="number">
    ///   <item><c>RuntimeBootstrap</c>（core-ipc-foundation）が <c>BeforeSceneLoad</c> で <see cref="CoreIpcRuntime.Current"/> を起動済み。</item>
    ///   <item><see cref="Awake"/>: <see cref="CoreIpcBusProvider"/> + <see cref="OutputSceneBootstrapper"/> + 3 アダプタ Bootstrapper（停止状態）を生成。</item>
    ///   <item><see cref="Start"/>: アダプタを順次起動（OutputSceneBootstrapper の Start 完了を待ってから）。UI shell 起動は <see cref="IntegratedDemoUiShellHost.Configure"/> で driver に登録済み。</item>
    /// </list>
    /// <para>
    /// <b>失敗フェイルオーバー</b>:
    /// 個々のアダプタ初期化が失敗してもシーン全体は描画継続を最優先する。
    /// 例外は Console にログ出力され、他アダプタの起動は阻害しない。
    /// SkinProfile が空のときは UI 側の起動を skip する（メイン出力のみ立ち上がる）。
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class IntegratedDemoBootstrap : MonoBehaviour
    {
        [SerializeField] private IntegratedDemoConfig _config = new IntegratedDemoConfig();
        [SerializeField, Tooltip("Inspector で割り当てた既存の OutputSceneBootstrapper（同一 GameObject 推奨）。null のとき子 GameObject に動的に追加する。")]
        private OutputSceneBootstrapper? _outputSceneBootstrapper;

        private CoreIpcBusProvider? _busProvider;
        private RacMainOutputAdapterHost? _racHost;
        private StageLightingVolumeOutputAdapterBootstrapper? _stageHost;
        private CameraSwitcherOutputAdapterBootstrapper? _cameraHost;
        private IDisposable? _inboundBridge;
        private bool _initialized;

        public IntegratedDemoConfig Config => _config;
        public OutputSceneBootstrapper? OutputScene => _outputSceneBootstrapper;
        public CoreIpcBusProvider? BusProvider => _busProvider;
        public RacMainOutputAdapterHost? RacHost => _racHost;
        public StageLightingVolumeOutputAdapterBootstrapper? StageHost => _stageHost;
        public CameraSwitcherOutputAdapterBootstrapper? CameraHost => _cameraHost;

        private void Awake()
        {
            if (!Application.isPlaying) return;
            if (_initialized) return;
            _initialized = true;

            try
            {
                // VTuber 配信用システムは Editor フォーカスが外れていても動き続ける必要があるため強制 ON。
                // これが false だと PlayMode の Update がフォアグラウンド時しか進まず、UI shell 起動も Bus 解決も止まる。
                Application.runInBackground = true;

                EnsureRuntimeBootstrapped();
                EnsureBusProvider();
                EnsureOutputSceneBootstrapper();
                EnsureMainOutputAdapters();
                // UI shell / RAC / Camera は CoreIpcRuntime.Current.Bus が available になるまで待つ必要があるため、
                // StartAdaptersAfterOutputReady() からまとめて起動する。
                Debug.Log("[IntegratedDemoBootstrap] Awake wiring complete (PlayMode integration scaffold ready).");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IntegratedDemoBootstrap] Awake threw: {ex}");
            }
        }

        private void Start()
        {
            if (!Application.isPlaying) return;
            // OutputSceneBootstrapper の Start で Dispatcher / Roots が初期化されるため、
            // 1 フレーム遅らせてアダプタ Bootstrapper を起動する。
            StartCoroutine(StartAdaptersAfterOutputReady());
        }

        private IEnumerator StartAdaptersAfterOutputReady()
        {
            int maxFrames = Mathf.Max(1, _config.AdapterStartupMaxFrames);

            // 1) OutputSceneBootstrapper が Complete に達するまで待つ。
            for (int frame = 0; frame < maxFrames; frame++)
            {
                if (_outputSceneBootstrapper != null
                    && _outputSceneBootstrapper.Diagnostics != null
                    && _outputSceneBootstrapper.Diagnostics.CurrentPhase ==
                        VTuberSystemBase.OutputRendererShell.Abstractions.OutputSceneInitPhase.Complete)
                {
                    break;
                }
                yield return null;
            }

            // 2) CoreIpcRuntime.Current.Bus が available になるまで待つ。
            //    RuntimeBootstrap.OnBeforeSceneLoad が CoreIpcRuntimeHost.InitializeAsync() を発火するが、
            //    WebSocket Server.StartServerAsync の完了を待つ非同期処理なので Awake 時点では null。
            ICoreIpcBus? bus = null;
            for (int frame = 0; frame < maxFrames; frame++)
            {
                bus = _busProvider?.Bus;
                if (bus != null) break;
                yield return null;
            }
            if (bus == null)
            {
                Debug.LogWarning(
                    "[IntegratedDemoBootstrap] CoreIpcRuntime.Current.Bus did not become available within "
                    + $"{maxFrames} frames; UI shell and IPC-dependent adapters will not start.");
                yield break;
            }

            // 3) UI shell を起動。
            EnsureUiShell();

            // 4) RAC adapter を inactive child GameObject で生成 → bus を inject → activate。
            //    Awake で AddComponent すると Start が OutputSceneBootstrapper.Start より先に走って
            //    Dispatcher null で abort してしまうため、ここまで遅延させる。
            EnsureRacAdapterAfterBusReady();

            // 5) Stage adapter は Awake で AddComponent 済み（Start で no-op で抜ける作り）。
            //    Complete 状態の Dispatcher / Roots に対する解決を再起動で確実にする。
            if (_stageHost != null)
            {
                try { _stageHost.TryStart(); }
                catch (Exception ex)
                {
                    Debug.LogError($"[IntegratedDemoBootstrap] StageLightingVolume adapter TryStart threw: {ex}");
                }
            }

            // 6) Camera adapter は Dispatcher / Roots / Bus の全部が揃った段階で生成する。
            EnsureCameraAdapterAfterOutputReady();

            // 6.5) バス → OutputCommandDispatcher のインバウンド結線。
            //      OutputScene は Dispatcher を生成するだけでバスとは繋がない設計（OnEnvelopeReceived は
            //      上流が繋ぎ込む契約）。同一プロセス統合ではここで bus の生インバウンドを Dispatcher へ転送し、
            //      タブ→アダプタのコマンド（camera/command 等）が実際にハンドラへ届くようにする。
            EnsureBusToDispatcherBridge();

            // 7) UI shell が running になるのを待ち、タブ Bootstrapper を起動。
            for (int frame = 0; frame < maxFrames; frame++)
            {
                if (VTuberSystemBase.UiToolkitShell.Bootstrap.UiShellLifecycleDriver.IsRunning)
                {
                    break;
                }
                yield return null;
            }
            if (VTuberSystemBase.UiToolkitShell.Bootstrap.UiShellLifecycleDriver.IsRunning)
            {
                // ui-toolkit-shell の RootUiDocumentBuilder は PanelSettings を ScriptableObject.CreateInstance で
                // 動的生成するため themeStyleSheet が null のままになる。Sample 同梱の TSS を runtime に注入する。
                TryAssignDefaultPanelTheme();

                try { IntegratedDemoUiShellHost.LaunchTabBootstrappers(); }
                catch (Exception ex)
                {
                    Debug.LogError($"[IntegratedDemoBootstrap] LaunchTabBootstrappers threw: {ex}");
                }
            }
            else if (_config.SkinProfile != null)
            {
                Debug.LogWarning(
                    "[IntegratedDemoBootstrap] UI shell did not become running within "
                    + $"{maxFrames} frames; tab Bootstrappers were not launched.");
            }
        }

        private void TryAssignDefaultPanelTheme()
        {
#if UNITY_EDITOR
            try
            {
                UnityEngine.UIElements.ThemeStyleSheet? theme = null;
                var guids = UnityEditor.AssetDatabase.FindAssets(
                    "IntegratedDemoRuntimeTheme t:ThemeStyleSheet");
                if (guids != null && guids.Length > 0)
                {
                    var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]);
                    theme = UnityEditor.AssetDatabase
                        .LoadAssetAtPath<UnityEngine.UIElements.ThemeStyleSheet>(path);
                }
                if (theme == null)
                {
                    Debug.LogWarning(
                        "[IntegratedDemoBootstrap] IntegratedDemoRuntimeTheme.tss not found in project; "
                        + "PanelSettings will run without a default theme and UI may not render. "
                        + "Reimport the 'Integrated Demo Scene Walkthrough' Sample to restore the asset.");
                    return;
                }

                int assigned = 0;
                foreach (var ps in Resources.FindObjectsOfTypeAll<UnityEngine.UIElements.PanelSettings>())
                {
                    if (ps == null) continue;
                    if (ps.name != "VsbUiToolkitShellPanelSettings") continue;
                    if (ps.themeStyleSheet != null) continue;
                    ps.themeStyleSheet = theme;
                    assigned++;
                }
                Debug.Log(
                    $"[IntegratedDemoBootstrap] Assigned default ThemeStyleSheet to {assigned} PanelSettings instance(s).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[IntegratedDemoBootstrap] TryAssignDefaultPanelTheme threw: {ex.Message}");
            }
#endif
        }

        private void OnDestroy()
        {
            // 各 Host MonoBehaviour は OnDestroy で自分の Bootstrapper を Shutdown するので、
            // ここでは GameObject の破棄に任せる。CoreIpcBus 自体は core-ipc-foundation の
            // RuntimeBootstrap が Application.quitting で dispose するので本クラスでは触らない。
            try { _inboundBridge?.Dispose(); } catch { /* defensive */ }
            _inboundBridge = null;
        }

        // ---- private wiring ------------------------------------------------

        private void EnsureRuntimeBootstrapped()
        {
            // core-ipc-foundation の AutoBootstrapDisabler が Editor PlayMode 中も
            // RuntimeBootstrap.OnBeforeSceneLoad を抑制してしまうため
            // (UNITY_INCLUDE_TESTS が com.unity.test-framework 同梱時は常時立つ)、
            // Sample 経路では Awake 時点で手動 Bootstrap を試みる。
            if (RuntimeBootstrap.IsBootstrapped) return;
            try
            {
                Debug.Log("[IntegratedDemoBootstrap] CoreIpcRuntime not bootstrapped; starting manually.");
                RuntimeBootstrap.Bootstrap(
                    optionsLoader: CoreIpcConfigLoader.Load,
                    runtimeFactory: () => new CoreIpcRuntimeHost(),
                    quitHandlerAttacher: null,
                    initFailureLogger: ex => Debug.LogError(
                        $"[IntegratedDemoBootstrap] CoreIpcRuntime initialization failed: {ex}"),
                    initSuccessLogger: () => Debug.Log(
                        "[IntegratedDemoBootstrap] CoreIpcRuntime initialization completed."));
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IntegratedDemoBootstrap] EnsureRuntimeBootstrapped threw: {ex}");
            }
        }

        private void EnsureBusProvider()
        {
            _busProvider = GetComponent<CoreIpcBusProvider>()
                ?? gameObject.AddComponent<CoreIpcBusProvider>();
        }

        private void EnsureOutputSceneBootstrapper()
        {
            if (_outputSceneBootstrapper == null)
            {
                // 1) 同 GameObject (Inspector でドロップしたケース or テストハーネス) を最優先で再利用。
                _outputSceneBootstrapper = GetComponent<OutputSceneBootstrapper>();
            }
            if (_outputSceneBootstrapper == null)
            {
                // 2) シーン内に既存の OutputSceneBootstrapper があれば共有する。
#if UNITY_2022_2_OR_NEWER
                _outputSceneBootstrapper = UnityEngine.Object.FindAnyObjectByType<OutputSceneBootstrapper>();
#else
                _outputSceneBootstrapper = UnityEngine.Object.FindObjectOfType<OutputSceneBootstrapper>();
#endif
            }
            if (_outputSceneBootstrapper == null)
            {
                // 3) 無ければ同 GameObject に AddComponent する (README で「同一 GameObject 推奨」を明記)。
                _outputSceneBootstrapper = gameObject.AddComponent<OutputSceneBootstrapper>();
            }

            // Inject the IPC bus into the OutputSceneBootstrapper before its Awake runs.
            // 既に Awake が走っているケースは「同 GameObject 配置 → 同フレーム Awake 順」に依存する。
            // README で AddComponent 順を明示している前提で OverrideServices を呼ぶが、
            // 既に IPC server started 状態の場合は no-op として安全に抜ける（D-4: トランスポートは上流委譲）。
            try
            {
                var bus = _busProvider?.Bus;
                if (bus != null)
                {
                    _outputSceneBootstrapper.OverrideServices(routing: null, ipcBus: bus);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[IntegratedDemoBootstrap] OverrideServices threw: {ex.Message}");
            }
        }

        private void EnsureMainOutputAdapters()
        {
            // RAC adapter は [DefaultExecutionOrder(100)] で Start 同期 Initialize する作りなので、
            // Awake で AddComponent すると OutputSceneBootstrapper.Start より前に走って
            // Dispatcher null abort してしまう。後段の EnsureRacAdapterAfterBusReady() で
            // bus と Dispatcher が揃ってから inactive child GameObject に生成する。
            _racHost = null;

            // Stage adapter Bootstrapper - Awake は no-op、Start で TryStart（依存未準備時は no-op で抜ける）。
            // AddComponent はここで実施し、StartAdaptersAfterOutputReady() で TryStart() を再呼び出しする。
            _stageHost = GetComponent<StageLightingVolumeOutputAdapterBootstrapper>()
                ?? gameObject.AddComponent<StageLightingVolumeOutputAdapterBootstrapper>();

            // Camera adapter は AddComponent 直後に Awake → TryStart が走り、その時点で
            // Dispatcher / SceneRoots が null だと「deferring」警告になる。
            // ここでは AddComponent せず、StartAdaptersAfterOutputReady() の後段で
            // OutputSceneBootstrapper.Diagnostics == Complete を確認した後に InjectForTesting → AddComponent する。
            _cameraHost = null; // 後段で生成
        }

        private void EnsureRacAdapterAfterBusReady()
        {
            if (_racHost != null) return;
            try
            {
                // RAC host を inactive な child GameObject で生成し、Inject 完了後に activate する。
                // これで [DefaultExecutionOrder(100)] による Start 順序問題を回避でき、
                // OutputSceneBootstrapper.Dispatcher / Roots / CoreIpcBus が揃った状態で Start が走る。
                var racGo = new GameObject("RacMainOutputAdapterHost");
                racGo.transform.SetParent(transform, worldPositionStays: false);
                racGo.SetActive(false);
                _racHost = racGo.AddComponent<RacMainOutputAdapterHost>();
                BindBusProviderToRacHostViaReflection(_racHost);
                racGo.SetActive(true);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IntegratedDemoBootstrap] RAC adapter creation failed: {ex}");
            }
        }

        private void EnsureCameraAdapterAfterOutputReady()
        {
            if (_cameraHost != null) return;
            try
            {
                if (_outputSceneBootstrapper == null
                    || _outputSceneBootstrapper.Dispatcher == null
                    || _outputSceneBootstrapper.Roots == null)
                {
                    Debug.LogWarning(
                        "[IntegratedDemoBootstrap] Cannot create CameraSwitcherOutputAdapter: "
                        + "OutputSceneBootstrapper subsystems are still null.");
                    return;
                }

                // Camera adapter を inactive な child GameObject で生成し、Inject 完了後に activate
                // する（Awake → TryStart の順序を踏むため）。Bus がまだ揃っていない場合でも
                // child GameObject + AddComponent は実行する：シーン構造は Bus 有無に独立であり、
                // テストハーネスや CoreIpcRuntime 初期化遅延ケースでも GameObject 探索が成立する。
                var camGo = new GameObject("CameraSwitcherOutputAdapterHost");
                camGo.transform.SetParent(transform, worldPositionStays: false);
                camGo.SetActive(false);
                _cameraHost = camGo.AddComponent<CameraSwitcherOutputAdapterBootstrapper>();

                var bus = _busProvider?.Bus;
                if (bus != null)
                {
                    _cameraHost.InjectForTesting(
                        bus,
                        _outputSceneBootstrapper.Dispatcher!,
                        _outputSceneBootstrapper.Roots!);
                    camGo.SetActive(true);
                }
                else
                {
                    // Bus が null のままで activate すると Awake → TryStart →
                    // CamerasListPublisher(bus, ...) で ArgumentNullException が走る。
                    // GameObject だけ残し、Bus が後で揃ったときに activate する。
                    Debug.LogWarning(
                        "[IntegratedDemoBootstrap] CameraSwitcherOutputAdapter GameObject created but ICoreIpcBus is null; "
                        + "leaving the host inactive until the bus becomes available.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IntegratedDemoBootstrap] Camera adapter creation failed: {ex}");
            }
        }

        private void BindBusProviderToRacHostViaReflection(RacMainOutputAdapterHost host)
        {
            try
            {
                // Set _coreIpcBusProviderBehaviour to the BusProvider component on this GameObject.
                var providerField = typeof(RacMainOutputAdapterHost).GetField(
                    "_coreIpcBusProviderBehaviour",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (providerField != null && _busProvider != null)
                {
                    providerField.SetValue(host, _busProvider);
                }

                var sceneField = typeof(RacMainOutputAdapterHost).GetField(
                    "_outputSceneBootstrapper",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (sceneField != null && _outputSceneBootstrapper != null)
                {
                    sceneField.SetValue(host, _outputSceneBootstrapper);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[IntegratedDemoBootstrap] Reflection bind to RacMainOutputAdapterHost failed: {ex.Message}");
            }
        }

        private void EnsureBusToDispatcherBridge()
        {
            if (_inboundBridge != null) return;
            try
            {
                var host = CoreIpcRuntime.Current as CoreIpcRuntimeHost;
                var dispatcher = _outputSceneBootstrapper?.Dispatcher as OutputCommandDispatcher;
                if (host == null || dispatcher == null)
                {
                    Debug.LogWarning(
                        "[IntegratedDemoBootstrap] Cannot wire bus->dispatcher bridge: "
                        + $"host={(host == null ? "null" : "ok")}, dispatcher={(dispatcher == null ? "null" : "ok")}.");
                    return;
                }

                _inboundBridge = host.SubscribeAllInbound(envelope =>
                {
                    if (!string.IsNullOrEmpty(envelope.Topic) && dispatcher.HasHandlerFor(envelope.Topic))
                    {
                        dispatcher.OnEnvelopeReceived(envelope);
                    }
                });

                // 帰り道: 出力側 request ハンドラの応答をバスの outbound へ流す。inbound ブリッジが
                // request を Dispatcher へ届けても、応答シンクが無いと Response が捨てられる（OutputScene は
                // 単体 spec として responseSink:null で生成する契約）。ここで結線して往復を成立させる。
                dispatcher.SetResponseSink(host.SendEnvelope);
                Debug.Log("[IntegratedDemoBootstrap] Bus -> OutputCommandDispatcher inbound bridge + response sink wired.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IntegratedDemoBootstrap] EnsureBusToDispatcherBridge threw: {ex}");
            }
        }

        private void EnsureUiShell()
        {
            // SkinProfile が無い場合は UI shell を起動しない（メイン出力のみで起動）。
            if (_config.SkinProfile == null)
            {
                Debug.Log(
                    "[IntegratedDemoBootstrap] SkinProfile not set in IntegratedDemoConfig; " +
                    "skipping UI shell startup. Main-output adapters will still run. " +
                    "Assign a SkinProfile asset in the Inspector to enable Display 1 UI.");
                return;
            }

            ICoreIpcBus? bus = _busProvider?.Bus;
            if (bus == null)
            {
                Debug.LogWarning(
                    "[IntegratedDemoBootstrap] CoreIpcRuntime.Current.Bus is null; " +
                    "skipping UI shell startup until the bus is available.");
                return;
            }

            IntegratedDemoUiShellHost.Configure(_config, bus);
            // UiShellLifecycleDriver は RuntimeInitializeOnLoadMethod(BeforeSceneLoad) で StartShell を一度試行済み。
            // Configure 直前ではダミー (no provider) のため shell は dormant のはず。手動で StartShell を呼び直す。
            VTuberSystemBase.UiToolkitShell.Bootstrap.UiShellLifecycleDriver.StartShell();
        }
    }
}
