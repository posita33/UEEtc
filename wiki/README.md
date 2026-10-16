---
date: 2026-10-16
tags: [wiki, unreal-engine, tracing]
status: todo
---

# UEEtc 調査・実装ノート

Unreal Engine の C++ ソースに Trace マクロを付けるツールの記録です。

- [UE 5.8 のマクロ使用条件と付与方針](tracing/engine-research.md)
- [共通コア・WPF・Console の設計と使い方](tracing/implementation.md)
- [Lyra／BlockBreaker での実験結果](tracing/experiments-2026-10-16.md)
- [CPU スコープとカウンターがセットになる条件（Issue #5）](tracing/macro-pairing.md)

2026-10-16 の実験では、対象プロジェクトを読み取り、差分と加工済みコピーだけをリポジトリ内の `artifacts/` に出力しました。元の Source フォルダーには適用していません。
