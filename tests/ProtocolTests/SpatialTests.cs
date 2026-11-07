using System;
using IcarusII.Common.Json;
using IcarusII.Common.Spatial;

namespace IcarusII.Tests
{
    /// <summary>
    /// 空间变量的离线测试。
    ///
    /// 覆盖两件不能出错的事：
    /// 1. (方法, 类型) 支持表与 docs/dsp-agent.xlsx 一致——表填了什么，工具就只接受什么；
    /// 2. 参数解析与几何换算的边界，尤其是 unit/window 折算与经纬度跨 180° 的判定。
    /// </summary>
    internal static class SpatialTests
    {
        public static void Run()
        {
            SupportMatrixTests();
            RequestParseTests();
            UnitConversionTests();
            GeometryTests();
            MethodNameTests();
            NavigationRoutingTests();
        }

        // ------------------------------------------------------------ 支持矩阵

        private static void SupportMatrixTests()
        {
            // 区域 list：只有戴森球留空（它的成员由"恒星"列枚举）
            foreach (var type in SpatialTypes.RegionTypes)
            {
                var expected = type != SpatialTypes.DysonSphere;
                Program.Check($"区域 list {(expected ? "支持" : "不支持")} {type}",
                    SpatialTypes.Supports(SpatialMethod.List, type) == expected);
            }

            // 蓝图能读建筑清单与理论流量，但没有位置、不能导航
            Program.Check("蓝图支持 list",
                SpatialTypes.Supports(SpatialMethod.List, SpatialTypes.Blueprint));
            Program.Check("蓝图不支持 location",
                !SpatialTypes.Supports(SpatialMethod.Location, SpatialTypes.Blueprint));
            Program.Check("蓝图不支持 navigation",
                !SpatialTypes.Supports(SpatialMethod.Navigation, SpatialTypes.Blueprint));
            Program.Check("蓝图支持 information",
                SpatialTypes.Supports(SpatialMethod.Information, SpatialTypes.Blueprint));
            Program.Check("蓝图支持 note",
                SpatialTypes.Supports(SpatialMethod.Note, SpatialTypes.Blueprint));

            // 恒星与戴森球没有物流输入（只有太阳帆/火箭这种专属流入）
            Program.Check("恒星不支持 input", !SpatialTypes.Supports(SpatialMethod.Input, SpatialTypes.Star));
            Program.Check("戴森球不支持 input", !SpatialTypes.Supports(SpatialMethod.Input, SpatialTypes.DysonSphere));
            Program.Check("行星支持 input", SpatialTypes.Supports(SpatialMethod.Input, SpatialTypes.Planet));

            // 定点：只有容器类有流量，坐标类没有
            Program.Check("建筑支持 production",
                SpatialTypes.Supports(SpatialMethod.Production, SpatialTypes.Building));
            Program.Check("矿脉不支持 production",
                !SpatialTypes.Supports(SpatialMethod.Production, SpatialTypes.Vein));
            Program.Check("矿脉支持 output",
                SpatialTypes.Supports(SpatialMethod.Output, SpatialTypes.Vein));
            Program.Check("世界坐标只支持 parent/location/navigation",
                SpatialTypes.MethodsOf(SpatialTypes.WorldPosition).Count == 3);

            // 动点：只有玩家有 list
            Program.Check("玩家支持 list", SpatialTypes.Supports(SpatialMethod.List, SpatialTypes.Player));
            Program.Check("垃圾块不支持 list",
                !SpatialTypes.Supports(SpatialMethod.List, SpatialTypes.Trash));
            Program.Check("垃圾块支持 information",
                SpatialTypes.Supports(SpatialMethod.Information, SpatialTypes.Trash));
            Program.Check("玩家不支持 parent",
                !SpatialTypes.Supports(SpatialMethod.Parent, SpatialTypes.Player));

            // 未知类型与未知方法一律拒绝
            Program.Check("未知类型不受支持", !SpatialTypes.Supports(SpatialMethod.List, "not_a_type"));
            Program.Check("类型可归类", SpatialTypes.KindOf(SpatialTypes.Vein) == SpatialKind.Point);
            Program.Check("类别可判定", SpatialTypes.IsMotion(SpatialTypes.Trash));

            // 每张表的类型数必须与 xlsx 的列数一致
            Program.Eq("区域类型数", 11, SpatialTypes.RegionTypes.Length);
            Program.Eq("定点类型数", 7, SpatialTypes.PointTypes.Length);
            Program.Eq("动点类型数", 11, SpatialTypes.MotionTypes.Length);
        }

        // ------------------------------------------------------------ 参数解析

