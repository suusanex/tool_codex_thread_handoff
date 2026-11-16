# tool_codex_thread_handoff

Codex GUI からコピーした thread deeplink を、別のコーディングエージェントへ渡す handoff JSON に圧縮するツールです。要約には OpenAI API、または配置済みの Codex Agent Facade CLI と GitHub Copilot CLI を使えます。Codex モデルの新しい推論ターンは使いません。

handoff JSON は元履歴の損失のある圧縮です。詳細が要るときは、artifact に書いてある deeplink を Codex GUI で開くか、`dump` で filtered transcript を取り直します。`dump` の実行場所と、同じ Codex 保存環境が必要なことは artifact の `source.reread` に書いてあります。

## 共通の前提

- .NET SDK 11 以降。エントリは `#:include` を使うため、.NET 10 SDK だけでは `dotnet run --file` できません。ターゲットフレームワークは net10.0 なので、.NET 10 ランタイムも必要です。
- Codex CLI は次のどちらかです。
  - PATH 上の、直接実行できる `codex.exe` または `codex`。Windows の `codex.cmd` / `codex.ps1` / `codex.bat` は起動しません。
  - PATH 上のディレクトリに `node_modules/@openai/codex/bin/codex.js` がある npm 導入。この形だけ Node.js が必要です。ツールは `node` でそのファイルを `app-server` として起動します。

PATH は前から順に見ます。最初に見つかった形を使い、後ろのディレクトリにある実行ファイルを、前にある `codex.js` より優先しません。

## バックエンド

`--backend` は `openai` と `facade` を受け付けます。省略時は従来互換の `openai` です。モデル指定は `--model` で行い、Facade では必須です。

| backend | 要約の実行先 | 必要な認証 |
| --- | --- | --- |
| `openai` | OpenAI Responses API | `CODEX_THREAD_HANDOFF_OPENAI_API_KEY` |
| `facade` | Codex Agent Facade CLI 経由の GitHub Copilot CLI | GitHub Copilot CLI の既存ログイン |

バックエンド間で暗黙にフォールバックしません。`facade` で失敗した場合は OpenAI API を呼ばず、`openai` で失敗した場合も Facade や GitHub Copilot を呼びません。

### OpenAI API

生成時に専用環境変数 `CODEX_THREAD_HANDOFF_OPENAI_API_KEY` を設定します。`OPENAI_API_KEY` は読みません。専用変数が未設定なら API を呼ばずに失敗し、key はリポジトリにも handoff JSON にも書きません。

OpenAI 経路は strict JSON Schema を使って `HandoffBodyParser.SchemaJson` に従う応答を要求します。既定モデルは strict JSON Schema に対応する `gpt-6-luna` です。

### Facade + GitHub Copilot

