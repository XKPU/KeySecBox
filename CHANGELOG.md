# Changelog

## [v1.2.0](https://github.com/XKPU/KeySecBox/compare/v1.1.2...main) - 2026-11-20

### 新增

- 空闲自动锁定：设置 → 安全 → 自动锁定可设置分钟数（默认 5，填 0 关闭）。超时后核心擦除派生密钥、BCrypt 句柄与内存明文，界面清空列表并重新要求输入主密码。
- 隐藏文件 / 文件夹：用「隐藏 + 系统 + 只读」属性（等价 `attrib +s +h +r`）把文件从资源管理器中隐去，恢复时清除这三项（`attrib -s -h -r`）。支持自定义展示名，以及按项设置的单独密码——设置后隐藏、恢复、移除前都需再校验一次。
- 隐藏记录整篇 AES-GCM 加密存于 `data\vault.hidden`，路径与展示名不以明文落盘；改主密码时随新密钥一起重写。

### 变更

- `build_amd64.bat` 改用 `vswhere` 定位 Visual Studio（2019 / 2022 / 2026）并复用其 MSBuild、CMake 与 Windows SDK，新增 `Test` 模式；补充 `CMakePresets.json`。
- 解决方案与 `KeySecBox.DLL.vcxproj` 适配 VS 2026（v180 工具集）。

### 仓库

- 源文件统一添加 SPDX 许可证头，构建脚本移出版本跟踪。
- README 补充证书与徽章链接，整理仓库基础配置。

### 测试与 CI

- 新增 `tests/core_tests.cpp`：10 组核心 C API 用例（解锁/锁定、分类、条目、多分类、全部视图排序、搜索与 Unicode、恢复密钥、隐藏文件、改密、诊断），经 ctest 运行。
- 新增 `tests/ui.tests`：隐藏文件属性语义与 CSV 读写的控制台测试。
- 新增 GitHub Actions：按改动范围触发核心 ctest（Debug / Release）、前端编译与前端逻辑测试。

## [v1.1.2](https://github.com/XKPU/KeySecBox/compare/v1.1.1...v1.1.2) - 2026-09-05

### 修复

- 解锁界面点击「忘记密码」后，被隐藏的解锁框返回值被当成取消，程序直接退出。

## [v1.1.1](https://github.com/XKPU/KeySecBox/compare/v1.1.0...v1.1.1) - 2026-09-02

### 新增

- 设置 → 对话框外观：对话框圆角可在 0–32 px 之间调整，保存前即时预览。

### 优化

- 条目列表的备注列只显示首个换行前的内容，空备注不再显示占位文字。
- 恢复密钥对话框的标题带上「分类 → 账户」，避免同名条目混淆。

## [v1.1.0](https://github.com/XKPU/KeySecBox/compare/v1.0.1...v1.1.0) - 2026-08-19

### 新增

- 条目可归属多个分类，条目编辑提供「多分类」模式。
- 分类、分类内条目与「全部」视图都支持上下移动排序；「全部」视图单独记录被移动条目的位置，不影响分类内顺序。
- 内置旧版 1.0.x 库的只读导入向导，旧库数据合并进当前新版库。
- 动画帧率可调（1 fps 至显示器刷新率）。

### 变更

- 存储层重写为 KSX4 六文件：分类、关联与排序列为明文，密码字段逐条 AES-GCM 加密。
- 取消 1.0.x 的墓碑（tombstone）追加写机制，改为临时文件 + 原子替换的整文件重写。
- 移除 `ksbx_set_tomb_limit` / `ksbx_get_tomb_limit` 等墓碑相关接口。

## [v1.0.1](https://github.com/XKPU/KeySecBox/compare/v1.0.0...v1.0.1) - 2026-08-16

### 新增

- 对话框统一套用当前明暗主题与 12 px 圆角。
- `backup.bat` 源码备份脚本（备份源码、`.git` 与根目录构建文件；后续版本移除）。

### 变更

- 精简自包含构建产物。
- 移除多余的 VSCode 语言服务配置。

## [v1.0.0](https://github.com/XKPU/KeySecBox/releases/tag/v1.0.0) - 2026-08-16

首个版本：本地优先的 Windows 密码保险库，所有数据存放在程序目录的 `data\`，无云端、无账号。

- WinUI 3 前端 + 原生 C++ 核心，界面通过 P/Invoke 调用核心。
- KSX3 多文件存储：索引整块加密、条目逐条加密，追加写 + 墓碑机制支持增量保存。
- AES-256-GCM（BCrypt）、PBKDF2-HMAC-SHA256 密钥派生、BCryptGenRandom 随机源。
- 主密码找回：备用密码（PBKDF2 + AES-GCM）或 DPAPI 绑定当前 Windows 账户（PIN / 指纹 / 人脸）。
- 分类与条目管理、条目恢复密钥（2FA 备用）、CSV 导入导出（兼容 Microsoft Edge）、修改主密码。
- 框架依赖与自包含两种发布形态。
