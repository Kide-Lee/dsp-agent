## 项目概况
IcarusII 是《戴森球计划》的 Agent。它包括：

- **mod（C# / Unity）**：通过BepInEx，窗口、热键、状态采集、工具执行。
- **sidecar（Node / TS）**：内嵌 pi Agent SDK，负责 LLM 调用、工具编排、知识库。
- 两者通过`127.0.0.1`WebSocket 通信，协议 v1。参见`docs/protocol-v1.md`。

模组的目标环境是 Windows 与 Linux + Proton（Steam Deck 同样适用），游戏版本 `0.10.30.3100` / Unity 版本 `2022.3.62f3c1`；sidecar 另产出 Windows 可执行文件。项目近况可参见`docs/current-status.md`。开始修改前检查工作区状态及相关实现。不要覆盖用户已有的未提交修改。

## 约束
### 通信协议
- 协议变更必须先更新 [protocol-v1.md](docs/protocol-v1.md)，再同步修改：
    - mod/src/Shared/Protocol/Protocol.cs
    - sidecar/src/protocol.ts
    - 相关测试。
- 不得改变 v1 既有字段的语义。新增能力优先使用可选字段或新消息，使旧端可以忽略不认识的内容。
- mod/src/Shared 必须保持 Unity-free，不得引用 UnityEngine。
- 协议类型、工具 schema、配置迁移必须有离线测试。

### UI
- UI 和布局**必须**定义在 Prefab 中，**不得**通过代码在运行期构建和调整UI。
  - 除非是像在列表中添加新项这样的动态内容。
  - 但这种列表中的项也必须在 Prefab 中存在一个模板。
- 编辑 UI 的工作流应该是：
  1. 使用 MCP for Unity 工具修改`dsp-game/Assets/IcarusII/ChatWindow.prefab`。
  2. 如果用户没有开启 MCP，请求用户打开再继续工作。
  3. 编辑结束后，必须截图查看界面是否满足预期。
  4. 保存 prefab 并关闭 Unity；`Temp/UnityLockfile` 存在时 `scripts/build-ui-bundle.sh` 会拒绝执行。
  5. 用 `IcarusIIDumpPrefab.DumpChatWindow` 在构建前后各 dump 一次，对比 `tool-bar` 里 `send-button` / `permission-group` / `thinking-group` / `model-group` 的右锚点（`-20` / `-100` / `-220` / `-340`），确认没有漂移。
  6. 重打 UI AssetBundle。
  7. 检查生成的 Prefab 是否与`mod/unity/Prefab/`中的一致。若不一致，寻找原因然后告诉用户。
- prefab 是二进制，`git diff` 只显示 `Bin`，判断布局是否漂移只能靠 dump 对比，不能靠 diff。
- 一般的 UI 动效，必须复用游戏本就有的组件来实现，比如按钮、图标 tag、tooltip 的显示/隐藏与跟随等。
  - 只有在游戏原生组件确实无法实现预期时，才在 mod 中通过代码生成。
- UI中的Icon，优先复用游戏中本就存在的Icon；若没有，就在网上寻找可匹配游戏美术风格的图标。禁止用emoji来作为本mod的Icon。
- 原生组件的字段、事件和常见组合见 skill `dsp-native-ui`（数据来自 DSPGame.unity 的 In Game 子树实测）。

### 工具安全边界
- 不向模型暴露 shell、任意文件系统或任意 Unity API。例外只有三个，且都只操作游戏蓝图根目录：
    - `blueprint_save`：只写仓库拉取的蓝图，可指定蓝图根内的子目录（默认 `IcarusII/`）；
    - `game_copy_blueprint`：只把当前存档里已有建筑交给游戏自己的复制逻辑生成蓝图；
    - `blueprint_fs`：蓝图目录的受限虚拟文件系统。
  三者都必须清洗路径、限制在蓝图根内，默认不覆盖同名文件。
