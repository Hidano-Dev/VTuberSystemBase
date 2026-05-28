# UI 設計タスク用 API リファレンス（逆引き版）

本ドキュメントは「Display 1 側 UI（タブ + シェル）を本格的にデザインし直す」タスクのために、プロジェクトの 10 パッケージが UI 側へ公開している契約面を **「○○したい」から関連 API を引ける逆引き構成** に並べ直したものである。専門用語に頼らず、初めてこのコードに触れる開発者でも処理イメージが掴めるように説明している。

- 版数: 0.2 / 更新日: 2026-05-23（逆引き構成へ全面改訂）
- 出典: `VTuberSystemBase/Packages/com.hidano.vtuber-system-base.*/Runtime/`
- 各 API 行末尾の `path:line` から実シグネチャ・コメントへ辿れる。
- 「UI 側から触る面」に絞っており、各パッケージの内部実装（Apply 側のハンドラなど）はあえて省略している。
- 補完資料: `docs/integration-plan.md` / `docs/spec-breakdown.md`

## 読み方

- 見出しはすべて「やりたいこと」になっている。まず**何をしたいか**で章を絞り、中の API 行を辿る。
- メソッドはクラスごとに節を切らず、関数名のみで列挙している。所属クラスは末尾の `path` から判別する。
- 引数は `name: 型` の形で全部書き、戻り値があるものは ` → 戻り値型` を付けている。`?` は省略可 / nullable の意味。
- 「Topic」とは IPC で送受信するメッセージの宛先名のこと。`/` 区切りの文字列でルーティングしている。
- IPC の方向は UI から見て、↑ = UI からアダプタへ送る、↓ = アダプタから UI へ受け取る、で書いている。

## 全体像

```
Display 1 (UI process)                                 Display 2+ (Output process / 同一プロセスでも可)
┌─────────────────────────────────────┐               ┌─────────────────────────────────────┐
│ ui-toolkit-shell                    │               │ output-renderer-shell               │
│  ├ Root UIDocument (Display 1)      │               │  ├ Scene Roots / Dispatcher         │
│  ├ TabPanelRegistry                 │               │  └ Display routing (BuiltIn/RDS)    │
│  └ IUiCommandClient/Subscription    │               │                                     │
│                                     │               │   ┌────────────────────────────┐    │
│  ├ character-selection-tab          │ ICoreIpcBus   │   │ rac-main-output-adapter    │    │
│  ├ stage-lighting-volume-tab        │◀────────────▶│   │ stage-lighting-volume-     │    │
│  └ camera-switcher-tab              │ (topic 単位)  │   │   output-adapter           │    │
│                                     │               │   │ camera-switcher-output-    │    │
└─────────────────────────────────────┘  OSC ──┐      │   │   adapter (+ OSC 受信)     │    │
                                              ▼      │   └────────────────────────────┘    │
                                       /ucapi/camera/{id}/flat                              │
                                                     └─────────────────────────────────────┘
```

3 つのタブ ↔ 3 つの output-adapter は **1 対 1** で対応する。タブ側は「UI 用の窓口（`IUiCommandClient`）」経由で topic に値を書いたり要求を投げたりし、アダプタ側は「バス本体（`ICoreIpcBus`）」で同じ topic を購読・応答する。

## 目次（やりたいこと別）

| # | やりたいこと | 章 |
| - | --- | --- |
| A | UI シェルを起動・停止したい | §A |
| B | UI シェル全体の設定を組み立てたい | §B |
| C | UI シェルの見た目（UXML / USS）を差し替えたい | §C |
| D | タブを登録・切り替えたい | §D |
| E | タブの寿命や後始末を管理したい | §E |
| F | アセット（Addressables）を非同期ロードしたい | §F |
| G | IPC バス本体で送受信したい | §G |
| H | タブから IPC を送受信したい（UI 側ファサード） | §H |
| I | IPC のメッセージや結果の形を知りたい | §I |
| J | IPC ランタイムを起動・設定したい | §J |
| K | 出力側（Display 2+）のシーンをセットアップしたい | §K |
| L | 各アダプタを起動・差し替えたい | §L |
| M | Character タブで操作したい | §M |
| N | Stage / Light / Volume タブで操作したい | §N |
| O | Camera タブで操作したい | §O |
| P | アダプタが受け取る Topic を一覧で確認したい | §P |
| Q | integrated-demo の起動シーケンスを参照したい | §Q |
| R | UI 再設計時の落とし穴を確認したい | §R |

---

## A. UI シェルを起動・停止したい

Display 1 側で UI を「電源を入れる／切る」操作に相当する。UI シェルとは、ルートの UIDocument・PanelSettings・タブ枠・通知バーなど **「画面の土台」全体を一括で管理する仕組み**のこと。

### A-1. UI シェルを起動したい

UIDocument を生成し、ルート UXML を差し込み、3 つのタブを順番に登録して画面に出す、までを一気にやる。`Configure` で設定とログを先に渡してから `StartShell` を呼ぶ流れ。

- `Configure(configProvider: IUiShellConfigProvider, bootstrapperFactory?: Func<UiShellConfig, IUiShellBootstrapper>, loggerFactory?: Func<LogLevel, IDiagnosticsLogger>) → void` — `ui-toolkit-shell/Runtime/Bootstrap/UiShellLifecycleDriver.cs:100`
- `StartShell() → void`（static / Configure 後に呼ぶ） — `ui-toolkit-shell/Runtime/Bootstrap/UiShellLifecycleDriver.cs`
- `StartShell(config: UiShellConfig) → BootstrapResult`（低レベル直接版） — `ui-toolkit-shell/Runtime/Bootstrap/IUiShellBootstrapper.cs:20`

### A-2. UI シェルを止めたい

開いていた UI を閉じ、登録したタブや購読を全部破棄してメモリから解放する。

- `StopShell() → void` — `ui-toolkit-shell/Runtime/Bootstrap/IUiShellBootstrapper.cs:26`

### A-3. シェルが起動中か確認したい

「いま画面が出ている状態か」のフラグ。再起動防止やデバッグに使う。

- `IsRunning: bool` — `ui-toolkit-shell/Runtime/Bootstrap/UiShellLifecycleDriver.cs:78`
- `IsRunning: bool`（低レベル版） — `ui-toolkit-shell/Runtime/Bootstrap/IUiShellBootstrapper.cs:28`

### A-4. 起動の進捗・ログを確認したい

シェルを立ち上げる過程でどこまで進んだか（PanelSettings 生成 → Skin 検証 → タブ登録…）の足跡を取り出す。失敗解析に便利。

- `InitializationSteps: IReadOnlyList<UiShellInitializationStep>` — `ui-toolkit-shell/Runtime/Bootstrap/IUiShellBootstrapper.cs:35`
- `Current: IUiShellBootstrapper?`（いま走っている bootstrapper） — `ui-toolkit-shell/Runtime/Bootstrap/UiShellLifecycleDriver.cs:85`
- `StartInvocationCount: int`（再起動回数。リーク検証用） — `ui-toolkit-shell/Runtime/Bootstrap/UiShellLifecycleDriver.cs:93`

---

## B. UI シェル全体の設定を組み立てたい

シェルへ「どの UXML を使うか」「どこの IPC バスに繋ぐか」「どのモニタに表示するか」をまとめて渡す箱が `UiShellConfig`。**`SkinProfile` と `IpcBus` だけ必須**で、残りは省略すると標準実装が入る。

### B-1. 必須項目を埋めたい（最小起動）

- `SkinProfile: UiToolkitShellSkinProfile`（必須・ §C 参照） — `ui-toolkit-shell/Runtime/Bootstrap/UiShellConfig.cs:31`
- `IpcBus: ICoreIpcBus`（必須・通信先のバス） — `ui-toolkit-shell/Runtime/Bootstrap/UiShellConfig.cs:34`

### B-2. タブの mount 方法を上書きしたい

タブの UXML をどこにぶら下げるか（ルートのどこに `Add` するか）の戦略。既定はパッケージ標準実装が入る。

- `TabMountStrategy: ITabMountStrategy?` — `ui-toolkit-shell/Runtime/Bootstrap/UiShellConfig.cs:42`

### B-3. Addressables 初期化をシェル側から動かしたい

UXML やキャラ素材を Addressables で配っている場合、シェル側の起動シーケンスから一度だけ初期化させたい時に渡す。

- `AddressablesInitializer: IAddressablesInitializer?` — `ui-toolkit-shell/Runtime/Bootstrap/UiShellConfig.cs:49`

### B-4. ログ送信先を差し替えたい

シェルが出すログを独自のロガー（ファイル出力等）に流したい時に渡す。

- `DiagnosticsLogger: IDiagnosticsLogger?` — `ui-toolkit-shell/Runtime/Bootstrap/UiShellConfig.cs:56`
- `MinimumLogLevel: LogLevel?`（出力する最小レベル） — `ui-toolkit-shell/Runtime/Bootstrap/UiShellConfig.cs:83`

