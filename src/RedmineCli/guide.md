# redmine CLI — AI エージェント向け手順書

このツールは Redmine の REST API を叩く CLI です。人間の手打ちと AI エージェントの両方から使われることを前提に、
「書き込み先を間違えない」ことを最優先に設計されています。この手順書は `redmine guide` で表示されます。

## 1. 安全モデル (最初に理解すること)

- **書き込み先は 1 つに固定**。カレントディレクトリから上位へ `.redmine.json` を探し、その `url` と `project` にしか書きません。
  コマンド引数で別のプロジェクトへ書く手段はありません。別プロジェクトを扱うなら、別のリポジトリ (別の `.redmine.json`) で実行します。
- **id 指定の更新は所属を検証**。`issues update/comment/attach` と `time log` は、まずチケットを取得し、所属プロジェクトが対象と一致しなければ終了コード 4 で拒否します。
- **送信前に必ず確認**。書き込み系は宛先 (サーバー、プロジェクト、操作内容) を stderr に表示し、`--yes` が無ければ対話で確認します。
  非対話環境 (AI から実行する場合はほぼ常に非対話) では `--yes` が無いと送信せず終了コード 4 になります。
- **`--dry-run` で送信内容を確認**。送信せず、送る予定の HTTP メソッド・パス・ボディを JSON で stdout に出します。
- **API キーはプロジェクト専用の環境変数のみ**。名前は `REDMINE_API_KEY_<接尾辞>` で、接尾辞は `.redmine.json` の `env` (無ければ `project`) を大文字にし `-` を `_` にしたもの。`redmine target` が表示します。
  汎用の `REDMINE_API_KEY` は読みません。引数では渡せず、出力にも出ません。AI はキーを読もうとしないでください。

## 2. AI がやってはいけないこと

- `.redmine.json` を編集・作成・削除しない (書き込み先を変える行為)。必要ならユーザーに依頼する。
- 環境変数 `REDMINE_API_KEY_*`、`REDMINE_CLIENT_CERT_*`、`REDMINE_CLIENT_KEY_*`、`REDMINE_CLIENT_CERT_PASSWORD_*` を表示・設定・変更しない。証明書ファイルの中身も読まない。
  これらは CLI のプロセスが自分で読む。AI が値を知る必要はない。
- `redmine setup` を実行しない。人が端末で API キーや証明書を入れるためのコマンドで、非対話の実行は終了コード 4 で断られる。
- `redmine update` (CLI 自身の更新) は、ユーザーに頼まれたときだけ実行する。`--check` で確かめるのは自由。
- `api` コマンドの `--unsafe` を、`issues` / `time` サブコマンドで代替できる操作に使わない。`api` は宛先プロジェクトを検証できません。
- `--yes` を付ける前に、`--dry-run` の出力または `issues show` で対象が正しいことを確認する。
- 説明やコメントの長文は `--description-file` / `--note-file` (`-` で stdin) で渡す。シェルのクォート事故を避けるため。

## 3. 作業前の確認

```
redmine target --json      # ネットワーク不要。config_file / url / project / api_key_env / api_key_set を返す
redmine me --json          # 接続確認。user と target.project (id, name, identifier) を返す
```

`target` が終了コード 3 なら `.redmine.json` が無いので、ユーザーに `redmine init` を依頼してください。
`api_key_set` が `false` なら、ユーザーにこのリポジトリで `redmine setup` を端末から実行してもらってください (API キーと、必要なら証明書を対話で設定する)。

## 4. コマンド早見表

| 目的 | コマンド | 種別 |
| --- | --- | --- |
| 向き先の確認 | `redmine target` | 読み取り (ローカル) |
| API キーと証明書の設定 | `redmine setup` | 人が端末で実行する (AI は使わない) |
| 接続できないときの診断 | `redmine doctor [--offline]` | 読み取り |
| 接続確認 | `redmine me` | 読み取り |
| プロジェクト一覧 | `redmine projects [--search <text>]` | 読み取り |
| カスタムフィールド一覧 | `redmine fields` | 読み取り |
| チケット一覧 | `redmine issues list [--mine] [--status open\|closed\|all\|<名前>] [--search <text>] [--limit N] [--offset N]` | 読み取り |
| チケット詳細 | `redmine issues show <id> [--no-journals]` | 読み取り |
| 添付一覧 | `redmine issues files <id>` | 読み取り |
| 関連チケットの一覧 | `redmine issues relations <id>` | 読み取り |
| 子チケットの一覧 | `redmine issues list --parent <id> [--status all]` | 読み取り |
| 添付の保存 | `redmine issues download <id> [--all\|--name <f>\|--attachment <id>] [--dir <path>]` | 読み取り |
| 作業時間一覧 | `redmine time list [--issue <id>] [--from <date>] [--to <date>] [--user me\|all]` | 読み取り |
| 生 API (GET) | `redmine api GET /path.json?query` | 読み取り |
| チケット作成 | `redmine issues create --subject <text> [...] --yes` | 書き込み |
| チケット更新 | `redmine issues update <id> [--status ...] [--note ...] --yes` | 書き込み |
| コメント | `redmine issues comment <id> "<text>" --yes` | 書き込み |
| 親の設定・解除 | `redmine issues update <id> --parent <親 id\|none> --yes` | 書き込み |
| 関連を付ける | `redmine issues relate <id> <相手> [--type blocks] [--delay N] --yes` | 書き込み (両方とも対象プロジェクト内) |
| 関連を外す | `redmine issues unrelate <id> <相手> [--type blocks] --yes` | 書き込み (両方とも対象プロジェクト内) |
| 添付 | `redmine issues attach <id> <file...> [--note ...] --yes` | 書き込み |
| 作業時間の記録 | `redmine time log <id> --hours <h> [--comment ...] --yes` | 書き込み |
| 生 API (書き込み) | `redmine api POST\|PUT\|DELETE /path.json --data '<json>' --unsafe --yes` | 書き込み (検証なし) |

