# NssStartupGuard

用于解决 NUC x15 控制台（NucSoftware Studio / NSS）开机自启失败，以及手工第一次启动后报 WebSocket 通信失败的问题。

不替换、不修改 Intel / Uniwill 的任何原始文件、服务或配置，只在当前用户登录时接管 NSS 的启动时机。

## 下载与使用

预编译包在 [Releases](../../releases) 里，下载 `NssStartupGuard.zip`。

1. 解压全部内容到合适位置。程序按 EXE 所在目录定位配置和 `logs/`，请放在固定目录，不要留在临时目录或下载目录。
2. 双击 `Register-Startup.bat`，它在 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 注册当前用户启动项。
3. 手工去任务管理器「启动应用」，或你惯用的启动项管理工具，禁用 NucSoftware Studio 自身的开机自启项。NSS 只能有一个启动入口，否则守护程序和原启动任务会互相抢。
4. 注销重新登录，或者重启。enjoy。

撤销注册：双击 `Unregister-Startup.bat`，同时可以把 NSS 自身的开机自启项恢复回来。

## 问题现象

- 开机进桌面后托盘里没有 NSS，启动任务看起来已经跑过，但主程序没起来。
- 第一次手工打开 NSS 报通信错误；关掉再开第二次就正常。

这两件事的成因不同，分开看。

## 原因

### 一、开机启动链交接失败

包内启动器 `NssOnStartupLauncher.exe` 的无参数分支（也就是开机那条路径）做的事是：

```text
启动隐藏的 cmd.exe
→ 通过标准输入写入 NucSoftwareStudio.exe tray
→ 刷新并关闭标准输入
→ 启动器立即返回
```

它不等 NSS、不看退出码、也没有交接日志。命令行里用的是裸文件名，依赖 PATH 和应用执行别名解析，而实测执行别名与包内路径都会返回「拒绝访问」，NSS 因此根本没被拉起来。NSS 主日志里不会出现新的 `Launched NSS`，Uniwill Service 此时反而是正常的。

### 二、Uniwill Service 崩溃导致的 WebSocket 报错

NSS 建立 duplex WebSocket 会话后，服务的监控定时器在发送回调时访问了无效或正在关闭的 WebSocket，未处理异常直接把服务进程带崩：

```text
UWSystemMonitor 定时器
→ IUniwillServiceCallback.SysMonitorNotification
→ WebSocketBase.SendAsyncCore
→ WebSocket.ThrowOnInvalidState
→ System.Net.WebSockets.WebSocketException
→ System.ServiceModel.CommunicationException
→ UniwillService.exe 退出
```

服务失败恢复动作是在 60 秒后重启，所以第一次连上的 NSS 报错，等服务重启后再启动就正常——这正是「第一次报错、第二次能用」的来源。服务地址是 `ws://localhost:58594/UniwillService`。

对应的证据在事件查看器：`.NET Runtime` 和 `Application Error` 记录服务崩溃，`Service Control Manager` 事件 7031 记录异常退出与重启；NSS 自己的文件日志在
`%LOCALAPPDATA%\Packages\AppUp.IntelNUCSoftwareStudio_8j3eq9eme6ctt\LocalCache\Local\NucSoftwareStudio\NssLog.txt`。

## 守护程序做了什么

接管登录启动后，以当前用户权限跑一个有界状态机：

1. 每用户互斥体保证只有一个守护实例；NSS 已经在运行就直接成功退出，不重复启动。
2. 有界等待 `UniwillService` 进入 Running 并记录服务 PID，要求该 PID 在 `prelaunch_stable_sec` 窗口内保持不变；服务退出或 PID 变化则重新等待。
3. 通过 Windows `IApplicationActivationManager.ActivateApplication`，用包 AUMID 加 `tray` 参数激活 NSS，保留包身份。不直接执行 WindowsApps 包内文件，也不依赖执行别名。
4. COM 激活返回成功不代表 NSS 已经稳定，必须实际观察到 NSS 主进程。启动后联合观察 NSS 与服务 PID。
5. 服务崩了就等 Windows 自己恢复，不主动重启服务；这期间 NSS 仍然存活就不重复启动它。
6. 进程查询失败一律按「未知」处理，不按「未运行」处理。状态未知、或启动器还在跑可能有迟到激活时，禁止重试。
7. 只有明确未运行且还有尝试额度才重试；达到上限退出并记录原因，没有无限循环。

## 文件说明

