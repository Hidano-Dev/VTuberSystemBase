# 実装計画: avatar-mocap-facial-integration

> 本計画は MVP 先行の段階構成に厳密に従う。
> **Phase 1（タスク 1〜8）= avatar 表示 + VMC mocap 駆動 + SlotMotionDriver**。FacialControl を一切参照せずコンパイル/再生が成立する。タスク 8 が Phase 1 の目視検証チェックポイント。
> **Phase 2（タスク 9〜11）= FacialControllerAttacher による演者自走表情**。Phase 1 完了後にのみ着手する。タスク 11 が Phase 2 の目視検証チェックポイント。
> Phase 境界はタスク番号帯と見出しで明示する。`(P)` は同一親内で並行実行可能なタスクに付与する（並行モード）。

---

## Phase 1: avatar 表示 + VMC mocap 駆動（MVP）

- [ ] 1. パッケージ取込と依存解決（Foundation）
- [ ] 1.1 manifest.json に mocap-vmc / FacialControl 4 件を git+ssh で登録し VContainer を解決する
  - mocap-vmc を `#main` で、FacialControl コア + `.lipsync` + `.osc` + `.inputsystem` の 4 件を `#feature/hidano/generate-prototype` 派生ブランチ固定で dependencies に追記する
  - VContainer を明示 dependency `jp.hadashikick.vcontainer 1.16.6` として追記し、OpenUPM scopedRegistry の scopes に `jp.hadashikick` を 1 行追加する
  - git+ssh は SSH 鍵前提のため、解決失敗時は SSH 鍵設定・派生ブランチ存在・OpenUPM scope を確認する手順を注記する（Resolve エラー時の切り分け）
  - 観測可能な完了状態: Unity Package Manager が 5 件 + VContainer をコンパイルエラーなしで解決し、Package Manager ウィンドウに全パッケージが表示される
  - _Requirements: 1.1, 1.2, 1.3, 1.4, 1.5_

- [ ] 2. AMFI 新規パッケージ雛形の作成（Foundation）
- [ ] 2.1 AMFI パッケージの package.json と asmdef 一式を作成する（facial 非参照構成）
  - 新規 embedded パッケージ `com.hidano.vtuber-system-base.avatar-mocap-facial-integration` を作成し、package.json に rac-main-output-adapter / RAC core / Builtin / Motion / mocap-vmc への依存を宣言する
  - Phase 1 用 Runtime asmdef を VTuberSystemBase.* 命名規約に従って作成し、FacialControl を参照しない（RAC core / Builtin / Motion / mocap-vmc / 既存 Contracts のみ参照）
  - Editor asmdef・EditMode/PlayMode テスト asmdef を作成し、既存 spec パッケージのテスト構造規約に倣う
  - 観測可能な完了状態: Phase 1 asmdef 群が FacialControl 未導入でもコンパイル成功し、AMFI が compilation domain に現れる
  - _Requirements: 7.1, 7.2_
- [ ] 2.2 Facial.asmdef を defineConstraints で Phase 1 未コンパイル化する
  - `Facial/` ディレクトリと Facial asmdef を配置し、`defineConstraints: ["AMFI_FACIAL"]` を設定して Phase 1 ではコンパイル対象外にする
  - Facial asmdef は Runtime asmdef + FacialControl Adapters を参照する子 asmdef として宣言する（Phase 2 で Scripting Define `AMFI_FACIAL` を立てて有効化）
  - 観測可能な完了状態: `AMFI_FACIAL` 未定義状態で Facial 配下が未コンパイルとなり、Phase 1 のビルドに影響しない
  - _Requirements: 7.1, 7.2, 7.3_
  - _Depends: 2.1_

