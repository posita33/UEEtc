---
date: 2026-10-16
tags: [wiki, research/tech, unreal-engine, unreal-insights, tracing]
status: todo
---

# CPU 対象の絞り込みと数量 ADD（Issue #18・#21）

[目次](../README.md) / [CPU 配置](cpu-scope-policy.md) / [イベント選択](event-counters.md)

## Engine 調査

UE 5.8 の `Engine/Source` と `Engine/Plugins` を検索して代表箇所を読みました。以下は `C:/Program Files/Epic Games/UE_5.8/Engine/` 基準のパスです。全箇所の意味解析ではなく、Engine に全関数共通の選定規則があるという結論でもありません。

### INCREMENT と ADD

| 実装箇所 | 計測の意味 |
| --- | --- |
| `Source/Developer/DerivedDataCache/Private/HttpCacheStore.cpp:2771–2772, 2870–2871` | 通信完了時の物理受信・送信バイト数を別カウンターへ ADD |
| 同ファイル `3156, 3179, 3234, 3257, 3325` | `Requests.Num()` を要求件数へ ADD。ADD はバイト専用ではない |
| 同ファイル `3166, 3201, 3218` | 成功応答ごとにヒット数を INCREMENT。要求総数とは計測箇所・意味が異なる |
| `Source/Developer/DerivedDataCache/Private/S3CacheStore.cpp:1686, 1692, 1730, 1737` | 要求数・命中数を INCREMENT。数量の併記は必須ではない |
| `Source/Developer/IoStoreUtilities/Private/IoStoreWriter.cpp:374, 427, 551` | 処理中件数・使用メモリーに ADD。別箇所で減算する現在値のライフサイクル |

定義は `Source/Runtime/Core/Public/ProfilingDebugging/CountersTrace.h:393–394` で、ADD は宣言済みカウンターの `Add(Value)` に展開されます。無効時は475行付近で引数評価ごと消えるため、副作用のある式を追加してはいけません。

