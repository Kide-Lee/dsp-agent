---
name: ui-ugui
description: 理解、修改并生成 Unity uGUI 的 Canvas 层级、RectTransform、Layout Group 与 prefab UI。遇到 Canvas、uGUI、RectTransform 或 prefab UI 文件相关的请求时使用。
---

理解现有的 Unity uGUI，做定点修改，并生成新的基于 Canvas 的层级。

处理 ScrollRect/ScrollView 时，先读参考文件：
- [references/scrollview-setup.md](references/scrollview-setup.md) —— 必需的层级、配置规则与常见故障

## 适用范围

先判断用户在要什么：

| 请求类型 | 动作 |
|--------------|--------|
| 关于 UI 的提问 | **理解** —— 分析层级，解释结构 |
| 修改某个具体元素 | **编辑** —— 只做定点修改 |
| 新建 UI | **生成** —— 创建新层级 |
| 修复/改进现有 UI | **编辑** —— 改现有结构，不要重建 |

**只做被要求的东西：**

| 请求 | 产出 |
|---------|--------|
| UI 布局 | 仅 prefab 或场景层级 |
| "带代码" / "带逻辑" / "要能用" | 层级 + 脚本 |

**以下说法都不代表要写脚本：**
- "正规的按钮" → 配置正确的 Button 组件
- "能用的 UI" → 能正常渲染的合法层级
- "菜单界面" → 只要视觉布局

## 硬性规则

**命名空间歧义：**
- 创建或引用 UI 组件时，一律使用全限定类型名
- 写 `UnityEngine.UI.Image`，不写 `Image`
- 写 `UnityEngine.UI.Button`，不写 `Button`
- 项目里其他命名空间会让简写产生类型歧义错误

**改之前先核实：**
- 动手前一律先查当前已有的东西
- 加子节点前确认父对象存在
- 改属性前确认组件已挂上
- 绝不假设层级状态 —— 先查

**增量修复优于重建：**
- 修问题时做定点修正
- 绝不为了修问题而销毁并重建整个层级 —— 被销毁的对象会引发空引用连锁
- 优先定位具体坏掉的属性、只改那一处，而不是重写大段结构
- **某次修复失败时，先回退这次改动**，再试别的方案

**一次只改一处：**
- 改一处，验证结果
- 不要把多个不相关的修改攒成一批
- 改某个元素时注意别顺手改坏或删掉相邻元素
- 出问题先弄清原因，再试替代方案
- 避免同时改多处、说不清是哪处起作用的"霰弹式调试"

**颜色与可见性：**
- **默认保证文字可读：** 选颜色时确保文字与背景有对比 —— 但要尊重有意为之的低对比用法（禁用态、占位文本、装饰元素）
- **检查新元素可见性：** 建完 UI 元素后确认尺寸非零且落在父级范围内。有意先隐藏（之后切换显示、做动画等）的元素不算问题

**遵守规格：**
- **精确执行用户给的规格：** 用户给出像素尺寸、十六进制颜色、坐标、间距等精确值时，原样应用 —— 不要近似或替换
- **尽量减少无关改动：** 编辑时不要动用户没提的属性，除非修复本身必须配套调整

## 约定

**先沿用项目既有模式。** 套默认值之前先搜现有文件。

| 类型 | 约定 | 好 | 差 |
|------|------------|------|-----|
| GameObject 命名 | 大驼峰 | `SubmitButton` | `submit-button` |
| Prefab 路径 | 按功能分目录 | `Assets/UI/Inventory/` | `Assets/Prefabs/UI/` |

## 工作流

1. **核实状态** —— 动手前先查场景/层级里已有什么。
2. **分析** —— 明确到底需要什么，不要多加。
3. **搜索** —— 找已有的 prefab、canvas、资源，不要凭空猜路径。
4. **沿用项目模式** —— 与目录结构和命名保持一致。
5. **创建或编辑** —— 用正确的锚点搭结构，或做定点修改。
6. **确认结果** —— 验证改动生效后再进行下一步。

## Canvas 搭建

