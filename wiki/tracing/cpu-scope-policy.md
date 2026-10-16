---
date: 2026-10-16
tags: [wiki, implementation, csharp, unreal-engine, tracing, profiling]
status: todo
---

# CPU スコープの短いラベルと Super 後の配置

通常形／STRの現行の選択規則と比較は [Issue #33 の調査](cpu-macros-and-counter-lifetimes.md) を参照してください。短いラベルと配置の規則は維持します。

標準モードの対象絞り込みは [Issue #21 の追加調査と実装](focused-cpu-and-quantity-counters.md) を参照してください。このページはラベルと配置の規則を説明します。

[目次](../README.md) / [Issue #12](https://github.com/posita33/UEEtc/issues/12) / [Issue #13](https://github.com/posita33/UEEtc/issues/13) / [Quick の調査](quick-scope-cycle-counter.md)

## ラベル

新しく生成する CPU マクロのラベルを「クラス名 関数名」に変更しました。パス、`UEEtc/` 接頭辞、ハッシュは付けません。パス・行・元の修飾付き関数名はレポートに残るため、ラベルを短くしても変更箇所を確認できます。

`UMyComponent.cpp` の生成例:

```cpp
void UMyComponent::BeginPlay()
{
    Super::BeginPlay();
    // UEEtc: CPU scope
    TRACE_CPUPROFILER_EVENT_SCOPE_STR("UMyComponent BeginPlay");

    InitializeComponentState();
}
```

名前空間を含む `UE::Game::AMyActor::Tick` は `AMyActor Tick`、修飾子のない自由関数は関数名だけにします。C++ の型解決は行わず、`::` の直前の修飾子を所有スコープとして扱います。そのため名前空間付きの自由関数とクラスの static メソッドの厳密な区別はできません。

オーバーロードや別の名前空間にある同名クラスは、要望どおり同じ短い表示名になる場合があります。区別用のハッシュは戻しません。既存の CPU マクロ・既に生成した旧ラベルは維持します。今回の変更は新規生成の規則であり、既存マクロを改名する機能ではありません。

旧 Console の関数進入カウンターは、表示名にパス・ハッシュを持つ従来の識別方法を維持しています。別機能のイベントカウンターも、この CPU ラベル規則の対象ではありません。

## Super 呼び出しがある場合

Engine では `C:/Program Files/Epic Games/UE_5.8/Engine/Plugins/Runtime/InstancedActors/Source/InstancedActors/Private/InstancedActorsModifierVolumeComponent.cpp:55-59,98-102` に、BeginPlay / EndPlay の親呼び出し後へ短いラベルの CPU スコープを置く例があります。これを本ツールの生成規則へ反映しました。すべての Engine コードがこの位置に統一されている、という意味ではありません。

`CpuScopePlacement` を FunctionTracing 機能内に切り出し、次の規則で位置を決めます。

| 元のコード | 新しい CPU スコープの位置 |
| --- | --- |
| 対応する Super 呼び出しがない | 関数本体の先頭 |
| 関数の直接のスコープに単独文の `Super::同じ関数名(...);` が 1 回ある | その文の直後 |
| 上記の後に処理がない | 追加を省略し、理由を表示 |
| 条件・ループ・ラムダ内部の呼び出し、`return Super::...()`、代入・式内の呼び出し | CPU は手動確認としてスキップ |
| 同名の Super 呼び出しが複数、または関数内にプリプロセッサー指令がある | CPU は手動確認としてスキップ |
| 別名の親ヘルパーだけを呼ぶ | 通常の関数先頭。例: Work 内の `Super::Helper()` |
| 既存の CPU / Quick 等がある | 既存計測を維持 |

コメント・文字列内の Super 表記は呼び出しと判定しません。引数内の括弧・文字列・ラムダがあっても、同名呼び出しの単独文の終わりを確認します。マクロの展開、typedef の解析、親クラス型の解決はしません。

生成する RAII オブジェクトは関数の直接のスコープへ置きます。親呼び出し直後だけを囲む追加ブロックは作らず、関数を抜けるまでの時間を計測します。早期 return でもスコープが終了します。

親呼び出しより前に準備処理がある場合も、計測するのは親呼び出し後だけです。親クラスの時間と先行する準備処理はこのラベルの時間に含まれません。そのことを `CpuDecision` と WPF の説明へ記載しました。「関数全体」の計測と同じ値として比較しないでください。

## カウンターへの影響

旧互換モードの関数進入回数は関数先頭へ置きます。CPU の位置を親呼び出し後に変えても、進入回数まで移動しません。CPU を安全に置けない場合も、明示的に選んだ旧進入カウンターだけを追加できます。イベントカウンター機能の候補・選択・挿入位置は変更していません。

## 検証

| 検証 | 結果 |
| --- | --- |
| Core | 既存 65 + 今回 15、計 80 テスト成功。短い名前、名前空間、自由関数、オーバーロード、既存ラベル維持、Super の各構文、二重付与防止、文字コードを検証 |
| WPF | 29 チェック成功。画面分離と候補選択の回帰に加え、短い CPU ラベルと Super 後の配置を確認 |
| CPU 配置専用の C++ fixture | MSVC C++20 `/W4 /WX` で CPU / Counter の有効・無効 4 組をコンパイル・実行。親呼び出しと先行処理中は CPU スコープ非活動、後続処理中は活動、早期 return 後は終了、ラベル一致、進入回数は親呼び出し前に増分済みであることを確認 |
| 既存 C++ fixture | 12 組のコンパイル・実行成功 |
| Release ビルド | 警告 0 / エラー 0 |

Lyra と BlockBreaker を CPU モードで再解析し、元ソースへの書き込みなしで以下を確認しました。

| 対象 | 解析 .cpp | 変更コピー | CPU 追加 | うち Super 後 | Super の構文による手動確認 | Super の後に処理なし |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Lyra | 233 | 208 | 1,370 | 189 | 68 | 94 |
| BlockBreaker | 47 | 39 | 223 | 45 | 5 | 7 |

いずれも差分の適用チェック成功、生成コピーを再解析した追加変更 0 件、診断 0 件でした。手動確認・付与省略は関数単位の理由であり、ファイルの構文診断ではありません。元ソース全 599 ファイルの SHA-256 は一致しています。実 UE ビルド・Insights 取得は未実施です。

実験生成物（Git 管理外）:

- 実プロジェクト: `artifacts/trace-experiments/run-20261016-113847-fbd68a42/`
- 配置専用 C++: `artifacts/cpu-policy-cpp/8406793c410a41eb9fb9cd54a1dab11d/`
- 既存 C++: `artifacts/cpp-smoke/a584416974494d5cb931ccbd9968b0f1/`
- WPF: `artifacts/cpu-policy/ui/`

再現コマンド（リポジトリルート）:

```powershell
dotnet build UEEtc.sln -c Release
dotnet run --project tests/UEEtc.Core.Tests -c Release --no-build
dotnet run --project tests/UEEtc.Wpf.SmokeTests -c Release --no-build
./scripts/Test-CpuScopePolicy.ps1
./scripts/Test-CppInstrumentation.ps1
./scripts/Invoke-TraceExperiments.ps1 -Mode cpu
```
