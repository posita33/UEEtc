---
date: 2026-10-16
tags: [wiki, unreal-engine, tracing, research/tech, roadmap]
status: todo
---

# 追加Trace機能の候補

[目次](../README.md) / [現行2機能のロジック](current-feature-logic.md)

以下は**未実装の候補**。2026-10-16にローカルUE 5.8の定義・利用例と公式資料を確認した。
優先順位は、このツールでの使いやすさと観測できる情報からの提案であり、実測した性能改善順位ではない。
Engineパスは`C:/Program Files/Epic Games/UE_5.8/Engine`からの相対パス。行番号はこのインストールに対応する。

## 比較

| 候補・想定画面 | 主なマクロ/API | 分かること・ゲームでの用途 | 自動化の難しさ | 優先度 |
| --- | --- | --- | --- | --- |
| 状態変化のブックマーク | `TRACE_BOOKMARK` | ラウンド開始・クリア・GameOverと処理負荷の関係 | 低～中。発生時点と頻度は選ぶ | 高・最初に追加 |
| 現在値のグラフ | `TRACE_COUNTER_SET`、`TRACE_INT_VALUE`、`TRACE_FLOAT_VALUE` | ボール数・残機・キュー長・待機時間等の推移 | 中。値の意味・型・取得時点を指定する | 高・次に追加 |
| 複数関数にまたがる区間 | `TRACE_BEGIN_REGION_WITH_ID` / `TRACE_END_REGION_WITH_ID` | ロード要求から完了、画面遷移、ラウンド全体 | 高。開始と終了、取消、IDを対応させる | 中 |
| メモリーの用途別タグ | `LLM_SCOPE_BYNAME` / `LLM_SCOPE` | 装備生成やステージ準備に由来する割り当て | 中。既存タグ・非同期処理・取得設定を確認 | 中 |
| トレース内のスクリーンショット | `TRACE_SCREENSHOT`、`FTraceScreenshot::RequestScreenshot` | 異常時に画面で何が起きていたか | 中～高。頻度制限と撮影処理が必要 | 中 |
| 対象別のCPU時間 | `TRACE_CPUPROFILER_EVENT_SCOPE_TEXT` | 同じ関数内で装備・アセット等の種類ごとに比較 | 中。名前式と種類数を制限する | 中～低 |
| アセットのメタデータ | `UE_TRACE_METADATA_SCOPE_ASSET` | 割り当てとアセット/クラス/パッケージの関連 | 高。UObjectの有効性・依存モジュール・既存計測を確認 | 低 |
| ゲーム固有の構造化イベント | `UE_TRACE_EVENT_BEGIN` / `UE_TRACE_EVENT_FIELD` / `UE_TRACE_LOG` | BallId・相手・ダメージ・理由等を対応付けて調べる | 非常に高。専用Analyzer/表示も必要 | 長期候補 |

## 1. 状態変化のブックマーク

最初の新機能として勧める。タイミング上の印を、CPU時間やカウンターの山と見比べられる。
BlockBreakerならラウンド遷移・GameOver、Lyraなら装備切替や重要なゲームフェーズの変化が候補。

- 定義: `Source/Runtime/Core/Public/ProfilingDebugging/MiscTrace.h:102–115`。
- 利用例: `Source/Runtime/Engine/Private/ProfilingDebugging/TraceHitches.cpp:91`のヒッチ検出。
- プラグインの例: `Plugins/Runtime/OpenXR/Source/OpenXRHMD/Private/OpenXRHMD.cpp:4045, 4377`。フレーム同期付近の印であり、そのままゲーム全関数へ付ける規則ではない。
- 取得: `MISCTRACE_ENABLED`と`bookmark`チャンネル。

挿入イメージ（仮の`RoundFlow.cpp`の状態遷移が成立した位置）:

```cpp
TRACE_BOOKMARK(TEXT("Round.Cleared"));
```

