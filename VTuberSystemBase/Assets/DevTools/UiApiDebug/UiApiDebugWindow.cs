#nullable enable
using System;
using UnityEditor;
using UnityEngine;
using VTuberSystemBase.CoreIpc.Core;
using VTuberSystemBase.UiToolkitShell.Bootstrap;

namespace VtsApiDebug
{
    /// <summary>各ボタンが「今押して効果があるか」の判定結果。</summary>
    public enum ActionReadiness
    {
        /// <summary>実行できる（緑）。</summary>
        Ready,

        /// <summary>前提が足りないが、別のボタンで準備すれば実行できる（黄）。</summary>
        Caution,

        /// <summary>モード違い等で今は実行できない（赤）。</summary>
        Blocked,
    }

    /// <summary>
    /// docs/ui-api-reference.md の逆引き項目を 1 ボタン = 1 操作で実行するデバッグウィンドウ。
    /// 各ボタンは <see cref="UiApiDebugHub"/> の static メソッドを呼ぶだけの薄い View。
    /// 同じメソッドを uloop execute-dynamic-code からも呼べるので、人手でも Claude でも同じ操作になる。
    ///
    /// 項目は <see cref="Actions"/> 配列に 1 行追加するだけで増やせるデータ駆動構成。
    /// </summary>
    public sealed class UiApiDebugWindow : EditorWindow
    {
        private readonly struct DebugAction
        {
            public DebugAction(
                string group, string label, string description,
                Func<string> run, Func<(ActionReadiness state, string note)> readiness)
            {
                Group = group;
                Label = label;
                Description = description;
                Run = run;
                Readiness = readiness;
            }

            public string Group { get; }
            public string Label { get; }

            /// <summary>このボタンの効能（押すと何が起きるか）。下部「説明」パネルに表示する。</summary>
            public string Description { get; }

            public Func<string> Run { get; }

            /// <summary>現在のシステム状態で押して効果があるかを評価する。</summary>
            public Func<(ActionReadiness state, string note)> Readiness { get; }

            public (ActionReadiness state, string note) EvaluateReadiness()
                => Readiness != null ? Readiness() : (ActionReadiness.Ready, string.Empty);
        }

        // 機能ごとにグループ化。新項目はここに 1 行足すだけ。
        // 表示は日本語ラベル。第 3 引数は効能の説明（下部「説明」パネルに表示）。
        // 第 5 引数は「今押して効果があるか」を返す前提条件評価（下の Rd* ヘルパを使う）。
        //
        // ラベル命名規約: 状態を「表示するだけ（読み取り専用）」のボタンは末尾を「〜を表示」にして、
        // 押すと処理が走る動作ボタン（「〜を起動」「〜を追加」等）と一目で区別できるようにする。
        //
        // 各グループ末尾の // §X は逆引きリファレンス docs/ui-api-reference.md の章対応（UI には出さない）。

        // 読み取り専用ボタン（「〜を表示」）共通の readiness。いつでも実行可。
        // メソッドではなくフィールドにしているのは、ボタン色分けで「このボタンは読み取り専用か」を
        // ReferenceEquals で判定するため（同一インスタンスを全 Actions が共有する）。
        // Actions 初期化子から参照されるので、必ず Actions より前に宣言する（static フィールドの初期化順）。
        private static readonly Func<(ActionReadiness state, string note)> RdAlways =
            () => (ActionReadiness.Ready, "読み取り専用。いつでも実行できます");

