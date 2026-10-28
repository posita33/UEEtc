---
title: BP_VillagerのC++移植試作
date: 2026-10-28
tags: [unreal-engine, blueprint, cpp, experiment]
---

# BP_VillagerをC++へ移す試作（Issue #61）

対象はCropoutの`/Game/Blueprint/Villagers/BP_Villager`。
元資料は`BP_Villager-20261027T234428555-4467c628a60343e28fbc89ab9b8ca1c0`のanalysis.json/Blueprint.txt/Class.txt/Defaults.txt。
アセットSHA256は`70802AC545F0CAC220819AE97AD0402D20DEAF99842856CF6770AE687419BB50`。
生成資料を人が読み、ピン接続と引数を確認してC++へ移した実験である。WPFに汎用自動変換ボタンは追加していない。

## 構成

`examples/BlueprintConversion/CropoutVillager`に独立したUEプラグインを置く。

| ファイル | 責任 |
| --- | --- |
| CropoutVillagerPawn.h / .cpp | Pawnの状態・コンポーネント・ライフサイクル・初期化 |
| CropoutVillagerJobs.cpp | Action、仕事変更、非同期ロード、Blackboard、仕事停止 |
| CropoutVillagerResources.cpp | 保持資源の受け渡し、食事通知、Enum値の対応 |
| CropoutVillagerAnimation.cpp | Montageと停止タイマー、終了時の道具非表示 |
| Scripts/prepare_assets.py | 元DTを読んで別型の新規DTと比較用子BPを準備 |
| Private/Tests/VillagerTests.cpp | 実Engine上で状態遷移・元の空実装を検証 |

既存のC#ライブラリやUIから独立し、ソリューションの.NETビルドには追加していない。

## グラフとの対応

| 元グラフ/イベント | 移植内容 |
| --- | --- |
| EventGraph / ReceiveBeginPlay | AddActorWorldOffset(Z=CapsuleHalfHeight) → Eatのループタイマー24秒 → ChangeJob(Idle)。髪6種類から選び非同期ロード |
| UserConstructionScript | BodyのCustom Primitive Data 0/1へランダム値 |
| Eat | GameModeへResource=NewEnumerator0、Value=3のメッセージ。C++からはBPブリッジイベントを発火 |
| Action | 有効Targetを保存。TargetのTags[0]があれば仕事変更し、GameInstanceへUpdate All Villagers通知 |
| Change Job | New Jobを先に保存、DT行検索、Tagsに未登録の場合だけTagsを置換・Reset・ロード |
| 非同期BTロード完了 | RunBehaviorTree、ActiveBehaviorを保持、有効TargetをBlackboardのTargetキーへ設定 |
| Reset Job State | 帽子を外す、道具を非表示/メッシュ除去、TargetToolをクリア、StopJob |
| Stop Job | 移動停止、道具非表示、DefaultGroupのMontage停止、Quantity=0。資源種別は維持 |
| Add Resource | ResourcesHeld/Quantityを入力値で上書きし、Crateを表示。加算ではない |
| Remove Resource | 元値を出力へ退避、資源種別を既定値/数量0へ戻す、Crate非表示・メッシュ除去 |
| Play Work Anim | WorkAnimationを再生後、TargetToolを設定して表示 |
| PlayVillagerAnim | Montage再生、指定時間後DefaultGroup停止、Completed/InterruptedでTool非表示 |
| Play Deliver Anim | AM_PutDown、Length=1秒、Delay出力=1秒を即座に返す |
| Progress Building / Get Current Resources / Check Resource / Remove Target Resource | 元グラフの空実装を維持（0/既定Enum/falseと0/no-op） |

`NewEnumerator0/5`の名前末尾と値が等しいと仮定せず、準備時に`UEnum::GetValueByNameString`で対応する。
設定前のByteの既定値は0/5だが、実Cropout比較では必ずEnumから設定する。
資源の表示名やBehavior Tree内部の意味はこの試作で新たに推測していない。

## 意図的な差分・残る確認

