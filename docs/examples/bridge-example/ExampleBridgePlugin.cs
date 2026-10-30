using BepInEx;

namespace ExampleBridge
{
    /// <summary>
    /// 最小示例：一个公开静态方法 + 一份 IcarusII.tools.json。
    /// 编译需要 BepInEx 5.4.17 的 BepInEx.dll 引用；这里只作为 SDK 示例，不参与本仓库构建。
    /// </summary>
    [BepInPlugin("com.example.bridge", "ExampleBridge", "1.0.0")]
    public sealed class ExampleBridgePlugin : BaseUnityPlugin
    {
        /// <summary>返回两个整数之和。</summary>
        public static int Add(int left, int right) => left + right;
    }
}
