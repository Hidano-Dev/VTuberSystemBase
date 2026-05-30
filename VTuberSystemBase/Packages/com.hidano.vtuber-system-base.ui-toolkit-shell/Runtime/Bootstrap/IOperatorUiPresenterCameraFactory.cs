#nullable enable
using System;
using VTuberSystemBase.UiToolkitShell.Diagnostics;

namespace VTuberSystemBase.UiToolkitShell.Bootstrap
{
    /// <summary>
    /// オペレーター UI（UI Toolkit のオーバーレイパネル）を表示するディスプレイを毎フレーム
    /// clear/present するためだけの「presenter カメラ」を生成する差し替え可能なファクトリ。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>なぜ必要か.</b> UI Toolkit のオーバーレイパネルはカメラを介さずディスプレイのバック
    /// バッファへ直接合成される。そのディスプレイに <see cref="UnityEngine.Camera"/> が 1 台も
    /// 割り当たっていないと、バックバッファが毎フレーム clear/present されず、前フレームの残骸の
    /// 上に UI が重ね描きされて滲み・ゴーストが発生する（メイン出力カメラを別ディスプレイ／Spout
    /// RT に逃がしている構成で顕在化する）。本ファクトリは何も描画しない clear 専用 Base カメラを
    /// 1 台だけ置き、その合成面を安定させる。
    /// </para>
    /// <para>
    /// <b>必須ではない.</b> <see cref="UiShellConfig.PresenterCameraFactory"/> は既定 <c>null</c>＝
    /// 無効。ホストが明示的に factory を渡したときだけ生成される。ホストは
    /// <see cref="DefaultOperatorUiPresenterCameraFactory"/> をそのまま使うか、独自実装に
    /// 差し替えるか、あるいは <c>null</c> のままにして presenter カメラを使わない選択ができる。
    /// </para>
    /// </remarks>
    public interface IOperatorUiPresenterCameraFactory
    {
        /// <summary>
        /// <paramref name="targetDisplay"/> を clear/present する presenter カメラを生成する。
        /// </summary>
        /// <param name="targetDisplay">
        /// オペレーター UI のオーバーレイパネルが割り当てられた最終ディスプレイインデックス
        /// （<see cref="UiShellBootstrapper.EffectiveTargetDisplay"/> と同値、0-based）。
        /// </param>
        /// <param name="logger">診断ロガー。</param>
        /// <returns>
        /// 生成したカメラを所有する <see cref="IDisposable"/>。Dispose でカメラ GameObject を破棄する。
        /// 生成しない／できない場合（Edit モード等）は <c>null</c> を返してよい。
        /// </returns>
        IDisposable? Create(int targetDisplay, IDiagnosticsLogger logger);
    }
}
