---
date: 2026-10-16
tags: [wiki, unreal-engine, tracing, research/tech, refactoring, csharp, wpf]
status: todo
---

# Engineとの整合性監査と責務の分離（Issue #47）

[目次](../README.md) / [付与ロジックとEngine参照](current-feature-logic.md) / [統合画面](combined-tracing.md)

## 結果

固定名CPUタイマー、宣言済みatomicカウンターへのINC/ADD/DEC、ON_SCOPE_EXITによる通常のスコープ退出処理は、UE 5.8のAPIに沿っている。
ただし、**APIの使い方が正しいことと、どこを測ると有益かは別の判断**になる。
バッチに別のActiveScopesを付ける規則やCPUの4呼び出し閾値は本ツール独自の推奨で、Engineの統一規約ではない。

監査では、gotoでスコープ途中へ入る元コードにRAII変数を追加するとコンパイルを壊す不具合を再現し、保護を追加した。
通常のLyra/BlockBreakerのコピー生成結果226ファイルは、リファクタリング前とSHA256で一致した。
変更対象のEngine・ゲームの元ソースには適用していない。

## Engineで確認した仕様

参照ルートは `C:/Program Files/Epic Games/UE_5.8/Engine`。以下はこのルートからの相対パスと行番号。

| 確認事項 | Engineの参照箇所 | 現行実装の扱い |
| --- | --- | --- |
| 通常形と固定STR | `Source/Runtime/Core/Public/ProfilingDebugging/CpuProfilerTrace.h:390–454` | 通常形は名前を文字列化してSTRへ進む。ASCIIの固定ラベルは通常形、必要に応じ固定リテラルのSTR。両者を速度の異なる別方式とは扱わない |
| 動的名 | 同上 `:473–490` | 別のTEXT系がある。変化する名前をSTRへ渡さない。現行の生成は固定名のみ |
| カウンター宣言・操作 | `Source/Runtime/Core/Public/ProfilingDebugging/CountersTrace.h:247–249, 288–317, 387–402` | int64のatomic宣言と、同じ識別子への操作を生成。名前は文字列引数ではなく、宣言済みカウンターを参照するトークン |
| 無効時の展開 | 同上 `:429–488`、CpuProfilerTrace.hの無効時の定義 | マクロの引数評価が消える場合がある。副作用を必要とする式は付与しない |
| チャンネル無効時 | `CountersTrace.h:147–158, 216–234` | 値の更新と、チャンネル有効時の出力は別。チャンネルを無効にすればatomic更新の費用も消える、とは説明しない |
| 累積とInflight | `Source/Developer/IoStoreUtilities/Private/IoStoreWriter.cpp:3364, 3412–3413, 3474, 3493, 3946, 3953` | 累積ヒット数と、投入から完了までの現在数を分ける。Countに無条件の減算を付けない |
| 入力数量と結果数量 | `Source/Developer/DerivedDataCache/Private/HttpCacheStore.cpp:2771–2772, 3156, 3166, 3179, 3201` | 入力Requests.Num()と完了後のHit/転送バイト数を区別。自動ADDはInputItems/InputBytesであり、成功量と断定しない |
| 退出ガード | `Source/Runtime/Core/Public/Misc/ScopeExit.h:16–31, 59–73` | 通常退出・return・例外の巻き戻しで減算する。非同期コールバックの完了までガードが延びるわけではない |