### B-5. 表示する Display（モニタ）を選びたい

複数モニタ環境でどのディスプレイに UI を出すか。既定戦略は Display 0、整数で明示指定すると上書きされる。

- `DisplayAssignmentStrategy: IDisplayAssignmentStrategy?` — `ui-toolkit-shell/Runtime/Bootstrap/UiShellConfig.cs:62`
- `RequestedTargetDisplay: int?`（直接モニタ番号を指定） — `ui-toolkit-shell/Runtime/Bootstrap/UiShellConfig.cs`

### B-6. 起動時に最初に開くタブを決めたい

シェルが立ち上がった瞬間に有効化されるタブを `TabId` で指定する。未指定なら Character タブが選ばれる。

- `InitialTab: TabId?` — `ui-toolkit-shell/Runtime/Bootstrap/UiShellConfig.cs:86`

---

## C. UI シェルの見た目（UXML / USS）を差し替えたい

「**スキン**」と呼んでいるのは UXML（画面構造）と USS（スタイル）の差し替えセット。シェルは ScriptableObject の `UiToolkitShellSkinProfile` から UXML / USS を読み取ってルートとタブを作る。

### C-1. ルート UXML / USS を差し替えたい

タブバーと通知バーを含む、シェルの一番外側の画面定義。

- `RootVisualTreeAsset: VisualTreeAsset`（必須） — `ui-toolkit-shell/Runtime/Skin/UiToolkitShellSkinProfile.cs:35`
- `RootStyleSheets: List<StyleSheet>` — `ui-toolkit-shell/Runtime/Skin/UiToolkitShellSkinProfile.cs:36`
- `CommonUiStyleSheets: List<StyleSheet>`（共通スタイル上書き） — `ui-toolkit-shell/Runtime/Skin/UiToolkitShellSkinProfile.cs:51`

### C-2. タブ別の UXML / USS を差し替えたい

タブごとに別 UXML を当てたい時に使う。Character / StageLighting / CameraSwitcher の 3 タブそれぞれにフィールドが用意されている。

- `CharacterTabVisualTreeAsset: VisualTreeAsset` — `ui-toolkit-shell/Runtime/Skin/UiToolkitShellSkinProfile.cs:39`
- `CharacterTabStyleSheets: List<StyleSheet>` — `ui-toolkit-shell/Runtime/Skin/UiToolkitShellSkinProfile.cs:40`
- `StageLightingTabVisualTreeAsset: VisualTreeAsset` — `ui-toolkit-shell/Runtime/Skin/UiToolkitShellSkinProfile.cs:43`
- `StageLightingTabStyleSheets: List<StyleSheet>` — `ui-toolkit-shell/Runtime/Skin/UiToolkitShellSkinProfile.cs:44`
- `CameraSwitcherTabVisualTreeAsset: VisualTreeAsset` — `ui-toolkit-shell/Runtime/Skin/UiToolkitShellSkinProfile.cs:47`
- `CameraSwitcherTabStyleSheets: List<StyleSheet>` — `ui-toolkit-shell/Runtime/Skin/UiToolkitShellSkinProfile.cs:48`

### C-3. 必須 USS クラスの規約を確認したい

シェルは「ルートと各タブが特定の USS クラス名を持っていること」を起動時にチェックする。**接頭辞 `vsb-` 必須**。これを満たさないと起動失敗するので、新 UXML を作る時は必ず照合する。

- ルート必須: `vsb-tab-bar`, `vsb-tab-bar__button`, `vsb-notification-bar` — `ui-toolkit-shell/Runtime/Skin/SkinValidationRules.cs:102-107`
- Character タブ必須: `vsb-tab-root`, `vsb-tab-root--character` — `ui-toolkit-shell/Runtime/Skin/SkinValidationRules.cs:110-114`
- StageLighting タブ必須: `vsb-tab-root`, `vsb-tab-root--stage-lighting` — `ui-toolkit-shell/Runtime/Skin/SkinValidationRules.cs:117-121`
- CameraSwitcher タブ必須: `vsb-tab-root`, `vsb-tab-root--camera-switcher` — `ui-toolkit-shell/Runtime/Skin/SkinValidationRules.cs:124-128`
- `RequiredTabClassesFor(tabId: TabId) → IReadOnlyList<string>`（タブ別の必須クラス取得） — `ui-toolkit-shell/Runtime/Skin/SkinValidationRules.cs:135`

### C-4. ルート UIDocument / PanelSettings の名前や生成を制御したい

シェルが内部的に生成する GameObject 名や PanelSettings は固定だが、API から作り直すこともできる。

- `DefaultRootGameObjectName: string = "VsbUiToolkitShellRoot"` — `ui-toolkit-shell/Runtime/Panels/RootUiDocumentBuilder.cs:43`
- `DefaultPanelSettingsName: string = "VsbUiToolkitShellPanelSettings"` — `ui-toolkit-shell/Runtime/Panels/RootUiDocumentBuilder.cs:52`
- `CreateSharedPanelSettings(requestedTargetDisplay: int) → PanelSettings` — `ui-toolkit-shell/Runtime/Panels/RootUiDocumentBuilder.cs:67`
- `Build(profile: UiToolkitShellSkinProfile, panelSettings: PanelSettings) → UIDocument` — `ui-toolkit-shell/Runtime/Panels/RootUiDocumentBuilder.cs:83`

> Sample 経路では `themeStyleSheet` が動的生成で `null` になるため、`IntegratedDemoBootstrap.TryAssignDefaultPanelTheme`（Editor 限定）で `IntegratedDemoRuntimeTheme.tss` を後付けしている（`integrated-demo/Runtime/IntegratedDemoBootstrap.cs:187`）。

---

## D. タブを登録・切り替えたい

シェルは 3 つのタブ（Character / StageLighting / CameraSwitcher）を **「いま誰が active か」だけ動かす状態機械**として管理する。タブの本体（UXML や presenter）は各タブパッケージ側で生成し、シェルへ「登録 → mount 通知 → 必要に応じて切替」の順で繋ぐ。

### D-1. タブを登録したい

「このタブはこういう表示名で、もうすぐ画面に出すから準備しといてね」とシェルに伝える。戻り値の `ITabLifecycleHandle` でそのタブ専用の後始末を組む（§E）。

- `RegisterTab(tabId: TabId, metadata: TabMetadata) → ITabLifecycleHandle` — `ui-toolkit-shell/Runtime/Panels/ITabPanelRegistry.cs:56`
- `TabMetadata.DisplayName: string`（タブバーに出す名前） — `ui-toolkit-shell/Runtime/Panels/TabMetadata.cs:18`

### D-2. タブの UXML が画面に乗ったことを通知したい

UXML の `VisualElement` を実際にツリーへ挿した直後に呼ぶ。これで shell 側が「mount 済み」と認識し、active 切替の対象になる。

- `NotifyTabMounted(tabId: TabId) → void`（VisualElement を渡さない版） — `ui-toolkit-shell/Runtime/Panels/ITabPanelRegistry.cs:66`
- `NotifyTabMounted(tabId: TabId, root: VisualElement) → void`（VisualElement 付き） — `ui-toolkit-shell/Runtime/Panels/ITabPanelRegistry.cs:76`

### D-3. タブを切り替えたい

タブバーのボタンが押された時など、画面の表示タブを変える。

- `SwitchTo(target: TabId) → SwitchResult` — `ui-toolkit-shell/Runtime/Panels/ITabPanelRegistry.cs:95`

### D-4. 現在のアクティブタブを知りたい

- `ActiveTab: TabId` — `ui-toolkit-shell/Runtime/Panels/ITabPanelRegistry.cs:85`

### D-5. タブ切替を検知したい

切替アニメーションや、タブごとの非表示中描画停止などに使う。

- `OnTabSwitched: Action<TabSwitchedEventArgs>` — `ui-toolkit-shell/Runtime/Panels/ITabPanelRegistry.cs:103`

### D-6. プリロードの進捗を取りたい

3 タブすべての準備が終わったか（最初の起動演出を消すタイミング判定など）。

- `TotalTabCount: int`（想定総数 = 3） — `ui-toolkit-shell/Runtime/Panels/ITabPanelRegistry.cs:29`
- `GetPreloadProgress() → TabPreloadProgress` — `ui-toolkit-shell/Runtime/Panels/ITabPanelRegistry.cs:38`
- `IsPreloadComplete: bool` — `ui-toolkit-shell/Runtime/Panels/ITabPanelRegistry.cs:45`
- `OnPreloadChanged: Action<TabPreloadProgress>` — `ui-toolkit-shell/Runtime/Panels/ITabPanelRegistry.cs:121`

### D-7. 失敗扱いにしたい

タブの初期化が失敗した時に呼ぶと、切替先候補から除外され、診断ログにも記録される。

- `MarkTabFailed(tabId: TabId, reason: string) → void` — `ui-toolkit-shell/Runtime/Panels/ITabPanelRegistry.cs:112`

