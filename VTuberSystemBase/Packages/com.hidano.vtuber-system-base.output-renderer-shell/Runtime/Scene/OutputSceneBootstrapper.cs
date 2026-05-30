#nullable enable
using System;
using UnityEngine;
using UnityEngine.Rendering;
using VTuberSystemBase.CoreIpc.Abstractions;
using VTuberSystemBase.OutputRendererShell.Abstractions;
using VTuberSystemBase.OutputRendererShell.Diagnostics;
using VTuberSystemBase.OutputRendererShell.Display;
using VTuberSystemBase.OutputRendererShell.Dispatch;
// CoreIpc.Abstractions と OutputRendererShell.Diagnostics の双方が LogLevel を公開しているため、
// 本ファイル内では shell 側の LogLevel をエイリアスで固定する（Req 9.7 のログレベル切替は shell 側の定義に従う）。
using LogLevel = VTuberSystemBase.OutputRendererShell.Diagnostics.LogLevel;

namespace VTuberSystemBase.OutputRendererShell.Scene
{
    /// <summary>
    /// メイン出力シーンの Composition Root。<see cref="OutputSceneRoots"/> /
    /// <see cref="DefaultCameraFactory"/> / <see cref="DefaultLightFactory"/> / <see cref="GlobalVolumeFactory"/> /
    /// <see cref="IDisplayRoutingService"/> / <see cref="OutputCommandDispatcher"/> / <see cref="OutputDiagnostics"/>
    /// の生成・依存注入・ライフサイクル管理を担う MonoBehaviour（Req 1.6 / 2.2 / 2.5 / 3.1 / 6.1〜6.7）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>起動順序（Flow 1, Req 1.6 / 5.5 / 9.1）</strong>:
    /// <see cref="Awake"/> で Roots → Camera → Light → Volume を生成し、各フェーズ完了で
    /// <see cref="OutputDiagnostics.AdvancePhase"/> を進める。<see cref="Start"/> で IPC サーバ起動 →
    /// <see cref="OutputCommandDispatcher"/> バインド → <see cref="IDisplayRoutingService.Activate"/> を実行する。
    /// 任意フェーズで例外が発生しても <c>Application.Quit()</c> は呼ばず、<see cref="OutputSceneInitPhase.Failed"/>
    /// を記録したうえで可能な限り後続フェーズを続行する（描画継続最優先）。
    /// </para>
    /// <para>
    /// <strong>描画禁止契約（Req 5.2 / 5.6 / 9.6）</strong>: 本コンポーネントおよび配下に <c>OnGUI</c> /
    /// <c>IMGUI</c> / UI Toolkit（<c>UIDocument</c> / <c>PanelSettings</c>）を一切アタッチしない。
    /// 診断は <see cref="OutputShellLogger"/> を経由して Unity Console へ出力するのみ。
    /// </para>
    /// <para>
    /// <strong>ライフサイクル（D-9 継承）</strong>: PlayMode 開始〜停止の間のみ活動する。
    /// ドメインリロードを跨いだ状態維持を試みず、<see cref="OnDestroy"/> 後はあらゆるリソース参照を破棄する
    /// （Task 6.3 で逆順 Shutdown を完結）。
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class OutputSceneBootstrapper : MonoBehaviour
    {
        private const string ComponentName = "OutputSceneBootstrapper";

        [Header("Display Routing")]
        [Tooltip("メイン出力カメラを割り当てるディスプレイインデックス（0-based、既定 1 = Display 2）。")]
        [SerializeField]
        private int _targetDisplayIndex = 1;

        [Tooltip("全画面表示モード。既定 FullScreenWindow。Editor PlayMode では Game View 制約により無視される。")]
        [SerializeField]
        private FullScreenMode _fullScreenMode = FullScreenMode.FullScreenWindow;

        [Tooltip("Editor PlayMode 固有の Display.Activate 制限警告を抑止する場合は ON。既定 OFF。")]
        [SerializeField]
        private bool _suppressEditorWarning;

        [Tooltip("ディスプレイ振り分けサービスの実装。BuiltIn=Display.Activate ベース、RuntimeDisplaySelector=RDS Facade 経由。既定 BuiltIn。")]
        [SerializeField]
        private DisplayRoutingProvider _routingProvider = DisplayRoutingProvider.BuiltIn;

        [Tooltip("Klak Spout センダー名。空のとき Spout 経路を使わない（物理ディスプレイ経路のみ）。RuntimeDisplaySelector 選択時のみ参照される。")]
        [SerializeField]
        private string _spoutSenderName = string.Empty;

        [Tooltip("メイン出力カメラを描画する RenderTexture（= Spout 送出元）の解像度。既定 1920x1080。各成分が 0 以下のときは安全な既定にフォールバックする。RuntimeDisplaySelector 選択時のみ参照される。")]
        [SerializeField]
        private Vector2Int _outputResolution = new Vector2Int(1920, 1080);

        [Header("Lifecycle")]
        [Tooltip("シーン開始時に自動的に起動シーケンスを実行する場合は ON。既定 ON。")]
        [SerializeField]
        private bool _autoStart = true;

        [Header("Diagnostics")]
        [Tooltip("OutputShellLogger の最小ログレベル。既定 Info。")]
        [SerializeField]
        private LogLevel _minLogLevel = LogLevel.Info;

        // 依存注入（テスト時にモック差し替え用）
        private IDisplayRoutingService? _injectedRouting;
        private ICoreIpcBus? _injectedIpcBus;

        // 起動時に生成する内部サービス群
        private OutputShellLogger? _logger;
        private OutputDiagnostics? _diagnostics;
        private OutputSceneRoots? _roots;
        private Camera? _defaultCamera;
        private Light? _defaultLight;
        private Volume? _globalVolume;
        private IDisplayRoutingService? _routing;
        private bool _routingOwnedByThis;
        private OutputCommandDispatcher? _dispatcher;

        private bool _selfDestroyed;
        private bool _ipcServerStarted;

        /// <summary>
        /// <see cref="Awake"/> 前にテスト時の依存注入を行う接合点（モック差し替え用）。
        /// </summary>
        /// <param name="routing">
        /// テスト用 <see cref="IDisplayRoutingService"/>。<c>null</c> の場合は本番デフォルト
        /// （<see cref="BuiltInDisplayRoutingService"/>）が使われる。
        /// </param>
        /// <param name="ipcBus">
        /// テスト用 <see cref="ICoreIpcBus"/>。<c>null</c> の場合は IPC 接続を行わず、
        /// ディスパッチャはハンドラ登録のみ受け付ける（UI 未接続フェイルセーフ、Req 7.1 / 7.6）。
        /// </param>
        /// <remarks>
        /// 本メソッドは GameObject が <see cref="GameObject.activeInHierarchy"/> = false の状態で呼び出し、
        /// その後アクティブ化することで <see cref="Awake"/> へ流すこと。
        /// </remarks>
        public void OverrideServices(IDisplayRoutingService? routing = null, ICoreIpcBus? ipcBus = null)
        {
            _injectedRouting = routing;
            _injectedIpcBus = ipcBus;
        }

        /// <summary>
        /// 本コンポーネントから取得した不変な <see cref="DisplayRoutingConfig"/>。
        /// Inspector フィールドの組合せをコンストラクションのたびに反映する。
        /// </summary>
        public DisplayRoutingConfig BuildRoutingConfig() => new()
        {
            TargetDisplayIndex = _targetDisplayIndex,
            FullScreenMode = _fullScreenMode,
            SuppressEditorWarning = _suppressEditorWarning,
            SpoutSenderName = string.IsNullOrEmpty(_spoutSenderName) ? null : _spoutSenderName,
            OutputResolution = _outputResolution.x > 0 && _outputResolution.y > 0
                ? _outputResolution
                : new Vector2Int(1920, 1080),
        };

        /// <summary>
        /// Inspector で選択された <see cref="IDisplayRoutingService"/> 実装の種別。
        /// テスト時に <see cref="OverrideServices"/> 経由で実装を直接注入した場合は本値は無視される。
        /// </summary>
        public DisplayRoutingProvider RoutingProvider => _routingProvider;

        /// <summary>
        /// Inspector で設定された自動起動フラグ。<c>true</c> の場合、<see cref="Start"/> 内で
        /// IPC サーバ起動／ディスパッチャ起動／ディスプレイ切替が実行される。
        /// </summary>
        public bool AutoStart => _autoStart;

        /// <summary>
        /// 重複配置による自己破棄が走った場合に <c>true</c>。テストでの活動継続検証に利用する。
        /// </summary>
        public bool IsSelfDestroyed => _selfDestroyed;

        /// <summary>
        /// 起動済み <see cref="OutputDiagnostics"/> への読み取り専用アクセス（Req 9.8 / 2.4a）。
        /// 起動前は <c>null</c>。
        /// </summary>
        public IOutputDiagnostics? Diagnostics => _diagnostics;

        /// <summary>
        /// 起動済み <see cref="IOutputCommandDispatcher"/> への読み取り専用アクセス（後続タブ spec 連携用）。
        /// 起動前は <c>null</c>。
        /// </summary>
        public IOutputCommandDispatcher? Dispatcher => _dispatcher;

        /// <summary>
        /// 起動済み <see cref="IOutputSceneRoots"/> への読み取り専用アクセス（後続タブ spec 連携用）。
        /// 起動前は <c>null</c>。
        /// </summary>
        public IOutputSceneRoots? Roots => _roots;

        internal OutputShellLogger Logger => _logger ??= new OutputShellLogger(_minLogLevel);

        private void Awake()
        {
            if (!Application.isPlaying)
            {
                // Edit モードでは何もしない（D-9 / Req 6.5）。
                return;
            }

            if (DetectAndDestroyDuplicate())
            {
                return;
            }

            EnsureLogger();
            _diagnostics = new OutputDiagnostics();

            RunPhase(OutputSceneInitPhase.RootsCreated, "create scene roots", () =>
            {
                _roots = new OutputSceneRoots();
            });

            RunPhase(OutputSceneInitPhase.CameraReady, "create default camera", () =>
            {
                if (_roots is null) throw new InvalidOperationException("scene roots are not initialized.");
                _defaultCamera = DefaultCameraFactory.Create(_roots, Logger);
            });

            RunPhase(OutputSceneInitPhase.LightReady, "create default light", () =>
            {
                if (_roots is null) throw new InvalidOperationException("scene roots are not initialized.");
                _defaultLight = DefaultLightFactory.Create(_roots);
            });

            RunPhase(OutputSceneInitPhase.VolumeReady, "create global volume", () =>
            {
                if (_roots is null) throw new InvalidOperationException("scene roots are not initialized.");
                _globalVolume = GlobalVolumeFactory.Create(_roots);
            });
        }

        private void Start()
        {
            if (_selfDestroyed) return;
            if (!_autoStart) return;
            if (!Application.isPlaying) return;
            if (_diagnostics is null) return;

            RunPhase(OutputSceneInitPhase.IpcServerReady, "ensure ipc server", () =>
            {
                // 注入された ICoreIpcBus は既に Initialize 済みである前提（テストではモック）。
                // 本 spec はトランスポート起動を上流に委ねる（D-4）。
                // _injectedIpcBus が null の場合は UI 未接続フェイルセーフとしてシンプルに通過する（Req 7.1 / 7.6）。
                _ipcServerStarted = _injectedIpcBus is not null;
            });

            RunPhase(OutputSceneInitPhase.DispatcherReady, "create dispatcher", () =>
            {
                _dispatcher = new OutputCommandDispatcher(Logger, responseSink: null);
                _diagnostics!.AttachHandlerCountProvider(() => _dispatcher?.RegisteredHandlerCount ?? 0);
            });

            RunPhase(OutputSceneInitPhase.DisplayRouted, "activate display routing", () =>
            {
                if (_defaultCamera is null) throw new InvalidOperationException("default camera is not initialized.");
                _routing = ResolveRoutingService();
                var assignment = _routing.Activate(_defaultCamera, BuildRoutingConfig());
                _diagnostics!.SetDisplayAssignment(assignment);
            });

            // すべての必須フェーズが Failed なしに進んだ場合のみ Complete に到達させる。
            // Failed が記録されている場合は Complete に進まず、可能な範囲の機能で運用を続行する。
            if (_diagnostics.CurrentPhase != OutputSceneInitPhase.Failed)
            {
                _diagnostics.AdvancePhase(OutputSceneInitPhase.Complete);
            }
        }

        private void OnDestroy()
        {
            // 逆順 Shutdown（Flow 1 の構築順を反転、Req 6.3 / 6.4 / 6.6）。
            // 例外が起きても後続の解放を続行する（描画継続最優先, Req 5.5）。
            SafeDispose("dispatcher", () =>
            {
                _dispatcher?.Dispose();
                _dispatcher = null;
            });

            SafeDispose("routing", () =>
            {
                if (_routingOwnedByThis)
                {
                    _routing?.Dispose();
                }
                _routing = null;
                _routingOwnedByThis = false;
            });

            // IPC サーバの停止は本 spec では行わない：
            //  - _injectedIpcBus は呼び出し元（テスト or 上位 Composition Root）が所有する
            //  - 本 spec はそのライフサイクルに介入せず参照のみ手放す
            //  これにより複数 spec が同一バスを共有してもサーバ停止が二重に走らない。
            _ipcServerStarted = false;

            SafeDispose("global volume", () =>
            {
                GlobalVolumeFactory.DestroyVolume(_globalVolume);
                _globalVolume = null;
            });

            SafeDispose("default light", () =>
            {
                if (_defaultLight != null)
                {
                    UnityEngine.Object.Destroy(_defaultLight.gameObject);
                }
                _defaultLight = null;
            });

            SafeDispose("default camera", () =>
            {
                if (_defaultCamera != null)
                {
                    UnityEngine.Object.Destroy(_defaultCamera.gameObject);
                }
                _defaultCamera = null;
            });

            SafeDispose("scene roots", () =>
            {
                DestroyRootIfPresent(_roots?.Stage);
                DestroyRootIfPresent(_roots?.Characters);
                DestroyRootIfPresent(_roots?.Lights);
                DestroyRootIfPresent(_roots?.Cameras);
                DestroyRootIfPresent(_roots?.Volumes);
                _roots = null;
            });

            SafeDispose("diagnostics reset", () =>
            {
                _diagnostics?.Reset();
                _diagnostics = null;
            });

            _injectedRouting = null;
            _injectedIpcBus = null;
            _logger = null;
        }

        private void SafeDispose(string label, System.Action action)
        {
            try
            {
                action();
            }
            catch (System.Exception ex)
            {
                Logger.Error($"shutdown step '{label}' threw; continuing.", ex, ComponentName);
            }
        }

        private static void DestroyRootIfPresent(Transform? root)
        {
            if (root == null) return;
            UnityEngine.Object.Destroy(root.gameObject);
        }

        private void EnsureLogger() => _logger ??= new OutputShellLogger(_minLogLevel);

        private IDisplayRoutingService ResolveRoutingService()
        {
            if (_injectedRouting is not null)
            {
                _routingOwnedByThis = false;
                return _injectedRouting;
            }
            _routingOwnedByThis = true;
            return _routingProvider switch
            {
                DisplayRoutingProvider.RuntimeDisplaySelector => new RuntimeDisplaySelectorRoutingService(Logger),
                _ => new BuiltInDisplayRoutingService(Logger),
            };
        }

        /// <summary>
        /// 1 フェーズを実行し、成功時は <see cref="OutputDiagnostics.AdvancePhase"/> を進める。
        /// 例外発生時は <see cref="OutputDiagnostics.RecordError"/> で <see cref="OutputSceneInitPhase.Failed"/>
        /// を記録し、後続フェーズを継続するため呼び出し元へは伝搬しない（描画継続最優先, Req 5.5 / 9.1）。
        /// </summary>
        private void RunPhase(OutputSceneInitPhase phaseOnSuccess, string description, Action body)
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                Logger.Error(
                    $"phase '{description}' failed; continuing with next phase to keep main output rendering.",
                    ex,
                    ComponentName);
                _diagnostics!.RecordError($"{description}: {ex.GetType().Name}: {ex.Message}",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                return;
            }

            try
            {
                if (_diagnostics!.CurrentPhase != OutputSceneInitPhase.Failed)
                {
                    _diagnostics.AdvancePhase(phaseOnSuccess);
                }
            }
            catch (InvalidOperationException ex)
            {
                // 単調遷移違反は Bootstrapper のロジックバグなので診断ログに残す。
                Logger.Error(
                    $"phase advance to '{phaseOnSuccess}' rejected by diagnostics; continuing.",
                    ex,
                    ComponentName);
            }

            Logger.Info($"phase complete: {description} -> {phaseOnSuccess}", ComponentName);
        }

        private bool DetectAndDestroyDuplicate()
        {
#if UNITY_2022_2_OR_NEWER
            var existing = UnityEngine.Object.FindObjectsByType<OutputSceneBootstrapper>(FindObjectsSortMode.None);
#else
            var existing = UnityEngine.Object.FindObjectsOfType<OutputSceneBootstrapper>();
#endif
            int liveCount = 0;
            for (int i = 0; i < existing.Length; i++)
            {
                if (existing[i] != null && !existing[i]._selfDestroyed)
                {
                    liveCount++;
                }
            }

            if (liveCount > 1)
            {
                Logger.Warning(
                    $"duplicate OutputSceneBootstrapper detected (live count={liveCount}); destroying this instance.",
                    ComponentName);
                _selfDestroyed = true;
                UnityEngine.Object.Destroy(this);
                return true;
            }

            return false;
        }
    }
}
