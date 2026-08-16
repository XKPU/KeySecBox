# KeySecBox

本地优先的 Windows 密码保险库，支持保存恢复密钥。

- **本地优先**：所有数据存放在程序目录的 `data\` 下，无云端、无账号，拷贝文件夹即可迁移。
- **混合核心**：密码、保险库结构与加密由原生 C++ DLL（`KeySecBox.DLL`）实现，UI 通过 P/Invoke 调用。

![构建](https://img.shields.io/badge/Windows-x64-blue?logo=data:image/svg%2bxml;base64,PHN2ZyB0PSIxNzg2ODcyNzIwMTU2IiBjbGFzcz0iaWNvbiIgdmlld0JveD0iMCAwIDEwMjQgMTAyNCIgdmVyc2lvbj0iMS4xIiB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHAtaWQ9IjM3NzMiIHdpZHRoPSIyMDAiIGhlaWdodD0iMjAwIj48cGF0aCBkPSJNNDE5Ljg0IDU0MC4xNnY0MDAuMzg0TDAgODgzLjJ2LTM0Mi41MjhoNDE5Ljg0eiBtMC00NTcuMjE2djQwNS41MDRIMFYxNDAuOGw0MTkuODQtNTcuODU2eiBtNjA0LjE2IDQ1Ny4yMTZWMTAyNEw0NjUuOTIgOTQ3LjJ2LTQwNi41MjhoNTU4LjA4ek0xMDI0IDB2NDg4LjQ0OEg0NjUuOTJWNzYuOEwxMDI0IDB6IiBmaWxsPSIjZmZmZmZmIiBwLWlkPSIzNzc0Ij48L3BhdGg+PC9zdmc+)
![版本](https://img.shields.io/github/v/release/XKPU/KeySecBox?logo=github)
![许可证](https://img.shields.io/github/license/XKPU/KeySecBox?logo=data:image/svg+xml;base64,PHN2ZyB0PSIxNzg2ODczMTA1MjgwIiBjbGFzcz0iaWNvbiIgdmlld0JveD0iMCAwIDEwMjQgMTAyNCIgdmVyc2lvbj0iMS4xIiB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHAtaWQ9IjY2MzEiIHdpZHRoPSIyMDAiIGhlaWdodD0iMjAwIj48cGF0aCBkPSJNNTEyIDE2QzIzOC4wNjYgMTYgMTYgMjM4LjA2NiAxNiA1MTJzMjIyLjA2NiA0OTYgNDk2IDQ5NiA0OTYtMjIyLjA2NiA0OTYtNDk2Uzc4NS45MzQgMTYgNTEyIDE2eiBtMCA4OTZjLTIyMS4wNjQgMC00MDAtMTc4LjkwMi00MDAtNDAwIDAtMjIxLjA2MiAxNzguOTAyLTQwMCA0MDAtNDAwIDIyMS4wNjQgMCA0MDAgMTc4LjkwMiA0MDAgNDAwIDAgMjIxLjA2NC0xNzguOTAyIDQwMC00MDAgNDAweiBtMjE0LjcwMi0yMDIuMTI4Yy0xOS4yMjggMTkuNDI0LTkxLjA2IDgyLjc5Mi0yMDguMTMgODIuNzkyLTE2NC44NiAwLTI4MC45NjgtMTIyLjg1LTI4MC45NjgtMjgzLjEzNCAwLTE1OC4zMDQgMTIwLjU1LTI3OC44MDIgMjc5LjUyNC0yNzguODAyIDExMS4wNjIgMCAxNzcuNDc2IDUzLjI0IDE5NS4xODYgNjkuNTU4YTIzLjkzIDIzLjkzIDAgMCAxIDMuODcyIDMwLjY0NGwtMzYuMzEgNTYuMjI2Yy03LjY4MiAxMS45LTIzLjkzMiAxNC41NjQtMzQuOTk4IDUuODQyLTE3LjE5LTEzLjU1Mi02My42MjgtNDUuMDc2LTEyMy40MTYtNDUuMDc2LTk2LjYwNiAwLTE1NS44MzIgNzAuNjYtMTU1LjgzMiAxNjAuMTY0IDAgODMuMTc4IDUzLjc3NiAxNjcuMzg0IDE1Ni41NTQgMTY3LjM4NCA2NS4zMTQgMCAxMTMuNjg2LTM4LjA3OCAxMzEuNDUyLTU0LjQ1IDEwLjU0LTkuNzE0IDI3LjE5Mi04LjA3OCAzNS42NCAzLjQ3NmwzOS43MyA1NC4zNGEyMy44OTQgMjMuODk0IDAgMCAxLTIuMzA0IDMxLjAzNnoiIGZpbGw9IiNmZmZmZmYiIHAtaWQ9IjY2MzIiPjwvcGF0aD48L3N2Zz4=)

## 目录

- [运行需求](#运行需求)
- [安全设计](#安全设计)
- [导入导出](#导入导出)
- [构建指南](#构建指南)
- [运行时目录](#运行时目录)
- [仓库结构](#仓库结构)
- [许可证](#许可证)

## 运行需求

| 发布形态 | 系统要求 | 说明 |
| --- | --- | --- |
| Framework（框架依赖） | .NET 8 Runtime + Windows App SDK >= 2.0.1 | 体积小，需目标机预装运行时 |
| SelfContained（自包含） | 无额外要求 | 内置 .NET 与 Windows App SDK，开箱即用 |

## 安全设计

| 关键环节 | 技术方案 | 详细说明 |
| --- | --- | --- |
| 静态加密 | AES-256-GCM | 基于 BCrypt API，每条密文配随机 12B Nonce + 16B 认证标签 |
| 密钥派生 | PBKDF2-HMAC-SHA256 | 32 字节密钥，16 字节随机盐，抗暴力破解 |
| 随机源 | BCryptGenRandom | 调用 Windows CSPRNG，确保密码学安全随机性 |
| 密码找回 | 备用密码 / DPAPI | 备用密码经 AES-GCM 包裹；DPAPI 绑定当前 Windows 账户（PIN / 指纹 / 人脸） |
| 存储格式 | KSX3 多文件体系 | 索引整块加密，条目逐条加密；追加写 + 墓碑机制，支持增量保存 |
| 诊断日志 | 脱敏记录 | 仅记录操作名 / 计数 / 返回码，绝不包含密码等机密信息 |

机密明文策略：列表、搜索、分类仅读取加密索引，不解密条目内容；密码、恢复密钥等敏感数据使用后立即 `Array.Clear` 或内存零化，不在内存中长期驻留。

## 导入导出

- **通用格式**：支持标准 CSV（RFC4180）导入 / 导出，提供表头列名映射（`name`、`url`、`username`、`password`、`note` 等）。
- **浏览器兼容**：兼容 Microsoft Edge 导出的网站密码 CSV。
- **安全入库**：导入时可选择目标分类，密码在 C++ 核心层逐条加密后写入，明文不经过 UI 层。

## 构建指南

### 环境准备

- Visual Studio（勾选「使用 C++ 的桌面开发」工作负载）
- .NET 8 SDK

注意：C++ 核心依赖 MSVC，因此环境本身必须安装 Visual Studio 或 Build Tools。

### 脚本构建（推荐）

适用于已安装 Visual Studio 的环境。先检查 `build_amd64.bat` 中 `VsDevCmd.bat` 的路径是否正确，然后运行：

```bat
:: Debug 模式构建
build_amd64.bat Debug

