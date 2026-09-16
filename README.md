# MeetingRecorder

Zoomに限らず、通話・会議アプリの音声をローカルで録音するWindowsデスクトップアプリ(.NET 8 / WinForms)。

仮想マイク・仮想スピーカードライバは使わず、WASAPIの標準機能だけで実現している。

- マイク入力(`WasapiCapture`)
- スピーカー出力のループバック(`WasapiLoopbackCapture`、相手の声など)

この2つを同時にキャプチャし、1本のWAVファイルにミックスして保存する。

## 構成

- `MeetingRecorder/` — 本体(WinFormsアプリ)
  - `AudioRecorder.cs` — 録音のコアロジック(WASAPIキャプチャ、ミキシング、WAV書き出し)
  - `MainForm.cs` — UI(デバイス選択、保存先、開始/停止、録音後にファイル/フォルダを開くボタン)
  - `Program.cs` — エントリポイント
- `SmokeTest/` — 動作確認・トラブルシューティング用のコンソール診断ツール

## ビルド

```
cd MeetingRecorder
dotnet build -c Release
```

`MeetingRecorder/bin/Release/net8.0-windows/MeetingRecorder.exe` が生成される。

## 既知の制限

企業PCなど、マイク入力を制限する環境では、マイク側の録音が `COMException 0x8007007A` (システムコールに渡されるデータ領域が小さすぎます)で失敗する場合がある。スピーカー側のループバック録音には影響しない。

このエラーが出る場合は以下を試すこと。

1. 設定 > システム > サウンド > 入力 > 対象マイクの「オーディオ強化」を「オフ」にする
2. 上記変更後、PCを再起動する(ドライバレベルの設定変更が反映されるまで再起動が必要な場合がある)
3. それでも解決しない場合は、Windowsのマイクプライバシー設定(設定 > プライバシーとセキュリティ > マイク)や、CrowdStrikeなど導入されているエンドポイントセキュリティ製品のマイクアクセス許可をIT部門に確認する

## OneDriveでの自動文字起こしについて

WAVファイルをOneDriveに保存するだけでは自動的に文字起こしはされない。「OneDrive/SharePointの動画プレーヤーが自動でトランスクリプトを作る」機能は、Streamが「動画」と認識するコンテナ形式(.mp4など)が対象で、音声単体の.wav/.mp3はその対象外。

WAVのまま文字起こししたい場合は、Word on the webの「トランスクリプト」機能(ディクテーション → トランスクリプト)で手動アップロードする方法がある。.wav / .mp3 / .mp4 / .m4a に対応しているが、月300分までという制限がある。

完全に自動化したい場合は、Power Automateなどでファイル追加をトリガーにして外部の文字起こしサービス(Azure AI Speech、Sonixなど)に投げるフローを組む必要がある。
