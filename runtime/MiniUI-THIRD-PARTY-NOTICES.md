# THIRD-PARTY-NOTICES — MiniUI

配布物に含むコード、実行時に使用するライブラリと OS 機能の権利表記です。

## Windows 配布物（`dist/windows-x64/aot/`）

| ファイル | 由来 | ライセンス | 備考 |
|---|---|---|---|
| `MiniUI.Engine.dll` | 自作（C#、Native AOT で単一のネイティブ DLL に静的リンク） | 本プロジェクト | **.NET ランタイム（ILCompiler が静的リンクする System.Private.CoreLib 等）を含む。**下の「.NET ランタイム」の通知が必要 |
| `MiniUI.Bridge.dll` | 自作（C#、.NET Framework 4.8 向けの P/Invoke 宣言だけ） | 本プロジェクト | 既存 PowerShell 5.1 の CLR で読み込む。第三者コード無し |
| `miniui.cmd` | 自作 | 本プロジェクト | 既存 `powershell.exe` を `-Command` で起動するだけ |

### 実行時に読み込む OS 機能（Windows に同梱。配布物には含めない）

user32 / gdi32 / kernel32（窓・入力・DIB・BitBlt・クリップボード・待ち時間）、shell32 / ole32（ファイル選択、タスクバーでのアプリの識別）、imm32（IME、インライン変換）、d2d1（Direct2D、DC レンダーターゲット）、dwrite（DirectWrite、シェーピング・折返し・ヒットテスト）、winhttp（HTTP(S) GET、リダイレクト追従なし）、psapi（メモリ計測）、powershell.exe 5.1（ホスト。ExecutionPolicy の対象になる .ps1 は配布しない）。

**ブラウザ・WebView2・CEF・Node.js・V8・JavaScriptCore・QuickJS・Jint は読み込まない。**HTML 解析・CSS・レイアウト・JS 実行・イベント処理は `MiniUI.Core` の自作コードである。

### .NET ランタイム（MIT）

`MiniUI.Engine.dll` には .NET 9 ランタイムライブラリの一部が静的リンクされている（Native AOT）。

```
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

.NET ランタイムの第三者通知（`THIRD-PARTY-NOTICES.TXT`、dotnet/runtime）は静的リンクされた範囲に適用される。配布時は SDK 同梱の原本を `THIRD-PARTY-NOTICES.dotnet.txt` として添付する。

### 任意の利用者部品

起動 HTML と同じフォルダの `MiniUI.App.dll` を、ページの `uiHost.call(string)` が初めて呼ばれた時だけ読み込む。通常のエンジン配布には含めず、DLL がなくても起動できる。

`samples/app-call` の小さな往復例は自作 C# を Native AOT で構築したもので、.NET ランタイムの一部を静的リンクする。上記と同じランタイム通知が適用される。追加の PackageReference、エンジンへの参照、別途インストールするランタイムはない。利用者が独自に作る部品の依存と通知は、その部品ごとに記録する。

JavaScript ライブラリは、MiniUI が対応する言語機能と API の範囲で利用できます。

## Linux の文字処理ライブラリ

| ライブラリ | 用途 | ライセンス | 扱い |
|---|---|---|---|
| HarfBuzz | シェーピング（文字列 → グリフ列） | MIT 系（Old MIT） | OS に既存のものを Linux 接続層から参照。同梱しない |
| FreeType | グリフのラスタライズ・フォント読み込み | FTL または GPLv2 | OS に既存のものを参照。同梱しない。採用環境とライセンスは実装時に記録 |
| fontconfig | フォントの列挙・フォールバック選択 | MIT 系 | OS に既存のものを参照。同梱しない |

OS にあるものを使い、同梱しません。不足時は理由を診断し、自動で取得・導入しない。中核は文字計測・描画の契約だけを知る。使用するライブラリの版と対応 OS は配布環境で確認してください。

## macOS の OS 機能

文字・入力・アクセシビリティには CoreText・NSTextInputClient・NSAccessibility 等の macOS 機能を使用します。これらは OS から利用し、配布物には同梱しません。

## 参考にした実装（コードは取り込んでいない）

Avalonia（MIT）、Jint（BSD-2）、AngleSharp（MIT）、Ladybird（BSD-2）、Blitz / Taffy / Parley（MIT/Apache-2.0）、Servo Stylo（MPL-2.0、読むだけ）。設計の参考であり、ソースコードの複製は無い。

## Chromium hand cursors (BSD)

The embedded `hand_grab.cur` and `hand_grabbing.cur` resources come from [Chromium ui/resources/cursors](https://chromium.googlesource.com/chromium/src/+/main/ui/resources/cursors/). Loaded only when CSS requests grab/grabbing.

```
// Copyright 2015 The Chromium Authors
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
//
//    * Redistributions of source code must retain the above copyright
// notice, this list of conditions and the following disclaimer.
//    * Redistributions in binary form must reproduce the above
// copyright notice, this list of conditions and the following disclaimer
// in the documentation and/or other materials provided with the
// distribution.
//    * Neither the name of Google LLC nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

```
