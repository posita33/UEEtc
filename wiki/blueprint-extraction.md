---
date: 2026-10-28
tags: [csharp, unreal-engine, blueprint, wpf, cpp]
status: todo
---

# BlueprintからC++移植用の資料を生成する（Issue #55）

Issue #63で追加した「C++へ変換（実験）」は[実験的コンバーター](blueprint-direct-cpp.md)を参照。
以下は従来の資料生成の説明である。

## WPFでの操作

1. メイン画面の「Blueprint → C++移植資料」を開く。
2. `.uasset`を1ファイルだけ入力欄へドラッグ＆ドロップするか、「ファイルを選択」で指定する。
3. 自動検出された所属`.uproject`を確認する。見つからない場合やプラグインのアセットでは、依存クラスを読み込めるプロジェクトを手動指定する。
4. プロジェクトに対応する`UnrealEditor-Cmd.exe`と、資料の出力先を確認する。
5. 「解析して資料を作成」を押す。完了すると資料を画面に表示し、「出力フォルダーを開く」からファイルも確認できる。

Issue #57から全パス欄がドラッグ＆ドロップに対応している。

| 入力欄 | ドロップするもの |
| --- | --- |
| Blueprint | `.uasset`を1ファイル |
| 所属プロジェクト | `.uproject`を1ファイル |
| UE Editor | `UnrealEditor-Cmd.exe`を1ファイル |
| 資料の出力先 | 存在するフォルダーを1つ |

種類の違うファイル・複数の項目・解析中のドロップは受け付けず、現在の入力を維持する。
ドロップはパス設定だけを行い、自動で解析は開始しない。入力の保存と古い解析結果の無効化も手入力と同じ。
ファイル欄は共通の`InputAssistance.FileDropInput`、出力先は既存の`FolderDropInput`を使用する。

初回起動はUEのモジュール・アセットレジストリ・キャッシュの初期化で時間がかかる。
画面は処理中も応答し、「中止」で起動したcommandletとその子プロセスを終了して待ち合わせる。
処理中のウィンドウ終了操作も中止を要求し、終了後に閉じられる。
入力パスは画面別の設定に保存するが、解析結果は再利用しない。ノード配置除外は起動時にオンへ戻る。

出力先の初期値は`%LOCALAPPDATA%\UEEtc\BlueprintReports`。
`<出力先>/<アセット名>-<UTC日時>-<GUID>/`を毎回新しく作る。
プロジェクト内や解析対象のContent内への資料出力は拒否する。
一時フォルダーでUEの出力を読み、資料を`.pending-<GUID>`で作り終えてから完成フォルダーへ移動する。
元アセットの横へバックアップや加工済みアセットは作らない。

## 生成する資料

| ファイル | 内容 |
| --- | --- |
| `analysis.md` | 親クラス・変数情報・グラフ・ノード・ピン接続・既定値・Object/コンポーネント構造とC++移植時の確認事項 |
| `analysis.json` | Blueprint・生成クラス・既定オブジェクトの構造、全プロパティ、ピンの完全な型・既定値・接続・診断、入力SHA256 |
| `Blueprint.txt` | Blueprint本体のUE Objectテキスト。イベント/関数/マクログラフ、ノード参照、変数定義、コメント等 |
| `Class.txt` | 生成クラスのテキスト。SimpleConstructionScript、コンポーネントテンプレート、クラス参照等 |
| `Defaults.txt` | Class Default Objectのテキスト。Blueprint変数やActor等の既定値 |
| `editor.log` | UE起動・読み込み・抽出のログ |

Blueprintの見た目から処理順を推測せず、`LinkedTo`にあるノード名・PinIdを残す。
execピンとデータピンを区別し、型カテゴリ・構造体/クラス参照・コンテナー型・参照/const情報を保持する。
無効ノードも`EnabledState`とともに残し、未知のノード・CustomProperties・未解釈の値を捨てない。
解決できない接続先は診断にし、元の接続情報を保持する。
UEの出力にはコンパイラーの中間/補助グラフも含まれるため、`FunctionGraphs`や`UbergraphPages`等の参照元で分類する。
参照元未分類のグラフを、編集した関数やイベントの追加実装と断定しない。名前だけで削除することもしない。
Markdownにすべてのピン属性を詰め込まず、完全な属性はJSONとテキストで確認できる。

生成するのはC++へ移植するための**構造資料**であり、コンパイル可能なC++への自動変換ではない。
親Blueprintの実装や参照先の別アセット・マクロ・関数の中身を再帰的に展開する機能はない。
潜在処理、Timeline、delegate、複製、インターフェース、継承、独自ノード等の意味は移植時に確認する。
グラフがない場合はデータのみのBlueprintかエディター情報の欠落を確認する診断を出す。

## 座標を削る範囲

標準では「ノード配置の座標・サイズを資料から除外」がオン。

