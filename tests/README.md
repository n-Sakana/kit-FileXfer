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

`FileXfer.MiniUI.Probe.csproj` は別途 MiniUI ソースを必要とする既存のネイティブ試験です。
