---
date: 2026-10-16
tags: [wiki, architecture, csharp, wpf, unreal-engine, tracing]
status: todo
---

# Trace マクロ付与ツールの設計と使い方

[目次](../README.md) / [調査](engine-research.md) / [実験](experiments-2026-10-16.md)

## 構成

UI と Console は同じ .NET 10 ライブラリを参照します。WPF の入力・画面表示以外に解析ロジックを持たせていません。外部 NuGet パッケージへの依存はありません。

```text
src/
  UEEtc.Core/
    AnalysisPlan.cs              解析オプション、関数・ファイルの判定、変更案
    SourceDocument.cs            文字コードと BOM を維持する読み書き
    CppLexer.cs                  C++ の字句と括弧・条件コンパイルの検査
    CppInstrumenter.cs           関数の候補抽出とマクロ挿入案
    InstrumentationService.cs    再帰探索と明示的な適用
    ReportExporter.cs            JSON、差分、加工済みコピーの出力
  UEEtc.Wpf/                    非同期解析、プレビュー、保存、適用
  UEEtc.Console/                同じコアを使う CLI
tests/
  UEEtc.Core.Tests/             コアの回帰テスト（実行型）
  UEEtc.Wpf.SmokeTests/         STA / Dispatcher を用いた WPF 検証
scripts/
  Test-CppInstrumentation.ps1   MSVC で生成 C++ のコンパイル・実行確認
  Invoke-TraceExperiments.ps1   実プロジェクトの解析・再解析・元ファイル同一性検証
```

`InstrumentationService.Analyze()` は元ファイルを読み取るだけです。返される `AnalysisPlan` は、元のバイト列、加工後のバイト列、関数の判定理由、診断を保持します。書き込みを行う操作は `ReportExporter.Export()` と `InstrumentationService.Apply()` に分けました。

## 挿入するコード

生成形式を示すための独自の短いサンプルです。実際の名前にはパス・シグネチャから求めた SHA-256 の先頭 16 桁が入り、オーバーロードを区別します。行番号をキーにしないため、挿入により行がずれても名前が変わりません。相対パスを求める起点は解析対象フォルダーです。

例: `Source/Example/Example.cpp`

```cpp
#include "Example.h"
#include "ProfilingDebugging/CpuProfilerTrace.h"
#include "ProfilingDebugging/CountersTrace.h"

namespace {
    TRACE_DECLARE_ATOMIC_INT_COUNTER(UEEtcCalls_0123456789ABCDEF,
        TEXT("UEEtc/Example/Example.cpp/AExample::Work [0123456789ABCDEF]/Calls"));
}

void AExample::Work()
{
    TRACE_CPUPROFILER_EVENT_SCOPE_STR(
        "UEEtc/Example/Example.cpp/AExample::Work [0123456789ABCDEF]");
    TRACE_COUNTER_INCREMENT(UEEtcCalls_0123456789ABCDEF);
    DoWork();
}
```

STR には固定のリテラルを渡します。カウンターは関数のローカル自動変数として毎回作る構成にはせず、翻訳単位で保持します。条件コンパイルの代替枝に同一シグネチャの関数がある場合、宣言は共有します。元のソースは文字列の挿入以外で整形しません。

## WPF

起動は Visual Studio のスタートアッププロジェクトを `UEEtc.Wpf` にするか、リポジトリルートで次を実行します。

```powershell
# リポジトリルートで実行
dotnet run --project src/UEEtc.Wpf
```

1. Source フォルダーのフルパスを入力するか「フォルダーを選択」で指定します。
2. CPU スコープと関数進入回数のうち必要なものを選び、「解析・プレビュー」を押します。
3. 左のファイル一覧から選択し、「付与後プレビュー」「付与前」「関数・除外理由」「診断」を確認します。ファイル名にマウスを合わせると相対パスを表示します。
4. 「レポートを保存」で対象ソースの親・子ではない空のフォルダーに出力できます。元ソースは変わりません。
5. 実際に変更したい段階で「元ファイルへ適用」を選びます。ファイル数と対象パスを表示する確認画面の後にバックアップを作成し、元ファイルを更新します。

解析中は入力をロックし、中止ボタンでキャンセルできます。入力パスやオプションを変更すると古い解析結果は無効になります。診断が残る場合、適用ボタンは無効です。適用後・適用失敗後は再解析が必要です。

今回の調査では手順 5 を実プロジェクトに対して実行していません。

## Console

引数なし、または `--help` で使い方を表示します。ソースのフルパスだけなら読み取りと解析結果の出力だけを行います。

```powershell
# リポジトリルートで実行: 読み取りだけ
dotnet run --project src/UEEtc.Console -- "D:/Projects/posita/git/Game/BlockBreaker/Source"

# 対象外の空フォルダーにレポートと加工済みコピーを保存
dotnet run --project src/UEEtc.Console -- "D:/Projects/posita/git/Game/BlockBreaker/Source" --report "D:/Temp/UEEtc-BlockBreaker-Preview"

# 片方だけの付与を試す。--counter-only も使用可能
dotnet run --project src/UEEtc.Console -- "D:/Projects/posita/git/Game/BlockBreaker/Source" --cpu-only
```