### D-8. タブの種類を表す ID

- `enum TabId { Character, StageLighting, CameraSwitcher }` — `ui-toolkit-shell/Runtime/Panels/TabId.cs:10-15`

---

## E. タブの寿命や後始末を管理したい

タブ単位で確保した購読・Disposable・Addressables ハンドルを、タブが捨てられるタイミングで自動的に解放するための仕組み。`RegisterTab` の戻り値 `ITabLifecycleHandle` がそれ。

### E-1. タブの ID / アクティブ状態を取りたい

- `TabId: TabId`（読取） — `ui-toolkit-shell/Runtime/Panels/ITabLifecycleHandle.cs:31`
- `IsActive: bool`（読取） — `ui-toolkit-shell/Runtime/Panels/ITabLifecycleHandle.cs:33`
- `ScopeId: AssetScopeId`（Addressables のスコープ識別子） — `ui-toolkit-shell/Runtime/Panels/ITabLifecycleHandle.cs:42`
- `IsDisposed: bool` — `ui-toolkit-shell/Runtime/Panels/ITabLifecycleHandle.cs:49`

### E-2. タブの有効化・無効化を検知したい

「このタブが画面に出ている間だけ毎フレーム処理したい」「裏に回ったら止めたい」みたいな時に使う。

- `OnActivated: Action` — `ui-toolkit-shell/Runtime/Panels/ITabLifecycleHandle.cs:60`
- `OnDeactivated: Action` — `ui-toolkit-shell/Runtime/Panels/ITabLifecycleHandle.cs:62`

### E-3. タブが破棄される時に Disposable を一括で捨てたい

`IDisposable` をハンドルに紐付けると、タブが破棄される時に自動で `Dispose` してくれる（購読トークンや CTS など）。

- `Track(disposable: IDisposable) → void` — `ui-toolkit-shell/Runtime/Panels/ITabLifecycleHandle.cs:71`

### E-4. タブで読んだアセットをまとめて解放したい

Addressables ローダーを紐付けておくと、タブ破棄時に当該スコープのアセットがすべて Release される。

- `TrackAssetScope(loader: IAsyncAssetLoader) → void` — `ui-toolkit-shell/Runtime/Panels/ITabLifecycleHandle.cs:80`

---

## F. アセット（Addressables）を非同期ロードしたい

タブが UXML テンプレや画像、アバター ScriptableObject を Addressables から取りに行く時の入口。タブのスコープに紐付くので、タブを閉じれば自動的に Release される。

### F-1. 非同期にロードしたい

- `LoadAsync<T>(addressableKey: string, scopeId: AssetScopeId, onCompleted: Action<AssetLoadResult<T>>) → IAssetLoadHandle` — `ui-toolkit-shell/Runtime/AssetLoading/IAsyncAssetLoader.cs:24`

### F-2. 1 件のハンドルを解放したい

- `Release(handle: IAssetLoadHandle) → void` — `ui-toolkit-shell/Runtime/AssetLoading/IAsyncAssetLoader.cs:30`

### F-3. タブのスコープごと一括解放したい

タブを閉じる時にそのタブが取っていた全アセットを一気に解放する。

- `ReleaseAll(scopeId: AssetScopeId) → void` — `ui-toolkit-shell/Runtime/AssetLoading/IAsyncAssetLoader.cs:32`

### F-4. いまロード中のアセット一覧を見たい

リーク調査やデバッグ用。

- `GetSnapshot() → AssetLoaderSnapshot` — `ui-toolkit-shell/Runtime/AssetLoading/IAsyncAssetLoader.cs:34`

---

## G. IPC バス本体で送受信したい

`ICoreIpcBus` はプロセス内・プロセス間を通したメッセージ通信の本丸。**状態の発行・イベント発行・要求応答・購読**の 4 種類を扱う。タブからは普通 §H の UI 用ファサード経由で触り、アダプタ側はこちらを直接使う。

### G-1. 状態（State）を発行したい

「いまのスロット 0 のアバターはこれ」みたいな**最新値の押し付け**。後から購読した人にも残っている最新の値が届く（latched）。

- `PublishState<TPayload>(topic: string, payload: TPayload) → IpcResult` — `core-ipc-foundation/Runtime/Abstractions/ICoreIpcBus.cs:10`

### G-2. イベント（Event）を 1 回送りたい

「いまボタンを押した」「いまエラーが出た」みたいな**一過性の合図**。後から購読した人には届かない（残らない）。

- `PublishEvent<TPayload>(topic: string, payload: TPayload) → IpcResult` — `core-ipc-foundation/Runtime/Abstractions/ICoreIpcBus.cs:12`

### G-3. リクエストを送って結果を待ちたい

要求 → 応答型。返事が返ってくるまで非同期に待つ。タイムアウトやキャンセル可。

- `RequestAsync<TReq, TRes>(topic: string, payload: TReq, options?: RequestOptions, ct?: CancellationToken) → Task<IpcResult<TRes>>` — `core-ipc-foundation/Runtime/Abstractions/ICoreIpcBus.cs:14`

### G-4. 状態を購読したい

「対象 topic に値が来たらこの関数を呼んで」と登録する。登録直後に最新値も一度配ってくれる。

- `SubscribeState<TPayload>(topic: string, handler: Action<MessageEnvelope<TPayload>>) → ISubscriptionToken` — `core-ipc-foundation/Runtime/Abstractions/ICoreIpcBus.cs:20`

### G-5. イベントを購読したい

- `SubscribeEvent<TPayload>(topic: string, handler: Action<MessageEnvelope<TPayload>>) → ISubscriptionToken` — `core-ipc-foundation/Runtime/Abstractions/ICoreIpcBus.cs:24`

### G-6. リクエストの応答ハンドラを登録したい

`RequestAsync` で来た要求を受け取って結果を返す側。アダプタ側で 1 度だけ登録する。

- `RegisterRequestHandler<TReq, TRes>(topic: string, handler: Func<TReq, CancellationToken, Task<TRes>>) → ISubscriptionToken` — `core-ipc-foundation/Runtime/Abstractions/ICoreIpcBus.cs:28`

### G-7. 接続診断にアクセスしたい

いま繋がっているか、再接続中か、などの状態と履歴。

- `Diagnostics: IConnectionDiagnostics` — `core-ipc-foundation/Runtime/Abstractions/ICoreIpcBus.cs:32`
- `CurrentState: ConnectionState`（`Disconnected | Connecting | Connected | Reconnecting | PermanentlyDisconnected`） — `core-ipc-foundation/Runtime/Abstractions/IConnectionDiagnostics.cs:8`
- `ConnectionStateChanged: Action<ConnectionStateChangedEventArgs>` — `core-ipc-foundation/Runtime/Abstractions/IConnectionDiagnostics.cs:20`
- `TakeSnapshot() → DiagnosticsSnapshot` — `core-ipc-foundation/Runtime/Abstractions/IConnectionDiagnostics.cs:22`

---

## H. タブから IPC を送受信したい（UI 側ファサード）

タブ実装側は **`IUiCommandClient` と `IUiSubscriptionClient`** を使う。バスへの直接アクセスはせず、メッセージサイズ超過や未接続を `SendResult` で受け取れる安全な窓口になっている。

### H-1. UI から状態を発行したい

例: スライダを動かしたら「light/{id}/intensity」topic に新しい値を流す。

- `PublishState<TPayload>(topic: string, payload: TPayload) → SendResult` — `ui-toolkit-shell/Runtime/Commands/IUiCommandClient.cs:18`

### H-2. UI からイベントを送りたい

例: ボタンを押したら「camera/command」topic に追加コマンドを 1 回流す。

- `PublishEvent<TPayload>(topic: string, payload: TPayload) → SendResult` — `ui-toolkit-shell/Runtime/Commands/IUiCommandClient.cs:20`

### H-3. UI からリクエストを送って答えを待ちたい

例: 設定スキーマや Volume スキーマを問い合わせて、その内容で動的に UI を組み立てる。

- `RequestAsync<TReq, TRes>(topic: string, payload: TReq, options?: RequestOptions, ct?: CancellationToken) → Task<RequestResult<TRes>>` — `ui-toolkit-shell/Runtime/Commands/IUiCommandClient.cs:22`

### H-4. UI から購読したい

`MessageKind`（State / Event）を引数で渡せるので、購読対象を 1 メソッドで指定できる。

- `Subscribe<TPayload>(topic: string, kind: MessageKind, handler: Action<MessageEnvelope<TPayload>>) → ISubscriptionToken` — `ui-toolkit-shell/Runtime/Commands/IUiSubscriptionClient.cs:15`

### H-5. 接続状態 UI を出したい

「接続中…」「再接続中」「切断」など、画面端のバッジに繋ぐ。