        private static void RequestParseTests()
        {
            var empty = SpatialRequest.Parse(JsonValue.NewObject(), SpatialMethod.List);
            Program.Eq("window 默认 10m", StatWindow.Minute10, empty.Window);
            Program.Eq("unit 默认 perMin", StatUnit.PerMinute, empty.Unit);
            Program.Eq("basis 默认 actual", StatBasis.Actual, empty.Basis);
            Program.Eq("limit 默认 50", 50, empty.Limit);
            Program.Eq("depth 默认 1", 1, empty.Depth);
            Program.Eq("radius 默认 200", 200.0, empty.Radius);
            Program.Check("target=auto 不解析成 id", empty.TargetId is null);

            var args = JsonValue.Parse(
                "{\"op\":\"production\",\"type\":\"planet\",\"target\":\"5\",\"window\":\"1h\"," +
                "\"unit\":\"perHour\",\"basis\":\"installed\",\"limit\":500,\"offset\":-3}");
            var request = SpatialRequest.Parse(args, SpatialMethod.Production);
            Program.Eq("window 1h", StatWindow.Hour1, request.Window);
            Program.Eq("unit perHour", StatUnit.PerHour, request.Unit);
            Program.Eq("basis installed", StatBasis.Installed, request.Basis);
            Program.Eq("target id", "5", request.TargetId);
            Program.Eq("limit 上限截断", SpatialRequest.MaxLimit, request.Limit);
            Program.Eq("offset 负数归零", 0, request.Offset);

            // 类型大小写与连字符都要归一
            var normalized = SpatialRequest.Parse(
                JsonValue.Parse("{\"type\":\"Power-Grid\"}"), SpatialMethod.Storage);
            Program.Eq("类型归一", SpatialTypes.PowerGrid, normalized.Type);

            // 未知类型必须在解析期拒绝，不能等到执行期
            var rejected = false;
            try
            {
                SpatialRequest.Parse(JsonValue.Parse("{\"type\":\"nope\"}"), SpatialMethod.List);
            }
            catch (SpatialException)
            {
                rejected = true;
            }
            Program.Check("未知类型解析期报错", rejected);

            // 非法枚举值也要拒绝，不能静默用默认值
            var badWindow = false;
            try
            {
                SpatialRequest.Parse(JsonValue.Parse("{\"window\":\"5m\"}"), SpatialMethod.Output);
            }
            catch (SpatialException)
            {
                badWindow = true;
            }
            Program.Check("非法 window 报错", badWindow);

            // RequireSupported 给出该类型支持什么
            var message = string.Empty;
            try
            {
                var unsupported = SpatialRequest.Parse(
                    JsonValue.Parse("{\"type\":\"trash\"}"), SpatialMethod.List);
                unsupported.RequireSupported();
            }
            catch (SpatialException ex)
            {
                message = ex.Message;
            }
            Program.Check("不支持时列出可用方法",
                message.Contains("storage") && message.Contains("location"));

            // 每个 xlsx 声明过的参数都必须真的落到 request 上，
            // 否则会出现"schema 里有、实现忽略"的静默丢失。
            var all = SpatialRequest.Parse(JsonValue.Parse(
                "{\"target\":\"7\",\"limit\":5,\"offset\":2,\"filter\":\"铁\",\"kind\":\"building\"," +
                "\"itemFilter\":\"铁块\",\"window\":\"1h\",\"unit\":\"perHour\",\"basis\":\"installed\"," +
                "\"depth\":3,\"frame\":\"world\",\"precision\":5,\"radius\":80}"),
                SpatialMethod.List);
            Program.Eq("target 落地", "7", all.TargetId);
            Program.Eq("limit 落地", 5, all.Limit);
            Program.Eq("offset 落地", 2, all.Offset);
            Program.Eq("filter 落地", "铁", all.MemberFilter);
            Program.Eq("kind 落地", "building", all.MemberKind);
            Program.Eq("itemFilter 落地", "铁块", all.ItemFilter);
            Program.Eq("depth 落地", 3, all.Depth);
            Program.Eq("frame 落地", "world", all.Frame);
            Program.Eq("precision 落地", 5, all.Precision);
            Program.Eq("radius 落地", 80.0, all.Radius);
        }

        // ------------------------------------------------------------ 单位折算

        private static void UnitConversionTests()
        {
            Program.Eq("10m 累计转每分钟", 12.0,
                SpatialRequest.ConvertUnit(120, StatWindow.Minute10, StatUnit.PerMinute));
            Program.Eq("10m 累计转每小时", 720.0,
                SpatialRequest.ConvertUnit(120, StatWindow.Minute10, StatUnit.PerHour));
            Program.Eq("10m 累计转总量", 120.0,
                SpatialRequest.ConvertUnit(120, StatWindow.Minute10, StatUnit.Total));
            Program.Eq("1h 累计转每分钟", 2.0,
                SpatialRequest.ConvertUnit(120, StatWindow.Hour1, StatUnit.PerMinute));
            Program.Eq("all 不折算", 120.0,
                SpatialRequest.ConvertUnit(120, StatWindow.All, StatUnit.PerMinute));

            Program.Eq("窗口分钟数 100h", 6000.0, SpatialRequest.WindowMinutes(StatWindow.Hour100));
        }

        // ------------------------------------------------------------ 几何

