# ScrollView 搭建

ScrollRect 需要特定层级才能正常工作。组件缺失或顺序错乱会导致抖动、重复项或内容无法滚动。

**必需层级：**
```
ScrollView (ScrollRect)
├── Viewport (RectTransform + Mask + Image)
│   └── Content (RectTransform + VerticalLayoutGroup + ContentSizeFitter)
│       └── [子节点 —— 静态或运行时添加]
└── Scrollbar（可选）
```

**配置规则：**
- **ScrollRect** 组件挂在 ScrollView 根对象上
- **Viewport** 必须挂 `Mask` 和 `Image`（Mask 依赖 Image）
- **Content** 必须挂 `ContentSizeFitter`，并把 Vertical Fit 设为 "Preferred Size"，滚动区才会随子级增长
- ScrollRect 引用：把 Content 指给 `Content`、Viewport 指给 `Viewport`，可选再挂一个 Scrollbar
- Content 里可以放静态子节点（设置列表、关于页面），也可以在运行时动态填充

**动态填充（运行时条目）：**
- 填充前**先清空已有子节点**，否则面板每次打开都会重复
- 把条目 prefab 实例化为 Content 的子节点
- Content 上的 ContentSizeFitter + VerticalLayoutGroup 会自动撑出可滚动区域

**常见故障：**
- 每次打开条目都翻倍 → 填充前没清空 Content
- 内容滚不动 → Content 缺 ContentSizeFitter，或 Viewport 缺 Mask
- 抖动/闪烁 → ContentSizeFitter 与父级 Layout Group 冲突（见主技能里的"避免布局冲突"）
