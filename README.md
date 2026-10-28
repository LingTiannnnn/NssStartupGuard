# NssStartupGuard

NUC x15 控制台（NucSoftware Studio，下面简称 NSS）开机自启失败，或者手工第一次启动后报 WebSocket 通信失败，就用这个程序接管登录时的启动时机。不改 Intel / Uniwill 的任何原始文件、服务和配置。

预编译包在 [Releases](../../releases) 里的 `NssStartupGuard.zip`。

## 食用方法

1. 解压全部内容到一个固定位置。配置和 `logs/` 都按 EXE 所在目录查找，别留在下载目录或临时目录。
2. 双击 `Register-Startup.bat`，在 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 注册当前用户启动项。
3. 到任务管理器的「启动应用」里禁用 NucSoftware Studio 自身的开机自启项。NSS 只能有一个启动入口，否则守护程序和原启动任务会互相抢。
4. 重新登录或重启，enjoy。

要撤销就双击 `Unregister-Startup.bat`，然后把 NSS 自身的开机自启项恢复回去。

## 两个故障

开机进桌面托盘里没有 NSS，和第一次打开报通信错误，是两条独立的链。

包内启动器 `NssOnStartupLauncher.exe` 的开机分支只是起了一个隐藏的 cmd.exe，把 `NucSoftwareStudio.exe tray` 写进标准输入就返回，不等 NSS，不看退出码，自己也没留日志。命令行里用的是裸文件名，要靠 PATH 和应用执行别名解析，而实测执行别名和包内路径两处都返回「拒绝访问」，NSS 根本没被拉起来。这条链上 Uniwill Service 反而是正常的，NSS 主日志也不会有新的 `Launched NSS`。

另一条是服务自己崩。NSS 建好 duplex WebSocket 会话后，服务的 `UWSystemMonitor` 定时器通过 `SysMonitorNotification` 回调往一个无效或正在关闭的 WebSocket 里发数据，`WebSocketException` 转成 `CommunicationException` 没人接，`UniwillService.exe` 跟着退出。Windows 按失败恢复设置等 60 秒后重启服务，所以第一次连上的 NSS 报错，服务重启后再开就正常。服务地址是 `ws://localhost:58594/UniwillService`。崩溃记在事件查看器的 `.NET Runtime` 和 `Application Error`，服务重启记在 `Service Control Manager` 事件 7031，NSS 自己的日志在 `%LOCALAPPDATA%\Packages\AppUp.IntelNUCSoftwareStudio_8j3eq9eme6ctt\LocalCache\Local\NucSoftwareStudio\NssLog.txt`。

## 守护程序做的事

登录之后按一个有界状态机走：

1. 互斥体保证只有一个守护实例，NSS 已经在跑就直接成功退出。
2. 等 `UniwillService` 进入 Running，并要求服务 PID 在 `prelaunch_stable_sec` 内保持不变；服务退出或 PID 变了就重新等。
3. 用 `IApplicationActivationManager` 按包 AUMID 加 `tray` 参数激活 NSS，不碰执行别名，也不直接跑 WindowsApps 里的文件。
4. 激活返回成功不算数，得真看到 NSS 主进程。启动后 NSS 和服务 PID 一起观察。
5. 服务崩了就等 Windows 恢复，不主动重启服务；这期间 NSS 还活着就不重复启动它。
6. 进程查不到就按未知处理，不再往下决策。状态未知，或者启动器还在跑、可能有迟到激活时，禁止重试。重试有上限，没有无限循环。

## 文件

| 文件 | 说明 |
| --- | --- |
| `NssStartupGuard.exe` | .NET Framework 4.6.1，Windows GUI 子系统，无第三方依赖 |
| `NssStartupGuard.cs` | 状态机、Windows 适配器、日志和内置测试的源码 |
| `NssStartupGuard.exe.config` | 运行时版本声明 |
| `nss_startup_guard.config.json` | 服务名、包 AUMID、等待窗口和重试参数 |
| `Register-Startup.bat` 和 `Unregister-Startup.bat` | 注册、删除当前用户启动项 |
| `logs/nss_startup_guard.log` | 运行时生成，按大小滚动 |

正常运行和成功退出都不弹控制台。守护模式下非零退出会打开终端，显示退出码、错误详情和日志末尾。

## 命令

```text
NssStartupGuard.exe [--config path] [--check-config] [--self-test] [--activate-only]
```

不带参数就是完整守护流程，启动项注册的是它。`--config path` 指定配置文件位置。`--check-config` 只校验 JSON，不查询也不启动组件。`--self-test` 用模拟适配器和虚拟时钟跑 19 项隔离测试。`--activate-only` 只做一次真实包激活，用来单独验证 AUMID 和参数对不对。

退出码：`0` 成功，或 NSS 已在运行；`1` 已有守护实例；`2` 配置或参数错误；`3` 等待服务超时；`4` 状态不稳定或未知，停止决策；`5` 达到启动重试上限；`6` 包激活失败。

## 配置

跟机器有关的是 `service_name`、`nss_process_name`、`package_app_user_model_id`、`nss_arguments` 四项。其余是时间窗口和重试参数：`startup_timeout_sec`、`recovery_timeout_sec`、`poll_interval_sec`、`prelaunch_stable_sec`、`observation_sec`、`nss_start_grace_sec`（要小于 `observation_sec`）、`retry_backoff_sec`、`max_launch_attempts`（1 到 5），日志与输出捕获是 `log_max_bytes`、`log_backup_count`、`launcher_output_limit`。配置里出现未知键会直接报错，不静默忽略。

## 边界

只管开机和登录这一条竞态链。手动启动、其他用户会话、其他客户端连接触发的服务崩溃不在范围内，那些要给 `UniwillService.exe` 打方法级 IL 补丁，本仓库没有。NSS 的 WPF `XamlParseException` / `InvalidOperationException` 界面崩溃是另一个故障，两者不能互为验收依据。

程序不停服务、不结束 NSS、不改原始二进制。回滚就是删掉启动项，把 NSS 自身的开机自启恢复回来。

## 测试

基本功能全部在本人真机上通过实机测试。
