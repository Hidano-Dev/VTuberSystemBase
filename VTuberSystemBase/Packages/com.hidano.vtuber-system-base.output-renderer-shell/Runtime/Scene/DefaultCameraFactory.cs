#nullable enable
using UnityEngine;
using UnityEngine.Rendering.Universal;
using VTuberSystemBase.OutputRendererShell.Abstractions;
using VTuberSystemBase.OutputRendererShell.Diagnostics;

namespace VTuberSystemBase.OutputRendererShell.Scene
{
    /// <summary>
    /// メイン出力用デフォルトカメラを <see cref="IOutputSceneRoots.Cameras"/> 配下に 1 台生成するファクトリ。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 生成するカメラは URP（<see cref="UniversalAdditionalCameraData"/> が自動付与される）に対応し、
    /// ステージ全景を初期視野に収める既定 Transform（Position (0, 1.5, -3.5)、Y 軸 0°、見下ろし 5°）と
    /// 既定 FOV 60° を持つ（Req 1.2 / 1.4 / 5.1）。
    /// </para>
    /// <para>
    /// <c>targetDisplay</c> の設定は本ファクトリでは行わず、後続 <c>IDisplayRoutingService</c>
    /// （task 3.x）の責務とする（Req 2.x の境界）。本ファクトリは生成直後の <c>targetDisplay</c> を
    /// 既定値（0）のまま残し、<c>IDisplayRoutingService.Activate(camera, ...)</c> 経由で書き換えられる前提とする。
    /// </para>
    /// <para>
    /// メイン出力カメラのカリングマスクから「オペレーター UI 専用レイヤー」
    /// （慣用名 <c>"OperatorUI"</c>。spec #3 ui-toolkit-shell が UIDocument レイヤーとして利用予定）
    /// を除外する。レイヤー未定義時は除外を行わず、Verbose ログのみ残す（Req 5.1 / 5.6）。
    /// </para>
    /// </remarks>
    public static class DefaultCameraFactory
    {
        /// <summary>
        /// オペレーター UI 専用レイヤーの慣用名。spec #3 が同名レイヤーを Project Settings に追加する想定。
        /// </summary>
        public const string OperatorUiLayerName = "OperatorUI";

        /// <summary>
        /// <see cref="IOutputSceneRoots.Cameras"/> 配下に 1 台のデフォルトカメラを生成し、
        /// <c>OutputSceneRoots.SetDefaultCamera</c> 経由でサービスロケータに登録する。
        /// </summary>
        /// <param name="roots">既に <see cref="OutputSceneRoots"/> として初期化されたシーンロケータ。</param>
        /// <param name="logger">既存ロガー。null 不可。</param>
        /// <param name="clearFlags">
        /// メイン出力カメラの背景塗りつぶし方法。既定 <see cref="CameraClearFlags.SolidColor"/>。
        /// VTuber 用途では <see cref="CameraClearFlags.Skybox"/> やクロマキー用の単色塗りつぶしを選択できる。
        /// </param>
        /// <param name="backgroundColor">
        /// <paramref name="clearFlags"/> が <see cref="CameraClearFlags.SolidColor"/> のときの背景色。
        /// 既定 <see cref="Color.black"/>。クロマキー運用時はここに緑などのキー色を指定する。
        /// </param>
        /// <returns>生成されたカメラ。</returns>
        public static Camera Create(
            OutputSceneRoots roots,
            OutputShellLogger logger,
            CameraClearFlags clearFlags = CameraClearFlags.SolidColor,
            Color? backgroundColor = null)
        {
            var go = new GameObject("DefaultMainOutputCamera");
            go.transform.SetParent(roots.Cameras, worldPositionStays: false);
            go.transform.SetLocalPositionAndRotation(
                new Vector3(0f, 1.5f, -3.5f),
                Quaternion.Euler(5f, 0f, 0f));
            go.transform.localScale = Vector3.one;

            var camera = go.AddComponent<Camera>();
            camera.fieldOfView = 60f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 1000f;
            camera.clearFlags = clearFlags;
            camera.backgroundColor = backgroundColor ?? Color.black;

            int operatorUiLayer = LayerMask.NameToLayer(OperatorUiLayerName);
            if (operatorUiLayer >= 0)
            {
                camera.cullingMask &= ~(1 << operatorUiLayer);
            }
            else
            {
                logger.Verbose(
                    component: nameof(DefaultCameraFactory),
                    topic: "culling-mask",
                    correlationId: null,
                    message: $"Layer '{OperatorUiLayerName}' is not defined; culling mask kept unchanged.");
            }

            // URP では Camera 追加時に UniversalAdditionalCameraData が自動付与されるが、
            // 本ファクトリ契約として明示的に AddComponent しておくことで「未付与で出荷される」事故を防ぐ。
            if (go.GetComponent<UniversalAdditionalCameraData>() == null)
            {
                go.AddComponent<UniversalAdditionalCameraData>();
            }

            roots.SetDefaultCamera(camera);
            return camera;
        }
    }
}
