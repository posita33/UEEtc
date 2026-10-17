---
date: 2026-10-16
tags: [csharp, unreal-engine, tracing]
status: todo
---

# UEEtc
UEいろいろコード

Unreal Engine 関連の C# Console / WPF 開発を試すための実験リポジトリです。

現在は C++ ソースへの Trace マクロ付与ツールを実装しています。WPF のメイン画面から「CPU・イベント統合付与」、個別の「CPU スコープ付与」「イベントカウンター付与」を開けます。統合画面では1回の解析で両方をプレビュー・適用できます。[操作と統合ロジック](wiki/tracing/combined-tracing.md)を参照してください。解析・レポート保存では元ソースを変更しません。

CPU のラベルは「クラス名 関数名」です。対応する `Super::同じ関数名(...)` がある場合はその後から計測し、安全な配置を判断できない構文は理由を表示します。[CPU の名前・計測範囲](wiki/tracing/cpu-scope-policy.md)を参照してください。

イベントカウンターは条件分岐の候補を提示し、数える意味・箇所・名前を確認して選択したファイルにだけ追加します。WPFでは解析時に変更候補のあるファイルを適用対象として選択します。[現行の使い方と構成](wiki/tracing/automatic-counter-preview.md)を参照してください。旧 Console の `--both` / `--counter-only` は既存スクリプト用の関数進入回数モードとして残しています。

CPUマクロは自動・通常形・STRを選べます。UE 5.8の通常形とSTRは同じ静的ID経路で、STRは動的文字列用ではありません。[Engine調査と計測モードの使い分け](wiki/tracing/cpu-macros-and-counter-lifetimes.md)を参照してください。

[調査・設計・実験結果の目次](wiki/README.md)

[現行2機能の付与ロジックとEngine参照](wiki/tracing/current-feature-logic.md)、[追加Trace機能の候補と優先度](wiki/tracing/trace-feature-candidates.md)に、実装根拠と今後の検討内容をまとめています。
[Engineとの整合性監査と責務分離](wiki/tracing/engine-audit-and-refactoring.md)には、マクロの照合結果、gotoの安全対策、Core/WPFの構成を記録しています。

## 開発環境

- Windows（WPF のビルド・実行に必要）
- .NET 10 SDK
- .NET 10 に対応した Visual Studio と「.NET デスクトップ開発」ワークロード

`global.json` で .NET 10 の安定版 SDK を使用します。10.0.100 以降の
インストール済み 10.0 系 SDK から、最新の feature band / patch を選択します。

## 構成

```text
UEEtc.sln
global.json
src/
  UEEtc.Core/       # 共通の探索・解析・付与・出力ロジック（net10.0）
    Features/      # FunctionTracing / EventCounters / CombinedTracing を名前空間ごとに分離
    Source/        # 共通のソース処理・バックアップ付き適用
    Plans/         # 共通の変更案モデル
    Reporting/     # レポート・差分出力
  UEEtc.Console/    # Console アプリ（net10.0）
  UEEtc.Wpf/        # WPF アプリ（net10.0-windows）
tests/             # Core 回帰テスト、WPF スモークテスト
scripts/           # C++ コンパイル確認と実プロジェクトの読み取り実験
wiki/              # 調査結果・実装ドキュメント
```

Unreal Engine 本体や外部 NuGet パッケージに依存せず、起動できます。

## Visual Studio で実行

1. `UEEtc.sln` を開きます。
2. ソリューション エクスプローラーで `UEEtc.Console` または `UEEtc.Wpf` を右クリックし、
   「スタートアップ プロジェクトに設定」を選択します。
3. F5（デバッグ実行）または Ctrl+F5（デバッグなしで実行）で起動します。

## コマンドラインで実行

リポジトリのルートで実行します。

```powershell
dotnet build UEEtc.sln
dotnet run --project src/UEEtc.Console
dotnet run --project src/UEEtc.Wpf
```

Console は引数なしでヘルプを表示します。解析するフォルダーを指定する例:

