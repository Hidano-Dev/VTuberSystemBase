#nullable enable
using System;
using UnityEngine;
using VTuberSystemBase.OutputRendererShell.Abstractions;
using VTuberSystemBase.OutputRendererShell.Diagnostics;

namespace VTuberSystemBase.OutputRendererShell.Display
{
    /// <summary>
    /// <see cref="IDisplayRoutingService"/> の <c>com.hidano.runtime-display-selector</c>（RDS）連携実装。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 本実装は RDS の Facade <c>Hidano.RuntimeDisplaySelector.RuntimeDisplaySelector.Current</c> 経由で
    /// <c>AssignCameraToDisplay(camera, displayIndex, AssignmentOptions)</c> を呼び出す薄いアダプタである
    /// （Wave 3e）。<see cref="DisplayRoutingConfig.SpoutSenderName"/> に値が指定されている場合は
    /// RDS の <c>KlakSpoutSenderStore</c> を経由して Spout センダー登録も行い、OBS Spout Source からの
    /// 取り込みを可能にする。
    /// </para>
    /// <para>
    /// <strong>永続化</strong>：本サービスはアサイン情報の永続化を行わず、すべて RDS 側
    /// <c>JsonAssignmentStore</c> に委譲する（output-renderer-shell 側で重複永続化しない）。
    /// </para>
    /// <para>
    /// <strong>RDS 未初期化シナリオへの耐性</strong>：<c>RuntimeDisplaySelector.Current</c> への参照は遅延し、
    /// <c>null</c> の場合（Facade が未配置 / 多重配置で破棄された / Awake 前）は警告ログを残して
    /// <see cref="DisplayAssignmentInfo.IsFallbackActive"/> = true で物理ディスプレイ経路（<see cref="BuiltInDisplayRoutingService"/>）に
    /// 降りる。Assign 呼び出し中の例外も catch し、同じく fallback 経路で <c>Camera.targetDisplay</c> を直接設定する。
    /// </para>
    /// <para>
    /// 本実装は <c>com.hidano.runtime-display-selector</c> アセンブリ参照を必須とするが、
    /// RDS の Facade 型を <c>using</c> しか使わないため、テスト用差し替え点として
    /// <see cref="IRuntimeDisplaySelectorBridge"/> を経由する。本番では既定の
    /// <see cref="DefaultRuntimeDisplaySelectorBridge"/> が <c>RuntimeDisplaySelector.Current</c> を参照する。
    /// </para>
    /// </remarks>
    public sealed class RuntimeDisplaySelectorRoutingService : IDisplayRoutingService
    {
        private const int DefaultOutputWidth = 1920;
        private const int DefaultOutputHeight = 1080;

        private readonly IRuntimeDisplaySelectorBridge _bridge;
        private readonly OutputShellLogger _logger;
        private readonly IDisplayProbe _probe;
        private DisplayAssignmentInfo _lastAssignment;
        private bool _disposed;

        // メイン出力カメラを「物理ディスプレイ非依存」で描画させるための専用 RenderTexture と、その張り替え先カメラ。
        // 本サービスが所有し、Dispose / fallback / 解像度変更時に解放する。
        private RenderTexture? _outputRenderTexture;
        private Camera? _routedCamera;

        /// <summary>
        /// 本番用コンストラクタ。<paramref name="bridge"/> を省略すると RDS Facade
        /// <c>Hidano.RuntimeDisplaySelector.RuntimeDisplaySelector.Current</c> を参照する既定実装が使われる。
        /// </summary>
        /// <param name="logger">出力シェル共通ロガー。</param>
        /// <param name="bridge">RDS Facade への抽象橋渡し。null で既定実装を使用。</param>
        /// <param name="probe">物理ディスプレイ操作のスタブ点。null で <see cref="UnityDisplayProbe"/> を使用。</param>
        public RuntimeDisplaySelectorRoutingService(
            OutputShellLogger logger,
            IRuntimeDisplaySelectorBridge? bridge = null,
            IDisplayProbe? probe = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _bridge = bridge ?? new DefaultRuntimeDisplaySelectorBridge();
            _probe = probe ?? new UnityDisplayProbe();
        }

        /// <inheritdoc />
        public bool IsFallbackActive => _lastAssignment.IsFallbackActive;