- `IsConnected: bool` — `ui-toolkit-shell/Runtime/Commands/IConnectionStatus.cs:15`
- `CurrentStatus: ConnectionStatusCode`（`Initializing | Connecting | Connected | Disconnected | Reconnecting | FailedPermanently`） — `ui-toolkit-shell/Runtime/Commands/IConnectionStatus.cs:17`
- `OnStatusChanged: Action<ConnectionStatusChangedEventArgs>` — `ui-toolkit-shell/Runtime/Commands/IConnectionStatus.cs:19`

### H-6. 送信失敗のエラーコードを判定したい

`SendResult.Error` / `RequestResult.Error` の中身。

- `SendResult { Success: bool, Error: SendError? }` — `ui-toolkit-shell/Runtime/Commands/SendResult.cs:20`
- `RequestResult<T> { Success: bool, Value: T?, Error: RequestError? }` — `ui-toolkit-shell/Runtime/Commands/RequestResult.cs:21`
- `enum SendErrorCode { NotConnected, PayloadTooLarge, SerializationFailed, TopicInvalid, ShellNotRunning }` — `ui-toolkit-shell/Runtime/Commands/SendResult.cs:67-74`
- `enum RequestErrorCode { /* SendErrorCode の 5 種 */ + Timeout, Cancelled }` — `ui-toolkit-shell/Runtime/Commands/RequestResult.cs:66-75`

---

## I. IPC のメッセージや結果の形を知りたい

### I-1. メッセージ本体（Envelope）

すべての IPC メッセージはこの封筒の中に Payload が入った構造で流れる。

```
MessageEnvelope {
    ProtocolVersion: int,
    Kind: MessageKind,
    Topic: string,
    CorrelationId: string?,
    TimestampUnixMs: long,
    Payload: JsonElement
}
```

— `core-ipc-foundation/Runtime/Abstractions/MessageEnvelope.cs`

### I-2. メッセージの種類

- `enum MessageKind { State = 0, Event = 1, Request = 2, Response = 3 }` — `core-ipc-foundation/Runtime/Abstractions/MessageKind.cs`

### I-3. 送受信の結果型

成功 / 失敗の判定と、失敗時のエラー詳細を持つ。

- `IpcResult { Success: bool, Error: CoreIpcError? }` — `core-ipc-foundation/Runtime/Abstractions/Results/IpcResult.cs`
- `IpcResult<T> { Success: bool, Value: T?, Error: CoreIpcError? }` — `core-ipc-foundation/Runtime/Abstractions/Results/IpcResult.cs`

---

## J. IPC ランタイムを起動・設定したい

`CoreIpcRuntime` がプロセス全体に 1 つだけ存在する IPC のホスト。WebSocket サーバ起動や設定 JSON の読み込みもここでやる。

### J-1. プロセス内シングルトンを取りたい

- `Current: ICoreIpcRuntime?` — `core-ipc-foundation/Runtime/Core/CoreIpcRuntime.cs:13`

### J-2. バスを取り出したい（初期化完了後）

- `Bus: ICoreIpcBus` — `core-ipc-foundation/Runtime/Core/CoreIpcRuntimeHost.cs:77`

### J-3. 自動起動を抑制したい（テスト用）

ユニットテストで「シーン読み込み時に勝手にランタイムが立ち上がる」のを止める。

- `DisableAutoBootstrap() → void` — `core-ipc-foundation/Runtime/Core/Lifecycle/RuntimeBootstrap.cs:39`

### J-4. 手動で Bootstrap したい

設定ローダーやランタイム生成 Factory を自前で組み立てて起動する。戻り値は `(ICoreIpcRuntime, 初期化 Task)`。

- `Bootstrap(optionsLoader: Func<CoreIpcOptions>, runtimeFactory: Func<CoreIpcOptions, ICoreIpcRuntime>, ...) → (ICoreIpcRuntime, Task)` — `core-ipc-foundation/Runtime/Core/Lifecycle/RuntimeBootstrap.cs:69`

### J-5. 起動完了を待ちたい

- `IsBootstrapped: bool` — `core-ipc-foundation/Runtime/Core/Lifecycle/RuntimeBootstrap.cs:20`
- `LastInitializationTask: Task?` — `core-ipc-foundation/Runtime/Core/Lifecycle/RuntimeBootstrap.cs:25`
- `InitializeAsync(options: CoreIpcOptions, ct?: CancellationToken) → Task`（低レベル直接版） — `core-ipc-foundation/Runtime/Core/CoreIpcRuntimeHost.cs:91`

### J-6. ランタイム状態を見たい

- `State: RuntimeState`（`NotInitialized | Initializing | Running | ShuttingDown | Disposed`） — `core-ipc-foundation/Runtime/Core/CoreIpcRuntimeHost.cs:67`
- `Options: CoreIpcOptions`（いま使われている設定） — `core-ipc-foundation/Runtime/Core/CoreIpcRuntimeHost.cs:72`

### J-7. 接続パラメータを知りたい（CoreIpcOptions の主要項目）

`Abstractions/CoreIpcOptions.cs:6-27` の record。代表値:

- `Host: string = "127.0.0.1"`、`Port: int = 61874`
- `DefaultRequestTimeout: TimeSpan = 5s`
- `ReconnectInitialDelay = 250ms`、`ReconnectMultiplier = 2.0`、`ReconnectMaxDelay = 5s`、`ReconnectMaxAttempts = 20`
- `MaxMessageSizeBytes = 1MB`
- `EventQueueWarningThresholdPerTopic = 1000`
- `LogLevel`: `Trace | Debug | Info | Warning | Error`

### J-8. 設定 JSON の読み込み順を知りたい

設定はこの順番で読まれ、最後に見つかったものが採用される（後勝ち）。

1. `Resources/CoreIpcConfig`（JSON、Unity の Resources）
2. `StreamingAssets/core-ipc-config.json`
3. `%AppData%/VTuberSystemBase/core-ipc-config.json`

— `core-ipc-foundation/Runtime/Core/Configuration/CoreIpcConfigLoader.cs:29`

### J-9. トランスポートを選びたい

- ループバック（同一プロセス・テスト用） — `core-ipc-foundation/Runtime/Core/Transport/Loopback/InMemoryLoopbackTransport.cs:11`
- WebSocket（本番） — `core-ipc-foundation/Runtime/Core/Transport/WebSocket/WebSocketTransportAdapter.cs`

---

## K. 出力側（Display 2+）のシーンをセットアップしたい

Display 2 以降に出る「本番映像」側のホストが `OutputSceneBootstrapper`。カメラ・ライト・ボリュームの土台を作り、表示先 Display へルーティングする。

### K-1. SerializeField で挙動を決めたい

| Field | 型 | 既定 | 用途 |
| --- | --- | --- | --- |
| `_targetDisplayIndex` | `int` | 1 | 表示する Display 番号（0 = メイン、1 以降 = サブモニタ） |
| `_fullScreenMode` | `FullScreenMode` | `FullScreenWindow` | フルスクリーン挙動 |
| `_suppressEditorWarning` | `bool` | `false` | Editor で出る警告を消す |
| `_routingProvider` | `DisplayRoutingProvider` | `BuiltIn` | 描画経路の選択（BuiltIn か RuntimeDisplaySelector） |
| `_spoutSenderName` | `string` | (任意) | Spout 出力名（RDS 経路のみ意味あり） |
| `_autoStart` | `bool` | `true` | シーン読込で自動起動するか |
| `_minLogLevel` | `LogLevel` | `Info` | ログ出力の閾値 |

— `output-renderer-shell/Runtime/Scene/OutputSceneBootstrapper.cs:50-76`

### K-2. 出力シーンの公開メンバを取りたい

- `Diagnostics: IOutputDiagnostics?` — `output-renderer-shell/Runtime/Scene/OutputSceneBootstrapper.cs:150`
- `Dispatcher: IOutputCommandDispatcher?` — `output-renderer-shell/Runtime/Scene/OutputSceneBootstrapper.cs:156`
- `Roots: IOutputSceneRoots?` — `output-renderer-shell/Runtime/Scene/OutputSceneBootstrapper.cs:162`
- `RoutingProvider: DisplayRoutingProvider`（読取） — `output-renderer-shell/Runtime/Scene/OutputSceneBootstrapper.cs:133`
- `AutoStart: bool`（読取） — `output-renderer-shell/Runtime/Scene/OutputSceneBootstrapper.cs:139`
- `IsSelfDestroyed: bool` — `output-renderer-shell/Runtime/Scene/OutputSceneBootstrapper.cs:144`

### K-3. ルーティング / IPC バスを差し替えたい（テスト用）

- `OverrideServices(routing?: IDisplayRoutingService, ipcBus?: ICoreIpcBus) → void` — `output-renderer-shell/Runtime/Scene/OutputSceneBootstrapper.cs:111`
- `BuildRoutingConfig() → DisplayRoutingConfig` — `output-renderer-shell/Runtime/Scene/OutputSceneBootstrapper.cs:121`

### K-4. シーン初期化フェーズを監視したい

どの段階まで進んでいるか / 失敗していないか。