- sidecar 只注册项目自定义工具；pi 内置的 `bash` / `read` / `write` / `edit` 必须保持关闭。
- 写操作必须经过 sidecar 确认钩子 + mod 侧一次性令牌，不得绕过。
- `run_script` 只在 QuickJS-WASM 中执行；禁止改成 `node:vm`、`new Function`、子进程或任何能拿回宿主全局对象的方式。
- 脚本能力面只能来自注入的 `dsp.call`：它走同一个 `GameToolHost`，白名单来自 `host.tools()`，因此继承 risk / 路由 / 超时。
- 脚本默认只读。受控写脚本必须在脚本里出现字面量 `dsp.call("写工具名", …)`，执行前由 sidecar 检查并弹一次脚本级确认；mod 发短期脚本租约（token + 脚本哈希 + 最多 10000 次），脚本结束立即释放。不得动态拼写工具名绕过确认。
- 必须保留三重预算：调用次数（默认 100，最大 10000）、CPU 时间（默认 30s，最大 120s）、返回文本（≤6K 字符）。
- 沙箱不得注入 `process` / `require` / `fetch` / `WebSocket` 等宿主对象；新增任何注入都要在测试里断言 `typeof ... === 'undefined'`。
- 脚本必须可重放：不要用 `Date.now` / `Math.random`，也不要依赖重放之间的外部可变状态。
- `blueprint_fs` 拒绝 `..`、盘符、绝对系统路径、NUL 和符号链接；`write` 只接受能通过 `inspectBlueprintText` 的 `.txt` 蓝图；`rm` / `rmdir` 是永久删除，必须 `confirm=true`。
- API Key 只存 sidecar 配置，不得写进日志、聊天记录、测试输出或提交。
- sidecar 只监听 `127.0.0.1`，不要改成 `0.0.0.0`。

### 补丁
当前插件刻意不注册 Harmony 补丁，功能优先通过游戏公开类型和成员实现。确实需要 patch 时，先说明必要性、替代方案和风险，不得静默加入。

### 部署与提交
工作流：
1. 根据改动范围运行相关测试（skill：`verify-changes`）。
2. 按改动类型构建 mod DLL、sidecar 和/或 UI bundle；prefab/UI 改动用 skill `rebuild-ui-bundle`。
3. 运行 scripts/package.sh 打包（skill：`package-release`）。
4. 运行 .dsh/skills/update-steam-mod/scripts/update-steam-mod.sh 部署到 r2modman 配置档（默认；手动安装用 `--game-dir`），并确认 SHA-256 校验通过（skill：`update-steam-mod`）。
5. 检查提交差异，执行 git add 和 git commit；提交后确认 git status --short 为空。
提交信息约定：
- 用 `feat:` / `fix:` / `refactor:` / `style:` / `docs:` / `release:` 前缀，主题可中文。
- 一个提交只做一类事；代码、prefab 快照、构建器改动放同一逻辑提交。
安全与授权：
- 执行前确认目标路径、工作区差异和操作范围。
- 不得提交用户原有的未提交改动、密钥、运行期数据或忽略目录。
- 如果游戏正在运行、Steam 路径不可用、部署脚本拒绝执行，或操作会覆盖用户现有修改，应停止并报告阻塞原因；不得静默跳过，也不得强行继续。
- 仅文档修改可不进行部署。

### 回归问题处理
已验收或明确工作正常的功能突然失效时，先按回归问题调查，不要直接叠加补丁。
1. 确认基线：找到最后正常的提交或版本，记录当时行为、日志或验收结论。
2. 倒查历史：使用 git log -- <path>、git diff <old>..<new> -- <path>，必要时使用 git blame。
3. 恢复原状：确认属于回归后，优先通过 git revert 或恢复原提交修复基线，并验证恢复结果。
4. 再做修复：如仍需实现新需求，在干净基线上进行最小修改，并说明其与已验收内容的关系。
5. 保留证据：在回复和提交信息中记录相关回归提交、根因、恢复方式及验证结果。

