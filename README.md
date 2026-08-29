# KeySecBox

![构建](https://img.shields.io/badge/Windows-x64-blue?logo=data:image/svg%2bxml;base64,PHN2ZyB0PSIxNzg2ODcyNzIwMTU2IiBjbGFzcz0iaWNvbiIgdmlld0JveD0iMCAwIDEwMjQgMTAyNCIgdmVyc2lvbj0iMS4xIiB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHAtaWQ9IjM3NzMiIHdpZHRoPSIyMDAiIGhlaWdodD0iMjAwIj48cGF0aCBkPSJNNDE5Ljg0IDU0MC4xNnY0MDAuMzg0TDAgODgzLjJ2LTM0Mi41MjhoNDE5Ljg0eiBtMC00NTcuMjE2djQwNS41MDRIMFYxNDAuOGw0MTkuODQtNTcuODU2eiBtNjA0LjE2IDQ1Ny4yMTZWMTAyNEw0NjUuOTIgOTQ3LjJ2LTQwNi41MjhoNTU4LjA4ek0xMDI0IDB2NDg4LjQ0OEg0NjUuOTJWNzYuOEwxMDI0IDB6IiBmaWxsPSIjZmZmZmZmIiBwLWlkPSIzNzc0Ij48L3BhdGg+PC9zdmc+)
![版本](https://img.shields.io/github/v/release/XKPU/KeySecBox?logo=github)
![许可证](https://img.shields.io/github/license/XKPU/KeySecBox?logo=data:image/svg+xml;base64,PHN2ZyB0PSIxNzg2ODczMTA1MjgwIiBjbGFzcz0iaWNvbiIgdmlld0JveD0iMCAwIDEwMjQgMTAyNCIgdmVyc2lvbj0iMS4xIiB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHAtaWQ9IjY2MzEiIHdpZHRoPSIyMDAiIGhlaWdodD0iMjAwIj48cGF0aCBkPSJNNTEyIDE2QzIzOC4wNjYgMTYgMTYgMjM4LjA2NiAxNiA1MTJzMjIyLjA2NiA0OTYgNDk2IDQ5NiA0OTYtMjIyLjA2NiA0OTYtNDk2Uzc4NS45MzQgMTYgNTEyIDE2eiBtMCA4OTZjLTIyMS4wNjQgMC00MDAtMTc4LjkwMi00MDAtNDAwIDAtMjIxLjA2MiAxNzguOTAyLTQwMCA0MDAtNDAwIDIyMS4wNjQgMCA0MDAgMTc4LjkwMiA0MDAgNDAwIDAgMjIxLjA2NC0xNzguOTAyIDQwMC00MDAgNDAweiBtMjE0LjcwMi0yMDIuMTI4Yy0xOS4yMjggMTkuNDI0LTkxLjA2IDgyLjc5Mi0yMDguMTMgODIuNzkyLTE2NC44NiAwLTI4MC45NjgtMTIyLjg1LTI4MC45NjgtMjgzLjEzNCAwLTE1OC4zMDQgMTIwLjU1LTI3OC44MDIgMjc5LjUyNC0yNzguODAyIDExMS4wNjIgMCAxNzcuNDc2IDUzLjI0IDE5NS4xODYgNjkuNTU4YTIzLjkzIDIzLjkzIDAgMCAxIDMuODcyIDMwLjY0NGwtMzYuMzEgNTYuMjI2Yy03LjY4MiAxMS45LTIzLjkzMiAxNC41NjQtMzQuOTk4IDUuODQyLTE3LjE5LTEzLjU1Mi02My42MjgtNDUuMDc2LTEyMy40MTYtNDUuMDc2LTk2LjYwNiAwLTE1NS44MzIgNzAuNjYtMTU1LjgzMiAxNjAuMTY0IDAgODMuMTc4IDUzLjc3NiAxNjcuMzg0IDE1Ni41NTQgMTY3LjM4NCA2NS4zMTQgMCAxMTMuNjg2LTM4LjA3OCAxMzEuNDUyLTU0LjQ1IDEwLjU0LTkuNzE0IDI3LjE5Mi04LjA3OCAzNS42NCAzLjQ3NmwzOS43MyA1NC4zNGEyMy44OTQgMjMuODk0IDAgMCAxLTIuMzA0IDMxLjAzNnoiIGZpbGw9IiNmZmZmZmYiIHAtaWQ9IjY2MzIiPjwvcGF0aD48L3N2Zz4=)

本地优先的 Windows 密码保险库，支持保存恢复密钥。

- **本地优先**：所有数据存放在程序目录的 `data\` 下，无云端、无账号，拷贝文件夹即可迁移。
- **纯托管实现**：UI 与核心逻辑全部使用 C# / .NET 8 + WinUI 3（Windows App SDK 2.0.1），加密直接调用 .NET 的 `AesGcm` 与 `Rfc2898DeriveBytes`，DPAPI 通过 P/Invoke 调用，不再依赖原生 C++ DLL。

## 目录

- [功能特性](#功能特性)
- [运行需求](#运行需求)
- [安全设计](#安全设计)
- [数据操作](#数据操作)
- [构建指南](#构建指南)
- [运行时目录](#运行时目录)
- [仓库结构](#仓库结构)
- [许可证](#许可证)

## 功能特性

- **主密码保险库**：首次运行设置主密码，之后每次启动解锁；忘记密码时可通过备用密码或 Windows 账户找回。
- **分类管理**：新建 / 重命名 / 删除分类，支持上下移动调整分类显示顺序。
- **条目管理**：新增 / 编辑 / 删除条目（账号、密码、备注），一键复制密码到剪贴板。
- **搜索与视图**：按备注或账号实时过滤，「全部」视图与分类视图切换，支持在「全部」视图固定条目顺序。
- **恢复密钥**：服务层支持为条目保存多组恢复密钥，密钥随条目一并加密存储。
- **旧版迁移**：启动检测到 KSX3 旧库时，引导输入旧主密码并把旧数据合并进当前库。
- **外观设置**：主题（跟随系统 / 浅色 / 深色）、动画帧率，窗口位置自动记忆。

## 运行需求

| 发布形态 | 系统要求 | 说明 |
| --- | --- | --- |
| Framework（框架依赖） | .NET 8 Runtime + Windows App SDK >= 2.0.1 | 体积小，需目标机预装运行时 |
| SelfContained（自包含） | 无额外要求 | 内置 .NET 与 Windows App SDK，开箱即用 |

## 安全设计

| 关键环节 | 技术方案 | 详细说明 |
| --- | --- | --- |
| 静态加密 | AES-256-GCM | 基于 .NET `AesGcm`，每条密文配随机 12B Nonce + 16B 认证标签 |
| 密钥派生 | PBKDF2-HMAC-SHA256 | 32 字节密钥，16 字节随机盐，600000 次迭代 |
| 随机源 | `RandomNumberGenerator.Fill` | 调用系统 CSPRNG（Windows CNG），确保密码学安全随机性 |
| 内存清理 | `CryptographicOperations.ZeroMemory` | 派生的中间密钥与口令缓冲使用后立即清零 |
| 密码找回 | 备用密码 / DPAPI | 备用密码经 PBKDF2 + AES-GCM 包裹主密码，DPAPI 绑定当前 Windows 账户 |
| 原子写入 | 临时文件 + 替换 | 保险库文件写入失败不会破坏已有数据 |
| 存储格式 | KSX4 六文件体系 | 魔数 `KSXM` / `KSXE` / `KSXR`，密码与恢复密钥逐条加密 |
| 诊断日志 | 脱敏记录 | 仅记录操作名 / 计数 / 返回码，绝不包含密码等机密信息 |

## 数据操作

- **通用格式**：`CsvService` 已实现 RFC4180 兼容的导入 / 导出，表头为 `Account,Password,Note,Categories`，多个分类用分号分隔。
- **旧版迁移**：启动时自动识别 KSX3 旧库（`vault.settings` / `vault.index` / `vault.data` / `vault.recovery`），验证旧主密码后合并到当前库。
- **安全入库**：条目密码在核心服务层逐条 AES-GCM 加密后写入，明文不落盘。

> 说明：CSV 导入导出、条目恢复密钥、修改主密码等能力已在服务层实现，对应的 UI 入口仍在补齐中。

## 构建指南

### 环境准备

- .NET 8 SDK
- Visual Studio 2022 或更高版本（含 WinUI / Windows 应用开发工作负载；Release 自包含发布合并 PRI 时需要 Windows SDK 的 `makepri.exe`）

### 脚本构建（推荐）

```bat
::: Debug 模式构建
build-amd64.bat Debug

::: Release 模式构建（同时产出框架依赖版与自包含版）
build-amd64.bat Release
```

脚本会自动定位 `VsDevCmd.bat`（找不到时仍会继续构建），清理当前配置的输出，并按配置执行构建或发布。

### 手动分步构建

```bat
::: 1. 就地构建（Debug）
dotnet build KeySecBox.csproj -c Debug -p:Platform=x64

::: 2. 发布自包含版（先发布到 TMP 暂存目录）
dotnet publish KeySecBox.csproj -c Release -r win-x64 ^
  --self-contained true ^
  -p:Platform=x64 -p:WindowsAppSDKSelfContained=true ^
  -p:OutputPath=bin\Release\x64\TMP\build ^
  -o bin\Release\x64\TMP

::: 3. 发布框架依赖版
dotnet publish KeySecBox.csproj -c Release -r win-x64 ^
  --self-contained false ^
  -p:Platform=x64 -p:WindowsAppSDKSelfContained=false ^
  -p:OutputPath=bin\Release\x64\TMP\build ^
  -o bin\Release\x64\framework
```

自包含版没有已注册的 framework 包，需把 WinAppSDK 的 `.pri` 合并进 `KeySecBox.pri`；随后在 TMP 中剔除多余目录（仅保留 `en-us`、`zh-CN`、`Microsoft.UI.Xaml`），再把剩余文件复制到 `bin\Release\x64\selfcontained`。以上步骤 `build-amd64.bat` 已自动完成。

`OutputPath` 的作用是把 publish 过程中的中间编译输出（各语言附属程序集等）一并留在 TMP 内，否则 `bin\Release\x64` 根目录会被这些中间产物污染。

### 产物说明

构建产物位于 `bin\<Configuration>\x64\`：

| 目录 | 类型 | 说明 |
| --- | --- | --- |
| （根目录） | 就地构建 | Debug 模式输出 `KeySecBox.exe`，适合开发调试 |
| `framework\` | 框架依赖 | 需目标机安装 .NET 8 与 Windows App SDK 运行时 |
| `selfcontained\` | 自包含 | 文件夹部署模式，未启用单文件解压 |
| `TMP\` | 发布暂存 | 自包含版在此发布、合并 PRI、剔除多余目录后复制到 `selfcontained\`；同时承接中间编译输出，构建结束即删除 |

## 运行时目录

程序运行时数据统一存储在程序目录下的 `data\` 文件夹：

| 文件名 | 用途 | 备注 |
| --- | --- | --- |
| `vault.prefs` | 偏好设置 | 明文 JSON（如诊断开关） |
| `vault.master` | 校验块 + KDF 参数 | 二进制 `KSXM`；含盐与迭代次数 |
| `vault.cats` | 分类 | 明文 JSON，分类名 + ID，数组序 = 显示序 |
| `vault.entries` | 条目记录 | 二进制 `KSXE`；仅密码字段 AES-GCM 加密，ID / 账号 / 备注为明文 |
| `vault.map` | 条目关联 | 明文 JSON：分类↔条目关联、分类内条目序、计数器、全部视图排序覆盖 |
| `vault.recovery` | 条目恢复密钥 | 二进制 `KSXR`；ID 明文，密钥内容加密 |
| `master.recovery` | 主密码找回记录 | `KSXRv2`；备用密码 / DPAPI 包裹，可整体删除以停用找回 |
| `appconfig.json` | UI 偏好 | 主题、窗口位置、动画帧率 |
| `crash.log` | 崩溃日志 | 仅在未处理异常时生成 |
| `vault.diag.log` | 核心诊断日志 | 仅诊断模式开启时写入，已脱敏 |

备份与迁移：直接拷贝整个 `data\` 文件夹即可完成完整备份或跨设备迁移。

## 仓库结构

```
KeySecBox/
├─ KeySecBox.sln                    Visual Studio 解决方案
├─ KeySecBox.csproj                 WinUI 3 工程文件（根目录，源码限定为 src\）
├─ build-amd64.bat                  一键构建脚本（Debug / Release）
├─ version.txt                      版本号（注入程序集）
├─ .vscode/                         VS Code 运行与调试配置（launch.json / tasks.json）
├─ LICENSE                          AGPL-3.0 许可证
└─ src/
   ├─ app.manifest                  应用清单（DPI 感知等）
   ├─ Program.cs                    应用入口
   ├─ App.xaml / App.xaml.cs        全局应用、DI 容器与未处理异常处理
   ├─ AppPaths.cs                   运行时路径与 data\ 布局
   ├─ MainWindow.xaml(.cs)          主窗口
   ├─ MainWindow.Category.cs        分类相关交互
   ├─ MainWindow.Entry.cs           条目相关交互
   ├─ Models/                       数据模型（条目、分类、配置、错误码等）
   ├─ Services/                     核心服务
   │  ├─ CryptoService.cs           加密原语（PBKDF2 / AES-GCM / CSPRNG）
   │  ├─ BinaryFormatService.cs     KSX4 二进制格式编解码
   │  ├─ JsonSerializationService.cs 明文元数据的 JSON 序列化
   │  ├─ FileIOService.cs           原子化文件读写
   │  ├─ VaultService*.cs           保险库核心（分类 / 条目 / 持久化 / 查询 / 生命周期）
   │  ├─ LegacyVaultService.cs      KSX3 旧库读取与迁移
   │  ├─ MasterRecoveryService.cs   主密码找回（备用密码 / DPAPI）
   │  ├─ RecoveryService.cs         条目恢复密钥
   │  ├─ CsvService.cs              RFC4180 CSV 导入导出
   │  ├─ AppConfigurationService.cs UI 偏好
   │  ├─ ClipboardService.cs        剪贴板
   │  └─ DiagnosticService.cs       脱敏诊断日志
   ├─ Platforms/Native/
   │  └─ DpapiNative.cs             DPAPI (CryptProtectData) P/Invoke 绑定
   ├─ Helpers/
   │  ├─ Converters.cs              数据绑定转换器
   │  └─ DialogAnim.cs              对话框动画
   └─ Views/
      ├─ UnlockDialog.xaml(.cs)          解锁 / 初始化主密码
      ├─ EntryDialog.xaml(.cs)           条目编辑
      ├─ ForgotPasswordDialog.xaml(.cs)  主密码找回
      └─ SettingsDialog.xaml(.cs)        设置（主题 / 动画帧率）
```

## 许可证

本项目基于 [GNU Affero General Public License v3.0](LICENSE) 开源。