- `CurrentPhase: OutputSceneInitPhase` — `output-renderer-shell/Runtime/Abstractions/IOutputDiagnostics.cs:31`
- `CurrentDisplayAssignment: DisplayAssignment` — `output-renderer-shell/Runtime/Abstractions/IOutputDiagnostics.cs:37`
- `RegisteredHandlerCount: int` — `output-renderer-shell/Runtime/Abstractions/IOutputDiagnostics.cs:43`
- `LastErrorMessage: string?` — `output-renderer-shell/Runtime/Abstractions/IOutputDiagnostics.cs:49`
- `LastErrorAtUnixMs: long?` — `output-renderer-shell/Runtime/Abstractions/IOutputDiagnostics.cs:54`
- `enum OutputSceneInitPhase { Uninitialized=0, RootsCreated=1, CameraReady=2, LightReady=3, VolumeReady=4, IpcServerReady=5, DispatcherReady=6, DisplayRouted=7, Complete=8, Failed=99 }` — `output-renderer-shell/Runtime/Abstractions/OutputSceneInitPhase.cs:16-45`
- `enum DisplayRoutingProvider { BuiltIn=0, RuntimeDisplaySelector=1 }` — `output-renderer-shell/Runtime/Abstractions/DisplayRoutingProvider.cs:20-33`

---

## L. 各アダプタを起動・差し替えたい

アダプタ = Display 2 側で UI からの IPC を実際に Unity の Camera / Light / Volume に当てる仕事をするレイヤ。3 タブそれぞれに対応した 3 種類がいる。

### L-1. RAC（Character）アダプタを起動したい

`RacMainOutputAdapterHost` は MonoBehaviour。`[DefaultExecutionOrder(100)]` のついた `Start` で同期初期化する。

- SerializeField: `_outputSceneBootstrapper` — `rac-main-output-adapter/Runtime/Bootstrapper/RacMainOutputAdapterHost.cs:34`
- SerializeField: `_coreIpcBusProviderBehaviour: MonoBehaviour`（`ICoreIpcBusProvider` 実装を持たせる） — `rac-main-output-adapter/Runtime/Bootstrapper/RacMainOutputAdapterHost.cs:39`
- SerializeField: `_minLogLevel: AdapterLogLevel` — `rac-main-output-adapter/Runtime/Bootstrapper/RacMainOutputAdapterHost.cs:44`
- `Bootstrapper: RacMainOutputAdapterBootstrapper` — `rac-main-output-adapter/Runtime/Bootstrapper/RacMainOutputAdapterHost.cs:51`
- `OverrideMessageSink(sink: IAdapterMessageSink) → void` — `rac-main-output-adapter/Runtime/Bootstrapper/RacMainOutputAdapterHost.cs:57`

Bootstrapper 本体:

- `Diagnostics: IRacMainOutputAdapterDiagnostics` — `rac-main-output-adapter/Runtime/Bootstrapper/RacMainOutputAdapterBootstrapper.cs:79`
- `IsRunning: bool` — `rac-main-output-adapter/Runtime/Bootstrapper/RacMainOutputAdapterBootstrapper.cs:82`
- `OverrideServices(dispatcher?: ..., sceneRoots?: ..., messageSink?: ..., keyResolver?: ..., schemaProvider?: ..., settingsAdapter?: ..., mocapFactory?: ..., clock?: IClock, logger?: ILogger) → void` — `rac-main-output-adapter/Runtime/Bootstrapper/RacMainOutputAdapterBootstrapper.cs:56`
- `Initialize() → void` — `rac-main-output-adapter/Runtime/Bootstrapper/RacMainOutputAdapterBootstrapper.cs:87`
- `Shutdown() → void` — `rac-main-output-adapter/Runtime/Bootstrapper/RacMainOutputAdapterBootstrapper.cs:182`

### L-2. ICoreIpcBusProvider を実装したい

3 アダプタが共通でバス取得のために要求する interface。`CoreIpcRuntime.Current.Bus` を返す MonoBehaviour を別途用意する。

- `CoreIpcBus: ICoreIpcBus`（読取） — `rac-main-output-adapter/Runtime/Bootstrapper/RacMainOutputAdapterHost.cs:154-162`

### L-3. Stage アダプタを起動したい

- SerializeField: `_autoStart: bool = true` — `stage-lighting-volume-output-adapter/Runtime/Bootstrap/StageLightingVolumeOutputAdapterBootstrapper.cs:23`
- SerializeField: `_outputSceneBootstrapper` — `stage-lighting-volume-output-adapter/Runtime/Bootstrap/StageLightingVolumeOutputAdapterBootstrapper.cs:24`
- `Diagnostics: IStageLightingVolumeOutputAdapterDiagnostics` — `stage-lighting-volume-output-adapter/Runtime/Bootstrap/StageLightingVolumeOutputAdapterBootstrapper.cs:38`
- `TryStart() → void`（依存未準備時は何もしない） — `stage-lighting-volume-output-adapter/Runtime/Bootstrap/StageLightingVolumeOutputAdapterBootstrapper.cs:51`

### L-4. Camera アダプタを起動したい

- SerializeField: `_config: CameraSwitcherOutputAdapterConfig?` — `camera-switcher-output-adapter/Runtime/CameraSwitcherOutputAdapterBootstrapper.cs:28`
- SerializeField: `_autoStart: bool` — `camera-switcher-output-adapter/Runtime/CameraSwitcherOutputAdapterBootstrapper.cs:31`
- `Adapter: CameraSwitcherOutputAdapterCore?` — `camera-switcher-output-adapter/Runtime/CameraSwitcherOutputAdapterBootstrapper.cs:39`
- `Diagnostics: ICameraSwitcherOutputAdapterDiagnostics?` — `camera-switcher-output-adapter/Runtime/CameraSwitcherOutputAdapterBootstrapper.cs:40`
- `InjectForTesting(bus: ICoreIpcBus, dispatcher: IOutputCommandDispatcher, sceneRoots: IOutputSceneRoots) → void` — `camera-switcher-output-adapter/Runtime/CameraSwitcherOutputAdapterBootstrapper.cs:47`

---

## M. Character タブで操作したい

Character タブはプレイヤースロットへのアバター割当・設定変更・プリセット操作を担当する。「スロット」= キャラ枠（複数人配信時に 1 人 1 枠）。

### M-1. Character タブを Bootstrap したい

タブ全体の組み立て。テンプレ UXML を 3 種類渡せる箇所が特徴（プレイヤーカード、アバター項目、プリセットバー）。

```
new CharacterTabBootstrapper(
    tabHandle: ITabLifecycleHandle,
    commandClient: IUiCommandClient,
    subscriptionClient: IUiSubscriptionClient,
    connectionStatus: IConnectionStatus,
    assetLoader: IAsyncAssetLoader,
    logger: IDiagnosticsLogger?,
    presetStorage: IPresetStorage,
    clock: IClock,
    tabRoot: VisualElement,
    thumbnailResolverOverride: IAvatarThumbnailResolver? = null,
    configOverride: CharacterTabConfig? = null,
    playerCardTemplate: VisualTreeAsset? = null,
    avatarItemTemplate: VisualTreeAsset? = null,
    presetBarTemplate: VisualTreeAsset? = null)
```

— `character-selection-tab/Runtime/Bootstrap/CharacterTabBootstrapper.cs:56-146`

### M-2. スロットにアバターを割り当てたい（解除も）

`AvatarKey` に `null` を入れるとそのスロットを空にする（アバターを外す）。

- ↑ state `slot/{slotId}/assignment` / `SlotAssignmentPayload { AvatarKey: string? }` — `character-selection-tab/Runtime/Ipc/CharacterTabIpcBinder.cs`

### M-3. スロットの設定値（パラメータ）を変えたい

アバターごとに公開されているパラメータ（眉の高さ等）を 1 値ずつ送る。

- ↑ state `slot/{slotId}/settings/{settingKey}` / `SlotSettingValuePayload`

### M-4. スロットをリセット / リロード / プリセット適用したい

3 種類のスロット操作を 1 つの topic に「Kind」で振り分けて送る。

- ↑ event `slot/{slotId}/command` / `SlotCommandPayload { Kind, Argument? }`
  - `Kind` = `Reset | Reload | PresetApply`

### M-5. アバターの設定スキーマを取りたい

「このアバターはどんな項目を持っているか」を問い合わせる。返答で UI の設定パネルを動的に組み立てる。

- ↑ request `avatars/{avatarKey}/schema` → `AvatarSettingsSchemaPayload`

### M-6. スロット一覧・アバター一覧（カタログ）を表示したい

アダプタ側から流れてくる「いま使えるスロットの並び」「Addressables に乗っているアバターのリスト」。

- ↓ state `slots/catalog` / `SlotCatalogPayload`
- ↓ state `avatars/catalog` / `AvatarCatalogPayload`

### M-7. スロットの状態・エラーを画面に出したい

スロットが空か、アサイン中か、エラーか、を色や文字で表示するための情報源。

