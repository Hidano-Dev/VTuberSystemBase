#nullable enable
namespace VtsApiDebug
{
    /// <summary>
    /// UI（<see cref="UiApiDebugWindow"/>）が各ボタンの「今押して効果があるか」を判定するための、
    /// 副作用なし・ログなしのライブ状態クエリ群。各 partial が持つ追跡フィールド
    /// （直近カメラ・購読フラグ・OSC emitter 等）を読むだけで、IPC 送信や Console ログは伴わない。
    ///
    /// ホバー中の 1 ボタン分しか評価しないことを前提にしているため、
    /// <see cref="HasAnyCamera"/> のように出力アダプタ診断を引く（FindAnyObjectByType を含む）
    /// 重めのクエリも許容している。全ボタンを毎フレーム評価する用途には使わないこと。
    /// </summary>
    public static partial class UiApiDebugHub
    {
        /// <summary>シェルが稼働しているか（CommandClient / SubscriptionClient が使えるか）。</summary>
        public static bool IsShellRunning
            => VTuberSystemBase.UiToolkitShell.Bootstrap.UiShellLifecycleDriver.IsRunning;

        /// <summary>出力アダプタに 1 つ以上カメラが存在するか（直近カメラ系操作の前提）。</summary>
        public static bool HasAnyCamera => AllCameraIds().Count > 0;

        /// <summary>ステージ状態を購読済みか（ライト一覧の解決に必要）。</summary>
        public static bool IsStageSubscribed => _stageSubscribed;

        /// <summary>UI 側にライトが 1 つ以上キャッシュされているか（直近ライト系操作の前提）。</summary>
        public static bool HasAnyStageLight => _stageLights.Count > 0;

        /// <summary>キャラクター状態を購読済みか（スロット/アバターの解決に必要）。</summary>
        public static bool IsCharacterSubscribed => _charSubscribed;

        /// <summary>UI 側にスロットが 1 つ以上キャッシュされているか。</summary>
        public static bool HasAnySlot => _charSlots.Count > 0;

        /// <summary>UI 側にアバターが 1 つ以上キャッシュされているか。</summary>
        public static bool HasAnyAvatar => _charAvatars.Count > 0;

        /// <summary>OSC emitter が起動済みか（OSC 送信の前提）。</summary>
        public static bool IsOscEmitterStarted => _oscEmitter != null;
    }
}