各コマンドの全オプションと出力形式は `redmine <command> <sub> --help` に書いてあります。

## 5. グローバルオプション

| オプション | 意味 |
| --- | --- |
| `--json` | 結果を JSON で stdout に出す。stdout には JSON 以外を出さない (進捗・確認・エラーは stderr) |
| `--yes` / `-y` | 書き込み前の確認を省略。非対話環境では必須 |
| `--dry-run` | 送信せず、送信予定を `{ "dry_run": true, "target": {...}, "request": { "method", "path", "body" } }` で出す |
| `--verbose` / `-v` | HTTP の往復 (メソッド、URL、ステータス) を stderr に出す。キーは出さない |

AI からは常に `--json` を付けることを推奨します。人向けのテーブルはパースしないでください。

## 6. 書き込みの標準手順

1. `redmine target --json` で向き先を確認する。
2. 更新なら `redmine issues show <id> --json` で対象を確認する (`in_target` が `true` であること)。
3. `--dry-run` で送信内容を確認する。名前指定 (`--status "進行中"` など) が id に解決されていることも見える。
4. 同じコマンドを `--yes --json` で実行する。
5. 終了コードを確認する。0 以外なら stderr のエラーとヒントを読む。

例:

```
redmine issues update 123 --status "進行中" --done 30 --note "着手しました" --dry-run
redmine issues update 123 --status "進行中" --done 30 --note "着手しました" --yes --json
```

## 7. 値の指定ルール

- `--tracker` `--status` `--priority` `--category` `--version` `--activity`: 名前でも id でもよい。名前は大文字小文字を区別せず、一意な部分一致も可。解決できないときは終了コード 2 と、選べる候補の一覧が stderr に出る。
- `--assignee`: `me` / ユーザー id / メンバー名 / `none` (未割当)。
- `--parent`: チケット id。対象プロジェクト内のチケットに限る。`update` では `none` で親から外す。
- `relate` / `unrelate` の `--type`: `<id>` から見た向きで書く。`relates` (既定)、`duplicates` / `duplicated`、`blocks` (`<id>` が終わるまで相手を終えられない) / `blocked`、
  `precedes` / `follows` (`--delay` で間の日数)、`copied_to` / `copied_from`。`blocked-by` のように `-` で書いてもよい。
  関連は両方のチケットの履歴に残るので、両方とも対象プロジェクト内でなければ終了コード 4。
- `--field "名前=値"`: カスタムフィールド。`create` / `update` / `list` で使える。複数回指定可で、同じ名前を繰り返すと複数選択になる。`"名前="` でクリア。
  名前と選択肢は `redmine fields --json` で先に確認する。真偽は `yes` / `no`、リストは選択肢の表示名、ユーザー型は `me` / id / メンバー名、日付は `YYYY-MM-DD`。
  `show --json` では `issue.custom_fields[]` に `{ id, name, value }` で入る (複数選択は配列)。
  `fields --json` の `detailed` が `false` のときは、型と選択肢の定義が取れていない (管理者権限が無い環境)。
  その場合 CLI は値を変換・検証しないので、リストは表記を正確に、真偽は `1` / `0`、ユーザーとバージョンは数値 id で書く。
  `observed_values` は既存チケットに実際に入っていた値で、表記を確かめる手がかりとしてだけ使う。定義ではないので、そこに無い値が誤りとは限らない。
- 日付は `YYYY-MM-DD`。`--estimated` は時間 (小数可)。`--done` は 0〜100 の整数。
- チケット id は `123` でも `#123` でもよい。

## 8. JSON 出力の形 (要点)

