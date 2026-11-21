# KeePassModern

基于 **Avalonia 12 + .NET 10 + NativeAOT** 与 **Microsoft Fluent 2 Design System** 重新设计与实现的跨平台现代 KeePass 密码管理器，原生支持 **macOS / Windows / Linux**（x64 & arm64），完整兼容 **KDBX 3.1 / 4.0 / 4.1** 数据库格式。

## 核心特性

- **纯托管、零反射、100% NativeAOT 兼容 KDBX 引擎**
  - 完整读写 KDBX 3.1 / 4.0 / 条件 4.1 数据库
  - 纯托管加密算法：AES-256-CBC、ChaCha20、Twofish；KDF：Argon2id、Argon2d、AES-KDF；HMAC-SHA-256 块认证与 Salsa20/ChaCha20 内存保护流
  - 复合主密钥：主密码 + 密钥文件（XML v1.0 / XML v2.0 / 二进制）
- **现代 Fluent 2 桌面体验（零 Emoji，全矢量图标）**
  - **跨平台原生标题栏适配**：macOS 原生全高三栏 + 交通灯对称微调 + 原生顶部菜单栏（`NativeMenu`）；Windows 11 48px Mica Alt 一体化标题栏
  - **主题色与实时界面密度**：默认跟随系统强调色 + 6 款内置预设 + 自定义取色；支持紧凑 / 标准 / 宽松三档密度无需重启实时切换
  - **单页卡片 + `SettingsExpander` 渐进式表单**：彻底告别旧版多选项卡，支持快速模板、自定义字段、加密附件与历史版本快照回滚
- **安全与生产力工具**
  - 内置 TOTP/HOTP 动态验证码（兼容 `otpauth://` 与 `TimeOtp-*`）
  - 强密码生成器（随机字符 / 助记词 / KeePass Pattern + 实时熵值评估）
  - 安全审计仪表盘（弱密码、重复密码、过期条目、2FA 覆盖率）
  - 安全剪贴板自动倒计时清空、CSV 导入/导出、HTML 应急表导出、数据库无损合并同步
  - GitHub Releases 自动检查更新与跨平台一键升级

## 开发与构建

```bash
# 运行核心单元测试
dotnet test tests/KeePassModern.Tests/KeePassModern.Tests.csproj

# 调试运行
dotnet run --project src/KeePassModern/KeePassModern.csproj

# NativeAOT 原生发布
dotnet publish src/KeePassModern/KeePassModern.csproj -c Release -o publish/local
```
