#nullable enable
using System;
using UnityEngine;
using VTuberSystemBase.UiToolkitShell.Diagnostics;
using LogLevel = VTuberSystemBase.UiToolkitShell.Diagnostics.LogLevel;
using LogCategory = VTuberSystemBase.UiToolkitShell.Diagnostics.LogCategory;

namespace VTuberSystemBase.UiToolkitShell.Bootstrap
{
    /// <summary>
    /// 既定の <see cref="IOperatorUiPresenterCameraFactory"/>。何も描画しない（cullingMask=0）
    /// clear 専用の Base カメラを 1 台生成し、対象ディスプレイを毎フレーム塗りつぶして UI Toolkit
    /// オーバーレイの合成面を安定させる。
    /// </summary>
    /// <remarks>
    /// <para>
    /// URP では <c>Camera</c> 追加時にレンダリングが <see cref="UnityEngine.Camera"/> を初めて描画する
    /// タイミングで <c>UniversalAdditionalCameraData</c> が自動付与されるため、本ファクトリは URP へ
    /// 明示依存しない（ui-toolkit-shell は URP を参照しない方針）。Built-in でも clear 専用カメラとして
    /// そのまま機能する。
    /// </para>
    /// <para>
    /// 描画契約: <c>cullingMask = 0</c>（3D を一切描かない）、<c>clearFlags = SolidColor</c>
    /// （毎フレーム clear が主目的）、<c>depth</c> は十分小さい値（既定 -100）にして他カメラより先に
    /// 描く。HDR/MSAA は不要なので無効化する。
    /// </para>
    /// </remarks>
    public sealed class DefaultOperatorUiPresenterCameraFactory : IOperatorUiPresenterCameraFactory
    {
        /// <summary>生成されるカメラ GameObject の名前。</summary>
        public const string CameraObjectName = "OperatorUiPresenterCamera";

        private readonly Color _backgroundColor;
        private readonly float _cameraDepth;

        /// <param name="backgroundColor">
        /// clear に使う背景色。既定 <see cref="Color.black"/>。クロマキー運用時はキー色を渡す。
        /// </param>
        /// <param name="cameraDepth">
        /// カメラの描画順。既定 -100（他カメラより先に描く）。
        /// </param>
        public DefaultOperatorUiPresenterCameraFactory(Color? backgroundColor = null, float cameraDepth = -100f)
        {
            _backgroundColor = backgroundColor ?? Color.black;
            _cameraDepth = cameraDepth;
        }

        public IDisposable? Create(int targetDisplay, IDiagnosticsLogger logger)
        {
            // Edit モードではカメラを生成しない（D-9 系の Edit モード非活動契約と整合）。
            if (!Application.isPlaying)
            {
                logger?.Log(LogLevel.Debug, LogCategory.Lifecycle,
                    "DefaultOperatorUiPresenterCameraFactory: skipped in Edit mode.");
                return null;
            }

            var go = new GameObject(CameraObjectName);
            try
            {
                var camera = go.AddComponent<Camera>();
                camera.targetDisplay = targetDisplay;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = _backgroundColor;
                camera.cullingMask = 0;            // 何も描画しない：clear + present のみ
                camera.depth = _cameraDepth;
                camera.allowHDR = false;
                camera.allowMSAA = false;
                camera.useOcclusionCulling = false;

                logger?.Log(LogLevel.Info, LogCategory.Lifecycle,
                    $"DefaultOperatorUiPresenterCameraFactory: created clear-only presenter camera on display {targetDisplay}.");
                return new PresenterCameraHandle(go, logger);
            }
            catch (Exception ex)
            {
                logger?.Log(LogLevel.Warning, LogCategory.Lifecycle,
                    $"DefaultOperatorUiPresenterCameraFactory: camera creation failed; destroying partial GameObject: {ex.Message}", ex);
                if (go != null) UnityEngine.Object.Destroy(go);
                return null;
            }
        }

        /// <summary>
        /// 生成した presenter カメラ GameObject を所有し、Dispose で破棄する <see cref="IDisposable"/>。
        /// </summary>
        private sealed class PresenterCameraHandle : IDisposable
        {
            private GameObject? _go;
            private readonly IDiagnosticsLogger? _logger;

            public PresenterCameraHandle(GameObject go, IDiagnosticsLogger? logger)
            {
                _go = go;
                _logger = logger;
            }

            public void Dispose()
            {
                if (_go == null) return;
                var go = _go;
                _go = null;
                try
                {
                    UnityEngine.Object.Destroy(go);
                }
                catch (Exception ex)
                {
                    _logger?.Log(LogLevel.Warning, LogCategory.Lifecycle,
                        $"DefaultOperatorUiPresenterCameraFactory: presenter camera disposal threw: {ex.Message}", ex);
                }
            }
        }
    }
}