- [ ] 3. AvatarCatalog データ層（Foundation）
- [ ] 3.1 AvatarCatalog SO とエントリ型を作成し OnValidate バリデーションを実装する
  - `[CreateAssetMenu]` 付き ScriptableObject として AvatarCatalog を作成し、エントリ一覧（AvatarKey / DisplayName / AvatarPrefab）を SerializeField で保持する
  - FacialProfile 参照は Phase 1 の facial 非依存を維持するため `UnityEngine.Object` 弱型 SerializeField として保持する（Facial asmdef 側でキャスト解決）
  - OnValidate で avatarKey の重複と AvatarPrefab null を検出して警告ログを出す
  - 観測可能な完了状態: Inspector で重複 avatarKey または prefab 未設定エントリを入力すると OnValidate 警告が表示され、有効なカタログが SO アセットとして保存できる
  - _Requirements: 2.1, 2.4, 5.1_
  - _Boundary: AvatarCatalog_

- [ ] 4. Addressables 非依存のアバター解決とスキーマ（Core）
- [ ] 4.1 (P) CatalogAvatarKeyResolver を実装する
  - AvatarCatalog を参照し、命中時に BuiltinAvatarProviderConfig を動的生成して Builtin typeId の AvatarProviderDescriptor を返す IAvatarKeyResolver を実装する
  - 未命中時は null を返し、解決できない旨を診断ログに記録する
  - AvatarKeys 列挙と OnAvatarKeysChanged を実装し、Addressables 型を一切参照しないことを保証する
  - 観測可能な完了状態: EditMode テストで、命中 key は avatarPrefab 設定済み Builtin descriptor を返し、未命中 key は null + 診断ログとなることを検証する
  - _Requirements: 2.1, 2.4, 2.5, 2.6_
  - _Boundary: CatalogAvatarKeyResolver_
  - _Depends: 3.1_
- [ ] 4.2 (P) InMemoryAvatarSchemaProvider を実装する
  - 非表情設定中心の AvatarSettingsSchemaPayload を同期返却する IAvatarSchemaProvider を実装する（当面は空スキーマ、未知 key は null）
  - 観測可能な完了状態: EditMode テストで、既知 key は非 null スキーマ、未知 key は null を返すことを検証する
  - _Requirements: 2.2_
  - _Boundary: InMemoryAvatarSchemaProvider_
  - _Depends: 3.1_

- [ ] 5. VMC モーキャップ設定の配線（Core）
- [ ] 5.1 (P) VmcMoCapSourceConfigFactory を実装する
  - typeId="VMC" の MoCapSourceDescriptor（VMCMoCapSourceConfig: port=39539 / bindAddress="0.0.0.0"）を slot 単位で構築する IMoCapSourceConfigFactory を実装する
  - VMC Factory の自己登録は mocap-vmc 側が担うため AMFI では登録せず、解決成否で確認する方針をコメントに明記する
  - 観測可能な完了状態: EditMode テストで、Build 結果が SourceTypeId="VMC" かつ Config が VMCMoCapSourceConfig（port/bindAddress 既定値）であることを検証する
  - _Requirements: 3.1_
  - _Boundary: VmcMoCapSourceConfigFactory_
- [ ] 5.2* (P) MoCapRegistryProbe（任意）で登録済み typeId 一覧をログ出力する
  - RegistryLocator の登録済み typeId 一覧を起動時 1 回ログ出力し、VMC 自己登録の検証補助とする
  - 実機検証で SlotManager の Resolve 成否ログで十分と判断できる場合は省略可（その旨を注記）
  - 観測可能な完了状態: 起動時 Console に登録済み typeId 一覧が出力され、"VMC" を含むことを確認できる
  - _Requirements: 3.2_
  - _Boundary: MoCapRegistryProbe_