## 通用工作方式
1. 永远不要把“举例说明”当成全部情况，当用户举例时，一定要思考如何泛化，或者提问用户泛化的边界。
2. 不要新增只被调用一次的小 helper；只有当它能命名清楚概念、隔离复杂逻辑、复用已有边界或改善测试时才抽函数。
3. 作为对规则2的补充，如果同样的逻辑出现超过3次，就应考虑作为共享函数，并写入项目记忆以供继续复用（如果有项目记忆的话）。
4. 新模块应围绕职责和变化原因命名，避免按“工具集合”“杂项”“common”堆放无关能力。
5. 不要假设自己处在沙箱(sandbox)网络中，这会让你认为自己无法访问网络，干扰测试。
6. 如果用户的话里存在多个问题，**一定要写 TODO 清单或并行开启sub agent**，以防上下文固定于一个特定的问题而丢弃其他问题。
7. 优化prompts应注意简短和通用性，不可将当前文档或问题限定的内容完全输入提示词。
8. 编写测试代码或者**任何可运行的文件**时，**绝对禁止**将文件输出到项目文件夹以外的路径。
9. 编写总结、计划、方案、报告、备忘时，**务必**使用中文文件名并输出到项目路径下的 `docs/plans/` 文件夹，禁止输出到 `/tmp` 路径。
10. 调试和分析问题时，应一次分析、修改、验证完毕，不要只分析然后说“仍需实施”，或者只改代码就报称修复。每次修复后必须用单元测试/集成测试/组件测试验证实际效果，确认问题真正解决才能报完成。
11. 集成测试/组件测试必须全部通过才能运行主流程。
12. 禁止使用环境变量作为配置项：玩家/模型可见的运行期配置放 sidecar 的 `config.json`，不要用环境变量承载；构建脚本的路径、版本、跳过开关等环境变量覆盖不受此限。
13. 如果接收到空的用户输入，不必管它，继续当前的工作。
14. 选取第三方开源库时，应优先选择使用量大、活跃维护的库。
15. 不要替我计算工作量需要几天/几人天/几人月，你是AI，速度足够快。
16. README是用户说明，不是开发者手册，不准将开发进度写进去。
17. GUI 的构建/验证必须后台完成，**禁止抢前台焦点或注入键鼠**（用户在用机器时会直接被打断）。
18. 写提示词和注释的时候，一句话一行，例如：
    ```text
    你需要做如下工作，做完报告给我。
    ```
    禁止出现明明是一句话却要强行换行的情况：
    ```text
    你需要做如下工作，
    做完报告给我。
    ```
19. 写注释时禁止写入被否定的决策，除非它对应的代码曾在真实的运行中出现过bug；此时要写明问题现象和解决方式，而不是只描述“不要怎样”。
20. 本仓库代码频繁改动，**注释可能与实际代码逻辑不一致**。判断函数行为时**一律以代码实现为准**，不得仅凭注释下结论；改动前必须读代码核实当前真实行为。
21. 除非用户要求，否则禁止使用任何打分机制和置信度机制，因为没有理论指导如何消费这些分数。
22. **确认废弃的代码一律删除**，不要想着“保留工作”或者“兜底”。死代码不只是垃圾，还会吸附后续修复接错线。重构时先查调用方，确认零调用即删；测试、参数、兼容层随机制一并删除。

## C# 编码约定
23. 禁止修改代码格式（除非影响代码逻辑，例如不当的缩进导致编译错误或者if分支执行不完整），这会造成无谓的时间和token消耗；本仓库没有自动格式化钩子，不要顺手重排格式。
24. 一个方法最好不要超过20条语句。
25. 用插值字符串代替字符串拼接（因为C#编译器对此有优化）。示例：
    ```csharp
    // GOOD
    string result = $"Welcome, {firstName} {lastName}!";

    // BAD，是字符串拼接
    string result = string.Format("Welcome, {0} {1}!", firstName, lastName);

    // BAD，是字符串拼接
    string result = string.Concat("Welcome, ", firstName, " ", lastName, "!");
    ```
26. 对于小型、频繁传递的不可变数据，例如坐标、颜色或日期范围，使用struct/record代替class。
27. 对于无法直接修改的第三方类型，使用扩展方法；但不要给你能直接修改的类添加扩展，这属于逻辑浪费。
28. 在泛型类或方法中，不要与 object 类型进行来回转换，而应使用 where 约束或 as 运算符来指定泛型参数的确切特性。例如：
    ```csharp
    class SomeClass {}

    // Don't
    class MyClass<T> {
    void SomeMethod(T t){
    object temp = t;
    SomeClass obj = (SomeClass) temp;
    }
    }

    // Do
    class MyClass<T> where T : SomeClass {
    void SomeMethod(T t) {
    SomeClass obj = t;
    }
    }
    ```