:: Release 模式构建（同时产出框架依赖版与自包含版）
build_amd64.bat Release
```

### 手动分步构建

适用于已安装 Visual Studio、需自定义编译参数的场景。

```bat
:: 1. 构建 C++ 核心 -> bin\Release\x64\KeySecBox.DLL.dll
cmake -S . -B build -G "Visual Studio 18 2026" -A x64
cmake --build build --target KeySecBox.DLL --config Release

:: 2. 构建 UI（就地调试）
dotnet build src\ui\KeySecBox.UI.csproj -c Release -p:Platform=x64

:: 3. 发布框架依赖版
dotnet publish src\ui\KeySecBox.UI.csproj -c Release -r win-x64 ^
  --self-contained false ^
  -p:Platform=x64 -p:WindowsAppSDKSelfContained=false ^
  -o bin\Release\x64\framework

:: 4. 发布自包含版
dotnet publish src\ui\KeySecBox.UI.csproj -c Release -r win-x64 ^
  --self-contained true ^
  -p:Platform=x64 -p:WindowsAppSDKSelfContained=true ^
  -o bin\Release\x64\selfcontained

:: 自行将 KeySecBox.DLL.dll 拷贝到 UI 目录下
```

### 产物说明

构建产物位于 `bin\<Configuration>\x64\`：

| 目录 | 类型 | 说明 |
| --- | --- | --- |
| `exe\` | 就地构建 | `KeySecBox.UI.exe` + `KeySecBox.DLL.dll`，适合开发调试 |
| `framework\` | 框架依赖 | 需目标机安装 .NET 8 与 Windows App SDK 运行时 |
| `selfcontained\` | 自包含 | 文件夹部署模式，未启用单文件解压 |

## 运行时目录

程序运行时数据统一存储在程序目录下的 `data\` 文件夹：

| 文件名 | 用途 | 备注 |
| --- | --- | --- |
| `vault.settings` | 保险库配置 | 加密存储 |
| `vault.index` | 条目索引 | 整块加密，支持快速搜索 |
| `vault.data` | 条目数据 | 逐条加密，追加写入 |
| `vault.tomb` | 墓碑文件 | 记录已删除条目 |
| `vault.recovery` | 恢复密钥 | 备用密码或 DPAPI 包裹 |
| `appconfig.json` | UI 偏好 | 主题、窗口位置等 |
| `crash.log` | 崩溃日志 | 仅在未处理异常时生成 |
| `trace.log` | UI 跟踪日志 | 仅诊断模式开启时写入 |
| `vault.diag.log` | 核心诊断日志 | 仅诊断模式开启时写入，已脱敏 |

备份与迁移：直接拷贝整个 `data\` 文件夹即可完成完整备份或跨设备迁移。

## 仓库结构

```
KeySecBox/
├─ CMakeLists.txt                   C++ 核心构建配置
├─ KeySecBox.DLL.vcxproj            C++ 工程文件（CMake 生成）
├─ KeySecBox.sln                    Visual Studio 解决方案
├─ build_amd64.bat                  一键构建脚本
├─ LICENSE                          AGPL-3.0 许可证
└─ src/
   ├─ keysecbox.h                   C API 定义
   ├─ crypto.h / crypto.cpp         加密原语
   ├─ json.h / json.cpp             轻量 JSON 解析
   ├─ internal.h                    内部共用定义
   ├─ vault.cpp                     保险库核心
   ├─ persist.cpp                   存储层
   ├─ format.cpp                    数据格式与序列化
   ├─ diag.cpp                      诊断日志
   └─ ui/                           WinUI 3 前端
      ├─ KeySecBox.UI.csproj
      ├─ app.manifest
      ├─ Program.cs                 应用入口
      ├─ App.xaml / App.xaml.cs     全局应用与未处理异常处理
      ├─ AppPaths.cs                运行时路径与 data\ 布局
      ├─ AppSettings.cs             偏好设置
      ├─ NativeMethods.cs           P/Invoke 绑定
      ├─ RecoveryManager.cs         主密码找回
      ├─ MainWindow.xaml(.cs)       主窗口
      ├─ *Dialog.xaml(.cs)          解锁 / 条目 / 分类 / 导入导出 / 找回 / 设置等对话框
      ├─ Csv.cs                     极简 RFC4180 CSV 解析
      ├─ Converters.cs              数据绑定转换器
      └─ DialogAnim.cs              对话框动画
```

## 许可证

本项目基于 [GNU Affero General Public License v3.0](LICENSE) 开源。
