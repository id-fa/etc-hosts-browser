# Third-Party Notices

etc-hosts-browser 自体は MIT License（[LICENSE](LICENSE)）ですが、配布物（ビルド済みの exe）には
以下の第三者ソフトウェアが含まれます。それぞれのライセンスは各権利者のものが適用されます。

The etc-hosts-browser source code is licensed under the MIT License. Binary distributions
include the third-party components listed below, each under its own license.

---

## 1. Microsoft Edge WebView2 SDK (`Microsoft.Web.WebView2`)

- 含まれるもの: `Microsoft.Web.WebView2.Core.dll`, `Microsoft.Web.WebView2.WinForms.dll`, `WebView2Loader.dll`
- ライセンス: BSD 3-Clause 形式（下記全文）
- この SDK 自体が含む第三者コンポーネントの表示は、配布物の `licenses/WebView2-SDK-NOTICE.txt` を参照

```
Copyright (C) Microsoft Corporation. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

   * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
   * Redistributions in binary form must reproduce the above
copyright notice, this list of conditions and the following disclaimer
in the documentation and/or other materials provided with the
distribution.
   * The name of Microsoft Corporation, or the names of its contributors
may not be used to endorse or promote products derived from this
software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## 2. Microsoft Edge WebView2 Runtime（同梱していません / not bundled）

ブラウザエンジン本体である WebView2 Runtime は **配布物に含めていません**。利用者の Windows に
インストールされているもの（Windows 11 では標準搭載）を実行時に使います。WebView2 Runtime には
Microsoft のソフトウェアライセンス条項が適用されます。

## 3. .NET Runtime / Windows Desktop Runtime（ランタイム同梱版のみ / self-contained builds only）

ランタイム同梱版（`dotnet publish -p:PublishProfile=win-x64`）の exe には、Microsoft の .NET ランタイムと
Windows Desktop ランタイム（Windows Forms を含む）が含まれます。

- ソースコード: MIT License, Copyright (c) .NET Foundation and Contributors
  (<https://github.com/dotnet/runtime>, <https://github.com/dotnet/winforms>)
- Microsoft が配布する Windows 向けバイナリ: 「MICROSOFT .NET LIBRARY」ライセンス条項
  （再配布可能コードとしてアプリへの同梱が認められています）
- 全文は配布物の `licenses/dotnet-LICENSE.txt` と `licenses/dotnet-ThirdPartyNotices.txt` を参照
