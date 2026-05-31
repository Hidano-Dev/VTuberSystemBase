# Requirements Document

## Project Description (Input)
VTuberSystemBase に、実アバターをランタイム表示してモーキャップと表情制御を動かす統合機能を追加する。手触り確認を最優先しつつ、Kiro 3フェーズ(requirements→design→tasks)で正式に進める。

【ゴール】
Addressables を使わず、RAC のビルトインアバター(FBX prefab 直参照)をスロットに組み込み、VMC モーキャップで全身を駆動し、FacialControl フレームワークで表情を制御できるようにする。既存の Character タブ / IPC / 出力(Spout)経路に結線する。

【構成パッケージ(いずれも既存・完成済み、VTSB に未配線)】
- アバター: com.hidano.realtimeavatarcontroller 0.2.0 の BuiltinAvatarProvider (prefab を Instantiate、Addressables 不要)。VTSB 導入済み。
- Mocap: com.hidano.realtimeavatarcontroller.mocap-vmc 0.1.0 (typeId="VMC"、属性ベース自己登録、uOSC 経由で /VMC/Ext/Bone/Pos・/VMC/Ext/Root/Pos を受信、HumanoidMotionFrame を発行)。VTSB 未導入。
- 表情: com.hidano.facialcontrol 0.1.0-preview.2 (+ 必要に応じ .osc/.inputsystem/.lipsync)。avatar GameObject に FacialController(PlayableGraph) を Add し FacialCharacterProfileSO を結線、Activate(expression)/Deactivate(expression) で駆動。VContainer(jp.hadashikick.vcontainer 1.16.6, OpenUPM) 依存。VTSB 未導入。

【パッケージ取込方針(確定)】
manifest.json に git+ssh で参照する。
- mocap-vmc: git@github.com:Hidano-Dev/RealtimeAvatarController.git?path=RealtimeAvatarController/Packages/com.hidano.realtimeavatarcontroller.mocap-vmc#main
- facialcontrol(コア+サブ): git@github.com:NHidano/FacialControl.git?path=FacialControl/Packages/<pkg>#feature/hidano/generate-prototype  (最新は main ではなくこの派生ブランチ)
- VContainer 用 OpenUPM scopedRegistry(scope: jp.hadashikick.vcontainer) を追加。

【VTSB 側で新規に作る配線】
1. Addressables 非依存のアバター解決: 自前 IAvatarKeyResolver(SerializeField のアバターカタログ→BuiltinAvatarProviderConfig.avatarPrefab を返す) と インメモリ IAvatarSchemaProvider を実装し、RacMainOutputAdapterBootstrapper.OverrideServices で差し込む(既定は AddressablesAvatarKeyResolver/AddressablesAvatarSchemaProvider)。
2. VMC Mocap 配線: IMoCapSourceConfigFactory を VMC descriptor(typeId="VMC", VMCMoCapSourceConfig) を返す実装に差し替え。VMCMoCapSourceFactory の自己登録を確認。
3. モーション適用ループ(重要・現状欠落): RAC の SlotManager は解決した IMoCapSource のフレームをアバターに適用しない(TryGetSlotResources で上位に委譲)。VTSB 側に、Active スロットごとに TryGetSlotResources→HumanoidMotionApplier を毎フレーム駆動する MonoBehaviour/サービスを新設する。
4. 表情(FacialControl)配線: スロットの avatar prefab に FacialControl の FacialController を Add(または prefab に内蔵)し FacialCharacterProfileSO を結線。Character タブの表情操作(IPC 経由の slot settings/command)→ Activate/Deactivate へルーティングする。RAC の IFacialController/facialControllerDescriptor は使わない(FacialControl は RAC と独立駆動するため RAC 本体改修は不要)。

【制約・注意】
- FacialControl は現状 FBX 前提(VRM は今後対応予定)。検証は BlendShape 付き FBX アバターで行う(ユーザー所有の FBX を使用)。
- FacialControl は VContainer 依存・per-FacialController LifetimeScope を張る。Adapter Binding は [FacialAdapterBinding] 属性で Inspector 自動列挙。
- RAC SlotManager(0.2.0) は表情/リップシンク descriptor を解決しないことを確認済み。
- 出力(Spout/URP/RT 経路)・Character タブ・IPC 基盤は既存実装を流用。
- 環境: Unity 6000.3.10f1 / URP 17.3.0。