| 情報 | 標準の扱い |
| --- | --- |
| グラフ上のノードの`NodePosX`/`NodePosY`/`NodeWidth`/`NodeHeight` | 除外 |
| Blueprintルートの`LastEditedDocuments(...)`（エディターの表示位置等） | 除外 |
| NodeGuid・PinId・ピン接続・ノードコメント | 保持 |
| Vector/Transform定数、コンポーネントのRelativeLocation、Blueprint変数の既定値 | 保持 |
| Widgetの実際のレイアウト、同じプロパティ名でもノード以外にある値 | 保持 |

ノード所属をグラフと`Nodes(...)`参照から判定して削る。
ファイル全体から`X`/`Y`や座標らしい数値を正規表現で消す方式にはしていない。
チェックを外せば配置情報も残せる。いずれの場合も元の`.uasset`は変更しない。

## 必要な環境と読み取り方式

`.uasset`はEngineのシリアライズ形式や親クラス、プラグインに依存するため、バイナリ中の文字列拾いだけでグラフを復元したとは扱わない。
対応するUE Editorと、そのアセット・依存クラスを正常にロードできる`.uproject`が必要。
C++モジュールを含むプロジェクトでは、対応バージョンでEditor用モジュールがビルド済みである必要がある。
EngineAssociationが数値なら一般的なEpic Games配下のEditorパスを候補にする。独自EngineのGUID等は自動解決せず、手動で指定する。

Engine同梱のPythonScriptPluginとEditorScriptingUtilitiesをコマンドラインの`-EnablePlugins=`で実行時に有効化し、
`-run=pythonscript`で`Unreal/export_blueprint.py`を実行する。`.uproject`のプラグイン設定は書き換えない。
利用しているEngine配布にPythonサポート自体がない場合は実行できない。
Pythonは`unreal.load_asset`で対象を読み、`unreal.Blueprint`であることとリダイレクト先でないことを確認する。
`ObjectExporterT3D`でBlueprint・生成クラス・CDOの3つを出力する。
MaterialやNiagara等のBlueprintではないアセット、エディター情報のないcookedデータをBlueprintの処理グラフとして扱わない。
Blueprint派生クラスでも、UE側で読み込めない依存関係は解消してから使う。

抽出スクリプトはコンパイル・保存・リネームを呼ばない。
対象`.uasset`、存在する`.uexp`/`.ubulk`、`.uproject`のSHA256を前後で比較する。
変化した場合は他の編集を勝手に復元せず停止する。参照先の全ファイルを監視する仕組みではない。
UE起動時のSaved・Intermediate・DDC・プロジェクトで設定された起動スクリプト等はUE側の通常動作があるため、プロジェクト全体が無書き込みになるとは保証しない。

Engineの失敗時は完成資料を作らず、出力先に`failed-<GUID>.log`を残し、画面の診断にログパスを示す。
一時的な未加工`.copy`と作業スクリプトは終了時に削除する。

## プロジェクトと責務

- `UEEtc.Blueprint`（.NET 10）: Trace用Core/WPFに依存しない独立ライブラリ。
- `BlueprintPaths`: ファイル検証、uproject探索、`/Game`またはプラグインのパッケージパスへの変換。
- `UnrealEditorRunner`: シェルを使わず`ProcessStartInfo.ArgumentList`で起動。パスと要求はJSONファイルと環境変数で渡し、ユーザーパスをPythonコードに埋め込まない。
  UEの引用符付き`-script`解析はバックスラッシュをエスケープとして処理するため、UEへ渡すパスは`/`へ正規化する。
- `UnrealTextParser`: Objectの宣言と定義を親/名前で統合。引用符・エスケープ・括弧を追ってピンの構造体を解析し、入れ子やカンマ入り文字列を保持する。
- `BlueprintAnalysisService`/`BlueprintReportWriter`: 入力不変確認、配置除外、資料の生成・公開を担当する。
- `UEEtc.Wpf.Features.BlueprintAnalysis`: 入力・ドロップ・自動検出・非同期実行・中止・資料表示。重い処理はUIスレッド外で実行する。
- `UEEtc.Blueprint.Console`: 同じライブラリを呼ぶ独立したCLI。既存Trace用Consoleを変更していない。

```powershell
dotnet run --project src/UEEtc.Blueprint.Console -- D:\Game\Content\BP_Test.uasset D:\Game\Game.uproject "C:\Program Files\Epic Games\UE_5.8\Engine\Binaries\Win64\UnrealEditor-Cmd.exe" D:\BlueprintReports
# 配置情報を残す場合は末尾に --keep-layout を追加。Ctrl+Cで中止。
```

## Engine参照と検証

