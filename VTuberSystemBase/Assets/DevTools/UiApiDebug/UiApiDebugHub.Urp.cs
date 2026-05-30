#nullable enable
using UnityEngine;
using UnityEngine.Rendering;

namespace VtsApiDebug
{
    /// <summary>
    /// 描画パイプライン（URP）の状態診断と、Spout 出力の目視検証用サポート。
    ///
    /// 背景: このプロジェクトは URP 前提で組まれており（出力カメラに URP 用の追加カメラデータを付与）、
    /// URP の RenderPipelineAsset が未割当だと実行時に Built-in RP へフォールバックし、URP 前提のカメラが
    /// シーンを描画できず メイン出力（= Spout 送出）が真っ黒になる。どの URP アセットを使うか（品質設定含む）は
    /// Unity の <c>Project Settings &gt; Graphics / Quality</c> でユーザーが設定する範疇。
    /// 本クラスはその設定を肩代わりせず、<see cref="DumpRenderPipeline"/> で割当状態を読み取って確認する診断のみを提供する。
    /// </summary>
    public static partial class UiApiDebugHub
    {
        private const string SpoutTestCubeName = "VtsApiDebug_TEMP_TestCube";

        /// <summary>
        /// Spout 出力検証用に、メイン出力カメラの背景を Skybox にしてテスト用キューブを視野内に置く。
        /// PlayMode 限定の一時注入（シーンに保存されない）。Addressables 未ビルドでコンテンツ（アバター/ステージ）が
        /// ロードされず、かつ出力カメラの既定 clearFlags が黒単色のため「Play しただけ」では OBS が黒になる。
        /// 本メソッドで映すものを用意し、RT 経路（カメラ→RenderTexture→SpoutSender Texture モード）が
        /// ディスプレイ非依存で OBS に届くことを目視確認する。
        /// </summary>
        public static string InjectSpoutTestContent()
        {
            if (!Application.isPlaying)
                return Report("InjectSpoutTestContent", false, "PlayMode 中に実行してください（一時注入のため）。");

            var cam = FindMainOutputCamera();
            if (cam == null)
                return Report("InjectSpoutTestContent", false,
                    "DefaultMainOutputCamera が見つかりません（MainDemo を Play していますか）。");

            cam.clearFlags = CameraClearFlags.Skybox;

            var cube = GameObject.Find(SpoutTestCubeName);
            var created = false;
            if (cube == null)
            {
                cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = SpoutTestCubeName;
                created = true;
            }
            cube.transform.position = cam.transform.position + cam.transform.forward * 3f;
            cube.transform.localScale = new Vector3(1.5f, 1.5f, 1.5f);
            cube.transform.rotation = Quaternion.Euler(20f, 30f, 0f);
            var renderer = cube.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material.color = new Color(1f, 0.9f, 0.2f, 1f);
            }

            return Report("InjectSpoutTestContent", true,
                $"メイン出力カメラ '{cam.name}' の背景を Skybox にし、テストキューブを{(created ? "生成" : "再配置")}しました。" +
                "OBS の Spout Source 'RuntimeDisplaySelector_Display_1' に青空＋黄色いキューブが映るはずです" +
                "（Game ビューを Display 2 に切り替える必要はありません＝ディスプレイ非依存）。" +
                "※これは PlayMode 限定の検証用。Stop で消えます。本物のキャラ/ステージ表示には Addressables のビルドが必要です。");
        }

        /// <summary>注入したテスト用キューブを破棄する（背景 Skybox はそのまま）。</summary>
        public static string RemoveSpoutTestContent()
        {
            var cube = GameObject.Find(SpoutTestCubeName);
            if (cube == null)
                return Report("RemoveSpoutTestContent", true, "テストキューブは存在しません。");
            UnityEngine.Object.Destroy(cube);
            return Report("RemoveSpoutTestContent", true, "テストキューブを破棄しました。");
        }

        private static Camera? FindMainOutputCamera()
        {
            var cams = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
            foreach (var c in cams)
            {
                if (c != null && c.name == "DefaultMainOutputCamera" && c.gameObject.scene.name == "MainDemo")
                    return c;
            }
            // フォールバック: 名前一致のみ、それも無ければ Camera.main。
            foreach (var c in cams)
            {
                if (c != null && c.name == "DefaultMainOutputCamera")
                    return c;
            }
            return Camera.main;
        }

        /// <summary>
        /// 現在の有効レンダーパイプライン状態をダンプする（読み取り専用）。
        /// </summary>
        public static string DumpRenderPipeline()
        {
            var def = GraphicsSettings.defaultRenderPipeline;
            var q = QualitySettings.renderPipeline;
            var effective = q ?? def;
            return Report("DumpRenderPipeline", effective != null,
                $"defaultRenderPipeline={(def != null ? def.GetType().Name + ":" + def.name : "null")}、" +
                $"QualitySettings.renderPipeline={(q != null ? q.GetType().Name + ":" + q.name : "null")}、" +
                $"effective={(effective != null ? effective.GetType().Name : "null (= Built-in RP)")}");
        }
    }
}
