---
date: 2026-10-16
tags: [wiki, research/tech, unreal-engine, unreal-insights, tracing]
status: todo
---

# CPU スコープと increment カウンターの併用条件

[目次](../README.md) / [実装](implementation.md) / [Issue #5](https://github.com/posita33/UEEtc/issues/5)

## 結論

UE 5.8 の `TRACE_CPUPROFILER_EVENT_SCOPE_STR` と `TRACE_COUNTER_INCREMENT` は必須セットではありません。片方がもう片方を呼び出す依存関係もありません。

CPU スコープは区間の経過時間、カウンターは定義した計測量の増加を記録します。同じ処理の時間と回数の両方を観察したい場合は併用できますが、その判断は計測目的によります。UEEtc が標準設定で両方を生成するのは、本ツールで時間と関数進入回数の両方を選択しているためです。Engine の必須条件ではありません。

公式の [Trace Developer Guide](https://dev.epicgames.com/documentation/unreal-engine/developer-guide-to-tracing-in-unreal-engine) も Timers と Counters を別々に説明し、それぞれの例はもう片方のマクロを必要としていません。

## Engine の記述箇所の調査

対象は `C:/Program Files/Epic Games/UE_5.8/Engine/Source` と `Engine/Plugins` の `.cpp` / `.h` です。`scripts/Measure-EngineTraceMacroPairs.ps1` で、以下を区別して検索しました。

- 正確に `TRACE_CPUPROFILER_EVENT_SCOPE_STR(...)`
- 正確に `TRACE_COUNTER_INCREMENT(...)`
- その他の `TRACE_CPUPROFILER_EVENT_SCOPE...(...)` 系

| テキスト集計 | 件数 |
| --- | ---: |
| 正確な STR 版の記述箇所 | 239 |
| increment の記述箇所 | 76 |
| その他の CPU Scope 系の記述箇所 | 7,413 |
| 正確な STR 版があるファイル | 71 |
| increment があるファイル | 20 |
| 両方が同じファイルにある | 1 |
| STR 版があり increment がないファイル | 70 |
| increment があり正確な STR 版がないファイル | 19 |
| increment があり直接の CPU Scope 系記述がないファイル | 4 |

出力先は `artifacts/trace-pairing/20261016-095745-2b0aa882/` です。`sites.csv` はファイル・行・マクロ種別、`files.csv` はファイルごとの件数、`both-in-file.csv` は同じファイルで見つかった例、`summary.json` は集計です。

**集計の限界:** 正規表現による記述検索です。コメントだけの行と先頭が `#define` の行を除外しますが、複数行コメント・文字列・継続行のマクロ定義は完全には字句解析しません。条件コンパイル、include 先、独自ラッパーも展開しません。したがって集計値は実行経路・コンパイル後のイベント数・全関数の厳密な AST 集計ではありません。後述する結論は、マクロ定義と実際の関数・スコープを読むことで確認しています。

## 同じファイルでも同じ処理とは限らない

両方の正確なマクロが見つかったファイルは、`Engine/Source/Runtime/Engine/Private/AssetCompilingManager.cpp` でした。

| 場所 | 内容 | 条件・対象 |
| --- | --- | --- |
| 674–683 行、`FAssetCompilingManager::FinishAllCompilation` | 676 行に STR 版。関数内に increment はない | 全アセットのコンパイル完了を待つ区間の時間 |
| `FMemoryBoundScheduledWork::DoThreadedWork` 内の `WaitForSCWMemory` ラムダ | 274 行に **STR ではない** `TRACE_CPUPROFILER_EVENT_SCOPE`、282 / 283 行に increment | メモリー共有が有効、メモリー制約あり、RequiredMemory が HardMemoryLimit を超え、SCW のメモリー待ちを行う経路 |
| 同ラムダの終了付近 | 307 行に `TRACE_COUNTER_DECREMENT(AsyncDelayedTaskActive)` | Active 値を戻す。時間計測マクロの終了とは別の責務 |

後者では `AsyncDelayedTask` は待機処理への進入回数、`AsyncDelayedTaskActive` はその処理中のタスク数を表します。加算はループの前なので、最初の判定で待機不要となり Sleep が 0 回でも加算されます。後者を増やして減らす操作は、状態カウンターとしての組み合わせです。CPU スコープと一対一に固定された組ではありません。

## 単独使用と併用の代表例

以下のパスは `Engine/` を起点とします。

| 例 | 観察したコード | 判断 |
| --- | --- | --- |
| STR のみ | `Plugins/Animation/RigLogic/Source/RigLogicModule/Private/AnimNode_RigLogic.cpp:54`、`Initialize_AnyThread` | `#if CPUPROFILERTRACE_ENABLED` の下で時間を測り、increment はない |
| increment のみの経路 | `Source/Runtime/Engine/Private/Streaming/SimpleStreamableAssetManager.cpp:564`、`AssignPrimitiveHandle` | 有効な古い登録ハンドルが残っていた場合に `SelfHealedRegistrations` を加算する。関数に直接の CPU Scope 系マクロはない |
| CPU Scope 系と increment を併用 | `Source/Runtime/Engine/Private/ShaderCompiler/ShaderCompilerJobCache.cpp:1759–1771`、`FShaderJobCache::Find` | 検索試行数を増やして関数時間を測り、命中時だけ別のカウンターを増やす。CPU マクロは STR 版ではない |
| 独自 CPU ラッパーと increment | `Plugins/Mutable/Source/MutableRuntime/Private/MuR/System.cpp:204–205`、`FSystem::BeginUpdate_MutableThread` | `MUTABLE_CPUPROFILER_SCOPE` と `MutableRuntime_Updates` を併用。正確な STR の検索だけではこの計測を拾えない |
| 条件付きカウンター | `Plugins/Runtime/Metasound/Source/MetasoundGenerator/Private/MetasoundOperatorCache.cpp:376–383` | ロック内でプールへ追加した時に increment。独自の `METASOUND_OPERATORCACHEPROFILER_ENABLED` は `CountersTrace.h` 側の有効条件を参照する |

特にキャッシュ命中・復旧・失敗数などは「特定の分岐を通った回数」です。関数先頭で毎回増やす関数進入回数と同じ意味にはなりません。時間とこれらの数を同時に見る必要がある時に両方を置く、という傾向を代表例から確認できます。Epic が一律の付与基準を定めているという主張ではありません。

## コンパイル時・実行時の条件

| 観点 | CPU STR | increment |
| --- | --- | --- |
| 主なヘッダー | `ProfilingDebugging/CpuProfilerTrace.h` | `ProfilingDebugging/CountersTrace.h` |
| コンパイル時フラグ | `CPUPROFILERTRACE_ENABLED` | `COUNTERSTRACE_ENABLED` |
| フラグが未指定の場合 | `UE_TRACE_ENABLED && !UE_BUILD_SHIPPING` | `UE_TRACE_ENABLED && !UE_BUILD_SHIPPING` |
| 記録に必要なチャンネル | Cpu | Counters |
| 他に必要なもの | 名前が一定であること、RAII オブジェクトのスコープ | 対応するカウンター宣言と更新可能なカウンター実体 |

初期値の式が同じでも、二つのフラグは別々に定義されます。どちらかを使うために他方を有効にする処理はありません。

確認した定義:

- `Source/Runtime/Core/Public/ProfilingDebugging/CpuProfilerTrace.h:19–27,189,399–409,525`。STR は CPU の event 宣言と scope オブジェクトに展開され、increment を含まない。
- `Source/Runtime/Core/Public/ProfilingDebugging/CountersTrace.h:16–24,219–227,399–400,445`。increment は宣言済み実体の `Increment()` を呼び、CPU scope を含まない。

`COUNTERSTRACE_ENABLED` が有効なビルドでは、Counters チャンネルがオフでも `Increment()` 内の値は更新されます。チャンネルが制御するのはその値のトレース出力です。CPU scope 側の記録は Cpu チャンネルによって制御されます。二つのレコードが必ず同数・同時に出る保証はありません。

## UEEtc で両方が生成される条件

`src/UEEtc.Core/AnalysisPlan.cs` の `InstrumentationOptions` は、初期値が `CpuScopes = true`、`CallCounters = true` です。WPF の二つのチェックも初期状態でオン、Console も引数で片方を指定しなければ両方オンです。

そのため次の条件をすべて満たすと、同じ関数先頭に両方を挿入します。

1. 両オプションがオン。
2. 字句解析で対象として認識できる、非空の通常の関数定義。
3. 特殊構文・コンストラクター等の除外条件に該当しない。
4. 関数内に既存の CPU 計測・cycle counter・named event・カウンター更新がない。
5. 必要な include・カウンター宣言を置く位置が確保できる。

この場合に時間は関数全体、カウンターは早期 return を含む進入数です。`--cpu-only`、`--counter-only` または WPF の片方のチェックだけで、個別に生成できます。現在は既存計測が片方でもあれば関数全体をスキップするため、もう片方を自動で補完してセットにする処理もありません。

今回の結論から、Engine の規則として両方を強制する変更は行っていません。既存の独立したオプションを維持します。時間と進入回数の両方が必要なら両方、時間だけなら CPU のみ、意味のあるイベント数だけならカウンターという使い分けができます。

## 再現と検証

リポジトリルートで次を実行します。

```powershell
# Engine を読み取って集計。Engine 自体は変更しない
./scripts/Measure-EngineTraceMacroPairs.ps1

# 独自の小さな fixture で検索と集計の分類を検証
./scripts/Test-TraceMacroPairingAudit.ps1
```

fixture は単独使用、STR 以外の CPU Scope、同じファイルの別関数、条件コンパイルの別枝、複数行呼び出し、コメント行と `#define` 行を含みます。9 項目の集計値と、「同一ファイルに共存」の 2 ケースを検証します。後者を同一関数・同一ビルド経路でのペアと解釈しないためのサンプルです。

この調査は UE を起動せず、Engine / Lyra / BlockBreaker のソースにマクロを追加しません。未マージの命名改善 PR #4 に依存しない、`develop` 起点の調査ブランチです。
