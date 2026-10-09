# redmine-cli-dotnet

Redmine の REST API を叩く CLI (`redmine.exe`)。Node 製の [redmine-cli](https://github.com/yakumo-amamiya/redmine-cli) を .NET 10 で作り直したもので、
exe 1 つで動く (Node も .NET も入れなくてよい)。人間の手打ちと AI エージェントの両方から使う前提で、**書き込み先を間違えない**ことを最優先に作ってある。

- 書き込み先は、リポジトリ直下の `.redmine.json` (`url` と `project`) で固定。コマンド引数では変えられない
- API キーはプロジェクト専用の環境変数 `REDMINE_API_KEY_<識別子>` からだけ読む。引数では渡せず、出力にも出ない
- `.redmine.json` の `project` が書き換わると対応する環境変数が無くなり止まる (ファイルと環境変数の二重鍵)
- id 指定の更新は、チケットの所属プロジェクトをサーバーに確認してから送る
- 書き込み前に宛先を表示し、`--yes` / 対話確認 / `--dry-run` のいずれかを必ず通る
- 社内プロキシ (`HTTPS_PROXY` / `NO_PROXY`、無ければ Windows のプロキシ設定) と社内 CA (Windows の証明書ストア、または `REDMINE_EXTRA_CA_CERTS`) に対応
- クライアント証明書 (mTLS) での接続に対応。証明書はプロジェクト専用の環境変数でパスを指定

コマンド、`.redmine.json`、環境変数、終了コード、`--json` の形は Node 版と同じ。違いは [Node 版との違い](#node-版との違い) に書いた。

## 内部利用・免責事項

本プロジェクトは内部利用を目的として提供しています。利用者は自身の責任において利用してください。
本ソフトウェアは現状のまま提供され、動作、正確性、安全性、特定の目的への適合性について、いかなる保証も行いません。
本ソフトウェアの利用または利用不能により生じたデータの消失・破損、誤更新、業務の中断、その他一切の損害・不利益について、開発者および提供者は、法令で認められる範囲において一切の責任を負いません。

## 動作要件

- Windows 10 / 11 (x64)。.NET のインストールは要らない
- Redmine 4.x 以降 (REST API が有効で、ユーザーに API アクセスキーがあること)

## インストール

PowerShell (Windows PowerShell 5.1 でも PowerShell 7 でもよい) で次の 1 行を実行する。管理者権限は要らない。

```powershell
irm https://raw.githubusercontent.com/yakumo-amamiya/redmine-cli-dotnet/main/install.ps1 | iex
```

[install.ps1](install.ps1) がやること:

1. 最新の Release から `redmine-win-x64.zip` を落とし、同じ Release の `SHA256SUMS` とハッシュを照合する (合わなければ入れずに止まる)
2. `%LOCALAPPDATA%\Programs\redmine-cli\redmine.exe` に置く
3. そのフォルダをユーザーの PATH に足す (初回だけ。新しく開いたシェルから `redmine` で使える)

更新するときは `redmine update` を実行する (確かめるだけなら `redmine update --check`)。Release から新しい exe を落として
SHA256SUMS と照合し、動くことを確かめてから今の exe と置き換える。v0.2.0 以前の版には update が無いので、同じ 1 行をもう一度実行して入れ直す。
install.ps1 で版を指定するとき、外すときは次のようにする。

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/yakumo-amamiya/redmine-cli-dotnet/main/install.ps1))) -Version 0.1.0
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/yakumo-amamiya/redmine-cli-dotnet/main/install.ps1))) -Uninstall
```

スクリプトを実行できない PC では、[Releases](https://github.com/yakumo-amamiya/redmine-cli-dotnet/releases) から `redmine-win-x64.zip` を落として
`redmine.exe` を好きなフォルダに置き、そのフォルダを PATH に足す。

### Node 版から移るとき

`.redmine.json` と環境変数 (`REDMINE_API_KEY_*`、`REDMINE_CLIENT_*`) はそのまま使える。Node 版の `redmine` (npm link) が PATH の前の方にあると
そちらが動くので、Node 版の clone で `npm unlink -g redmine-cli` を実行して外す (install.ps1 は先に別の `redmine` が見つかると警告する)。

## 初期設定

### リポジトリに `.redmine.json` が既にあるとき (いちばん多い場合)

誰かが `redmine init` 済みのリポジトリなら、そのリポジトリで次を実行するだけでよい。

```powershell
redmine setup
```

次の順に聞いて、このプロジェクト専用のユーザー環境変数に書き、最後に `redmine doctor` と同じ検査で接続を確かめる。

1. API キー。Redmine の「個人設定」(`<url>/my/account`) の「APIアクセスキー」の値を貼り付ける。画面には出ない
2. クライアント証明書。要る環境だけ答える。PFX か PEM のパスと、パスワード (パスフレーズ) を聞き、その場で証明書を開けるか確かめる

書く前に内容を見せて確認する (`--dry-run` なら書かない)。値は新しく開いたシェルから使える。
端末から実行したときだけ動き、AI エージェントなど非対話の実行は断る。

手で設定するとき、`.redmine.json` を新しく作るときは、以下の手順。

### 1. API キーをプロジェクト専用の環境変数に置く

Redmine の「個人設定」→「APIアクセスキー」→「表示」で得た値を、ユーザー環境変数に設定する。
リポジトリ内の `.env` などのファイルには書かない。

変数名は `REDMINE_API_KEY_<接尾辞>`。接尾辞は `.redmine.json` の `env` (無ければ `project` の識別子) を大文字にし、`-` を `_` に置き換えたもの。

| `.redmine.json` | 環境変数名 |
| --- | --- |
| `"project": "my-project"` | `REDMINE_API_KEY_MY_PROJECT` |
| `"project": "plan", "env": "hosyu"` | `REDMINE_API_KEY_HOSYU` |

識別子が `plan` のように分かりにくいときは、`redmine init --env hosyu` のように別名を付ける。

```powershell
setx REDMINE_API_KEY_MY_PROJECT "<キー>"
```

設定後に新しいシェルを開く。CLI はこの値を表示しないし、引数でも受け取らない。
`setx` の行はキーごと PowerShell の履歴に残ることがあるので、気になるなら `redmine setup` を使う。
汎用の `REDMINE_API_KEY` は読まない。読むべき変数名は `redmine target` が表示する。

`env` を付けずに識別子から変数名を決めておくと、`.redmine.json` の `project` が書き換わったときに対応する変数が無くなって止まる (ファイルと環境変数の二重鍵)。
`env` で別名を付けるとこの保護は効かなくなるので、`.redmine.json` の差分を git で見張る。
プロジェクト専用の Redmine ユーザー (対象プロジェクトにしかメンバーシップを持たないユーザー) のキーを入れれば、サーバー側でも他プロジェクトへ書けなくなる。

### 2. 社内プロキシと社内 CA (必要な場合)

プロキシは Node 版と同じ環境変数と同じ規則で決まる。Node 版で設定した `HTTPS_PROXY` / `NO_PROXY` はそのまま効く。

```powershell
setx HTTPS_PROXY "http://proxy.example.co.jp:8080"        # 認証付きなら http://user:pass@proxy.example.co.jp:8080
setx NO_PROXY "localhost,127.0.0.1,.example.co.jp"        # プロキシを通さない宛先
```

- https の Redmine は `HTTPS_PROXY` を通る。`HTTPS_PROXY` が無ければ `HTTP_PROXY` を通る。http の Redmine は `HTTP_PROXY`。小文字の `https_proxy` なども同じ (Windows では同じ変数)
- `NO_PROXY` に書いた宛先へは直接つなぐ。カンマか空白で区切る。`*` は全部、`redmine.example.co.jp` はそのホストだけ、
  `.example.co.jp` か `*.example.co.jp` はその下の全部、`redmine.example.co.jp:8443` はそのポートだけ
- プロキシの URL の `user:pass@` はプロキシの Basic 認証に使う。記号は `%40` (`@`) のように URL エンコードする。値は表示しない
- プロキシの環境変数が 1 つも無いときは、Windows のプロキシ設定 (ブラウザと同じもの) を使う (Node 版は直接つないでいた)

どれを通っているかは `redmine doctor` の「経路」の行か、`--verbose` の最初の行に出る。
Windows の統合認証 (NTLM / Kerberos) を求めるプロキシには、サインイン中のユーザーの資格情報で応答する (実際の社内プロキシではまだ確かめていない)。

サーバーの証明書は Windows の証明書ストアで検証する。プロキシが TLS を復号 (証明書を差し替え) している環境でも、社内ルート CA が
「信頼されたルート証明機関」に入っていれば (ブラウザで警告が出ていなければ) 何もしなくてよい。ストアに無い CA を使うときは、その PEM ファイルを指定する。

```powershell
setx REDMINE_EXTRA_CA_CERTS "C:\path\to\corp-root-ca.pem"
```

Node 版の `NODE_EXTRA_CA_CERTS` も、`REDMINE_EXTRA_CA_CERTS` が無ければ読む。証明書検証を無効にするオプションは用意していない。

### 2b. クライアント証明書で接続する環境 (mTLS / CBA)

Redmine の前段が SSO ではなくクライアント証明書で機械アクセスを許している場合は、証明書ファイルのパスを
プロジェクト専用の環境変数に置く。値はファイルの中身ではなくパス。ファイルはリポジトリの外に置く。

| 変数 | 内容 |
| --- | --- |
| `REDMINE_CLIENT_CERT_<識別子>` | 証明書ファイルのパス。`.pfx` / `.p12` なら PKCS#12、それ以外は PEM |
| `REDMINE_CLIENT_KEY_<識別子>` | PEM の秘密鍵ファイルのパス。証明書ファイルに鍵も入っていれば不要 |
| `REDMINE_CLIENT_CERT_PASSWORD_<識別子>` | PFX のパスワード、または PEM 鍵のパスフレーズ。暗号化されていなければ設定しない |

パスフレーズの有無はどちらでもよい。暗号化されていれば `REDMINE_CLIENT_CERT_PASSWORD_<識別子>` を設定し、
されていなければその変数を作らない。パスフレーズが必要なのに無いときは、その変数名を案内して終了コード 1 になる。
PEM の鍵は PKCS#8 (`BEGIN PRIVATE KEY` / `BEGIN ENCRYPTED PRIVATE KEY`) か `BEGIN RSA PRIVATE KEY` / `BEGIN EC PRIVATE KEY`。
古い形式の暗号化鍵 (`Proc-Type: 4,ENCRYPTED`) は読めないので、`openssl pkcs8 -topk8` で変換するか PFX にする。

PFX (IT 部門から `.pfx` / `.p12` で渡された場合):

```powershell
setx REDMINE_CLIENT_CERT_MY_PROJECT "C:\Users\<you>\certs\redmine-client.pfx"
setx REDMINE_CLIENT_CERT_PASSWORD_MY_PROJECT "<PFX のパスワード>"
```

PEM (証明書と鍵が別ファイルの場合。鍵がパスフレーズ付きなら 3 行目も):

```powershell
setx REDMINE_CLIENT_CERT_MY_PROJECT "C:\Users\<you>\certs\redmine-client.crt"
setx REDMINE_CLIENT_KEY_MY_PROJECT "C:\Users\<you>\certs\redmine-client.key"
setx REDMINE_CLIENT_CERT_PASSWORD_MY_PROJECT "<鍵のパスフレーズ>"
```

設定後に新しいシェルを開き、`redmine target` で証明書のパスが認識されていることを確認してから `redmine me` で接続する。
プロキシ経由でも、CONNECT トンネルの先の Redmine との TLS に証明書を提示する。証明書ファイルに中間 CA の証明書も入っていれば、一緒に提示する。
Windows の証明書ストアから直接読む機能は無いので、ストアにしか無い場合は「エクスポート」で PFX に書き出す
(秘密鍵がエクスポート不可の証明書は使えない。その場合は IT 部門にファイルでの発行を頼む)。

### 3. リポジトリごとに書き込み先を固定する

Redmine と対応させたいリポジトリの直下で実行する。

```powershell
cd C:\path\to\your-repo
redmine init --url https://redmine.example.co.jp --project my-project
```

サーバーに識別子の実在を問い合わせてから `.redmine.json` を書く。

```json
{
  "url": "https://redmine.example.co.jp",
  "project": "my-project"
}
```

`--env hosyu` を付けると `"env": "hosyu"` が加わり、環境変数の接尾辞がそれになる。
このファイルはトークンを含まないのでコミットしてよい。以後、このディレクトリ配下ではここにしか書き込まれない。
別プロジェクトを扱うリポジトリには、そちらで別の `.redmine.json` を作る。

### 4. 接続確認

```powershell
redmine setup      # 自分の API キーと証明書を対話で設定し、接続まで確かめる
redmine target     # ローカルの設定を表示 (ネットワーク不要)
redmine doctor     # 設定 → 証明書の復号 → 経路 → 接続 → 認証 → プロジェクト、の順に検査して止まった段階を示す
redmine me         # サーバーに接続し、ユーザーと対象プロジェクトを表示
```

つながらないときは `redmine doctor` の最初の NG 行が原因。`--offline` を付けると、ネットワークに出ずに証明書とパスフレーズだけを検査する。
「経路」の行に、直接つなぐか、どのプロキシを通るか (環境変数か Windows の設定か) が出る。

## 使い方

```
redmine --help                       # 全体 (安全モデル、環境変数、終了コード)
redmine issues --help                # チケット系の一覧
redmine issues create --help         # 各コマンドの全オプション、出力の形、例 (redmine help issues create でも同じ)
redmine guide                        # AI エージェント向けの手順書 (Markdown)
```

よく使うもの:

```powershell
redmine issues list --mine
redmine issues show 123
redmine issues create --subject "ログイン画面の崩れ" --tracker Bug --assignee me --dry-run
redmine issues create --subject "ログイン画面の崩れ" --tracker Bug --assignee me --yes
redmine issues update 123 --status "進行中" --done 30 --note "着手" --yes
redmine issues comment 123 "確認しました" --yes
redmine fields                                             # カスタムフィールドの名前・型・選択肢
redmine issues update 123 --field "顧客=ACME" --field "対象OS=Windows" --field "対象OS=Linux" --yes
redmine issues attach 123 .\screenshot.png --note "再現時の画面" --yes
redmine issues files 123
redmine issues download 123 --all --dir .\tmp\123
redmine time log 123 --hours 1.5 --comment "調査" --yes
redmine time list --from 2026-09-01 --to 2026-09-30
redmine api GET "/issues.json?assigned_to_id=me&limit=5"
```

グローバルオプション:

| オプション | 意味 |
| --- | --- |
| `--json` | 結果を JSON で stdout に出す。stdout には JSON 以外を出さない |
| `-y, --yes` | 書き込み前の確認を省略 (非対話環境では必須) |
| `--dry-run` | 送信せず、送信予定の内容を JSON で出す |
| `-v, --verbose` | HTTP の往復を stderr に出す (キーは出さない) |

出力は UTF-8。PowerShell で `redmine ... | Out-File` のように受けて文字化けするときは、先に
`[Console]::OutputEncoding = [Text.Encoding]::UTF8` を実行する。

終了コード:

| コード | 意味 |
| --- | --- |
| 0 | 成功 |
| 1 | その他の失敗 (接続、ファイル IO、上書き拒否) |
| 2 | 引数誤り、名前解決失敗 |
| 3 | 設定不足 (`.redmine.json` / `REDMINE_API_KEY_<識別子>`)、識別子が不在 |
| 4 | 安全装置で拒否 (対象外プロジェクト、`--yes` なし、`--unsafe` なし、対話で中止) |
| 5 | サーバーがエラーを返した (401 / 403 / 404 / 422 / 413 など) |

## 安全装置の詳細

| 層 | 内容 |
| --- | --- |
| 宛先の固定 | `.redmine.json` の `url` と `project` のみ。`--project` のような書き込み先指定は存在しない |
| 所属の検証 | `issues update/comment/attach`、`time log`、`--parent` は対象チケットの所属プロジェクトを取得して照合する |
| 送信前の確認 | 宛先と内容を stderr に表示。`--yes` / 対話 y/N / `--dry-run` のいずれかを必ず通る |
| 非対話の扱い | stdin が端末でなく `--yes` も無ければ送信せず終了コード 4 |
| 生 API | `api` の GET 以外は `--unsafe` が必須。プロジェクトの検証はできないと明示している |
| トークン | プロジェクト専用の環境変数 `REDMINE_API_KEY_<識別子>` からのみ。汎用名は読まない。ログ・エラー・`--verbose` にも出さない。リダイレクトで別のホストに飛ばされたら、そこへは送らない |

CLI 側でできるのはここまでで、`.redmine.json` と環境変数の両方を書き換えられれば迂回できる。
より強くしたい場合は、対象プロジェクトにしかメンバーシップを持たない専用 Redmine ユーザーの API キーを使う (サーバー側で制限)。

## AI エージェントに使わせる

対象リポジトリの `CLAUDE.md` や `AGENTS.md` に次を書いておく。

```markdown
## Redmine