29. LINQ表达式的返回结果应进行物化，例如：
    ```csharp
    var query =
        from customer in db.Customers
        where customer.Balance > GoldMemberThresholdInEuro
        select new GoldMember(customer.Name, customer.Balance);

    return query; // LINQ是延迟执行的，所以query实质上是表达式树，而非最终结果
    return query.ToList();  //所以需要ToList()、ToArray()方法进行物化
    ```
30. 何时使用IEnumerable\<T\>/Span\<T\>（栈上）/Memory\<T\>（堆上）？
    ```text
    需要处理数据吗？
    ├── 是连续内存块（数组、字符串、栈内存、非托管内存）？
    │   ├── 方法完全是同步的？
    │   │   ├── 需要写入？→ Span<T>
    │   │   └── 只读？→ ReadOnlySpan<T>
    │   └── 需要跨 await、存为字段、或传入异步流？
    │       ├── 需要写入？→ Memory<T>
    │       └── 只读？→ ReadOnlyMemory<T>
    └── 是离散/惰性序列（数据库行、文件行、无限流）？
        └── 用 IEnumerable<T> / IAsyncEnumerable<T>
    ```

    使用Span\<T\>/Memory\<T\>提升性能的典例：

    | 对比维度    | 传统方式                            | Span / Memory 方式                | 典型提升              |
    | ------- | ------------------------------- | ------------------------------- | ----------------- |
    | 字符串切片   | `Substring`                     | `ReadOnlySpan<char>.Slice`      | **4~7.5x 速度，零分配** |
    | 数组子集    | `Array.Copy` + `new[]`          | `Span<T>.Slice`                 | **O(1) 切片，零分配**   |
    | 数字解析    | `int.Parse(str.Substring(...))` | `int.Parse(str.AsSpan(...))`    | **零子串分配**         |
    | 小缓冲区    | `new byte[256]`                 | `stackalloc byte[256]` + `Span` | **13x 速度，零堆分配**   |
    | GUID 转换 | `Guid.Parse` / `ToString()`     | `Span` 解析/格式化                   | **40~50% 速度，减分配** |

    一些普通操作的速查表：

    | 场景               | 推荐类型                                     | 理由                         |
    | ---------------- | ---------------------------------------- | -------------------------- |
    | 同步字符串/数组处理       | `ReadOnlySpan<T>` / `Span<T>`            | 零分配切片，无 GC 压力              |
    | `stackalloc` 缓冲区 | `Span<T>`                                | 唯一支持栈内存的类型                 |
    | P/Invoke 同步调用    | `Span<T>`                                | 可直接映射到指针                   |
    | 异步 I/O 缓冲区       | `Memory<T>`                              | 可跨 `await`，再转 `Span` 处理    |
    | 类字段保存缓冲区         | `Memory<T>`                              | `Span` 不能作为字段              |
    | 数据库/文件行遍历        | `IEnumerable<T>` / `IAsyncEnumerable<T>` | 非连续，惰性求值                   |
    | 需要 LINQ 操作       | `IEnumerable<T>`                         | `Span`/`Memory` 不支持原生 LINQ |
    | 不确定数据是否连续        | `IEnumerable<T>`                         | 最通用的抽象                     |

31. 代码中的字面量（魔数）应以常量声明封装（若用于日志记录和追踪除外），例如：
    ```csharp
    public class Whatever {
        public static readonly Color PapayaWhip = new Color32(0xFF, 0xEF, 0xD5, 0xFF); //字面量0xFFEFD5用PapayaWhip声明
        public const int MaxNumberOfWheels = 18;  //字面量18用MaxNumberOfWheels声明
        public const byte ReadCreateOverwriteMask = 0b0010_1100;  //字面量0b0010_1100用ReadCreateOverwriteMask声明
    }
    ```
    语义非常明确且不会发生变化的情况也不必封装，例如：
    ```text
    mean = (a + b) / 2; // 平均数自然是/2，声明成常量反倒是画蛇添足
    WaitMilliseconds(waitTimeInSeconds * 1000); 秒换算成毫秒必须是*1000，声明成常量也是画蛇添足
    ```
