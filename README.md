# FileXfer

ファイルやフォルダを、画面の QR とカメラの間で運ぶ光学転送アプリです。
送受信、スマホの PWA リモコン、QR の表示・読取り設定を備えています。

[decimen-optical-transfer](https://github.com/bashalarmistalt/decimen-optical-transfer)
v0.3.0 を基にしています。ライセンスは [第三者表示](THIRD-PARTY-NOTICES.md) を参照してください。

## Windows で使う

リポジトリ全体を配置し、`FileXfer.vbs` をダブルクリックします。
画面は MiniUI で表示します。Windows PowerShell 5.1 が `runtime\windows-x64` の MiniUI を読み込み、
`ui\index.html` と、その横の `ui\MiniUI.App.dll`（FileXfer の本体）を動かします。
.NET SDK、WebView2、ブラウザは不要です。
起動ログを表示する場合は `FileXfer.cmd` を使います。
ログは `%LOCALAPPDATA%\FileXfer\logs` に保存します。
ダウンロードした zip から展開した場合は、MiniUI の DLL 2 つを
`%LOCALAPPDATA%\FileXfer\runtime` に書き写してから読み込みます。

`runtime\windows-x64` と `ui\MiniUI.App.dll` は `tools\windows\build.cmd` で作ります。
.NET SDK 9、Visual Studio の C++ ビルドツール、隣のフォルダにある miniUI のソースが必要です。

`install.bat` は管理者権限を使わず、HKCU のファイル・フォルダ・エクスプローラ背景・
デスクトップ背景に **FileXfer** を登録します。ファイル／フォルダでは
`FileXfer > Open` の1項目、背景では直接アプリを開きます。
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
Linux の起動コマンドは `./FileXfer` です。Windows と同じ `ui/` の画面を MiniUI で開きます。
別途 MiniUI ソースを必要とするため、[Linux 手順](tools/linux/README.md) を参照してください。

スマホの PWA リモコンには、Tailscale の HTTPS 接続を利用できます。

[テスト手順](tests/README.md)