- 4種類のソフト参照は1つのStreamable要求にまとめる。元BPはSequenceで別々に開始するため、BTが先に始まる等の完了順は再現しない。
- 仕事変更・StopJob・EndPlayで古い要求をキャンセルし、世代番号で古いコールバックを無視する。
- アニメーションの遅延は最後の呼び出しを優先する1本のタイマーにする。元の単一Delayノードは実行中の再入を無視するため、連打時の挙動に差がある。
- Editor専用補助のDefaultSceneRootは生成せず、Capsuleを移動用Rootにする。Collision profileはPawn。細かなBodyInstanceのデフォルト、描画・物理設定の完全複写は行わない。
- Actor Tickを無効化する（元EventGraphにTick処理がない）。MovementやメッシュのコンポーネントTickは残す。
- BPI_Villager/BPI_Resource/BPI_GIはBlueprint専用のまま。子BPの明示的な接続が必要。元BPのreparentや一括差し替えはしない。
- ABP_Villager/Behavior Tree Task等が元BP_VillagerへのCastや変数取得を使う可能性は残る。それらの依存先グラフは未移植なので、別の親クラスを持つ試作Pawnでの確認が必要。
- bReplicates/Movement replicationは有効だが、元の全PropertyFlags、RPCやネットワーク権限の互換を保証しない。
- 45グラフ524ノードのうち、ExecuteUbergraphや_MERGED等のコンパイラー生成グラフは別機能として二重実装しない。

## Engine参照

- Runtime/Engine/Classes/GameFramework/Pawn.h / FloatingPawnMovement.h: Pawnと移動コンポーネント。
- Runtime/Engine/Classes/Engine/StreamableManager.h: 弱いUObjectコールバックと非同期ロードハンドル。
- Runtime/AIModule/Classes/AIController.h / BehaviorTree/BlackboardComponent.h: RunBehaviorTree、Target設定。
- Runtime/AnimGraphRuntime/Public/PlayMontageCallbackProxy.h とPrivate実装: 元K2 Play Montageと同じCompleted/Interrupted通知。
- Runtime/Engine/Public/TimerManager.h: Delay(0)相当の次フレーム予約とタイマー破棄。
- Runtime/Engine/Classes/Kismet/DataTableFunctionLibrary.h: JSONから別行型の新規DTを作成。

利用手順と子BPの接続表はプラグインのREADMEに記載。

## 検証結果（2026-10-28）

- UE 5.8のUHTとMSVCでRuntimeモジュールとEditor用テストをビルド。最終の差分ビルドは警告・エラー0。
- `UEEtc.Cropout.VillagerState`が成功（30確認）。保持数量の上書き、退避してリセット、空インターフェースの既定値、
  同じ仕事で再初期化しない条件、仕事変更時のリセット、異なる実値を持つEnumの対応等を確認した。
- アセットのないHostProjectでのテストは、BodyにSkeletalMeshを割り当てていないためheadSocket/hand_rSocketの警告が6件ある。
  テスト失敗・クラッシュは0。AutomationReportでは`Success` / `succeededWithWarnings=1`として記録される。
- CropoutのContent/Config/Pluginsを別フォルダーへコピーし、準備スクリプトでネイティブ型の仕事データ6行と子BPを作成した。
- 別のEditorプロセスでロードし直し、仕事データ、Bodyメッシュとアニメーションクラス、Crate、配送Montageの永続化を確認した。
- 元のContent全ファイル・Config全ファイル・uprojectのSHA256は前後で一致した。

今回のローカル検証結果は次にある（artifactsはGit管理対象外）。

- `artifacts/villager-validation/c62fcde66b4142f9ab3b7ddc2392ea98/AutomationReportFixed/index.json`
- `artifacts/villager-cropout-copy/c4166510838d4a83ab4fec93684e1db6/CropoutCopy.uproject`
- 子BP: コピー先の`Plugins/CropoutVillager/Content/Prototype/BP_VillagerNative.uasset`

元BPとのゲーム内A/B比較、BPIアダプターの接続、ABPやBehavior Tree Taskの元BPへのCast依存解消、
複数人/大量Actor/高速な非同期切替/ネットワークでの検証は含まない。既存ゲームをそのまま置き換えられる完成版ではない。
