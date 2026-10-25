# tool_codex_thread_handoff

Codex GUI からコピーした thread deeplink を、別のコーディングエージェントへ渡す handoff JSON に圧縮するツールです。要約は OpenAI API で作ります。Codex モデルの新しい推論ターンは使いません。

handoff JSON は元履歴の損失のある圧縮です。詳細が要るときは、artifact に書いてある deeplink を Codex GUI で開くか、`dump` で filtered transcript を取り直します。`dump` の実行場所と、同じ Codex 保存環境が必要なことは artifact の `source.reread` に書いてあります。

## 前提

- .NET SDK 11 以降。エントリは `#:include` を使うため、.NET 10 SDK だけでは `dotnet run --file` できません。ターゲットフレームワークは net10.0 なので、.NET 10 ランタイムも必要です。
- Codex CLI は次のどちらかです。
  - PATH 上の、直接実行できる `codex.exe` または `codex`。Windows の `codex.cmd` / `codex.ps1` / `codex.bat` は起動しません。
  - PATH 上のディレクトリに `node_modules/@openai/codex/bin/codex.js` がある npm 導入。この形だけ Node.js が必要です。ツールは `node` でそのファイルを `app-server` として起動します。
- 生成だけ環境変数 `CODEX_THREAD_HANDOFF_OPENAI_API_KEY`。`dump` に API key は不要です。

PATH は前から順に見ます。最初に見つかった形を使い、後ろのディレクトリにある実行ファイルを、前にある `codex.js` より優先しません。

`OPENAI_API_KEY` は読みません。未設定の専用環境変数では、API を呼ばずに失敗します。key はリポジトリにも handoff JSON にも書きません。

## 生成

```powershell
$env:CODEX_THREAD_HANDOFF_OPENAI_API_KEY = "<your key>"
dotnet run --file codex-thread-handoff.cs -- "codex://threads/<thread-id>"
```

Markdown link の target に同じ deeplink がある場合も受け付けます。

```powershell
dotnet run --file codex-thread-handoff.cs -- "[title](codex://threads/<thread-id>)" -o .\handoff.json --model gpt-6-luna
```

既定の出力はカレントディレクトリの `handoff-<thread-id>.json` です。同じパスにファイルがある場合は上書きします。既定モデルは strict JSON Schema に対応する `gpt-6-luna` です。Git が使えない thread でも handoff は作り、`repository` は null にします。

OpenAI へ送るのは、user message の text、各 turn の最終 agent message、取得できた場合の branch / HEAD / dirty / `git status --short` / 上限付き `git diff --stat` です。tool output、command output、diff 本文は既定で送りません。1 メッセージは 8,000 文字、transcript 全体は 120,000 文字までです。cwd が無い、path が無い、Git repository でない場合でも、thread の内容だけで handoff を作ります。

## 再取得

```powershell
dotnet run --file codex-thread-handoff.cs -- dump "codex://threads/<thread-id>"
dotnet run --file codex-thread-handoff.cs -- dump .\handoff-<thread-id>.json
```

別の作業ディレクトリから実行するときは、artifact の `source.reread.dumpFromDeeplink` にある絶対パスを使います。`dump` は、そのコマンドを実行する OS ユーザーの Codex 保存先を読みます。`CODEX_HOME` が設定されていればそのディレクトリ、無ければそのユーザーの既定ディレクトリです。別のマシンや別ユーザーの保存先は読めません。`repository.cwd` は対象リポジトリであり、ツールの場所でも thread の保存先でもありません。

`dump` は要約を再生成しません。要約入力で省略した古い turn と、長い user / agent message の全文を stdout の filtered transcript に戻します。tool output、command output、diff 本文は戻しません。API key は不要です。

## テスト

```powershell
dotnet run --file codex-thread-handoff.tests.cs
```

実 API と実 thread の要約品質は、自動テストでは確認しません。未実施の手順と記録先は [docs/real-environment-validation.md](docs/real-environment-validation.md) です。
