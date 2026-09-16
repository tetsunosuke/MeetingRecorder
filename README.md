# MeetingRecorder

Zoomに限らず、通話・会議アプリの音声をローカルで録音し、そのままローカルで文字起こしまで行うWindowsデスクトップアプリ(.NET 8 / WinForms)。

仮想マイク・仮想スピーカードライバは使わず、WASAPIの標準機能だけで実現している。文字起こしもクラウドAPIは使わず、Whisperのモデルをローカルで動かすため追加課金は発生しない。

- マイク入力(`WasapiCapture`)
- スピーカー出力のループバック(`WasapiLoopbackCapture`、相手の声など)

この2つを同時にキャプチャし、1本のWAVファイルにミックスして保存する。録音停止後は、Whisper(small モデル)による文字起こしをローカルで自動実行し、同じフォルダに `.txt` として保存する。

## 主な機能

- マイク + スピーカー出力の同時録音(WAV)
- マイク/スピーカーそれぞれのレベルメーター表示(録音前のデバイス選択時から常時モニタリング可能)
- 録音停止後、Whisper(small, 約500MB・初回のみダウンロード)によるローカル文字起こしを自動実行
- 保存先フォルダ・自動文字起こしのON/OFFを「設定」タブで変更可能(設定は `%AppData%\MeetingRecorder\settings.json` に保存)
- 録音ファイル/フォルダをワンクリックで開くボタン

## 構成

- `MeetingRecorder/` — 本体(WinFormsアプリ)
  - `AudioRecorder.cs` — 録音のコアロジック(WASAPIキャプチャ、ミキシング、WAV書き出し)
  - `AudioLevelMonitor.cs` — デバイス選択時のレベルメーター用の軽量キャプチャ
  - `Transcriber.cs` — Whisper.netによるローカル文字起こし
  - `AppSettings.cs` — 設定の読み書き(JSON)
  - `MainForm.cs` — UI(録音タブ/設定タブ)
  - `Program.cs` — エントリポイント
- `SmokeTest/` — 動作確認・トラブルシューティング用のコンソール診断ツール

## ビルド

開発用(このPCで動かすだけ、.NET 8 Desktop Runtimeが前提):

```
cd MeetingRecorder
dotnet build -c Release
```

`MeetingRecorder/bin/Release/net8.0-windows/MeetingRecorder.exe` が生成される。

配布用(自己完結型、.NET Desktop Runtimeが入っていないPCでもそのまま動く。Releasesのzipはこちらでビルドしている):

```
cd MeetingRecorder
dotnet publish -c Release -r win-x64 --self-contained true
```

`MeetingRecorder/bin/Release/net8.0-windows/win-x64/publish/` 以下に、ランタイム同梱の実行ファイル一式が生成される(約130MB)。

## 文字起こしについて

初回の文字起こし実行時に、Whisperのsmallモデル(約500MB)をHugging Faceからダウンロードして `%LocalAppData%\MeetingRecorder\models\` に保存する(以降はこれを再利用するので再ダウンロードは発生しない)。処理は完全にローカルで完結し、外部への音声送信は行わない。

日本語を含む多言語に対応しているが、モデルサイズを小さくしている分、固有名詞や早口には精度の限界がある。

## 既知の制限

企業PCなど、マイク入力を制限する環境では、マイク側の録音が `COMException 0x8007007A` (システムコールに渡されるデータ領域が小さすぎます)で失敗する場合がある。スピーカー側のループバック録音には影響しない。

このエラーが出る場合は以下を試すこと。

1. 設定 > システム > サウンド > 入力 > 対象マイクの「オーディオ強化」を「オフ」にする
2. 上記変更後、PCを再起動する(ドライバレベルの設定変更が反映されるまで再起動が必要な場合がある)
3. それでも解決しない場合は、Windowsのマイクプライバシー設定(設定 > プライバシーとセキュリティ > マイク)や、CrowdStrikeなど導入されているエンドポイントセキュリティ製品のマイクアクセス許可をIT部門に確認する
