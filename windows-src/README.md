# windows-src —— Windows 版（Caelus Studio.exe）源码

这个目录放 **VB.NET + WPF** 的 Windows 客户端工程，由 GitHub Actions 的 Windows runner 编译，
产物自动提交到本仓库 `Update/Caelus Studio.exe`（= download.caelus.top 上的下载文件）。

## 为什么放这里

Windows 版是 `.NET Framework 4.8` 的 WPF 工程，**macOS 上编不了**（WPF 的编译目标只有 Windows 的 MSBuild 有）。
放在本仓库是为了让 Actions 能用同一份源码、并把产物直接落到下载目录，不依赖任何人的本机环境。

## 放什么

把 VS 工程里最内层的那个 `Caelus Studio` 目录的**内容**平铺进来（含 `My Project/`）：

```
windows-src/
  Caelus Studio.vbproj
  Application.xaml / .xaml.vb
  MainWindow.xaml / .xaml.vb
  HomePage.xaml / .xaml.vb
  DownloadPage / DownloadCard / ShopPage / ShopCard
  SettingsPage / AboutPage / StarIdSetupWindow / UpdateWindow
  TreeOSSource.vb / StarIDSource.vb / AppUpdater.vb
  RoundedWindow.vb / ProgressConverters.vb
  logo.ico / app.manifest / App.config
  author-croc.jpg / author-orange.jpg
  My Project/…
```

**不要**放 `bin/`、`obj/`、`.vs/`、`.build-check/`。

## 触发编译

- 手动：仓库 **Actions → Build Windows app → Run workflow**
- 自动：改动 `windows-src/**` 推到 `main` 后自动跑

编译过程：`msbuild "Caelus Studio.vbproj" /p:Configuration=Release /p:Platform=AnyCPU`。
结束后产物会 commit 回 `Update/Caelus Studio.exe`（无变化则跳过提交），同时可在 Artifacts 里直接下载。

## 注意

- 本仓库是 **公开** 的（GitHub Pages 服务 download.caelus.top），源码放进来等于公开源码。
  如果不想公开，改放到私有仓库并配 PAT 往这里推。
- 旧目录 `Updat/`（少个 e）是历史遗留的重复副本，可删。
- 版本号：更新逻辑在 `TreeOSSource.vb` 的 `LocalAppVersion`，与云端 `app_config` 的 `latest_version` 比对；
  发新版记得一起改。
