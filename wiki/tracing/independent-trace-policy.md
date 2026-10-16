---
date: 2026-10-16
tags: [wiki, implementation, csharp, wpf, unreal-engine, tracing]
status: todo
---

# CPU とカウンターの独立した付与条件

[目次](../README.md) / [調査](macro-pairing.md) / [Issue #7](https://github.com/posita33/UEEtc/issues/7)

## 調査から決めた動作

Engine では CPU スコープとカウンターは独立した計測です。本ツールでも必須セットとして扱うことをやめ、標準は時間計測だけにしました。これは時間計測を入口にするための本ツールの設計判断であり、Epic がすべての関数に CPU マクロを付けることを定めているという意味ではありません。

| 利用方法 | CPU を追加 | 進入回数を追加 |
| --- | --- | --- |
| 標準、または `--cpu-only` | 選択 | 非選択 |
| `--counter-only` | 非選択 | 選択 |
| `--both` | 選択 | 選択 |

WPF は「CPU スコープ（時間）」だけが初期状態でオンです。「関数進入回数（任意）」を選ぶと両方、CPU を外せばカウンターのみになります。両方オフでは解析を開始せず、少なくとも片方の選択が必要であることを通知します。

カウンターを追加する場合に数えるのは、早期 return や失敗を含む関数への進入です。命中・成功・復旧・破壊などのゲーム上の意味は、自動で判断して計測箇所を決めません。それらが必要な場合は該当する分岐への個別設計が必要です。

## 既存計測の判定

既存 CPU の判定と既存カウンターの判定を分離しました。以下の表は、両方を選択し、通常の非空関数などの基本条件を満たしている場合です。

| 元ソース内の既存計測 | CPU の処理 | カウンターの処理 |
| --- | --- | --- |
| どちらもない | 追加 | 関数進入回数を追加 |
| CPU のみある | 維持 | 関数進入回数を追加 |
| カウンターのみある | 追加 | 既存の操作を維持 |
| 両方ある | 維持 | 維持 |

片方しか選んでいない場合は、その種類だけを判定します。もう片方を補完してセットにする処理は行いません。また、未選択は既存計測の削除を意味しません。たとえば CPU のみを選んでも、元ソースにカウンターがあればそのまま残ります。

既存カウンターの意味は分からないため、既存の `TRACE_COUNTER_INCREMENT(CacheHits)` 等があっても、それを進入回数として読み替えたり、`CallCount` に改名したりしません。その関数では新しい進入回数カウンターを自動追加せず、レポートに「既存のカウンター操作を維持（意味は自動推定しない）」と記録します。別の CPU 計測は追加可能です。

### 識別する既存マクロと制限

- CPU: `TRACE_CPUPROFILER...`、名前に `SCOPE_CYCLE_COUNTER` を含むマクロ、`SCOPED_NAMED_EVENT...` の呼び出し。
- Counter: `TRACE_COUNTER_...` の呼び出し。increment だけでなく SET / GET / ADD / DECREMENT 等も、既存の計測設計を維持するため保守的に扱います。
- コメント・文字列中のマクロ名、呼び出しではない同名の単語は既存計測として扱いません。

探索は関数本体のネストしたブロック・ラムダ内も含みます。条件コンパイルも展開しないため、別の条件分岐や内部区間にある同種計測も保守的に維持します。内部区間と関数全体の計測を区別して自動で重ねる処理や、任意の独自ラッパーの展開は行いません。

元の構文除外は維持しています。コンストラクター・デストラクター、constexpr、テンプレート、operator、requires、コルーチンなど、識別できない・安全な挿入を確定できないものは手動確認の対象です。

## 実装

- `InstrumentationOptions` の既定値を `CpuScopes = true` / `CallCounters = false` に変更。
- `CppInstrumenter` で `addCpu` / `addCounter` を独立して決定し、実際に追加する種類のヘッダー・宣言だけを生成。
- `CounterNaming` による読みやすい名前と衝突回避を維持。通常は `ABlockBreakerGameMode_BeginPlay_CallCount` の形で、衝突時だけハッシュを付ける。
- WPF に独立した目的、任意の進入回数であることを表示。関数一覧に CPU と Counter の判断列を追加。
- Console に排他的な `--both` を追加。引数がない既定モード、成功数等を推定しない点をヘルプに記載。

`FunctionAnalysis` は `CpuScopeAdded`、`CallCounterAdded`、`CpuDecision`、`CounterDecision` を持ちます。`Counter` は今回追加するカウンターの名前で、既存カウンターを列挙するものではありません。`FileAnalysis` / `AnalysisPlan` に `AddedCpuScopes`、`AddedCallCounters` を追加し、JSON と画面・CLI の集計にも反映しました。

これらの件数は今回挿入するマクロの箇所数です。条件コンパイルの同一シグネチャが宣言を共有する場合、カウンター実体の数より挿入箇所数が多くなります。`Add` は少なくとも片方を追加、`Skip` は今回どちらも追加しない関数です。基本条件により除外した場合も、種類ごとに除外理由または未選択を記録します。

## 使い方

```powershell
# リポジトリルートで実行。すべて解析のみ
dotnet run --project src/UEEtc.Console -- "D:/Projects/posita/git/Game/BlockBreaker/Source"
dotnet run --project src/UEEtc.Console -- "D:/Projects/posita/git/Game/BlockBreaker/Source" --counter-only
dotnet run --project src/UEEtc.Console -- "D:/Projects/posita/git/Game/BlockBreaker/Source" --both

# 3 モードの実プロジェクト実験（元ソースへ適用しない）
./scripts/Invoke-TraceExperiments.ps1 -Mode cpu
./scripts/Invoke-TraceExperiments.ps1 -Mode counter
./scripts/Invoke-TraceExperiments.ps1 -Mode both
```

`--cpu-only` / `--counter-only` / `--both` を複数指定すると、あいまいな設定として終了コード 2 を返します。レポート出力は従来の `--report`、元ソースへの適用は明示的な `--apply` です。今回の実験では `--apply` を使っていません。

## 検証結果

| 検証 | 結果 |
| --- | --- |
| Core | 42 テスト成功。片方既存、両方既存、CPU→両方、Counter→両方、二重付与防止、ヘッダーの分離、既存カウンターの共有維持など |
| WPF | 16 チェック成功。初期 CPU のみ、任意で両方、Counter のみ、入力・選択変更時の無効化、元ファイル維持 |
| C++ 独自 fixture | 3 付与モード × CPU/Counter の独立した有効・無効 4 組み合わせ。MSVC C++20 `/W4 /WX` でコンパイル・実行 |
| ソリューション Release | 警告 0 / エラー 0 |
| 実プロジェクト | 各モードとも `git apply --check` 成功、コピー再解析の追加変更 0、元の合計 599 ファイルの SHA-256 一致 |

新しく追加するマクロの箇所数:

| 対象 | モード | CPU | 関数進入回数 |
| --- | --- | ---: | ---: |
| Lyra | CPU のみ | 1,532 | 0 |
| Lyra | Counter のみ | 0 | 1,537 |
| Lyra | 両方 | 1,532 | 1,537 |
| BlockBreaker | CPU のみ | 235 | 0 |
| BlockBreaker | Counter のみ | 0 | 235 |
| BlockBreaker | 両方 | 235 | 235 |

Lyra の既存 CPU 計測を持つ 5 関数では、新たな CPU スコープを重ねず、Counter が選択された場合だけ進入回数を追加しました。すべてのモードで診断は 0 でした。

実験生成物:

- CPU のみ: `artifacts/trace-experiments/run-20261016-101102-77d78d6e/`
- Counter のみ: `artifacts/trace-experiments/run-20261016-101210-46cbb883/`
- 両方: `artifacts/trace-experiments/run-20261016-101224-e4730369/`
- C++ fixture: `artifacts/cpp-smoke/4b93d7a4117b4b199cfaad2263b3fdb6/`
- WPF 表示確認: `artifacts/trace-experiments/wpf-independent-policy.png`

実プロジェクトのコピーは相対パスを保つ変更 `.cpp` だけです。完全なゲームプロジェクトではなく、UE 実ビルドと Unreal Insights の取得・表示検証は未実施です。Engine / Lyra / BlockBreaker の元ソースへ書き込んでいません。

## 前提変更との関係

実装ブランチには未マージの PR #4（読みやすい命名と衝突回避）と PR #6（併用条件の調査記録）を前提変更として取り込みました。既存の命名要望を維持したうえで付与方針を変更しています。元の調査記録の数値は調査当時の設定によるものとして残し、現行の操作手順と付与条件を更新しました。
