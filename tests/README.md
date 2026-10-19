# FileXfer のテスト

.NET SDK 9、Node.js、Python と `tests/requirements.txt` のパッケージを使います。
Chromium/Chrome が必要です。別の実行ファイルを使うときは `PLAYWRIGHT_CHROMIUM_EXECUTABLE` を指定します。

```bash
dotnet build FileXfer.csproj
dotnet run --project tests/FileXfer.Regression.csproj -c Release
node --test tests/optical-core.test.js
python3 tests/browser_regression.py
python3 tests/app_smoke.py
```

C# は C# 5 構文でコンパイルし、Fountain、損失・重複・順序入替、QR の互換性、
受信ファイルの一致、破損拒否、選択の検証と保存先の競合回避を確認します。
JavaScript は20件です。ブラウザ回帰試験は実 WASM と合成 QR／仮想カメラを使い、API をモックします。
`app_smoke.py` は実サーバを別ポートで起動し、実際の QR 描画・終了、削除 API の拒否と
別アプリのプロセスを置換しないことを確認します。`DOTNET_BIN` で dotnet の場所を指定できます。

登録定義・削除定義・PowerShell 構文・HKCU の範囲・メニュー16行制限は次で確認します。
レジストリへの書込みは行いません。Linux の PowerShell 7 でも実行できます。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\test-desktop-context.ps1
```

デスクトップの画面とネイティブ部品は次で確認します。どちらも隣のフォルダの miniUI ソースを使います。

```bash
dotnet run --project tests/FileXfer.MiniUI.Probe.csproj -c Release
python tests/ui_drive.py               # Windows: runtime/windows-x64 と ui/MiniUI.App.dll を使う
python3 tests/ui_drive.py --linux      # Linux: ./FileXfer.sh を使う
```

`FileXfer.MiniUI.Probe.csproj` は、画面に出す QR（QrStream と ScaledQr）をカメラ相当の画像に置いて
デスクトップの受信処理（CameraReceiver）に渡し、受け取ったファイルが元と一致することを確かめます。
`ui_drive.py` は MiniUI の MCP で画面を操作し、送る・受け取る・スマホ・QR 表示をライトとダークで
`tests/results/screens` に保存します。

## 診断ログの隔離確認

`FileXfer.Headless.Probe.csproj` は同じ `NativeLoopbackProbe` を、MiniUIソース・実カメラ・窓なしで実行します。
`TMPDIR` と `FileXfer_LOG_DIR` を試験フォルダに設定し、`--artifacts-path` でビルド先も分離できます。
これは合成カメラ画像による試験であり、物理カメラでの転送成功や配布版MiniUIの動作を示しません。

`FileXfer.Diagnostics.csproj` は引数で指定した隔離フォルダに試料・ログ・保存結果を作り、
製品の送信／受信処理からの成功・中止・重複・拒否・破損検出・保存失敗、秘密値の非記録、
JSONL形式、キュー上限・ローテーション、集計呼出しの負担を確認します。

```bash
dotnet run --project tests/FileXfer.Diagnostics.csproj -c Release -- /path/to/isolated/log-test
```

`logging_integration.py` は隔離publishしたHTTP版をランダムな空きポートで起動します。
実HTTP・実WASM・canvasによる合成カメラ・実C#復元／保存を通し、バイト一致、ログの照合、
通信待ち／通信断中のIndexedDB保存、PCへの自動回収、16,384件の通常履歴を上書きした後も残る障害履歴、ストレージ拒否を確認します。
試験が起動したPIDだけを終了します。既存アプリのポートや配布フォルダは指定しないでください。

```bash
dotnet publish FileXfer.csproj -c Release --no-self-contained -o /path/to/isolated/http
python3 tests/logging_integration.py --app /path/to/isolated/http --work /path/to/isolated/evidence
```

2026-10-19のログ実装確認では、C#回帰10件・JS20件・Chromium/WASM回帰・上記の隔離試験を実行しました。
成功／中止／破損／保存失敗、PWA通信保留・切断、ログ上限・秘密値非記録を確認しています。
Windowsネイティブ部品はソースのmanagedコンパイルまで、PowerShellはLinuxの7.6.5で確認しました。
`diagnostics_collection.py --stage /path/to/isolated/linux-distribution --work /path/to/evidence` は
隔離したXvfbで通常の `FileXfer.sh` → Linux MiniUI → 実サーバを起動し、PWAの起動・通信断・復旧履歴が
PCへ自動保存されることを確認します。回収用のコンソールコマンドは実行しません。
実workerのWASM取得503、原因の安全な記録、同origin検査・容量制限・秘密値除去も確認します。
使用するXvfb、アプリ、ブラウザだけを起動し、それぞれ自分のPIDで片づけます。物理カメラは使いません。

Linux MiniUIは既存配布物の `MINIUI_SOURCE_REVISION` に記録された `c0cc050` のソース入力でビルド・隔離起動を確認。
現在のMiniUI HEADではLinuxFetcher／Engine API変更によりFileXferの既存ホストがビルドできません。
今回MiniUIやホストのAPI適応は変更していません。
Windows Native AOT配布物・PowerShell 5.1での起動、物理カメラ・スマホ実機・Tailscale実接続は別途確認が必要です。

`FileXfer.Shutdown.csproj` は隔離フォルダ・専用ポートでNativeAppの実サーバを起動し、
終了直前のイベント・集計の保存とサーバ終了を確認します。書込みゲートを占有させ、
呼出元の待ちが上限で戻ることと、占有解除後に末尾が排出されることも確認します。
これはmanaged側の確認です。Native AOTの `filexfer_shutdown` とPowerShell 5.1の事前コンパイル済み接続DLLは、
Windows通常配布物で `launcher.native_shutdown` のcode0、traceの `app.stop` / `native.shutdown.end`、
所有PID残存0を別途確認してください。コンパイルは配布ビルド時だけです。
