#nullable enable
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VTuberSystemBase.OutputRendererShell.Abstractions;
using VTuberSystemBase.OutputRendererShell.Scene;
using RdsFacade = Hidano.RuntimeDisplaySelector.RuntimeDisplaySelector;

namespace VtsApiDebug
{
    /// <summary>
    /// RuntimeDisplaySelector（RDS）+ Klak Spout を使った出力経路の検証プローブ。
    ///
    /// 目的: MainDemo を RDS+Spout に本結線する前に、「Editor PlayMode で Spout sender が
    /// 実際に立つか（DisplayIndexValidator が単一ディスプレイの Editor で displayIndex を
    /// 弾かないか）」を、シーンを変更せず・OBS 無しで実証する。検証は
    /// <see cref="Klak.Spout.SpoutSender"/> コンポーネントがシーンに生成された数で行う
    /// （sender が立てば OBS の Spout Source で受けられる）。
    ///
    /// RDS Facade は MonoBehaviour シングルトン（<c>Current</c>）。prefab ロード（パス文字列＝
    /// uloop の quote 問題）を避け、無ければ AddComponent で一時生成する。後始末は
    /// <see cref="CleanupRdsProbe"/>。
    /// </summary>
    public static partial class UiApiDebugHub
    {
        private static GameObject? _rdsProbeGo;

        /// <summary>
        /// 直近カメラ（無ければ Camera.main / 任意のカメラ）を displayIndex=1 に RDS でアサインし、
        /// Spout sender が立つかを SpoutSender コンポーネント数で検証する。シーンは変更しない。
        /// </summary>
        public static string ProbeRdsSpoutToDisplay1() => ProbeRdsSpout(1);

        /// <summary>displayIndex=0 版（Editor でも通る基準ケース。1 と比較して制約を切り分ける）。</summary>
        public static string ProbeRdsSpoutToDisplay0() => ProbeRdsSpout(0);

        private static string ProbeRdsSpout(int displayIndex)
        {
            if (!RequirePlayMode(out var guard)) return guard;

            var cam = Camera.main;
            if (cam == null) cam = UnityEngine.Object.FindAnyObjectByType<Camera>();
            if (cam == null) return Report("ProbeRdsSpout", false, "no Camera in scene to assign.");

            var before = UnityEngine.Object.FindObjectsByType<Klak.Spout.SpoutSender>(FindObjectsSortMode.None).Length;

            // RDS Facade を取得（無ければ一時生成。AddComponent で Awake が同期実行され Current にセットされる）。
            var rds = RdsFacade.Current;
            var createdHere = false;
            if (rds == null)
            {
                _rdsProbeGo = new GameObject("[VtsDebug.RDS]");
                rds = _rdsProbeGo.AddComponent<RdsFacade>();
                createdHere = true;
            }

            if (RdsFacade.Current == null)
                return Report("ProbeRdsSpout", false, "RuntimeDisplaySelector.Current is still null after AddComponent (Awake did not set it?).");

            try
            {
                RdsFacade.Current.AssignCameraToDisplay(cam, displayIndex);
            }
            catch (Exception ex)
            {
                return Report("ProbeRdsSpout", false,
                    $"AssignCameraToDisplay(cam, {displayIndex}) threw {ex.GetType().Name}: {ex.Message}. " +
                    $"(createdFacadeHere={createdHere}) — Editor がこの displayIndex を弾く場合は standalone でのみ成立。");
            }

            var after = UnityEngine.Object.FindObjectsByType<Klak.Spout.SpoutSender>(FindObjectsSortMode.None).Length;
            var spoutLit = after > before;
            return Report("ProbeRdsSpout", spoutLit,
                $"AssignCameraToDisplay(cam, {displayIndex}) OK. SpoutSender count {before} -> {after} " +
                $"(createdFacadeHere={createdHere}). " +
                (spoutLit
                    ? $"Spout sender 'RuntimeDisplaySelector_Display_{displayIndex}' が立った（OBS の Spout Source で受信可能のはず）。"
                    : "SpoutSender は増えなかった（Spout 経路が成立していない可能性。要確認）。"));
        }

        /// <summary>プローブで一時生成した RDS Facade GameObject を破棄する。</summary>
        public static string CleanupRdsProbe()
        {
            if (_rdsProbeGo == null) return Report("CleanupRdsProbe", true, "no probe facade to clean up.");
            try { UnityEngine.Object.Destroy(_rdsProbeGo); }
            catch (Exception ex) { return Report("CleanupRdsProbe", false, "destroy threw: " + ex.Message); }
            _rdsProbeGo = null;
            return Report("CleanupRdsProbe", true, "probe facade destroyed.");
        }

        // ===== 本結線（Edit モードでシーンを編集して保存） =====

        /// <summary>
        /// 現在開いているシーン（MainDemo 想定）を RDS+Spout 出力経路に結線する:
        /// (1) RDS Facade コンポーネントが無ければ配置 (2) OutputSceneBootstrapper の
        /// RoutingProvider を RuntimeDisplaySelector に、Spout sender 名を設定 (3) シーンを保存。
        /// Edit モード専用（PlayMode 中はシーン変更が保存されないため拒否）。冪等。
        /// </summary>
        public static string SetupRdsOnCurrentScene()
        {
            if (Application.isPlaying)
                return Report("SetupRdsOnCurrentScene", false,
                    "Edit モードで実行してください（PlayMode 中はシーン変更が保存されません）。");

            var osb = UnityEngine.Object.FindAnyObjectByType<OutputSceneBootstrapper>();
            if (osb == null)
                return Report("SetupRdsOnCurrentScene", false,
                    "OutputSceneBootstrapper が見つからない（MainDemo を開いていますか）。");

            var scene = osb.gameObject.scene;

            // (1) RDS Facade をシーンに配置（無ければ）。prefab ではなく素の GameObject + AddComponent。
            var rds = UnityEngine.Object.FindAnyObjectByType<RdsFacade>();
            var createdRds = false;
            if (rds == null)
            {
                var go = new GameObject("RuntimeDisplaySelector");
                go.AddComponent<RdsFacade>();
                Undo.RegisterCreatedObjectUndo(go, "Create RuntimeDisplaySelector");
                createdRds = true;
            }

            // (2) OutputSceneBootstrapper の routing 設定を SerializedObject 経由で変更。
            var so = new SerializedObject(osb);
            var providerProp = so.FindProperty("_routingProvider");
            var spoutProp = so.FindProperty("_spoutSenderName");
            if (providerProp == null)
                return Report("SetupRdsOnCurrentScene", false, "_routingProvider プロパティが見つからない（OutputSceneBootstrapper の内部実装変更？）。");

            providerProp.enumValueIndex = (int)DisplayRoutingProvider.RuntimeDisplaySelector;
            if (spoutProp != null) spoutProp.stringValue = "VsbMainOutput";
            so.ApplyModifiedProperties();

            // (3) シーンを保存。
            EditorSceneManager.MarkSceneDirty(scene);
            var saved = EditorSceneManager.SaveScene(scene);

            return Report("SetupRdsOnCurrentScene", saved,
                $"RDS Facade {(createdRds ? "を新規配置" : "は既存を使用")}、" +
                $"OutputSceneBootstrapper.RoutingProvider=RuntimeDisplaySelector / Spout='VsbMainOutput' に設定、" +
                $"シーン '{scene.name}' を保存{(saved ? "成功" : "失敗")}。" +
                "※実際の Spout sender 名は RDS の SenderNamingPolicy 依存（RuntimeDisplaySelector_Display_{index}）。PlayMode で検証してください。");
        }
    }
}