每个 UI 都需要 Canvas：

```
Canvas (Screen Space - Overlay 或 Camera)
├── CanvasScaler（推荐 Scale With Screen Size）
├── GraphicRaycaster
└── [UI 内容]
```

**CanvasScaler 设置：**
- 除非项目有明确理由用 "Constant Pixel Size"（例如像素风美术、固定分辨率目标），默认用 UI Scale Mode "Scale With Screen Size"
- Reference Resolution：跟随项目标准（例如 1920x1080）
- 用 Screen Space - Camera 建 Canvas 时，**先读相机的参考分辨率**
- Match Width Or Height：0.5（均衡）
- 若已有 Canvas 用 "Constant Pixel Size"，先指出来并询问用户，再决定是否改
- 布局优先用锚点和 Layout Group，而不是绝对像素坐标

## 布局组件

**Layout Group 控制子级尺寸：**
- 父级挂了 Layout Group，就由它管理子级 RectTransform
- 子级的锚点和 sizeDelta 可能被父级覆盖
- 设值前先弄清尺寸由父级还是子级决定

**Vertical/Horizontal Layout Group：**
- Control Child Size：决定父级是否设定子级尺寸
- Child Force Expand：决定子级是否拉伸填满空间
- Control Child Size 关闭时，子级必须给出明确尺寸

**避免布局冲突：**
- 父级已经在控制时，不要再手设子级锚点/sizeDelta
- 不要给应保持固定尺寸的元素挂 Layout Group
- 嵌套 Layout Group 时每一层都要仔细配置
- 布局不对时先看父级设置，再改子级
- ContentSizeFitter 与开了 Control Child Size 的 Layout Group 挂在**同一个**对象上 = 冲突
- 子级挂 ContentSizeFitter、而父级开了 Control Child Size = ContentSizeFitter 被覆盖（白挂）
- Layout Group 父级配 ContentSizeFitter 时，在对应轴上关掉父级的 Control Child Size
- 常见组合：ScrollView 的 Content 挂 ContentSizeFitter + VerticalLayoutGroup，由 VLG 管子级、由 ContentSizeFitter 决定 Content 自身尺寸

**Grid Layout Group：**
- 用于背包网格、卡片布局
- Cell Size 必须显式设定 —— 子级按它定尺寸
- Constraint 控制行/列上限

**Content Size Fitter：**
- Horizontal/Vertical Fit：Preferred Size
- 用在需要"按内容撑开"的容器上
- 需要 Layout Element 或文本组件提供 preferred size

## RectTransform 锚点

**元素要有非零尺寸才可见：**
- 用 sizeDelta 显式设宽高，或
- 用拉伸锚点配正确的 offset，或
- 交给已开 Control Child Size 的父级 Layout Group 控制

**锚点配置顺序：**
1. 先设锚点预设（贴角、贴边或拉伸）
2. 再设位置/偏移值
3. 最后确认结果尺寸非零

**常见模式：**
- **拉伸锚点** —— 用于填满可用空间的自适应元素
- **贴角锚点** —— 用于固定位置、固定尺寸的元素
- **贴边锚点** —— 用于只沿一个方向拉伸的元素

**按自然语言描述定位：**
用户描述位置时（例如"右上角""底部条""左侧"）：
1. 判断它是**贴角**（固定点）、**贴边**（沿一轴拉伸）还是**填满**（两轴都拉伸）
2. 设 anchorMin 与 anchorMax —— 贴角时两者同点；贴边/填满时是一个区间
3. **pivot 与锚点对齐** —— pivot 必须和元素被锚定的位置一致，不能停留在默认的 (0.5, 0.5)。"右上角"元素的 pivot 要在右上角；"顶部条"的 pivot 要在上边中点
4. 锚点和 pivot 都配好**之后**，再设位置/偏移值

**可见性检查表：**
- 宽和高都大于零
- 元素落在父级范围内
- 元素没有被同级元素挡住（检查层级顺序）
- Image 组件有 sprite，或颜色 alpha > 0

## 常用组件

