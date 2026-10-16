---
date: 2026-10-16
tags: [wiki, research/tech, unreal-engine, unreal-insights, tracing]
status: todo
---

# UE 5.8 の Trace マクロ調査と付与方針

[目次](../README.md) / [実装](implementation.md) / [実験](experiments-2026-10-16.md)

## 調査範囲

公式の [Trace Developer Guide](https://dev.epicgames.com/documentation/unreal-engine/developer-guide-to-tracing-in-unreal-engine) と、ローカルにインストールされた UE 5.8 の以下のソースを調べました。

- `C:/Program Files/Epic Games/UE_5.8/Engine/Source`
- `C:/Program Files/Epic Games/UE_5.8/Engine/Plugins`

以下のパスは、特に断りのない限りこの `Engine/` からの相対パスです。Engine 全体をパターン検索して代表例を読み、マクロ定義と照合しました。すべての付与箇所を意味解析した調査ではありません。

## 二つのマクロが測るもの

| マクロ | 測定対象 | 自動付与時の解釈 |
| --- | --- | --- |
| `TRACE_CPUPROFILER_EVENT_SCOPE_STR` | RAII オブジェクトが生存する処理区間の経過時間 | 関数本体への進入から退出まで。途中の return でもスコープが閉じる |
| `TRACE_COUNTER_INCREMENT` | 宣言済みカウンターの値の増分 | 対象関数に入った回数。失敗・早期 return も数える |

時間には、その区間内で待機した時間や同期的に呼んだ子処理も含まれます。GPU 時間や、後で別スレッドで実行される非同期処理全体の完了時間は測れません。

## ヘッダーと有効条件

公式 Developer Guide の本文には `CpuProfilingTrace.h` と書かれていますが、この環境の実ファイルは **`ProfilingDebugging/CpuProfilerTrace.h`** です。生成する include は実ファイルに合わせています。

| 実ファイル | 確認箇所 | 確認内容 |
| --- | --- | --- |
| `Source/Runtime/Core/Public/ProfilingDebugging/CpuProfilerTrace.h` | 19–27 行 | 未指定時の `CPUPROFILERTRACE_ENABLED` は `UE_TRACE_ENABLED && !UE_BUILD_SHIPPING` に従う |
| 同上 | 399–409 行 | STR 版は一定の名前を呼び出し箇所ごとにキャッシュする。毎回変わる文字列を渡さない |
| 同上 | 525 行以降 | 無効時のマクロは空に展開される |
| `Source/Runtime/Core/Public/ProfilingDebugging/CountersTrace.h` | 16–24 行 | 未指定時の `COUNTERSTRACE_ENABLED` も `UE_TRACE_ENABLED && !UE_BUILD_SHIPPING` に従う |
| 同上 | 219 行付近 | increment は値を更新し、Counters チャンネル有効時に値を出力する |
| 同上 | 245 行付近、288–317 行 | 通常の整数型と atomic 整数型、それぞれの宣言マクロを提供する |
| 同上 | 399 行、445 行以降 | increment は宣言済みオブジェクトを参照し、無効時は宣言・更新とも空に展開される |

したがって `TRACE_COUNTER_INCREMENT(SomeCounter)` だけを挿入しても動きません。対応するカウンター宣言と `CountersTrace.h` が必要です。本実装ではファイルごとの無名 namespace に `TRACE_DECLARE_ATOMIC_INT_COUNTER` を置き、関数本体の先頭で increment します。UE の unity build で名前が衝突しにくいよう、識別子にはファイル相対パスとシグネチャ由来のハッシュを付けます。

atomic 版を選ぶのは、任意の関数が Game Thread だけで動くとは判断できないためです。値の更新時のデータ競合を避けますが、競合する各スレッドからの出力順序まで保証するものではありません。

コンパイル時の有効条件とは別に、記録時のチャンネルも必要です。CPU・Counter・Frame のチャンネルについては [Unreal Insights Reference](https://dev.epicgames.com/documentation/en-us/unreal-engine/unreal-insights-reference-in-unreal-engine-5) を確認しました。明示的に記録する場合の引数例は `-trace=cpu,counters,frame` です。今回、UE を起動してトレースを取得する操作は実施していません。

## Engine / Plugins の代表例

| 場所 | 観察した条件 | 自動付与への示唆 |
| --- | --- | --- |
| `Plugins/Animation/RigLogic/Source/RigLogicModule/Private/AnimNode_RigLogic.cpp:54` | 初期化・キャッシュ処理の関数全体。明示的な `#if CPUPROFILERTRACE_ENABLED` もある | 処理単位を名前で識別する。追加の guard がなくてもマクロ定義自体は無効化に対応 |
| `Source/Developer/MeshBuilder/Private/StaticMeshBuilder.cpp:926` | LOD 構築の内部ブロック | 一つの関数に複数の意味ある処理区間を置くこともある |
| `Plugins/Experimental/SemanticSearch/Source/SemanticSearch/Private/SemanticSearchDerivedDataBuilder.cpp:114` | DDC の命中など特定経路のスコープ | 分岐の意味を知らずに自動で同じ粒度を選ぶことはできない |
| `Source/Runtime/Engine/Private/ShaderCompiler/ShaderCompilerJobCache.cpp:1759,1771` | 検索試行数と、キャッシュに命中した場合の回数を別々に加算 | 関数進入数と成功数は区別する必要がある |
| `Source/Developer/DerivedDataCache/Private/ZenCacheStore.cpp:44–47,585,885` | atomic カウンターを宣言し、Get/Put の命中時に加算 | 複数スレッドで更新する場合の宣言例 |
| `Plugins/Runtime/Metasound/Source/MetasoundGenerator/Private/MetasoundOperatorCache.cpp:28,381` | 専用コンパイルフラグとロックの下でプール内の数を更新 | 現在数を測る設計では decrement 等も必要。increment だけでは現在数にならない |

Engine の実例には「一定以上の行数の関数に必ず付く」といった共通の機械的な条件は見当たりません。開発者が計測したい区間やイベントを選んでいます。これは代表例からの判断であり、Epic の正式な統一ルールとして扱いません。

## 今回実装した機械的なルール

1. フォルダー配下の通常の `.cpp` を再帰探索する。ヘッダー、生成 `.cpp`、ThirdParty、ビルド成果物、リンク／ジャンクションは対象外にする。
2. namespace 内を含め、字句解析で識別できる非空の関数定義の本体先頭に付与する。ループ・if ブロック・ラムダ・クラス内定義には自動付与しない。
3. コンストラクター、デストラクター、constexpr/consteval、テンプレート、operator、requires、function-try-block、コルーチン、特殊な宣言は除外または手動確認に回す。
4. 関数内に既存の CPU マクロ・cycle counter・named event・カウンター更新があれば、関数全体をスキップする。ネストした処理の計測も含めた保守的な判定であり、マクロが片方だけあってももう片方を補完しない。
5. コメントや文字列内のマクロ名は既存計測として扱わない。括弧対応や条件コンパイルの分岐を確定できないファイルは変更案を生成せず診断を残す。
6. ソース固有の最初の include を先頭に維持し、ファイル先頭の include 群の最後の無条件 include の直後に、必要な include と無名 namespace 内の宣言を追加する。これにより既存の `CountersTrace.h` よりも宣言が前に出ることを避ける。コードの開始前に挿入位置を確保できない場合はスキップする。

判定は C++ コンパイラーの AST ではなく、コメント・通常文字列・raw 文字列・プリプロセッサー行を区別する字句解析です。未認識の宣言、マクロ展開によって生成される関数、未対応の C++ 構文は網羅しません。スキップ件数は「識別できた候補のうち除外した件数」であり、全関数数の保証には使えません。

## 今後の意味に沿った計測候補

BlockBreaker では `ABall::OnHit` の進入数、実際にパドルで反射した数、キャッチした数は異なります。`ABrick` のダメージ受付数と破壊成功数、敵のスポーン要求数と生成成功数も異なります。

Lyra では能力実行の要求、サーバー側でのダメージ計算、武器の発射成立などを分けると有用です。今回のツールは関数の時間と進入数を広く観察する入口です。命中・成功などの意味を持つカウンターは、実際のトレースを見ながら個別に配置する次の段階になります。