- ↓ state `slot/{slotId}/assignment` / `SlotAssignmentPayload`（確認返し）
- ↓ state `slot/{slotId}/status` / `SlotStatusPayload { Status: Empty | Assigning | Assigned | Error, Detail? }`
- ↓ event `slot/{slotId}/error` / `SlotErrorPayload { ErrorCode, Detail? }`

### M-8. プリセットを作る・名前変更・複製・削除・適用したい

プリセットバー UI からの操作。Presenter が直接 preset bar の要素群を触る。

- `SlotListPresenter.OnSlotSelected(slotId: int) → void`
- `SlotListPresenter.OnSettingsRequested(slotId: int) → void`
- `SlotListPresenter.OnResetRequested() → void`
- `SlotListPresenter.OnReloadRequested() → void`
- `AvatarCatalogPresenter.OnAvatarClicked(avatarKey: string) → void`
- `PresetManagerPresenter`（preset-bar 要素群を直接操作）
- `SettingsPanelPresenter.OpenForAsync(slotId: int) → Task`
- `AssignmentFlowPresenter.SelectSlot(slotId: int) / RequestAssignment(...) / RequestOperation(...)`

— `character-selection-tab/Runtime/Presenters/*`

### M-9. 必須 UXML region を満たしたい

`*View/ViewQueryHelpers.cs` が `Q` で要求する name。これらが UXML に揃っていないと起動失敗する。

| name | 型 | 中で何が起きるか |
| --- | --- | --- |
| `vsb-char-tab__preset-bar` | `VisualElement` | プリセットの作成・名前変更・適用ボタン群が入る |
| `vsb-char-tab__player-cards` | `VisualElement` | スロット毎にプレイヤーカードを並べる縦リスト |
| `vsb-char-tab__avatar-catalog` | `VisualElement` | 使えるアバターをサムネ付きで一覧表示する |
| `vsb-char-tab__settings-panel` | `VisualElement` | 選択中スロットの設定スライダ群 |
| `vsb-char-tab__diagnostics` | `VisualElement` | 接続中・切断などのバッジ・エラー表示 |

preset-bar の中（テンプレ展開後）にこれらが必要:

| name | 型 |
| --- | --- |
| `vsb-preset-bar__active` | `Label` |
| `vsb-preset-bar__dropdown` | `DropdownField` |
| `vsb-preset-bar__name-input` | `TextField` |
| `vsb-preset-bar__create-btn` | `Button` |
| `vsb-preset-bar__rename-btn` | `Button` |
| `vsb-preset-bar__duplicate-btn` | `Button` |
| `vsb-preset-bar__delete-btn` | `Button` |
| `vsb-preset-bar__activate-btn` | `Button` |
| `vsb-preset-bar__error` | `Label` |

### M-10. サービスを差し替えたい（テスト・モック）

- `IPresetStorage.LoadAllAsync() / SaveAsync(...) / DeleteAsync(...) / SetActiveAsync(...)` — `character-selection-tab/Runtime/Services/IPresetStorage.cs:13-21`（既定 `JsonPresetStorage`）
- `IClock`（時刻・遅延供給） — `character-selection-tab/Runtime/Services/IClock.cs`
- `IAvatarThumbnailResolver`（サムネ供給）

---

## N. Stage / Light / Volume タブで操作したい

ステージ（背景）・ライト・ポストプロセスボリュームをまとめて触るタブ。Stage タブだけ命名規約が他と違って **接頭辞なし**（`preset-section` 等そのまま）なので注意。

### N-1. Stage タブを Bootstrap したい

```
new StageLightingVolumeTabBootstrapper(
    registry: ITabPanelRegistry,
    tabRoot: VisualElement,
    commandClient: IUiCommandClient,
    subscriptionClient: IUiSubscriptionClient,
    assetLoader: IAsyncAssetLoader,
    connectionStatus: IConnectionStatus,
    logger: IDiagnosticsLogger?,
    presetStorage: IPresetStorage,
    previewAccessor: IPreviewRenderTextureAccessor,
    previewCameraAdapter: IPreviewCameraAdapter,
    clock: IClock)
```

— `stage-lighting-volume-tab/Runtime/Bootstrap/StageLightingVolumeTabBootstrapper.cs:47-119`

> Stage タブだけは `RegisterTab` を Bootstrapper の内部で呼ぶ実装（`IntegratedTabMountStrategy.cs:175-181` の注釈参照）。

### N-2. ステージを読み込みたい / 解除したい

`Op` で「load / unload」を切り替える。`AddressableKey` は load 時のみ必須。

- ↑ event `stage/command` / `StageCommandDto { Op: "load" | "unload", AddressableKey: string? }`

### N-3. ステージ一覧 / 現在のステージを受け取りたい

- ↓ state `stage/catalog` / `StageCatalogDto`（使えるステージ一覧）
- ↓ state `stage/current` / `StageCurrentDto`（いま読み込まれているステージ）
- ↓ event `stage/loaded` / `StageCurrentDto`（読込完了の合図）
- ↓ event `stage/load-failed` / `StageLoadFailedDto`（読込失敗の合図）

### N-4. ライトを追加・削除したい

- ↑ event `light/command` / `LightCommandDto { Op: "add" | "remove", LightId: string? }`
- ↓ state `lights/list` / `LightListDto`（いま生きているライトの並び）
- ↓ event `light/added` / `LightAddedDto`（追加完了通知）
- ↓ event `light/error` / `LightErrorDto`

### N-5. ライトのパラメータを変えたい

それぞれ独立した topic に値を流す。プレビューに即時反映させる用。

- ↑ state `light/{lightId}/intensity` / `float`（明るさ）
- ↑ state `light/{lightId}/color` / `ColorDto`（RGB）
- ↑ state `light/{lightId}/rotation` / `Vector3Dto`（向き）
- ↑ state `light/{lightId}/type` / `LightTypeDto`（種類: Directional / Spot / Point など）
- ↑ state `light/{lightId}/range` / `float`（光が届く距離）
- ↑ state `light/{lightId}/spotAngle` / `float`（スポット角）
- ↑ state `light/{lightId}/displayName` / `string`（表示名）

### N-6. ボリューム override を編集したい

ポストプロセスの各効果をオン / オフ、各パラメータを上書きする。

- ↑ state `volume/override/{typeFullName}/enabled` / `bool`（その効果を ON/OFF）
- ↑ state `volume/override/{typeFullName}/{paramName}` / `VolumeOverrideParamValueDto`（個別の値）
- ↑ request `volume/override/schema` → `VolumeOverrideSchemaDto`（どんなパラメータがあるか問い合わせ）
- ↑ event `volume/command`（予約・未使用）

### N-7. プレビューテクスチャを表示したい

ステージとライティングの結果を縮小レンダーした RenderTexture を画面に出す。

- ↓ state `preview/state` / `PreviewStateDto`
- `IPreviewRenderTextureAccessor.IsReady: bool` / `TryGet(out RenderTexture) → bool` / `RenderTextureChanged: Action` — `stage-lighting-volume-tab/Runtime/Preview/IPreviewRenderTextureAccessor.cs:13-29`
- `IPreviewCameraAdapter.IsAvailable: bool` / `ResetToDefaultView() → void` / `OnAvailabilityChanged: Action<bool>` — `stage-lighting-volume-tab/Runtime/Preview/IPreviewCameraAdapter.cs:13-34`

### N-8. ViewModel / View / 警告コード

タブ内の状態を 1 か所に集めた箱。**この単位で UI を書き直すと既存の警告系処理がそのまま使える**。

- Observables: `Presets / ActivePresetName / StageCurrent / StageCatalog / Lights / SelectedLightId / VolumeSchema / VolumeOverrideEnabled / IsConnected` — `stage-lighting-volume-tab/Runtime/ViewModel/StageLightingVolumeTabViewModel.cs:30-120`
- Events: `OnStateChanged / OnValidationError / OnOperationWarning`
- 警告コード: `WarnIpcDisconnected / WarnStageLoadFailed / WarnStageInProgress / WarnStageUnresolved / WarnLightAddFailed / WarnLightAddTimeout / WarnVolumeSchemaFailed`

View セクション（UXML を組み替える時の単位）:

- `StagePresetSectionView`
- `StageSelectionSectionView`
- `LightListSectionView`
- `LightPropertyEditorView`
- `VolumeOverrideSectionView`
- `PreviewPanelController`

### N-9. 必須 UXML region を満たしたい

`View/StageLightingVolumeTabPanel.cs:27-34` の Query 対象。

| name | 型 | 中で何が起きるか |
| --- | --- | --- |
| `preview-panel` | `VisualElement` | プレビュー RenderTexture を表示 |
| `preset-section` | `VisualElement` | プリセット CRUD ボタン群 |
| `stage-selection-section` | `VisualElement` | ステージ選択リスト |
| `light-list-section` | `VisualElement` | ライト一覧（追加・選択） |
| `light-editor-section` | `VisualElement` | 選択ライトのパラメータ編集 |
| `volume-override-section` | `VisualElement` | ボリューム override の編集 |
| `active-preset-label` | `Label`（任意） | アクティブプリセット名表示 |

