# tool_codex_thread_handoff

Codex GUI からコピーした thread deeplink を、別のコーディングエージェントへ渡す handoff JSON に圧縮するツールです。要約は OpenAI API で作ります。Codex モデルの新しい推論ターンは使いません。

handoff JSON は元履歴の損失のある圧縮です。詳細が要るときは、artifact に書いてある deeplink を Codex GUI で開くか、`dump` で filtered transcript を取り直します。

## 前提

- .NET 10 以降
- PATH 上の Codex CLI（`codex app-server` を起動できること）
- 環境変数 `CODEX_THREAD_HANDOFF_OPENAI_API_KEY`

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

OpenAI へ送るのは、user message の text、各 turn の最終 agent message、取得できた場合の branch / HEAD / dirty / `git status --short` / 上限付き `git diff --stat` です。tool output、command output、diff 本文は既定で送りません。cwd が無い、path が無い、Git repository でない場合でも、thread の内容だけで handoff を作ります。

## 再取得

```powershell
dotnet run --file codex-thread-handoff.cs -- dump "codex://threads/<thread-id>"
dotnet run --file codex-thread-handoff.cs -- dump .\handoff-<thread-id>.json
```

`dump` は要約を再生成しません。user message と最終 agent message を中心にした filtered transcript JSON を stdout に出します。API key は不要です。

## テスト

```powershell
dotnet run --file codex-thread-handoff.tests.cs
```
