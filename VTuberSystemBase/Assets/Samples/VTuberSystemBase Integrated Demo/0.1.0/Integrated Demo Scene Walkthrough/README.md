# Integrated Demo — Sample Scene

`MainDemo.unity` / `IntegratedDemoSkinProfile.asset` / 4 つの UXML を 1 つの Import Sample にまとめたもの。Import 直後に PlayMode で UI / IPC / 3 adapter の起動を確認できる。Avatar や Stage の実 prefab を表示したい場合のみ、§3 の Addressables 設定が必要（任意）。

## 1. 前提

- Unity 6.3 (6000.3+)
- URP 17.x が有効化されている
- 以下 10 パッケージがプロジェクトに追加されている (`Packages/manifest.json`)：
  - `com.hidano.vtuber-system-base.core-ipc-foundation`
  - `com.hidano.vtuber-system-base.output-renderer-shell`
  - `com.hidano.vtuber-system-base.ui-toolkit-shell`
  - `com.hidano.vtuber-system-base.character-selection-tab`
  - `com.hidano.vtuber-system-base.stage-lighting-volume-tab`
  - `com.hidano.vtuber-system-base.camera-switcher-tab`
  - `com.hidano.vtuber-system-base.rac-main-output-adapter`
  - `com.hidano.vtuber-system-base.stage-lighting-volume-output-adapter`
  - `com.hidano.vtuber-system-base.camera-switcher-output-adapter`
  - `com.hidano.vtuber-system-base.integrated-demo` (本パッケージ)
- 外部依存：`com.hidano.realtimeavatarcontroller`, `com.hidano.scene-view-style-camera-controller`, `com.hidano.ucapi4unity`, `com.hidano.uosc`, `com.hidano.runtime-display-selector` (任意), `com.unity.addressables`, `com.unity.render-pipelines.universal`

## 2. Sample を Import する

1. `Window > Package Manager` を開く
2. `VTuberSystemBase Integrated Demo` を選択
3. Inspector の `Samples` セクションで **Integrated Demo Scene Walkthrough** の **Import** をクリック
4. `Assets/Samples/VTuberSystemBase Integrated Demo/<version>/Integrated Demo Scene Walkthrough/` に以下が展開される：
   ```
   ├ MainDemo.unity                                  (シーン本体)
   ├ README.md                                       (本ファイル)
   └ SkinProfile/
       ├ IntegratedDemoSkinProfile.asset             (4 UXML 参照済み)
       ├ IntegratedDemo_Root.uxml                    (tab bar + tab content + notification bar)
       ├ IntegratedDemo_CharacterTab.uxml            (vsb-char-tab__* 5 region)
       ├ IntegratedDemo_StageTab.uxml                (preview / preset / stage / light / volume)
       └ IntegratedDemo_CameraTab.uxml               (vsb-cam-tab__* 6 region)
   ```
5. `MainDemo.unity` を開く（`IntegratedDemoRoot` GameObject に `OutputSceneBootstrapper` + `IntegratedDemoBootstrap` が配線済み、`Config > Skin Profile` も自動 assign 済み）

> Spout 経路を使いたいときだけ `OutputSceneBootstrapper` の Inspector で `Spout Sender Name = VsbMainOutput` を設定する。

## 3. （任意）Addressables Group の構成

**Sample の動作確認だけなら本節は丸ごとスキップして OK**。UI shell / 3 adapter / IPC は Addressables Settings が未作成でも全部起動する（Console に `RuntimeData is null` / `DefaultThumbnail.Probe failed` 系のエラーは出るが、`DefaultThumbnailValidator` を含めて全部 graceful に skip する設計なので機能影響なし）。

下記は **Avatar や Stage の実 prefab を IPC 経由で表示したい場合のみ**必要なセットアップ：

1. `Window > Asset Management > Addressables > Groups` で Groups ウィンドウを開き、初回は **Create Addressables Settings** ボタンを押す
2. Groups ウィンドウで以下の Group / Address を登録：

| Group | Address | 内容 | 連携先 |
|---|---|---|---|
| `Avatars` | `avatars/sample-avatar` | VRM Avatar Prefab | rac-main-output-adapter |
| `Avatars` (任意) | `avatars/sample-avatar/schema` | Avatar 設定 schema JSON | character-selection-tab |
| `Stages` | `stages/sample-stage` | Stage Prefab (Cube + Plane でも可) | stage-lighting-volume-output-adapter |
| `Thumbnails` (任意) | `vtuber-system-base/character/default-avatar-thumbnail` | 64×64 程度の Texture2D | character-selection-tab フォールバック |

3. Groups ウィンドウのツールバーで `Build > New Build > Default Build Script`

## 4. PlayMode 起動

`MainDemo.unity` を Play した直後に以下が出れば成功：