- [ ] 6. 覗き窓と全身モーション適用ループ（Core, rac-main-output-adapter への最小追加）
- [ ] 6.1 RacMainOutputAdapterBootstrapper に SlotManager 公開プロパティを追加する
  - read-only プロパティ `public SlotManager SlotManager => _slotManager;` の **1 本のみ**を追加し、既存ロジックを変更しない
  - `OnSlotStateChanged` 名のプロパティは追加しない（既存 private メソッドと CS0102 衝突するため）。購読者は `SlotManager.OnSlotStateChanged` 経由で参照することをコメントに明記する
  - 観測可能な完了状態: Initialize 後に Bootstrapper.SlotManager が非 null を返し、Shutdown 後に null となる（既存テストが緑のまま）
  - _Requirements: 4.1_
  - _Boundary: RacMainOutputAdapterBootstrapper_
- [ ] 6.2 SlotMotionDriver を実装する（LateUpdate 駆動ループ）
  - rac-main-output-adapter 内に MonoBehaviour として SlotMotionDriver を新設し、Runtime asmdef に RealtimeAvatarController.Motion 参照を追加する
  - Attach(SlotManager) で SlotManager.OnSlotStateChanged を購読し、Active で TryGetSlotResources → MotionCache.SetSource + HumanoidMotionApplier.SetAvatar の per-slot pipeline を構築する
  - 非 Humanoid avatar（SetAvatar が InvalidOperationException）は pipeline を作らずスキップ + 診断ログとし、他 Active slot の適用を継続する
  - source 未解決 slot はスキップし、LateUpdate で各 Active pipeline を SlotManager.ApplyWithFallback で毎フレーム駆動する
  - Disposed で pipeline teardown（Cache/Applier Dispose）し、Detach で全 pipeline を破棄する。VMC 無送信時は LatestFrame 前フレーム保持 + HoldLastPose で直前姿勢を維持する
  - 観測可能な完了状態: PlayMode テストで、Stub source + Humanoid avatar の slot が Active で pipeline 構築・LateUpdate で ApplyWithFallback 呼出・Disposed で teardown され、非 Humanoid slot は pipeline 未構築で他 slot が継続する
  - _Requirements: 4.1, 4.2, 4.3, 4.4, 4.5, 3.3, 3.4_
  - _Boundary: SlotMotionDriver_
  - _Depends: 6.1_

- [ ] 7. AMFI Composition Root と IntegratedDemo 統合（Integration）
- [ ] 7.1 AmfiCompositionRoot を実装する（Bootstrapper 生成・OverrideServices・Initialize・Driver 配線）
  - MonoBehaviour として、RacMainOutputAdapterBootstrapper を直接生成し、自前 resolver / schema provider / mocap factory を OverrideServices に渡してから Initialize() を呼ぶ
  - 公開された SlotManager を取得し SlotMotionDriver.Attach(slotManager) で駆動ループを起動する（同一 SlotManager 共有を保証）
  - 再生サイクル堅牢化（MEMORY: ui_shell_addressables_nonfatal 整合）: OnDestroy / ExitingPlayMode で Bootstrapper.Shutdown + Driver.Detach を確実に行い、DisableDomainReload 下の static 残留と 2 回目再生での二重登録を回避する（CoreIpcRuntime.Current 生死判定パターンに倣う）
  - 観測可能な完了状態: PlayMode テストで、Initialize 後に adapter が自前 resolver/mocapFactory を採用（既定 Addressables 不使用）し、3 連続 Play で Shutdown→再 Initialize が安定する
  - _Requirements: 2.3, 7.1, 8.1, 8.5_
  - _Boundary: AmfiCompositionRoot_
  - _Depends: 4.1, 4.2, 5.1, 6.2_
- [ ] 7.2 IntegratedDemo に AMFI と既存 Host の排他起動分岐を実装する
  - IntegratedDemo の RAC 生成箇所を、AMFI AmfiCompositionRoot 起動と既存 RacMainOutputAdapterHost 起動の **二者択一分岐**に置き換え、両者同時起動を禁止する（SlotManager 二重生成回避）
  - dispatcher/sceneRoots/messageSink/bus を AMFI Composition Root に注入し、AMFI 未配置時は従来 Host 経路へ degrade する安全弁を残す
  - 観測可能な完了状態: Play 時に AMFI と Host のどちらか一方のみが起動し SlotManager が 1 つだけ生成されることをログ/テストで確認できる
  - _Requirements: 8.1, 8.5_
  - _Depends: 7.1_

