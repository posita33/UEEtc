---
date: 2026-10-16
tags: [wiki, unreal-engine, tracing]
status: todo
---

# UEEtc 調査・実装ノート

Unreal Engine の C++ ソースに Trace マクロを付けるツールの記録です。

- [入力保存とフォルダードロップ（Issue #16）](input-assistance.md)
- [CPU 対象の絞り込みと数量 ADD（Issue #18・#21）](tracing/focused-cpu-and-quantity-counters.md)
- [変更プレビュー・選択・インデント・数量推奨（Issue #23・#24・#25）](tracing/preview-selection-and-indentation.md)
- [ファイル選択・数量の自動追加・短い表示名（Issue #27〜#30）](tracing/event-files-and-input-quantities.md)
- [CPU マクロ選択とカウンターの生存区間（Issue #32・#33）](tracing/cpu-macros-and-counter-lifetimes.md)

- [UE 5.8 のマクロ使用条件と付与方針](tracing/engine-research.md)
- [Engine のカウンター名の共有・重複傾向と命名改善（Issue #3）](tracing/counter-naming.md)
- [共通コア・WPF・Console の設計と使い方](tracing/implementation.md)
- [Lyra／BlockBreaker での実験結果](tracing/experiments-2026-10-16.md)
- [CPU スコープとカウンターがセットになる条件（Issue #5）](tracing/macro-pairing.md)
- [独立した付与条件と CPU のみの標準設定（Issue #7）](tracing/independent-trace-policy.md)
- [イベントカウンターの付与条件・機能別画面・名前空間（Issue #10、現行）](tracing/event-counters.md)
- [QUICK_SCOPE_CYCLE_COUNTER の使い方と有効条件（Issue #11）](tracing/quick-scope-cycle-counter.md)
- [CPU ラベルの簡略化と Super 後の配置（Issue #12・#13、現行）](tracing/cpu-scope-policy.md)
- [イベントカウンターの候補を絞る再調査（Issue #10 再開）](tracing/counter-effectiveness-review.md)

2026-10-16 の実験では、対象プロジェクトを読み取り、差分と加工済みコピーだけをリポジトリ内の `artifacts/` に出力しました。元の Source フォルダーには適用していません。
