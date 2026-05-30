# セッション引き継ぎノート

「Spout にメイン出力が出ない（OBS が真っ黒）」を根本原因まで掘り、URP 有効化＋RT 経路への本番改修で**ディスプレイ非依存の Spout 出力を成立させた**セッション。最終的に OBS で青空＋テストキューブの目視確認まで完了。

## ◯ 今回やったこと

- **根本原因特定**: プロジェクトは URP 前提（`DefaultCameraFactory` が出力カメラに `UniversalAdditionalCameraData` を明示付与、URP 17.3.0 + GlobalSettings + VolumeProfile 導入済み）なのに **URP RenderPipelineAsset 本体が未作成・未割当** → 実行時 Built-in にフォールバック → URP前提カメラが描画できず Spout 黒、と判明。
- **URP 有効化**: ユーザが menu で正規 URP アセット（`Assets/Settings/New Universal Render Pipeline Asset.asset` + Renderer）を生成 → `UiApiDebugHub.AssignUrpAssetFromProject()` で `GraphicsSettings.defaultRenderPipeline` に割当。RenderGraph エラー（`null resource index`）消滅、カメラが正常描画するようになった（Game ビューで青空＋キューブ確認）。
- **第2の罠を特定**: URP 経路の SpoutSender は `CaptureMethod.Camera`＋`CameraCaptureBridge`。これは「カメラが実描画したときだけ発火」するため、`targetDisplay=1`（Display2）+ Editor 単一ディスプレイだとカメラが描画されず Spout 黒。ユーザが Game ビューを Display 2 表示にした瞬間に Spout に絵が出て確定。
- **本番改修（RT 経路）**: `RuntimeDisplaySelectorRoutingService.Activate` を「出力カメラの `targetTexture` を専用 RenderTexture(既定1920x1080) に張り替え → RDS の `AssignRenderTextureToDisplay`（Texture モード）で Spout 送出」に変更。カメラが Display 表示状態に依存せず毎フレーム描画される＝**ディスプレイ非依存**。Editor でも standalone 単一画面でも出る。
- **テスト更新・全パス**: output-renderer-shell EditMode 78/78、PlayMode RDS routing 4/4。
- **検証用ツール追加**: `UiApiDebugHub.InjectSpoutTestContent()` / `RemoveSpoutTestContent()`（VtsApiDebug ボタン「Spout 検証」）。PlayMode 中に背景 Skybox＋テストキューブを一時注入。
- **OBS 目視確認完了**: Play → 注入 → OBS の `RuntimeDisplaySelector_Display_1` に青空＋黄キューブが、**Display 2 表示に切り替えず**映ることを確認。

## ◯ 決定事項

- **メイン出力 Spout は RT 経路（カメラ→専用RT→SpoutSender Texture モード）に統一**。camera-capture（CameraCaptureBridge）は Editor/standalone でディスプレイ表示依存になるため不採用。
- RT 解像度は `DisplayRoutingConfig.OutputResolution`（既定 1920x1080）。RT は `RuntimeDisplaySelectorRoutingService` が所有し Dispose/fallback/解像度変更時に解放。
- 検証は **ReadPixels ではなく uloop screenshot / OBS** で行う（URP RenderGraph 下では ReadPixels がカメラ描画 RT を正しく読めない）。
- URP アセットは **Editor の Create > Rendering > URP Asset で正規生成**する（プログラム生成は不可、下記）。
- 自動コミットフックが動作中（把握済み）。本作業は随時 auto-commit 済み。`Assets/Scenes/MainDemo.unity`（出力専用・SkinProfile=null）が auto-commit で削除されたが**復元不要**と確認済み。

## ◯ 捨てた選択肢と理由

- **URP アセットのプログラム生成**（`UniversalRenderPipelineAsset.Create` + 素の `UniversalRendererData`）→ 却下。Unity6/URP17 では RenderGraph 必須リソースを満たせず不完全になり `Render Graph Execution error: null resource index` でカメラが描画しなくなる。menu 生成が正解。
- **camera-capture のまま targetDisplay を 0 にする/Display2 を表示し続ける** → 却下。standalone 単一画面で破綻、Editor 運用も非現実的。RT 経路がディスプレイ非依存で確実。
- **「ReadPixels で黒だからカメラが描画していない」という判断** → 一時誤認。readback 自体は正常（GL.Clear 赤は読める）だが、URP RenderGraph のカメラ描画 RT は ReadPixels で空に見えることがある。スクショで青空＋キューブを確認して覆した。
- **何度も PlayMode 出入り＋compile を重ねた汚染セッションの読み** → 信用しない。`scene.name==""` 等の異常値はクラッシュ寸前の半壊状態由来だった。クリーンに Play し直して再確認するのが正。
- **SpoutSender を実行時に Camera→Texture へ手動切替** → 却下。KlakSpout は Texture 切替時に `CameraCaptureBridge` のアクションを外さず、残留アクションが画面/RTを黒くする。最初から Texture 経路（`AssignRenderTextureToDisplay`）で組む。