- [ ] 8. Phase 1 チェックポイント: 目視検証（Validation）
- [ ] 8.1 検証用 FBX アバターを準備し AvatarCatalog に登録する
  - ユーザー所有の Humanoid rig（Animator.isHuman==true）FBX を通常 Assets 配下に配置し、AvatarCatalog に avatarKey/DisplayName/AvatarPrefab を登録する（Phase 1 では FacialProfile は未設定で可）
  - 観測可能な完了状態: Character タブのアバター一覧に登録 avatarKey が表示され、スロットに割当可能になる
  - _Requirements: 2.1, 2.4, 8.2_
  - _Depends: 3.1, 7.2_
- [ ] 8.2 Phase 1 目視検証シナリオを Game/OBS ビューで確認する
  - Play → Character タブで FBX をスロットに割当 → アバターが表示される（Spout/URP/RT 既存出力経路に乗る）ことを確認する
  - VMC 送信元（VSeeFace 等）で全身がモーションに追従して動くことを Game/OBS ビューで確認する
  - VMC 送信停止でアバターが直前姿勢を保持し、クラッシュ/エラー停止しないことを確認する
  - 観測可能な完了状態: Unity 6000.3.10f1 / URP 17.3.0 の Sample MainDemo で「表示→全身駆動→停止で姿勢保持」が目視成立する（FacialControl 非依存のまま）
  - _Requirements: 3.3, 3.4, 4.3, 7.2, 8.2, 8.3, 8.5_
  - _Depends: 8.1_

---

## Phase 2: FacialControl による演者自走表情

> Phase 2 は Phase 1（タスク 8 完了）後にのみ着手する。Scripting Define `AMFI_FACIAL` を立てて Facial asmdef を有効化する。

- [ ] 9. FacialControllerAttacher の実装（Core, Facial.asmdef）
- [ ] 9.1 Scripting Define AMFI_FACIAL を有効化し Facial asmdef をコンパイル対象にする
  - Player Settings の Scripting Define に `AMFI_FACIAL` を追加し、Facial asmdef（FacialControl Adapters 参照）をコンパイル可能にする
  - README 参照用に、Phase 分離（Phase 1=未定義 / Phase 2=定義）の切替手順を確認する
  - 観測可能な完了状態: `AMFI_FACIAL` 定義下で Facial 配下がコンパイル成功し、Phase 1 機能が引き続き動作する
  - _Requirements: 7.3_
  - _Depends: 2.2, 8.2_
- [ ] 9.2 FacialControllerAttacher を実装する（実行時 Add + Profile + Initialize、IsInitialized ガード）
  - SlotManager.OnSlotStateChanged の Active を購読し、TryGetSlotResources で avatar を取得、AvatarCatalog の FacialProfile（弱型 → `as FacialCharacterProfileSO`）を解決する
  - avatar へ FacialController を実行時 Add し CharacterSO に Profile を割当てた上で、`fc.IsInitialized` を確認してから Initialize() を呼ぶ（二重 Init 回避）
  - avatar は slot Disposed で破棄され再 Active で BuiltinAvatarProvider が新規 Instantiate する（使い回さない）前提を実装コメントに明記する
  - Profile 未割当 or avatar に BlendShape（SkinnedMeshRenderer）無しの場合は Add/Initialize をスキップし診断ログを出す。Activate/Deactivate は呼ばず演者自走に委ねる（RAC IFacialController/descriptor は不使用、LifetimeScope は FacialController 内製）
  - slot Disposed で追跡辞書を掃除し、Detach で全結線を解除する
  - 観測可能な完了状態: PlayMode テストで、Active 時に FacialController が Add + CharacterSO 設定 + IsInitialized==true となり、Profile 欠如 slot は skip + ログとなる
  - _Requirements: 5.1, 5.2, 5.3, 5.4, 5.5, 5.6_
  - _Boundary: FacialControllerAttacher_
  - _Depends: 9.1_

