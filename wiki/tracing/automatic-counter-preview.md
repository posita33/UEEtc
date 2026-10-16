---
date: 2026-10-16
tags: [csharp, unreal-engine, tracing, wpf]
status: todo
---

# 解析時の ADD・DECREMENT 自動設定（Issue #35）

## 操作

イベントカウンター画面でソースのフルパスを指定し、「解析・プレビュー」を押す。
アプリが候補・名前・数量・スコープ計測を設定し、付与後プレビューに追加内容、件数、判断理由、変更行を表示する。
通常は数量や DECREMENT のチェックボックスを操作する必要はない。
元ソースは適用操作まで変更しない。適用ファイルのチェックは従来どおり初期0件。

Issue #37でファイル一覧に `DECREMENT` と `ADD` の付与予定数の列を追加した。
「含む」が有効な候補だけを集計し、候補の除外や詳細設定の変更に即時追従する。
適用ファイルのチェックが外れていても予定数を表示するため、適用前に対象を比較できる。
既存コードのマクロ総数ではなく今回追加する予定数であり、数量の手動設定が未完了の候補も含む。

Issue #39では候補が多いと説明欄が途中で切れる不具合を修正した。
GridのAuto行だけにMaxHeightを設定すると、子のScrollViewerが内容全体の高さで測定され、
表示は切り詰められてもスクロール範囲ができなかった。
高さ制限をScrollViewer自身に移し、説明欄は最大160、コード欄は残りの高さで個別にスクロールする。
150候補・付与後909行のソースで修正前のスクロール範囲0を再現し、
修正後に説明の最終候補とコードの最終行まで到達できることをWPFで検証する。

旧「候補・名前・数量ADD」タブは「候補・詳細設定」に改称した。
候補の除外は一覧で行い、名前・値・計測方式を変更する場合だけ、折りたたまれた「詳細設定」を開く。
自動設定後に編集した値・単位は従来の確認が必要。解析をやり直すと自動推奨を作り直す。
プレビューの「解析時の判断」は初期推奨の理由、「付与するマクロ」は現在の設定を示す。

## 自動設定の条件

| ソース上の根拠 | 生成する計測 |
| --- | --- |
| 安全な型の数量引数を1つ特定できる（NumBytes等） | 累積進入回数と、入力数量の ADD |
| const UE 配列の Num() が利用でき、分岐直下の for がその配列を処理し、数量候補が1つ | 上記に加え、別の ActiveScopes の入口 INCREMENT と退出 DECREMENT |
| 数量が曖昧、参照・型・構文が対象外、対応区間を推定できない | 累積進入回数のみ |

配列の型・事前利用・直接ループの制約は[数量推奨の調査](event-files-and-input-quantities.md)を引き継ぐ。
ラムダ内だけにあるループや複数の数量候補は自動設定しない。
DECREMENT の自動対象をバッチに絞る方針は本ツールの保守的な推奨であり、Engine がすべてのバッチに同じガードを置くという意味ではない。

既存 Engine の実例は[生存区間の調査](cpu-macros-and-counter-lifetimes.md)に記録している。
IoStoreWriter の Inflight や DDC のリクエスト数は、投入と完了を対応させて増減する。
一方、累積ヒット件数や入力バイト数には退出時の減算を付けない。
この違いに合わせ、自動追加する現在数は累積カウンターから分離した。

```cpp
TRACE_COUNTER_INCREMENT(F_Batch_If1_Count);
TRACE_COUNTER_INCREMENT(F_Batch_If1_ActiveScopes);
ON_SCOPE_EXIT
{
    TRACE_COUNTER_DECREMENT(F_Batch_If1_ActiveScopes);
};
TRACE_COUNTER_ADD(F_Batch_If1_InputItems, Items.Num());
```

ActiveScopes はこの分岐の C++ スコープに入ってから退出するまでの現在数。
return・例外でもガードが減算する。再入・並行実行時には重なったスコープの数となる。
非同期タスクの完了待ち数、オブジェクトの生存数、配列の残数は表さない。
ループ内で非同期処理を起動していても、起動した仕事の完了まで追跡する機能ではない。
InputItems は分岐入口の入力要素数で、成功件数・実処理件数とは区別する。
単一スレッドで再入しない処理では現在数は主に0/1になる。費用対効果は実際のトレースで判断する。

## 共通実装

- `EventCounterDefaults.Create` が自動の選択案、`Explain` が根拠を提供する。
- `QuantityRecommendation.IsDirectBatch` が直接バッチの根拠を保持する。
- `EventCounterSelection.IncludeActiveScope` で、累積回数に独立した現在数を追加する。
- 既存の `Measurement: ActiveScope` は手動互換として残す。同時指定は検証で拒否する。
- 現在数の識別子・表示名も既存の衝突回避に含め、必要な `Misc/ScopeExit.h` を追加する。
- JSONレポートの `ActiveScopeCounter` に別カウンター名を記録する。DECREMENT件数にも含める。

Console でも同じ方針を使用できる。既存の選択済み候補に対して実行する。
空の選択ファイルを自動で全選択にはしない。
手動の Quantity は維持する。`--recommended-add` は従来どおり数量のみ追加する。

```powershell
dotnet run --project src/UEEtc.Console -- counters "D:/Game/Source" --selection "D:/Reports/selection.json" --recommended-counters --report "D:/Reports/preview"
```

## 検証

- Release ビルド: 警告0・エラー0。
- Core 114/114、WPF 109チェック。解析ボタンのみでADD/DECREMENTが現れ、手動設定が閉じていること、累積回数には減算しないこと、名前の衝突回避、再解析、元ソース不変を確認。
- 実 Engine の `ScopeExit.h` と計測スタブを使い、MSVC C++20 `/W4 /WX` で累積・現在数・別現在数の3方式×カウンター有効/無効の6構成を実行。
  早期return・再帰・例外・ローカル変数破棄を確認。別現在数方式では累積7、現在数の最大3、退出後0を確認した。
- `Invoke-EventCounterExperiments.ps1 -RecommendedCounters`: LyraはADD4・DECREMENT4、BlockBreakerは両方0。
  コピーへの全候補生成による構文ストレス実験で、診断0、差分適用チェック成功、二度目の候補0。
  元ソース599ファイルのSHA256が実験前後で一致。
- 4箇所は Lyra の SpawnEquipmentActors / UpdateInteractions / GetFirstRandomUnoccupiedPlayerStart / AddUnconfirmedServerSideHitMarkers。
  元の Unreal プロジェクトのビルド・Insights実測は本検証に含めない。
