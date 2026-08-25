# PSB/PSP动态立绘播放器

English: **PSB/PSP Dynamic Character Player**

一个基于 FreeMoteViewer / FreeMote 运行时的 Windows 桌面小工具，用来切换和调试 `.PSP / .PSB` 动态立绘。

## 功能

- 自动读取指定文件夹中的 `.PSP / .PSB` 模型，过滤小于 10KB 的 `.json` 旁路文件
- 自动识别 PS4 平台 `.PSB`，转换为 Win 平台 + RGBA8 普通像素格式后播放；转换文件保存在原文件旁边（如 `xxx.ps4.psb` → `xxx.ps4.win.psb`）
- 支持同时显示多个模型
- 支持重复添加相同模型，并自动编号（`#2`、`#3`）
- 支持通过左侧“框”点击切换当前模型
- 支持双击模型库开启、双击已添加模型关闭
- 支持拖动框调整图层前后顺序
- 每个模型可独立暂停 / 恢复，也可全部暂停 / 恢复
- 支持背景图片 / 背景视频
- 支持背景显示模式：自适应、拉伸、裁剪、原始大小
- 支持主题颜色切换和自定义主题色
- 支持缩放（1% ~ 400%）、拖动立绘
- 支持模型文件夹切换，并记住上次目录
- 支持启动后自动刷新清晰度

## 环境要求

- Windows 10/11
- .NET Framework 4.8
- .NET SDK（建议 9.0+）用于编译
- FreeMoteViewer 的 `lib` 目录中相关 FreeMote DLL

## 构建

默认 `FreeMoteDir` 指向本机的：

```
D:\test\galgame\解包工具\FreeMoteViewer\lib
```

如果你在其他机器上构建，请把 FreeMoteViewer 的 `lib` 内容放到本仓库 `lib` 目录，或者指定路径：

```powershell
dotnet build -c Release
# 或
dotnet build -c Release -p:FreeMoteDir="D:\你的路径\FreeMoteViewer\lib"
```

运行：

已把 FreeMote DLL 放到本仓库 `lib` 目录时：

```powershell
dotnet run -c Release
```

如果需要指定 FreeMote 路径，运行和构建都要带上同一个参数：

```powershell
dotnet run -c Release -p:FreeMoteDir="D:\你的路径\FreeMoteViewer\lib"
```

或者直接运行构建出的程序：

```
bin\Release\net48\PSBPSPDynamicCharacterPlayer.exe
```

## 直接运行

仓库内的 `dist` 目录已经包含可直接运行的版本：

```
dist\PSBPSPDynamicCharacterPlayer.exe
```

所有 DLL 必须和 exe 放在同一目录，不要单独移动 exe。

## 目录说明

```
PSBPSPDynamicCharacterPlayer.csproj   项目文件
App.xaml / App.xaml.cs  程序入口
MainWindow.xaml          界面布局
MainWindow.xaml.cs       核心逻辑
dist                      可直接运行的 exe + 依赖 DLL
```

运行时会在 exe 同目录生成：

- `model_folder.txt`：记住上次打开的模型文件夹
- `theme.txt`：记住上次使用的主题

## 说明

本项目依赖 FreeMote 相关库，FreeMote 版权归原作者 Ulysses 所有，使用时请遵守 FreeMote 的 LICENSE。
