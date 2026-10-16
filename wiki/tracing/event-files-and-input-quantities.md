---
date: 2026-10-16
tags: [wiki, implementation, wpf, unreal-engine, tracing]
status: todo
---

# ファイル選択・数量の自動追加・短い表示名（Issue #27〜#30）

最新の操作とADD・別スコープDECREMENTの自動設定は [Issue #35](automatic-counter-preview.md) を参照してください。以下のタブ名は実装当時の記録です。

累積回数とスコープ現在数の切替、DECREMENTの条件は [Issue #32 の追加実装](cpu-macros-and-counter-lifetimes.md) を参照してください。

[目次](../README.md) / [変更プレビューとインデント](preview-selection-and-indentation.md)

## 調査と対応

| Issue | 調査結果 | 対応 |
| --- | --- | --- |
| #27 | 小さいチェック欄だけが選択操作の入口で、一覧は単一行選択だった | 大きいチェック領域、複数行、全選択・全解除・強調行の選択と解除・Space切替 |
| #28 一覧 | 左一覧が分岐単位で同じファイルを何行も表示していた | 左をファイル単位にし、詳細タブにそのファイルの候補を配置 |
| #28 例外 | ADD有効化直後の値・単位未入力でも Build が ArgumentException を生成していた | UI用の TryBuildPreview が生成前に検証し、入力エラーを値として返す |
| #28 推奨 | 値引数だけを候補にしており、Engine が使う配列 Num() を扱っていなかった | 型が限定できる入力配列と、その配列を処理する直接のループを確認して推奨 |
| #29 | 挿入コメントと表示名にアプリ名の固定文字列があった | 新規生成コードからコメントを除去し、固定のアプリ名やパスを表示名へ含めない |
| #30 | カウンターの表示名がパスから始まっていた | クラス・関数・分岐を先頭にした短い既定値へ変更 |

## 一覧の操作

CPU・イベントの両画面で、左側の一覧はファイル単位です。初期の適用選択は0件です。

- 「全選択」「全解除」: 検索で隠れたファイルも含めます。候補のないファイルは選択対象外です。
- 「行を選択」「行を解除」: 強調表示した行を適用対象にする・外す操作です。Ctrl / Shift で複数行を強調できます。
- Space: 強調行がすべてチェック済みなら解除し、それ以外は選択します。
- 「表示中を選択」「表示中を解除」: 検索結果だけに作用します。

チェック欄はセル幅まで操作領域を広げ、高さを28以上にしました。行を強調してプレビューを見る操作と、適用のチェックは区別します。

イベントの「候補・名前・数量ADD」には、強調したファイルの分岐候補を表示します。「含む」は既定で有効で、特定の分岐だけ除外できます。左のファイルをチェックすると、含まれる候補をまとめて適用します。候補のないファイルも元ソースの閲覧用に一覧へ出します。

適用・レポート保存の計画はチェックしたファイルだけを含みます。検索で隠れたチェックは維持します。ファイルを一つも選んでいない場合は全候補の参考プレビューとなり、適用・保存は無効です。ファイル選択と候補の除外状態は保存せず、名前の手入力は従来どおり保存します。

## ADD 入力エラーの修正

従来は RefreshPreview で例外を受け止めていましたが、生成処理内で ArgumentException 自体が発生していました。今回、入力途中のエラーを例外として発生させる経路をなくしました。

共通コアの `TryBuildPreview` は値・単位・識別子・表示名等を生成前に検証します。正常なら Plan、入力エラーなら Errors を返します。チェックを入れただけで値や単位が空なら、画面に不足項目を表示して適用を無効にします。手入力を編集した場合の意味確認と、未確認計画の Core.Apply による拒否は維持します。

API の厳密な `BuildPlan` / `BuildPreview` は不正な要求に対して引き続き例外を返します。UI は TryBuildPreview を使います。回帰テストでは ADD を空の状態で有効にし、FirstChanceException を監視して EventCounterService の ArgumentException が0件であることまで確認しました。

## Engine の調査から広げた数量推奨

パスは `C:/Program Files/Epic Games/UE_5.8/Engine/` 基準です。

- `Source/Developer/DerivedDataCache/Private/HttpCacheStore.cpp:3156, 3179, 3234, 3257, 3325`: Requests.Num() で入力バッチ件数を加算し、その後で各要求を処理します。成功ヒット数は応答側の別カウンターです。
- 同ファイル `2771–2772, 2870–2871`: 通信の物理読込・書込サイズは完了時の統計から加算します。入力数量と実際の処理量は同一ではありません。
- `Source/Runtime/Core/Public/Containers/Array.h:1388–1390`、`ArrayView.h:441–443`: Num() は保持された要素数を返す const 読み取りです。
- `Source/Runtime/Core/Public/Containers/ContainersFwd.h:72`: TConstArrayView の既定のサイズ型は int32 です。
- `Source/Developer/IoStoreUtilities/Private/IoStoreWriter.cpp:374, 427, 551`: 処理中件数・メモリーには別箇所での減算が必要です。今回の累積入力数量とは区別します。

この実例をもとに、今回自動で追加するものの意味を **分岐入口での入力数量** と固定しました。Insights の表示名にも InputItems / InputBytes を付けます。成功数・完了数・実処理量を推定したものではありません。例えばループ途中で break すれば、入力要素数と実際に処理した件数は異なります。

### 推奨条件

従来の既知の符号なし32ビット以下の値引数と数量名の条件に加え、次を扱います。

1. 単純な要素型を持つ `const TArray<T>&` または `TConstArrayView<T>` の値・参照引数。
2. 挿入前にその引数が未使用か、Num() / IsEmpty() の読み取りだけに使われていること。別処理へ渡した、代入した、ローカルで同名を宣言した等の曖昧な場合は除外します。
3. 対象分岐の直下に、その配列を range-for または Num() による for で処理すること。内側の別の if やラムダだけで処理する場合、外側へは推奨しません。
4. 推奨数量が一つだけであること。複数あって選べない場合は自動で決めません。

安全な値の一覧 AddValues と、自動追加すべき場所 RecommendedQuantity は区別します。配列の型が分かるだけでは自動追加しません。入れ子の要素型・カスタムアロケーター・任意メンバー・一般の関数式は対象外です。Size のように単位が曖昧なスカラー名も推奨しません。C++の型解決・マクロ展開・並行更新を完全に検証する解析ではありません。

WPF は推奨を InputItems / InputBytes として有効化し、ファイル選択後は追加の手入力なしで適用可能です。手動で値・単位を変えたら確認を解除します。推奨は解析ごとに再計算します。Console は次のフラグで、選択済み候補のうち Quantity を手入力していないものへ同じコアの推奨を追加します。

```powershell
dotnet run --project src/UEEtc.Console -- counters "D:/Game/Source" --selection "D:/Reports/selection.json" --recommended-add --report "D:/Reports/automatic"
```

CLI の初期選択が空という動作は維持します。手入力の Quantity は上書きしません。

## Lyra で確認した4箇所

Lyra Source 基準のパスです。

| ファイル・行 | 関数 | 入力数量と意味 |
| --- | --- | --- |
| `LyraGame/Equipment/LyraEquipmentInstance.cpp:76` | SpawnEquipmentActors | ActorsToSpawn.Num()。装備生成へ渡した要素数で、生成成功数ではない |
| `LyraGame/Interaction/Abilities/LyraGameplayAbility_Interact.cpp:46` | UpdateInteractions | InteractiveOptions.Num()。IndicatorManagerが存在する分岐で扱う入力オプション数 |
| `LyraGame/Player/LyraPlayerSpawningManagerComponent.cpp:178` | GetFirstRandomUnoccupiedPlayerStart | StartPoints.Num()。Controllerが存在する場合に検査する開始地点候補数 |
| `LyraGame/Weapons/LyraWeaponStateComponent.cpp:108` | AddUnconfirmedServerSideHitMarkers | FoundHits.Num()。投影や表示判定の前の入力件数。確定ヒット・表示成功数ではない |

Args が存在するだけのコンソールコマンドの早期returnや、配列を扱わない内側の型チェック分岐は推奨しません。BlockBreaker はこの限定条件に合う推奨が0件でした。

## アプリ名を含めない短い生成名

新規挿入するコメント自体を除去しました。既存の元ソースにあるコメント・表示名・既存計測は書き換えません。アプリ名が必要なバックアップディレクトリ等はソース文字列ではないため変更していません。

既定のイベント表示名は `AThing Work if1` / `AThing Work else2`、数量は `AThing Work if1 InputItems [items]` のようになります。ファイル名・フォルダーパス・アプリ名を含めません。同じ短いラベルが衝突する場合だけ末尾に識別用IDを足します。旧Consoleの進入回数も `AThing Work Calls [ID]` とし、必要な情報を先頭へ寄せます。CPUは従来どおり `AThing Work` です。

パス・行・完全な関数名・安定IDは解析レポート側に保持します。カウンターのC++識別子も従来の衝突回避を維持します。保存されている旧自動生成のパス付き表示名はWPFで新しい既定値へ移行し、利用者が付けた独自名は保持します。古い選択JSONの明示的な表示名は上書きしないため、CLIでは必要に応じてdiscoveryから作り直してください。

## 検証

- Releaseビルド: 警告0・エラー0。Core 101/101、WPF 97チェック。
- WPF: ファイル単位化、全体・表示中・強調行・Space選択、選択ファイルだけの適用とバックアップ、空ADDで例外が発生しないこと、旧ラベル移行、手入力の確認解除等。
- MSVC `/W4 /WX`: 従来12構成、CPU配置4構成、手入力数量2構成、バッチ自動数量2構成。有効・無効時の処理結果を維持。バッチテストは3回のイベントに対して、実際に配列を処理する分岐だけへ1個のADDを生成し、入力2itemsを集計。
- Lyra: イベント1525候補、推奨ADD4箇所。BlockBreaker: 423候補、推奨ADD0箇所。全候補の適用はコピー上の構文検証で、有用な全イベントの推奨ではありません。
- 推奨ADDを含む差分の適用確認が通り、コピー再解析の追加候補0。CPUの451／93箇所も追加変更0。いずれも診断0、元599ファイルのSHA-256一致。

Engine・ゲーム元ソースへの適用、UE全体のビルド、Insightsでの実測は行っていません。再現には `scripts/Test-EventCounters.ps1 -Batch` と `scripts/Invoke-EventCounterExperiments.ps1 -RecommendedAdd` を使用します。
