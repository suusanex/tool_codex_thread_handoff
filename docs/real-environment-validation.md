# 実環境検証の申し送り

この文書は、自動テストでは確認できない実環境検証の手順と、秘密情報を残さない結果の記録先です。現時点では未実施です。実 API 呼び出しや、長さの違う thread の圧縮品質が合格した記録ではありません。

以前に、実 thread 1 件について App Server の metadata と `thread/items/list` の pagination が応答することだけを確認しました。その確認は要約を生成しておらず、この文書の検証 1 も検証 2 も満たしません。

## 未実施の理由

個人の Codex thread 本文を OpenAI API に送り、その本文や handoff の文章をリポジトリへ残すことは、この実装作業の範囲外です。目的レビューで求められているのは実測値そのものではなく、未実施の検証を parent へ戻せる申し送りです。

## 担当と実施時点

担当はリポジトリ所有者（suusanex）です。要約品質を受け入れ可能と判断する前、および GitHub issue #1 を閉じる前に実施します。

## 共通の環境

- このリポジトリの作業ツリー
- .NET SDK 11 以降と .NET 10 ランタイム
- 環境変数 `CODEX_THREAD_HANDOFF_OPENAI_API_KEY`。値、リクエスト本文、応答本文は記録しない
- 検証する thread を保存しているのと同じマシン、同じ OS ユーザー。別マシンの thread は使わない
- Codex は PATH 上の直接実行できる `codex` / `codex.exe`、または npm 導入の `node_modules/@openai/codex/bin/codex.js`。後者だけ Node.js が必要

## 検証 1: 実 API で handoff を生成する

### 対象要件

実在する Codex thread の deeplink から、OpenAI Structured Outputs が作業継続に使える handoff JSON を生成する。

### 操作

1. 専用環境変数だけを設定する。専用変数が無いときは API を呼ばずに失敗することを、失敗メッセージだけで一度確認する。thread 本文は出さない。
2. 実 thread の deeplink を指定して生成する。

```powershell
dotnet run --file codex-thread-handoff.cs -- "codex://threads/<thread-id>" -o $env:TEMP\handoff-check.json
```

3. 生成された JSON をその場で読む。確認後、出力ファイルは削除する。thread 本文、handoff の文章、API key、diff 本文は書かない。現在状態、不確定事項、Git との整合は、検証 2 の「主要成果の評価」と同じ操作で判定し、pass / fail / n/a だけを残す。

### 期待結果と合否基準

合格は次をすべて満たすこと。

- exit code が 0
- `schemaVersion` が `"1"`
- `source.threadId` が指定した ID と一致する
- `source.reread` に Codex GUI、同一 Codex 保存環境、ツールの所在、`dump` コマンドがある
- `handoff.purpose` が空でない
- `handoff.decisions` と `handoff.remainingWork` が、継続に必要な決定と残件を欠かしていない
- Git リポジトリなら `repository` はオブジェクト、そうでなければ null と warning
- ファイルに API key とモデル名が無い
- 「主要成果の評価」の currentState、openQuestions、Git 整合が、それぞれの合格条件を満たす

一つでも欠ければ不合格。

## 検証 2: 長さの違う thread で要約入力の上限と品質を見る

### 対象要件

長さの違う実在 thread で、1 request の入力 token 量と要約品質を測る。品質には、現在状態、重要な不確定事項、取得できた Git 状態との整合を含む。多段要約と恒久的な token 計測機能は対象外。

### 操作

履歴の長さが明らかに違う thread を 2 件以上選ぶ。1 件は短い thread、もう 1 件は user message または agent message が要約上限を超える長い thread とする。検証 1 と同じコマンドで、thread ごとに別の一時ファイルへ生成する。プロンプト本文は保存しない。

各生成の直後に、その request の入力 token 数を次の手順で読む。