| 组件 | 用途 |
|-----------|----------|
| `Image` | 底图、图标 |
| `RawImage` | Render Texture、视频 |
| `Text (TMP)` | 所有文本（用 TextMeshPro） |
| `Button` | 可点击元素 |
| `Toggle` | 复选框、单选框 |
| `Slider` | 数值区间 |
| `ScrollRect` | 可滚动内容 |
| `InputField (TMP)` | 文本输入 |

## 最佳实践

- 所有文本用 TextMeshPro（不用旧版 Text）
- 带文字的 WorldSpace UI 也要用 TextMeshPro；先检查项目，缺少 TMP Essentials 时导入
- 若导入了 TextMeshPro Essentials，资源导入完成后务必关掉 TMP Importer 窗口和 Import Unity Package 窗口
- 层级按逻辑组织（Header、Content、Footer）
- 能用 Layout Group 就别手工定位
- 非交互图片设 Raycast Target = false
- 用 sprite 图集提升性能

**绝不要用** `EditorApplication.ExecuteMenuItem("Window/TextMeshPro/Import TMP Essential Resources")`，除非用户明确要求交互式安装 TMP Essentials。它会弹出模态对话框，把调用它的流程一直阻塞到有人手动点掉。

**不要用** `AssetDatabase.ImportPackage()` 导入 TMP 资源，应该用 `TMP_PackageResourceImporter.ImportResources()` 这个规范的非交互 API。

## 交互可用性

凡是带交互元素的 UI，完成前必须确认：

1. 场景里存在 **EventSystem**（且只有一个）
2. Canvas 上挂了 **GraphicRaycaster**
3. 交互元素的 **Raycast Target = true**，非交互元素为 false 以免挡点击
4. **Button.onClick 已连线**（通过 Inspector 或脚本）—— 仅在要求了脚本或逻辑时才需要

前三条缺失时，交互元素看起来在、点起来却毫无反应。

## 理解现有 UI

用户对现有 UI 提问时：

**先读层级。** 不要猜 —— 回答前一律先检查场景或 prefab。

**分析结构：**
- 找出 Canvas 及其渲染模式
- 理清父子关系
- 判断哪些 Layout Group 控制哪些子级
- 检查 RectTransform 的锚点配置

**可以回答的问题：**
- "这个按钮做什么？" → 说明组件、层级位置、事件连线
- "这里是怎样布局的？" → 说明 Layout Group、锚点、层级
- "为什么它不可见？" → 检查尺寸、锚点、父级范围、组件状态
- "这个元素的尺寸由什么决定？" → 追 Layout Group 设置或锚点

## 编辑

针对现有 UI 做定点修改：

**改之前先读。** 一律先检查当前状态。

**编辑工作流：**
1. 确认目标对象存在
2. 找出要改的具体属性或组件
3. 只做必需的最小改动
4. 验证结果后再继续

**绝不靠销毁来修：**
- 销毁对象会连锁引发别处的空引用
- 就地改属性，而不是重建
- 真要移除元素，先把所有引用更新掉

## C#（仅在要求时）

- 用全限定 UI 类型名，避免命名空间冲突
- Inspector 引用用 `[SerializeField]`
- 在 Awake() 里缓存组件引用
- 按钮回调用事件/委托
- 脚本放在 prefab 同目录（沿用项目模式）

**组件引用：**
- 访问前先确认被引用的对象存在
- 处理序列化引用可能为 null 的情况
- 连线时确认目标组件已挂上

## 出错处理

出问题时：

**停下来诊断：**
- 找出确切的报错或现象
- 查清根因再动手
- 不要做猜测性修改

**增量修复：**
- 一次只解决一个问题
- 每修一个先验证，再进行下一个
- 记录改过什么

**避免破坏性做法：**
- "推倒重来"会把能用的元素和坏掉的一起毁掉
- 重建整个层级制造的问题比解决的更多
- 优先外科手术式修改，而非整体替换

**卡住时：**
- 重新核实层级当前状态
- 确认之前的改动是否真的生效
- 想想是不是方向本来就错了，而不是实现细节错了