- [ ] 10. Composition Root への Attacher 接続（Integration）
- [ ] 10.1 AmfiCompositionRoot に Facial 有効化分岐と Attacher 配線を追加する
  - `_enableFacial` フラグ有効時のみ FacialControllerAttacher.Attach(slotManager, catalog) を呼び、Phase 1 の avatar+motion を維持したまま表情結線を追加する
  - 再生サイクル teardown（OnDestroy/ExitingPlayMode）に Attacher.Detach を含め、Phase 1 同様の堅牢化を保つ
  - 観測可能な完了状態: `_enableFacial` ON で Active slot の avatar に FacialController が結線され、OFF では Phase 1 と同一挙動（facial 非結線）になる
  - _Requirements: 5.1, 7.3_
  - _Depends: 7.1, 9.2_

- [ ] 11. Phase 2 チェックポイント: 目視検証（Validation）
- [ ] 11.1 FacialCharacterProfileSO を準備し AvatarCatalog にひも付ける
  - BlendShape 付き FBX 用の FacialCharacterProfileSO を作成し、AdapterBindings（OSC ポート/ARKit/uLipSync デバイス）の既定値を設定する
  - AvatarCatalog の該当エントリに FacialProfile を割り当て、弱型保持の誤割当がないことを確認する
  - 観測可能な完了状態: 該当 avatarKey の slot Active で FacialController が Profile を読み込み IsInitialized==true になる
  - _Requirements: 5.1, 5.6_
  - _Depends: 10.1_
- [ ] 11.2 演者入力による表情切替を Game/OBS ビューで確認する
  - Phase 1 の表示+全身駆動を維持したまま、演者入力（OSC/ARKit・uLipSync・InputSystem）で表情が切り替わることを Game/OBS ビューで確認する（VTSB 操作なしで自走）
  - 観測可能な完了状態: Unity 6000.3.10f1 / URP 17.3.0 の Sample MainDemo で、演者入力に応じて avatar の表情が OBS/Game ビューに反映され、avatar+mocap 機能が維持される
  - _Requirements: 5.2, 5.3, 7.3, 8.4, 8.5_
  - _Depends: 11.1_

---

## セットアップ補助タスク

- [ ] 12. README の整備（Integration）
- [ ] 12.1 AMFI のセットアップ手順を README に記載する
  - git+ssh 取込（SSH 鍵前提・派生ブランチ）と VContainer/OpenUPM scope のセットアップ手順を記載する
  - AdapterBindings の既定値、AvatarCatalog 登録手順、Phase 分離の Scripting Define（AMFI_FACIAL）切替手順を記載する
  - 観測可能な完了状態: README に従って未構成環境から Phase 1/Phase 2 を再現できる手順が揃う
  - _Requirements: 1.1, 1.2, 1.3, 5.6, 7.3_
  - _Depends: 11.2_

---

## 要件カバレッジ確認

- Requirement 1（パッケージ取込）: 1.1
- Requirement 2（アバター解決）: 3.1, 4.1, 4.2, 7.1, 8.1
- Requirement 3（VMC 設定）: 5.1, 5.2, 6.2, 8.2
- Requirement 4（モーション適用ループ）: 6.1, 6.2, 8.2
- Requirement 5（表情駆動）: 3.1, 9.2, 10.1, 11.1, 11.2
- Requirement 6（表情 IPC ルーティング）: **本仕様スコープ外**（演者自走化により不要化。将来 Phase3 で任意追加。設計 §Requirements Traceability 参照）
- Requirement 7（段階導入）: 2.1, 2.2, 9.1, 10.1, 11.2, 12.1
- Requirement 8（出力結線・目視検証）: 7.1, 7.2, 8.1, 8.2, 11.2
