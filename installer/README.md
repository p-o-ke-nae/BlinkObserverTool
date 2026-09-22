# BlinkObserverTool Installer

`BlinkObserverTool.Installer` は WiX Toolset v4 で per-user MSI を生成します。アプリは
`%LOCALAPPDATA%\Programs\pokenae\BlinkTools\BlinkObserverTool` に配置され、スタートメニュー
ショートカットと、選択可能なデスクトップショートカットが作成されます。

## 前提

- Windows
- Visual Studio 2022（Visual Studio から扱う場合）
- HeatWave for WiX Toolset v4（Visual Studio から `.wixproj` を扱う場合）
- リポジトリが要求する .NET SDK

WiX SDK と拡張はプロジェクトの `PackageReference` から復元されます。現在の検証済み
ベースラインは `4.0.5` です。

## Visual Studio

1. HeatWave for WiX Toolset v4 をインストールする。
2. `BlinkObserverTool.slnx` を開く。
3. タグを付けない通常のローカル確認では構成を `Debug` にする。
4. `BlinkObserverTool.Installer` をビルドする。
5. MSI は `installer\BlinkObserverTool.Installer\bin\Debug\BlinkObserverTool.Installer.msi`
   に生成される。

インストーラープロジェクトの1回のビルドで、アプリの publish、ファイル収集、MSI 作成まで
実行されます。`Release` を選ぶ場合は、先にすべての変更をコミットし、その HEAD に
`vMAJOR.MINOR.PATCH` 形式のタグを正確に1つだけ付ける必要があります。

## CLI

リポジトリルートで次を実行します。

```powershell
dotnet build .\installer\BlinkObserverTool.Installer\BlinkObserverTool.Installer.wixproj -c Debug
```

タグなしのローカルビルドには `Debug` を使用します。`Debug` MSI は `ProductVersion`
が `0.0.0` のままでも、再ビルドした local パッケージで既存の local インストールを
置き換えられるよう same-version upgrade を有効化しています。`Release` はコミット済み
の HEAD に単一のリリースタグを作成してから実行します。

```powershell
git status --short
git tag -a v1.2.3 -m "BlinkObserverTool v1.2.3"
.\build\Resolve-GitVersion.ps1 -RepositoryPath . -Configuration Release
dotnet build .\installer\BlinkObserverTool.Installer\BlinkObserverTool.Installer.wixproj -c Release
```

`git status --short` に出力がある場合は、先に変更をコミットしてください。アプリだけを
事前 publish する必要はありません。

## 設定

`BlinkObserverTool.Installer\installer.settings.json` を編集します。対応する JSON Schema は
`installer.settings.schema.json` で、対応エディターでは入力時に検証されます。

- `ProductName` / `Manufacturer`: MSI と「インストールされているアプリ」の表示
- `Version`: `git`、`auto`、または `Major.Minor.Build`
- `UpgradeCode`: 製品固有の固定 GUID。既存製品の更新では変更しない
- `AppDirectoryName`: 製品のインストール先ディレクトリ名
- `CommonCompanyDirName` / `CommonAppsRootDirName`: 共通親ディレクトリ名
- `MainExecutableName`: ショートカットの起動先
- `IconFilePath`: 任意の `.ico`
- `AppSourceDir`: 自動 publish または既存配布物の収集先
- `ReferencedAppProjectPath`: 指定時はビルド内でアプリを publish
- `PublishConfiguration`: 省略時はインストーラーの構成
- `PublishRuntimeIdentifier`: publish RID
- `PublishSelfContained`: self-contained publish の有無

## 更新動作

`UpgradeCode` を維持して `Version` を上げると Major Upgrade が行われます。同じ版または
古い版によるダウングレードは拒否されます。インストール範囲、ショートカット、および
アップグレード動作は `Package.wxs` で定義されています。

## MSI 検証

ICE38 と ICE64 は抑制せず、ユーザープロファイル配下の各コンポーネントに HKCU
レジストリ KeyPath とアンインストール時のディレクトリ削除を生成して解消しています。
Release では ICE91 のみ、`LocalAppDataFolder` 固定の per-user パッケージであることを
意図した設計として抑制します。per-machine インストールへ切り替えられないため、ICE91 が
警告する `ALLUSERS` に応じた配置先の切り替えは適用対象外です。加えて local 用 `Debug`
ビルドでは、再ビルドした `0.0.0` MSI を既存の local インストールへ置き換える same-version
upgrade を意図的に有効化するため、対応する ICE61 も抑制します。