Facade は別リポジトリ [`suusanex/codex_agent_facade`](https://github.com/suusanex/codex_agent_facade) から取得し、常駐 MCP ホストではなく通常アプリ向けの `CodexAgentFacadeCli.exe` を publish して配置します。Facade の [CLI 仕様](https://github.com/suusanex/codex_agent_facade/blob/main/docs/cli.md) を参照してください。

Windows での publish 例です。Facade リポジトリ直下で実行し、出力先は各環境に合わせて変更します。

```powershell
dotnet publish src/CodexAgentFacadeCli.cs -c Release -r win-x64 --self-contained false -o D:\Tools\CodexAgentFacade
```

Facade のビルドには .NET 11 SDK が必要です。framework-dependent な publish 成果物を実行する環境には .NET 10 ランタイムが必要です。`CodexAgentFacadeCli.exe` と同じ publish に含まれる必要な成果物をまとめて配置してください。SDK のビルドメッセージが stdout へ混ざらないよう、このツールは `dotnet run --file` ではなく publish 済み exe を呼びます。

Facade が起動する `copilot` を PATH から実行できるよう GitHub Copilot CLI を導入し、事前にそのアカウントでログインしてください。利用するモデル ID がその CLI、GitHub Copilot 契約、組織ポリシーで利用できることも確認します。この経路に OpenAI API key と、常駐 MCP ホスト用の `CODEX_AGENT_FACADE_TOKEN` は不要です。

配置した exe のフルパスを専用環境変数へ設定するか、実行ごとに `--facade-cli` で上書きします。引数が指定されていれば環境変数より優先します。相対パスや存在しないファイルは使わず、原因を示して失敗します。PATH の `CodexAgentFacadeCli.exe` 自動探索はしません。

`auto_approve=false` と `timeout_seconds=600` は Facade の request へ明示されます。10 分経過または Ctrl+C で呼び出し側が Facade process tree を終了し、handoff を保存せず原因を示して失敗します。`auto_approve=false` は Copilot CLI 側の既存権限設定を上書きして全ツール利用を禁止する保証ではありません。handoff 生成は要約だけを行い、このツールが prompt にもツール呼び出しやファイル編集の禁止を明記しています。

## 生成

OpenAI API を使う例です。

```powershell
$env:CODEX_THREAD_HANDOFF_OPENAI_API_KEY = "<your key>"
dotnet run --file codex-thread-handoff.cs -- "codex://threads/<thread-id>"
```

Markdown link の target に同じ deeplink がある場合も受け付けます。

```powershell
dotnet run --file codex-thread-handoff.cs -- "[title](codex://threads/<thread-id>)" -o .\handoff.json --model gpt-6-luna
```

Facade + GitHub Copilot を使う例です。`--facade-cli` は一時上書きなので、環境変数を設定済みなら省略できます。

```powershell
$env:CODEX_THREAD_HANDOFF_FACADE_CLI = "D:\Tools\CodexAgentFacade\CodexAgentFacadeCli.exe"
dotnet run --file codex-thread-handoff.cs -- "codex://threads/<thread-id>" --backend facade --model <copilot-model-id>

dotnet run --file codex-thread-handoff.cs -- "codex://threads/<thread-id>" --backend facade --model <copilot-model-id> --facade-cli "D:\Other\CodexAgentFacade\CodexAgentFacadeCli.exe"
```

既定の出力はカレントディレクトリの `handoff-<thread-id>.json` です。同じパスにファイルがある場合は上書きします。Git が使えない thread でも handoff は作り、`repository` は null にします。

OpenAI へ送るのは、user message の text、各 turn の最終 agent message、取得できた場合の branch / HEAD / dirty / `git status --short` / 上限付き `git diff --stat` です。tool output、command output、diff 本文は既定で送りません。1 メッセージは 8,000 文字、transcript 全体は 120,000 文字までです。cwd が無い、path が無い、Git repository でない場合でも、thread の内容だけで handoff を作ります。

Facade 経路でも送る要約入力と上限は OpenAI 経路と同じです。OpenAI の strict JSON Schema の代わりに、Facade の prompt へ schema と JSON object のみを返す指示を入れ、`result.outputText` を `HandoffBodyParser` で検証します。Facade の起動、応答の status / output kind、JSON、必須項目のいずれかが不正なら、handoff を保存せず失敗します。どちらのバックエンドでも artifact の形式は同じです。

GitHub Copilot を使う場合、filtered transcript と Git 状態の要約入力は Copilot のサービスへ送信されます。元 thread の機微情報を確認し、Facade / Copilot の run log や一時的な診断ログの取扱いにも注意してください。

## 再取得

```powershell
dotnet run --file codex-thread-handoff.cs -- dump "codex://threads/<thread-id>"
dotnet run --file codex-thread-handoff.cs -- dump .\handoff-<thread-id>.json
```

別の作業ディレクトリから実行するときは、artifact の `source.reread.dumpFromDeeplink` を PowerShell に貼ります。引数は単一引用符で囲んであり、`$` や部分式は展開されません。`dump` は、そのコマンドを実行する OS ユーザーの Codex 保存先を読みます。`CODEX_HOME` が設定されていればそのディレクトリ、無ければそのユーザーの既定ディレクトリです。別のマシンや別ユーザーの保存先は読めません。`repository.cwd` は対象リポジトリであり、ツールの場所でも thread の保存先でもありません。

`dump` は要約を再生成しません。要約入力で省略した古い turn と、長い user / agent message の全文を stdout の filtered transcript に戻します。tool output、command output、diff 本文は戻しません。OpenAI API key、Facade、GitHub Copilot の認証は不要です。

## テスト

```powershell
dotnet run --file codex-thread-handoff.tests.cs
```

実 API、実 Facade / GitHub Copilot CLI、実 thread の要約品質は、自動テストでは確認しません。未実施の手順と記録先は [docs/real-environment-validation.md](docs/real-environment-validation.md) です。