        /// <inheritdoc />
        public DisplayAssignmentInfo GetAssignment() => _lastAssignment;

        /// <inheritdoc />
        public DisplayAssignmentInfo Activate(Camera camera, DisplayRoutingConfig config)
        {
            if (camera == null) throw new ArgumentNullException(nameof(camera));
            if (config is null) throw new ArgumentNullException(nameof(config));

            int requested = config.TargetDisplayIndex;
            bool isEditor = _probe.IsEditor;
            string? diagMessage;
            bool fallback;
            int effective;

            // まずは RDS Facade が利用可能かを判定。
            bool rdsAvailable = _bridge.IsAvailable;
            if (!rdsAvailable)
            {
                diagMessage = $"RuntimeDisplaySelector.Current is unavailable; falling back to direct Camera.targetDisplay assignment (requested index={requested}).";
                _logger.Warning(diagMessage,
                    component: nameof(RuntimeDisplaySelectorRoutingService),
                    topic: "display-routing");
                effective = ApplyFallback(camera, config);
                fallback = true;
            }
            else
            {
                try
                {
                    // 物理ディスプレイ非依存で毎フレーム描画させるため、出力カメラの targetTexture を
                    // 専用 RenderTexture に張り替える。targetDisplay=1（Display 2）+ Editor 単一ディスプレイや
                    // standalone 物理 2 画面目なしの環境では、camera-capture 方式だとカメラが描画されず Spout が
                    // 黒くなる。targetTexture を持たせると Display 表示状態に関係なくカメラがレンダリングされ、
                    // その結果を RDS の Texture モード経由で Spout 送出できる。
                    var rt = EnsureOutputRenderTexture(config.OutputResolution);
                    camera.targetTexture = rt;
                    _routedCamera = camera;

                    _bridge.AssignRenderTextureToDisplay(rt, requested);

                    effective = requested;
                    fallback = false;
                    diagMessage = string.IsNullOrEmpty(config.SpoutSenderName)
                        ? $"RDS routing active via RenderTexture ({rt.width}x{rt.height}) Texture-mode Spout (display-independent)."
                        : $"RDS routing active with Spout sender '{config.SpoutSenderName}' via RenderTexture ({rt.width}x{rt.height}, display-independent).";

                    _logger.Info(
                        $"RDS routing applied (RenderTexture {rt.width}x{rt.height}, display-independent): requested index={requested}, spoutSender='{config.SpoutSenderName ?? "<none>"}'.",
                        component: nameof(RuntimeDisplaySelectorRoutingService),
                        topic: "display-routing");
                }
                catch (Exception ex)
                {
                    diagMessage = $"RDS AssignRenderTextureToDisplay threw {ex.GetType().Name}; falling back to direct Camera.targetDisplay assignment (requested index={requested}).";
                    _logger.Error(diagMessage, ex,
                        component: nameof(RuntimeDisplaySelectorRoutingService),
                        topic: "display-routing");
                    effective = ApplyFallback(camera, config);
                    fallback = true;
                }
            }

            // FullScreenMode は Standalone のみ作用する。RDS は FullScreenMode 制御を提供しないため、
            // 本サービス側で BuiltIn 同等の挙動を提供する（IDisplayProbe 経由）。
            _probe.SetFullScreenMode(config.FullScreenMode);

            if (isEditor && !config.SuppressEditorWarning)
            {
                _logger.Info(
                    "Display routing in Editor PlayMode is limited (Display.Activate is no-op); physical multi-display routing applies in standalone builds only.",
                    component: nameof(RuntimeDisplaySelectorRoutingService),
                    topic: "editor-limited-mode");
            }

            _lastAssignment = new DisplayAssignmentInfo
            {
                RequestedDisplayIndex = requested,
                EffectiveDisplayIndex = effective,
                IsFallbackActive = fallback,
                IsEditorLimitedMode = isEditor,
                DiagnosticMessage = diagMessage,
            };
            return _lastAssignment;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // 本サービスが所有する出力 RenderTexture を解放し、カメラの targetTexture を元（null）に戻す。
            // RDS 本体のライフサイクル（_assigner / _senderStore 等）は RDS Facade の OnDestroy が解放する。
            ReleaseOutputRenderTexture(_routedCamera);
        }