| 文件 | 说明 |
| --- | --- |
| `NssStartupGuard.exe` | .NET Framework 4.6.1 守护程序，Windows GUI 子系统，无第三方依赖 |
| `NssStartupGuard.exe.config` | 运行时版本声明 |
| `nss_startup_guard.config.json` | 服务名、包 AUMID、各类等待窗口与重试参数 |
| `Register-Startup.bat` | 注册当前用户启动项 |
| `Unregister-Startup.bat` | 删除该启动项，未注册时直接返回成功 |
| `NssStartupGuard.cs` | 状态机、Windows 适配器、日志与内置隔离测试的完整源码 |
| `logs/` | 运行时生成，日志文件为 `nss_startup_guard.log` |

正常运行和成功退出都不弹控制台。守护模式下非零退出时会打开终端，显示退出码、错误详情和日志末尾，方便开机失败后回头查。

## 命令

```text
NssStartupGuard.exe [--config path] [--check-config] [--self-test] [--activate-only]
```

| 参数 | 说明 |
| --- | --- |
| 无参数 | 完整守护流程，开机启动项用的就是它 |
| `--config path` | 指定配置文件路径 |
| `--check-config` | 只校验 JSON 配置，不查询也不启动任何组件 |
| `--self-test` | 用模拟适配器和虚拟时钟跑 19 项隔离测试，不接触真实 NUC 组件 |
| `--activate-only` | 只执行一次真实包激活，用来单独验证 AUMID 和参数是否正确 |

退出码：`0` 成功，或 NSS 已在运行；`1` 已有守护实例；`2` 配置或参数错误；`3` 等待服务超时；`4` 状态不稳定或未知，停止决策；`5` 达到启动重试上限；`6` 包激活失败。

## 配置项

| 键 | 含义 |
| --- | --- |
| `service_name` | 要等待的服务，本机为 `UniwillService` |
| `nss_process_name` | NSS 主进程名，用于判断是否已在运行 |
| `package_app_user_model_id` | NSS 包的 AUMID，激活时不带版本化安装目录 |
| `nss_arguments` | 激活参数，`tray` 表示按托盘模式启动 |
| `startup_timeout_sec` | 首次等待服务稳定的上限 |
| `recovery_timeout_sec` | 服务崩溃后等待 Windows 自动恢复的上限 |
| `poll_interval_sec` | 轮询间隔 |
| `prelaunch_stable_sec` | 启动 NSS 前要求服务 PID 保持不变的时长 |
| `observation_sec` | 启动后的联合观察窗口 |
| `nss_start_grace_sec` | 允许 NSS 迟到的宽限，必须小于 `observation_sec` |
| `retry_backoff_sec` | 两次启动尝试之间的间隔 |
| `max_launch_attempts` | 启动尝试上限，取值 1 到 5 |
| `launcher_output_limit` | 单次输出捕获的字节上限，超出部分标记截断 |
| `log_max_bytes` / `log_backup_count` | 日志滚动大小与保留份数 |

未知键名会导致配置加载失败，不会静默忽略。

## 换机器

默认配置对应 Intel NUC x15。其他机型需要改三个值：服务名、NSS 进程名、包 AUMID。

```powershell
Get-Service | Where-Object Name -like "*niwill*"
Get-StartApps | Where-Object Name -like "*NUC*"
```

`Get-StartApps` 输出的 AppID 就是 `package_app_user_model_id`。改完先跑 `--check-config`，再用 `--activate-only` 确认能激活出 NSS 主进程，最后才注册启动项。

## 自己编译

只需要 .NET Framework 自带的 C# 编译器，`JavaScriptSerializer` 依赖 `System.Web.Extensions.dll`。

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -target:winexe -out:NssStartupGuard.exe -reference:System.Web.Extensions.dll NssStartupGuard.cs
```

编译产物记得和 `NssStartupGuard.exe.config`、`nss_startup_guard.config.json` 放在同一目录。

## 边界

- 这只解决开机 / 登录竞态这条链。手动启动、其他用户会话、或其他客户端连接触发的服务崩溃，它管不到。
- 崩溃本身的根因修复方向是给 `UniwillService.exe` 打方法级 IL 补丁，处理 `WebSocketException` 与 `CommunicationException` 并让后续监控周期继续。那条路要求保留原始文件哈希与可回滚副本，厂商更新后还得重新审核，本仓库不含该补丁。
- NSS 另有过 WPF `XamlParseException` / `InvalidOperationException` 界面崩溃记录，那是另一个故障，不能拿 WebSocket 服务崩溃的修复结果来验收。
- 程序不停止服务、不结束 NSS、不修改任何原始二进制。回滚只需删掉启动项并恢复 NSS 自身的开机自启。

## 已知限制

连续冷启动、启动项安装与无人值守开机验证尚未在真实机器上完整跑过；`--self-test` 覆盖的是模拟场景。实际遇到启动失败时，先看 `logs/nss_startup_guard.log`，里面记录了每一次状态转换和退出原因。
