# Gap Analysis: avatar-mocap-facial-integration

## 分析サマリー
- VTSB の RAC 配線基盤（adapter / Character タブ / IPC / IntegratedDemo）は完成済みで、4 つの拡張点（`IAvatarKeyResolver` / `IAvatarSchemaProvider` / `IMoCapSourceConfigFactory` / `IAvatarSettingsAdapter`）+ `OverrideServices` により、RAC 本体・既存パッケージ無改修で全要件を達成可能。
- 最大のギャップは Requirement 4（モーション適用ループ）。`SlotManager.TryGetSlotResources` は存在するが、adapter の `SlotManager` インスタンスが `RacMainOutputAdapterBootstrapper` の private フィールドに閉じ込められており、外部の駆動ループから参照できない。新規サービス + adapter 側の SlotManager 公開（or 内蔵駆動）が必須。
- 表情ルーティング（Requirement 5/6）は `IAvatarSettingsAdapter.Apply(avatar, settingKey, …)` が「avatar GameObject + settingKey」を渡すため、ここが FacialControl 駆動の天然フック。表情は schema の `Kind="command"`（slot/command）または `Enum` setting としてモデル化できる。
- 2 つ目のギャップ: 既存 `RacMainOutputAdapterHost` / `IntegratedDemoBootstrap` は keyResolver/mocapFactory を `OverrideServices` に渡していない（dispatcher/sink/logger のみ）。カスタム解決を差し込むには Host への注入経路追加が必要。
- パッケージ取込は manifest.json への git+ssh 3 件（FacialControl は 4 パッケージ）+ OpenUPM scopedRegistry（scope: `jp.hadashikick`）追加 + VContainer の明示 dependency。asmdef 参照の追加が複数必要。

## Requirement → 資産マップ（ギャップタグ）

| Req | 既存資産（流用） | ギャップ |
|---|---|---|
| R1 取込 | `Packages/manifest.json`, 各 package.json/asmdef | **Missing**: git+ssh 参照・OpenUPM scope 追加・VContainer dependency・asmdef 参照配線 |
| R2 アバター解決 | `IAvatarKeyResolver`, `AddressablesAvatarKeyResolver`(参考), `BuiltinAvatarProvider`/`Config`, `OverrideServices` | **Missing**: 自前 SerializeField カタログ resolver / inmemory schema provider / Host 注入経路 |
| R3 VMC 設定 | `IMoCapSourceConfigFactory`, `StubMoCapSourceConfigFactory`(参考), `VMCMoCapSourceFactory`(自己登録) | **Missing**: VMC descriptor 返却 factory + Host 注入 |
| R4 モーションループ | `SlotManager.TryGetSlotResources`, `MotionCache`, `HumanoidMotionApplier`, RAC `SlotManagerBehaviour`(参考実装) | **Missing/Constraint**: adapter の SlotManager 非公開・駆動 MonoBehaviour 不在・ライフサイクル所有者未定 |
| R5 表情駆動 | `FacialController`, `FacialCharacterProfileSO`, `FacialProfile.FindExpressionById`, `IAvatarSettingsAdapter` | **Missing**: settings adapter 実装・Expression 解決・FacialController 結線方式 |
| R6 表情ルーティング | `CharacterTopics.SlotSettingValue/SlotCommand`, `SlotSettingsApplier`, `SlotCommandApplier`, schema `Kind="command"` | **Missing**: schema に表情エントリ・adapter で Activate/Deactivate 呼出 |
| R7 段階導入 | （設計判断） | **Constraint**: facial を独立 disable 可能に保つ構成 |
| R8 出力/検証 | OutputSceneBootstrapper, Spout, IntegratedDemo | 既存流用（追加実装ほぼ不要） |

---

## 詳細分析

### 1) Addressables 非依存アバター解決の差し込み箇所と avatarKey 解決フロー

**差し込み箇所**: `RacMainOutputAdapterBootstrapper.Initialize()`（`Runtime/Bootstrapper/RacMainOutputAdapterBootstrapper.cs:99-104`）の既定フォールバックは `_keyResolver ??= new AddressablesAvatarKeyResolver(...)` / `_schemaProvider ??= new AddressablesAvatarSchemaProvider(...)`。`OverrideServices(keyResolver:, schemaProvider:, mocapFactory:)`（同 56-76 行）を **Initialize 前に**呼べば null 合体演算子により自前実装が採用される。