        private static readonly DebugAction[] Actions =
        {
            // §A
            new DebugAction("UIシェル", "シェル状態を表示",
                "シェルの稼働状態・起動/停止回数・現在の bootstrapper 型を返す（読み取り専用）。", UiApiDebugHub.ShellStatus, RdAlways),
            new DebugAction("UIシェル", "シェルを起動",
                "登録済みの config provider を使ってシェルを起動する（稼働中なら no-op）。PlayMode 必須。", UiApiDebugHub.StartShell, RdStartShell),
            new DebugAction("UIシェル", "シェルを停止",
                "稼働中のシェルを停止し、購読・UIDocument 等を破棄する。", UiApiDebugHub.StopShell, RdStopShell),
            new DebugAction("UIシェル", "初期化ステップ履歴を表示",
                "直近の起動で到達した初期化ステップ列を返す（どこまで進んだか）。実行はせず読むだけ。", UiApiDebugHub.DumpInitSteps, RdAlways),

            // §D
            new DebugAction("タブ", "キャラクタータブへ切替",
                "表示タブを Character に切り替える（style.display の付け替えのみ。再クローンはしない）。PlayMode 必須。", UiApiDebugHub.SwitchToCharacter, RdShell),
            new DebugAction("タブ", "ステージタブへ切替",
                "表示タブを StageLighting に切り替える。PlayMode 必須。", UiApiDebugHub.SwitchToStage, RdShell),
            new DebugAction("タブ", "カメラタブへ切替",
                "表示タブを CameraSwitcher に切り替える。PlayMode 必須。", UiApiDebugHub.SwitchToCamera, RdShell),
            new DebugAction("タブ", "現在のタブを表示",
                "現在表示中のタブを返す（読み取り専用）。", UiApiDebugHub.ActiveTab, RdAlways),
            new DebugAction("タブ", "プリロード状況を表示",
                "3 タブのプリロード完了状況（完了フラグ・タブ数・進捗）を返す（読み取り専用）。", UiApiDebugHub.PreloadProgress, RdAlways),

            // §G/§J
            new DebugAction("IPC", "ランタイム状態を表示",
                "IPC ランタイムが起動済みで Bus を取り出せるかを返す（読み取り専用）。", UiApiDebugHub.RuntimeStatus, RdAlways),
            new DebugAction("IPC", "接続状態を表示",
                "バスの現在の接続状態（Connected / Reconnecting など）を返す（読み取り専用）。", UiApiDebugHub.ConnectionStatus, RdAlways),

            // §B/C/E/F/H/K/L（読み取り専用の診断ダンプ）
            new DebugAction("診断（読み取り専用）", "全診断を表示",
                "この「診断（読み取り専用）」グループの各項目（シェル構成〜各アダプタ）を順に実行し、結合した結果を返す。", UiApiDebugHub.DumpAllDiagnostics, RdAlways),
            new DebugAction("診断（読み取り専用）", "シェル設定を表示",
                "UI シェル構成（SkinProfile / 表示先 / バス / スキン UXML 資産）を読む。", UiApiDebugHub.DumpShellConfig, RdAlways),
            new DebugAction("診断（読み取り専用）", "スキン検証結果を表示",
                "起動時のスキン検証結果（必須 USS クラスの欠落一覧）を読む。新たに検証は実行しない。", UiApiDebugHub.DumpSkinValidation, RdAlways),
            new DebugAction("診断（読み取り専用）", "タブ状態を表示",
                "タブの状態（アクティブ / プリロード進捗 / 失敗タブ）を読む。", UiApiDebugHub.DumpTabStates, RdAlways),
            new DebugAction("診断（読み取り専用）", "アセットローダー状態を表示",
                "Addressables ローダーの稼働カウンタ（pending / completed / failed）を読む。", UiApiDebugHub.DumpAssetLoader, RdAlways),
            new DebugAction("診断（読み取り専用）", "接続診断を表示",
                "UI 側 IConnectionStatus（接続中か / 現在のステータスコード）を読む。", UiApiDebugHub.DumpConnection, RdAlways),
            new DebugAction("診断（読み取り専用）", "出力シーン状態を表示",
                "出力シーン（Display 2+）の初期化フェーズ・表示割当・ハンドラ数・直近エラーを読む。", UiApiDebugHub.DumpOutputScene, RdAlways),
            new DebugAction("診断（読み取り専用）", "RACアダプタ状態を表示",
                "RAC（Character）アダプタの稼働状態・スロット数・カタログ数を読む。", UiApiDebugHub.DumpRacAdapter, RdAlways),
            new DebugAction("診断（読み取り専用）", "ステージアダプタ状態を表示",
                "Stage/Light/Volume アダプタの ready 状態・ライト数・ステージキーを読む。", UiApiDebugHub.DumpStageAdapter, RdAlways),

            // §M
            new DebugAction("キャラクター", "キャラ状態を購読",
                "slots/catalog・avatars/catalog と各 slot の status/error を購読キャッシュする（操作の前に 1 度実行）。PlayMode 必須。", UiApiDebugHub.SubscribeCharacter, RdShell),
            new DebugAction("キャラクター", "不正アバターをスロット0へ割当",
                "先頭スロットに存在しないアバターキーを割り当て、KeyNotFound 応答で IPC 経路を検証する。", UiApiDebugHub.AssignBogusToFirstSlot, RdCharSlot),
            new DebugAction("キャラクター", "先頭アバターをスロット0へ割当",
                "avatars/catalog の先頭アバターを先頭スロットに割り当てる（catalog が空なら NG）。", UiApiDebugHub.AssignFirstAvatarToFirstSlot, RdCharAvatar),
            new DebugAction("キャラクター", "スロット0をリセット",
                "先頭スロットへ Reset コマンドを送る（event slot/{id}/command）。", UiApiDebugHub.ResetFirstSlot, RdCharSlot),
            new DebugAction("キャラクター", "スロット0をクリア",
                "先頭スロットを空にする（state slot/{id}/assignment, AvatarKey=null）。", UiApiDebugHub.ClearFirstSlot, RdCharSlot),
            new DebugAction("キャラクター", "スロット送信プローブを実行",
                "合成 slotId へ assignment を 1 件送り、UI→bus の送信パスが生きているかだけを確認する（ハンドラ未登録のため往復はしない）。", UiApiDebugHub.ProbeSlotSend, RdShell),
            new DebugAction("キャラクター", "キャラ状態を表示（UI側）",
                "UI 側にキャッシュした Character 状態（slot 一覧・状態・avatar 一覧）を読む。", UiApiDebugHub.DumpCharacterState, RdAlways),

            // §N
            new DebugAction("ステージ照明", "ステージ状態を購読",
                "lights/list・stage/current・light/added・light/error を購読し、UI 側状態をキャッシュする（操作の前に 1 度実行）。PlayMode 必須。", UiApiDebugHub.SubscribeStage, RdShell),
            new DebugAction("ステージ照明", "平行光源を追加",
                "Directional ライトを追加（event light/command, op=add）。id はアダプタが採番し lights/list に反映。検証は「ステージアダプタ状態を表示」。", UiApiDebugHub.AddDirectionalLight, RdShell),
            new DebugAction("ステージ照明", "ポイント光源を追加",
                "Point ライトを追加（event light/command, op=add）。", UiApiDebugHub.AddPointLight, RdShell),
            new DebugAction("ステージ照明", "スポット光源を追加",
                "Spot ライトを追加（event light/command, op=add）。", UiApiDebugHub.AddSpotLight, RdShell),
            new DebugAction("ステージ照明", "直近ライトの強度を上げる",
                "直近に追加したライトの intensity を 4 に上げる（state light property）。", UiApiDebugHub.SetLastLightIntensityHigh, RdStageLight),
            new DebugAction("ステージ照明", "直近ライトを赤に変更",
                "直近に追加したライトの color を赤 (1,0,0,1) に設定する。", UiApiDebugHub.SetLastLightColorRed, RdStageLight),
            new DebugAction("ステージ照明", "直近ライトを削除",
                "直近に追加したライトを削除（event light/command, op=remove）。", UiApiDebugHub.RemoveLastLight, RdStageLight),
            new DebugAction("ステージ照明", "ステージをアンロード",
                "ステージを解除する（event stage/command, op=unload）。", UiApiDebugHub.UnloadStage, RdShell),
            new DebugAction("ステージ照明", "ステージ状態を表示（UI側）",
                "UI 側にキャッシュした Stage 状態（現在ステージ・ライト一覧）を読む。", UiApiDebugHub.DumpStageState, RdAlways),

            // §O
            new DebugAction("カメラ", "透視カメラを追加",
                "Perspective カメラを追加（event camera/command, op=add）。反映は非同期なので検証は「カメラアダプタ状態を表示」で。", UiApiDebugHub.AddPerspectiveCamera, RdShell),
            new DebugAction("カメラ", "平行投影カメラを追加",
                "Orthographic カメラを追加（event camera/command, op=add）。", UiApiDebugHub.AddOrthographicCamera, RdShell),
            new DebugAction("カメラ", "直近カメラをアクティブ化",
                "直近に追加したカメラをアクティブに切替（event camera/command, op=active-set）。", UiApiDebugHub.ActivateLastCamera, RdCamera),
            new DebugAction("カメラ", "直近カメラを削除",
                "直近に追加したカメラを削除（event camera/command, op=delete）。", UiApiDebugHub.DeleteLastCamera, RdCamera),
            new DebugAction("カメラ", "プリセットを作成（デモ）",
                "デモ名でカメラプリセットを作成（event camera/preset/command, op=create）。", UiApiDebugHub.CreateCameraPresetDemo, RdShell),
            new DebugAction("カメラ", "全カメラのプレビューを開始",
                "全カメラのプレビューを開始（attach, 320x180@15fps）。", UiApiDebugHub.StartPreviewAll, RdCamera),
            new DebugAction("カメラ", "全カメラのプレビューを停止",
                "全カメラのプレビューを停止（detach）。", UiApiDebugHub.StopPreviewAll, RdCamera),
            new DebugAction("カメラ", "直近カメラにBloomを追加",
                "直近に追加したカメラに Bloom の Volume override を追加する（op=override-add）。", UiApiDebugHub.AddBloomToLastCamera, RdCamera),
            new DebugAction("カメラ", "直近カメラのVolumeを有効化",
                "直近に追加したカメラの Volume を有効化（state camera/{id}/volume/enabled=true）。", UiApiDebugHub.EnableVolumeOnLastCamera, RdCamera),
            new DebugAction("カメラ", "カメラアダプタ状態を表示",
                "camera-switcher-output-adapter の診断（カメラ数・アクティブ・OSC 受信状態）を読む。", UiApiDebugHub.DumpCameraAdapter, RdAlways),

            // §O-8
            new DebugAction("カメラOSC送信", "OSC送信を開始",
                "emitter を出力アダプタの実際の受信 host/port へ向けて起動する（ポートを推測しない＝UDP の偽成功回避）。PlayMode 必須。", UiApiDebugHub.StartOscEmitter, RdOscStart),
            new DebugAction("カメラOSC送信", "直近カメラへOSC送信",
                "直近に追加したカメラへ特徴的な position で OSC を 1 フレーム送る。往復検証は「カメラアダプタ状態を表示」の OscFramesReceived/Applied で。", UiApiDebugHub.SendOscToLastCameraDemo, RdOscSend),
            new DebugAction("カメラOSC送信", "OSC送信を停止",
                "emitter を停止し hidden GameObject / socket を破棄する。", UiApiDebugHub.StopOscEmitter, RdOscStop),

            // request/response 往復
            new DebugAction("リクエスト/レスポンス", "バス往復プローブを実行（echo）",
                "バスに echo handler を登録し、自分で request を投げて往復を実証する。~1 秒後に「プローブ結果を表示」で確認。PlayMode 必須。", UiApiDebugHub.ProbeBusRequestResponse, RdShell),
            new DebugAction("リクエスト/レスポンス", "プローブ結果を表示",
                "直近の request/response プローブの結果を読む。", UiApiDebugHub.DumpProbeResult, RdAlways),
            new DebugAction("リクエスト/レスポンス", "プローブの後始末",
                "プローブの echo handler を解除する。", UiApiDebugHub.CleanupProbe, RdShell),
            new DebugAction("リクエスト/レスポンス", "直近カメラのVolume schemaを要求",
                "直近カメラの volume override schema を request（responseSink 経由の往復実証）。要カメラ。~1 秒後に「Volume schema結果を表示」で確認。", UiApiDebugHub.RequestVolumeMetadataOnLastCamera, RdCamera),
            new DebugAction("リクエスト/レスポンス", "Volume schema結果を表示",
                "直近の volume metadata request の結果を読む。", UiApiDebugHub.DumpVolumeMetaResult, RdAlways),

            // URP（描画パイプライン）。未割当だと Built-in にフォールバックしメイン出力が黒くなる。
            new DebugAction("URP 設定", "描画パイプライン状態を表示",
                "現在の有効レンダーパイプライン状態をダンプ（読み取り専用）。null は Built-in RP フォールバックで出力が黒くなる原因。", UiApiDebugHub.DumpRenderPipeline, RdAlways),
            new DebugAction("URP 設定", "プロジェクトのURPアセットを割当",
                "プロジェクト内の正規 URP アセットを既定パイプラインに割り当てる（不完全な Vsb* は除外。候補 0/複数件なら割り当てない）。Edit モード専用。", UiApiDebugHub.AssignUrpAssetFromProject, RdEdit),
            new DebugAction("URP 設定", "生成した不完全URPアセットを後始末",
                "本セッションでプログラム生成した不完全 URP アセット（Assets/Settings/Vsb*）を削除し、割当も null（Built-in）に戻す。Edit モード専用・冪等。", UiApiDebugHub.CleanupGeneratedUrpAssets, RdEdit),

            // Spout 出力の目視検証（PlayMode 中）。
            new DebugAction("Spout 検証", "テスト用コンテンツを注入（Skybox＋キューブ）",
                "メイン出力カメラの背景を Skybox にしテストキューブを視野内に置く。OBS の Spout Source に映るか目視確認。PlayMode 限定の一時注入（Stop で消える）。", UiApiDebugHub.InjectSpoutTestContent, RdPlay),
            new DebugAction("Spout 検証", "テスト用コンテンツを削除",
                "注入したテストキューブを破棄する（背景 Skybox はそのまま）。", UiApiDebugHub.RemoveSpoutTestContent, RdPlay),

            // RDS+Spout 出力経路の実証（シーン非変更・OBS 不要。SpoutSender 数で成立判定）
            new DebugAction("RDS/Spout 検証", "RDS Spout検証を実行 → Display1",
                "直近/任意のカメラを displayIndex=1 に RDS でアサインし、SpoutSender が立つかを数で検証する（シーン非変更）。PlayMode 必須。", UiApiDebugHub.ProbeRdsSpoutToDisplay1, RdPlay),
            new DebugAction("RDS/Spout 検証", "RDS Spout検証を実行 → Display0",
                "displayIndex=0 版（Editor でも通る基準ケース。1 と比較して表示制約を切り分ける）。PlayMode 必須。", UiApiDebugHub.ProbeRdsSpoutToDisplay0, RdPlay),
            new DebugAction("RDS/Spout 検証", "RDS検証の後始末",
                "プローブで一時生成した RDS Facade GameObject を破棄する。", UiApiDebugHub.CleanupRdsProbe, RdPlay),
            new DebugAction("RDS/Spout 検証", "現在のシーンにRDS結線（Edit時・保存）",
                "現在のシーン（MainDemo 想定）を RDS+Spout 出力経路に結線して保存する（RDS Facade 配置 + RoutingProvider/Spout 名設定）。Edit モード専用・冪等。", UiApiDebugHub.SetupRdsOnCurrentScene, RdEdit),
        };

