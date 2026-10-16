---
date: 2026-10-16
tags: [wiki, research/tech, unreal-engine, profiling, stats]
status: todo
---

# QUICK_SCOPE_CYCLE_COUNTER の使い方と有効条件

[目次](../README.md) / [Issue #11](https://github.com/posita33/UEEtc/issues/11) / [CPU ラベル・配置](cpu-scope-policy.md)

## 何を計測するか

`QUICK_SCOPE_CYCLE_COUNTER` は、その位置から現在の C++ スコープを抜けるまでの時間を計測する Stats 系マクロです。呼び出し回数を累積する `TRACE_COUNTER_INCREMENT` とは異なります。事前に専用グループを定義せず、Quick グループへ計測を置く用途です。スパイクが疑われる処理や、条件付きの重い区間を短期間調べる際に使いやすい構成です。

[Epic の Stats System Overview](https://dev.epicgames.com/documentation/unreal-engine/unreal-engine-stats-system-overview) は Quick グループへの宣言と、一時的な統計としての用途を説明しています。ただし、具体的な展開・有効条件は手元の UE 5.8 のソースで確認しました。

例として、ゲームの `.cpp` 内に次のように記述します。この調査では実際のゲームへ適用していません。

`MyActor.cpp` の計測例:

```cpp
#include "MyActor.h"
#include "Stats/Stats.h"

void AMyActor::RebuildCache()
{
    QUICK_SCOPE_CYCLE_COUNTER(STAT_MyActor_RebuildCache);
    RebuildEntries();
}
```

引数は `STAT_MyActor_RebuildCache` のような C++ 識別子です。表示文字列を直接渡すマクロではありません。同じブロックに同じ識別子のマクロを複数置くと、展開した変数等が重複するため避けます。`STAT_` はよくある慣習ですが必須ではなく、実際の Engine には `NET_AActor_FlushNetDormancy` や `IA_EntityIndexCaching` もあります。

公式ページにある文字列引数の例をそのまま採用するのでなく、UE 5.8 の `Stats.h` で識別子の文字列化・トークン結合が行われていることに合わせてください。

## Engine ソースで確認した条件

以下は `C:/Program Files/Epic Games/UE_5.8/Engine` を基準にした、2026-10-16 の位置です。

| ソース | 確認内容 |
| --- | --- |
| `Source/Runtime/Core/Public/Stats/Stats.h:221-227` | `STATS` 有効時は Quick グループへの Stats 宣言と `FScopeCycleCounter` を作る |
| `Source/Runtime/Core/Public/Stats/GlobalStats.inl:60` | Quick グループの定義 |
| `Source/Runtime/Core/Public/Stats/StatsSystemTypes.h:1526-1546` | Named Events が有効で、CPU trace がコンパイルされ、Cpu チャンネルが有効な場合に Insights のイベントを出す経路 |
| `Source/Runtime/Core/Public/Stats/Stats.h:560-573` | `STATS == 0` でも `UE_USE_LIGHTWEIGHT_STATS` が有効なら別の計測経路を持つ。Stat の有効性と Named Events を条件に CPU trace を出す |
| `Source/Runtime/Core/Public/Stats/LightweightStats.h:349` | Named Events と Stat の有効条件を確認 |
| `Source/Runtime/Core/Public/Stats/Stats.h:599-636` | 上記が無効でも hitch 用のスコープになる構成があり、それも無効なら空のマクロ |
| `Source/Runtime/Launch/Private/LaunchEngineLoop.cpp:1759` | `-statnamedevents` が Named Events を有効化する |
| `Source/Runtime/Core/Private/Misc/CoreMisc.cpp:548` | 実行中の `stats.NamedEvents 1` / `stats.NamedEvents 0` のコマンド定義 |

したがって、`-trace=cpu` だけで常に Quick のマクロが Insights に現れるとは限りません。CPU trace・Named Events・ビルドの Stat 設定が必要です。「STATS が 0 なら必ずすべて無効」とも断定できません。

Insights での確認手順は、有効な開発用ビルドで起動引数に `-trace=cpu -statnamedevents` を指定し、トレースを取得して Timing Insights で名前を探す、というものです。実行中の有効化には、今回ソースで確認した `stats.NamedEvents 1` を使えます。Stat / Group の個別の抑制、チャンネル、コンパイル設定も確認してください。この手順の実機トレース取得は未実施です。

Stats の画面内表示は別の目的です。従来の Stats が有効な構成では、グループ表示の規則に従い `stat quick` で Quick グループを表示します。Named Events の有効化そのものは、画面内にリアルタイムの負荷一覧を表示する操作ではありません。

## 実際に置かれている位置

`Source/Runtime/Engine/Private/Actor.cpp:3109` の `NET_AActor_FlushNetDormancy` は、早期 return のガードを通過した後にあり、関数全体へ無条件に置く形ではありません。同ファイルにはコンポーネント登録・初期化の時間を計測する例もあります。

`Plugins/Runtime/InstancedActors/Source/InstancedActors/Private/InstancedActorsData.cpp:1942` の `IA_EntityIndexCaching` は、キャッシュを更新する必要がある分岐内にあります。処理を行う経路だけを、現在のブロックの終了まで計測する例です。

`Plugins/Runtime/InstancedActors/Source/InstancedActors/Private/InstancedActorsModifierVolumeComponent.cpp:352` では描画処理のスコープに使われています。QUICK という名前だけで「一度使ったら必ず直ちに削除する」「Engine が恒久的には使わない」とは言えません。残す範囲や頻度は実際の負荷と目的から判断します。

## どのマクロを選ぶか

| 目的 | 選択 |
| --- | --- |
| Insights の時間計測を直接追加したい | `TRACE_CPUPROFILER_EVENT_SCOPE_STR("クラス名 関数名")`。本ツールの CPU 機能が生成する形 |
| Quick グループで手早く Stats のスコープを作りたい | `QUICK_SCOPE_CYCLE_COUNTER(識別子)` |
| 独自グループ・宣言を管理した Stats 計測 | `DECLARE_CYCLE_STAT` と `SCOPE_CYCLE_COUNTER` 等 |
| 成功・拒否・復旧などの発生回数 | 確認した位置への `TRACE_COUNTER_INCREMENT`。本ツールではイベントカウンターの別機能 |

一回のコストが軽いと決めつけず、高頻度な区間では計測自体の影響を確認してください。同じ処理へ Quick と CPU trace を重ねると、測定が重複する場合があります。

## 本ツールへの反映

この Issue は調査として対応しました。Quick 生成専用の画面や自動置換機能は追加していません。既存の `QUICK_SCOPE_CYCLE_COUNTER` を CPU 計測として維持し、同じ関数へ `TRACE_CPUPROFILER_EVENT_SCOPE_STR` を追加しない動作を回帰テストで確認しています。既存コードの計測名や種類を勝手に変更しません。

Engine の元ソース、Lyra、BlockBreaker のソースは変更していません。実際の UE ビルド・Stats HUD・Insights 取得の確認は未実施です。
