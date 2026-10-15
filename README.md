# UEEtc
UEいろいろコード

Unreal Engine 関連の C# Console / WPF 開発を試すための実験リポジトリです。

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
  UEEtc.Console/    # Console アプリ（net10.0）
  UEEtc.Wpf/        # WPF アプリ（net10.0-windows）
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

Console は起動メッセージと使用中の .NET ランタイムを出力して終了します。
WPF は起動確認用のウィンドウを表示します。

Release ビルド:

```powershell
dotnet build UEEtc.sln --configuration Release
```