        private const string ResultHeightPrefKey = "VtsApiDebug.ResultHeight";
        private const float MinResultHeight = 60f;
        private const float SplitterThickness = 6f;
        private const float DescriptionBoxHeight = 96f; // 約 5 行ぶん（ホバーで高さが変わらないよう固定）。

        // ボタンの色分け（GUI.backgroundColor で素の Button を着色）。
        // 今すぐ押して効果がある動作ボタン＝黄ハイライト。前提不足・no-op の動作ボタン＝減光。
        // モード違いで不可の動作ボタンは GUI.enabled=false（Unity 既定のグレーアウト）。
        // 読み取り専用（「〜を表示」）は着色しない＝いつ押しても安全な既定色。
        private static readonly Color ReadyActionColor = new Color(1f, 0.82f, 0.3f);
        private static readonly Color DimColor = new Color(0.45f, 0.45f, 0.45f, 1f);

        private Vector2 _buttonScroll;
        private Vector2 _resultScroll;
        private string _lastResult = "(まだ何も実行していません)";

        /// <summary>説明パネルに表示する対象ボタンの index（前フレームの Repaint でホバー判定したもの）。-1 = なし。</summary>
        private int _hoveredActionIndex = -1;

        /// <summary>結果欄の高さ（スプリッターのドラッグで可変・EditorPrefs に永続化）。</summary>
        private float _resultHeight = 140f;
        private bool _resizingResult;

