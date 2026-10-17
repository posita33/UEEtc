---
date: 2026-10-17
tags: [csharp, unreal-engine, tracing, wpf, cpp]
status: todo
---

# STRへの統一と独自CPUマクロ・ヘッダー（Issue #53）

## 生成する形式

CPU計測の新規追加は、固定文字列1引数の形式に統一した。
標準は `TRACE_CPUPROFILER_EVENT_SCOPE_STR("クラス名 関数名");` と
`#include "ProfilingDebugging/CpuProfilerTrace.h"`。
通常形の `TRACE_CPUPROFILER_EVENT_SCOPE` と動的名用の `TRACE_CPUPROFILER_EVENT_SCOPE_TEXT` は新規生成しない。
既存の通常形・STR・TEXT・cycle counterなどは維持し、自動置換はしない。
対象の絞り込み、Super後の配置、gotoの除外、イベントカウンターとの独立性は変更していない。

独自マクロを指定した場合も、ラベルは同じ固定文字列リテラル。
引数の引用符・バックスラッシュはエスケープし、動的な文字列生成は追加しない。
通常形と固定STRのUE 5.8での実行経路は同じであり、今回の統一は性能改善を理由とするものではない。

## WPFで指定する

CPU画面・CPUとイベントの統合画面で、次の欄を設定する。

| 欄 | 標準値 | 独自設定の例 |
| --- | --- | --- |
| CPUマクロ（固定文字列1引数） | `TRACE_CPUPROFILER_EVENT_SCOPE_STR` | `PROJECT_TRACE_CPU_SCOPE_STR` |
| 定義ヘッダー（includeパス） | `ProfilingDebugging/CpuProfilerTrace.h` | `Tracing/ProjectCpuTrace.h` |

設定は画面ごとに保存・復元する。変更時は古いプレビューを無効にするので再解析する。
イベント単独画面にはCPU設定を追加しない。
ヘッダー欄はソース内に生成するincludeパスであり、ディスク上のフルパスではない。
独自ヘッダーはプロジェクト側で用意し、対象モジュールからincludeできる場所へ配置する。
例えば `MyModule/Public/Tracing/ProjectCpuTrace.h` を配置し、必要なモジュール依存関係もプロジェクト側で設定する。
このツールはヘッダーやBuild.csをプロジェクトへ自動作成・変更しない。

指定ヘッダーはCPU追加があるファイルにだけ挿入し、同じパスの無条件includeが既にあれば増やさない。
独自ヘッダーの指定時にEngineのCPUヘッダーを別途自動追加することはない。
同名でも異なるディレクトリのヘッダーは区別する。
標準マクロ名を維持し、定義ヘッダーだけ独自のものへ変更することも可能。

## DevelopmentとTestだけ有効にする例

[サンプルヘッダー](../../examples/Tracing/ProjectCpuTrace.h)をプロジェクトへコピーし、名前を適宜変更する。

```cpp
#pragma once
#include "ProfilingDebugging/CpuProfilerTrace.h"

#if UE_BUILD_DEVELOPMENT || UE_BUILD_TEST
#define PROJECT_TRACE_CPU_SCOPE_STR(Name) TRACE_CPUPROFILER_EVENT_SCOPE_STR(Name)
#else
#define PROJECT_TRACE_CPU_SCOPE_STR(Name)
#endif
```

上の表の独自設定を使うと、次のように生成する。

```cpp
#include "MyActor.h"
#include "Tracing/ProjectCpuTrace.h"

void AMyActor::Tick(float DeltaSeconds)
{
    Super::Tick(DeltaSeconds);
    PROJECT_TRACE_CPU_SCOPE_STR("AMyActor Tick");
    DoWork();
}
```