`--apply` を追加すると実際の元ファイルに適用します。明示的に書き換えるためのオプションであり、今回の実プロジェクト実験では付けていません。`--cpu-only` と `--counter-only` は排他的で、どちらも指定しなければ両方が対象です。

| 終了コード | 意味 |
| --- | --- |
| 0 | 処理完了、ファイル診断なし |
| 1 | 解析完了、一部のファイル・ディレクトリに診断あり |
| 2 | 引数・パス・入出力・適用などのエラー |
| 130 | 解析を Ctrl+C で中止 |

診断のある計画もレビュー用に出力できますが、適用できません。スキップは通常の判定であり、診断とは区別します。

## 出力

| 出力 | 内容 |
| --- | --- |
| `report.json` | 対象パス、オプション、件数、各関数の名前・元行番号・判定理由・カウンター名 |
| `changes.patch` | ファイル全体をコンテキストとする unified diff。レビュー用で省略なし |
| `modified/` | 変更案のある `.cpp` だけを相対パスを保って保存したコピー |

`modified/` は完全なゲームプロジェクトではありません。ヘッダー、変更しない `.cpp`、Build.cs などを複製していないため、そのフォルダー単体で UE プロジェクトをビルドする用途には使えません。

UTF-8（BOM 有無）、BOM 付き UTF-16 LE/BE を読み込み、元の文字コードと BOM を保ってコピー・適用します。挿入する改行は、そのファイルに CRLF があれば CRLF、それ以外は LF です。既存部分の混在改行は変更しません。不正な UTF-8、BOM なしの別エンコーディング、UTF-32、NUL は診断して除外します。Shift-JIS などを推測で変換しません。

patch 自体は UTF-8 です。UTF-8 の実プロジェクトでは `git apply --check` が通ることを確認しました。UTF-16 ソースの patch はデコード済みテキストのレビュー用とし、書き戻すときは元のバイト列を扱う適用機能か `modified/` のコピーを使います。

## 元ファイルへの適用と復元

適用は全対象のパス・読み取り専用属性・元バイト列を先に確認し、解析後に編集されたファイルがあれば開始しません。バックアップを全件作ってから、各ファイルを再検査し、一時ファイルから置き換えます。

バックアップ先は `<解析対象>/.ueetc-backups/<UTC日時>-<識別子>/<相対パス>` です。上書きに失敗した場合、すでに書いたファイルは生成直後のバイト列のままなら元に戻します。復元にも失敗した場合は例外にバックアップ先を含めます。複数ファイル全体が OS レベルで一つのトランザクションになるわけではありません。

復元の専用 UI はありません。生成された差分とバージョン管理で確認するか、該当バックアップのファイルを元の相対パスへコピーして戻します。バックアップの自動削除は行いません。

## 検証

テストは追加のテストランナー NuGet パッケージを使わない実行型プロジェクトです。`dotnet test` ではなく、以下の `dotnet run` で実行します。

```powershell
# リポジトリルートで実行
dotnet build UEEtc.sln --configuration Release
dotnet run --project tests/UEEtc.Core.Tests --configuration Release
dotnet run --project tests/UEEtc.Wpf.SmokeTests --configuration Release
./scripts/Test-CppInstrumentation.ps1
./scripts/Invoke-TraceExperiments.ps1
```

Core / WPF テストの適用検証はテスト専用の一時フォルダーだけで行います。C++ テストには Visual Studio の MSVC x64 ツールが必要です。UE に似た形の有効／無効マクロを持つ独自スタブで、宣言配置、オーバーロード、条件分岐、カウンター更新、RAII 退出を検証します。UE の実ヘッダーを使ったゲームのビルド・トレース取得は別の検証です。

`Invoke-TraceExperiments.ps1` は Lyra / BlockBreaker のパスを引数で変更できます。解析前後の SHA-256 マニフェスト、patch チェック、加工済みコピーの再解析結果を `artifacts/trace-experiments/run-<日時>-<識別子>/` に保存します。`--apply` を呼びません。

## 制限と次の拡張

- 完全な C++ パーサーではないため、全関数への網羅性や任意の入力に対する UE コンパイル成功を保証しません。詳細な除外条件は [調査](engine-research.md) を参照してください。
- 関数ごとの選択・除外リスト、重い関数だけへの絞り込み、既存計測の片方補完は未実装です。
- 広範囲に入れると計測自体の負荷、カウンターやイベント定義の数が増えます。大きな範囲では CPU のみ、または対象フォルダーを絞って確認できます。
- ヘッダー内 inline 定義、テンプレート、コンストラクターを扱う拡張には、UE のコンパイルデータを使う AST 解析の導入を検討します。