[公式 Trace Developer Guide](https://dev.epicgames.com/documentation/unreal-engine/developer-guide-to-tracing-in-unreal-engine) の例も、回数と書き込み量を別々の識別子で計測します。単位の異なる値を混ぜると解釈できません。同じ単位の要求数を一件なら INCREMENT、バッチなら ADD として共有する設計は可能ですが、本機能では別の数量カウンターを生成します。

### CPU スコープ

| STR の実装箇所 | 観測している処理 |
| --- | --- |
| `Source/Runtime/Engine/Private/AssetCompilingManager.cpp:676` | 全アセットのコンパイル完了待ち。管理対象をループして完了処理 |
| `Source/Runtime/Engine/Private/InstancedStaticMesh.cpp:433, 778, 1023, 4472` | RHI初期化、バッファーバインド、変換の一括更新 |
| `Source/Runtime/Engine/Private/PrimitiveSceneProxy.cpp:930` | レンダースレッドでのインスタンス更新 |
| `Plugins/Runtime/InstancedActors/Source/InstancedActors/Private/InstancedActorsModifierVolumeComponent.cpp:59, 102, 154` | BeginPlay / EndPlay の独自処理、保留中の複数インスタンス修正。前二つは Super 後 |

待機・データ処理・複数対象の更新・ライフサイクル処理を観測する例です。名前だけで重いと断定したり、BeginPlay なら必ず付けたりはできません。単一の呼び出しでも重い場合、ループでも軽い場合があります。非同期処理を起動する関数のスコープには、その後の非同期実行時間は含まれません。

## CPU の選定実装

WPF の標準と明示的な `cpu` コマンドは、従来の構文・配置判定を通った関数について、計測開始位置以降に次のいずれかがあれば追加します。

- `for` / `while` / `do` のループ。
- `Serialize`、`Deserialize`、`Compress`、`Decompress`、`Uncompress`、`Flush`、`Wait`、`WaitForCompletion`、`ParallelFor`、`LoadFileToArray`、`SaveArrayToFile`、`Sort`、`StableSort` の呼び出し。
- 4箇所以上の処理呼び出し。`Get` / `Is` / `Has` / `Can` で始まる名前、制御構文、一部キャスト、`UE_` マクロ、check / ensure 系を除外。

閾値とAPIリストは本ツールの初期ヒューリスティックであり、Engine の規約や実測から導いた閾値ではありません。ラムダ本体の後日実行、コメント・文字列は根拠にしません。マクロ展開、型・呼び出し解決、実行頻度、配列サイズ、実負荷は判定しません。添字とラムダの曖昧な構文も保守的に読み飛ばす場合があります。

既存計測・短いラベル・安全な Super 後配置は維持します。Super 前だけにループがあっても追加せず、`CpuDecision` に追加・除外理由を出します。

```powershell
dotnet run --project src/UEEtc.Console -- cpu "D:/Game/Source" --report "D:/Reports/focused"
# 従来の広い対象（構文や既存計測の除外は引き続き有効）
dotnet run --project src/UEEtc.Console -- cpu "D:/Game/Source" --all-functions
```

WPF は「全関数（互換）」で切り替えます。API の `InstrumentationOptions` は互換性のため `FocusedCpu=false` が既定です。直接呼ぶ場合は `new(FocusedCpu: true)` を渡します。従来のパス始まりのCLIと進入回数モードは維持します。

## 数量 ADD の設定

イベント画面で候補を解析し、一覧で対象を選択します。「数量 ADD（任意）」タブで追加を有効にし、専用の識別子・表示名・値・単位を入力し、ソースから値の意味を確認してチェックします。同じ分岐の入口に INCREMENT と ADD、ファイルの匿名名前空間に独立した二つの atomic int 宣言を生成します。編集すると適用可能な変更案を破棄します。数量設定・有効化・確認状態は保存せず、次回確認し直します。

CLI は選択 JSON の各行に任意の `Quantity` を指定します。省略・nullなら回数のみです。Id と fingerprint は自分の discovery レポートから取得します。

```json
{
  "Id": "候補の実際のId",
  "CounterName": "WriteSuccessCount",
  "DisplayName": "IO/WriteSuccess",
  "Quantity": {
    "CounterName": "WrittenBytes",
    "DisplayName": "IO/WrittenBytes",
    "Value": "Size",
    "Unit": "bytes",
    "MeaningConfirmed": true
  }
}
```

値は候補 JSON の `AddValues` に挙がる値引数か、0～4294967295 の十進整数です。`uint8/16/32`、`uint*_t`、対応する `std::` 型、一部 `unsigned` 型を認識します。参照・ポインター・符号付き・64ビット・型別名・既定値・複雑な宣言は候補にしません。挿入前に同名トークンを使っていたら、シャドーイング等を避けるため除外します。条件で値を読んだだけでも除外する保守的な制限です。関数呼び出し、メンバー参照、演算式、インクリメントは拒否します。

構文上利用可能な値と、実際に処理した量という意味は別です。渡されたサイズを成功前に実転送量として数えることはできません。必要なら確定値を安全な値引数で受け取る処理へ手動で切り出してから選択します。Engine のメンバー式や `Requests.Num()` の自動複製はしません。今回の累積数量機能は減算が必要な現在値も扱いません。

識別子は回数・数量・既存ソース間で衝突解決し、数量表示名に `[bytes]` 等を付けます。レポートに `AddedQuantityCounters` と `QuantityCounter` / `QuantityValue` / `QuantityUnit` を出します。

## 検証

| 対象 | 従来CPU追加 | 絞り込み後 | 診断 | 加工コピー再解析の追加変更 |
| --- | ---: | ---: | ---: | ---: |
| Lyra: 233 cpp / 全486ファイル | 1370 | 451 | 0 | 0 |
| BlockBreaker: 47 cpp / 全113ファイル | 223 | 93 | 0 | 0 |

`scripts/Invoke-TraceExperiments.ps1 -Focused` と互換モードを比較しました。両方で `git apply --check` が通り、元599ファイルの SHA-256 は一致しました。件数減少は性能改善や最適な観測点を実測で証明したものではありません。

- Core: 87テスト。絞り込み・Super後判定・ラムダ除外・入力制約・衝突・JSON往復・再付与防止。
- WPF: 58チェック。互換切替・候補とのバインド・数量プレビュー・編集時無効化・再起動時の未確認状態。
- MSVC `/W4 /WX`: 既存12構成、CPU配置4構成、回数のみ2構成、数量併用2構成。有効・無効で処理結果を維持し、3回と9itemsを独立集計。
- Release ビルド: 警告0・エラー0。

数量テストは `scripts/Test-EventCounters.ps1 -Quantity` で再実行できます。Engine・ゲーム元ソースへの適用、UE全体のビルド、Insights実測は行っていません。Issue #10 のイベント候補選定全体の改善とは分けた実装です。