**重要ギャップ**: 現状 `OverrideServices` を呼ぶ唯一の本番経路 `RacMainOutputAdapterHost.Start()`（`Runtime/Bootstrapper/RacMainOutputAdapterHost.cs:118-122`）は `dispatcher / sceneRoots / messageSink / logger` のみ渡し、keyResolver/schemaProvider/mocapFactory を渡していない。`IntegratedDemoBootstrap` は Host をリフレクション生成するだけ。→ 自前解決の差し込みには次のいずれか:
- Host に Provider を追加し `Start()` の `OverrideServices` に含める（Host は別 spec の所有物で「既存改修しない」方針と緊張）。
- (推奨) 本仕様用の薄い Composition Root を新設し `RacMainOutputAdapterBootstrapper` を直接生成・`OverrideServices`・`Initialize`・`Shutdown`。IntegratedDemo の RAC 生成箇所をこれに差し替え。

**avatarKey → カタログ解決フロー**:
1. Character タブ →（IPC）→ `slot/{id}/assignment`(`SlotAssignmentPayload.AvatarKey`)。
2. `SlotAssignmentApplier.HandleAssignment`（`Receivers/SlotAssignmentApplier.cs:209`）が `_keyResolver.Resolve(avatarKey)` → `AvatarProviderDescriptor`（null なら `KeyNotFound` → Req2.5 診断ログ充足）。
3. 同 217-223 で `SlotSettings` を組み立て、`avatarProviderDescriptor` と `moCapSourceDescriptor = _mocapFactory.Build(slotId)` をセットし `_slotManager.AddSlotAsync`。
4. RAC `SlotManager.AddSlotAsync`（`SlotManager.cs:95-99`）→ `BuiltinAvatarProvider.RequestAvatar` → `Instantiate(config.avatarPrefab)`（`BuiltinAvatarProvider.cs:52`、Addressables 非依存）。

**自前 resolver 実装要点**: `AddressablesAvatarKeyResolver`（`Defaults/AddressablesAvatarKeyResolver.cs:60-91`）と同型で、Addressables の代わりに SerializeField カタログから prefab を引き、`BuiltinAvatarProviderConfig`(動的生成)→`AvatarProviderDescriptor{ ProviderTypeId = BuiltinAvatarProviderFactory.BuiltinProviderTypeId }` を返す。`AvatarKeys` + `OnAvatarKeysChanged` も実装すれば `AvatarCatalogPublisher` 経由で Character タブのアバター一覧が埋まる（現状 demo はデフォルト Addressables のためカタログ空＝この実装で解消）。

### 2) VMC mocap 配線 + モーション適用ループ新設

**VMC 設定差し替え**:
- 自前 `IMoCapSourceConfigFactory.Build(slotId)` は `StubMoCapSourceConfigFactory` と同型で `MoCapSourceDescriptor{ SourceTypeId = "VMC", Config = ScriptableObject.CreateInstance<VMCMoCapSourceConfig>() }`（port=39539, bindAddress="0.0.0.0"）を返す。typeId は `VMCMoCapSourceFactory.VmcSourceTypeId = "VMC"`。
- **自己登録の確認**: `VMCMoCapSourceFactory.RegisterRuntime`（`mocap-vmc/Runtime/VMCMoCapSourceFactory.cs:82-96`）が `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]` で `RegistryLocator.MoCapSourceRegistry.Register("VMC", ...)`。Editor 側は `Editor/VmcMoCapSourceFactoryEditorRegistrar.cs`。検証は `AddSlotAsync` 内 `_moCapSourceRegistry.Resolve(descriptor)` 成否、または Registry 登録 typeId 一覧のログ出力ユーティリティ追加（Req3.2）。
- uOSC 依存は VTSB 導入済み（`com.hidano.uosc 1.0.0`）。追加なし。