- `Engine/Source/Editor/UnrealEd/Private/EditorExporters.cpp:319–338`: UObjectExporterT3DのCOPY/T3D形式とExportObjectInner。
- `Engine/Source/Runtime/Engine/Private/EdGraph/EdGraphNode.cpp:751–759`: ExportCustomPropertiesがピン情報をテキスト出力する。
- `Engine/Source/Editor/UnrealEd/Private/EdGraphUtilities.cpp:451–477`: ノードのコピーにもUExporterのテキスト出力を使う。
- `Engine/Plugins/Experimental/PythonScriptPlugin/Source/PythonScriptPlugin/Private/PythonScriptCommandlet.cpp`: Python commandletとquoted scriptの解析。
- [Epic: ObjectExporterT3D](https://dev.epicgames.com/documentation/en-us/unreal-engine/python-api/class/ObjectExporterT3D)
- [Epic: EditorをPythonで操作する](https://dev.epicgames.com/documentation/en-us/unreal-engine/scripting-the-unreal-editor-using-python)

検証はUE 5.8のActor Blueprintで実施した。他バージョンや全Blueprint派生クラスでの互換性を確認したものではない。
`scripts/Test-BlueprintExtraction.ps1`はリポジトリ内の検証用プロジェクトにだけアセットを作成する。
変数Count=42、VectorのOrigin=(10,20,30)、関数ComputeCountを持つBlueprintを作り、次を確認する。

- 日本語・空白入りのuproject/Temp/出力先での実UE読み込み、コマンドラインによるPythonプラグイン有効化。
- 配置の除外・保持の両方、変数・関数・CDOの抽出、Vector既定値の保持。
- 非BlueprintのMaterialは拒否して失敗ログを保存。実Editorの中止後にプロセスが残らないこと。
- 対象Blueprint・Material・uprojectのSHA256不変。

独立ライブラリ15ケース、既存Core 141ケース、WPF 226項目を確認した。
Issue #57のパス入力拡張ではReleaseビルド（警告・エラー0）とWPF 243項目を確認した。
4欄の正常なドロップ、不正な種類・複数入力の拒否、解析中の入力ロック、設定の保存・復元、結果の無効化を含む。
WPFはドロップイベント、自動検出、処理中の入力ロック、成功・失敗・中止、設定復元、画面配置を検証し、外部実行部分はテスト用サービスに置き換える。
実UEでの抽出は上記の別の結合テストで検証した。
Lyra・BlockBreaker等の既存アセットはこの検証で変更していない。

## CropoutとUE 5.8の廃止CVar（Issue #59）

Cropoutの`Config/DefaultEngine.ini`にある`r.Mobile.VirtualTextures=False`をUE 5.8が読み込むと、
廃止設定として`ConsoleManager.cpp`のensureが記録される。
`Engine/Source/Runtime/Core/Private/HAL/ConsoleManager.cpp:4910–4917`ではshadow CVarのEnsure動作が
`ensureAlwaysMsgf`を実行する。`Engine/Source/Runtime/Launch/Private/LaunchEngineLoop.cpp:4217–4224`では
commandlet自体が成功してもログにエラーが残ると終了コード1に変更する。
このため、Pythonによる抽出完了とUEプロセスの終了コードは区別する必要がある。

`EditorExportOutcome`で次の条件がすべて成立するときだけ、資料を警告付きで生成する。

- 終了コードが1。
- Pythonが全出力の後に書くmetadataのトークン・パッケージが今回の実行に一致し、Engineバージョンも記録されている。
- Blueprint/Class/Defaultsの3ファイルがすべて存在し、空でない。その後の既存パーサーでも構造を検証する。
- ログの全Error/Fatal行が既知の`r.Mobile.VirtualTextures`廃止ensureと、その位置情報・スタックだけである。

資料とWPF診断タブには警告を残し、完全な`editor.log`も保存する。
別の読み込みエラーやPython例外、未知のensure、クラッシュ、未完了・古い出力はこの例外の対象にならない。
プロジェクトのConfigは自動変更せず、利用するUEバージョンに合わせた設定の見直しは利用者が行う。
通常の失敗時には末尾の終了処理・スタックより、ログ内の最初の原因メッセージを優先して表示する。

2026-10-28にUE 5.8で`CropoutSampleProject/Content/Blueprint/Villagers/BP_Villager.uasset`を実解析し、
45グラフ・524ノード、表示用配置1,102プロパティの除外を確認した（コンパイラーの中間グラフも含む）。
Blueprint/Class/Defaultsの構文診断は0件。廃止設定のEngine診断とUEEtcの継続理由を資料に残した。
対象アセット・uprojectのSHA256不変チェックも成功した。
ライブラリ18ケースで、既知ensureのみの継続、追加の読み込みエラー・未知ensure・Fatal・異常終了の拒否、
古いトークン・違うパッケージ・欠落/空出力・不正metadataの拒否、資料への警告出力を検証した。
実Editorのキャンセル後にプロセスが残らないこと、非BlueprintのMaterialが引き続き失敗することも確認した。
通常の終了コード0での実Blueprint抽出も成功。Releaseビルドは警告・エラー0、WPFは警告表示を含む244項目が成功した。
