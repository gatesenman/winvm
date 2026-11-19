# WinVM

**1.6.0** · Windows 上的 Hyper-V 虚拟机管理器：创建、启动、连接，并从官方源下载 ISO 安装系统。

## 功能特性

- 向导式创建 Generation 2 虚拟机（Windows 11 可启用 TPM + Secure Boot）。
- 解析并下载官方 ISO（Linux 校验和目录；Windows 优先 zh-CN），支持进度、暂停、断点续传与校验。
- 连接方式：vmconnect / Enhanced Session、来宾 RDP、SSH；可复制连接信息。
- 「基础环境」向导：RDP / OpenSSH、主机名、时区、本地用户脚本（密码只写入本机脚本目录，不写日志）。
- 命令行：`WinVM.exe --list-ready`、`--connect-info` 输出 JSON，便于同机发现。

## 技术栈

- C# / .NET 8（`net8.0`）
- Hyper-V
- 项目：`WinVM.csproj`

## 从源码运行

源码在仓库根目录。本仓库不包含安装包。需要 Windows，并已启用 Hyper-V。

```bash
dotnet build WinVM.csproj -c Release
dotnet run --project WinVM.csproj
```
说明见 `README.zh-CN.md`。

## 文档

- [中文说明](README.zh-CN.md)

---

## English

# WinVM

**1.6.0** · A Hyper-V virtual machine manager for Windows: create, start, and connect VMs, and download official ISOs to install operating systems.

## Features

- Wizard for Generation 2 VMs (Windows 11 can enable TPM + Secure Boot).
- Resolves and downloads official ISOs (Linux checksum directories; Windows prefers zh-CN), with progress, pause, resume, and verification.
- Connect via vmconnect / Enhanced Session, guest RDP, or SSH; connection info can be copied.
- A “baseline environment” wizard for RDP / OpenSSH, hostname, timezone, and local-user scripts (passwords are written only to local script folders, not logs).
- CLI: `WinVM.exe --list-ready` and `--connect-info` emit JSON for same-machine discovery.

## Tech stack

- C# / .NET 8（`net8.0`）
- Hyper-V
- 项目：`WinVM.csproj`

## Getting started

Source is at the repository root. This repository does not include installers. Windows with Hyper-V enabled is required.

```bash
dotnet build WinVM.csproj -c Release
dotnet run --project WinVM.csproj
```
See `README.zh-CN.md`.

## Documentation

- [中文说明](README.zh-CN.md)

