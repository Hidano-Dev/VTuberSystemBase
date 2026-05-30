#nullable enable
using UnityEngine;
using VTuberSystemBase.UiToolkitShell.Skin;

namespace VTuberSystemBase.IntegratedDemo
{
    /// <summary>
    /// Inspector で設定する <see cref="IntegratedDemoBootstrap"/> 用の構成オブジェクト。
    /// SkinProfile（必須）と OSC/Preset の上書き設定を 1 か所に集約し、シーンの
    /// 構築手順を README から最小限に保つためのもの。
    /// </summary>
    [System.Serializable]
    public sealed class IntegratedDemoConfig
    {
        [Header("UI Shell")]
        [Tooltip("ui-toolkit-shell の SkinProfile（Display 1 UI 必須）。未設定時は UI 側を起動せず、メイン出力のみ立ち上げる。")]
        public UiToolkitShellSkinProfile? SkinProfile;

        [Tooltip("Display 1 のターゲット表示インデックス。既定 0 でメイン出力（Display 2+）と分離される。")]
        public int UiTargetDisplay = 0;

        [Tooltip("Display 1 を毎フレーム clear/present する presenter カメラを生成する。UI Toolkit オーバーレイがカメラ無しのディスプレイで前フレーム残像により乱れるのを防ぐ。既定 ON。システム上必須ではないので、不要なら OFF にしてよい。")]
        public bool CreateOperatorUiPresenterCamera = true;

        [Tooltip("presenter カメラの背景色（Clear 色）。クロマキー運用時はキー色（緑など）を指定する。既定 黒。")]
        public Color OperatorUiPresenterCameraBackgroundColor = Color.black;

        [Header("Camera Switcher (OSC)")]
        [Tooltip("OSC 送信先ホスト。空のとき camera-switcher-tab の既定（127.0.0.1）が採用される。")]
        public string CameraOscHost = "127.0.0.1";

        [Tooltip("OSC 送信先ポート。0 のとき camera-switcher-tab の既定が採用される。")]
        public int CameraOscPort = 0;

        [Header("Camera Switcher (Preset)")]
        [Tooltip("camera-switcher-tab のプリセット保存ファイル。空のとき persistentDataPath/camera-presets.json。")]
        public string CameraPresetPath = string.Empty;

        [Header("Diagnostics")]
        [Tooltip("メイン出力 Bootstrap が立ち上がった後に手動でアダプタを起動するまで待機するフレーム数（既定 60）。")]
        public int AdapterStartupMaxFrames = 60;
    }
}
