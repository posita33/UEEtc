---
title: .uassetからC++を生成する実験
date: 2026-10-29
tags: [unreal-engine, blueprint, cpp, experiment]
---

# .uasset → C++の実験的変換（Issue #63）

Blueprint移植資料画面で`.uasset`、所属`.uproject`、対応するUnrealEditor-Cmd.exe、出力先を設定し、
**C++へ変換（実験）**を押すと、資料とC++プラグインの下書きを生成する。
各パスは従来どおりドロップ可能。C++プレビューと診断を確認してから出力フォルダーを開く。
「解析して資料を作成」は従来の資料生成だけを行う。

```powershell
dotnet run --project src/UEEtc.Blueprint.Console -- D:/Game/Content/BP_Test.uasset D:/Game/Game.uproject "C:/Program Files/Epic Games/UE_5.8/Engine/Binaries/Win64/UnrealEditor-Cmd.exe" D:/Reports --cpp
```

`--keep-layout`も併用できる。終了コードは対応範囲内の生成0、未対応を含む下書き3、引数エラー2、実行失敗1、中止130。
0でも生成C++のコンパイル・動作等価性は別途確認する。

## 処理の構成

1. `UEEtc.Blueprint`がUEでアセットを読み込み、Engine標準のテキスト出力を抽出する。解析は1回。
2. 独立した`UEEtc.Blueprint.Cpp`が`BlueprintReport`からユーザーのグラフ・変数・ピン接続を読む。
3. `BlueprintCppEmitter`が宣言・名前・型・関数を整理し、`GraphCompiler`が実行フローとデータ式を変換する。
4. `BlueprintCppConversionService`が新しい解析結果フォルダー内へCppを一時出力し、成功時だけ確定する。

バイナリ文字列の推測や、前回手書きしたBP_Villagerプラグインのコピーはしない。
UE・プロジェクト・依存モジュールが必要で、`.uasset`だけで依存を解決するオフライン変換ではない。
元アセット・uproject・プロジェクトSourceは変更しない。UEによるSaved/キャッシュ生成は従来どおり発生し得る。

## 最初の対応範囲

| 項目 | 内容 |
| --- | --- |
| 親クラス | EngineのActor/Pawn。その他は仮のActor＋Error診断 |
| 変数 | NewVariablesのbool/int32/int64/float/double/FName/FStringと対応する既定値 |
| 関数 | FunctionEntry/FunctionResultと基本型の入力・出力引数。戻り値も出力参照引数として生成 |
| 実行 | self変数代入、Branch、Sequence、execのReroute、関数終了 |
| データ | self変数取得、Entry引数、Reroute、基本型リテラル |
| 演算 | 明示したKismetMathLibraryの加算・減算・乗算・比較・等値比較。型とA/B引数名を確認 |
| BeginPlay | Actor.ReceiveBeginPlayに限りSuper::BeginPlay後に生成関数を呼ぶ |

FunctionGraphs、UbergraphPages、ImplementedInterfacesで参照されたユーザーグラフを対象にする。
ExecuteUbergraphや_MERGED等の中間グラフは二重変換しない。
名前はASCII識別子にし、衝突時は番号を付ける。関数は`Fn_...`、変数は`Var_...`。
元BPIや呼び出し先への自動的な差し替えは行わない。

実行フローはswitchによる状態遷移。Sequenceは残りの出力を逆順でスタックへ積み、先頭から実行する。
データ式は実行ノードごとに再評価し、更新前の値を全関数にキャッシュしない。
整数の加減乗算はunsignedで計算して戻す。ゼロ除算等を安易に生成しないため、除算は未対応。
循環するデータ式・型不一致・不正リテラルはErrorにする。
1呼び出し100,000ステップで終了する上限を設ける。元UEのループ検出設定と同じ条件ではない。

## 未対応箇所の扱い