チケット操作は `redmine` CLI を使う。最初に `redmine guide` を読み、作業前に `redmine target --json` で向き先を確認する。
書き込みは `--dry-run` で内容を確認してから `--yes --json` で実行する。`.redmine.json` と環境変数 `REDMINE_API_KEY_*` は編集しない。
```

`redmine guide` に、安全モデル、コマンド早見表、JSON の形、終了コードごとの対処、典型シナリオをまとめてある。

## 添付ファイルについて

- アップロードは `POST /uploads.json` でトークンを得てからチケットに紐付ける 2 段階。CLI が 1 コマンドにまとめている
- サイズ上限は Redmine の設定 (既定 5 MB) とプロキシに依存する。超えると終了コード 5
- ダウンロードは必ずチケット id 経由。添付 id は全プロジェクト共通の通し番号なので、単体では所属を検証できない
- 同名ファイルがあれば上書きしない (`--force` で上書き)。途中で切れたときに半端なファイルを残さないよう、`.part` に書いてから置き換える

## Node 版との違い

| | Node 版 (redmine-cli) | この版 |
| --- | --- | --- |
| 入れ方 | clone して `npm install` と `npm link` | `irm ... \| iex` (exe 1 つ。Node も .NET も要らない) |
| 更新 | `git pull` | `redmine update` (Release から落として照合し、exe を置き換える) |
| 自分のキーと証明書の設定 | `setx` を手で | `redmine setup` で対話 (キーとパスワードは画面に出さず、証明書はその場で開けるか確かめる) |
| 社内 CA | `NODE_EXTRA_CA_CERTS` | Windows の証明書ストア。ストアに無い CA は `REDMINE_EXTRA_CA_CERTS` (`NODE_EXTRA_CA_CERTS` も読む) |
| プロキシ | 環境変数 (`HTTPS_PROXY`、無ければ `HTTP_PROXY`、`NO_PROXY`) だけ。NTLM / Kerberos は px や cntlm が必要 | 環境変数は同じ規則。どれも無ければ Windows の設定を使う。NTLM / Kerberos はサインイン中のユーザーで応答 |
| リダイレクト | 飛ばされた先にも API キーのヘッダーが付く | 別のホストに飛ばされたらキーを送らない |
| doctor | | 「経路」(直接かどのプロキシか) の行がある。証明書の内容は PFX でも出す |
| 引数 | `--limit abc` などは既定値として扱う | 数でない値は引数誤り (終了コード 2) |
| `api GET` のボディ | 送ろうとして失敗する | 引数誤りとして止める |
| ヘルプ | | `redmine help <command>` でも出る |

## 開発

.NET 10 SDK が要る。

```powershell
dotnet build
dotnet test     # モックの Redmine (Kestrel) を立て、redmine.exe を子プロセスで実行する。mTLS のテストも含む
dotnet run --project src/RedmineCli -- target
```

構成:

```
src/RedmineCli/
  Program.cs            エントリ (UTF-8 の出力)
  Cli.cs                コマンドの組み立て、グローバルオプション、ヘルプの補足、終了コード
  Config.cs             .redmine.json の探索と検証、REDMINE_API_KEY_<識別子> などの変数名と読み取り
  RedmineClient.cs      HTTP。プロキシ、リダイレクト、エラーの日本語化、アップロード、ダウンロード
  Tls.cs                クライアント証明書 (PFX / PEM) の読み込み、REDMINE_EXTRA_CA_CERTS
  Updater.cs            redmine update (最新版の確認、ダウンロードと照合、exe の入れ替え)
  Proxy.cs              プロキシの選び方 (HTTPS_PROXY / HTTP_PROXY / NO_PROXY を Node 版と同じ規則で。無ければ Windows の設定)
  Context.cs            安全装置。対象プロジェクトの解決、所属検証、送信前確認
  Lookups.cs            名前 → id の解決 (トラッカー、ステータス、担当者、カスタムフィールドなど)
  Output.cs, Json.cs    テーブル整形、JSON の出力、stdout / stderr の使い分け
  guide.md              AI 向け手順書 (redmine guide。exe に埋め込む)
  Commands/*.cs         各コマンド (SetupCommand.cs は setup の対話)
tests/RedmineCli.Tests/ テスト (Fixtures/tls はテスト専用の自己署名証明書)
tests/install-check.ps1 install.ps1 の確認 (CI 用。ユーザーの PATH を書き換えるので手元では動かさない)
install.ps1             インストーラー
```

配布は Native AOT の exe。手元で `dotnet publish src/RedmineCli -c Release -r win-x64` するには Visual Studio の
「C++ によるデスクトップ開発」が要る (無くても build と test はできる。CI では毎回 AOT の exe を作り、同じテストをそれにも流す)。

リリースは `v*` のタグを push する (`git tag v0.2.0 && git push origin v0.2.0`)。GitHub Actions が AOT の exe を作り、テストを通してから
`redmine-win-x64.zip` と `SHA256SUMS` を Release に置く。版はタグから決まる。

## やらないこと

- 複数サーバーの切り替え、サブプロジェクトの扱い (1 サーバー、1 プロジェクト前提)
- Wiki、バージョン・カテゴリ・メンバーの管理、添付の削除 (必要なら `api` で)
- 証明書検証の無効化
