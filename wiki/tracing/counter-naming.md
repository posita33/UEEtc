---
date: 2026-10-16
tags: [wiki, research/tech, unreal-engine, tracing, architecture]
status: todo
---

# Engine のカウンター名の共有・重複傾向と自動命名

[目次](../README.md) / [実装](implementation.md) / [Issue #3](https://github.com/posita33/UEEtc/issues/3)

## 結論

Engine は多くのカウンターに「システム名＋イベント・状態」を表す識別子を手で付けています。`TRACE_COUNTER_INCREMENT` の呼び出しごとに別の名前を作る仕組みではありません。同じ計測量を複数の経路で増やす場合、同じ識別子を再利用します。

今回確認した直接のカウンター定義では、識別子の文字列重複はありませんでした。一方、同じ識別子を引数にする increment は複数あります。この二つを分けて扱う必要があります。

## 調査方法と集計

ローカルの `C:/Program Files/Epic Games/UE_5.8/Engine/Source` と `Engine/Plugins` にある `.cpp` / `.h` を `scripts/Measure-EngineTraceCounters.ps1` で検索しました。`TRACE_COUNTER_INCREMENT` と、整数・浮動小数点・メモリー、atomic / unchecked / extern の直接の宣言マクロを集計しています。汎用 `TRACE_DECLARE_COUNTER` のマクロ本体、コメントだけの行、`#define` 行は集計から外します。

| 指標 | 件数 |
| --- | ---: |
| increment の記述箇所 | 76 |
| increment の引数の異なる識別子 | 51 |
| 2 箇所以上で使う識別子 | 15 |
| カウンターの直接の定義 | 308 |
| 定義の異なる識別子 | 308 |
| 複数の直接定義が見つかった識別子 | 0 |
| extern 宣言 | 27 |

51 種類の increment 識別子は、いずれもこの検索範囲で直接定義が一つ見つかりました。結果は `artifacts/engine-counters/20261016-094334/` の CSV / JSON に保存しています。

この集計はテキスト調査です。プリプロセッサーの分岐、ユーザー定義ラッパー、マクロ展開、ビルド対象・モジュール・名前空間を完全には解決しません。複数行コメントの内部や複雑な引数なども完全には判定しないため、Engine 全体の厳密な AST 調査や全ビルド構成での一意性の証明としては扱いません。

## 代表例

以下は `Engine/` を起点とした実ファイルです。

| 識別子 | 宣言・使用例 | 観察した意味 |
| --- | --- | --- |
| `HttpDDC_GetHit` | `Source/Developer/DerivedDataCache/Private/HttpCacheStore.cpp:86` で定義、2995 / 3201 / 3218 / 3279 / 3308 / 3352 行で increment | 異なる Get の成功経路を、同じ HTTP DDC 命中数として集計する |
| `ChunkDecoderQueueCount` | `Source/Runtime/Core/Private/IO/IoDispatcherChunkDecoder.cpp:9` で定義、29 / 112 / 188 行で increment | キュー追加経路が複数ある。同じファイルの decrement と組み合わせて状態を表す |
| `OnDemandContentInstaller_HttpRequestsFail` | `Source/Runtime/Experimental/IoStore/OnDemand/Private/OnDemandContentInstaller.cpp:35` で定義、1313 / 1393 行で increment | リクエスト結果や検証失敗を同じ失敗数へ集計する |
| `MutableRuntime_Updates` | `Plugins/Mutable/Source/MutableRuntime/Private/MuR/System.cpp:68` で定義、205 行で increment | システム名と更新イベントを組み合わせた名前 |

たとえば `HttpDDC_GetHit` を 6 種類の名前に分けてしまうと、本来まとめて見たい命中数を分断します。既存の共有カウンターを、自動で「重複」として改名しない方針にしました。

## C++ の名前と記録側の ID

`Source/Runtime/Core/Public/ProfilingDebugging/CountersTrace.h:288` の宣言マクロは、指定名から `__GTraceCounter<指定名>` という変数を作ります。同ファイルの 399 行の increment は、その変数の `Increment()` を呼びます。マクロに識別子の自動一意化機能はありません。C++ の名前空間やリンケージも含めて、同じ実体を指す名前を渡す必要があります。

`TEXT("...")` は表示名です。`Source/Runtime/Core/Private/ProfilingDebugging/CountersTrace.cpp:45–64` では、記録用の数値 `CounterId` を別途発行します。この ID 発行は、C++ の変数名やソース上の識別子の衝突を解決するものではありません。

宣言したカウンターの識別子を increment に渡す使い方は、[公式 Trace Developer Guide の Counters 節](https://dev.epicgames.com/documentation/unreal-engine/developer-guide-to-tracing-in-unreal-engine#counters)にも示されています。

無名 namespace は公開範囲を翻訳単位内に限定できますが、Unity Build の同一翻訳単位内では無名 namespace 同士を分離できません。詳細は [実装ノート](implementation.md#無名-namespace-の理由) を参照してください。

## 実装した命名規則

1. 基本名は `<namespace・クラス名・関数名>_CallCount` とする。`UEEtcCalls_` は付けない。
2. `::` 等を `_` にし、連続する `_` を作らない。無名 namespace は `Anonymous` として名前に含める。
3. 選択フォルダー内の全 `.cpp` の候補を集めてから命名する。オーバーロードや、区切り文字の正規化で同じ基本名になる別関数を検出する。
4. 既存 `.cpp` の識別子と、`.h` / `.hpp` / `.inl` / `.cc` / `.cxx` の識別子を予約する。実際のマクロ展開後の変数名 `__GTraceCounter<基本名>` との衝突も確認する。予約判定はコメント・文字列に現れる単語も含む保守的なテキスト判定。
5. 基本名が衝突・予約されている場合だけ、パスとシグネチャ由来の 16 桁のハッシュを付ける。その候補も予約されていれば `_2`、`_3` を付けて回避する。割り当て順はパス・シグネチャで固定する。
6. 条件コンパイルの代替枝にある同一シグネチャは、同じカウンターと宣言を共有する。通常のオーバーロードは独立した関数進入数として分ける。
7. 既存の計測関数はスキップし、意図的な共有カウンターを変更しない。

通常の例:

```cpp
// BlockBreakerGameMode.cpp の生成例
TRACE_COUNTER_INCREMENT(ABlockBreakerGameMode_BeginPlay_CallCount);
```

衝突するオーバーロードの例:

```cpp
// GameHUD.cpp の生成例: ShowCredits() と ShowCredits(FSimpleDelegate)
TRACE_COUNTER_INCREMENT(AGameHUD_ShowCredits_CallCount_7C28228ADC15DBE6);
TRACE_COUNTER_INCREMENT(AGameHUD_ShowCredits_CallCount_B0C9FE9028FBFC80);
```

生成中は外部へ出さない仮の識別子を使い、命名確定後にカウンター宣言・increment のトークンだけを置き換えます。表示名・CPU ラベル・元ソースの文字列やコメントは変更しません。`FunctionAnalysis.Counter` と判定理由にも確定後の名前と衝突回避の情報を反映します。

### 有効範囲と制限

衝突判定の範囲は選択した解析対象フォルダーです。除外ディレクトリ、対象外拡張子、外部ヘッダー、PCH、ビルド時マクロ、別に解析したフォルダーまで C++ コンパイラーのように解決するものではありません。個別に生成した複数のフォルダーを同じ Unity Build にまとめる場合は、共通の親フォルダーからまとめて解析してください。

同じ入力・同じ対象範囲なら命名は決定的です。後からオーバーロードや既存識別子を追加すると、必要なハッシュの有無が変わることがあります。すでに計測済みのファイルは再命名しないため、既存の計測を維持しながら新しい候補だけを回避名にできます。単一ファイル用 `CppInstrumenter.Analyze()` が照合するのは、そのファイルだけです。

元の表示名と CPU ラベルには、従来どおりパスとシグネチャ由来のハッシュを残しています。今回改善したのは `TRACE_COUNTER_INCREMENT` に渡す識別子です。

## 実プロジェクトでの検証

`artifacts/trace-experiments/run-20261016-094836-eccc205c/` に出力しました。

| 指標 | Lyra | BlockBreaker |
| --- | ---: | ---: |
| マクロ付与候補の関数定義 | 1,532 | 235 |
| 異なる生成カウンター | 1,529 | 235 |
| 通常の名前（ハッシュなし） | 1,523 | 233 |
| 衝突回避した名前（ハッシュ付き） | 6 | 2 |
| コピーの再解析による追加変更 | 0 | 0 |
| 元 Source のファイル差分 | 0 | 0 |

Lyra の 6 個は `FLyraInventoryList::AddEntry`、`ULyraSettingsListEntrySetting_KeyboardInput::HandleKeySelectionCanceled`、`ULyraTeamSubsystem::CompareTeams` の各 2 オーバーロードです。BlockBreaker の 2 個は `AGameHUD::ShowCredits` のオーバーロードです。

Lyra で定義数よりカウンター数が 3 個少ないのは、同一シグネチャの条件コンパイル代替定義が共有されるためです。集計値は呼び出し時のゲーム内イベント数ではありません。

Core の 35 テスト、WPF の 8 チェック、Release ビルド（警告・エラー 0）、元ソースの `git apply --check` が成功しました。独自 C++ fixture では、衝突するクラス・関数名を二つの `.cpp` に分け、それらを一つに include する Unity 相当の形で Trace 有効・無効の両方を MSVC C++20 でコンパイル・実行しています。

Engine / Lyra / BlockBreaker の元ソースへ適用していません。後二者は合計 599 ファイルの SHA-256 が実験前後で一致しました。UE 実プロジェクトの変更後ビルド・トレース取得は未実施です。