        private GUIStyle? _wrapStyle;

        [MenuItem("Tools/Hidano/VTuberSystem/Debug/VTS API Debug")]
        public static void Open()
        {
            GetWindow<UiApiDebugWindow>("VTS API Debug");
        }

        private void OnEnable()
        {
            // ホバー追従（説明パネル）と、シェル状態ライブパネルの更新のため、マウス移動で再描画する。
            // 浮動ツールチップ（GUIContent.tooltip）は PlayMode 中に出にくい仕様なので使わず、
            // 常設の「説明」パネルに効能＋実行可否を出す方式に統一している。
            wantsMouseMove = true;
            _resultHeight = EditorPrefs.GetFloat(ResultHeightPrefKey, 140f);
        }

        // EditorWindow に対して毎秒約 10 回呼ばれる。シェル状態と実行可否のライブ表示を更新するため再描画する。
        private void OnInspectorUpdate() => Repaint();

        private void OnGUI()
        {
            _wrapStyle ??= new GUIStyle(EditorStyles.label) { wordWrap = true };

            // ホバー追従のため、マウス移動でも再描画する。
            if (Event.current.type == EventType.MouseMove)
            {
                Repaint();
            }

            EditorGUILayout.HelpBox(
                Application.isPlaying
                    ? "PlayMode 中。ボタンを押すと該当 API を実行し、結果を下に表示します。挙動は Game ビュー / Console で確認してください。"
                    : "PlayMode ではありません。シェル/タブ系の操作は PlayMode 中のみ動作します。",
                Application.isPlaying ? MessageType.Info : MessageType.Warning);

            DrawLiveShellStatus();

            // 説明パネル（シェル状態の直下に配置・固定高）。ホバー中ボタンの効能と、今押して効果があるかを表示。
            DrawDescriptionPanel();

            // ボタン色分けの凡例。
            DrawButtonColorLegend();

            // ボタン一覧（残りの縦スペースを占有）。
            // 各ボタンは現在の readiness で色分けする（凡例は下の DrawButtonColorLegend を参照）:
            //   ・読み取り専用（「〜を表示」） … 既定色のまま。いつ押しても安全。
            //   ・動作ボタンで今すぐ実行可     … 黄色ハイライト。押せば効果がある。
            //   ・前提不足／押しても変化なし   … 減光。押せるが今は意味が薄い。
            //   ・モード違いで実行不可         … グレーアウト（無効化）。今は押せない。
            // 全ボタンを毎フレーム評価するので、重い Demo() 検索は 1 フレーム 1 回に畳む。
            int hoveredThisFrame = -1;
            _buttonScroll = EditorGUILayout.BeginScrollView(_buttonScroll, GUILayout.ExpandHeight(true));
            string? currentGroup = null;
            UiApiDebugHub.BeginReadinessSnapshot();
            try
            {
                for (int i = 0; i < Actions.Length; i++)
                {
                    var action = Actions[i];
                    if (action.Group != currentGroup)
                    {
                        currentGroup = action.Group;
                        GUILayout.Space(6f);
                        EditorGUILayout.LabelField(currentGroup, EditorStyles.boldLabel);
                    }

                    bool readOnly = ReferenceEquals(action.Readiness, RdAlways);
                    var (state, _) = action.EvaluateReadiness();

                    var prevBg = GUI.backgroundColor;
                    bool prevEnabled = GUI.enabled;
                    // 読み取り専用は既定色のまま。動作ボタンだけ実行可否で色を変える。
                    if (!readOnly)
                    {
                        switch (state)
                        {
                            case ActionReadiness.Ready:
                                GUI.backgroundColor = ReadyActionColor;
                                break;
                            case ActionReadiness.Caution:
                                GUI.backgroundColor = DimColor;
                                break;
                            default: // Blocked: モード違い等で今は押せない → 無効化してグレーアウト
                                GUI.enabled = false;
                                break;
                        }
                    }

                    bool pressed = GUILayout.Button(action.Label);

                    GUI.backgroundColor = prevBg;
                    GUI.enabled = prevEnabled;

                    if (pressed)
                    {
                        Execute(action);
                    }

                    // ホバー判定は Repaint 時のみ有効な実座標で行う（スクロールビュー内のローカル座標系）。
                    // GUI.enabled を戻した後でもレイアウト矩形は不変なので、無効化ボタンにカーソルを
                    // 乗せても説明パネルに「現在: …（理由）」を表示できる。
                    if (Event.current.type == EventType.Repaint
                        && GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
                    {
                        hoveredThisFrame = i;
                    }
                }
            }
            finally
            {
                UiApiDebugHub.EndReadinessSnapshot();
            }
            EditorGUILayout.EndScrollView();

            // 今フレームのホバー結果を確定。説明パネルは次フレーム冒頭でこれを読む（1 フレーム遅延・体感は即時）。
            if (Event.current.type == EventType.Repaint)
            {
                _hoveredActionIndex = hoveredThisFrame;
            }

            // 結果欄。スプリッターをドラッグして高さを変えられ、収まらない出力はスクロールで読む。
            EditorGUILayout.LabelField("最後の実行結果", EditorStyles.boldLabel);
            DrawResultSplitter();
            _resultScroll = EditorGUILayout.BeginScrollView(_resultScroll, GUILayout.Height(_resultHeight));
            EditorGUILayout.SelectableLabel(_lastResult, EditorStyles.textArea, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// シェルの内部稼働状態を毎フレーム static driver から直読みして表示するライブパネル。
        /// シェルは <see cref="UiShellLifecycleDriver"/>（static）が保持し、シーン上に GameObject が無いため
        /// 通常の Inspector には出せない。その代替として、起動/停止が内部に反映されているかを常時可視化する。
        /// </summary>
        private void DrawLiveShellStatus()
        {
            bool running = UiShellLifecycleDriver.IsRunning;
            int starts = UiShellLifecycleDriver.StartInvocationCount;
            int stops = UiShellLifecycleDriver.StopInvocationCount;
            var bootstrapper = UiShellLifecycleDriver.Current;
            string bootstrapperName = bootstrapper == null ? "<null>" : bootstrapper.GetType().Name;

            string busState;
            try
            {
                var bus = CoreIpcRuntime.Current?.Bus;
                busState = bus == null
                    ? "Bus=<null>"
                    : $"Bus=ok, 接続={bus.Diagnostics.CurrentState}";
            }
            catch (Exception ex)
            {
                busState = "Bus 取得失敗: " + ex.Message;
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                var prevColor = GUI.contentColor;
                GUI.contentColor = running ? new Color(0.4f, 0.85f, 0.4f) : new Color(0.85f, 0.55f, 0.3f);
                EditorGUILayout.LabelField(
                    running ? "● シェル稼働中（内部 IsRunning=true・自動更新）" : "○ シェル停止中（IsRunning=false・自動更新）",
                    EditorStyles.boldLabel);
                GUI.contentColor = prevColor;

                EditorGUILayout.LabelField($"起動回数={starts} / 停止回数={stops} / Bootstrapper={bootstrapperName}");
                EditorGUILayout.LabelField(busState);
            }
        }

        /// <summary>
        /// ホバー中ボタンの効能（説明）と、現在のシステム状態で押して効果があるか（実行可否）を表示する固定高パネル。
        /// 高さを固定しているのは、ホバーごとに行数が変わって UI 全体の高さが揺れるのを防ぐため。
        /// </summary>
        private void DrawDescriptionPanel()
        {
            EditorGUILayout.LabelField("説明（ボタンにマウスを乗せると表示）", EditorStyles.boldLabel);

            var rect = GUILayoutUtility.GetRect(0f, DescriptionBoxHeight, GUILayout.ExpandWidth(true));
            GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);

            const float pad = 5f;
            float statusH = EditorGUIUtility.singleLineHeight;
            var descRect = new Rect(rect.x + pad, rect.y + pad, rect.width - pad * 2f, rect.height - pad * 2f - statusH);
            var statusRect = new Rect(rect.x + pad, rect.yMax - pad - statusH, rect.width - pad * 2f, statusH);

            string description;
            (ActionReadiness state, string note) readiness;
            if (_hoveredActionIndex >= 0 && _hoveredActionIndex < Actions.Length)
            {
                var a = Actions[_hoveredActionIndex];
                description = a.Description;
                readiness = a.EvaluateReadiness();
            }
            else
            {
                description = "ボタンにカーソルを合わせると、その効能と、いま押して効果があるか（前提条件）をここに表示します。";
                readiness = (ActionReadiness.Ready, string.Empty);
            }

            GUI.Label(descRect, description, _wrapStyle);

            if (!string.IsNullOrEmpty(readiness.note))
            {
                var prev = GUI.contentColor;
                GUI.contentColor = ReadinessColor(readiness.state);
                GUI.Label(statusRect, $"{ReadinessIcon(readiness.state)} 現在: {readiness.note}", EditorStyles.miniBoldLabel);
                GUI.contentColor = prev;
            }
        }

        /// <summary>ボタンの色分けが何を意味するかを 1 行で示す凡例。</summary>
        private void DrawButtonColorLegend()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var prev = GUI.contentColor;
                EditorGUILayout.LabelField("ボタン色:", GUILayout.Width(52f));

                GUI.contentColor = ReadyActionColor;
                EditorGUILayout.LabelField("■ 今すぐ実行可", GUILayout.Width(96f));

                GUI.contentColor = new Color(0.6f, 0.6f, 0.6f);
                EditorGUILayout.LabelField("■ 前提不足/変化なし", GUILayout.Width(124f));

                GUI.contentColor = prev;
                EditorGUILayout.LabelField("□ グレーアウト=不可 / 既定色=読み取り専用");
            }
        }

