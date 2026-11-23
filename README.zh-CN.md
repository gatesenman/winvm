# WinVM 1.6.0

Hyper-V 虚拟机资源库（Avalonia 11 + .NET 8）。中文界面。支持 Windows / Linux 来宾；**不支持**在 Hyper-V 上安装 macOS（界面中有诚实说明卡）。

## 一键安装（优先）

菜单 / 工具栏 / 空状态：**一键安装系统**（`Ctrl+I`）。

流程（自动）：

1. 选择官方系统（Ubuntu / Debian / Fedora / Alma / Mint / Windows 11 / Windows 10 …）
2. 解析官方下载地址（Linux：校验和目录；Windows：Microsoft software-download，优先 zh-CN）
3. 下载 ISO（进度 / 暂停 / 断点续传 / SHA-256 或 sidecar）
4. 按推荐预设创建 Generation 2 虚拟机（Win11：TPM + Secure Boot）
5. 可选：立即启动并打开 **vmconnect / Enhanced Session**

Mac 条目仅展示「Hyper-V 不支持」说明，不提供镜像下载。

Windows 若在线解析失败：可打开微软官网，再在镜像库中选择本地 ISO。

## 连接（来宾）

列表 / 菜单 / 工具栏 **「连接」**：

| 方式 | 何时可用 | 说明 |
|------|----------|------|
| 控制台 / Enhanced Session | 几乎总是 | `vmconnect`（优先） |
| 远程桌面 (RDP) | 运行中且已有来宾 IP | `mstsc /v:IP` |
| SSH | 运行中且已有来宾 IP | 系统 `ssh` |
| 复制连接信息 | 已选中虚拟机 | 主机 IP、RDP 3389、SSH 22、用户名 |

来宾 IP 来自 Hyper-V 网卡 / 集成服务（`Get-VMNetworkAdapter` 的 `IPAddresses`）。未上报前显示「—」，请先用控制台完成安装并等待网络。

### 代理 / CLI（同机发现）

```text
WinVM.exe --list-ready [--out ready.json]
WinVM.exe --connect-info <VM名或Id> [--user name] [--out info.json]
```

输出 JSON：name / id / state / os / ip / rdp / ssh / vmconnect / ready。

## 基础环境（尽力而为）

一键安装向导中的 **「基础环境」**：

- 芯片：启用 RDP（Win）、OpenSSH、Agent 就绪（Linux: openssh-server/curl/git/sudo；Win: OpenSSH Server 功能说明）
- 主机名、时区 Asia/Shanghai、本地用户与密码（**密码只写入** `%USERPROFILE%\WinVM\Setup\<VM>\` 下的脚本，**不写日志**）

生成内容：

- Windows：`Enable-WinGuest.ps1` + RDP 说明
- Linux：`cloud-init/user-data` + `firstboot.sh` + SSH 说明

**自动**：创建 / 启动 / 打开控制台 / 写出脚本。  
**引导**：在来宾完成系统安装后，运行上述脚本（完整 unattend / NoCloud 挂载在部分 Insider 主机上不稳定，故默认采用脚本 + 说明）。

## 其它

- 镜像库：`Ctrl+G`；导入：`Ctrl+Shift+I`
- 不改动 `agentos-e2e-*` 虚拟机；不启用额外 Windows 功能 / 不循环提权重启

## 下载

本仓库目前没有已发布的 GitHub Release 安装包。请从源码运行。GitHub 自动生成的 Source code zip/tar 不算安装包。

## 界面截图

仓库里目前还没有已提交的真实产品界面截图（图标、logo 或品牌图不算）。请不要把下列预留路径当成已经存在的图片去引用（避免破链）。待在真实运行窗口中截取后放入：

- `docs/screenshots/01-workbench.png`
- `docs/screenshots/02-main-or-result.png`

截图说明必须写明操作系统、软件版本、是否示例/演示数据，以及是否在 Windows 安装后截取。

## 构建

```text
dotnet build WinVM.csproj -c Release
dotnet test tests/WinVM.Tests/WinVM.Tests.csproj -c Release
dotnet publish WinVM.csproj -c Release -r win-x64 --self-contained false -o publish/win-x64
```
