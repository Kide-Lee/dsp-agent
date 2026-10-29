# 会话标题输入时卡死：Mono GC 致命错误诊断

> 现场时间：2026-10-29 22:50（部署后首次实机验证标题重命名）
> 结论：**不是标题输入框的托管异常，是 Mono(Boehm) GC 在 Proton 下挂起线程失败**。

## 现场

- 游戏进程 `DSPGAME.exe`（PID 178139）仍在，163 个线程全部处于 `S`，无 CPU 占用；
- 主线程阻塞在 `win32u.so → ntdll.so → libc futex`，即 Windows 模态消息循环；
- X11 里存在一个**未映射**（`Map State: IsUnMapped`）的窗口，标题为 `Fatal error in GC`；
- 该窗口所属进程的堆里能读到 UTF-16 字符串 `Fatal error in GC` 与 `SuspendThread loop failed`；
- 屏幕上用户看到的是 GNOME 的“steam_app_1366540 无响应”对话框（Mono 的致命错误框不可见，所以点不到）。

## 根因

`SuspendThread loop failed` 来自 Mono 的 Boehm GC（`mono-2.0-bdwgc.dll`）：

- `GC_suspend()` 在停世界时对每个托管线程执行 `SuspendThread` + `GetThreadContext`；
- `GetThreadContext` 连续失败 100 万次后 `ABORT("SuspendThread loop failed")`，Mono 弹出致命错误对话框并等待确认；
- 这是 Unity Mono × Wine/Proton 的线程上下文竞态，Unity 自己的 bdwgc 修复（`unity-master-fix-get-thread-context-abort`，循环重试 + `GetExitCodeThread`）已经在游戏里，但仍未命中这个线程。

崩溃前 10 秒（22:50:09）进程内新建了一个托管线程（TID 178521，此后阻塞在 futex）。Boehm 需要挂起全部托管线程，线程创建/退出与 GC 撞车是这类失败的已知诱因。

## 为什么不是标题输入框的代码

- BepInEx 日志与 `Player.log` 里没有任何托管异常（NullReference/IndexOutOfRange 等）；
- 崩溃前最后的焦点诊断是 `want=False isFocused=False title=True selected=name-input`，说明标题框已经正常获得焦点、聊天框没有抢回去——本次修复生效；
- 截图里标题框确实输入了 `g`（字符已经进入 InputField，说明输入路径本身正常）；
- 标题相关代码没有创建线程、没有显式 GC、没有递归；GC 失败的线程在 Mono 运行时层面，mod 无法接管。

## 处理

- 该进程无法自行恢复（致命错误框未映射，Mono 在等一个看不见的“确定”）；只能强制退出（GNOME 对话框的「强制退出」，或 `kill 178139`）。
- 重启后重试标题输入；这是竞态，可能不再复现。
- 若复现，收集 `BepInEx/LogOutput.log` 与崩溃现场，再考虑运行环境层面：更换 Proton 版本，或关闭 fsync/esync（Steam 启动项 `PROTON_NO_FSYNC=1 %command%`）。

## 可选的 mod 侧缓解（非修复）

- 标题显示层 `ConversationTitleDisplay.RefreshNow` 每帧都会走 `FitLabel`（两次 `Replace` + `Trim`），可缓存文本避免常驻分配；
- 这类改动只能降低 GC 频率，不能消除 Mono 的线程挂起竞态。
