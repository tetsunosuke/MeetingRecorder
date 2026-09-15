# ZoomRecorder

Zoomなど通話アプリの音声をローカルで録音するWindowsデスクトップアプリ(.NET 8 / WinForms)。

仮想マイク・仮想スピーカードライバは使わず、WASAPIの標準機能だけで実現している。

- マイク入力(`WasapiCapture`)
- スピーカー出力のループバック(`WasapiLoopbackCapture`、Zoomの相手の声など)

この2つを同時にキャプチャし、1本のWAVファイルにミックスして保存する。

## 構成

- `ZoomRecorder/` — 本体(WinFormsアプリ)
  - `AudioRecorder.cs` — 録音のコアロジック(WASAPIキャプチャ、ミキシング、WAV書き出し)
  - `MainForm.cs` — UI(デバイス選択、保存先、開始/停止)
  - `Program.cs` — エントリポイント
- `SmokeTest/` — 動作確認・トラブルシューティング用のコンソール診断ツール

## ビルド

```
cd ZoomRecorder
dotnet build -c Release
```

`ZoomRecorder/bin/Release/net8.0-windows/ZoomRecorder.exe` が生成される。

## 既知の制限

企業PCなど、CrowdStrikeなどのエンドポイントセキュリティ製品がマイク入力を許可アプリ以外からブロックしている環境では、マイク側の録音が `COMException 0x8007007A` などで失敗する場合がある。スピーカー側のループバック録音には影響しない。この場合はIT部門にマイクアクセスの許可を申請するか、Windowsの標準プライバシー設定(設定 > プライバシーとセキュリティ > マイク)を確認すること。