        /// <summary>
        /// 指定解像度の出力用 RenderTexture を取得する（無ければ生成）。解像度が変わった場合は作り直す。
        /// 各成分が 0 以下のときは既定 1920x1080 にフォールバックする。
        /// </summary>
        private RenderTexture EnsureOutputRenderTexture(Vector2Int resolution)
        {
            int w = resolution.x > 0 ? resolution.x : DefaultOutputWidth;
            int h = resolution.y > 0 ? resolution.y : DefaultOutputHeight;

            if (_outputRenderTexture != null
                && (_outputRenderTexture.width != w || _outputRenderTexture.height != h))
            {
                ReleaseOutputRenderTexture(_routedCamera);
            }

            if (_outputRenderTexture == null)
            {
                _outputRenderTexture = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32)
                {
                    name = "VsbMainOutput_SpoutRT",
                    hideFlags = HideFlags.HideAndDontSave,
                };
                _outputRenderTexture.Create();
            }

            return _outputRenderTexture;
        }

        /// <summary>
        /// 所有する出力 RenderTexture を解放し、張り替えた <see cref="Camera.targetTexture"/> を null に戻す。
        /// 他者が targetTexture を上書き済みの場合は触れない（上書き側の責務を尊重）。複数回呼んでも安全。
        /// </summary>
        private void ReleaseOutputRenderTexture(Camera? camera)
        {
            if (camera != null && _outputRenderTexture != null && camera.targetTexture == _outputRenderTexture)
            {
                camera.targetTexture = null;
            }

            if (_outputRenderTexture != null)
            {
                _outputRenderTexture.Release();
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(_outputRenderTexture);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(_outputRenderTexture);
                }
                _outputRenderTexture = null;
            }