【検証シナリオ】
Play → Character タブで FBX アバターをスロットに割当 → アバターが表示される → VMC 送信(例: VSeeFace 等)で全身が動く → 表情UIで表情が切り替わる、を OBS/Game ビューで目視確認。手触り優先で avatar+mocap を先に通し表情を後段に回す段階導入を許容する。

## Introduction
本仕様は、VTuberSystemBase(VTSB) に実アバターをランタイム表示し、VMC モーキャップで全身を、FacialControl フレームワークで表情を駆動して、既存の Character タブ / IPC / Spout 出力経路に結線するための統合機能を定義する。アバター解決は Addressables に依存せず、RAC(com.hidano.realtimeavatarcontroller 0.2.0) の BuiltinAvatarProvider で FBX prefab を直接 Instantiate する。RAC SlotManager(0.2.0) が解決済みモーキャップフレームをアバターへ適用しない既知ギャップを VTSB 側のモーション適用ループで補う。手触り確認を最優先とし、avatar+mocap を先に通して facial を後段に回す段階導入を許容する。

本仕様で言う「VTSB 統合レイヤー(VTSB Integration Layer)」とは、上記の配線(アバター解決オーバーライド、VMC mocap 設定差し替え、モーション適用ループ、表情ルーティング)を担う VTSB 側の新規実装一式を指す。

> 注記(設計フェーズで確定する事項): 本仕様には、過剰な決め打ちを避けるため設計フェーズで確定すべき未決論点が含まれる。各要件に注記で明示するほか、本書末尾の「設計フェーズで確定する事項」に集約する。

## Boundary Context
- **In scope**:
  - 構成パッケージ(mocap-vmc / facialcontrol + 必要なサブパッケージ / VContainer)の manifest.json への git+ssh および OpenUPM scopedRegistry による取込。
  - Addressables 非依存のアバター解決(自前 IAvatarKeyResolver / インメモリ IAvatarSchemaProvider の OverrideServices 差し込み)。
  - VMC モーキャップの設定配線(IMoCapSourceConfigFactory 差し替え)と全身モーション適用ループの新設。
  - FacialControl による表情駆動と Character タブ表情操作(IPC 経由)の Activate/Deactivate ルーティング。
  - Play モードでの目視検証シナリオ(アバター表示→VMC で全身駆動→表情切替)を成立させること。
- **Out of scope**:
  - RAC 本体(com.hidano.realtimeavatarcontroller)の改修。FacialControl は RAC と独立駆動するため RAC 本体改修は不要。
  - RAC の IFacialController / facialControllerDescriptor の利用(0.2.0 で未消費のデッドコードのため使わない)。
  - VRM アバター対応(FacialControl が現状 FBX 前提のため今後対応)。
  - Spout/URP/RT 出力経路・Character タブ UI 基盤・IPC 基盤そのものの新規実装(既存実装を流用する)。
  - Addressables を用いたアバター配信。
- **Adjacent expectations**:
  - 既存の Character タブ / IPC / Spout 出力は現行仕様のまま維持され、本統合はその拡張点(スロット割当・表情操作)に結線する。
  - 検証は BlendShape 付き FBX アバター(ユーザー所有)で行う。

## Requirements

### Requirement 1: 構成パッケージの取込と依存解決
**Objective:** VTuberSystemBase の開発者として、モーキャップ・表情制御パッケージとその依存を再現可能な形で取り込みたい。これにより、統合機能をクリーンな環境でも安定してビルド・再生できる。

