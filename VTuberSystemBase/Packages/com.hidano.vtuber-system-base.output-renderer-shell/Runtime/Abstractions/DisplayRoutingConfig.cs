#nullable enable
using UnityEngine;

namespace VTuberSystemBase.OutputRendererShell.Abstractions
{
    /// <summary>
    /// <see cref="IDisplayRoutingService"/> の Activate 呼び出しに渡す構成値。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 既定値はメイン出力を Display 2（インデックス 1）へ全画面ウィンドウ表示する構成（Req 2.2 / 2.3 / 2.7）。
    /// </para>
    /// <para>
    /// 本型は不変オブジェクトとして扱い、初期化後にプロパティを書き換えない（<c>init</c> アクセサ）。
    /// パラメータ無しコンストラクタ <c>new DisplayRoutingConfig()</c> は安全な既定値を返す。
    /// </para>
    /// </remarks>
    public sealed record DisplayRoutingConfig
    {
        /// <summary>
        /// アクティベート対象のディスプレイインデックス（0-based）。既定 1 = Display 2。
        /// 範囲外（<c>Display.displays.Length</c> 超過）の場合、暫定実装は Display 0 へフォールバックする（Req 2.4 / OR-1）。
        /// </summary>
        public int TargetDisplayIndex { get; init; } = 1;

        /// <summary>
        /// 全画面表示モード。既定 <see cref="FullScreenMode.FullScreenWindow"/>（Req 2.3）。
        /// Editor PlayMode では適用されない（Game View 制約, Req 6.8）。
        /// </summary>
        public FullScreenMode FullScreenMode { get; init; } = FullScreenMode.FullScreenWindow;

        /// <summary>
        /// Editor PlayMode 固有の警告ログを抑止する場合 <c>true</c>。既定 <c>false</c>（警告を出す）。
        /// </summary>
        public bool SuppressEditorWarning { get; init; }

        /// <summary>
        /// Klak Spout センダー名。<c>null</c> または空文字列の場合は Spout 経路を使用しない（物理ディスプレイ経路のみ）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 本フィールドは <see cref="IDisplayRoutingService"/> の実装により利用方法が異なる：
        /// </para>
        /// <list type="bullet">
        /// <item><description><c>BuiltInDisplayRoutingService</c>: 値を無視する（物理ディスプレイ振り分けのみ）。</description></item>
        /// <item><description><c>RuntimeDisplaySelectorRoutingService</c>: 非 <c>null</c> / 非空のとき RDS の <c>KlakSpoutSenderStore</c> 経由で Spout センダー登録を行う。</description></item>
        /// </list>
        /// <para>
        /// Wave 3e 統合計画に基づく追加。Spout 経路を併用することで OBS Spout Source からメイン出力カメラを直接取り込める。
        /// </para>
        /// </remarks>
        public string? SpoutSenderName { get; init; }

        /// <summary>
        /// メイン出力カメラを描画する RenderTexture（= Spout 送出元）の解像度。既定 1920x1080。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="RuntimeDisplaySelectorRoutingService"/> は出力カメラの <see cref="Camera.targetTexture"/> を
        /// この解像度の RenderTexture に張り替えて「物理ディスプレイ非依存」で毎フレーム描画させ、その RT を
        /// RuntimeDisplaySelector の Texture モード経由で Spout 送出する。これにより Editor（単一ディスプレイ）でも
        /// standalone（物理 2 画面目なし）でも、Display の表示状態に依存せず OBS の Spout Source へ出力できる。
        /// </para>
        /// <para>
        /// <c>BuiltInDisplayRoutingService</c> は本値を無視する（物理ディスプレイ振り分けのみ）。
        /// 各成分が 0 以下の場合、実装側で安全な既定（1920x1080）にフォールバックする。
        /// </para>
        /// </remarks>
        public Vector2Int OutputResolution { get; init; } = new Vector2Int(1920, 1080);
    }
}
