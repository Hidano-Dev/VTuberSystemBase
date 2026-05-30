#nullable enable
using System;
using UnityEditor;
using UnityEngine;
using VTuberSystemBase.CoreIpc.Core;
using VTuberSystemBase.UiToolkitShell.Bootstrap;

namespace VtsApiDebug
{
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
            public DebugAction(string group, string label, string tooltip, Func<string> run)
            {
                Group = group;
                Label = label;
                Tooltip = tooltip;
                Run = run;
            }

            public string Group { get; }
            public string Label { get; }

            /// <summary>ボタンの詳細説明。ホバー時に下部「説明」パネルへ表示する（GUIContent.tooltip と同内容）。</summary>
            public string Tooltip { get; }

            public Func<string> Run { get; }
        }

        // 機能ごとにグループ化。新項目はここに 1 行足すだけ。
        // 表示は日本語ラベル。第 3 引数はホバー時に下部「説明」パネルへ出す詳細説明で、
        // 各 UiApiDebugHub メソッドの <summary> と同じ内容に揃えてある。
        //
        // ラベル命名規約: 状態を「表示するだけ（読み取り専用）」のボタンは末尾を「〜を表示」にして、
        // 押すと処理が走る動作ボタン（「〜を起動」「〜を追加」等）と一目で区別できるようにする。
        // 例: 「初期化ステップ履歴を表示」は初期化を実行するのではなく、到達済みステップ列を読むだけ。
        //
        // 各グループ末尾の // §X は逆引きリファレンス docs/ui-api-reference.md の章対応
        // （開発者向けの内部メモ。UI には出さない）。
        private static readonly DebugAction[] Actions =
        {
            // §A
            new DebugAction("UIシェル", "シェル状態を表示",
                "シェルの稼働状態・起動/停止回数・現在の bootstrapper 型を返す（読み取り専用）。", UiApiDebugHub.ShellStatus),
            new DebugAction("UIシェル", "シェルを起動",
                "登録済みの config provider を使ってシェルを起動する（稼働中なら no-op）。PlayMode 必須。", UiApiDebugHub.StartShell),
            new DebugAction("UIシェル", "シェルを停止",
                "稼働中のシェルを停止し、購読・UIDocument 等を破棄する。", UiApiDebugHub.StopShell),
            new DebugAction("UIシェル", "初期化ステップ履歴を表示",
                "直近の起動で到達した初期化ステップ列を返す（どこまで進んだか）。実行はせず読むだけ。", UiApiDebugHub.DumpInitSteps),

            // §D
            new DebugAction("タブ", "キャラクタータブへ切替",
                "表示タブを Character に切り替える（style.display の付け替えのみ。再クローンはしない）。PlayMode 必須。", UiApiDebugHub.SwitchToCharacter),
            new DebugAction("タブ", "ステージタブへ切替",
                "表示タブを StageLighting に切り替える。PlayMode 必須。", UiApiDebugHub.SwitchToStage),
            new DebugAction("タブ", "カメラタブへ切替",
                "表示タブを CameraSwitcher に切り替える。PlayMode 必須。", UiApiDebugHub.SwitchToCamera),
            new DebugAction("タブ", "現在のタブを表示",
                "現在表示中のタブを返す（読み取り専用）。", UiApiDebugHub.ActiveTab),
            new DebugAction("タブ", "プリロード状況を表示",
                "3 タブのプリロード完了状況（完了フラグ・タブ数・進捗）を返す（読み取り専用）。", UiApiDebugHub.PreloadProgress),

            // §G/§J
            new DebugAction("IPC", "ランタイム状態を表示",
                "IPC ランタイムが起動済みで Bus を取り出せるかを返す（読み取り専用）。", UiApiDebugHub.RuntimeStatus),
            new DebugAction("IPC", "接続状態を表示",
                "バスの現在の接続状態（Connected / Reconnecting など）を返す（読み取り専用）。", UiApiDebugHub.ConnectionStatus),

            // §B/C/E/F/H/K/L（読み取り専用の診断ダンプ）
            new DebugAction("診断（読み取り専用）", "全診断を表示",
                "Phase2 の全 Inspection（シェル構成〜各アダプタ）を順に実行し、結合した結果を返す。", UiApiDebugHub.DumpAllDiagnostics),
            new DebugAction("診断（読み取り専用）", "シェル設定を表示",
                "UI シェル構成（SkinProfile / 表示先 / バス / スキン UXML 資産）を読む。", UiApiDebugHub.DumpShellConfig),
            new DebugAction("診断（読み取り専用）", "スキン検証結果を表示",
                "起動時のスキン検証結果（必須 USS クラスの欠落一覧）を読む。新たに検証は実行しない。", UiApiDebugHub.DumpSkinValidation),
            new DebugAction("診断（読み取り専用）", "タブ状態を表示",
                "タブの状態（アクティブ / プリロード進捗 / 失敗タブ）を読む。", UiApiDebugHub.DumpTabStates),
            new DebugAction("診断（読み取り専用）", "アセットローダー状態を表示",
                "Addressables ローダーの稼働カウンタ（pending / completed / failed）を読む。", UiApiDebugHub.DumpAssetLoader),
            new DebugAction("診断（読み取り専用）", "接続診断を表示",
                "UI 側 IConnectionStatus（接続中か / 現在のステータスコード）を読む。", UiApiDebugHub.DumpConnection),
            new DebugAction("診断（読み取り専用）", "出力シーン状態を表示",
                "出力シーン（Display 2+）の初期化フェーズ・表示割当・ハンドラ数・直近エラーを読む。", UiApiDebugHub.DumpOutputScene),
            new DebugAction("診断（読み取り専用）", "RACアダプタ状態を表示",
                "RAC（Character）アダプタの稼働状態・スロット数・カタログ数を読む。", UiApiDebugHub.DumpRacAdapter),
            new DebugAction("診断（読み取り専用）", "ステージアダプタ状態を表示",
                "Stage/Light/Volume アダプタの ready 状態・ライト数・ステージキーを読む。", UiApiDebugHub.DumpStageAdapter),

            // §M
            new DebugAction("キャラクター", "キャラ状態を購読",
                "slots/catalog・avatars/catalog と各 slot の status/error を購読キャッシュする（操作の前に 1 度実行）。PlayMode 必須。", UiApiDebugHub.SubscribeCharacter),
            new DebugAction("キャラクター", "不正アバターをスロット0へ割当",
                "先頭スロットに存在しないアバターキーを割り当て、KeyNotFound 応答で IPC 経路を検証する。", UiApiDebugHub.AssignBogusToFirstSlot),
            new DebugAction("キャラクター", "先頭アバターをスロット0へ割当",
                "avatars/catalog の先頭アバターを先頭スロットに割り当てる（catalog が空なら NG）。", UiApiDebugHub.AssignFirstAvatarToFirstSlot),
            new DebugAction("キャラクター", "スロット0をリセット",
                "先頭スロットへ Reset コマンドを送る（event slot/{id}/command）。", UiApiDebugHub.ResetFirstSlot),
            new DebugAction("キャラクター", "スロット0をクリア",
                "先頭スロットを空にする（state slot/{id}/assignment, AvatarKey=null）。", UiApiDebugHub.ClearFirstSlot),
            new DebugAction("キャラクター", "スロット送信プローブを実行",
                "合成 slotId へ assignment を 1 件送り、UI→bus の送信パスが生きているかだけを確認する（ハンドラ未登録のため往復はしない）。", UiApiDebugHub.ProbeSlotSend),
            new DebugAction("キャラクター", "キャラ状態を表示（UI側）",
                "UI 側にキャッシュした Character 状態（slot 一覧・状態・avatar 一覧）を読む。", UiApiDebugHub.DumpCharacterState),

            // §N
            new DebugAction("ステージ照明", "ステージ状態を購読",
                "lights/list・stage/current・light/added・light/error を購読し、UI 側状態をキャッシュする（操作の前に 1 度実行）。PlayMode 必須。", UiApiDebugHub.SubscribeStage),
            new DebugAction("ステージ照明", "平行光源を追加",
                "Directional ライトを追加（event light/command, op=add）。id はアダプタが採番し lights/list に反映。検証は「ステージアダプタ状態を表示」。", UiApiDebugHub.AddDirectionalLight),
            new DebugAction("ステージ照明", "ポイント光源を追加",
                "Point ライトを追加（event light/command, op=add）。", UiApiDebugHub.AddPointLight),
            new DebugAction("ステージ照明", "スポット光源を追加",
                "Spot ライトを追加（event light/command, op=add）。", UiApiDebugHub.AddSpotLight),
            new DebugAction("ステージ照明", "直近ライトの強度を上げる",
                "直近に追加したライトの intensity を 4 に上げる（state light property）。", UiApiDebugHub.SetLastLightIntensityHigh),
            new DebugAction("ステージ照明", "直近ライトを赤に変更",
                "直近に追加したライトの color を赤 (1,0,0,1) に設定する。", UiApiDebugHub.SetLastLightColorRed),
            new DebugAction("ステージ照明", "直近ライトを削除",
                "直近に追加したライトを削除（event light/command, op=remove）。", UiApiDebugHub.RemoveLastLight),
            new DebugAction("ステージ照明", "ステージをアンロード",
                "ステージを解除する（event stage/command, op=unload）。", UiApiDebugHub.UnloadStage),
            new DebugAction("ステージ照明", "ステージ状態を表示（UI側）",
                "UI 側にキャッシュした Stage 状態（現在ステージ・ライト一覧）を読む。", UiApiDebugHub.DumpStageState),

            // §O
            new DebugAction("カメラ", "透視カメラを追加",
                "Perspective カメラを追加（event camera/command, op=add）。反映は非同期なので検証は「カメラアダプタ状態を表示」で。", UiApiDebugHub.AddPerspectiveCamera),
            new DebugAction("カメラ", "平行投影カメラを追加",
                "Orthographic カメラを追加（event camera/command, op=add）。", UiApiDebugHub.AddOrthographicCamera),
            new DebugAction("カメラ", "直近カメラをアクティブ化",
                "直近に追加したカメラをアクティブに切替（event camera/command, op=active-set）。", UiApiDebugHub.ActivateLastCamera),
            new DebugAction("カメラ", "直近カメラを削除",
                "直近に追加したカメラを削除（event camera/command, op=delete）。", UiApiDebugHub.DeleteLastCamera),
            new DebugAction("カメラ", "プリセットを作成（デモ）",
                "デモ名でカメラプリセットを作成（event camera/preset/command, op=create）。", UiApiDebugHub.CreateCameraPresetDemo),
            new DebugAction("カメラ", "全カメラのプレビューを開始",
                "全カメラのプレビューを開始（attach, 320x180@15fps）。", UiApiDebugHub.StartPreviewAll),
            new DebugAction("カメラ", "全カメラのプレビューを停止",
                "全カメラのプレビューを停止（detach）。", UiApiDebugHub.StopPreviewAll),
            new DebugAction("カメラ", "直近カメラにBloomを追加",
                "直近に追加したカメラに Bloom の Volume override を追加する（op=override-add）。", UiApiDebugHub.AddBloomToLastCamera),
            new DebugAction("カメラ", "直近カメラのVolumeを有効化",
                "直近に追加したカメラの Volume を有効化（state camera/{id}/volume/enabled=true）。", UiApiDebugHub.EnableVolumeOnLastCamera),
            new DebugAction("カメラ", "カメラアダプタ状態を表示",
                "camera-switcher-output-adapter の診断（カメラ数・アクティブ・OSC 受信状態）を読む。", UiApiDebugHub.DumpCameraAdapter),

            // §O-8
            new DebugAction("カメラOSC送信", "OSC送信を開始",
                "emitter を出力アダプタの実際の受信 host/port へ向けて起動する（ポートを推測しない＝UDP の偽成功回避）。PlayMode 必須。", UiApiDebugHub.StartOscEmitter),
            new DebugAction("カメラOSC送信", "直近カメラへOSC送信",
                "直近に追加したカメラへ特徴的な position で OSC を 1 フレーム送る。往復検証は「カメラアダプタ状態を表示」の OscFramesReceived/Applied で。", UiApiDebugHub.SendOscToLastCameraDemo),
            new DebugAction("カメラOSC送信", "OSC送信を停止",
                "emitter を停止し hidden GameObject / socket を破棄する。", UiApiDebugHub.StopOscEmitter),

            // request/response 往復
            new DebugAction("リクエスト/レスポンス", "バス往復プローブを実行（echo）",
                "バスに echo handler を登録し、自分で request を投げて往復を実証する。~1 秒後に「プローブ結果を表示」で確認。PlayMode 必須。", UiApiDebugHub.ProbeBusRequestResponse),
            new DebugAction("リクエスト/レスポンス", "プローブ結果を表示",
                "直近の request/response プローブの結果を読む。", UiApiDebugHub.DumpProbeResult),
            new DebugAction("リクエスト/レスポンス", "プローブの後始末",
                "プローブの echo handler を解除する。", UiApiDebugHub.CleanupProbe),
            new DebugAction("リクエスト/レスポンス", "直近カメラのVolume schemaを要求",
                "直近カメラの volume override schema を request（responseSink 経由の往復実証）。要カメラ。~1 秒後に「Volume schema結果を表示」で確認。", UiApiDebugHub.RequestVolumeMetadataOnLastCamera),
            new DebugAction("リクエスト/レスポンス", "Volume schema結果を表示",
                "直近の volume metadata request の結果を読む。", UiApiDebugHub.DumpVolumeMetaResult),

            // URP（描画パイプライン）。未割当だと Built-in にフォールバックしメイン出力が黒くなる。
            // URP アセットは Editor の Create>Rendering>URP Asset で正規生成し、下のボタンで割り当てる。
            new DebugAction("URP 設定", "描画パイプライン状態を表示",
                "現在の有効レンダーパイプライン状態をダンプ（読み取り専用）。null は Built-in RP フォールバックで出力が黒くなる原因。", UiApiDebugHub.DumpRenderPipeline),
            new DebugAction("URP 設定", "プロジェクトのURPアセットを割当",
                "プロジェクト内の正規 URP アセットを既定パイプラインに割り当てる（不完全な Vsb* は除外。候補 0/複数件なら割り当てない）。Edit モード専用。", UiApiDebugHub.AssignUrpAssetFromProject),
            new DebugAction("URP 設定", "生成した不完全URPアセットを後始末",
                "本セッションでプログラム生成した不完全 URP アセット（Assets/Settings/Vsb*）を削除し、割当も null（Built-in）に戻す。Edit モード専用・冪等。", UiApiDebugHub.CleanupGeneratedUrpAssets),

            // Spout 出力の目視検証（PlayMode 中）。Play しただけだと黒（コンテンツ無し＋黒クリア）なので、
            // 映すものを一時注入して OBS の RuntimeDisplaySelector_Display_1 に出るか確認する。
            new DebugAction("Spout 検証", "テスト用コンテンツを注入（Skybox＋キューブ）",
                "メイン出力カメラの背景を Skybox にしテストキューブを視野内に置く。OBS の Spout Source に映るか目視確認。PlayMode 限定の一時注入（Stop で消える）。", UiApiDebugHub.InjectSpoutTestContent),
            new DebugAction("Spout 検証", "テスト用コンテンツを削除",
                "注入したテストキューブを破棄する（背景 Skybox はそのまま）。", UiApiDebugHub.RemoveSpoutTestContent),

            // RDS+Spout 出力経路の実証（シーン非変更・OBS 不要。SpoutSender 数で成立判定）
            new DebugAction("RDS/Spout 検証", "RDS Spout検証を実行 → Display1",
                "直近/任意のカメラを displayIndex=1 に RDS でアサインし、SpoutSender が立つかを数で検証する（シーン非変更）。PlayMode 必須。", UiApiDebugHub.ProbeRdsSpoutToDisplay1),
            new DebugAction("RDS/Spout 検証", "RDS Spout検証を実行 → Display0",
                "displayIndex=0 版（Editor でも通る基準ケース。1 と比較して表示制約を切り分ける）。PlayMode 必須。", UiApiDebugHub.ProbeRdsSpoutToDisplay0),
            new DebugAction("RDS/Spout 検証", "RDS検証の後始末",
                "プローブで一時生成した RDS Facade GameObject を破棄する。", UiApiDebugHub.CleanupRdsProbe),
            new DebugAction("RDS/Spout 検証", "現在のシーンにRDS結線（Edit時・保存）",
                "現在のシーン（MainDemo 想定）を RDS+Spout 出力経路に結線して保存する（RDS Facade 配置 + RoutingProvider/Spout 名設定）。Edit モード専用・冪等。", UiApiDebugHub.SetupRdsOnCurrentScene),
        };

        private const string ResultHeightPrefKey = "VtsApiDebug.ResultHeight";
        private const float MinResultHeight = 60f;
        private const float SplitterThickness = 6f;

        private Vector2 _buttonScroll;
        private Vector2 _resultScroll;
        private string _lastResult = "(まだ何も実行していません)";

        /// <summary>下部「説明」パネルに表示する、直近でホバーしたボタンの説明。Repaint 時に <see cref="GUI.tooltip"/> から更新する。</summary>
        private string _hoverDescription = string.Empty;

        /// <summary>結果欄の高さ（スプリッターのドラッグで可変・EditorPrefs に永続化）。</summary>
        private float _resultHeight = 140f;
        private bool _resizingResult;

        [MenuItem("Tools/Hidano/VTuberSystem/Debug/VTS API Debug")]
        public static void Open()
        {
            GetWindow<UiApiDebugWindow>("VTS API Debug");
        }

        private void OnEnable()
        {
            // GUIContent.tooltip の浮動表示はマウス停止中の MouseMove / Repaint で評価されるが、
            // EditorWindow（特に PlayMode 中）は idle 再描画が抑制され不安定。そのため浮動表示には頼らず、
            // 下部の常設「説明」パネル（GUI.tooltip を読む）を主たる説明手段にしている。
            // wantsMouseMove はホバー追従の再描画を確実にするために有効化しておく。
            wantsMouseMove = true;
            _resultHeight = EditorPrefs.GetFloat(ResultHeightPrefKey, 140f);
        }

        // EditorWindow に対して毎秒約 10 回呼ばれる。シェル状態のライブパネルを自動更新するために再描画する。
        private void OnInspectorUpdate() => Repaint();

        private void OnGUI()
        {
            // ホバー追従（説明パネル）のため、マウス移動でも再描画する。
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

            // ボタン一覧（残りの縦スペースを占有）。
            _buttonScroll = EditorGUILayout.BeginScrollView(_buttonScroll, GUILayout.ExpandHeight(true));
            string? currentGroup = null;
            foreach (var action in Actions)
            {
                if (action.Group != currentGroup)
                {
                    currentGroup = action.Group;
                    GUILayout.Space(6f);
                    EditorGUILayout.LabelField(currentGroup, EditorStyles.boldLabel);
                }

                if (GUILayout.Button(new GUIContent(action.Label, action.Tooltip)))
                {
                    Execute(action);
                }
            }
            EditorGUILayout.EndScrollView();

            // ホバー中ボタンの説明（浮動ツールチップの代替。Edit/Play 両対応で常時見える）。
            if (Event.current.type == EventType.Repaint)
            {
                _hoverDescription = GUI.tooltip;
            }
            EditorGUILayout.LabelField("説明（ボタンにマウスを乗せると表示）", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                string.IsNullOrEmpty(_hoverDescription)
                    ? "ボタンにカーソルを合わせると、その機能の詳細をここに表示します。"
                    : _hoverDescription,
                MessageType.None);

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
        /// 通常の Inspector には出せない。その代替として、ここで起動/停止が内部に反映されているかを常時可視化する。
        /// （Report 経由だと毎フレーム Console へログが出てしまうので、ここでは static を直接読む。）
        /// </summary>
        private void DrawLiveShellStatus()
        {
            bool running = UiShellLifecycleDriver.IsRunning;
            int starts = UiShellLifecycleDriver.StartInvocationCount;
            int stops = UiShellLifecycleDriver.StopInvocationCount;
            var bootstrapper = UiShellLifecycleDriver.Current;
            string bootstrapperName = bootstrapper == null ? "<null>" : bootstrapper.GetType().Name;

            // IPC バス / 接続状態も「本当に内部で動いているか」の指標として併記する（読み取りのみ・例外は握り潰す）。
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
    }
}