**モーション適用ループ（最重要ギャップ）**:
- RAC `SlotManager.AddSlotAsync` は provider/source を Resolve+Initialize するのみで、`IMoCapSource` → アバター適用は行わない（`SlotManager.cs:95-118`、`TryGetSlotResources` で上位委譲を明記）。
- **参考実装**: RAC Sample `SlotManagerBehaviour`（`Samples~/UI/Runtime/SlotManagerBehaviour.cs`）が完全な雛形。`OnSlotStateChanged(Active)` で `TryGetSlotResources` → `MotionCache.SetSource(source)` + `HumanoidMotionApplier.SetAvatar(avatar)` を per-slot 構築、`LateUpdate` で `SlotManager.ApplyWithFallback(slotId, () => applier.Apply(cache.LatestFrame, weight, settings))`、`Disposed` で teardown。`MotionCache` は受信スレッド→メインスレッドのロックフリー受け渡し。`HumanoidMotionApplier` は Animator.isHuman 必須・VMC BoneLocalRotations 経路対応済み（`HumanoidMotionApplier.cs:222-245`）。
- **構造上の制約**: 雛形は自前 `new SlotManager(...)` を持つが、VTSB adapter は専用 `SlotManager` を `RacMainOutputAdapterBootstrapper._slotManager`（private）に保持。**同一 SlotManager を駆動ループと共有しないと slot が存在しない**。→ (A) Bootstrapper に SlotManager/状態ストリームを read-only 公開、(B) 本仕様で Bootstrapper を直接生成する Composition Root を持ち SlotManager 参照をループへ渡す（推奨。論点1と一本化）。
- **駆動者**: `HumanoidMotionApplier.Apply` / `MotionCache.SetSource` はメインスレッド & `LateUpdate` 前提 → 駆動は MonoBehaviour の `LateUpdate` 必須。所有者候補: IntegratedDemo（手触り検証最短）／rac-main-output-adapter の新規 `SlotMotionDriver`（恒久的に綺麗）。output-renderer-shell は slot を知らず不適。
- フォールバック（Req3.4 VMC 無送信時「直前姿勢保持」）は `SlotSettings.fallbackBehavior=HoldLastPose`（既定）+ `MotionCache.LatestFrame` の前フレーム保持で自動充足。

### 3) 表情ルーティングと FacialController ライフサイクル結合点

**IPC 経路（主経路 = Character タブ UI）**:
- `Kind="command"` 経路: `slot/{id}/command`(`SlotCommandPayload{Kind, Argument}`) → `SlotCommandApplier.HandleEvent`（`Receivers/SlotCommandApplier.cs:66`）。現状 switch は Reset/Reload/PresetApply のみ。表情を流すなら新 Kind 追加が必要だが、`SlotCommandApplier` は `TryGetSlotResources` を持たず avatar GameObject に到達できない → avatar 参照経路追加が要る。
- **(推奨) settings 経路**: 表情を Enum/Bool setting として schema 化 → `slot/{id}/settings/{key}` → `SlotSettingsApplier.HandleState`（`Receivers/SlotSettingsApplier.cs:112-141`）が `_slotManager.TryGetSlotResources(slotId, out _, out avatar)` で **avatar GameObject を取得済み**で `_settingsAdapter.Apply(avatar, settingKey, type, value)` を呼ぶ。ここが天然フック。
- **adapter での受け口**: 自前 `IAvatarSettingsAdapter.Apply` を実装し、settingKey（例 `expression`）+ value（表情名）から avatar 上の `FacialController` を取得、`controller.CurrentProfile?.FindExpressionById(name)`（`FacialProfile.cs:308`）で `Expression` 解決して `Activate/Deactivate`。未知表情・BlendShape/Profile 欠如時は `AdapterApplyResult.UnknownKey`/ログ（Req5.6/6.4）。`Apply` は冪等要求に注意。

**FacialController と avatar/slot のライフサイクル結合点**:
- `FacialController` は `[RequireComponent(typeof(Animator))]`・`OnEnable` で `_characterSO != null` なら自動 `Initialize()`（`FacialController.cs:113-120`）。`Activate(Expression)` は `_isInitialized` 必須。
- **prefab 内蔵 vs 実行時 Add**:
  - 内蔵: prefab に `FacialController`+SO を仕込めば `Instantiate` 直後の `OnEnable` で自動初期化、最も単純。BlendShape 名はメッシュ依存で prefab 固有。
  - 実行時 Add: slot Active 後に `avatar.AddComponent<FacialController>()` + `CharacterSO=so` + `Initialize()`。avatarKey→SO マッピングを自前カタログに同梱可能 → **カタログ一元管理上有利**。