1. 生成を実行した API key の project で、[OpenAI の Logs](https://platform.openai.com/logs) を開く。
2. 実行時刻に最も近く、model がコマンドの model（既定は `gpt-6-luna`）と一致する Responses の 1 件を選ぶ。
3. そのログの usage に出る `input_tokens` の整数だけを結果表へ書く。request 本文、response 本文、key はコピーしない。
4. 一致するログが無い、または `input_tokens` が整数で読めない場合は、その thread の token 確認を不合格にする。計測用のコードは追加しない。

続けて、同じ一時ファイルと `dump` の stdout をその場で読み、下の「主要成果の評価」を行う。`dump` は次のコマンドで、stdout は保存も転記もしない。

```powershell
dotnet run --file codex-thread-handoff.cs -- dump "codex://threads/<thread-id>"
```

`dump` では、要約から落ちた古い turn の user / agent message と、長い message の末尾が戻るかを見る。command output と diff 本文が含まれないことも見る。

### 主要成果の評価

文章そのものは結果表へ書かない。各項目は pass、fail、または n/a だけを残す。

- currentState: `handoff.currentState` を、`dump` の user / agent message が示す実施済み内容と比べる。空でなく、継続に必要な実施済み内容と現在状態が揃っていれば pass。会話と食い違う、または空なら fail。
- openQuestions: `dump` 上で作業継続に重要な未決事項や risk があるとき、それが `handoff.openQuestions` に含まれていれば pass。重要なものが無ければ空配列で pass。見落としていれば fail。件数だけを備考に書いてよい。
- Git 整合: `repository` が null のときは n/a とし、不合格にしない。オブジェクトのときは `dirty`、`statusShort`、`diffStat` をその場で見る。`dirty` が true、または status が空でないのに、会話上は完了している変更が未コミットであることを currentState が述べていなければ fail。作業ツリーがきれいであること、または status に path が無いことだけを根拠に、実装がリポジトリに無いと currentState が断定している場合も fail。未コミットではない、という範囲を超えて欠落を断定していなければ pass。status と diff stat の本文は記録しない。

### 期待結果と合否基準

合格は次をすべて満たすこと。

- 短い thread と長い thread の生成がどちらも exit code 0 で、1 request に収まっている。context length の超過で失敗した場合は不合格
- 両方の `input_tokens` が整数で記録されている
- 長い thread では `coverage.truncated` が true、または `includedTurnCount` が `turnCount` より小さい、または `coverage.notes` に文字数制限の記載がある
- メッセージが上限未満の短い thread では `coverage.truncated` が false
- `dump` では、要約から落ちた古い turn の user / agent message と、長い message の末尾が戻り、command output と diff 本文は含まれない
- `purpose`、`decisions`、`remainingWork` が元 thread の継続に足りる
- 各 thread の currentState、openQuestions、Git 整合が「主要成果の評価」の合格条件を満たす

一つでも欠ければ不合格。多段要約が必要かは、この測定で 1 request に収まらないか、品質が不合格になったあとで parent が判断する。この手順の中では多段要約を実装しない。

記録してよいのは pass / fail / n/a、`coverage` のフラグと件数、thread id、`input_tokens` の整数、openQuestions の件数だけである。

## 結果の記録先

結果はこのファイルの「結果」へ追記する。秘密情報、transcript、handoff の文章、API key、status、diff 本文は残さない。生成した handoff JSON はコミットしない。token 数はログから写した整数だけを残す。

## 結果

未実施。

| 検証 | 日付 | thread id | 結果 | coverage の事実 | input_tokens | currentState | openQuestions | Git 整合 | 備考 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 実 API |  |  | 未実施 |  |  |  |  |  |  |
| 2 短い thread |  |  | 未実施 |  |  |  |  |  |  |
| 2 長い thread |  |  | 未実施 |  |  |  |  |  |  |

## 失敗時に parent の修正へ戻す手順

1. 不合格の検証番号、thread id、`coverage` のフラグと件数、`input_tokens` の整数か未確認、currentState / openQuestions / Git 整合の pass / fail / n/a、期待と違った点だけを implementation parent に戻す。本文、key、status、diff は戻さない。
2. 要約入力から必要な user / agent message が落ちている場合は `TranscriptFilter` の上限か選択規則を直す。currentState が Git の食い違いを落とす、または openQuestions が重要な未決事項を落とす場合は `PromptBuilder` の指示を直す。API の JSON の形が違う場合は `OpenAiHandoffModel` と `HandoffBodyParser` を直す。context length で 1 request に収まらない場合も、まず `TranscriptFilter` の上限を見直し、多段要約は測定で必要だと分かってから別途判断する。
3. Logs から `input_tokens` を読めなかった場合だけ、parent はコミットしない一時変更で Responses 応答の usage にある入力 token 数を標準エラーへ整数だけ出し、同じ確認をやり直す。一時変更は確認後に捨て、計測機能としては残さない。
4. `dotnet run --file codex-thread-handoff.tests.cs` を実行し、成功を確認する。
5. 不合格になった検証だけを同じ手順で再実行し、この表を更新する。