        /// <summary>結果欄の上に置く、縦サイズ変更用のドラッグハンドル。</summary>
        private void DrawResultSplitter()
        {
            var rect = GUILayoutUtility.GetRect(0f, SplitterThickness, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.25f));
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeVertical);

            var e = Event.current;
            switch (e.type)
            {
                case EventType.MouseDown when rect.Contains(e.mousePosition):
                    _resizingResult = true;
                    e.Use();
                    break;
                case EventType.MouseDrag when _resizingResult:
                    // ハンドルを上へドラッグ（delta.y<0）すると結果欄が広がる。ウィンドウ内に収まる範囲に制限。
                    float maxHeight = Mathf.Max(MinResultHeight, position.height - 220f);
                    _resultHeight = Mathf.Clamp(_resultHeight - e.delta.y, MinResultHeight, maxHeight);
                    e.Use();
                    Repaint();
                    break;
                case EventType.MouseUp when _resizingResult:
                    _resizingResult = false;
                    EditorPrefs.SetFloat(ResultHeightPrefKey, _resultHeight);
                    e.Use();
                    break;
            }
        }

        private void Execute(DebugAction action)
        {
            try
            {
                _lastResult = action.Run();
            }
            catch (Exception ex)
            {
                _lastResult = $"[VtsApiDebug] EXCEPTION {action.Label}: {ex}";
                Debug.LogException(ex);
            }
            Repaint();
        }

        // ===== 実行可否（readiness）の表示ヘルパ ==================================

        private static Color ReadinessColor(ActionReadiness state) => state switch
        {
            ActionReadiness.Ready => new Color(0.4f, 0.85f, 0.4f),
            ActionReadiness.Caution => new Color(0.95f, 0.8f, 0.3f),
            _ => new Color(0.95f, 0.5f, 0.4f),
        };

        private static string ReadinessIcon(ActionReadiness state) => state switch
        {
            ActionReadiness.Ready => "✓",
            ActionReadiness.Caution => "⚠",
            _ => "✗",
        };

        // ===== 実行可否（readiness）の評価ヘルパ ==================================
        // Blocked(赤)=モード違いなどで今は不可 / Caution(黄)=前提不足だが別ボタンで準備可 / Ready(緑)=実行可。
        // 評価対象はホバー中の 1 ボタンのみなので、多少重いクエリ（カメラ有無など）を含んでよい。

        private static (ActionReadiness, string) RdPlay()
            => Application.isPlaying
                ? (ActionReadiness.Ready, "実行できます")
                : (ActionReadiness.Blocked, "PlayMode 中のみ実行できます");

        private static (ActionReadiness, string) RdEdit()
            => !Application.isPlaying
                ? (ActionReadiness.Ready, "実行できます")
                : (ActionReadiness.Blocked, "Edit モード（PlayMode を停止）でのみ実行できます");

        private static (ActionReadiness, string) RdShell()
        {
            if (!Application.isPlaying) return (ActionReadiness.Blocked, "PlayMode 中のみ実行できます");
            return UiApiDebugHub.IsShellRunning
                ? (ActionReadiness.Ready, "実行できます")
                : (ActionReadiness.Caution, "シェルが停止中です（先に「シェルを起動」）");
        }

        private static (ActionReadiness, string) RdStartShell()
        {
            if (!Application.isPlaying) return (ActionReadiness.Blocked, "PlayMode 中のみ起動できます");
            return UiApiDebugHub.IsShellRunning
                ? (ActionReadiness.Caution, "すでに稼働中です（押しても変化なし）")
                : (ActionReadiness.Ready, "シェルを起動できます");
        }

        private static (ActionReadiness, string) RdStopShell()
            => UiApiDebugHub.IsShellRunning
                ? (ActionReadiness.Ready, "シェルを停止できます")
                : (ActionReadiness.Caution, "稼働していません（押しても変化なし）");

        private static (ActionReadiness, string) RequireShellThen(bool ready, string missingNote)
        {
            var shell = RdShell();
            if (shell.Item1 != ActionReadiness.Ready) return shell;
            return ready ? (ActionReadiness.Ready, "実行できます") : (ActionReadiness.Caution, missingNote);
        }

        private static (ActionReadiness, string) RdCamera()
            => RequireShellThen(UiApiDebugHub.HasAnyCamera, "カメラがありません（先に「透視カメラを追加」など）");

        private static (ActionReadiness, string) RdStageLight()
            => RequireShellThen(UiApiDebugHub.HasAnyStageLight, "対象ライトがありません（先に光源を追加。要購読）");

        private static (ActionReadiness, string) RdCharSlot()
            => RequireShellThen(UiApiDebugHub.HasAnySlot, "スロットがありません（先に「キャラ状態を購読」。0 件の場合あり）");

        private static (ActionReadiness, string) RdCharAvatar()
        {
            var shell = RdShell();
            if (shell.Item1 != ActionReadiness.Ready) return shell;
            if (!UiApiDebugHub.HasAnySlot) return (ActionReadiness.Caution, "スロットがありません（先に「キャラ状態を購読」）");
            return UiApiDebugHub.HasAnyAvatar
                ? (ActionReadiness.Ready, "実行できます")
                : (ActionReadiness.Caution, "アバターがありません（catalog が空）");
        }

        private static (ActionReadiness, string) RdOscStart()
        {
            if (!Application.isPlaying) return (ActionReadiness.Blocked, "PlayMode 中のみ実行できます");
            if (UiApiDebugHub.IsOscEmitterStarted) return (ActionReadiness.Caution, "すでに起動中です（押しても変化なし）");
            return UiApiDebugHub.HasAnyCamera
                ? (ActionReadiness.Ready, "実行できます")
                : (ActionReadiness.Caution, "カメラがありません（先にカメラを追加）");
        }

        private static (ActionReadiness, string) RdOscSend()
        {
            if (!Application.isPlaying) return (ActionReadiness.Blocked, "PlayMode 中のみ実行できます");
            if (!UiApiDebugHub.IsOscEmitterStarted) return (ActionReadiness.Caution, "emitter 未起動（先に「OSC送信を開始」）");
            return UiApiDebugHub.HasAnyCamera
                ? (ActionReadiness.Ready, "実行できます")
                : (ActionReadiness.Caution, "カメラがありません（先にカメラを追加）");
        }

        private static (ActionReadiness, string) RdOscStop()
            => UiApiDebugHub.IsOscEmitterStarted
                ? (ActionReadiness.Ready, "emitter を停止できます")
                : (ActionReadiness.Caution, "未起動です（押しても変化なし）");
    }
}