`preset-section` 内の必要名:

- ボタン: `preset-create / preset-rename / preset-duplicate / preset-delete / preset-activate`
- `preset-list: VisualElement`
- `stage-list: VisualElement`（`stage-selection-section` 内）
- `light-list: VisualElement`（`light-list-section` 内）
- `volume-override-list: VisualElement`（`volume-override-section` 内）

### N-10. サービスを差し替えたい（テスト・モック）

- `IPresetStorage.LoadAsync() → PresetLoadResult` / `SaveAsync(...) / FlushAsync()` — `stage-lighting-volume-tab/Runtime/Services/IPresetStorage.cs:16-39`（既定 `JsonPresetStorage`）
- `IClock.UtcNow: DateTime` / `Delay(ms: int, ct: CancellationToken) → Task` — `stage-lighting-volume-tab/Runtime/Services/IClock.cs:13-18`
- `IPreviewRenderTextureAccessor` — §N-7
- `IPreviewCameraAdapter` — §N-7

---

## O. Camera タブで操作したい

カメラの追加 / 削除 / 切替 / プレビュー / プリセット / Volume 編集を担当。**OSC で毎フレーム送る経路** だけ IPC とは別ライン。

### O-1. Camera タブを Bootstrap したい

```
new CameraSwitcherTabBootstrapper(
    tabHandle: ITabLifecycleHandle,
    commandClient: IUiCommandClient,
    subscriptionClient: IUiSubscriptionClient,
    connectionStatus: IConnectionStatus,
    assetLoader: IAsyncAssetLoader,
    logger: IDiagnosticsLogger?,
    tabRoot: VisualElement,
    oscHost: string? = null,        // 既定 "127.0.0.1"
    oscPort: int? = null,           // 既定 9000
    presetFilePath: string? = null, // 既定 persistentDataPath/camera-presets.json
    previewResolverOverride: RenderTextureHandleResolver? = null)
```

— `camera-switcher-tab/Runtime/Bootstrap/CameraSwitcherTabBootstrapper.cs:65-154`

### O-2. カメラを追加・削除・アクティブ化したい

1 つの topic に「Op」で 3 種類を振り分ける。`ClientRequestId` は応答との突き合わせ ID。

- ↑ event `camera/command` / `CameraCommandPayload { Op: "add" | "delete" | "active-set", CameraId: string?, Type: string?, DisplayName: string?, ClientRequestId: string }`

### O-3. 追加結果・エラー・一覧を受け取りたい

- ↓ event `camera/created` / `CameraCreatedEventPayload { CameraId, ClientRequestId }`
- ↓ event `camera/error` / `CameraErrorEventPayload { Code, Detail?, ClientRequestId? }`
- ↓ state `cameras/list` / `CameraListPayload`
- ↓ state `cameras/active` / `CamerasActiveStatePayload { CameraId? }`

### O-4. カメラのメタデータ（名前等）を書き換えたい

- ↑ state `camera/{cameraId}/metadata/{key}` / `CameraMetadataStatePayload`
- ↓ state `camera/{cameraId}/metadata/{key}` / `CameraMetadataStatePayload`（確認返し）

### O-5. カメラ単位の Volume を編集したい

- ↑ event `camera/{cameraId}/volume/command` / `VolumeCommandPayload`
- ↑ state `camera/{cameraId}/volume/enabled` / `VolumeEnabledStatePayload`
- ↑ state `camera/{cameraId}/volume/override/{type}/enabled` / `VolumeOverrideEnabledStatePayload`
- ↑ state `camera/{cameraId}/volume/override/{type}/{param}` / `VolumeOverrideParamStatePayload`
- ↑ request `camera/{cameraId}/volume/overrides/metadata` → `VolumeMetadataPayloads`（スキーマ取得）
- ↓ state `camera/{cameraId}/volume/overrides` / `VolumeOverridesStatePayload`（適用後の全体状態）

### O-6. プリセットを CRUD したい

`Op` 文字列で操作種別を切り替える。

- ↑ event `camera/preset/command` / `PresetCommandPayload { Op: "create" | "delete" | "rename" | "duplicate" | "activate" }`
- ↓ state `camera/preset/list` または `preset/list` / `PresetListStatePayload`
- ↓ state `camera/preset/active` または `preset/active` / `PresetActiveStatePayload`

### O-7. プレビューを表示したい

マルチプレビュー / アクティブプレビューの 2 種類を扱う。

- ↑ event `camera/preview/command` / `PreviewCommandPayload { Op, CameraIds: string[], Size?: [w: int, h: int], Fps?: int }`
- ↓ state `camera/{cameraId}/preview/handle` / `PreviewHandleStatePayload { TextureHandle: int? }`

### O-8. OSC で毎フレームのカメラ制御を送りたい

IPC とは別経路で UDP に直接吐く。**1 フレーム 1 メッセージ**（LateUpdate 内、編集対象切替で送信先 ID を切り替え、delete で停止）。

- Address: `/ucapi/camera/{cameraId}/flat`
- Payload: **138 byte 固定**（10 byte header + 128 byte record / UCAPI4Unity FlatRecord）
- 送信元: `IUcapiOscEmitter` + `OscAddressBuilder`

### O-9. Coordinator を経由して操作したい

タブ内の状態と命令を集約した中間層。

- `Status: TabStatus`（タブ全体の状態 enum） — `camera-switcher-tab/Runtime/Domain/ICameraSwitcherCoordinator.cs:19-76`
- `EditingCameraId: string?`
- `ActiveCameraId: string?`
- `Cameras: IReadOnlyList<CameraMetadata>`
- `OnStateChanged: Action`
- Camera 操作: `RequestAddCamera(type: string, displayName: string) → void` / `RequestDeleteCamera(cameraId: string) → void` / `ActivateCamera(cameraId: string) → void` / `SelectEditTarget(cameraId: string) → void` / `UpdateCameraMetadata(...) → void`
- Volume: `AddVolumeOverride(...) / RemoveVolumeOverride(...) / SetVolumeOverrideEnabled(...) / SetVolumeOverrideParam(...) / SetVolumeEnabled(...)`
- Preset: `CreatePreset(name: string) / RenamePreset(...) / DuplicatePreset(...) / DeletePreset(...) / ActivatePreset(...)`
- Lifecycle: `OnTabActivated() → void` / `OnTabDeactivated() → void` / `FrameTick(editingCameraSnapshot?: CameraSnapshot) → void`

View（UXML を組み替える時の単位）:

- `CameraListView`（追加ボタン等）
- `LocalVolumeEditorView`
- `PresetPanelView`
- `DiagnosticsBadgeView`
- `PreviewPanelView`

### O-10. 必須 UXML region を満たしたい

`camera-switcher-tab/Runtime/View/ViewQueryHelpers.cs:15-20`。

| name | 型 | 中で何が起きるか |
| --- | --- | --- |
| `vsb-cam-tab__preview-active` | `VisualElement` | 現在 active カメラの単独プレビュー |
| `vsb-cam-tab__preview-multi` | `VisualElement` | 候補カメラを並べたマルチプレビュー |
| `vsb-cam-tab__camera-list` | `VisualElement` | カメラ一覧と CRUD ボタン |
| `vsb-cam-tab__volume-editor` | `VisualElement` | 選択カメラの Volume override 編集 |
| `vsb-cam-tab__preset-panel` | `VisualElement` | プリセット操作 |
| `vsb-cam-tab__diagnostics` | `VisualElement` | 接続診断バッジ |

### O-11. サービスを差し替えたい（テスト・モック）

- `IPresetStore.LoadAllAsync() → PresetLoadOutcome` / `SaveAllAsync(...) → Task` — `camera-switcher-tab/Runtime/Contracts/IPresetStore.cs:21-38`（既定 `FileSystemPresetStore`）
- `ITimeProvider.UtcNow: DateTime` / `MonotonicSeconds: double` / `OnTick: Action<double>` / `CreateDebounce(window: TimeSpan, action: Action) → IDisposable` — `camera-switcher-tab/Runtime/Contracts/ITimeProvider.cs:16-32`（既定 `UnityTimeProvider`）
- `IPreviewHandleResolver.ResolveAsync(textureKey: int) → Task<RenderTexture>` / `Release(textureKey: int) → void` — `camera-switcher-tab/Runtime/Contracts/IPreviewHandleResolver.cs:20-29`（既定 `RenderTextureHandleResolver`）
- `IUcapiOscEmitter` / `IUcapiFlatRecordSerializer`

---

## P. アダプタが受け取る Topic を一覧で確認したい

タブ → アダプタの押し付け（↑）と、アダプタ → タブの押し戻し（↓）の俯瞰。UI 設計で「何が送れて何が来るか」を一覧したいときの早見表。