        private static void GeometryTests()
        {
            var (lat, lon) = GeoMath.ToLatLon(new Vec3d(0, 1, 0));
            Program.Eq("北极纬度", 90.0, Math.Round(lat, 6));

            var equator = GeoMath.FromLatLon(0, 0, 100);
            Program.Check("赤道 0° 朝 -Z", equator.Z < -99.9);

            var roundTrip = GeoMath.ToLatLon(GeoMath.FromLatLon(35.5, 120.25, 200));
            Program.Eq("经纬度往返 纬度", 35.5, Math.Round(roundTrip.Latitude, 6));
            Program.Eq("经纬度往返 经度", 120.25, Math.Round(roundTrip.Longitude, 6));

            // 跨 180° 的经度区间：起始 170，跨 20 度，应包含 -175 而不是 0
            Program.Check("跨 180° 包含 -175", GeoMath.LongitudeInRange(-175, 170, 20));
            Program.Check("跨 180° 排除 0", !GeoMath.LongitudeInRange(0, 170, 20));
            Program.Check("经度归一化", Math.Abs(GeoMath.NormalizeLongitude(190) + 170) < 1e-9);

            // 大圆距离：赤道上 90 度应等于 1/4 周长
            var quarter = GeoMath.SurfaceDistance(0, 0, 0, 90, 1000);
            Program.Eq("90° 弧长", Math.Round(Math.PI / 2 * 1000, 6), Math.Round(quarter, 6));

            // 方位角：正东 90°，正南 180°
            Program.Eq("正东方位角", 90.0, Math.Round(GeoMath.Bearing(0, 0, 0, 10), 6));
            Program.Eq("正南方位角", 180.0, Math.Round(GeoMath.Bearing(10, 0, 0, 0), 6));

            // 行星区域判定
            var shape = new RegionShape { LatMin = -10, LatMax = 10, LonStart = 100, LonSpan = 30 };
            Program.Check("区域内", shape.ContainsPatch(0, 110));
            Program.Check("纬度过界", !shape.ContainsPatch(20, 110));
            Program.Check("经度过界", !shape.ContainsPatch(0, 50));

            // 世界区域的 AABB
            var box = new RegionShape
            {
                IsWorldFrame = true,
                BoxMin = new Vec3d(0, 0, 0),
                BoxMax = new Vec3d(10, 10, 10),
            };
            Program.Check("盒内", box.ContainsBox(new Vec3d(5, 5, 5)));
            Program.Check("盒外", !box.ContainsBox(new Vec3d(11, 5, 5)));

            // 球体判定
            var sphere = new RegionShape { SphereCenter = Vec3d.Zero, SphereRadius = 10 };
            Program.Check("球内", sphere.ContainsSphere(new Vec3d(0, 0, 9)));
            Program.Check("球外", !sphere.ContainsSphere(new Vec3d(0, 0, 11)));
        }

        /// <summary>
        /// 方位指示按 kind/type 分流到区域还是定点，动点必须被挡住。
        /// 这段判定在 TetherAddTool.AddFromSpace 里，无法离线跑，
        /// 但它依赖的归类规则可以在这里钉住。
        /// </summary>
        private static void NavigationRoutingTests()
        {
            // 三个 kind 字符串与类型归类要对得上
            Program.Check("player 归为动点", SpatialTypes.IsMotion(SpatialTypes.Player));
            Program.Check("building 归为定点", SpatialTypes.IsPoint(SpatialTypes.Building));
            Program.Check("planet 归为区域", SpatialTypes.IsRegion(SpatialTypes.Planet));

            // 动点里没有能被 tether 表达的类型；区域/定点里也没有会被误判成动点的
            foreach (var type in SpatialTypes.MotionTypes)
            {
                Program.Check($"动点 {type} 不该被当成定点",
                    !SpatialTypes.IsPoint(type));
            }

            // navigation 在矩阵里是"支持"的，但它在只读工具里只负责报错引路，
            // 所以这里断言的是"三类主语都声明支持"，而不是真的有执行路径。
            Program.Check("区域声明支持 navigation",
                SpatialTypes.Supports(SpatialMethod.Navigation, SpatialTypes.Planet));
            Program.Check("定点声明支持 navigation",
                SpatialTypes.Supports(SpatialMethod.Navigation, SpatialTypes.Vein));
            Program.Check("动点声明支持 navigation",
                SpatialTypes.Supports(SpatialMethod.Navigation, SpatialTypes.Player));

            // 解析 space 时用 Location 借住，不能命中统计类分支
            var request = SpatialRequest.Parse(
                JsonValue.Parse("{\"type\":\"building\",\"target\":\"42\"}"),
                SpatialMethod.Location);
            Program.Check("借用解析不落到统计类", !request.IsStatMethod);
            Program.Eq("借用解析仍能读 target", "42", request.TargetId);
        }

        // ------------------------------------------------------------ 方法名

        private static void MethodNameTests()
        {
            Program.Eq("方法名往返", SpatialMethod.Consumption,
                SpatialTypes.MethodOf("consumption"));
            Program.Eq("方法名输出", "production", SpatialTypes.NameOf(SpatialMethod.Production));
            Program.Check("未知方法返回 null", SpatialTypes.MethodOf("nope") is null);

            // 12 个方法名与 xlsx 的行一致
            Program.Eq("方法总数", 12, Enum.GetValues(typeof(SpatialMethod)).Length);
        }
    }
}
