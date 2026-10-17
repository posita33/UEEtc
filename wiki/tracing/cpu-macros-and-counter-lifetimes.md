---
date: 2026-10-16
tags: [wiki, research/tech, unreal-engine, tracing, csharp, wpf]
status: todo
---

# CPU マクロの選択とカウンターの生存区間（Issue #32・#33）

CPUの現行仕様は[Issue #53のSTR統一と独自マクロ設定](custom-cpu-str.md)を参照。以下の自動/通常形/STR選択と `--cpu-macro` はIssue #32当時の仕様であり、現在は廃止している。Engineの固定名・動的名の調査とカウンターの生存区間の区別は引き続き有効。

イベントの最新の自動設定は [Issue #35](automatic-counter-preview.md) を参照してください。以下のスコープ現在数への手動切替に加え、累積回数を保持する別カウンターの自動追加を実装しています。

[目次](../README.md) / [現行のファイル選択](event-files-and-input-quantities.md)

## CPU 調査：STR は動的文字列用ではない

UE 5.8 の `C:/Program Files/Epic Games/UE_5.8/Engine/Source/Runtime/Core/Public/ProfilingDebugging/CpuProfilerTrace.h` を読み、定義とコメントを照合しました。Issue に引用された回答には、このバージョンの実装と異なる点があります。

| マクロ | 入力 | UE 5.8 の経路 |
| --- | --- | --- |
| TRACE_CPUPROFILER_EVENT_SCOPE | 引用符なしの plain text | 453–454行から445–446行へ進み、引数を文字列化して STR_CONDITIONAL を呼ぶ |
| TRACE_CPUPROFILER_EVENT_SCOPE_STR | 固定文字列リテラル、変化しない文字列ポインター、FName | 403–409行。399–401行の宣言・FEventScope を使用し、静的な SpecId をキャッシュ |
| TRACE_CPUPROFILER_EVENT_SCOPE_TEXT | 実行ごとに変わり得る文字列やFName | 473–490行。FDynamicEventScope を使う動的な名前用 |

**通常形と STR の固定文字列は同じ静的ID経路です。STR を使っているだけで動的文字列の処理負荷になるわけではありません。** 通常形への変換による速度向上を示す調査結果でもありません。

同ヘッダーの253–277行は GetOrCreateSpecId を通して保持されたIDでイベントを開始します。STR のコメントは名前が呼び出し間で変化しないことを要求します。実行時に変わる `*ItemName` を STR に渡すと、名前が呼び出し箇所で最初にキャッシュされるため、動的ラベルの使い方として適切ではありません。動的名には TEXT 系を使います。

[Epic の Trace Developer Guide](https://dev.epicgames.com/documentation/unreal-engine/developer-guide-to-tracing-in-unreal-engine) も、STR に固定リテラルを渡す例を掲載しています。動的な名前には追加の負荷があるという説明と、STR 自体を動的用と分類することは別です。

### 使い方

```cpp
#include "ProfilingDebugging/CpuProfilerTrace.h"

void AMyActor::CalculateComplexLogic()
{
    TRACE_CPUPROFILER_EVENT_SCOPE(AMyActor CalculateComplexLogic);
    DoSomething();
}
```

通常形の引数はC++の式ではなく、マクロで文字列化されるトークン列です。上の例は `AMyActor CalculateComplexLogic` という名前になります。一般的な `AMyActor_CalculateComplexLogic` や `AMyActor::CalculateComplexLogic` も使用できます。引用符付きの文字列を通常形へ渡すと余分な引用符まで名前に含まれるため、固定リテラルを明示する場合は次の形式を使います。

```cpp
TRACE_CPUPROFILER_EVENT_SCOPE_STR("AMyActor CalculateComplexLogic");
```

どちらも宣言位置からC++のスコープ退出までの時間を計測します。独自の中括弧で囲めば部分区間を測れます。非同期処理を開始しただけの関数のスコープは、その後の非同期実行時間を含みません。

計測のコンパイル有効条件はヘッダー19–25行付近の CPUPROFILERTRACE_ENABLED です。既定は UE_TRACE_ENABLED と非Shippingですが、事前定義による変更が可能なので「Shippingなら常に空」とは断定しません。QUICK_SCOPE_CYCLE_COUNTER 等も既存の機能として扱い、今回も既存計測を保持します。

### ツールの選択実装

- 自動: 生成ラベルが安全なASCII識別子と空白で表せる場合は通常形。それ以外は STR の固定文字列リテラル。
- 通常形: 通常形を優先しますが、安全に表せない文字では STR へフォールバック。
- 固定文字列: STR を明示的に選びます。

現在のラベルは固定のクラス名・関数名です。動的な TEXT を自動生成する理由はないため生成しません。通常の名前なら自動で SCOPE を選び、従来の `クラス名 関数名` 表示を維持します。非ASCII名のフォールバックも検証しました。

WPF のCPU画面に選択欄を追加しました。変更すると旧計画を無効にします。共通APIは `InstrumentationOptions.CpuMacro`、CLIは次のオプションです。

```powershell
dotnet run --project src/UEEtc.Console -- cpu "D:/Game/Source" --cpu-macro auto
dotnet run --project src/UEEtc.Console -- cpu "D:/Game/Source" --cpu-macro scope
dotnet run --project src/UEEtc.Console -- cpu "D:/Game/Source" --cpu-macro str
```

CPUの対象絞り込み、短いラベル、Super後の配置、既存計測の保持は維持します。追加済みのTRACE系呼び出しは処理呼び出し数の根拠から外し、カウンターを付けただけで短い関数がCPU対象になることを防ぎます。既存STRは静的に使われていれば有効なので、性能改善を理由に書き換えません。レポートへ CpuMacro / CpuScopeLabel、AddedPlainCpuScopes / AddedStringCpuScopes を出します。

## カウンター調査：累積と現在数では目的が違う

以下は同じ Engine の `Source/Developer/IoStoreUtilities/Private/IoStoreWriter.cpp` を読みました。

| 箇所 | 意味 | 減少が必要か |
| --- | --- | --- |
| 3946・3953行の IoStoreDDCHitCount / MissCount | 発生済みヒット・ミスの累積回数 | 減らすと回数の意味が失われる |
| 3364 → 3412行の IoStoreRefDbInflight | 非同期取得を開始し、完了コールバックで減少 | 要求の生存期間に対応して必要 |
| 3474 → 3493行の IoStoreCompressionInflight | 圧縮処理を開始し、最後のバッチ完了時に減少 | 関数から戻る時点ではなく、全バッチ完了時に必要 |
| 374 → 394行、427 → 441行 | バッチ件数を ADD し、各要求の完了ごとに DECREMENT | 一対一の隣接マクロではなく、同じ単位の要求ライフサイクル |

CountersTrace.h の399–403行では、INCREMENT / DECREMENT は宣言済みカウンターの増減へ展開されます。どちらを使うかはマクロの有無ではなく、**発生回数を残すのか、生存している数を追うのか**で決まります。

コールバック・別関数・オブジェクト破棄をまたぐ対応関係を、今回の字句解析だけで安全に決定することはできません。Engine の Inflight と同じように見える名前があるだけで、任意の関数末尾へ減少を入れることはしません。

### 実装した二つの計測モード

1. Cumulative（標準）: 選択分岐へ入った累積回数。INCREMENTのみです。
2. ActiveScope（明示選択）: 選択した分岐の中括弧の内側で実行中の数。入口でINCREMENTし、そのスコープを退出する際に同じカウンターをDECREMENTします。

WPFの「候補・名前・数量ADD」で「この分岐のスコープ現在数」を選びます。既定名を使っていた場合は識別子・表示名を ActiveScopes へ切り替えます。利用者が編集した独自名は保持します。モードと、それに伴う既定名の自動変更は保存せず、次の解析は累積回数から始めます。

CLIの選択行では `"Measurement": "ActiveScope"` を追加します。省略・Cumulativeなら従来どおり増加だけです。旧JSONとの互換性を保ち、未知のモードは入力エラーにします。

```json
{
  "Id": "discoveryから取得したId",
  "CounterName": "Work_ActiveScopes",
  "DisplayName": "AThing Work if1 ActiveScopes",
  "Measurement": "ActiveScope"
}
```

生成する本体は次の形です。CountersTrace.h に加えて Misc/ScopeExit.h を無条件の include 領域へ追加し、既存の include は重複させません。

```cpp
TRACE_COUNTER_INCREMENT(Work_ActiveScopes);
ON_SCOPE_EXIT
{
    TRACE_COUNTER_DECREMENT(Work_ActiveScopes);
};
```

UEのScopeExit.hはRAIIガードのデストラクターで処理を実行します。分岐末尾に単にDECREMENTを書く方式と異なり、早期returnや例外による巻き戻しにも対応します。後から作るローカル変数が破棄された後に減少させるため、後片付けも生存区間に含めます。強制終了等でC++のデストラクター自体が動かない場合まで保証するものではありません。

**このモードは非同期要求の未完了数やオブジェクトの生存数ではありません。** 非同期開始関数へ付けても、その場の分岐の退出で減少します。複数箇所の開始・完了対応を推測する機能は実装していません。必要な計測目的を選び、累積イベントへ無条件に減少を足すことを避けます。旧Consoleの関数入口カウンターも累積のままです。

数量ADDは独立した累積入力数量なので、ActiveScopeと併用しても数量は減らしません。トレース無効構成では増減マクロは空になりますが、追加したON_SCOPE_EXITは空処理のガードとして残ります。最適化されていない構成を含めたゼロコスト保証はしていません。

レポートへ CounterMeasurement / DecrementAdded / AddedDecrements を追加しました。再解析では既存の増減操作を保持し、未選択の他の分岐を引き続き候補にできます。

## 検証結果

- Core 109/109、WPF 103チェック、Releaseビルドは警告0・エラー0。
- MSVC `/W4 /WX`: 従来12構成、CPUの自動・通常・STR各4構成、累積／ActiveScopeを有効／無効で4構成、計28構成。
- CPUテストは三つの選択で同じ表示名とSuper後の区間を確認。ScopeExitはEngineの実ヘッダーをコピーしてコンパイルし、通常退出・早期return・再帰・例外・ローカル破棄の順序を確認しました。
- ScopeExitテストの累積モードは7回の進入を保持し、現在数モードは増加7回・減少7回、最大3、終了後0を確認。無効時はすべて0です。

| 実ソースのコピー実験 | Lyra | BlockBreaker |
| --- | ---: | ---: |
| CPU自動：通常形／STR | 451／0 | 93／0 |
| CPUのSTR指定：通常形／STR | 0／451 | 0／93 |
| ActiveScopeの構文負荷検証：増加／減少 | 1525／1525 | 423／423 |

三実験とも差分適用確認が通り、コピーの再解析による追加変更・再提示候補は0、診断0、元599ファイルのSHA-256は一致しました。全分岐へのActiveScope追加は構文上の負荷検証であり、全分岐を現在数として計測すべきという推奨ではありません。

Engine／ゲーム元ソースへの適用、UE全体のビルド、Insightsでの実測・オーバーヘッド比較は行っていません。通常形とSTRの差を性能数値で評価した結果ではありません。

再現: `scripts/Test-ScopedEventCounters.ps1`、`scripts/Test-CpuScopePolicy.ps1 -CpuMacro auto|scope|str`、`scripts/Invoke-TraceExperiments.ps1 -Focused -CpuMacro auto|str`、`scripts/Invoke-EventCounterExperiments.ps1 -Measurement ActiveScope`。
