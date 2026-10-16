---
date: 2026-10-16
tags: [wiki, research/tech, testing, unreal-engine, tracing]
status: todo
---

# Lyra / BlockBreaker のマクロ付与実験

[目次](../README.md) / [調査](engine-research.md) / [実装](implementation.md)

## 対象と実施内容

- Lyra: `D:/Projects/posita/UE/R_A_D/LyraStarterGame/Source`
- BlockBreaker: `D:/Projects/posita/git/Game/BlockBreaker/Source`
- 実施日: 2026-10-16（JST）
- 実行環境: Windows / .NET SDK 10.0.401 / .NET Runtime 10.0.12
- 最終検証出力: `artifacts/trace-experiments/run-20261016-092142-d15787f7/`

CPU と関数進入回数の両方を選択し、共通コアを使う Console から再帰解析しました。変更案、JSON、加工済みコピーをこのリポジトリの `artifacts/` に出力しました。**元のゲームソースへの適用は行っていません。** Engine にも書き込んでいません。

## 結果

| 指標 | Lyra | BlockBreaker |
| --- | ---: | ---: |
| Source 配下の全ファイル（ハッシュ確認対象） | 486 | 113 |
| 解析した `.cpp` | 233 | 47 |
| 変更案を生成した `.cpp` | 214 | 42 |
| マクロを付与する候補関数 | 1,532 | 235 |
| 識別した候補のうちスキップした関数 | 200 | 28 |
| ファイル・探索の診断 | 0 | 0 |
| 加工済みコピーを再解析した追加変更 | 0 | 0 |
| 元 Source の SHA-256 差分 | 0 | 0 |

スキップの内訳:

| 理由 | Lyra | BlockBreaker |
| --- | ---: | ---: |
| コンストラクター／デストラクター等 | 166 | 26 |
| 空の関数 | 29 | 2 |
| 既存の計測あり | 5 | 0 |

スキップ件数には、解析器が候補として認識していないクラス内定義やラムダ等は含みません。上の件数は実行時性能を測定した結果ではなく、ソースへの付与案の件数です。

## 確認した実例

### BlockBreaker

`BlockBreaker/Private/Gameplay/Actors/Balls/Ball.cpp` では 11 関数が候補になりました。`ABall::ABall` は除外し、`ABall::BeginPlay`、`ABall::OnHit`、`ABall::Launch`、`ABall::RecallToPaddle` などに両マクロと対応する宣言を生成しました。

`ABall::OnHit` の先頭に入れるカウンターは、OtherActor が不正な場合や、すでにアタッチされていて早期 return する場合も数えます。反射回数そのものを測るものではありません。`ABrick::InitializeFromData` 内のアセットロード用ラムダには自動でマクロを追加しません。

ほかの候補数は `Paddle.cpp` が 24、`Brick.cpp` が 11、`BlockBreakerGameMode.cpp` が 18 でした。処理の意味に沿ったカウンターは、この関数進入数を観察した後に絞り込む想定です。

### Lyra

`LyraGame/Character/LyraCharacter.cpp` では 46 関数が候補になりました。`FObjectInitializer` を使うコンストラクターは除外し、通常のメンバー関数に追加します。

`LyraGame/AbilitySystem/Executions/LyraDamageExecution.cpp` では 2 関数が候補です。`Execute_Implementation` 内の `#if WITH_SERVER_CODE` は維持し、関数スコープの計測をその外側に追加します。そのため、クライアント等で内部処理がコンパイルされない場合でも、関数を呼び出せば進入数を数えるという定義です。

`LyraGame/GameFeatures/LyraGameFeaturePolicy.cpp` の既存 CPU スコープ、`UI/IndicatorSystem/SActorCanvas.cpp` の既存 cycle counter など、計 5 関数はスキップしました。既存マクロを STR 版に置き換える処理は行いません。

## 検証結果と範囲

| 検証 | 結果 |
| --- | --- |
| ソリューション Release ビルド | 警告 0 / エラー 0 |
| Core 回帰テスト | 26 / 26 成功 |
| WPF の STA / Dispatcher スモークテスト | 8 チェック成功。非同期解析・選択・プレビュー・操作有効化・入力変更時の無効化・元ファイル無変更を確認 |
| 独自 C++ fixture の MSVC C++20 ビルド・実行 | Trace 無効／有効とも成功。`/W4 /WX` |
| 両対象の `changes.patch` | 元 Source をカレントフォルダーにした `git apply --check` 成功。適用はしていない |
| 加工済み `.cpp` の再解析 | 追加変更 0、診断 0 |
| 元フォルダーの完全性 | 合計 599 ファイルのパス・SHA-256 が一致 |

Core テストは、通常関数、オーバーロード、namespace、条件コンパイルの代替定義、文字列・raw 文字列・コメント、ラムダ非対象、数値区切り、既存計測、空関数、constexpr、文字コード・改行、探索除外、出力、バックアップ、解析後変更、読み取り専用、キャンセル等を検証します。適用のテストは専用の一時フォルダーのみで行いました。

UE の実プロジェクトを加工後の状態でビルドする検証と、Unreal Insights での計測結果の確認は未実施です。元ソースに手を加えない今回の範囲では、実プロジェクトへのコンパイル適合を確定していません。

## 生成物と再現

実プロジェクトのソース本文を含むコピーと patch は Git 管理外の `artifacts/` に置き、wiki には調査結果と集計値を残しています。

- `lyra/report.json`、`lyra/changes.patch`、`lyra/modified/`
- `blockbreaker/report.json`、`blockbreaker/changes.patch`、`blockbreaker/modified/`
- `lyra-before.json`、`blockbreaker-before.json`: 元ファイルの SHA-256
- `lyra-summary.json`、`blockbreaker-summary.json`: 集計と元ファイル差分
- `<対象>-second-pass/report.json`: 加工済みコピーの再解析

再実行はリポジトリルートで `./scripts/Invoke-TraceExperiments.ps1` を実行します。新しい出力フォルダーを作り、以前の実験結果を上書きしません。