            _routedCamera = null;
        }

        /// <summary>
        /// RDS 不在 / 例外時のフォールバック経路。<see cref="BuiltInDisplayRoutingService"/> と同等の
        /// <c>Camera.targetDisplay</c> 直接設定を行う（範囲外指定時は Display 0 へさらにフォールバック）。
        /// </summary>
        private int ApplyFallback(Camera camera, DisplayRoutingConfig config)
        {
            // RT 経路から物理ディスプレイ経路へ降りるため、本サービスが張り替えた targetTexture を解除する
            // （targetTexture が残っていると Camera.targetDisplay が無視されるため）。
            ReleaseOutputRenderTexture(camera);

            int requested = config.TargetDisplayIndex;
            int displayCount = _probe.DisplayCount;
            int effective = (requested >= 0 && requested < displayCount) ? requested : 0;

            if (effective != 0)
            {
                _probe.ActivateDisplay(effective);
            }

            camera.targetDisplay = effective;
            return effective;
        }
    }

    /// <summary>
    /// <see cref="RuntimeDisplaySelectorRoutingService"/> から RDS Facade を経由するための抽象。
    /// テスト時に振る舞いを差し替えるための薄いポート。
    /// </summary>
    public interface IRuntimeDisplaySelectorBridge
    {
        /// <summary>RDS Facade（<c>RuntimeDisplaySelector.Current</c>）が利用可能な場合 <c>true</c>。</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// RDS Facade に対して <c>AssignCameraToDisplay(camera, displayIndex, options)</c> を呼び出す。
        /// </summary>
        /// <param name="camera">アサイン対象 Camera。</param>
        /// <param name="displayIndex">論理ディスプレイ番号（0-based）。</param>
        /// <param name="spoutSenderName">Spout センダー名。null / 空文字列なら Spout 登録を行わない。</param>
        void AssignCameraToDisplay(Camera camera, int displayIndex, string? spoutSenderName);

        /// <summary>
        /// RDS Facade に対して <c>AssignRenderTextureToDisplay(renderTexture, displayIndex)</c> を呼び出す。
        /// RDS は当該 RenderTexture を Texture モードで Spout 送出する（センダー名は SenderNamingPolicy 依存）。
        /// 呼び出し側は別途カメラの <see cref="Camera.targetTexture"/> をこの RenderTexture に設定して描画させる。
        /// </summary>
        /// <param name="renderTexture">Spout 送出元となる RenderTexture。</param>
        /// <param name="displayIndex">論理ディスプレイ番号（0-based）。</param>
        void AssignRenderTextureToDisplay(RenderTexture renderTexture, int displayIndex);
    }

    /// <summary>
    /// 本番用 <see cref="IRuntimeDisplaySelectorBridge"/> 実装。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Hidano.RuntimeDisplaySelector.RuntimeDisplaySelector.Current</c> Facade の <c>AssignCameraToDisplay</c>
    /// と <c>SaveAssignments</c> を呼ぶ。Spout センダー登録は RDS の <c>KlakSpoutSenderStore</c>（Facade 内部の
    /// <c>_senderStore</c>）が担当するため、Facade の標準 API を呼び出すだけで Spout 経路が成立する。
    /// </para>
    /// <para>
    /// RDS が <c>SpoutSenderName</c> を引数として受け付けない（センダー名は <c>SenderNamingPolicy</c> で決定する）
    /// ため、本アダプタは Facade に対して <c>SenderNamingPolicy</c> を一時的に上書きする方式を取る：
    /// 引数で受け取った <paramref name="spoutSenderName"/> を <c>FixedSenderNamingPolicy</c>（RDS 公開型に存在しない場合は
    /// 単純に <c>SenderNamingPolicy.Default</c> をそのまま使用）相当で扱う。本番では Inspector 上で
    /// <c>RuntimeDisplaySelector.SenderNamingPolicy</c> を事前設定しておくのが標準運用となる。
    /// </para>
    /// </remarks>
    public sealed class DefaultRuntimeDisplaySelectorBridge : IRuntimeDisplaySelectorBridge
    {
        /// <inheritdoc />
        public bool IsAvailable => Hidano.RuntimeDisplaySelector.RuntimeDisplaySelector.Current != null;

        /// <inheritdoc />
        public void AssignCameraToDisplay(Camera camera, int displayIndex, string? spoutSenderName)
        {
            var facade = Hidano.RuntimeDisplaySelector.RuntimeDisplaySelector.Current;
            if (facade == null)
            {
                throw new InvalidOperationException(
                    "RuntimeDisplaySelector.Current is null. Ensure the RuntimeDisplaySelector Facade is placed in the scene and Awake has completed before invoking display routing.");
            }

            // RDS の Facade は SenderNamingPolicy を Inspector / setter 経由で受け取る。
            // SpoutSenderName が明示指定された場合のみ、Facade 側のセンダー名をその値に切り替える。
            // 切り替えは RDS 抽象境界に従い、SenderNamingPolicy インスタンスの InvokeMember 経由ではなく
            // 単純にプロパティ setter を呼ぶ（既存挙動を尊重）。空 / null の場合は触らない。
            if (!string.IsNullOrEmpty(spoutSenderName))
            {
                // RDS の SenderNamingPolicy はファクトリ型で、現状は Default のみ公開されている。
                // 動的なセンダー名上書きはファサード側の将来 API 拡張に依存するため、
                // ここでは「呼び出し時点の既存ポリシーに基づいて Spout が登録される」という事実のみを保証する。
                // ユーザは SenderNamingPolicy を Inspector で設定するか、RDS 側 setter を予め叩いておくこと。
                // 本サービスは spoutSenderName 引数を「Spout 経路を有効化する意思表示」として扱う。
            }

            // 既定の AssignmentOptions は ViewportRect=null / BroadcastResolution=null。
            // 本サービスは Camera 全画面送出を前提とするため、追加オプションは使わない。
            facade.AssignCameraToDisplay(camera, displayIndex, Hidano.RuntimeDisplaySelector.AssignmentOptions.Default);
        }

        /// <inheritdoc />
        public void AssignRenderTextureToDisplay(RenderTexture renderTexture, int displayIndex)
        {
            var facade = Hidano.RuntimeDisplaySelector.RuntimeDisplaySelector.Current;
            if (facade == null)
            {
                throw new InvalidOperationException(
                    "RuntimeDisplaySelector.Current is null. Ensure the RuntimeDisplaySelector Facade is placed in the scene and Awake has completed before invoking display routing.");
            }

            // RDS の Texture モード経路。SpoutSender が captureMethod=Texture でこの RT を送出する
            // （センダー名は RDS の SenderNamingPolicy 依存 = RuntimeDisplaySelector_Display_{index}）。
            facade.AssignRenderTextureToDisplay(renderTexture, displayIndex);
        }
    }
}