## ◯ ハマりどころ

- `Application.isPlaying=false`/Editor 未起動を見落として PlayMode 前提コードを空振り。Play 状態を毎回確認。
- 一連の強制レンダリング検証中に **Editor がクラッシュ**（再起動後はクリーンに再現確認した）。
- uloop `execute-dynamic-code` の長尺 C# は PowerShell 5.1 の二重引用符分割で失敗 → **node 直 argv 方式**（`process.execPath` で `cli.bundle.cjs` を `shell:false` + argv 配列起動、コードは temp ファイル経由で `--code` に単一要素渡し）で回避。
- dynamic code は `System.Type.GetType` / `Assembly.GetType` が Restricted で禁止 → concrete 型直参照（`typeof(Klak.Spout.SpoutSender)` 等）＋取得済みインスタンスへの reflection で回避。
- `UnityEngine.Object` は自動 using 解決の曖昧参照（CS0104）回避のため完全修飾必須。

## ◯ 学び

- **「使われていない実装は結線漏れを疑う」**が再び的中（URP 一式は導入済みでパイプラインアセットの割当だけ欠けていた）。
- **URP では targetTexture を持つカメラだけが Display 非依存で確実に描画される**。Spout/オフスクリーン出力は targetTexture 方式が堅牢。
- URP RenderGraph 下の検証は **目視（スクショ/OBS）が最も信頼できる**。ReadPixels は当てにならない。
- 長時間 PlayMode をいじり倒すとセッション状態が壊れる。**疑わしい結果はクリーンな再起動で再確認**。

## ◯ 次にやること（優先度順）

1. **【将来作業・当面スコープ外】Addressables コンテンツビルド**: アバター/ステージ catalog が空（`RuntimeData is null` / `Library/com.unity.addressables/aa/Windows/settings.json` 無効）。ただしこれは**設計上の想定内ログで graceful skip される**（Sample README §トラブルシュート「無視可・実害なし」）。Addressables ビルドが必要になるのは**実 avatar/stage prefab を IPC 経由で OBS に出す段階に入ったときのみ**。現フェーズ（Spout 出力経路の成立）では不要のため当面着手しない。
2. **出力カメラの clearFlags 既定**: 現状 `SolidColor` 黒。VTuber 用途では Skybox かクロマキー色が妥当か検討（`DefaultCameraFactory`）。
3. **`DisplayRoutingConfig.OutputResolution` の Inspector 公開**: 現状コード既定 1920x1080 のみ。`OutputSceneBootstrapper` に SerializeField を追加して可変にするか検討。
4. RT 経路の standalone 実機確認（物理出力なし＋OBS Spout で実運用想定）。

## ◯ 関連ファイル

### 本番（今回変更）
- `Packages/com.hidano.vtuber-system-base.output-renderer-shell/Runtime/Display/RuntimeDisplaySelectorRoutingService.cs`（RT 経路実装＋bridge に `AssignRenderTextureToDisplay` 追加＋RT ライフサイクル）
- `Packages/com.hidano.vtuber-system-base.output-renderer-shell/Runtime/Abstractions/DisplayRoutingConfig.cs`（`OutputResolution` 追加）
- `ProjectSettings/GraphicsSettings.asset`（defaultRenderPipeline = URP）
- `Assets/Settings/New Universal Render Pipeline Asset.asset`(+ Renderer)（ユーザ生成・割当）

### テスト（今回更新）
- `Packages/.../output-renderer-shell/Tests/EditMode/RuntimeDisplaySelectorRoutingServiceTests.cs`
- `Packages/.../output-renderer-shell/Tests/EditMode/Fakes/FakeRuntimeDisplaySelectorBridge.cs`

### Editor ツール（VtsApiDebug、Editor 専用）
- `Assets/DevTools/UiApiDebug/UiApiDebugHub.Urp.cs`（`AssignUrpAssetFromProject` / `DumpRenderPipeline` / `InjectSpoutTestContent` / `RemoveSpoutTestContent`）
- `Assets/DevTools/UiApiDebug/UiApiDebugWindow.cs`（URP 設定・Spout 検証ボタン）
- `Assets/DevTools/UiApiDebug/VtsApiDebug.asmdef`（`Unity.RenderPipelines.Universal.Runtime` 参照追加）

### 環境
- Unity `6000.3.10f1` / URP 17.3.0 / Klak Spout 2.0.6 / RuntimeDisplaySelector 0.1.1。
- 検証シーン: `Assets/Samples/VTuberSystemBase Integrated Demo/0.1.0/Integrated Demo Scene Walkthrough/MainDemo.unity`（統合版。出力専用 `Assets/Scenes/MainDemo.unity` は削除済み）。
- 検証手順: Play → `Tools/Hidano/VTuberSystem/Debug/VTS API Debug` → 「Spout 検証 > テスト用コンテンツ注入」→ OBS の Spout Source `RuntimeDisplaySelector_Display_1` を確認（Display2 表示への切替不要）。
