# FileXfer

ファイルやフォルダを、画面の QR とカメラの間で運ぶ光学転送アプリです。
送受信、スマホの PWA リモコン、QR の表示・読取り設定を備えています。

[decimen-optical-transfer](https://github.com/bashalarmistalt/decimen-optical-transfer)
v0.3.0 を基にしています。ライセンスは [第三者表示](THIRD-PARTY-NOTICES.md) を参照してください。

## Windows で使う

リポジトリ全体を配置し、`FileXfer.vbs` をダブルクリックします。
Windows PowerShell 5.1 が同梱 C# ソースをコンパイルし、WPF + WebView2 の画面を開きます。
.NET SDK は不要です。Microsoft Edge WebView2 Runtime が必要です。
起動ログを表示する場合は `FileXfer.cmd` を使います。
ログは `%LOCALAPPDATA%\FileXfer\logs` に保存します。

`install.bat` は管理者権限を使わず、HKCU のファイル・フォルダ・エクスプローラ背景・
デスクトップ背景に **FileXfer** を登録します。ファイル／フォルダでは
`FileXfer > 画面で開く` の1項目、背景では直接アプリを開きます。
Windows 11 では従来形式のメニュー（「その他のオプションを確認」）に表示されます。
`uninstall.bat` はこの4か所の FileXfer キーだけを削除します。
配布フォルダを移した場合は `install.bat` を再実行してください。

## 起動オプション

```powershell
.\FileXfer.cmd --path ".\input" --mode optical
.\FileXfer.cmd --no-browser
```

Markdown にしたものを転送する場合は、File2MD の出力ファイルを選びます。
QR の既定値は 1000 bytes / 30 fps / 誤り訂正 L です。

`--port` で待受ポートを指定できます。

## Linux・開発

```bash
dotnet build FileXfer.csproj
dotnet run --project FileXfer.csproj -- --no-browser
```

.NET SDK 9 を使います。HTTP 画面は表示された localhost URL から開きます。
MiniUI を使う起動コマンドは `./FileXfer` です。別途 MiniUI ソースを必要とするため、
[Linux 手順](tools/linux/README.md) を参照してください。

スマホの PWA リモコンには、Tailscale の HTTPS 接続を利用できます。

[テスト手順](tests/README.md)
