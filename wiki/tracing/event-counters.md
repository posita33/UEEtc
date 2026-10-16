---
date: 2026-10-16
tags: [wiki, research/tech, architecture, csharp, wpf, unreal-engine, tracing]
status: todo
---

# イベントカウンターの付与条件と機能分離

再オープンした Issue #10 で[候補の有用性を再調査](counter-effectiveness-review.md)しました。以下の全候補列挙や 7 箇所の選択例は実装・構文検証の履歴で、新しい推奨候補とは扱いません。特に可否チェックの拒否回数と実際の操作失敗は別の測定です。現行コードの候補抽出はまだ変更していません。

CPU 側は Issue #12・#13 で短いラベルと Super 後の配置へ変更しています。[CPU の現行規則](cpu-scope-policy.md)を参照してください。以下の構成・イベント機能の操作は引き続き利用できます。

[目次](../README.md) / [Issue #10](https://github.com/posita33/UEEtc/issues/10) / [従来の独立判定](independent-trace-policy.md)

## 調査結果

`TRACE_COUNTER_INCREMENT` は、計測したい事象が起きた箇所でカウンターを 1 増やす操作です。Engine は全関数へ一律には挿入せず、試行、結果の判定、復旧、キューの出入りなど、それぞれの意味が確定する位置を選んでいます。CPU 時間とは独立しています。

[Epic の Trace Developer Guide](https://dev.epicgames.com/documentation/en-us/unreal-engine/developer-guide-to-tracing-in-unreal-engine) でも、Counters は名前付きの値を時系列で追い、増分・減分・SET 等を使い分ける仕組みとして説明されています。回数は INCREMENT、書き込んだバイト量は ADD という区別も、今回の「累積回数に限定する」判断に対応します。

今回、ローカルの `C:/Program Files/Epic Games/UE_5.8/Engine/Source` と `Engine/Plugins` を検索し、次の実例では分岐条件・前後の処理・対応する減算も読みました。行番号はこのインストールの 2026-10-16 時点です。Engine のコードは転載せず、観察した使い方を記録します。

| 目的 | Engine 内の位置 | 観察した付与条件 | ツールへの反映 |
| --- | --- | --- | --- |
| 試行数とヒット数 | `Source/Developer/DerivedDataCache/Private/S3CacheStore.cpp:1686,1692,1730,1737` | 試行の区間入口と、検索に成功した分岐内で異なるカウンター。複数の検索経路が同じ Get / GetHit を共有 | 試行と結果を混同しない。ヒット率には母数の定義も必要 |
| キャッシュの結果 | `Source/Developer/IoStoreUtilities/Private/IoStoreWriter.cpp:3946,3953,4034` | DDC のヒット／ミス分岐、Put の成功を確認した分岐 | 結果分岐が有用。単に関数へ入ったことを成功とは呼ばない |
| HTTP 成功／失敗／取消 | `Source/Runtime/Experimental/IoStore/OnDemand/Private/OnDemandContentInstaller.cpp:1309,1313,1328,1393` | 取消と失敗を区別し、成功は HTTP 状態だけでなく内容のハッシュ検証後に記録 | 名前に Success があっても、何の検証を通過したか確認する |
| 処理の開始 | `Source/Developer/IoStoreUtilities/Private/IoStoreWriter.cpp:4017,4079,4104` | 待機を終えて圧縮・暗号化・書き込みの段階へ進む箇所 | 開始と完了は別の事象。今回の自動候補は分岐内に限定 |
| 異常からの復旧 | `Source/Runtime/Engine/Private/Streaming/SimpleStreamableAssetManager.cpp:564` | 既存の生きた登録を検出し、自己修復する分岐 | 再入、想定外のフォールバック、復旧の頻度は有用な候補 |
| 閾値による経路変更 | `Source/Runtime/PakFile/Private/IoDispatcherFileBackend.cpp:316,344` | 遅延・シーク距離の条件によるキュー選択変更 | 通常経路と例外経路を分けて観測する |
| キュー長 | `Source/Runtime/Core/Private/IO/IoDispatcherChunkDecoder.cpp:29,90,112,149,188` | 追加時の増分と取り出し時の減分が対応 | 累積回数とは別。INCREMENT だけでは再現しない |
| プールの現在個数 | `Plugins/Runtime/Metasound/Source/MetasoundGenerator/Private/MetasoundOperatorCache.cpp:303,381` | 追加で増分、取得で減分。プロファイル用フラグ内 | 現在個数・在庫・処理中件数は今回の自動追加対象にしない |

`Source/Runtime/Core/Public/ProfilingDebugging/CountersTrace.h:311,399` では、宣言がカウンター実体を作り、INCREMENT がその実体の `Increment()` を呼びます。引数は表示文字列ではなく識別子です。Atomic Int は同ファイルの `FCounterAtomicInt` で atomic 整数を使っています。今回の生成は複数スレッドから呼ばれる可能性を考え Atomic Int に統一しました。複数カウンターをまとめた原子的更新や、別イベント間の順序保証にはなりません。

Engine の複数箇所で同じ名前を使う理由は、同一の論理的な測定対象へ加算するためです。[命名・共有の既存調査](counter-naming.md)も参照してください。今回の候補間で同じ名前を入力しても、意味が同じとは判断せず衝突回避名を割り当てます。共有カウンターの設計や `EXTERN` による複数ファイル共有は自動生成しません。

## 実装する付与条件

Engine の実例から「成功分岐、拒否・ガード、フォールバック、状態遷移」を確認する機能にしました。ただし、名前からゲームの意味を確定することはできないため、候補提示と利用者による選択を分けています。

1. ソースフォルダーのフルパスから `.cpp` を再帰探索する。生成コード、ThirdParty、ビルド出力、バックアップ、リンクは従来どおり除外する。
2. 通常の関数の中から、非空で波括弧付きの `if` / `else` 分岐を候補にする。`else if` は独立した条件として扱い、括弧なしの文を包み直さない。
3. 条件式の単語からキャッシュ、結果・ガード、再試行・復旧、状態遷移のヒントを表示する。単語の一致による推測であり、成功率、重要度、呼び出し頻度、コストを証明するものではない。一般の条件分岐も提示する。
4. 選択は初期状態で 0 件。元の条件式と前後のソースを見て、数える目的を確認し、識別子・Insights 表示名を編集する。
5. 選んだ分岐の `{` の直後へ増分を挿入する。実際に数えるのは、その分岐へ入った回数。分岐内の処理が後で失敗しても増分済みなので、結果を意味する名前は条件で結果が確定している場合だけ使う。
6. 必要な Counter ヘッダーと、ファイル内の匿名名前空間への Atomic Int 宣言を追加する。CPU マクロは追加しない。

分岐がループより外側にあり、その分岐内部にループがある場合は分岐への進入を数えます。ループの内側の分岐は、件数が膨らむため今回の候補から除外します。頻度・オーバーヘッドの効果測定は UE 上で別途必要です。

同じ分岐の直下に既存 `TRACE_COUNTER_...(...)` がある場合は、SET / ADD 等も含めてその分岐を候補にしません。ネストした別分岐のカウンターは親分岐の候補を妨げません。CPU 計測の有無も候補を妨げません。再解析すると付与済み分岐は候補から外れ、未選択分岐の ID は維持されます。

### 自動処理を限定する構文

字句解析であり、C++ コンパイラーではありません。テンプレート、コンストラクター／デストラクター、クラス内 inline、operator、requires、function-try-block、コルーチン、constexpr / consteval 関数は自動付与しません。`if constexpr` / `if consteval` を含む関数も除外します。

条件コンパイル内の関数、関数本体にプリプロセッサー指令があるもの、ラムダ、switch、catch、ループ内、任意の独自マクロの展開、ラベル位置への挿入は扱いません。宣言より前の無条件 `#include` がないファイルも候補にしません。除外された関数があること自体はエラーではなく、全体の構文不整合・読めないファイル等は診断にします。

候補のヒントが少なくても、ゲーム固有の事象を把握した利用者は一般の条件分岐から選べます。反対に、現在 HP、残機、処理中件数、キュー長、バイト量を数える場合は `SET` / `ADD` / `DECREMENT` を含む別の設計が必要です。この機能の出力は累積の分岐進入回数だけです。

## WPF の操作

起動時はメイン画面を表示し、CPU とイベントカウンターのボタンからそれぞれ別のウィンドウを開きます。機能ごとのフォルダー指定・選択・プレビューは独立しています。CPU 画面の進入回数チェックは削除しました。

イベント画面ではソースフォルダーを指定して「候補を解析」を押します。パス・関数名・条件式・ヒントで絞り込み、候補行を選ぶと前後のソースを表示します。チェックした箇所と名前を確認し「選択した箇所をプレビュー」を押すと、選択済みの場所だけを加工した案ができます。絞り込みで隠れた行のチェックも選択として維持されます。

名前・選択・ソースフォルダーを変更すると前の変更案を無効化し、再プレビューを要求します。実際に割り当てられた衝突回避名は付与後のソースに表示します。保存はレポート・差分・変更ファイルのコピーだけです。明示的な「元ファイルへ適用」だけが確認ダイアログとバックアップを経て元ファイルを書き換えます。

## Console の操作

リポジトリルートの PowerShell で実行します。出力先は解析対象の外にある、毎回別の空のフォルダーにしてください。

```powershell
# CPU のみの解析
dotnet run --project src/UEEtc.Console -- cpu "D:/Projects/posita/git/Game/BlockBreaker/Source"

# イベント候補を調べる。この段階では選択 0 件
dotnet run --project src/UEEtc.Console -- counters "D:/Projects/posita/git/Game/BlockBreaker/Source" --report "D:/TraceReports/discovery"
```

`candidates.json` に Id、相対パス、関数、行、If / Else、条件式、ヒント、理由、SuggestedName、DisplayName を出力します。`selection.json` の RootPath / Fingerprint は保持し、確認した候補だけを `Selections` に追加します。形式の例は次のとおりです。Id は対象の `candidates.json` の実際の値を使います。

`selection.json`:

```json
{
  "RootPath": "D:\\Projects\\posita\\git\\Game\\BlockBreaker\\Source",
  "Fingerprint": "解析結果の値を保持",
  "Selections": [
    {
      "Id": "候補の実際のId",
      "CounterName": "BallSpawnSucceeded",
      "DisplayName": "BlockBreaker/Ball/SpawnSucceeded"
    }
  ]
}
```

選択後のプレビューも元ソースを書き換えません。

```powershell
dotnet run --project src/UEEtc.Console -- counters "D:/Projects/posita/git/Game/BlockBreaker/Source" --selection "D:/TraceReports/discovery/selection.json" --report "D:/TraceReports/preview"
```

候補 ID は相対パス・関数宣言・関数内の候補順序・分岐の種類・条件式から作ります。Fingerprint は `.cpp` のパスとバイト列、探索で予約した識別子を照合します。ソースやヘッダーの予約名が変わると、古い選択ファイルを拒否します。重複 ID、不明な ID、不正な識別子、空の名前、制御文字を含む表示名も拒否します。

実際に元ソースへ書き込むときだけ `--apply` を追加します。イベント用の適用には `--selection` が必須です。診断がある変更案は適用できません。適用時は元バイト列の一致も検査し、`.ueetc-backups` に全変更ファイルのバックアップを作ってから書き込みます。

旧形式の `<root> --counter-only` と `<root> --both` は従来の関数進入回数モードとして維持し、互換機能である旨を標準エラーに表示します。新しい `cpu` コマンドではこれらを受け付けず、カウンターは別の `counters` コマンドで扱います。

## 名前空間と切り出し方

```text
src/UEEtc.Core/
  Features/FunctionTracing/    UEEtc.Core.Features.FunctionTracing
  Features/EventCounters/     UEEtc.Core.Features.EventCounters
  Source/                     UEEtc.Core.Source
  Plans/                      UEEtc.Core.Plans
  Reporting/                  UEEtc.Core.Reporting
src/UEEtc.Wpf/
  MainWindow.xaml             機能選択だけを担当
  Features/CpuTracing/         UEEtc.Wpf.Features.CpuTracing
  Features/EventCounters/      UEEtc.Wpf.Features.EventCounters
```

FunctionTracing は CPU と互換用の旧進入回数モード、EventCounters は候補・選択・イベント付与を所有します。機能同士に参照はありません。共通部分はソースの文字コード保持、字句処理、再帰探索、変更案、バックアップ付き適用、レポート出力です。共通層から機能層への参照もありません。従来 Core 直下だった型の名前空間は上記へ変更しています。

新機能を追加するときは Core と WPF の `Features/<機能名>` に閉じて実装し、メイン画面にはその機能を開くボタンを追加します。既存の CPU 画面やイベント候補処理へ分岐を足す必要はありません。将来プロジェクトへ分割する場合も、共通層を参照する形に切り出せます。今回は DLL を増やさず、ディレクトリと名前空間による分離です。

イベントのコア API は `EventCounterService.Analyze` → `BuildPlan` です。WPF と Console が同じ API を使います。解析結果は `Feature = EventCounters` と `AddedEventCounters`、各追加箇所の `EventCounterAdded` を持ちます。`Functions` の 1 行は、この機能では 1 分岐への挿入箇所であり、同じ関数に複数行がある場合があります。従来名の `AddedFunctions` もその行数です。

識別子は通常、関数名と分岐番号が分かる自然な形を提案し、利用者が変更できます。`UEEtcCalls_` は付けません。同名候補・既存の識別子・`__GTraceCounter` 展開名との衝突時だけ ID 等を付加します。同じ Insights 表示名が複数候補に付いた場合も ID を付加して区別し、自動で共通のカウンターにはまとめません。生成した宣言を匿名名前空間へ置くのは、翻訳単位外から参照させないためです。

## 実ソースを読んで選んだ例

自動的な推奨ルールではなく、2026-10-16 のソースを読んで選んだ例です。すべて分岐への到達時点で計測します。

| 対象 | 関数・元の行 | 選んだ条件 | 名前 | 読めること・限界 |
| --- | --- | --- | --- | --- |
| BlockBreaker | `SpawnNewBall:60` | BallClass / PlayerController 不足 | `BallSpawnInvalidArguments` | 引数不足で処理を止めた回数。全スポーン失敗を表すものではない |
| BlockBreaker | `SpawnNewBall:76` | SpawnActor が非 null を返した If | `BallSpawnSucceeded` | Actor 生成成功。その後のパドルの Catch 成功は含意しない |
| BlockBreaker | `OnBrickDestroyedEx:96` | 登録集合からの除去で残数が変わらなかった If | `BrickRemovalFallbacks` | フォールバックへの到達。重複通知、未登録、null 等の原因を区別するには追加調査が必要 |
| BlockBreaker | `CheckClearCondition:416` | bShouldClear の If | `RoundClearDecisions` | クリアと判定して進行ロックを立てる経路。クリア演出完了数ではない |
| BlockBreaker | `PlayerDied:308` | RemainingLives > 0 の Else | `PlayerDeathGameOverDecisions` | GameOver 呼び出しへ進む判定。死亡通知全体の件数とは異なる |
| Lyra | `DoesAbilitySatisfyTagRequirements:404` | bBlocked の If | `AbilityTagBlockedRejections` | タグ要件チェックの blocked 拒否。発動試行全体の母数ではない |
| Lyra | 同上 `:412` | bMissing の If | `AbilityTagMissingRejections` | blocked 判定を通過した後の missing 拒否。両方立ったケースは先行する blocked 側に含まれる |

BlockBreaker は `BlockBreaker/Private/Framework/GameModes/BlockBreakerGameMode.cpp`、Lyra は `LyraGame/AbilitySystem/Abilities/LyraGameplayAbility.cpp` です。元ソースは変更せず、5 箇所／2 箇所を選んだ差分とコピーだけを出力しました。`scripts/Invoke-ReviewedEventCounterExamples.ps1` で再現できます。条件が変わった場合は一致を確認できず停止し、再確認を促します。

## 検証結果

| 検証 | 結果 |
| --- | --- |
| Release ビルド | 警告 0 / エラー 0 |
| Core | 既存 42 + イベント 23、計 65 テスト成功 |
| WPF | 27 チェック成功。ランチャー、別画面、CPU 専用、候補選択、名前編集、絞り込み、変更案の無効化、元ファイル維持 |
| C++ 既存モード | CPU / 進入回数 / 両方 × 有効・無効の 4 組、計 12 回の MSVC C++20 `/W4 /WX` コンパイル・実行成功 |
| C++ イベント | 確認した 3 分岐だけへ追加し、Counter 有効／無効の 2 回のコンパイル・実行成功。返り値と実行された増分回数を検証 |
| 実ソースの選択例 | BlockBreaker 5 箇所、Lyra 2 箇所。差分の適用チェック成功、元ソース維持 |

さらに、構文と再付与防止の負荷確認として、認識した全候補を生成コピーだけに付与しました。全選択はテスト専用であり、ゲームへの推奨設定ではありません。各結果は実験スクリプトの出力に記録します。

| 対象 | 解析 .cpp | 候補・追加箇所 | 変更コピー | 付与後コピーの再提示候補 | 診断 |
| --- | ---: | ---: | ---: | ---: | ---: |
| Lyra | 233 | 1,525 | 161 | 0 | 0 |
| BlockBreaker | 47 | 423 | 38 | 0 | 0 |

今回の生成物（Git 管理外）:

- 全候補の負荷確認: `artifacts/event-experiments/20261016-112536/`
- ソースを読んだ選択例: `artifacts/reviewed-events/20261016-112541/`
- イベント C++ コンパイル: `artifacts/event-cpp/85980ea9b9ba420689aea046888c9f93/`
- 旧モード C++ 回帰: `artifacts/cpp-smoke/d12fe2d64bea412892a24aa1789f2597/`
- WPF の表示確認: `artifacts/event-counters/ui-final/`

元ソース全 599 ファイルの SHA-256 を比較し、一致しています。UE の実ビルド・Insights での取得、ゲームの負荷測定は未実施です。今回確認したのは構文、選択位置、名前、分岐動作、再解析、元ソース保持です。

再現コマンド（リポジトリルート）:

```powershell
dotnet build UEEtc.sln -c Release
dotnet run --project tests/UEEtc.Core.Tests -c Release --no-build
dotnet run --project tests/UEEtc.Wpf.SmokeTests -c Release --no-build
./scripts/Test-CppInstrumentation.ps1
./scripts/Test-EventCounters.ps1
./scripts/Invoke-EventCounterExperiments.ps1
./scripts/Invoke-ReviewedEventCounterExamples.ps1
```