- `issues list --json`: Redmine の応答そのまま。`{ "issues": [...], "total_count", "offset", "limit" }`。続きは `--offset` で取る。
- `issues show --json`: `{ "issue": {...}, "in_target": bool }`。`issue.journals[].details[]` は `{ property, name, old_value, new_value }` で、値は id のまま。
- `issues create --json`: `{ "issue": { "id", "subject", ... } }`。
- `issues update/comment/attach --json`: 更新後の `{ "issue": {...} }`。
- `issues download --json`: `{ "issue_id", "downloaded": [ { "id", "filename", "path", "filesize" } ] }`。
- `time log --json`: `{ "time_entry": {...} }`。
- `issues relations --json`: `{ "issue_id", "relations": [ { "id", "relation_type", "label", "other_issue_id", "delay", "other_issue": { "id", "subject", "tracker", "status", "project" }|null } ] }`。
  `relation_type` は `issue_id` から見た向き (`issues show` の `relations` は Redmine が保存した向きのままなので、こちらを使う)。
- `issues relate --json`: Redmine の応答 `{ "relation": { "id", "issue_id", "issue_to_id", "relation_type", "delay" } }`。
- `issues unrelate --json`: `{ "deleted": { "id", "relation_type", "label", "issue_id", "other_issue_id" } }`。
- `api`: サーバーの応答そのまま。ボディが無い成功は `{ "ok": true }`。
- `--dry-run`: `{ "dry_run": true, "target": { "url", "project": { "id", "name", "identifier" } }, "request": { "method", "path", "body", "attachments"? } }`。

## 9. 終了コードとエラー

| コード | 意味 | AI の対処 |
| --- | --- | --- |
| 0 | 成功 | |
| 1 | 接続失敗、ファイル IO、上書き拒否 | stderr のヒント (プロキシ、証明書、`--force`) を読む。接続系はユーザーに報告 |
| 2 | 引数誤り、名前解決失敗、変更なし | stderr の候補一覧を見て指定を直す |
| 3 | `.redmine.json` が無い・不正、`REDMINE_API_KEY_<識別子>` 未設定、識別子が不在 | ユーザーに設定を依頼する (キーや証明書なら `redmine setup`)。AI は直さない |
| 4 | 安全装置で拒否 (対象外プロジェクト、`--yes` なし、`--unsafe` なし、対話で中止) | 対象を確認し直す。対象外なら別リポジトリでの作業をユーザーに提案 |
| 5 | サーバーエラー (401 認証、403 権限、404 不在、422 入力拒否、413 サイズ超過) | 422 は stderr の `- ...` 行に理由が出る。401/403 はユーザーに報告 |

エラーは stderr に `エラー: ...` と `ヒント: ...` の 2 行で出ます。stdout には出ません。

## 10. 典型シナリオ

自分の未完了チケットを把握する:

```
redmine issues list --mine --json
```

チケットの内容と経緯を読む:

```
redmine issues show 123 --json
```

作業を始めたことを記録する:

```
redmine issues update 123 --status "進行中" --assignee me --note "調査を開始" --yes --json
```

作業結果を報告し、ファイルを添付する:

```
redmine issues comment 123 --file ./report.md --yes --json
redmine issues attach 123 ./result.png ./log.txt --note "実行結果" --yes --json
redmine time log 123 --hours 1.5 --comment "調査と修正" --yes --json
```

チケットの添付を取得して調べる:

```
redmine issues files 123 --json
redmine issues download 123 --all --dir ./tmp/issue-123 --json
```

新しいチケットを起票する (本文はファイルで):

```
redmine issues create --subject "ログイン画面の崩れ" --tracker Bug --priority High --assignee me \
  --description-file ./draft.md --attach ./screenshot.png --dry-run
redmine issues create --subject "ログイン画面の崩れ" --tracker Bug --priority High --assignee me \
  --description-file ./draft.md --attach ./screenshot.png --yes --json
```

サブコマンドに無い情報を読む:

```
redmine api GET /projects/my-project/versions.json
redmine api GET /issues/123.json?include=watchers
```

## 11. 環境 (ユーザーが設定するもの)

| 変数 | 用途 |
| --- | --- |
| `REDMINE_API_KEY_<接尾辞>` | 対象プロジェクト用の API アクセスキー。必須。接尾辞と名前は `redmine target` で確認 |
| `REDMINE_CLIENT_CERT_<接尾辞>` | クライアント証明書のパス (mTLS が必要な環境のみ)。`.pfx` / `.p12` か PEM |
| `REDMINE_CLIENT_KEY_<接尾辞>` | PEM の秘密鍵のパス。証明書ファイルに鍵が含まれていれば不要 |
| `REDMINE_CLIENT_CERT_PASSWORD_<接尾辞>` | PFX や暗号化鍵のパスワード (任意) |
| `HTTPS_PROXY` / `HTTP_PROXY` / `NO_PROXY` | 社内プロキシ。`http://user:pass@proxy.example.co.jp:8080` の形式。https の宛先は `HTTPS_PROXY` (無ければ `HTTP_PROXY`)。どれも設定しなければ Windows のプロキシ設定を使う |
| `REDMINE_EXTRA_CA_CERTS` | プロキシが TLS を復号する環境で、社内ルート CA の PEM ファイル。Windows の証明書ストアに入っていれば不要 |

接続に失敗したら `redmine me --verbose` の stderr をユーザーに見せてください。