#### Acceptance Criteria
1. The VTSB Integration Layer shall manifest.json に mocap-vmc を git+ssh 参照(git@github.com:Hidano-Dev/RealtimeAvatarController.git?path=RealtimeAvatarController/Packages/com.hidano.realtimeavatarcontroller.mocap-vmc#main)で登録する。
2. The VTSB Integration Layer shall manifest.json に FacialControl コアおよび本仕様で導入対象とするサブパッケージを git+ssh 参照(git@github.com:NHidano/FacialControl.git?path=FacialControl/Packages/<pkg>#feature/hidano/generate-prototype)で登録する。
3. The VTSB Integration Layer shall VContainer(jp.hadashikick.vcontainer 1.16.6)を解決するため OpenUPM scopedRegistry(scope: jp.hadashikick.vcontainer)を manifest.json に追加する。
4. When Unity Package Manager がパッケージを解決したとき, the VTSB Integration Layer shall mocap-vmc・FacialControl・VContainer をコンパイルエラーなしで取り込んだ状態にする。
5. If 参照ブランチが main など指定外を指している場合, then the VTSB Integration Layer shall FacialControl を派生ブランチ(feature/hidano/generate-prototype)に固定して取り込む。

> 注記: FacialControl のどのサブパッケージ(.osc / .inputsystem / .lipsync)を今回導入するかは設計フェーズで確定する(Requirement 6 と連動)。

### Requirement 2: Addressables 非依存のアバター解決
**Objective:** VTuberSystemBase の利用者として、Addressables を構成せずに FBX prefab のアバターをスロットへ割り当てたい。これにより、配信基盤なしで実アバターを即座に表示して手触りを確認できる。

#### Acceptance Criteria
1. The VTSB Integration Layer shall SerializeField のアバターカタログを参照して BuiltinAvatarProviderConfig.avatarPrefab を返す自前 IAvatarKeyResolver を提供する。
2. The VTSB Integration Layer shall アバタースキーマをインメモリで提供する自前 IAvatarSchemaProvider を提供する。
3. When RacMainOutputAdapterBootstrapper が初期化されるとき, the VTSB Integration Layer shall OverrideServices で既定の AddressablesAvatarKeyResolver / AddressablesAvatarSchemaProvider を自前実装に差し替える。
4. When Character タブで FBX アバターがスロットに割り当てられたとき, the RAC BuiltinAvatarProvider shall 対応する FBX prefab を Instantiate してシーンに表示する。
5. If 指定されたアバターキーがカタログに存在しない場合, then the VTSB Integration Layer shall アバターを生成せず、解決できない旨を診断ログに記録する。
6. The VTSB Integration Layer shall アバター解決経路で Addressables への依存を持たない。

### Requirement 3: VMC モーキャップ設定の配線
**Objective:** VTuberSystemBase の利用者として、VMC プロトコルのモーキャップ送信をアバターのモーションソースとして構成したい。これにより、VSeeFace などの外部送信元から全身モーションを受け取れる。

#### Acceptance Criteria
1. The VTSB Integration Layer shall typeId="VMC" の descriptor(VMCMoCapSourceConfig を含む)を返す IMoCapSourceConfigFactory 実装を提供し、既定実装に差し替える。
2. When 統合機能が初期化されるとき, the VTSB Integration Layer shall VMCMoCapSourceFactory が typeId="VMC" で自己登録済みであることを確認できる状態にする。
3. When VMC モーキャップソースが解決されたとき, the mocap-vmc パッケージ shall uOSC 経由で /VMC/Ext/Bone/Pos および /VMC/Ext/Root/Pos を受信して HumanoidMotionFrame を発行する。
4. If VMC データ受信が無い、または送信元が未接続の場合, then the VTSB Integration Layer shall アバターを直前の姿勢に保持し、クラッシュやエラー停止を起こさない。

### Requirement 4: 全身モーション適用ループ(既知ギャップの補完)
**Objective:** VTuberSystemBase の開発者として、解決済み VMC フレームをアバターへ毎フレーム適用したい。これにより、RAC SlotManager(0.2.0) がフレームを適用しない既知ギャップを補い、全身が実際に動くようにする。

#### Acceptance Criteria
1. The VTSB Integration Layer shall Active なスロットごとに TryGetSlotResources で IMoCapSource とアバターリソースを取得し、HumanoidMotionApplier を毎フレーム駆動するサービス(MonoBehaviour 等)を提供する。
2. While 1つ以上のスロットが Active であるとき, the VTSB Integration Layer shall 各 Active スロットの最新 HumanoidMotionFrame をフレーム毎に対応アバターへ適用する。
3. When VMC 送信元(例: VSeeFace)が全身モーションを送信したとき, the VTSB Integration Layer shall Game/OBS ビューでアバターの全身がそのモーションに追従して動く状態にする。
4. If あるスロットで TryGetSlotResources が IMoCapSource を解決できない場合, then the VTSB Integration Layer shall そのスロットの適用処理をスキップし、他の Active スロットの適用を継続する。
5. While スロットが非 Active になったとき, the VTSB Integration Layer shall そのスロットに対するモーション適用を停止する。

### Requirement 5: 表情制御(FacialControl)の駆動
**Objective:** VTuberSystemBase の利用者として、FacialControl フレームワークでアバターの表情を切り替えたい。これにより、BlendShape 付き FBX アバターで表情演出を行える。

#### Acceptance Criteria
1. The VTSB Integration Layer shall スロットのアバターに FacialControl の FacialController(PlayableGraph)を結線し、FacialCharacterProfileSO を割り当てる。
2. When 表情のアクティブ化が要求されたとき, the VTSB Integration Layer shall 対象 FacialController の Activate(expression) を呼び出して該当表情を適用する。
3. When 表情の解除が要求されたとき, the VTSB Integration Layer shall 対象 FacialController の Deactivate(expression) を呼び出して該当表情を解除する。
4. The VTSB Integration Layer shall RAC の IFacialController / facialControllerDescriptor を使用せず、FacialControl を RAC と独立して駆動する(RAC 本体は改修しない)。
5. While FacialController が有効なとき, the VTSB Integration Layer shall per-FacialController LifetimeScope(VContainer)を確立して FacialControl の依存を解決する。
6. If 対象アバターが BlendShape を持たない、または FacialCharacterProfileSO が未割り当ての場合, then the VTSB Integration Layer shall 表情を適用せず、その旨を診断ログに記録する。

> 注記(確定): 表情は **演者自走**。VTSB は avatar に FacialController を**実行時 Add** し FacialCharacterProfileSO を割り当てるところまでを担い、`Activate/Deactivate` の発火自体は FacialControl の入力バインディング(OSC/ARKit・uLipSync・InputSystem)が行う。VTSB から IPC 経由で Activate/Deactivate は呼ばない(AC2/AC3 は FacialControl 内部挙動として有効)。LifetimeScope(VContainer)は FacialController が内製するため VTSB 側で明示構築しない。

### Requirement 6: Character タブ表情操作のルーティング 〔スコープ外・将来 Phase3〕
> **確定により本仕様スコープ外**: 「表情の制御主体＝演者自走」決定（上記 discovery 追補）により、表情を IPC(Character タブ)経由で駆動する本要件は本仕様では実装しない。FacialControl が自前バインディングで自走するため不要。オペレーターが UI から表情を上書きしたい需要が出た場合に、将来 Phase3 で settings 経路(slot/{id}/settings/{key} → IAvatarSettingsAdapter)として追加する余地のみ残す。以下の AC は将来参照用に保持する。

**Objective:** VTuberSystemBase の利用者として、既存 Character タブの表情操作で表情を切り替えたい。これにより、追加 UI を覚えずに既存ワークフロー上で表情演出できる。

#### Acceptance Criteria
1. When Character タブの表情操作が IPC 経由(slot settings/command)で受信されたとき, the VTSB Integration Layer shall その操作を該当スロットの FacialController の Activate/Deactivate にルーティングする。
2. When 表情切替操作が処理されたとき, the VTSB Integration Layer shall Game/OBS ビューでアバターの表情が切り替わる状態にする。
3. The VTSB Integration Layer shall 既存の Character タブ / IPC 基盤を改修せず、その拡張点に結線する。
4. If 指定された表情名が FacialCharacterProfileSO に存在しない場合, then the VTSB Integration Layer shall 表情を変更せず、未知の表情である旨を診断ログに記録する。

> 注記: 表情操作の入力経路(Character タブ UI 経由か、OSC/ARKit 直か)は設計フェーズで確定する。これは Requirement 1 のサブパッケージ導入範囲(.osc/.inputsystem 等)と連動する。

### Requirement 7: 段階導入(MVP=avatar+mocap → facial)
**Objective:** VTuberSystemBase の利用者として、まず avatar+mocap を先に通してから表情を後段で追加したい。これにより、手触りを早期に確認しつつリスクを分割して導入できる。

#### Acceptance Criteria
1. The VTSB Integration Layer shall avatar 表示 + VMC mocap 駆動(Requirement 2・3・4)を、表情(Requirement 5・6)に先行して成立させられる段階構成を採る。
2. While 表情統合(FacialControl)が未配線または無効のとき, the VTSB Integration Layer shall avatar 表示と VMC mocap 駆動を正常に機能させ続ける。
3. Where 表情統合が導入されているとき, the VTSB Integration Layer shall avatar+mocap の機能を維持したまま表情駆動を追加する。

> 注記: 具体的な Phase 区切り(MVP のスコープと facial 導入の境界)は設計フェーズで確定する。

### Requirement 8: 出力経路への結線と目視検証
**Objective:** VTuberSystemBase の利用者として、統合後のアバターを既存の Spout/URP/RT 出力で OBS/Game ビューに表示したい。これにより、配信品質を実機で目視確認できる。

#### Acceptance Criteria
1. The VTSB Integration Layer shall 表示・駆動されたアバターを既存の Spout/URP/RT 出力経路に流用結線する(出力基盤は新規実装しない)。
2. When Play モードで FBX アバターをスロットに割り当てたとき, the VTSB Integration Layer shall アバターを表示し、その出力を Game/OBS ビューで確認できる状態にする。
3. When VMC 送信が行われたとき, the VTSB Integration Layer shall Game/OBS ビューでアバターの全身がモーションに追従する状態にする。
4. When 表情操作が行われたとき, the VTSB Integration Layer shall Game/OBS ビューでアバターの表情が切り替わる状態にする。
5. The VTSB Integration Layer shall Unity 6000.3.10f1 / URP 17.3.0 環境で上記検証シナリオを成立させる。

## 設計フェーズで確定する事項

### ユーザー確定済み(discovery 追補 2026-05-31〜2026-06-01)
- **FacialControl サブパッケージ導入範囲: 全部導入**(コア `com.hidano.facialcontrol` + `.lipsync` + `.osc` + `.inputsystem`)。Requirement 1 の `<pkg>` は 4 パッケージを指す。
- **表情の制御主体: 演者自走(performer-driven)**。表情は FacialControl が自前の入力バインディング(OSC/ARKit PerfectSync・uLipSync・InputSystem)から avatar 上で直接駆動する。**VTSB の IPC / Character タブは表情に関与しない**。よって表情スキーマ・settings/command ルーティング・表情用 IAvatarSettingsAdapter は本仕様では実装しない。オペレーター UI からの表情制御は本仕様スコープ外(将来 Phase3 で任意追加)。→ これにより旧 Requirement 6(表情操作の IPC ルーティング)は不要化(スコープ外)。
- **FacialController 結線方式: 実行時 Add**。slot Active 後に avatar へ AddComponent + FacialCharacterProfileSO 割当 + Initialize。avatarKey→Prefab/Profile は自前 AvatarCatalog SO で一元管理。
- **パッケージ構成: Option C(子供＋覗き窓)**。新規統合パッケージ(RAC core / mocap-vmc / FacialControl に依存=「子供」)に自前実装(リゾルバ・スキーマ・VMC factory・AvatarCatalog SO・FacialController 結線フック・SlotMotionDriver)を集約。既存 rac-main-output-adapter には `SlotManager` の read-only 公開と駆動ループ呼び出しに必要な最小 API 追加のみ。RAC 本体・他既存 spec は無改修。
- **段階導入: MVP 先行**。Phase 1 = avatar 表示 + VMC mocap 駆動 + モーション適用ループ(Requirement 2・3・4)。Phase 2 = avatar への FacialController 結線(Requirement 5、自走表情)。

### 設計フェーズで詰める残論点
- VMC 自己登録の検証手段(Registry typeId ログ出力ユーティリティの要否)(Requirement 3)。
- AvatarCatalog SO の具体スキーマ、および IAvatarSchemaProvider が UI に出す項目(表情は自走化のため対象外。アバター一覧・非表情設定が中心)(Requirement 2)。
- FacialCharacterProfileSO の Adapter Bindings(OSC ポート/ARKit/uLipSync デバイス)の既定値と設定手順(Requirement 5)。
- 既存 adapter に追加する `SlotManager` 公開 API の最小形と、SlotMotionDriver の所有/ライフサイクル(Requirement 4)。