Object/Struct/Enum、コンテナー、外部オブジェクトやローカル変数、一般関数呼び出し、マクロ、Delay、
タイマー、非同期ロード、Montage、コンポーネント構築、Construction Script、Blueprint専用インターフェース/RPC等は未対応。
親クラスの全既定値、メタデータ、アクセス権、純粋関数宣言、ネットワーク互換も再現しない。

未対応があれば生成ヘッダーの`#error`で既定のコンパイルを停止する。
`conversion.md`と`conversion.json`にグラフ・ノード・理由を残す。
未変換の実行ノードは、後続を黙って実行せずログを出して関数を終了する。
確認目的でビルド側に`UEETC_ALLOW_INCOMPLETE_BLUEPRINT`を定義するとガードだけ解除できるが、未変換処理が動くようにはならない。

`IsComplete`はこの小さな対応範囲にErrorがないという意味。
完全移植・コンパイル成功・ゲーム全体の等価性を判定する値ではない。
抽出に警告が残る場合もErrorを追加する。既定値のコピー範囲は対応範囲内でもWarningとして常に明示する。

## 出力

解析結果の`Cpp/`以下に、BlueprintConversion.uplugin、Build.cs、Module.cpp、ConvertedBlueprint.h/.cpp、
conversion.md/.jsonを作る。モジュール名はBlueprintConversion。
既存のCppは上書きせず、キャンセルや書き込み失敗時はC++用一時フォルダーを除去する。
UE解析が終わってから変換を中止した場合、解析資料自体は残ることがある。
異なるアセットの生成プラグインを、同じプロジェクトへ複数同名で入れない。

## Engine参照と検証方法

- `Engine/Source/Editor/BlueprintGraph/Private/K2Node_ExecutionSequence.cpp:42–143`: 後続状態を逆順にPushし先頭へGotoする考え方。
- 同ディレクトリの`K2Node_IfThenElse.cpp`: 条件・then/elseピン。
- 同ディレクトリの`K2Node_VariableSet.cpp`: 変数の値入力ピン。
- 抽出基盤は`blueprint-extraction.md`のObjectExporterT3D/EdGraphNodeを利用。

`dotnet run --project tests/UEEtc.Blueprint.Cpp.Tests -c Release`で型・制御フロー・不正入力・診断・出力・中止を検証する。
`scripts/Test-GeneratedBlueprintCpp.ps1`は合成グラフから生成したC++をUHT/MSVCでビルドし、
Engine Automationで加算・更新後の分岐・整数境界を実行する。
検証用HostProjectだけで任意のEngineプラグインを既定無効にし、Fab等の追加プラグインのルールに依存しない。
合成グラフの検証は、任意の実Blueprintを完全変換できることの証明ではない。

実BP_VillagerもConsoleから処理し、19関数の下書きを出力した。
Object/Enum、一般関数、非同期処理、インターフェース、コンポーネント等は未対応として記録される。
前回の手動移植例と同じ完成度には到達していない。

2026-10-29の確認結果:

- Releaseソリューションビルド: 警告・エラー0。
- 変換ライブラリ29確認、既存Blueprintライブラリ18ケース、WPF 250項目が成功。
- 生成した加算/BranchのC++をUE 5.8のUHT/MSVCでビルドし、Engine Automation 1ケース8確認が成功（警告・失敗0）。
- `artifacts/generated-cpp-validation/8b0a80f1f23e478799d8070621d754ef/AutomationReport/index.json`に実行結果を保存。
- 実BP_Villagerは19関数・Error診断89件・Warning1件の下書きになり、Console終了コード3を確認。元アセット/uprojectのSHA256確認も成功。
- 出力例: `D:/Projects/posita/Tmp/BlueprintReports/BP_Villager-20261028T233348261-aa7439f962d74fca9c8513cfaadc49f6/Cpp`。

生成フォルダー名の日時はUTC。実アセットの検証は読み込みから生成までの確認であり、89件の未対応処理を動作検証したものではない。