公式の[Trace Developer Guide](https://dev.epicgames.com/documentation/en-us/unreal-engine/developer-guide-to-tracing-in-unreal-engine)も照合した。
定義やシグネチャについては、実際にインストールされたヘッダーを優先した。

### Engineと異なるが、誤用ではない方針

- Engineでは目的に応じて関数全体・内部ブロック・非同期完了を個別に計測する。本ツールは字句解析で安全に扱える範囲へ限定する。
- EngineのInflightは要求の投入と完了を対応させる。自動ActiveScopesが測るのは分岐のC++スコープの現在数であり、同じ意味ではない。
- Count・InputItems・ActiveScopesの3指標を同時に生成する場合がある。それぞれ累積入口回数・累積入力数量・現在数という異なる情報を持つ。
- 回数候補は一般のif/elseも含む。名前にhit/successがあるだけで成功イベントと判定しない。不要な候補は除外する。
- atomicを選ぶのは更新時のデータ競合を避けるため。業務データの取得安全性や、複数スレッドの記録順序を保証するものではない。

ON_SCOPE_EXITはCOUNTERSTRACE_ENABLEDが無効でも空のガードとして残り得る。
最適化で除かれる可能性はあるが、Debug等も含めてゼロコストとは保証しない。
関数内へ条件コンパイルを増やすと既存の保守的なパーサーが他候補を扱えなくなるため、今回この生成形式は変更していない。

## 修正した不具合: 初期化を飛び越すgoto

元の有効なコードの例（`Fixture.cpp`）:

```cpp
void Process(const TArray<int>& Items, bool Jump)
{
    if (Jump) goto Work;
    if (!Items.IsEmpty())
    {
    Work:
        for (auto Item : Items) { Use(Item); }
    }
}
```

分岐入口にON_SCOPE_EXITを挿入すると、gotoが初期化を飛び越えてガードの寿命内へ入る。
実EngineのScopeExit.hを使ったMSVC検証で、元コードはコンパイル成功、修正前の生成コードはC2362となることを確認した。
Superの前から後方ラベルへ飛ぶ関数にも、Super直後のCPUスコープを追加すると同じ問題が起こり得る。

対応は次のとおり。

- gotoを含む関数のイベント候補へ`ScopeTrackingExclusion`を記録する。別ActiveScopesの自動追加を止め、手動のActiveScope/IncludeActiveScope指定も検証で拒否する。
- WPFでは該当するスコープ設定を無効にし、理由を表示する。プログラムや古い選択JSONから指定されてもCore側で止める。
- Count/ADDは局所変数の寿命を追加しないため維持する。ただし入口への到達を測るもので、途中のラベルへ直接飛ぶ経路は数えない。
- 同名Super後へCPUスコープを置く場合は、gotoがあれば手動確認として追加しない。関数入口から計測する場合の関数内gotoは維持できる。
- コメント・文字列中のgotoは判定に使わない。飛び先の型・制御フローを完全に解決せず、関数単位で保守的に判定する。

gotoがないことだけで任意のC++構文の安全性を証明するわけではない。
longjmpや異常終了等を含む任意の制御フロー・寿命の意味解析は引き続き対象外で、UEプロジェクト全体のコンパイル確認は必要。

## Coreの責務分離

| 部品 | 責務 |
| --- | --- |
| [EventCandidateDiscovery](../../src/UEEtc.Core/Features/EventCounters/EventCandidateDiscovery.cs) | 元ソースの字句・構造解析、分岐候補、推奨入力、寿命上の除外理由 |
| [EventCounterModels](../../src/UEEtc.Core/Features/EventCounters/EventCounterModels.cs) | 公開の候補・選択・スナップショット。公開型の名前空間は維持 |
| [EventSelectionValidator](../../src/UEEtc.Core/Features/EventCounters/EventSelectionValidator.cs) | ID・名前・数量・確認状態・スコープ安全性の検証 |
| [EventCounterPlanBuilder](../../src/UEEtc.Core/Features/EventCounters/EventCounterPlanBuilder.cs) | 名前の割り当て、宣言・操作・退出ガードの挿入案 |
| [EventCounterService](../../src/UEEtc.Core/Features/EventCounters/EventCounterService.cs) | 探索・候補・検証・生成の組み立てと既存の公開API |
| [FileProposal](../../src/UEEtc.Core/Source/FileProposal.cs) | 元ソースと挿入案の保持・同一元ソースの統合・最終的な文字列/バイト列化 |
| [CombinedTracingService](../../src/UEEtc.Core/Features/CombinedTracing/CombinedTracingService.cs) | 個別機能へ同じ元バイト列を渡し、挿入案を統合 |

イベント解析で作ったSourceDocumentとCppStructureは、スナップショット内で保持する。
プレビュー更新のたびに同じ元コードをデコードし直したり、同じイベント構造を作り直したりしない。
TryBuildPreviewでの選択検証は1回にし、生成時にもう一度同じ検証を繰り返す構造を除いた。
選択候補はパスで先にグループ化し、ファイルごとに選択全件を再走査しない。
通常のActiveScopeと別ActiveScopesで重複していた退出ガード文字列の生成も1箇所へまとめた。

統合処理では、イベントとCPUそれぞれの最終文字列を作ってから再度まとめる処理をやめた。
それぞれがFileProposalを返し、統合後にRenderで文字列とエンコード済みバイト列を1回作る。
FileProposal.Mergeではパスと元バイト列が同一かも確認する。
挿入案は公開のFileAnalysisに付けず、外部へ出す解析結果と内部の生成途中状態を分けた。
これにより、旧関数進入カウンターの命名解決後に内部挿入案だけ古い名前で残る構造もなくなる。

CPUとイベントの構文判定そのものは別処理として残る。完全なC++ ASTへの置換や、全機能を1つのパーサーへ統合する変更ではない。
キャッシュは各解析スナップショットに閉じ、適用時の元ファイル一致検証を省略することもない。

## WPFの責務分離

| 部品 | 責務 |
| --- | --- |
| [PlanWindowWorkflow](../../src/UEEtc.Wpf/Workflow/PlanWindowWorkflow.cs) | CPU/統合画面共通の解析・取消・選択・検索・プレビュー・設定保存・閉じる処理 |
| [PlanWindowView](../../src/UEEtc.Wpf/Workflow/PlanWindowView.cs) | 共通操作が使う名前付きコントロールの境界。画面固有のレイアウト・列は各XAMLへ残す |
| [PlanAvailability](../../src/UEEtc.Wpf/Workflow/PlanAvailability.cs) | 3画面共通の適用/保存の有効条件と、適用できない理由 |
| [PlanFileActions](../../src/UEEtc.Wpf/Workflow/PlanFileActions.cs) | 3画面共通の保存先ダイアログ、適用確認、非同期の保存/適用、結果表示 |
| [CandidateRow](../../src/UEEtc.Wpf/Features/EventCounters/CandidateRow.cs) | イベントの候補編集状態と変更通知。初期案はCoreのEventCounterDefaultsを1回呼んで作る |

CPU/統合ウィンドウのコード側は、共通ワークフローと該当Coreサービスを接続する薄い構成にした。
イベント画面には編集・自動プレビュー更新・選択検証結果の表示を残し、通常の1回解析画面へ無理に統合していない。
ファイルのバックアップ・変更検出・復元は引き続きCoreのSourceWorkspaceが担う。
適用前の確認、全解除後の無効化、未確認数量・診断による適用停止は維持する。

## 検証

- Releaseビルド: 警告0・エラー0。
- Core 129/129、WPF 140項目。既存の個別/統合画面、選択・フィルター・設定保存・長文スクロール・数量編集・バックアップ付き部分適用に加え、gotoの拒否と理由表示を確認。
- `Test-CombinedTracing.ps1 -EngineMacroDefinitions`: インストール済みのCpuProfilerTrace.h/CountersTrace.hから対象マクロの有効/無効定義を読み、実際のマクロ本体を使う。
  出力バックエンドと必要な基本型はフィクスチャ、ScopeExit.hは実ヘッダー。auto/STR × CPU有効/無効 × Counters有効/無効の8構成をMSVC C++20 `/W4 /WX`でコンパイル・実行した。
  これはEngine全体のリンクやトレースストリームの動作試験ではなく、マクロ展開と生成コードの契約の検証。
- `Test-GotoInstrumentation.ps1`: 分岐内へのgotoとSuper後方へのgotoを含む元コード/修正後コードを、有効/無効の4構成でコンパイル。
- `Invoke-CombinedTraceExperiments.ps1`: Lyra 233cpp/188変更、CPU451・イベント1525・ADD4・DECREMENT4。BlockBreaker 47cpp/38変更、CPU93・イベント423・ADD0・DECREMENT0。
  診断0、コピーへの差分適用チェック成功、再解析の追加0、元ソース599ファイルのSHA256不変。
- リファクタリング前後の生成コピー226ファイルがバイト単位で一致。今回のゲームソースにはgotoがなく、保護条件による生成差分はなかった。

新規のマクロ・機能の追加、Engine/ゲームの元コードへの適用、UEゲーム全体のビルド、実Insightsセッションでの負荷測定はこの作業に含めていない。