新画面では関数・分岐を候補として提示し、「入口の試行」か「状態更新後の成立」かを選べるとよい。
BlockBreakerの`Private/Framework/GameModes/BlockBreakerGameMode.cpp`にある`RoundCleared` / `GameOver` / `ProceedToNextRound`は調査の入口になるが、関数名だけで成立位置を決めない。
初期実装は固定名に絞り、後でRound番号等の型付き引数に広げる。
Tick、衝突の毎フレーム通知、全関数への一括付与は既定にしない。高頻度イベントは既存カウンターの方が扱いやすい。
[公式Developer GuideのBookmark説明](https://dev.epicgames.com/documentation/en-us/unreal-engine/developer-guide-to-tracing-in-unreal-engine)も、低頻度の状態変化に使う方針を示している。

## 2. 現在値のグラフ

既存ADDが「これまでの入力数量」を累積するのに対し、SETは「その時点の値」を記録する。
残機や残ボール数を毎回ADDすると、合計が増え続けて意味が変わるため、別機能として分ける価値がある。

- 定義: `Source/Runtime/Core/Public/ProfilingDebugging/CountersTrace.h:272–277, 325–339, 387–388, 408–409`。
  宣言したカウンターへのSET、整数・浮動小数のインライン値、変更時だけSETする形がある。
- 利用例: `Source/Runtime/Engine/Private/ActorDeferredScriptManager.cpp:285`の残作業数。
- 利用例: `Source/Runtime/Engine/Private/ProfilingDebugging/StreamableManagerProfiling.cpp:37, 124–134`の現在数・平均処理時間。小数カウンターも使っている。
- 取得: `COUNTERSTRACE_ENABLED`と`counters`チャンネル。

新画面で値の式・型・単位・取得位置・頻度を指定し、固定名の整数SETから始める。
副作用のある式を勝手に再評価せず、更新済みローカル変数等を優先する。
BlockBreakerの残機や、`RegisterBoss` / `NotifyBossDefeated`付近の実際の残数が候補。
ただしコンテナーのNum()と有効なActor数は一致するとは限らないため、どの値を見たいかを確認する。
別スレッドの値を読み出す安全性は、atomicのトレースカウンターを使うだけでは確保できない。
複数インスタンスが同じカウンターへSETすると「最後の1体の値」になり得るため、集計値かインスタンス別かも選ぶ。

## 3. 複数関数にまたがる区間

CPUスコープでは測れない「要求開始から別コールバックでの完了」や、複数フレームにわたるフェーズを可視化する。

- 定義: `Source/Runtime/Core/Public/ProfilingDebugging/MiscTrace.h:131–164`。
  同名の区間が重なる場合はID付きの形が推奨されている。
- ID付きの利用例: `Source/Runtime/Engine/Private/AssetCompilingManager.cpp:267, 353, 371–373`。
  WorkにRegionIdを保持して開始と終了を対応させる。
- 名前で対応する例: `Source/Runtime/Engine/Private/LevelStreaming.cpp:1770, 1966`。
- 表示・チャンネルの根拠: `Source/Runtime/Core/Private/ProfilingDebugging/MiscTrace.cpp:11, 28–49, 92–120`。
  `region`で記録する、スレッドに縛られない時間区間。

新画面は開始地点・完了地点・取消/失敗地点・ID保持先を1組として選ぶ設計が必要。
Actor破棄や要求取消でも閉じる規約、重複終了の防止、Trace無効ビルドでのIDの扱いも決める。
現行のActiveScopesに`ON_SCOPE_EXIT`を置く手法を、そのまま非同期処理へ流用しない。
IDの保持にクラスメンバーが必要ならヘッダーも変更対象になるため、現行のcpp限定機能から独立させる。

## 4. メモリーの用途別タグ

ステージ生成、装備組み立て、キャッシュ構築等に由来する割り当てをグループ分けする。
CPU時間と同じ位置へ機械的に置くのではなく、メモリーの用途・所有関係から区間を選ぶ。

- 定義: `Source/Runtime/Core/Public/HAL/LowLevelMemTracker.h:19, 351–352, 381–384`。
  固定名のLLMスコープはMemory Trace側のスコープにも接続されている。
- 利用例: `Source/Runtime/Engine/Private/BoneContainer.cpp:33, 54, 74`のAnimation/BoneContainerタグ。
- 取得: LLMの有効ビルド条件とMemory Traceの設定を確認する。
  割り当て履歴を調べる場合は起動時から`-trace=default,memory`を使う構成が入口となる。
  LLM独自の集計を使う場合の`-llm`等と混同せず、[Memory Insights](https://dev.epicgames.com/documentation/en-us/unreal-engine/memory-insights-in-unreal-engine)の取得条件に合わせる。

新画面では選択した処理に固定の用途タグを付ける。既存のLLM/Memoryタグと重複する場所は避ける。
これはスコープ前後のプロセスメモリー差分を測る機能ではなく、割り当てに用途を付ける機能。
別スレッドや後で実行するラムダへタグが自動で引き継がれるとは扱わない。

## 5. トレース内のスクリーンショット

ラウンドが進まない、敵が残る、異常な復旧経路に入った等の時点で、画面と時間軸を結び付ける。

- マクロ定義: `Source/Runtime/Core/Public/ProfilingDebugging/MiscTrace.h:166–170`。
  **UE 5.8のTRACE_SCREENSHOTは、名前だけでは呼べない**。時刻、幅、高さ、画像データも受け取る。
- 撮影要求API: `Source/Runtime/Engine/Public/ProfilingDebugging/TraceScreenshot.h:8–12, 28`。
- 実装例: `Source/Runtime/Engine/Private/ProfilingDebugging/TraceScreenshot.cpp:73–88, 162–167`。
  `FTraceScreenshot::RequestScreenshot`から撮影処理へ進み、圧縮した画像をマクロへ渡す。
- 取得: `UE_SCREENSHOT_TRACE_ENABLED`等のビルド条件と`screenshot`チャンネル。画像を作る処理が必要なため、専用サーバーへの一律追加は対象外。

機能化するなら、単純なマクロ挿入より撮影要求APIの追加と必要なコンパイルガード・Engine依存を生成する。
ゲームスレッド側の実行位置、1回だけ/間隔制限、UIを写すか、画像サイズを選べるようにする。
大量の画像は取得負荷とトレース容量を増やすため、全分岐へ自動追加しない。
手軽に試す段階では、[公式Trace Quick Start](https://dev.epicgames.com/documentation/en-us/unreal-engine/trace-quick-start-guide-in-unreal-engine)の`Trace.Screenshot`コンソール操作も使える。

## 6. 対象別のCPU時間

同じ処理関数でも、装備種別やアセットの種類ごとに時間を比較したい場合の候補。
現行の固定名CPU計測と目的が異なるため、文字列式を指定する別の詳細機能として扱う。

- 定義: `Source/Runtime/Core/Public/ProfilingDebugging/CpuProfilerTrace.h:283–342, 473–490`。
- 利用例: `Source/Runtime/Engine/Private/Animation/AnimationCompressionDerivedData.cpp:191`のアニメーション名付き処理。
- 利用例: `Source/Runtime/Engine/Private/NetDriver.cpp:1174`のNetDriver定義名。
- 取得: `CPUPROFILERTRACE_ENABLED`と`cpu`チャンネル。

動的名の式はユーザーに選んでもらい、既存スコープへの追記ではなく意図した計測位置を決める。
STRへ可変名を渡す実装にはしない。毎回ユニークなActor番号等を名前に混ぜると種類数・メモリー・解析負荷が増えるため、少数のカテゴリを優先する。
文字列作成コストを含めた計測になる点にも注意する。

## 7. アセットのメタデータ

「どのアセット・クラス・パッケージに関係する割り当てか」を調べたい場合の候補。

- 定義: `Source/Runtime/Core/Public/ProfilingDebugging/AssetMetadataTrace.h:46–64`。
  Core以外のモジュールで利用する場合のTraceLog依存もコメントに示されている。
- 利用例: `Source/Runtime/CoreUObject/Private/Serialization/AsyncLoading2.cpp:9675, 10458`。
- 利用例: `Source/Runtime/CoreUObject/Private/UObject/ScriptCore.cpp:1145, 1354`。
- 取得: `assetmetadata`と`metadata`、Memory Traceでの用途に合わせた`memory`/`memalloc`の取得設定を確認する。
  根拠は[公式チャンネル一覧](https://dev.epicgames.com/documentation/en-us/unreal-engine/trace-in-unreal-engine-5)と`Source/Runtime/Core/Private/ProfilingDebugging/MetadataTrace.cpp:8`。

UObjectの有効性、対象クラス、パッケージ、既存スコープ、Build.cs依存を確認して限定的に挿入する必要がある。
CoreUObject側で既に計測される経路もあるため、まず既存記録で足りるか調べる。
通常のCPUタイマーに任意の業務値が自動表示される機能としては扱わない。

## 8. ゲーム固有の構造化イベント

BlockBreakerのBallId・衝突相手・ダメージ・破壊理由、Lyraの能力要求ID・結果等を、1つのイベントとして記録する。
単なる回数では対応付けられない「同じ要求がどう進んだか」を追う長期候補。

- 定義: `Source/Runtime/TraceLog/Public/Trace/Trace.h:577–593`。
- 実装例: `Source/Runtime/Core/Private/ProfilingDebugging/MiscTrace.cpp:14–49, 83–120`。
  Engine自身がイベントの型とフィールドを宣言して、チャンネルへ出力している。
- [公式Developer GuideのCustom Events](https://dev.epicgames.com/documentation/en-us/unreal-engine/developer-guide-to-tracing-in-unreal-engine)も、独自の分析処理を必要とすることを説明している。

新機能にはイベントスキーマ、相関ID、発生時刻、専用チャンネル、取得側と解析側の実装をまとめて用意する。
**マクロを挿入しただけでは、標準Insightsに専用の表やグラフは自動追加されない**。
まずJSON等への解析出力を用意し、必要ならInsights用Provider/Analyzer/表示拡張を別モジュールで作る。
既存のBookmark・Counter・Regionで不足する用途に絞って検討する。

## 新機能を切り出す設計案

既存Coreの`Source` / `Plans` / `Reporting`を共用し、ルールは`UEEtc.Core.Features.Bookmarks`、`StateValues`、`Regions`等へ分ける。
WPFはメイン画面から各機能の専用画面を開き、Consoleも機能別サブコマンドから同じCoreへ接続する。
この名前空間・画面・コマンドは提案であり、まだ追加していない。

各機能の候補には、少なくともパス・関数・行・理由・追加マクロ・必要な値・チャンネルを含める。
プレビューでは既存のマクロと今回の追加を区別し、再解析で重複しないことを検証する。
Regionでは候補単位を「1行」ではなく「開始と複数の終了経路」に拡張する。
値・型・寿命を解決できない箇所は、名前だけで自動確定しない。

最初の実装単位は**固定名BOOKMARK**、続いて**整数の現在値SET**が扱いやすい。
Frameマーカー、ファイルI/O、Task Graph等はEngineが既に記録する場合があるため、まず既存チャンネルで不足を確認する。
プロジェクト側で同じイベントを重ねて増やすことを、新機能の既定方針にはしない。