```powershell
# CPU の解析のみ
dotnet run --project src/UEEtc.Console -- cpu "D:/Projects/posita/git/Game/BlockBreaker/Source"

# CPU・イベントの統合プレビュー（元ソースは変更しない）
dotnet run --project src/UEEtc.Console -- combined "D:/Projects/posita/git/Game/BlockBreaker/Source" --report "D:/TraceReports/combined"

# イベント候補の解析と保存（出力先はソース外の空のフォルダー）
dotnet run --project src/UEEtc.Console -- counters "D:/Projects/posita/git/Game/BlockBreaker/Source" --report "D:/TraceReports/discovery"
```

WPF はメイン画面から機能を選び、ソースフォルダーを指定して「解析・プレビュー」を実行します。両画面とも変更候補のあるファイルが適用対象として選択され、問題のない変更案では「元ファイルへ適用」を押せます。不要なファイルはチェックを外してください。全体・表示中・強調行の選択／解除、Ctrl/Shiftの複数行選択、Space切替に対応します。変更行を緑色と「+」で示し、前後の変更箇所へ移動できます。

イベントは「解析・プレビュー」で入力数量のADDを自動設定し、入力配列を直接処理するバッチ分岐には別のスコープ現在数（入口INCREMENT・退出DECREMENT）も追加します。累積回数は保持します。付与後プレビューで追加内容・理由・差分を確認できます。「候補・詳細設定」では候補の除外と、折りたたみ内の名前・値の変更ができます。[自動設定の条件・操作・実験結果](wiki/tracing/automatic-counter-preview.md)を参照してください。

CPU はループ・対象API・複数の処理呼び出しを根拠に候補を絞ります。「全関数（互換）」または `cpu <path> --all-functions` で従来の対象に戻せます。[CPU の Engine 調査・使い方・実験結果](wiki/tracing/focused-cpu-and-quantity-counters.md)を参照してください。

ソースパス欄はフォルダーを 1 つドロップして指定できます。入力したパス・絞り込み・編集したカウンター名は次回起動時にも復元します。候補の選択や適用待ちの変更案は復元しません。[保存内容と保存先](wiki/input-assistance.md)も参照してください。

両方ともレポート・差分・加工済みコピーの保存に対応します。実際に元ファイルを変更する操作は、WPF の「元ファイルへ適用」または Console の `--apply` です。バックアップ先はWPFの各画面、Consoleの `--backup-dir <フルパス>` で指定できます。初期値はソース外の `%LOCALAPPDATA%\UEEtc\Backups` です。[バックアップ先の設定](wiki/tracing/backup-location.md)と[詳細な使い方と制限](wiki/tracing/implementation.md)を参照してください。

今回の Lyra / BlockBreaker 実験では元ソースを変更していません。字句解析によるツールのため、適用後の UE ビルドとトレース結果は別途検証が必要です。

Release ビルド:

```powershell
dotnet build UEEtc.sln --configuration Release
```

## 検証

```powershell
# テストランナーへの依存がない実行型テスト
dotnet run --project tests/UEEtc.Core.Tests --configuration Release
dotnet run --project tests/UEEtc.Wpf.SmokeTests --configuration Release

# MSVC x64 の C++ ツールが必要
./scripts/Test-CppInstrumentation.ps1
./scripts/Test-EventCounters.ps1
./scripts/Test-EventCounters.ps1 -Quantity
./scripts/Test-EventCounters.ps1 -Batch
./scripts/Test-CpuScopePolicy.ps1
./scripts/Test-CpuScopePolicy.ps1 -CpuMacro str
./scripts/Test-ScopedEventCounters.ps1
./scripts/Test-CombinedTracing.ps1
./scripts/Test-CombinedTracing.ps1 -EngineMacroDefinitions
./scripts/Test-GotoInstrumentation.ps1

# Lyra / BlockBreaker を読み取り、加工済みコピーだけを artifacts/ に出力
./scripts/Invoke-TraceExperiments.ps1
./scripts/Invoke-TraceExperiments.ps1 -Focused
./scripts/Invoke-EventCounterExperiments.ps1
./scripts/Invoke-EventCounterExperiments.ps1 -RecommendedAdd
./scripts/Invoke-ReviewedEventCounterExamples.ps1
```