32. 避免使用嵌套的try catch块，这会减弱可读性。
33. 不要使用ref/out参数，这会降低可读性，返回复合对象、结构体或元组。
    例外情况：
    - 依赖库或游戏 API 本身就以 ref/out 暴露（例如 Unity 的 `Physics.Raycast(ray, out hit)`），按其签名使用即可。
    - `bool success = int.TryParse(text, out int number);` 这类 Try* 模式需要保证逻辑正确。
34. async/await用于I/O密集型任务，Task.Run()/Task.StartNew()则用于CPU（计算）密集型任务。
35. 建议直接await ValueTask或者ValueTask\<T\>，且只等待一次。在 Unity 侧使用 `ConfigureAwait(false)` 要谨慎：await 之后如果还要碰 `UnityEngine` API，必须回到主线程。
    ```csharp
    // OK / GOOD
    int bytesRead = await stream.ReadAsync(buffer, cancellationToken);

    // OK / GOOD
    int bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

    // OK / GOOD - Get task if you want to overcome the limitations exposed by ValueTask / ValueTask<T>
    Task<int> task = stream.ReadAsync(buffer, cancellationToken).AsTask();
    ```
36. 应尽量使用C#新语法或者更规范的语法（mod 为 C# 9），包括且不限以下的内容：
    ```csharp
    ValueTuple<string, int> tuple = new ValueTuple<string, int>("", 1); //旧语法，不再使用
    (string, int) tuple = ("", 1); //推荐的新语法
    ```
    ```csharp
    Nullable<DateTime> startDate; //旧语法，不再使用
    DateTime? startDate; //推荐的新语法
    ```
    ```csharp
    if (startDate == null) ... //不规范，不推荐使用
    if (startDate is null) ... //规范用法
    ```
    ```csharp
    if (startDate.HasValue) ... //不规范，不推荐使用
    if (startDate is not null) ... //规范用法
    ```
    ```csharp
    if (startDate.HasValue && startDate.Value > DateTime.Now) ... // startDate.Value > DateTime.Now说明startData有值，没必要再判断一遍
    if (startDate > DateTime.Now) ...  // 正确的做法：只做必要的判断
    ```
    ```csharp
    if (list == null) list = new List<string>(); // 旧语法，且易读性低，不再使用
    list ??= new List<string>(); //新语法，推荐使用
    ```
37. C#支持解构元组，错误和正确示例如下：
    ```csharp
    // 错误示例，写法繁冗
    public record Point(int X, int Y);

    Point point = GetOrigin();
    int x = point.X;
    int y = point.Y;
    ```
    ```csharp
    // 正确示例，写法简单
    (int x, int y) = GetOrigin();

    //对foreach也适用
    foreach ((int key, int value) in dictionary){
    Console.WriteLine($"{key}: {value}");
    }
    ```
38. 不要使用#region标记。
39. 在写C#时，除非确实需要控制反转，否则不要总是试图用依赖注入去写逻辑。
40. 作为规则34的补充，涉及I/O的逻辑（例如Console.WriteLine/Console.Error.WriteLine，或者日志记录方法）应采取其异步重载（例如await Console.Out.WriteLineAsync）。

### Unity 侧约定
41. Unity API 只能在主线程调用；游戏状态采集与工具执行走现有的主线程调度，不要在后台线程直接访问 `UnityEngine` 对象。
42. 热路径避免每帧分配与 LINQ；UI 文本更新要节流。
43. 新界面文本走 `I18n/Strings.cs`（`Strings.Get(...)`），不要散落硬编码中文。
44. Unity 的 DLL 不要复制到输出目录（csproj 已有 `Private=false`）。

## 测试与验证
45. **测试代码不得复制/重写业务逻辑**。测试只负责组装输入（数据、文件路径、配置）和断言输出，**完全复用**业务逻辑，不重新实现业务逻辑，也不可自己绕过业务逻辑。
46. 只为可独立重用或复杂到需要隔离的逻辑编写单元测试。对于其余逻辑，对整个功能进行集成测试或组件测试可以提供更好的覆盖率，且维护成本更低。
47. 使用GUI（游戏内聊天窗 / 本仓库Web界面）进行测试时，禁止使用mock LLM，一律使用真实的LLM请求。
