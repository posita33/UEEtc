---
date: 2026-10-16
tags: [csharp, unreal-engine, tracing]
status: todo
---

# UEEtc
UEいろいろコード

Unreal Engine 関連の C# Console / WPF 開発を試すための実験リポジトリです。

現在は C++ ソースへの Trace マクロ付与ツールを実装しています。フォルダーのフルパスから `.cpp` を再帰解析し、CPU スコープと関数進入回数カウンターの付与案をプレビューできます。解析・レポート保存では元ソースを変更しません。

[調査・設計・実験結果の目次](wiki/README.md)

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
# 読み取りと解析だけを実行
dotnet run --project src/UEEtc.Console -- "D:/Projects/posita/git/Game/BlockBreaker/Source"
```

WPF ではフォルダーを入力・選択して「解析・プレビュー」を押してください。付与後／付与前のソース、関数の判定理由、診断を確認できます。

両方ともレポート・差分・加工済みコピーの保存に対応します。実際に元ファイルを変更する操作は、WPF の「元ファイルへ適用」または Console の `--apply` です。適用時にはバックアップを作成します。[詳細な使い方と制限](wiki/tracing/implementation.md)を参照してください。

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

# Lyra / BlockBreaker を読み取り、加工済みコピーだけを artifacts/ に出力
./scripts/Invoke-TraceExperiments.ps1
```