DevelopmentとTestではEngineのSTRへ展開し、DebugとShippingでは空になる。
DebugGameはモジュールごとのUBT定義に従う。構成名を文字列で推測せず、コンパイル時のUE_BUILD_*で判定する。
さらにEngine側の `CPUPROFILERTRACE_ENABLED` と実行時のCPUチャンネル設定も有効である必要がある。
独自ラッパーはEngine側で無効なトレースを強制的に有効化しない。
Engine標準名を#undefして置き換える構成より、独自名で包めば既存のEngine計測へ影響を広げずに設定できる。

マクロは呼び出し元のスコープ内にRAII変数を置く必要がある。
`do { TRACE_...; } while (0)` や関数で包むと、その内部で計測が終わってしまうので使用しない。
固定文字列1引数、同じ呼び出しスコープの寿命、UEの安全な計測APIを維持するのが独自ヘッダー側の契約。
任意の独自ヘッダーの意味やビルド可能性まではツールの字句解析では検証しない。

## ConsoleとCore

```powershell
dotnet run --project src/UEEtc.Console -- cpu D:\Game\Source --cpu-macro-name PROJECT_TRACE_CPU_SCOPE_STR --cpu-header Tracing/ProjectCpuTrace.h --report D:\TraceReports\cpu
dotnet run --project src/UEEtc.Console -- combined D:\Game\Source --cpu-macro-name PROJECT_TRACE_CPU_SCOPE_STR --cpu-header Tracing/ProjectCpuTrace.h --report D:\TraceReports\combined
```

省略すると標準のSTRとEngineヘッダーを使う。従来のルート形式のCLIも同じ設定に対応する。
旧 `--cpu-macro auto|scope|str` は廃止した。固定STRだけなら指定自体を削除する。
Coreは `InstrumentationOptions.CpuTrace` と `CombinedTracingService.Analyze(..., cpuTrace: ...)` へ
`CpuTraceSettings` を渡す。`CpuMacroKind` と旧 `InstrumentationOptions.CpuMacro` は廃止した。

識別子ではないマクロ名、標準の通常形・動的TEXTを指定する名前、空欄・絶対パス・親参照・改行・引用符を含むヘッダーを拒否する。
既存の独自マクロ呼び出しは**同じマクロ名の設定**で再解析すれば追加しない。
別の独自名へ設定を切り替えても以前の任意のマクロの意味までは識別しないので、既存の変換結果を一括改名する用途には使わない。
マクロやヘッダーを切り替えて比較する場合は未加工ソースのコピーから解析する。

## Engine参照・責務と検証

`CpuProfilerTrace.h` の399–409行の固定STR、445–454行の通常形からSTRへの展開、19–25行の有効条件、
`Misc/Build.h` のUE_BUILD_*定義を参照した。
`CpuTraceSettings` が共通の既定値・入力検証、`CpuScopeMacro` が呼び出しの文字列化、
`CppInstrumenter` が設定ヘッダーのincludeと既存マクロの検出、WPFの `CpuTraceSettingsEditor` が入力・保存を担当する。

- Releaseビルド: 警告0・エラー0。Core 141/141、WPF 211項目成功。
- 独自名と独自ヘッダー、標準名と独自ヘッダー、include重複防止、同設定での再解析、既存計測維持、不正設定拒否、画面の保存・復元を確認。
- `Test-CustomCpuTracing.ps1`: サンプルそのものとEngineから抽出したSTRのマクロ定義を使い、Debug/Development/Test/Shipping × Engine計測有効/無効の8構成をMSVC C++20 `/W4 /WX`でコンパイル・実行。
  Development/Testのみに限定されること、Super後からの計測、通常終了・早期return・例外時の寿命を確認。
- `Test-CombinedTracing.ps1 -EngineMacroDefinitions`: 標準/独自STR × CPU有効/無効 × カウンター有効/無効の8構成を確認。
- `Test-GotoInstrumentation.ps1`: 4構成、`Test-CpuScopePolicy.ps1`: 4構成のコンパイル・実行も成功。

C++検証のイベント出力バックエンドはフィクスチャであり、UE本体のビルドやInsights収録ではない。
Engine・Lyra・BlockBreakerのソースへは適用していない。
