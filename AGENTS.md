# redmine-cli-dotnet — エージェント向けの入口

Redmine の REST API を叩く CLI (`redmine.exe`)。Node 版 [redmine-cli](https://github.com/yakumo-amamiya/redmine-cli) の .NET 10 への移植で、
配布は Windows x64 の Native AOT の exe 1 つ。使い方と安全装置は [README.md](README.md)、AI 向けの手順書は [src/RedmineCli/guide.md](src/RedmineCli/guide.md)。

- ビルドとテスト: `dotnet build` / `dotnet test`。変更のあとは両方通す。警告はエラー扱い (`TreatWarningsAsErrors`、AOT の解析も含む)
- 動かす: `dotnet run --project src/RedmineCli -- <サブコマンド>`。本物の Redmine に書き込まないよう、確認は `--dry-run` か、テストのモックで行う
- 安全装置 (書き込み先は `.redmine.json` だけ、id 指定の書き込みは所属を確かめる、送信前の確認、API キーは
  プロジェクト専用の環境変数だけ) は弱めない。書き込み系のコマンドを足すときは `Context.ConfirmWriteAsync` と所属の検証を必ず通す
- `redmine setup` はキーを受け取る唯一の場面。端末からの実行に限り (非対話は終了コード 4)、キーとパスワードは画面・ログに出さない。
  受け取った値はプロジェクト専用のユーザー環境変数に書くだけで、CLI がキーを読むのは環境変数からのまま。引数でキーを渡す手段は作らない
- コマンド、`.redmine.json`、環境変数、終了コード、`--json` の形は Node 版と同じに保つ。変えるときは README の「Node 版との違い」に書く
- 終了コードの表は `Errors.cs`、`Cli.cs` (ルートのヘルプ)、`guide.md`、`README.md` にある。変えたら全部直す
- exe は Native AOT: JSON は `JsonNode` で扱い、リフレクションの直列化 (`JsonSerializer.Serialize<T>` など) は使わない。
  手元に C++ のツールが無いと AOT の publish はできないが、CI が毎回 AOT の exe を作って同じテストを流す
- 動きを変えたら `tests/RedmineCli.Tests` にテストを足す。テストは exe を子プロセスで動かし、Kestrel のモックが Redmine の代わりをする
  (`REDMINE_TEST_EXE` を指すと別の exe に同じテストを流せる)
- コードのコメントと XML doc は英語、利用者に見せる文言とドキュメントは日本語
- `install.ps1` は Windows PowerShell 5.1 でも動く書き方にする (`??`・三項演算子・`&&` を使わない)。`irm | iex` で呼び出し元のシェルで動くので `exit` を使わない
- `redmine update` は動いている exe を `.old` に退けて新しい exe と入れ替える。入れ替えた後は、まだ読み込んでいないコードを動かさない
  (exe のファイルがもうその場所に無い。結果の文字列は入れ替える前に作る)。テストは `REDMINE_CLI_RELEASES_URL` でモックの Release を指し、
  本物の exe は置き換えない。CI は AOT の exe を一時フォルダに置いて、本物の Release から `--to 0.2.0` で入れ替えを試す
- リリースは `v*` のタグ (README の「開発」)。Node 版のリポジトリは参照するだけで変更しない