1. Console: `[CoreIpc.RuntimeBootstrap] CoreIpcRuntime initialization completed.`
2. Console: `[IntegratedDemoBootstrap] Awake wiring complete (PlayMode integration scaffold ready).`
3. Console: `OutputSceneBootstrapper: phase complete: ... -> Complete`
4. Console: `[RacMainOutputAdapterHost] Initialize complete` または相当
5. Console: `[CameraSwitcherOutputAdapter] Camera Switcher Output Adapter ready`
6. Console: `[StageLightingVolumeOutputAdapterBootstrapper] ready` 系
7. Console: `UiShellBootstrapper: shell running.`
8. Display 1 にタブバー + 3 タブ UI が出る (Character タブが初期 active)
9. Display 2+ にメイン出力（既定はカメラ + 既定ライトのみ。アバターやステージは IPC 経由で表示）

## 4-a. 既知のハマりどころ

`IntegratedDemoBootstrap` が以下を自動で吸収しているため、Inspector で特に何もしなくても動作します（ただし覚えておくと別シーンを自作するときに役立ちます）。

- **`Application.runInBackground = true`** を Awake で自動 ON。Game View にフォーカスが無いとフレームが止まる Unity の挙動を打ち消す（VTuber 配信用システムなので必須）。
- **`CoreIpcRuntime` の手動 Bootstrap**。`com.unity.test-framework` 同梱時は `UNITY_INCLUDE_TESTS` が常時立ち、`AutoBootstrapDisabler` が `RuntimeBootstrap.OnBeforeSceneLoad` を抑制してしまうため、Bootstrap 側で `RuntimeBootstrap.IsBootstrapped == false` 検出 → `Bootstrap()` を直接呼ぶ。
- **Adapter 起動順序**。`RAC` / `Camera` adapter は inactive な child GameObject で `AddComponent` → `Bus` / `Dispatcher` / `Roots` の inject 完了後に `SetActive(true)`。Awake で直接 AddComponent すると Start が OutputSceneBootstrapper.Start より早く走って依存 null abort してしまうため。

## 5. UI 抜きで動かしたい場合

`IntegratedDemoBootstrap` の `Config > Skin Profile` を **None に外す** と UI shell の起動を skip し、メイン出力 + 3 アダプタだけ立ち上がる。OSC 送信や IPC を別経路から流し込むデバッグに使える。

## 6. トラブルシュート

| 症状 | 原因 | 対処 |
| --- | --- | --- |
| Display 1 に何も出ない | SkinProfile 未 assign | `IntegratedDemoBootstrap > Config > Skin Profile` を再 assign |
| SkinProfile の UXML 参照が空欄 | Sample 展開時に UXML の fileID が解決できなかった | `IntegratedDemoSkinProfile.asset` を選択 → Inspector で 4 つの UXML を手動で再 assign |
| `MarkTabFailed: ... Q failed` | タブ用 UXML を編集して必須 region 名を削った | 対象タブの `*TabPanel.cs` / `ViewQueryHelpers.cs` の constants と element 名を一致させる |
| Console に `CoreIpcRuntime.Current is null` | RuntimeBootstrap.OnBeforeSceneLoad が走っていない（テスト時に `DisableAutoBootstrap` で抑止） | 本番経路では起きない |
| Camera adapter `OutputSceneBootstrapper not initialized yet; deferring` | OutputSceneBootstrapper.Start が完了する前に CameraAdapter.Awake が走った | `IntegratedDemoBootstrap` と `OutputSceneBootstrapper` は必ず**同一 GameObject**に置く（Sample 標準どおり） |
| Stage adapter `dependencies_missing` | OutputSceneBootstrapper.Dispatcher が null のまま polling 切れ | `IntegratedDemoBootstrap.Config > Adapter Startup Max Frames` を 120 などに増やす |
| `Addressables - Unable to load runtime data` / `RuntimeData is null` / `DefaultThumbnail.Probe failed key=...` / `InvalidKeyException` | Addressables Settings 未作成 | **無視可**（実害なし、機能は graceful に skip）。実 prefab を表示したい場合のみ §3 の設定を実施 |
| `VolumeOverrideHandler.start_failed: VolumeManager ... not initialized` | URP の VolumeManager が初フレーム前で未初期化 | 無視可。次フレーム以降の volume/override コマンドは正常動作する |
| `No Theme Style Sheet set to PanelSettings` | PanelSettings に Theme Style Sheet 未設定 | 機能影響なし。スタイルを統一したい場合は `Assets/UI Toolkit/UnityThemes/UnityDefaultRuntimeTheme.tss` を PanelSettings に手動 assign |

## 7. 完了判定

`docs/integration-plan.md` §8 (1)〜(5) を README で satisfy 確認のうえ、L7 手動受け入れテストを通せば本 Wave 3d は完了。