- VContainer LifetimeScope（Req5.5）は `FacialController.InitializeInternal` → `FacialControllerLifetimeScope.Build`（`FacialControllerLifetimeScope.cs:68`）が **per-FacialController child scope を自動 build/dispose**。親は `FacialControlAppLifetimeScope.GetOrCreate()`。**VTSB 側で明示的に VContainer を張る必要はなく**、FacialController を結線するだけで Req5.5 充足。
- 破棄結合点: slot Disposed（`RemoveSlotAsync` → `Provider.ReleaseAvatar` → `Destroy(avatar)`）で FacialController も GameObject ごと破棄され `OnDisable→Cleanup` で child scope dispose。整合的。

### 4) FBX アバターと FacialCharacterProfileSO の配置・カタログ登録

- `FacialCharacterProfileSO`（`Adapters/ScriptableObject/FacialCharacterProfileSO.cs`、`CreateAssetMenu "FacialControl/Facial Character Profile"`）。`LoadProfile()` は `StreamingAssets/FacialControl/{SO名}/profile.json` 優先、無ければ SO Inspector データ（`BuildFallbackProfile`）。検証用 FBX は **Humanoid rig 必須**（`HumanoidMotionApplier.SetAvatar` が `Animator.isHuman==false` で例外、`HumanoidMotionApplier.cs:87-91`）かつ BlendShape 付き SkinnedMeshRenderer。
- **自前カタログ設計（推奨形）**: ScriptableObject ベースの `AvatarCatalog`。1 エントリ = `{ string AvatarKey; string DisplayName; GameObject AvatarPrefab; FacialCharacterProfileSO FacialProfile (optional); }`。自前 `IAvatarKeyResolver` がこの SO を保持し `Resolve(key)` で prefab→descriptor を返す。schema provider は同カタログから表情リスト（Enum options or command エントリ）を `AvatarSettingsSchemaPayload` として組み立て（`FacialProfile.Expressions` の Id を列挙）→ Character タブに表情 UI が自動生成される。
- prefab は通常 Assets 配下（Addressables 不要）に置き、カタログ SO で参照を握るだけ。

### 5) パッケージ取込（manifest / scopedRegistry / asmdef）

**manifest.json（`VTuberSystemBase/Packages/manifest.json`）追加**:
- dependencies に git+ssh:
  - `com.hidano.realtimeavatarcontroller.mocap-vmc`: `git@github.com:Hidano-Dev/RealtimeAvatarController.git?path=RealtimeAvatarController/Packages/com.hidano.realtimeavatarcontroller.mocap-vmc#main`
  - FacialControl 4 パッケージ（コア + `.lipsync`/`.osc`/`.inputsystem`、全導入確定）: `git@github.com:NHidano/FacialControl.git?path=FacialControl/Packages/<pkg>#feature/hidano/generate-prototype`（`<pkg>` = `com.hidano.facialcontrol`, `com.hidano.facialcontrol.lipsync`, `com.hidano.facialcontrol.osc`, `com.hidano.facialcontrol.inputsystem`）
- scopedRegistries に OpenUPM の **scope `jp.hadashikick`** 追加（既存 OpenUPM entry に 1 行）。
- VContainer は package.json dependencies に無く asmdef ref でのみ要求 → **manifest dependencies に `jp.hadashikick.vcontainer: "1.16.6"` を明示追記が必要**（scope だけでは入らない）。
- FacialControl コアは `com.hidano.scene-view-style-camera-controller 1.0.0` 依存（VTSB は 1.0.1 で充足）。

**asmdef 参照追加**:
- 実装を rac-main-output-adapter 内に置く場合 `VTuberSystemBase.RacMainOutputAdapter.Runtime.asmdef` に: `RealtimeAvatarController.MoCap.VMC`, `RealtimeAvatarController.Motion`（MotionCache/HumanoidMotionApplier/HumanoidMotionFrame）, FacialControl `Hidano.FacialControl.Adapters`/`Hidano.FacialControl.Domain`（Activate/Deactivate だけなら VContainer 直参照は不要）。
- **(推奨) 本仕様専用の新規 asmdef/パッケージ**（例 `com.hidano.vtuber-system-base.avatar-mocap-facial-integration`）に上記 ref を集約し、rac-main-output-adapter は無改修のまま（SlotManager 公開の最小 read-only API のみ）。段階導入 (R7) と「既存改修しない」方針の両立に最適、facial を別 asmdef で切り離して MVP を独立成立させやすい。
- mocap-vmc asmdef は `uOSC.Runtime`/`UniRx` 参照済み、FacialControl Adapters asmdef は `VContainer`/`Unity.Animation`/`Unity.Collections` 参照済み（依存は manifest で解決）。

