# BlinkObserverTool 開発者・運用者向けリリース運用マニュアル

## 目次

1. [概要](#1-概要)
2. [前提環境](#2-前提環境)
3. [リポジトリ構成と命名規則](#3-リポジトリ構成と命名規則)
4. [依存パッケージの準備と GitHub Packages 認証](#4-依存パッケージの準備と-github-packages-認証)
5. [Visual Studio と HeatWave による開発・ビルド運用](#5-visual-studio-と-heatwave-による開発ビルド運用)
6. [Debug ビルド運用](#6-debug-ビルド運用)
7. [厳格な vMAJOR.MINOR.PATCH Release ビルド運用](#7-厳格な-vmajorminorpatch-release-ビルド運用)
8. [Windows インストーラー (MSI) の ProductVersion 制約](#8-windows-インストーラー-msi-の-productversion-制約)
9. [MSI コンテンツ検証とチェックサム生成](#9-msi-コンテンツ検証とチェックサム生成)
10. [既定プロファイルマニフェストの保守](#10-既定プロファイルマニフェストの保守)
11. [アップグレード不変条件](#11-アップグレード不変条件)
12. [GitHub Actions CI および Release フロー](#12-github-actions-ci-および-release-フロー)
13. [障害復旧とロールバック運用](#13-障害復旧とロールバック運用)
14. [未署名とコード署名境界](#14-未署名とコード署名境界)
15. [リリース運用チェックリスト](#15-リリース運用チェックリスト)
16. [関連ドキュメント・参照先](#16-関連ドキュメント参照先)

---

## 1. 概要

本書は、**BlinkObserverTool** のビルド、パッケージング、および GitHub Releases への発行に関する開発者・運用者向け正式運用手順書です。

BlinkObserverTool は、WiX Toolset v4 ベースのソフトウェア開発キット（Software Development Kit: SDK）形式プロジェクトを用いて単一の Windows インストーラー（Microsoft Windows Installer: MSI）を生成します。本プロジェクトでは、セマンティックバージョニング（Semantic Versioning: SemVer）に準拠した Git タグを唯一の正本としてバージョンを厳格に同期し、Visual Studio および継続的インテグレーション／継続的デリバリー（Continuous Integration / Continuous Delivery: CI/CD）パイプラインの双方で決定論的かつ再現性の高いビルドを保証します。

一般利用者向けの導入・更新・設定保護仕様については、[BlinkObserverTool 利用者向けインストーラー導入マニュアル](user-installer-manual.ja.md) を参照してください。

---

## 2. 前提環境

ビルドおよびリリース作業を行う環境には、以下のソフトウェアおよび依存フィードが正しく導入・構成されている必要があります。

| ツール / コンポーネント | 推奨バージョン / 条件 | 用途・備考 |
|---|---|---|
| オペレーティング システム (OS: Operating System) | Windows 10 / Windows 11 (x64) | 必須ビルド環境 |
| .NET SDK | .NET 10.0 SDK (`10.0.x`) | アプリケーションおよびインストーラーのビルド |
| 統合開発環境 (Integrated Development Environment: IDE) | Visual Studio 2022 (Version 17.10 以上) | GUI (Graphical User Interface) 開発環境 (CLI ビルド時は不要) |
| Visual Studio 拡張機能 | FireGiant HeatWave Community Edition for VS 2022 | Visual Studio 上での WiX v4 プロジェクト読み込み・ビルド |
| バージョン管理システム | Git for Windows (2.40 以上) | バージョン導出およびリリースタグ管理 |
| シェル環境 | Windows PowerShell 5.1 または PowerShell 7 (pwsh) | ビルド補助スクリプトおよび検証スクリプトの実行 |
| GitHub コマンドラインツール | GitHub CLI (`gh`) | GitHub Releases の発行・アセット検証 |
| 外部依存パッケージ | `GenericRecognition.Workbench.*` (0.1.7) | GitHub Packages またはローカル NuGet フィードから供給 |

> **注意 (PowerShell 実行ポリシー):**  
> スクリプト実行が制限されている環境では、作業用 PowerShell プロンプトで `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass` を実行してスクリプトの実行を許可してください。

---

## 3. リポジトリ構成と命名規則

### 3.1 ディレクトリ構成

BlinkObserverTool リポジトリの主要なファイルおよびディレクトリ構成は以下のとおりです。

```text
C:\Users\o_leg\source\repos\BlinkObserverTool\
├── .github\
│   └── workflows\
│       ├── ci.yml                           # PR / main ブランチ push 時の CI ワークフロー
│       └── release.yml                      # v*.*.* タグ push 時のリリースワークフロー
├── build\
│   ├── Resolve-GitVersion.ps1              # Git タグからの厳格なバージョン解決スクリプト
│   ├── Test-MsiPackage.ps1                 # MSI 属性および同封ファイルの検証スクリプト
│   └── tests\
│       └── Resolve-GitVersion.Tests.ps1    # バージョン解決スクリプトの自動テスト
├── docs\
│   ├── developer-release-manual.ja.md      # 本書 (開発者・運用者向けマニュアル)
│   ├── user-installer-manual.ja.md         # 利用者向けインストーラー導入マニュアル
│   ├── installer-decision.md               # インストーラー選定および設計判断の記録
│   └── release-process.md                  # リリースプロセスの概要メモ
├── installer\
│   ├── README.md                           # インストーラープロジェクトの基本説明
│   └── BlinkObserverTool.Installer\        # WiX v4 インストーラープロジェクト
│       ├── BlinkObserverTool.Installer.wixproj # WiX SDK プロジェクトファイル
│       ├── Package.wxs                     # インストール構造、UI、Major Upgrade 定義
│       ├── installer.settings.json         # インストーラー構成の単一正本設定ファイル
│       ├── installer.settings.schema.json  # 設定ファイルの JSON Schema 定義
│       └── scripts\
│           ├── Generate-InstallerConfig.ps1 # settings.json から MSBuild プロパティを生成
│           └── Prepare-PerUserHarvest.ps1   # 収集ファイル群に HKCU レジストリキーを付与
├── reference\
│   └── verified-profiles\                  # ローカル検証用の既定プロファイル実体 (Git 追跡対象外)
├── BlinkObserverTool\                       # メイン WPF (Windows Presentation Foundation) アプリケーションプロジェクト
│   ├── BlinkObserverTool.csproj
│   └── DefaultProfiles\
│       └── manifest.json                   # 既定プロファイルの配布マニフェスト (SHA-256)
├── BlinkObserverTool.BlinkRecognition\      # 瞬き認識ロジックおよび専用設定画面
├── BlinkObserverTool.BlinkRecognition.Tests\ # 瞬き認識ロジックの単体テスト
├── BlinkObserverTool.ProfileSync\           # 既定プロファイルの安全な自動同期ライブラリ
├── BlinkObserverTool.ProfileSync.Tests\     # プロファイル同期エンジンの単体テスト
├── BlinkObserverTool.slnx                  # ソリューションファイル
└── NuGet.Config                            # パッケージソース設定 (LocalPackages / GitHub)
```
├── installer\
│   ├── README.md                           # インストーラープロジェクトの基本説明
│   └── BlinkObserverTool.Installer\        # WiX v4 インストーラープロジェクト
│       ├── BlinkObserverTool.Installer.wixproj # WiX SDK プロジェクトファイル
│       ├── Package.wxs                     # インストール構造、UI、Major Upgrade 定義
│       ├── installer.settings.json         # インストーラー構成の単一正本設定ファイル
│       ├── installer.settings.schema.json  # 設定ファイルの JSON Schema 定義
│       └── scripts\
│           ├── Generate-InstallerConfig.ps1 # settings.json から MSBuild プロパティを生成
│           └── Prepare-PerUserHarvest.ps1   # 収集ファイル群に HKCU レジストリキーを付与
├── reference\
│   └── verified-profiles\                  # ローカル検証用の既定プロファイル実体 (Git 追跡対象外)
├── BlinkObserverTool\                       # メイン WPF アプリケーションプロジェクト
│   ├── BlinkObserverTool.csproj
│   └── DefaultProfiles\
│       └── manifest.json                   # 既定プロファイルの配布マニフェスト (SHA-256)
├── BlinkObserverTool.BlinkRecognition\      # 瞬き認識ロジックおよび専用設定画面
├── BlinkObserverTool.BlinkRecognition.Tests\ # 瞬き認識ロジックの単体テスト
├── BlinkObserverTool.ProfileSync\           # 既定プロファイルの安全な自動同期ライブラリ
├── BlinkObserverTool.ProfileSync.Tests\     # プロファイル同期エンジンの単体テスト
├── BlinkObserverTool.slnx                  # ソリューションファイル
└── NuGet.Config                            # パッケージソース設定 (LocalPackages / GitHub)
```

### 3.2 命名規則と単一正本の原則

1. **製品固有命名の徹底:**  
   インストーラープロジェクトや関連ファイルにおいて、`installer-template` や `SharedInstaller` といった抽象的な雛形名は使用しません。製品ごとに `installer\BlinkObserverTool.Installer`、`BlinkObserverTool.Installer.wixproj` のように製品名を明示した固有名を使用します (`Shared` や `template` は未適用の汎用ライブラリ・テンプレート側のみで使用)。
2. **単一正本 (Single Source of Truth) の維持:**  
   - 製品メタデータ (製品名、発行元、GUID、実行ファイル名): `installer.settings.json`
   - 設定値の型・形式制約: `installer.settings.schema.json`
   - リリースバージョン: HEAD コミットを指す単一の Git タグ (`vMAJOR.MINOR.PATCH`)
   - インストール動作および UI フロー: `Package.wxs`
3. **一時生成物のコミット禁止:**  
   publish 出力ディレクトリ (`obj\publish`)、WiX のビルド出力 (`bin\`, `obj\`)、生成された `.msi`、`.wixpdb` は Git の追跡対象外とします。

---

## 4. 依存パッケージの準備と GitHub Packages 認証

BlinkObserverTool は、汎用画像認識基盤である `GenericRecognition.Workbench.*` パッケージ (バージョン `0.1.7`) を参照します。

### 4.1 ローカル環境でのパッケージ準備 (ローカルフィード利用)

同一マシン上の `C:\Users\o_leg\source\repos\GenericRecognitionWorkbench` からローカルにパッケージを供給する場合は、以下のコマンドで正式版 `0.1.7` として `LocalPackages` ディレクトリへ pack します。

```powershell
# GenericRecognitionWorkbench リポジトリルートで実行
cd C:\Users\o_leg\source\repos\GenericRecognitionWorkbench
dotnet pack .\Recognition.Core\Recognition.Core.csproj -c Release -o .\LocalPackages -p:Version=0.1.7
dotnet pack .\Recognition.Infrastructure\Recognition.Infrastructure.csproj -c Release -o .\LocalPackages -p:Version=0.1.7
dotnet pack .\Recognition.Wpf\Recognition.Wpf.csproj -c Release -o .\LocalPackages -p:Version=0.1.7
```

> **重要:**  
> `-p:Version=0.1.7` を指定せずに pack すると、開発用の接尾辞が付与された `0.1.7-local` が生成されます。BlinkObserverTool は正式版 `0.1.7` を厳格に参照するため、必ずバージョンを指定して pack してください。

### 4.2 GitHub Packages (GitHub パッケージレジストリ) 認証

開発者の環境で GitHub Packages から直接パッケージを復元する場合、`read:packages` スコープを持つ個人用アクセス トークン (Personal Access Token: PAT) を用いてパッケージソースを登録します。

```powershell
# 開発機ローカルでの登録例 (リポジトリルート外のユーザー設定へ登録)
dotnet nuget add source "https://nuget.pkg.github.com/<PACKAGE_OWNER>/index.json" `
  --name github `
  --username "<GITHUB_USERNAME>" `
  --password "<GITHUB_PERSONAL_ACCESS_TOKEN>" `
  --store-password-in-clear-text `
  --valid-authentication-types basic
```

> **セキュリティ境界ルール:**  
> - リポジトリ内の `NuGet.Config` に認証トークンやパスワードを直接書き込んでコミットすることは固く禁止されています。
> - CI / CD 実行時は、ワークフロー内で一時的に GitHub 提供の `GITHUB_TOKEN` を用いてソースを追加し、ステップ終了時に必ず削除 (`dotnet nuget remove source github`) します。

### 4.3 GitHub リポジトリ間アクセス設定

別リポジトリ (`GenericRecognitionWorkbench`) で発行されたパッケージを GitHub Actions 内の標準 `GITHUB_TOKEN` で読み取るには、パッケージ所有側での設定が必要です。

1. GitHub 上でパッケージの管理画面 (`https://github.com/orgs/<OWNER>/packages` または `https://github.com/users/<OWNER>/packages`) を開く。
2. `GenericRecognition.Workbench.*` の **Package settings** を開く。
3. **Manage Actions access** (Actions からのアクセス管理) を選択し、`BlinkObserverTool` リポジトリを **Read** 権限で追加する。
4. パッケージ所有者がリポジトリ所有者と異なる組織・アカウントの場合は、`BlinkObserverTool` リポジトリの **Settings > Secrets and variables > Actions > Variables** に `GENERIC_RECOGNITION_PACKAGE_OWNER` 変数を定義します。

### 4.4 パッケージ復元エラー時の原因と対処

`dotnet restore` 実行時にパッケージ `GenericRecognition.Workbench.*` の解決に失敗した場合、以下の原因を確認してください。

- **HTTP 401 (Unauthorized):** 個人用アクセス トークン (PAT) の有効期限切れ、または `read:packages` スコープの欠落。新しい PAT を生成してパッケージソースの認証情報を再登録してください。
- **HTTP 404 (Not Found):** パッケージ側の **Manage Actions access** に本リポジトリが追加されていないか、パッケージ所有者名（`<PACKAGE_OWNER>` またはリポジトリ変数 `GENERIC_RECOGNITION_PACKAGE_OWNER`）が誤っています。
- **バージョン不一致:** `GenericRecognitionWorkbench` 側で `-p:Version=0.1.7` を付けずに pack すると `0.1.7-local` が生成され、BlinkObserverTool が要求する正式版 `0.1.7` と一致せず復元エラーになります。

---

## 5. Visual Studio と HeatWave による開発・ビルド運用

Visual Studio 2022 を利用した日常的な開発およびインストーラー構築の手順です。

### 5.1 ソリューションの読み込み

1. Visual Studio 2022 に **FireGiant HeatWave Community Edition for VS 2022** がインストールされていることを確認します。
2. `BlinkObserverTool.slnx` を開きます。
3. ソリューションエクスプローラーに以下のプロジェクトが正常に読み込まれていることを確認します。
   - `BlinkObserverTool` (WPF メインアプリ)
   - `BlinkObserverTool.BlinkRecognition` (認識ロジック)
   - `BlinkObserverTool.BlinkRecognition.Tests` (認識テスト)
   - `BlinkObserverTool.ProfileSync` (プロファイル同期)
   - `BlinkObserverTool.ProfileSync.Tests` (同期テスト)
   - `BlinkObserverTool.Installer` (WiX v4 インストーラー)

### 5.2 WiX 拡張パッケージの確認

インストーラープロジェクトは NuGet 経由で WiX 拡張を参照しています。
- `WixToolset.UI.wixext` (4.0.5)
- `WixToolset.Util.wixext` (4.0.5)
- `WixToolset.Heat` (4.0.5)

これらは通常の NuGet パッケージとして自動的に復元されます。

### 5.3 ビルドと自動処理の流れ

Visual Studio の構成マネージャーで `Debug` を選択し、`BlinkObserverTool.Installer` を右クリックして **ビルド** または **リビルド** を実行します。

ビルド時には以下のターゲットが順次自動実行されます。
1. **GenerateInstallerConfig:** `scripts\Generate-InstallerConfig.ps1` が起動し、`installer.settings.json` を検証して `InstallerConfig.props` を生成。
2. **PublishReferencedApp:** `dotnet publish` が自動実行され、`BlinkObserverTool.csproj` を `win-x64` 向け自己完結型 (Self-Contained) として `installer\BlinkObserverTool.Installer\obj\publish` へ出力。
3. **ConfigureHarvestDirectory / Harvest:** WiX Heat により出力ディレクトリ内の全ファイルを自動ハーベスト (`_AppFiles_dir.wxs`)。
4. **PreparePerUserHarvest:** `scripts\Prepare-PerUserHarvest.ps1` がハーベスト結果を走査し、ユーザープロファイル配置用として各コンポーネントに HKCU レジストリキーパスおよびアンインストール時ディレクトリ削除定義を自動注入。
5. **CoreCompile / Link:** 単一の MSI パッケージを生成。

> **設計方針 (GUI 編集ツールの不採用):**  
> Visual Studio Installer Projects などのドラッグ＆ドロップ式専用 GUI は、Git での差分追跡性や CLI との共通化に欠けるため採用していません。設定の編集は `installer.settings.json` のテキスト編集で行い、JSON Schema による入力補完を活用します。

---

## 6. Debug ビルド運用

日常の開発、動作確認、およびインストーラーのローカル検証には `Debug` 構成を使用します。

### 6.1 Debug ビルドの特徴

- Git タグの存在は要求されません。
- バージョン解決スクリプトは常にバージョン `0.0.0` (情報バージョン: `0.0.0-local+g<ショートコミットハッシュ>`) を返します。
- 生成される MSI の `ProductVersion` も `0.0.0` となります。

### 6.2 CLI によるビルド手順

リポジトリルート (`C:\Users\o_leg\source\repos\BlinkObserverTool`) で Windows PowerShell を開き、以下を実行します。

```powershell
# 依存パッケージの復元
dotnet restore .\BlinkObserverTool.slnx

# バージョン解決スクリプトのテスト実行
.\build\tests\Resolve-GitVersion.Tests.ps1

# 各単体テストの実行
dotnet test .\BlinkObserverTool.ProfileSync.Tests\BlinkObserverTool.ProfileSync.Tests.csproj -c Debug --no-restore
dotnet test .\BlinkObserverTool.BlinkRecognition.Tests\BlinkObserverTool.BlinkRecognition.Tests.csproj -c Debug --no-restore

# Debug MSI のビルド
dotnet build .\installer\BlinkObserverTool.Installer\BlinkObserverTool.Installer.wixproj -c Debug --no-restore
```

ビルド完了後、以下の場所に MSI が生成されます。
`.\installer\BlinkObserverTool.Installer\bin\Debug\BlinkObserverTool.Installer.msi`

---

## 7. 厳格な vMAJOR.MINOR.PATCH Release ビルド運用

本番配布用の Release ビルドでは、バージョン解決スクリプト (`build\Resolve-GitVersion.ps1`) による厳格な検証が行われます。

### 7.1 Release ビルドの厳格ルール

1. **Git 作業ツリーの完全性:**  
   作業ツリー内に未コミットの変更が存在しないこと (`git status --short` が空であること)。
2. **HEAD を指す単一の SemVer タグ:**  
   HEAD コミットに、`^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$` に厳密に一致する Git 注釈付きタグ (Annotated Tag) が**正確に 1 つだけ**存在すること。
3. **例外時のビルド即時失敗:**  
   以下の不整合が検出された場合、スクリプト (`build\Resolve-GitVersion.ps1` および `Generate-InstallerConfig.ps1`) は直ちに例外を送出してビルドを中断します。
   - **Git リポジトリ外で Release 実行:**  
     `Git metadata is required for Release versioning, but '<パス>' is not a Git work tree.`
   - **HEAD コミットが取得できない場合:**  
     `Git metadata is required for Release versioning, but the repository has no valid HEAD commit.`
   - **HEAD にタグが存在しない場合:**  
     `Release versioning requires HEAD to have exactly one Git tag named vMAJOR.MINOR.PATCH.`
   - **タグの形式が不正な場合 (プレリリースやメタデータ含む):**  
     `Malformed Git tag(s) at HEAD: <タグ名>. Release tags must match vMAJOR.MINOR.PATCH exactly.`
   - **HEAD に複数のタグが付与されている場合:**  
     `Conflicting Git release tags at HEAD: <タグ名一覧>. Release builds require exactly one version tag.`
   - **タグの数値が Windows インストーラーの上限を超えている場合:**  
     `Git tag '<タグ名>' cannot be represented as an MSI version. MAJOR and MINOR must be <= 255; PATCH must be <= 65535.`
   - **installer.settings.json の Version 指定と HEAD タグが矛盾する場合:**  
     `Configured installer Version '<設定値>' conflicts with the HEAD Git tag version '<タグバージョン>'.`

### 7.2 CLI による Release ビルド手順

```powershell
# 1. 未コミット変更がないことを確認
git status --short

# 2. HEAD コミットに対して注釈付きタグを作成
git tag -a v1.2.3 -m "BlinkObserverTool v1.2.3"

# 3. バージョン解決結果の事前確認
.\build\Resolve-GitVersion.ps1 -RepositoryPath . -Configuration Release -OutputFormat Json

# 4. パッケージ復元とテスト実行
dotnet restore .\BlinkObserverTool.slnx
dotnet test .\BlinkObserverTool.ProfileSync.Tests\BlinkObserverTool.ProfileSync.Tests.csproj -c Release --no-restore
dotnet test .\BlinkObserverTool.BlinkRecognition.Tests\BlinkObserverTool.BlinkRecognition.Tests.csproj -c Release --no-restore

# 5. Release MSI のビルド
dotnet build .\installer\BlinkObserverTool.Installer\BlinkObserverTool.Installer.wixproj -c Release --no-restore

# 6. 生成された MSI の検証
.\build\Test-MsiPackage.ps1 `
  -MsiPath .\installer\BlinkObserverTool.Installer\bin\Release\BlinkObserverTool.Installer.msi `
  -ExpectedVersion 1.2.3 `
  -ExpectedUpgradeCode 813D1E2D-7D7A-4984-A062-81B06B5EBC26 `
  -MainExecutableName BlinkObserverTool.exe `
  -RequiredFileNames @("manifest.json", "profile.json", "template.png")
```

---

## 8. Windows インストーラー (MSI) の ProductVersion 制約

Windows インストーラー (Windows Installer Service) における `ProductVersion` プロパティの比較仕様は、一般的な SemVer とは異なる制限が存在します。

### 8.1 3 フィールド比較ルール

Windows Installer の Major Upgrade 判定では、`ProductVersion` のうち **先頭の 3 フィールド (`major.minor.build`) のみが比較対象** となります。
- 第 4 フィールド (`revision`) は存在しても Windows Installer によるバージョン比較ロジックで無視されます。
- 文字列によるプレリリース識別子 (例: `-alpha`, `-beta`, `-rc.1`) は Windows Installer のバージョン構文として許可されず、エラーとなります。

### 8.2 フィールドごとの数値上限

Windows Installer の仕様により、各フィールドに設定可能な数値の上限は以下のとおり厳格に制限されています。

| フィールド | 役割 | 許容範囲 | 上限値 |
|---|---|---|---|
| 第 1 フィールド (`MAJOR`) | メジャーバージョン | 0 ～ 255 | 255 |
| 第 2 フィールド (`MINOR`) | マイナーバージョン | 0 ～ 255 | 255 |
| 第 3 フィールド (`PATCH` / `BUILD`) | パッチバージョン | 0 ～ 65,535 | 65,535 |

本プロジェクトの `build\Resolve-GitVersion.ps1` は、Git タグから数値を抽出した段階でこの上限値をバリデーションし、超過している場合はビルド前に明確な例外を送出します。

---

## 9. MSI コンテンツ検証とチェックサム生成

リリース対象となる MSI の品質を担保するため、ビルド後に自動検証スクリプトを実行し、SHA-256 チェックサムを生成します。

### 9.1 Test-MsiPackage.ps1 による検証項目

`build\Test-MsiPackage.ps1` は Windows Installer の COM オブジェクト (`WindowsInstaller.Installer`) を直接呼び出し、生成された MSI データベースを非破壊検査します。

1. **ProductVersion 検証:** `Property` テーブルの `ProductVersion` が、期待されるバージョン値と完全一致するか検証。
2. **UpgradeCode 検証:** `Property` テーブルの `UpgradeCode` が製品固定 GUID (`813D1E2D-7D7A-4984-A062-81B06B5EBC26`) と一致するか検証。
3. **必須ファイル検証:** `File` テーブルを走査し、メイン実行ファイル (`BlinkObserverTool.exe`) および動作に必要な同梱プロファイルファイル群 (`manifest.json`, `profile.json`, `template.png`) が欠落なく格納されているか検証。

```powershell
# 検証コマンド
.\build\Test-MsiPackage.ps1 `
  -MsiPath .\installer\BlinkObserverTool.Installer\bin\Release\BlinkObserverTool.Installer.msi `
  -ExpectedVersion 1.2.3 `
  -ExpectedUpgradeCode 813D1E2D-7D7A-4984-A062-81B06B5EBC26 `
  -MainExecutableName BlinkObserverTool.exe `
  -RequiredFileNames @("manifest.json", "profile.json", "template.png")
```

#### 9.2 検証失敗時の例外メッセージと原因

`build\Test-MsiPackage.ps1` は以下の不整合を検出した際に例外をスローしてビルドを停止させます。

- **拡張子エラー:** `MsiPath must point to an .msi file: <指定パス>`
- **バージョン不一致:** `ProductVersion mismatch. Expected '<期待値>', found '<実際値>'.`
- **UpgradeCode 不一致:** `UpgradeCode mismatch. Expected '<期待値>', found '<実際値>'.`
- **必須ファイル欠落:** `Required file '<ファイル名>' was not found in the MSI File table.`

### 9.3 SHA-256 チェックサムファイルの生成

配布ファイルの完全性 (ダウンロード時の破損や改ざんの検知) を保証するため、MSI ファイルの SHA-256 ハッシュ値を算出し、チェックサムファイル (`.sha256`) を生成します。

```powershell
$msiPath = ".\installer\BlinkObserverTool.Installer\bin\Release\BlinkObserverTool.Installer.msi"
$hash = (Get-FileHash -LiteralPath $msiPath -Algorithm SHA256).Hash.ToLowerInvariant()
$fileName = [System.IO.Path]::GetFileName($msiPath)
$checksumContent = "$hash  $fileName`n"

# BOM なし UTF-8 で保存
[System.IO.File]::WriteAllText(
  "$msiPath.sha256",
  $checksumContent,
  [System.Text.UTF8Encoding]::new($false)
)
```

フォーマットは標準的な GNU `sha256sum` 互換 (小文字 64 文字のハッシュ値、半角スペース 2 個、ファイル名) とします。

---

## 10. 既定プロファイルマニフェストの保守

アプリケーションには検証済みの認識プロファイルが同梱されており、初回起動時および更新時にユーザー領域 (`%LOCALAPPDATA%\pokenae\BlinkObserverTool\Profiles`) へ自動同期されます。

### 10.1 マニフェストファイルの構造

マニフェストの正本は `BlinkObserverTool\DefaultProfiles\manifest.json` です。

```json
{
  "schemaVersion": 1,
  "version": "2026.09.21.1",
  "profiles": [
    {
      "id": "dark-2026-09-21-08-52-45-blink-feature",
      "relativePath": "Profiles/暗所_2026-09-21_08-52-45_瞬き特徴",
      "files": [
        {
          "relativePath": "profile.json",
          "sha256": "E52B66A7391405B13EFCA513F00C9BF159FCD1E1376A06F6742C7A7B12D4BC53"
        }
      ]
    },
    {
      "id": "dark-2026-09-21-08-59-52-template",
      "relativePath": "Profiles/暗所_2026-09-21_08-59-52_テンプレート",
      "files": [
        {
          "relativePath": "profile.json",
          "sha256": "C2FA146F97CF279AB46F146BEC97A70147BB4573F45FD2086E3E1ABD2F49577F"
        },
        {
          "relativePath": "images/recognition/template.png",
          "sha256": "A3A64620AC6D5560046127938200FABC000F811D9CC1C829200EABBA9F0A8281"
        }
      ]
    }
  ]
}
```

### 10.2 プロファイルの追加・更新手順

新しいプロファイルを同梱する場合、または既存の同梱プロファイルを更新する場合は、以下の手順に従います。

1. **実体ファイルの配置:**  
   Git では管理しないローカル作業用ディレクトリ `reference\verified-profiles\<プロファイル名>\` 配下に、
   必要なファイル (`profile.json`、テンプレート画像など) を配置します。
2. **SHA-256 ハッシュ値の算出:**  
   追加・変更した各ファイルの SHA-256 ハッシュ値を計算します (大文字英数字 64 桁)。
   ```powershell
   (Get-FileHash -LiteralPath ".\reference\verified-profiles\新プロファイル\profile.json" -Algorithm SHA256).Hash
   ```
3. **マニフェストの更新:**  
   `BlinkObserverTool\DefaultProfiles\manifest.json` を編集し、`version` を新しい値 (例: `YYYY.MM.DD.N`) に更新したうえで、プロファイルのエントリと各ファイルのハッシュ値を記載します。
4. **自動テストの実行:**  
   `dotnet test .\BlinkObserverTool.ProfileSync.Tests\BlinkObserverTool.ProfileSync.Tests.csproj` を実行し、マニフェスト構文やハッシュ値が正常であることを検証します。

### 10.3 同期エンジン (DefaultProfileSynchronizer) の安全仕様

プロファイル同期エンジンは、ユーザーのカスタム設定を破壊しないよう以下の不変条件に基づいて動作します。
- **新規プロファイル:** ユーザー側の Profiles フォルダに同名ディレクトリが存在しない場合のみコピーを配置します。
- **既存プロファイルの更新:** ユーザー側のファイル群が「前回の配布状態から一切変更されていない」場合 (ハッシュ値が完全一致する場合) にのみ、安全に更新を適用します。
- **ユーザー編集の保護:** ユーザーが少しでも編集したプロファイル、またはユーザーが独自に作成したプロファイルは**一切上書き・削除されません**。
- **同期状態の記録:** 前回の配布バージョンおよび各ファイルのハッシュ値は、`%LOCALAPPDATA%\pokenae\BlinkObserverTool\default-profiles-state.json` に安全に記録されます。

### 10.4 マニフェスト構文異常時の例外

マニフェストファイルの構文や参照先ファイルに不整合がある場合、同期エンジンは以下の例外を送出して処理を中断します。

- **スキーマバージョン不正:** `Unsupported default-profile manifest schema version '<バージョン>'.`
- **マニフェストバージョン空:** `The default-profile manifest version is required.`
- **プロファイル ID の重複または空:** `Default-profile ID '<ID>' is empty or duplicated.`
- **相対パスの重複:** `Default-profile path '<パス>' is duplicated.`
- **配布元ディレクトリの欠落または空:** `Default-profile source '<パス>' is missing or empty.`

---

## 11. アップグレード不変条件

MSI パッケージの更新において、既存ユーザー環境の安定性を維持するため、以下の条件を永続的に維持しなければなりません。

| 不変項目 | 要件 / 規定値 | 理由と影響 |
|---|---|---|
| `UpgradeCode` | `813D1E2D-7D7A-4984-A062-81B06B5EBC26` を固定 | 変更すると別製品扱いとなり、旧版の自動アンインストール (Major Upgrade) が機能しなくなる。 |
| インストールスコープ | `Scope="perUser"` (`LocalAppDataFolder`) を固定 | `ALLUSERS` によるマシン全体インストールへ途中で変更すると、権限昇格の不整合や配置先の断絶が発生する。 |
| ダウングレード抑止 | `AllowDowngrades="no"` を固定 | 新しいバージョンが導入された環境への旧バージョンの上書き導入を明示的に遮断する。 |
| ユーザーデータ保護 | インストール先ディレクトリに設定やデータを保存しない | アプリ本体は `%LOCALAPPDATA%\Programs\...` に配置し、設定やプロファイルは `%LOCALAPPDATA%\pokenae\BlinkObserverTool` に完全分離する。アンインストール時もユーザーデータは保持される。 |
| 内部整合性評価 (Internal Consistency Evaluator: ICE) | 理由のない一括抑制の禁止 | ICE38 および ICE64 は HKCU レジストリキーパスと `RemoveFolder` コンポーネントで解決済み。per-user 固定設計の根拠がある ICE91 のみ抑制する。 |

---

## 12. GitHub Actions CI および Release フロー

リポジトリには、GitHub Actions による自動ビルド・リリースパイプラインが構築されています。

### 12.1 CI ワークフロー (`.github\workflows\ci.yml`)

- **トリガー条件:** プルリクエスト (Pull Request: PR) の作成・更新、および `main` ブランチへの push。
- **権限 (Permissions):** `contents: read`, `packages: read` の最小権限。
- **実行内容:**
  1. サードパーティ Action は 40 桁の完全コミット SHA で固定参照。
  2. .NET 10 SDK のセットアップ。
  3. GitHub Packages 認証の一時設定 (ワークフロー実行中のみ有効化)。
  4. 依存パッケージの復元。
  5. バージョン解決スクリプトのテスト実行。
  6. プロファイル同期および瞬き認識テストの実行。
  7. Debug 構成での MSI ビルド実行。
  8. 一時的な認証ソースの確実な破棄 (`dotnet nuget remove source github`)。
  9. 生成された Debug MSI を GitHub Artifacts (保管期間 7 日) へアップロード。

### 12.2 リリースワークフロー (`.github\workflows\release.yml`)

- **トリガー条件:** `v*.*.*` 形式のタグ push。
- **権限 (Permissions):** `contents: write` (Releases 作成用), `packages: read`。
- **実行内容:**
  1. タグおよび HEAD コミットの厳格検証 (`build\Resolve-GitVersion.ps1`)。ワークフロー実行時のタグ名と解決されたタグ名が不一致の場合は例外 (`Workflow tag '$env:GITHUB_REF_NAME' does not match resolved HEAD tag '$expectedTag'.`) で失敗。
  2. GitHub Packages 認証とパッケージ復元。
  3. 全テストの Release 構成での実行。
  4. Release 構成での MSI ビルド (`BlinkObserverTool.Installer.wixproj -c Release`)。
  5. `build\Test-MsiPackage.ps1` による MSI 属性・同梱ファイル検証。
  6. SHA-256 チェックサムファイル (`BlinkObserverTool.Installer.msi.sha256`) の生成。
  7. 一時的な認証ソースの確実な破棄。
  8. GitHub CLI (`gh release create`) による GitHub Release の自動発行。

> **成果物の限定方針:**  
> GitHub Release に添付するアセットは `BlinkObserverTool.Installer.msi` およびその `.sha256` ファイルの **2 ファイルのみ** です。EXE 形式のインストーラーや zip アーカイブは添付しません。

---

## 13. 障害復旧とロールバック運用

ビルドパイプラインの失敗や、リリース後の不具合発覚時の復旧手順です。

### 13.1 リリース前 (ワークフロー失敗時) の復旧

GitHub Actions の `release.yml` が途中でエラーとなり、GitHub Release が未作成のまま終了した場合:
1. ワークフローログを確認し、失敗原因 (テスト不合格、タグ構文の誤り、同梱ファイル欠落など) を特定します。
2. 作業ブランチで修正コミットを作成し、`main` ブランチへマージします。
3. リモートの誤ったタグを削除します。
   ```powershell
   git push origin :refs/tags/v1.2.3
   ```
4. ローカルのタグを削除し、最新の修正コミットに付け直します。
   ```powershell
   git tag -d v1.2.3
   git tag -a v1.2.3 -m "BlinkObserverTool v1.2.3"
   git push origin v1.2.3
   ```
> **注意:**  
> タグの削除・付け直しが許容されるのは、**MSI がまだ外部の誰にもダウンロード・配布されていない段階に限定** されます。

### 13.2 リリース後 (MSI 配布後) のロールバック方針

すでに GitHub Release が公開され、エンドユーザーにダウンロードされた可能性がある場合は、**同じバージョンタグの再利用やアセットの上書きを行ってはなりません**。

1. **修正フォワード (Fix-Forward) の徹底:**  
   不具合を修正した新しいコミットを作成し、**パッチバージョンを繰り上げた新しいタグ** (例: `v1.2.3` に不具合があった場合は `v1.2.4`) を作成して再リリースします。
2. **自動修復の恩恵:**  
   同一の `UpgradeCode` を維持して上位バージョンを発行することで、不具合のある `v1.2.3` を導入してしまったユーザーも、新しい `v1.2.4` の MSI を実行するだけで Windows Installer の Major Upgrade 機能により自動的かつ安全に修正版へ置き換わります。
3. **不具合バージョンの無効化:**  
   GitHub 上の該当 Release を編集し、タイトルの先頭に `[DEPRECATED]` を付与するか、リリース説明文に利用中止と最新版への移行を明記します。必要に応じてリリースアセットを非公開化します。

---

## 14. 未署名とコード署名境界

### 14.1 現行のセキュリティ境界

現在、本プロジェクトで出力される `BlinkObserverTool.Installer.msi` は **未署名 (Authenticode コード署名証明書未適用)** です。
- 配布される `.sha256` ファイルは、ダウンロード時の通信エラーや第三者による改ざんを検証するための「完全性保証」を提供するものです。
- 発行元身元を証明するものではないため、エンドユーザー環境で初回実行時に Windows Defender SmartScreen の警告画面が表示されます。

### 14.2 将来のコード署名導入に向けた設計境界

正式なコード署名証明書を導入する場合、以下の設計境界に従って段階的に組み込みます。
1. **ビルド工程と署名工程の分離:**  
   WiX による MSI ビルド自体は未署名で行い、後続のリリースステージで署名ツール (SignTool または Azure Trusted Signing 等) を呼び出す構成とします。
2. **秘密鍵・証明書の保護:**  
   コード署名用証明書や秘密鍵はリポジトリに配置せず、GitHub Secrets、クラウドキー管理サービス (Azure Key Vault 等)、またはハードウェアセキュリティモジュール (Hardware Security Module: HSM) 経由で安全に参照します。
3. **MSI 検証と署名順序:**  
   MSI のビルド -> `Test-MsiPackage.ps1` による整合性検証 -> デジタル署名実行 -> SHA-256 チェックサム算出 -> GitHub Release 作成の順序でパイプラインを構成します。

---

## 15. リリース運用チェックリスト

リリースの準備から完了までの確認用チェックリストです。

### 15.1 リリース前準備

- [ ] `main` ブランチにすべての変更がマージされ、直近の CI がグリーン (成功) であることを確認した。
- [ ] 同梱プロファイルを変更した場合は、`manifest.json` の SHA-256 ハッシュ値と `manifest.version` が更新されていることを確認した。
- [ ] 依存する `GenericRecognition.Workbench.*` の正式版 (例: `0.1.7`) が GitHub Packages 上に存在し、アクセス可能であることを確認した。
- [ ] ローカル環境で未コミットの変更がないことを確認した (`git status --short` が空)。

### 15.2 リリース実行

- [ ] ローカルの最新 `main` ブランチでバージョン解決テストを実行した。
  ```powershell
  .\build\tests\Resolve-GitVersion.Tests.ps1
  ```
- [ ] 厳格な形式で注釈付き Git タグを作成した。
  ```powershell
  git tag -a v1.2.3 -m "BlinkObserverTool v1.2.3"
  ```
- [ ] Release 構成のバージョン解決結果を確認した。
  ```powershell
  .\build\Resolve-GitVersion.ps1 -RepositoryPath . -Configuration Release -OutputFormat Json
  ```
- [ ] タグのみをリモートへ push した。
  ```powershell
  git push origin v1.2.3
  ```

### 15.3 リリース後確認

- [ ] GitHub Actions の `Release` ワークフローが正常に完了したことを確認した。
- [ ] GitHub Releases ページに `BlinkObserverTool v1.2.3` が作成されていることを確認した。
- [ ] リリースアセットに以下の 2 ファイルのみが含まれていることを確認した。
  - `BlinkObserverTool.Installer.msi`
  - `BlinkObserverTool.Installer.msi.sha256`
- [ ] 配布用 MSI をクリーンな Windows 環境にインストールし、スタートメニューのショートカットおよびアプリが正常起動することを確認した。

---

## 16. 関連ドキュメント・参照先

- [BlinkObserverTool 利用者向けインストーラー導入マニュアル](user-installer-manual.ja.md)
- [インストーラー方式の選定と設計判断](installer-decision.md)
- [WiX インストーラープロジェクト概要 (installer\README.md)](../installer/README.md)
- [汎用型自動認識ツールの公開とホスト作成手順 (GenericRecognitionWorkbench)](https://github.com/p-o-ke-nae/GenericRecognitionWorkbench/blob/main/doc/publish-and-host-guide.md)
- [WiX Toolset v4 公式ドキュメント](https://docs.firegiant.com/wix/)
- [Microsoft Windows Installer ProductVersion 規定](https://learn.microsoft.com/windows/win32/msi/productversion)
- [Semantic Versioning 2.0.0 仕様書](https://semver.org/lang/ja/)
