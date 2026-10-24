# FileXfer の Linux 起動

NixOS では `./FileXfer` が .NET SDK 9・GTK3/X11・ffmpeg 等の環境で
FileXfer のホストをビルドして起動します。MiniUI は、このリポジトリの
`runtime/linux-x64` にあるビルド済み DLL を使います。MiniUI の repo は不要です。
部品を更新する場合は [更新手順](../../runtime/README.md) を参照してください。

```bash
./FileXfer --path ./input
```

ビルドだけを行う場合は次を使います。

```bash
./tools/linux/build.sh
./FileXfer.sh
```

出力は `artifacts/miniui-linux/FileXfer.dll` です。画面はこのリポジトリの `ui/`（Windows と同じもの）です。
`--path`、`--mode optical`、`--port`、`--no-browser` と、MiniUI 固有の
`--state-dir`、`--font-dir`、`--duration-ms`、`--screenshot`、`--mcp` を利用できます。
受け取ったファイルはリポジトリの `output` に保存します。`--output` で変えられます。
カメラは ffmpeg で読み込みます。`FileXfer_CAMERA_DEVICE=/dev/videoN` で選べます。
起動するとき、Windows と同じく `tailscale serve` で HTTPS の 10000 番をこのアプリにつなぎます。

MiniUI を使わず、ブラウザで光学転送の画面を開く場合は次を使います。

```bash
dotnet run --project FileXfer.csproj -- --no-browser
```

起動時に表示される localhost URL をブラウザで開いてください。

## アプリメニューに登録する

```bash
./tools/linux/install-desktop.sh
```

現在の配置先を埋め込んだ `FileXfer.desktop` を、ユーザー用の
`${XDG_DATA_HOME:-$HOME/.local/share}/applications` に生成します。管理者権限は不要です。
配布物の `FileXfer.desktop.in` は登録用テンプレートです。
フォルダを移した場合は、移動先で登録コマンドを再実行してください。
