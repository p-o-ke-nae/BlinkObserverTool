# MSI インストーラー方式の選定

## 結論

BlinkObserverTool のインストーラーは、当面 **WiX Toolset v4 系の
SDK-styleプロジェクト**を継続利用する。

採用条件は次のとおり。

- 配布物が単体のMSIである
- 無償でビルドできる
- Visual Studioと`dotnet build`の両方から同じプロジェクトをビルドできる
- 設定とインストール定義をテキストとしてレビューできる
- Major Upgrade、per-userインストール、ダウングレード拒否を明示できる
- GitHub Actionsで非対話ビルドできる

WiXの新しいメジャー版へ更新する場合は、互換性だけでなく、その時点の
ライセンス、無償利用条件、Visual Studio拡張の対応を再確認する。WiX v6以降は
Open Source Maintenance Feeが導入されており、WiX v7では一定の収益額に達する
まで費用を要求しない条件が示されている。このリポジトリでは、無償利用条件を
確認せずに自動更新しない。

Windows Installerの`ProductVersion`は`major.minor.build`の3フィールドだけを
比較し、上限は順に255、255、65,535である。GitタグのSemVerはこの範囲を
リリース前に検証し、第4フィールドやプレリリース表現へ更新順序を依存させない。

## 比較

| 候補 | MSI | Visual Studio | テキスト/CI | 更新制御 | 判定 |
|---|---:|---|---|---|---|
| WiX Toolset SDK | Yes | HeatWave Community Editionでプロジェクト、テンプレート、プロパティページ、Build/Rebuild/Cleanを利用可能 | `.wixproj`/`.wxs`とMSBuild | Major Upgradeを明示可能 | 採用 |
| Visual Studio Installer Projects | Yes | GUIオーサリングに優れる | `.vdproj`は自動生成・レビュー・CLI共通化に不向き | 基本的な更新に限定 | 不採用 |
| MSIX | No | 良好 | 良好 | MSIXの更新モデル | MSI必須条件を満たさない |
| Velopack | No | CLI/MSBuild中心 | 良好 | アプリ内更新向け | MSI必須条件を満たさない |
| GUI専用の無償MSI編集ツール | Yes | 製品による | GUI生成物とWiX定義の二重管理になる | 製品による | 正本には不採用 |

Visual Studio Installer Projectsは、Microsoftの案内どおりWPF/.NETの
`Publish Items`とpublish profileを扱える。しかし、GUI専用定義を別の正本に
すると、AI/CLI向けの宣言的設定と二重管理になるため採用しない。

## Visual Studioで保証する操作範囲

HeatWave Community Editionを使い、次をVisual Studioから実行できる状態を
サポート対象とする。

- インストーラープロジェクトをソリューション内で開く
- プロジェクトプロパティを確認する
- NuGet Package ManagerでWiX拡張を確認する
- Build、Rebuild、Cleanを実行する
- エラー一覧とビルド出力から設定エラーを確認する

製品固有値はJSON Schema付きの設定ファイルを唯一の正本とする。専用の
ドラッグ&ドロップ画面とJSONを双方向同期する仕組みは設けない。

## 再評価条件

次のいずれかが発生した場合は選定をやり直す。

- 利用中のWiX版またはHeatWaveが、使用するVisual Studio/.NET SDKをサポートしない
- 無償利用条件を満たさなくなる
- MSI以外の配布形式を許容する
- 自動更新がMSI管理より優先される
- GUIオーサリングをテキストでの差分管理より優先する

## 参照

- [WiX Toolset - What's new](https://docs.firegiant.com/wix/whatsnew/)
- [HeatWave for Visual Studio](https://marketplace.visualstudio.com/items?itemName=FireGiant.FireGiantHeatWaveDev17)
- [Visual Studio Installer Projects and .NET](https://learn.microsoft.com/visualstudio/deployment/installer-projects-net-core)
- [Windows Installer ProductVersion](https://learn.microsoft.com/windows/win32/msi/productversion)
- [Windows Installer Major Upgrades](https://learn.microsoft.com/windows/win32/msi/major-upgrades)
