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

        // 逆引き章ごとにグループ化。新項目はここに 1 行足すだけ。
        private static readonly DebugAction[] Actions =
        {
            new DebugAction("A. UI シェル", "Shell Status", UiApiDebugHub.ShellStatus),
            new DebugAction("A. UI シェル", "Start Shell", UiApiDebugHub.StartShell),
            new DebugAction("A. UI シェル", "Stop Shell", UiApiDebugHub.StopShell),
            new DebugAction("A. UI シェル", "Dump Init Steps", UiApiDebugHub.DumpInitSteps),

            new DebugAction("D. タブ切替", "Switch -> Character", UiApiDebugHub.SwitchToCharacter),
            new DebugAction("D. タブ切替", "Switch -> Stage", UiApiDebugHub.SwitchToStage),
            new DebugAction("D. タブ切替", "Switch -> Camera", UiApiDebugHub.SwitchToCamera),
            new DebugAction("D. タブ切替", "Active Tab", UiApiDebugHub.ActiveTab),
            new DebugAction("D. タブ切替", "Preload Progress", UiApiDebugHub.PreloadProgress),

            new DebugAction("G/J. IPC", "Runtime Status", UiApiDebugHub.RuntimeStatus),
            new DebugAction("G/J. IPC", "Connection Status", UiApiDebugHub.ConnectionStatus),

            new DebugAction("O. Camera", "Add Perspective Camera", UiApiDebugHub.AddPerspectiveCamera),
            new DebugAction("O. Camera", "Add Orthographic Camera", UiApiDebugHub.AddOrthographicCamera),
            new DebugAction("O. Camera", "Activate Last Camera", UiApiDebugHub.ActivateLastCamera),
            new DebugAction("O. Camera", "Delete Last Camera", UiApiDebugHub.DeleteLastCamera),
            new DebugAction("O. Camera", "Create Preset (demo)", UiApiDebugHub.CreateCameraPresetDemo),
            new DebugAction("O. Camera", "Start Preview All", UiApiDebugHub.StartPreviewAll),
            new DebugAction("O. Camera", "Stop Preview All", UiApiDebugHub.StopPreviewAll),
            new DebugAction("O. Camera", "Add Bloom (last cam)", UiApiDebugHub.AddBloomToLastCamera),
            new DebugAction("O. Camera", "Enable Volume (last cam)", UiApiDebugHub.EnableVolumeOnLastCamera),
            new DebugAction("O. Camera", "Dump Camera Adapter", UiApiDebugHub.DumpCameraAdapter),
        };

        private Vector2 _buttonScroll;
        private Vector2 _resultScroll;
        private string _lastResult = "(まだ何も実行していません)";

        [MenuItem("Tools/VTS API Debug")]
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
