#nullable enable
using System;
using UnityEditor;
using UnityEngine;

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
            public DebugAction(string group, string label, Func<string> run)
            {
                Group = group;
                Label = label;
                Run = run;
            }

            public string Group { get; }
            public string Label { get; }
            public Func<string> Run { get; }
        }

        // 機能ごとにグループ化。新項目はここに 1 行足すだけ。
        // 表示は日本語ラベル。各グループ末尾の // §X は逆引きリファレンス
        // docs/ui-api-reference.md の章対応（開発者向けの内部メモ。UI には出さない）。
        private static readonly DebugAction[] Actions =
        {
            // §A
            new DebugAction("UIシェル", "シェル状態", UiApiDebugHub.ShellStatus),
            new DebugAction("UIシェル", "シェル起動", UiApiDebugHub.StartShell),
            new DebugAction("UIシェル", "シェル停止", UiApiDebugHub.StopShell),
            new DebugAction("UIシェル", "初期化ステップ", UiApiDebugHub.DumpInitSteps),

            // §D
            new DebugAction("タブ", "キャラクタータブへ切替", UiApiDebugHub.SwitchToCharacter),
            new DebugAction("タブ", "ステージタブへ切替", UiApiDebugHub.SwitchToStage),
            new DebugAction("タブ", "カメラタブへ切替", UiApiDebugHub.SwitchToCamera),
            new DebugAction("タブ", "現在のタブ", UiApiDebugHub.ActiveTab),
            new DebugAction("タブ", "プリロード状況", UiApiDebugHub.PreloadProgress),

            // §G/§J
            new DebugAction("IPC", "ランタイム状態", UiApiDebugHub.RuntimeStatus),
            new DebugAction("IPC", "接続状態", UiApiDebugHub.ConnectionStatus),

            // §B/C/E/F/H/K/L（読み取り専用の診断ダンプ）
            new DebugAction("診断（読み取り専用）", "全診断をダンプ", UiApiDebugHub.DumpAllDiagnostics),
            new DebugAction("診断（読み取り専用）", "シェル設定", UiApiDebugHub.DumpShellConfig),
            new DebugAction("診断（読み取り専用）", "スキン検証", UiApiDebugHub.DumpSkinValidation),
            new DebugAction("診断（読み取り専用）", "タブ状態", UiApiDebugHub.DumpTabStates),
            new DebugAction("診断（読み取り専用）", "アセットローダー", UiApiDebugHub.DumpAssetLoader),
            new DebugAction("診断（読み取り専用）", "接続診断", UiApiDebugHub.DumpConnection),
            new DebugAction("診断（読み取り専用）", "出力シーン", UiApiDebugHub.DumpOutputScene),
            new DebugAction("診断（読み取り専用）", "RACアダプタ", UiApiDebugHub.DumpRacAdapter),
            new DebugAction("診断（読み取り専用）", "ステージアダプタ", UiApiDebugHub.DumpStageAdapter),

            // §M
            new DebugAction("キャラクター", "キャラ状態を購読", UiApiDebugHub.SubscribeCharacter),
            new DebugAction("キャラクター", "不正アバターをスロット0へ", UiApiDebugHub.AssignBogusToFirstSlot),
            new DebugAction("キャラクター", "先頭アバターをスロット0へ", UiApiDebugHub.AssignFirstAvatarToFirstSlot),
            new DebugAction("キャラクター", "スロット0をリセット", UiApiDebugHub.ResetFirstSlot),
            new DebugAction("キャラクター", "スロット0をクリア", UiApiDebugHub.ClearFirstSlot),
            new DebugAction("キャラクター", "スロット送信プローブ", UiApiDebugHub.ProbeSlotSend),
            new DebugAction("キャラクター", "キャラ状態（UI側）", UiApiDebugHub.DumpCharacterState),

            // §N
            new DebugAction("ステージ照明", "ステージ状態を購読", UiApiDebugHub.SubscribeStage),
            new DebugAction("ステージ照明", "平行光源を追加", UiApiDebugHub.AddDirectionalLight),
            new DebugAction("ステージ照明", "ポイント光源を追加", UiApiDebugHub.AddPointLight),
            new DebugAction("ステージ照明", "スポット光源を追加", UiApiDebugHub.AddSpotLight),
            new DebugAction("ステージ照明", "直近ライトの強度を上げる", UiApiDebugHub.SetLastLightIntensityHigh),
            new DebugAction("ステージ照明", "直近ライトを赤に", UiApiDebugHub.SetLastLightColorRed),
            new DebugAction("ステージ照明", "直近ライトを削除", UiApiDebugHub.RemoveLastLight),
            new DebugAction("ステージ照明", "ステージをアンロード", UiApiDebugHub.UnloadStage),
            new DebugAction("ステージ照明", "ステージ状態（UI側）", UiApiDebugHub.DumpStageState),

            // §O
            new DebugAction("カメラ", "透視カメラを追加", UiApiDebugHub.AddPerspectiveCamera),
            new DebugAction("カメラ", "平行投影カメラを追加", UiApiDebugHub.AddOrthographicCamera),
            new DebugAction("カメラ", "直近カメラをアクティブ化", UiApiDebugHub.ActivateLastCamera),
            new DebugAction("カメラ", "直近カメラを削除", UiApiDebugHub.DeleteLastCamera),
            new DebugAction("カメラ", "プリセット作成（デモ）", UiApiDebugHub.CreateCameraPresetDemo),
            new DebugAction("カメラ", "全カメラのプレビュー開始", UiApiDebugHub.StartPreviewAll),
            new DebugAction("カメラ", "全カメラのプレビュー停止", UiApiDebugHub.StopPreviewAll),
            new DebugAction("カメラ", "直近カメラにBloomを追加", UiApiDebugHub.AddBloomToLastCamera),
            new DebugAction("カメラ", "直近カメラのVolumeを有効化", UiApiDebugHub.EnableVolumeOnLastCamera),
            new DebugAction("カメラ", "カメラアダプタ", UiApiDebugHub.DumpCameraAdapter),

            // §O-8
            new DebugAction("カメラOSC送信", "OSC送信を開始", UiApiDebugHub.StartOscEmitter),
            new DebugAction("カメラOSC送信", "直近カメラへOSC送信", UiApiDebugHub.SendOscToLastCameraDemo),
            new DebugAction("カメラOSC送信", "OSC送信を停止", UiApiDebugHub.StopOscEmitter),

            // request/response 往復
            new DebugAction("リクエスト/レスポンス", "バス往復プローブ（echo）", UiApiDebugHub.ProbeBusRequestResponse),
            new DebugAction("リクエスト/レスポンス", "プローブ結果", UiApiDebugHub.DumpProbeResult),
            new DebugAction("リクエスト/レスポンス", "プローブの後始末", UiApiDebugHub.CleanupProbe),
            new DebugAction("リクエスト/レスポンス", "直近カメラのVolume schemaを要求", UiApiDebugHub.RequestVolumeMetadataOnLastCamera),
            new DebugAction("リクエスト/レスポンス", "Volume schema結果", UiApiDebugHub.DumpVolumeMetaResult),

            // URP（描画パイプライン）。未割当だと Built-in にフォールバックしメイン出力が黒くなる。
            // URP アセットは Editor の Create>Rendering>URP Asset で正規生成し、下のボタンで割り当てる。
            new DebugAction("URP 設定", "描画パイプライン状態", UiApiDebugHub.DumpRenderPipeline),
            new DebugAction("URP 設定", "プロジェクトのURPアセットを割当", UiApiDebugHub.AssignUrpAssetFromProject),
            new DebugAction("URP 設定", "生成した不完全URPアセットを後始末", UiApiDebugHub.CleanupGeneratedUrpAssets),

            // RDS+Spout 出力経路の実証（シーン非変更・OBS 不要。SpoutSender 数で成立判定）
            new DebugAction("RDS/Spout 検証", "RDS Spout検証 → Display1", UiApiDebugHub.ProbeRdsSpoutToDisplay1),
            new DebugAction("RDS/Spout 検証", "RDS Spout検証 → Display0", UiApiDebugHub.ProbeRdsSpoutToDisplay0),
            new DebugAction("RDS/Spout 検証", "RDS検証の後始末", UiApiDebugHub.CleanupRdsProbe),
            new DebugAction("RDS/Spout 検証", "現在のシーンにRDS結線（Edit時・保存）", UiApiDebugHub.SetupRdsOnCurrentScene),
        };

        private Vector2 _buttonScroll;
        private Vector2 _resultScroll;
        private string _lastResult = "(まだ何も実行していません)";

        [MenuItem("Tools/Hidano/VTuberSystem/Debug/VTS API Debug")]
        public static void Open()
        {
            GetWindow<UiApiDebugWindow>("VTS API Debug");
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                Application.isPlaying
                    ? "PlayMode 中。ボタンを押すと該当 API を実行し、結果を下に表示します。挙動は Game ビュー / Console で確認してください。"
                    : "PlayMode ではありません。シェル/タブ系の操作は PlayMode 中のみ動作します。",
                Application.isPlaying ? MessageType.Info : MessageType.Warning);

            _buttonScroll = EditorGUILayout.BeginScrollView(_buttonScroll);
            string? currentGroup = null;
            foreach (var action in Actions)
            {
                if (action.Group != currentGroup)
                {
                    currentGroup = action.Group;
                    GUILayout.Space(6f);
                    EditorGUILayout.LabelField(currentGroup, EditorStyles.boldLabel);
                }

                if (GUILayout.Button(action.Label))
                {
                    Execute(action);
                }
            }
            EditorGUILayout.EndScrollView();

            GUILayout.Space(8f);
            EditorGUILayout.LabelField("最後の実行結果", EditorStyles.boldLabel);
            _resultScroll = EditorGUILayout.BeginScrollView(_resultScroll, GUILayout.Height(90f));
            EditorGUILayout.SelectableLabel(_lastResult, EditorStyles.textArea, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
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
