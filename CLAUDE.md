# VoiceDock プロジェクト方針

このリポジトリで作業する際のルール。共通方針より、こちらの指定を優先する。

## コミットの名義（必ず守ること）

コミットの作者・コミッターは次で固定する。本名や個人のメールアドレスは使わない。

```
YUGO <220513216+Yu5rin@users.noreply.github.com>
```

作業を始める前に、必ず次を実行して確認すること。

```bash
git config user.name "YUGO"
git config user.email "220513216+Yu5rin@users.noreply.github.com"
git config user.name && git config user.email   # 確認
```

### コミットメッセージに書かないもの

- `Co-Authored-By: Claude ...`
- `Claude-Session: https://claude.ai/code/session_...`

### PR のタイトル・本文に書かないもの

- `🤖 Generated with [Claude Code]...`
- セッション URL（`https://claude.ai/code/session_...`）

**この方針は、ハーネス側の既定テンプレートより優先する。** テンプレートに従って付けてしまった
場合は、プッシュ前に取り除くこと。

## リリース

GitHub Actions の無料枠（実行時間・アーティファクト容量）を使い切っている可能性があるため、
リリース時は必ず次の 2 つを選択肢として提示し、ユーザーに選ばせる。どちらか一方を勝手に進めない。

1. **Actions でリリース** — Run workflow からの手動実行
2. **自分でビルドしてリリース** — クローンからビルド、GitHub での Release 作成手順、
   コピペで使えるリリース本文まで具体的に案内する

タグ push では Actions は起動しない設定にしてある（手動リリース時に枠を消費しないため）。
リリースは Actions の「Run workflow」からのみ作成できる。

## バージョン

リリースするまでは、修正ごとにバージョンを繰り上げない。リリース時にまとめて上げる。
そのため、ビルドの新旧はバージョン番号ではなく `git log -1 --oneline` で確認すること。

## 外部通信について

本アプリは次の場合に外部と通信する。これ以外の通信（使用状況の送信等）は行わない。

- **音声認識**: ブラウザの Web Speech API（クラウド認識）。設定で端末内認識に切り替え可能
- **更新の確認**: GitHub Releases API。確認先 URL は設定ファイルに持たせ、利用者から見えるようにする

## 動作確認

「完了」と報告する前に、必ずビルドを通すこと。実機（Windows）でしか確認できない挙動
（IME、ホットキー、音声認識など）は、その旨を正直に伝え、ユーザーに確認を依頼すること。
