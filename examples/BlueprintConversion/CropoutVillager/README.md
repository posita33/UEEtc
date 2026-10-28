# BP_Villager C++移植の試作

UE 5.8向けの独立したRuntimeプラグインです。`ACropoutVillagerPawn : APawn`が、解析したBP_Villagerの仕事・資源・アニメーション処理を持ちます。
自動変換器ではありません。**既存BPの差し替え・reparentはまだ行わず、Cropoutのコピーで比較してください。**
元のBlueprint専用インターフェース、Behavior Tree、GameInstanceのセーブ処理を含むゲーム全体の動作等価性は未検証です。

## ビルド

UEEtcのルートで次を実行します。VSのC++ツールとUE 5.8が必要です。

```powershell
./scripts/Test-CropoutVillagerPrototype.ps1
```

`artifacts/villager-validation/<実行ID>/`にパッケージ済みプラグイン、検証用HostProject、AutomationReportを作ります。
Editor/Win64 Developmentを検証します。Shipping・ゲームターゲット・パッケージングは未検証です。
既存の.NETプロジェクトへの参照追加やビルド依存はありません。

元プロジェクトを複製して準備・再読み込みまで確認するスクリプトもあります。

```powershell
./scripts/Test-CropoutVillagerSetup.ps1 -CropoutProject D:/Game/Cropout.uproject -BuiltPlugin D:/Build/CropoutVillager.uplugin
```

Content/Config/Pluginsを`artifacts/villager-cropout-copy/<実行ID>/`へコピーし、そこでのみ新規アセットを作成します。
元のContent/Config/uprojectのSHA256を前後で比較します。既存の検証フォルダーは再利用しません。

## Cropoutへの接続

1. Cropoutプロジェクトをコピーします。Editorを閉じて、ビルド済みプラグインをコピー側の`Plugins/CropoutVillager`へ配置・有効化します。
2. Python Editor Script PluginとEditor Scripting Utilitiesを有効化します。
3. EditorのPythonから`Scripts/prepare_assets.py`を明示的に実行します。
   元のDT_Jobsを読み、ネイティブ行型の`/CropoutVillager/Prototype/DT_JobsNative`と、
   `ACropoutVillagerPawn`の子`/CropoutVillager/Prototype/BP_VillagerNative`を作ります。
   Body/Hair/Hatのアニメーションクラス、メッシュ、影、配送Montage、箱、Resource Enum値も設定します。
   同名アセットがある場合は上書きせず停止します。途中失敗時に作られた新規アセットは確認してから整理してください。
4. **子BPに既存のBPI_Villager/BPI_Resourceを追加し、以下のアダプターを接続します。**
   作成スクリプトはインターフェースの追加やイベントグラフの自動作成を行いません。
5. 子BPをコンパイル・保存し、複製した検証レベルへ1体だけ配置します。元BPと位置・移動・仕事変更・運搬・食事・外見・配送を比較します。

`ST_Job`と`FCropoutJobRow`は別の型です。元DT_Jobsを直接ネイティブPawnのJobsへ設定しないでください。
作成スクリプトは行名とソフト参照を移し、元のデータテーブルを変更しません。

### 子BPで接続するノード

| 元のインターフェース | C++側の呼び出し先 |
| --- | --- |
| BPI_Villager / Action(Target) | ActOnTarget(Target) |
| Change Job(New Job) | ChangeJob(New Job) |
| Return To Default BT | ReturnToDefaultBehavior() |
| Play Work Anim(Delay) | PlayWorkAnimation(Delay) |
| Play Deliver Anim → Delay | PlayDeliveryAnimation()の戻り値を返す |
| Progress Building → Time Remaining | ProgressBuilding(Invested Time)の戻り値を返す（元BPも0） |
| BPI_Resource / Add Resource(Resource, Value) | EnumをByteへ変換してAddResource(Resource, Value) |
| Remove Resource → Target Resource, Value | RemoveResource()の出力Byteを元Enumへ変換して返す |
| Get Current Resources → Resources | GetCurrentResources()の出力を元Enumへ変換して返す（元BPも既定値） |
| Remove Target Resource | RemoveTargetResource()（元BPも空） |
| Check Resource → NewParam, NewParam3 | CheckResource()のAvailable/Valueを返す（元BPもfalse/0） |

同じ表示名のノードがある場合、`Villager`カテゴリのネイティブ関数を選び、インターフェースを再帰呼び出ししないよう確認してください。
`Action`と継承関数の名前衝突を避け、C++側は`ActOnTarget`としています。

さらに子BPの以下のイベントを実装します。

- `ConsumeGameResource(Resource, Value)` → Get Game Modeへ、既存BPI_Resourceの`Remove Target Resource`メッセージを送る。Byteを元Enumへ変換する。
- `UpdateAllVillagers` → Get Game Instanceへ、既存BPI_GIの`Update All Villagers`メッセージを送る。
- `JobLoadFailed(Job)` → Print String等で診断を表示する。

これらを接続するまでは、ゲーム全体への食事・保存通知やBPIからの操作は動作しません。
別のC++インターフェースを作っても既存BPIメッセージとは互換にならないため、明示的なアダプターにしています。

## 移した処理と差分

詳細は`wiki/blueprint-villager-cpp-prototype.md`を参照してください。

- BeginPlayでCapsule高さ分の位置補正、24秒周期のEat、Idle選択、髪の非同期ロード。
- 仕事の行検索、同じTagなら再初期化しない条件、Behavior Tree/Blackboard Target設定。
- AddResourceは保持値の上書き。RemoveResourceは退避してからリセット。名前から加算・減算を推測していません。
- 空のインターフェース実装は空/既定値のまま維持。
- 配送は1秒を即座に返し、アニメーションの終了を同期的に待ちません。
- 非同期読み込みは一括リクエスト＋世代チェックに変更。アニメーション要求は最新の要求を優先。
- ネットワークの権限分離・RPCは追加していません。マルチプレイ対応を保証するサンプルではありません。

このフォルダーにEpicの`.uasset`やテクスチャ・メッシュは含みません。実行には利用者のCropoutアセットが必要です。