---

## 実装アプローチ（A/B/C）

- **Option A（既存拡張）**: rac-main-output-adapter の Host/Bootstrapper を直接改修して resolver/mocapFactory/settingsAdapter 注入 + SlotManager 公開 + 駆動ループ内蔵。✅ ファイル少 ❌ 別 spec パッケージ改修で「既存改修しない」方針に反する、段階導入の切り離しが難。
- **Option B（新規パッケージ／Composition Root）**: 本仕様専用パッケージを新設し、`RacMainOutputAdapterBootstrapper` を直接生成・`OverrideServices`・`Initialize` する VTSB 統合 Composition Root + `SlotMotionDriver`(MonoBehaviour) + 自前 4 実装 + `AvatarCatalog` SO + `FacialExpressionSettingsAdapter` を内包。IntegratedDemo の RAC 生成箇所だけ差し替え。✅ 関心分離・R7 段階導入・RAC/既存無改修と整合・テスト容易 ❌ 新規ファイル多・SlotManager 共有のため Bootstrapper への最小限の read-only 公開 API 追加は不可避。
- **Option C（ハイブリッド／推奨）**: 自前 4 実装・カタログ・settings adapter は新規パッケージ（B）。ただし「SlotManager 参照公開」と「LateUpdate 駆動 MonoBehaviour」は rac-main-output-adapter に最小追加（`SlotManager` read-only 公開プロパティ + `SlotMotionDriver`）し、FacialControl 連携は新パッケージ側に隔離。Phase1=avatar+VMC+motion（FacialControl asmdef 参照なしで成立）、Phase2=facial パッケージ/asmdef を有効化。✅ MVP 先行・facial を物理的に切離可能・改修は read-only API + driver の最小限。

## Effort / Risk
- R1 取込: **S / Medium**（git+ssh 鍵・派生ブランチ・VContainer 解決でビルドが通るまで試行錯誤あり）
- R2 解決 + R3 VMC 設定: **M / Low**（既定実装の写経 + 既存拡張点、パターン確立済み）
- R4 モーションループ: **M / Medium**（SlotManagerBehaviour 雛形あり=Low寄りだが、SlotManager 共有/ライフサイクル所有者の設計判断と Humanoid 前提検証で Medium）
- R5/R6 表情: **M〜L / Medium**（settings/command どちらの経路か、prefab 内蔵 vs 実行時 Add、schema 生成、Expression 解決の確定が必要。VContainer 自体は FacialController が内製するので Low 寄せ可能）
- R7/R8: **S / Low**（出力は既存流用、段階構成は asmdef 分離で担保）

## 設計フェーズへ持ち越す Research/決定事項
1. RAC `SlotManager` をどう駆動ループへ共有するか（Bootstrapper 公開 API 追加 vs 新 Composition Root 直接生成）。「既存改修しない」境界の最終確定。
2. モーション駆動 MonoBehaviour の所有パッケージ（rac-main-output-adapter 内 `SlotMotionDriver` vs 新統合パッケージ vs IntegratedDemo）。
3. 表情 IPC 経路: settings(`slot/{id}/settings/{key}`、avatar 到達可) vs command(`slot/{id}/command`、avatar 未到達=要追加配線)。推奨は settings。
4. FacialController 結線方式: prefab 内蔵 vs 実行時 Add（カタログ一元管理なら実行時 Add 有利）。
5. `AvatarCatalog` SO のスキーマ（avatarKey / prefab / FacialCharacterProfileSO / 表情一覧 schema 生成方法）。
6. VMC 自己登録の検証手段（Registry typeId ログ出力ユーティリティの要否）。
7. `.osc/.inputsystem/.lipsync` の Phase2 有効化方法（主経路は Character タブ UI のため任意の追加入力源扱い）。

## 注意・前提
- **steering 欠落**: `.kiro/steering/*.md` は不在（テンプレのみ）。本分析は requirements.md + コードベース実体 + MEMORY.md を根拠とする。steering 整備で精度向上の余地あり。
- **requirements 未承認**: spec.json は `approved=false`。本ギャップ分析は要件改訂にも資する前提で実施。
