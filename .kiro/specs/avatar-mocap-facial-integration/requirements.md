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

## Requirements
<!-- Will be generated in /kiro-spec-requirements phase -->
