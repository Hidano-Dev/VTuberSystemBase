#nullable enable
using System;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace VtsApiDebug
{
    /// <summary>
    /// URP（Universal Render Pipeline）有効化サポート。
    ///
    /// 背景: このプロジェクトは URP 前提で組まれている（<c>DefaultCameraFactory</c> が
    /// メイン出力カメラに <see cref="UniversalAdditionalCameraData"/> を明示付与、URP パッケージ /
    /// <c>UniversalRenderPipelineGlobalSettings</c> / <c>DefaultVolumeProfile</c> 導入済み）。
    /// しかし URP の RenderPipelineAsset（パイプライン本体）が未作成・未割当だったため実行時に Built-in RP へ
    /// フォールバックし、URP 前提のカメラがシーンを描画できず メイン出力（= Spout 送出）が真っ黒になっていた。
    ///
    /// 注意: URP アセットの「プログラム生成」（<see cref="UniversalRenderPipelineAsset.Create"/> + 素の
    /// <see cref="UniversalRendererData"/>）は Unity 6 / URP 17 では RenderGraph 必須リソースを満たせず
    /// 不完全になり、<c>Render Graph Execution error: ...null resource index</c> でカメラが描画しなくなる。
    /// そのため URP アセットは Editor の <c>Create &gt; Rendering &gt; URP Asset (with Universal Renderer)</c>
    /// で正規生成し、本クラスの <see cref="AssignUrpAssetFromProject"/> で既定パイプラインへ割り当てる運用とする。
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

        /// <summary>
        /// プロジェクト内の <see cref="UniversalRenderPipelineAsset"/> を検索し、
        /// <see cref="GraphicsSettings.defaultRenderPipeline"/> に割り当てる（全 Quality レベルは override 無し＝
        /// default にフォールバックするためこれで有効化される）。Edit モード専用。
        ///
        /// 本セッションで生成した不完全アセット（名前に <c>Vsb</c> を含む）は候補から除外する。
        /// 候補が 0 件 / 複数件のときはその旨を報告して割り当てない（曖昧回避）。
        /// </summary>
        public static string AssignUrpAssetFromProject()
        {
            if (Application.isPlaying)
                return Report("AssignUrpAssetFromProject", false,
                    "Edit モードで実行してください（PlayMode 中は設定変更が安定しません）。");

            var guids = AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset");
            var sb = new StringBuilder();
            UniversalRenderPipelineAsset? chosen = null;
            var candidateCount = 0;

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
                if (asset == null) continue;
                var isIncomplete = asset.name.IndexOf("Vsb", StringComparison.OrdinalIgnoreCase) >= 0;
                sb.Append($"\n  - {path} (name={asset.name}{(isIncomplete ? " ※本セッション生成・不完全のため除外" : "")})");
                if (isIncomplete) continue;
                candidateCount++;
                chosen = asset;
            }

            if (candidateCount == 0)
                return Report("AssignUrpAssetFromProject", false,
                    $"割り当て候補の URP アセットが見つかりません（除外分含む全 {guids.Length} 件）:{sb}\n" +
                    "Editor の Create > Rendering > URP Asset (with Universal Renderer) で作成してから再実行してください。");

            if (candidateCount > 1)
                return Report("AssignUrpAssetFromProject", false,
                    $"URP アセット候補が複数あります（{candidateCount} 件）。どれを使うか曖昧なため割り当てません:{sb}");

            GraphicsSettings.defaultRenderPipeline = chosen;
            AssetDatabase.SaveAssets();

            var effective = QualitySettings.renderPipeline ?? GraphicsSettings.defaultRenderPipeline;
            return Report("AssignUrpAssetFromProject", true,
                $"URP アセット '{chosen!.name}' を GraphicsSettings.defaultRenderPipeline に割り当てました。" +
                $"現在の有効パイプライン型={(effective != null ? effective.GetType().Name : "null")}。" +
                $"候補一覧:{sb}\nPlayMode で currentRenderPipeline 非null と描画を確認してください。");
        }

        /// <summary>
        /// 本セッションでプログラム生成した不完全な URP アセット（<c>Assets/Settings/Vsb*</c>）を削除し、
        /// それが <see cref="GraphicsSettings.defaultRenderPipeline"/> に割り当たっていれば null に戻す。
        /// Edit モード専用。冪等。
        /// </summary>
        public static string CleanupGeneratedUrpAssets()
        {
            if (Application.isPlaying)
                return Report("CleanupGeneratedUrpAssets", false, "Edit モードで実行してください。");

            var sb = new StringBuilder();

            // 不完全アセットが既定パイプラインに割り当たっていれば外す。
            if (GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset cur
                && cur.name.IndexOf("Vsb", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                GraphicsSettings.defaultRenderPipeline = null;
                sb.Append("\n  defaultRenderPipeline を null（Built-in）に戻しました。");
            }

            string[] paths =
            {
                "Assets/Settings/VsbUniversalRenderPipelineAsset.asset",
                "Assets/Settings/VsbUniversalRenderer.asset",
            };
            foreach (var p in paths)
            {
                if (AssetDatabase.LoadAssetAtPath<ScriptableObject>(p) != null)
                {
                    var ok = AssetDatabase.DeleteAsset(p);
                    sb.Append($"\n  {(ok ? "削除" : "削除失敗")}: {p}");
                }
                else
                {
                    sb.Append($"\n  既に無し: {p}");
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return Report("CleanupGeneratedUrpAssets", true, $"後始末完了:{sb}");
        }
    }
}