### P-1. Character ↔ RAC

| 方向 | Kind | Topic | Payload | 何が起きるか |
| --- | --- | --- | --- | --- |
| ↑ | state | `slot/{id}/assignment` | `SlotAssignmentPayload { AvatarKey: string? }` | 指定スロットにアバターをセット（null で解除） |
| ↑ | state | `slot/{id}/settings/{key}` | `SlotSettingValuePayload` | スロットのパラメータを 1 件更新 |
| ↑ | event | `slot/{id}/command` | `SlotCommandPayload { Kind, Argument? }` | Reset / Reload / PresetApply の 3 操作 |
| ↑ | request | `avatars/{key}/schema` | → `AvatarSettingsSchemaPayload` | アバターの設定スキーマ問い合わせ |
| ↓ | state | `slots/catalog` | `SlotCatalogPayload` | 使えるスロットの並び |
| ↓ | state | `avatars/catalog` | `AvatarCatalogPayload` | Addressables 上のアバター一覧 |
| ↓ | state | `slot/{id}/status` | `SlotStatusPayload { Status, Detail? }` | スロットが Empty / Assigning / Assigned / Error |
| ↓ | event | `slot/{id}/error` | `SlotErrorPayload { ErrorCode, Detail? }` | アサイン中エラー通知 |

`RegistryLocator.ErrorChannel.Publish(SlotError(...))` から `slot/{id}/error{KeyNotFound}` + status Error が一度に流れる（`rac-main-output-adapter/Runtime/Bootstrapper/RacMainOutputAdapterBootstrapper.cs` 周辺）。

### P-2. Stage ↔ Stage Adapter

| 方向 | Kind | Topic | Payload | 何が起きるか |
| --- | --- | --- | --- | --- |
| ↑ | event | `stage/command` | `StageCommandDto { Op, AddressableKey? }` | ステージの load / unload |
| ↑ | event | `light/command` | `LightCommandDto { Op, LightId? }` | ライト追加 / 削除 |
| ↑ | state | `light/{id}/{intensity\|color\|rotation\|type\|range\|spotAngle\|displayName}` | 各 DTO | ライト 1 件のプロパティを更新 |
| ↑ | event | `volume/command` | (予約) | 将来用 |
| ↑ | state | `volume/override/{type}/enabled` | `bool` | ボリューム override の ON/OFF |
| ↑ | state | `volume/override/{type}/{param}` | `VolumeOverrideParamValueDto` | ボリューム override の値 |
| ↑ | request | `volume/override/schema` | → `VolumeOverrideSchemaDto` | スキーマ問い合わせ |
| ↓ | state | `stage/catalog`, `stage/current`, `lights/list`, `preview/state` | 各 DTO | 各種カタログ・現在状態・プレビュー |
| ↓ | event | `stage/loaded`, `stage/load-failed`, `light/added`, `light/error` | 各 DTO | 結果通知 |

### P-3. Camera ↔ Camera Adapter

| 方向 | Kind | Topic | Payload | 何が起きるか |
| --- | --- | --- | --- | --- |
| ↑ | event | `camera/command` | `CameraCommandPayload` | カメラの追加 / 削除 / アクティブ化 |
| ↑ | event | `camera/preview/command` | `PreviewCommandPayload` | プレビューの開始・停止・サイズ変更 |
| ↑ | event | `camera/preset/command` | `PresetCommandPayload` | プリセット CRUD |
| ↑ | event | `camera/{id}/volume/command` | `VolumeCommandPayload` | カメラ単位ボリューム命令 |
| ↑ | state | `camera/{id}/volume/enabled` | `VolumeEnabledStatePayload` | カメラ単位 Volume の ON/OFF |
| ↑ | state | `camera/{id}/volume/override/{type}/enabled` | state | override の ON/OFF |
| ↑ | state | `camera/{id}/volume/override/{type}/{param}` | state | override 値 |
| ↑ | request | `camera/{id}/volume/overrides/metadata` | → `VolumeMetadataPayloads` | スキーマ問い合わせ |
| ↓ | state | `cameras/list`, `cameras/active`, `preset/list`, `preset/active` | 各 DTO | カメラ・プリセット一覧と現在 |
| ↓ | state | `camera/{id}/metadata/{key}` | `CameraMetadataStatePayload` | メタデータの確認返し |
| ↓ | state | `camera/{id}/volume/overrides` | `VolumeOverridesStatePayload` | 適用後の全体状態 |
| ↓ | state | `camera/{id}/preview/handle` | `PreviewHandleStatePayload { TextureHandle: int? }` | プレビュー RT の取得口 |
| ↓ | event | `camera/created` | `CameraCreatedEventPayload` | 追加成功 |
| ↓ | event | `camera/error` | `CameraErrorEventPayload` | 操作失敗 |
| → | OSC | `/ucapi/camera/{id}/flat` | 138 byte FlatRecord（UDP, UI→Adapter） | 毎フレームのカメラ制御 |

### P-4. OSC 受信側の処理

アダプタ側で受けて URP Camera に当てるところ。

- Address: `/ucapi/camera/{cameraId}/flat`
- Payload: 138 byte（10 byte header + 128 byte record）
- `TryDecodeCameraId(address: string, prefix: string = "/ucapi/camera") → string?` — `camera-switcher-output-adapter/Runtime/Adapters/Ucapi/FlatRecordAddressDecoder.cs:36-64`
- `Ucapi4UnityFlatRecordApplier.Apply(cameraId: string, blob: ReadOnlySpan<byte>, camera: Camera) → void` — `camera-switcher-output-adapter/Runtime/Adapters/Ucapi/.../Ucapi4UnityFlatRecordApplier.cs:42-57`

---

## Q. integrated-demo の起動シーケンスを参照したい

新 UI を組む時に MainDemo シーンと同じ起動順を真似たい場合の読み始め。

- `IntegratedDemoBootstrap.cs` — MonoBehaviour 統合エントリ。`Awake` で Bus / Scene / Adapters を構築、`Start` で OutputScene の `Complete` を待ってから UI shell を起動、続けてタブ Bootstrapper を 3 個立ち上げる。
- `IntegratedDemoConfig.cs` — Inspector 設定:
  - `SkinProfile: UiToolkitShellSkinProfile?`（未設定なら UI 起動を skip）
  - `UiTargetDisplay: int = 0`
  - `CameraOscHost: string = "127.0.0.1"` / `CameraOscPort: int = 0`
  - `CameraPresetPath: string`
  - `AdapterStartupMaxFrames: int = 60`
- `IntegratedDemoUiShellHost.cs` — `Configure(config: IntegratedDemoConfig, bus: ICoreIpcBus) → void` → `UiShellLifecycleDriver.StartShell()` → `LaunchTabBootstrappers() → void` で 3 タブを構築。
- `IntegratedTabMountStrategy.cs` — タブ UXML を root に attach。**現実装は `vsb-tab-content` を使わず、shell-root の兄弟として attach** している（§R-3 参照）。
- `CoreIpcBusProvider.cs` — `CoreIpcRuntime.Current.Bus` を 3 アダプタに渡す `ICoreIpcBusProvider` 実装。

---

## R. UI 再設計時の落とし穴を確認したい

1. **タブ UXML のスタイリング**: Sample 同梱 UXML は region 名のみの足場。本格 UI を組むなら `UiToolkitShellSkinProfile` の `*TabVisualTreeAsset` を、§M-9 / §N-9 / §O-10 の必須 region を満たす独自 UXML + USS で差し替える。
2. **Skin 検証**: §C-3 の必須 USS クラス（`vsb-tab-bar` / `vsb-tab-root--*` 等）を満たしていないと shell の起動チェックで失敗する。新 UXML を作る時は必ず照合する。
3. **`vsb-tab-content` プレースホルダ**: 現在の `IntegratedTabMountStrategy` はタブを shell-root の直下に attach しており、Root UXML の `vsb-tab-content` プレースホルダは使われていない（`integrated-demo/Runtime/IntegratedTabMountStrategy.cs:87`）。UI 再設計のついでに mount 先を `vsb-tab-content` に寄せるか、placeholder を Root UXML から削除するかを決めると整理しやすい。
4. **テンプレート UXML**: Character タブは `playerCardTemplate / avatarItemTemplate / presetBarTemplate` をコンストラクタ引数で渡せる（§M-1）。preset bar の組み換えは UXML テンプレ差替で完結する。
5. **OSC は UI から直接吐く**: Camera タブだけは IPC とは別経路で OSC を出す（§O-8 / §P-4）。138 byte 固定バイナリで毎フレーム送るので、UI 側の rate-limit やデバウンスはタブ内の `IUcapiOscEmitter` が握っている。
6. **接続状態 UI**: 各タブの diagnostics region に `IConnectionStatus`（§H-5）を流すと、Disconnected / Reconnecting 時に CRUD を非活性化する既存ロジックと整合する（Character / Stage / Camera 3 タブとも対応済み）。
