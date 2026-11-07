using System;
using System.Globalization;
using IcarusII.Common.Json;

namespace IcarusII.Common.Spatial
{
    /// <summary>统计时间窗。数值取自游戏 ProductionStatistics 的 6 档。</summary>
    public enum StatWindow
    {
        Minute1 = 0,
        Minute10 = 1,
        Hour1 = 2,
        Hour10 = 3,
        Hour100 = 4,
        All = 5,
    }

    public enum StatUnit
    {
        PerMinute = 0,
        PerHour = 1,
        Total = 2,
    }

    /// <summary>口径：实际统计值，还是按配方与建筑数算出的理论值。</summary>
    public enum StatBasis
    {
        Actual = 0,
        Installed = 1,
    }

    /// <summary>
    /// 一次空间查询的规范化参数。
    ///
    /// 参数名、默认值来自 docs/dsp-agent.xlsx 的 B/C 两列；解析失败一律抛
    /// <see cref="SpatialException"/>，由工具层转成 invalid_args。
    /// </summary>
    public sealed class SpatialRequest
    {
        public const double DefaultRadius = 200;
        public const double MinRadius = 1;
        public const double MaxRadius = 2000;
        public const int DefaultLimit = 50;
        public const int MaxLimit = 200;
        public const int DefaultDepth = 1;
        public const int MaxDepth = 4;

        public SpatialMethod Method;
        /// <summary>显式类型；null 表示按 auto 从玩家上下文推断。</summary>
        public string Type;
        public string TargetId;
        public StatWindow Window = StatWindow.Minute10;
        public StatUnit Unit = StatUnit.PerMinute;
        public StatBasis Basis = StatBasis.Actual;
        public string ItemFilter;
        public int Limit = DefaultLimit;
        public int Offset;
        public int Depth = DefaultDepth;
        public double Radius = DefaultRadius;
        /// <summary>location 的坐标系：auto / planet / world。</summary>
        public string Frame = "auto";
        public int Precision = 3;

        public static SpatialRequest Parse(JsonValue args, SpatialMethod method)
        {
            args = args != null && args.IsObject ? args : JsonValue.NewObject();
            var request = new SpatialRequest { Method = method };

            var target = args["target"];
            if (target.IsString)
            {
                var text = target.AsString(string.Empty);
                if (!string.IsNullOrEmpty(text) && !string.Equals(text, SpatialTypes.Auto, StringComparison.Ordinal))
                    request.TargetId = text;
            }
            else if (target.IsObject)
            {
                var type = target["type"].AsString(null);
                if (!string.IsNullOrEmpty(type) && !string.Equals(type, SpatialTypes.Auto, StringComparison.Ordinal))
                    request.Type = NormalizeType(type);
                var id = target["id"].AsString(null);
                if (!string.IsNullOrEmpty(id)) request.TargetId = id;
                // 不传 type 时允许目标自带几何参数，等价于给了一个几何区域
                request.TargetSpec = target;
            }

            if (request.Type is null)
            {
                var type = args["type"].AsString(null);
                if (!string.IsNullOrEmpty(type) && !string.Equals(type, SpatialTypes.Auto, StringComparison.Ordinal))
                    request.Type = NormalizeType(type);
            }

            if (request.Type is not null && !SpatialTypes.IsKnown(request.Type))
                throw new SpatialException($"未知的类型：{request.Type}");

            request.Window = ParseWindow(args["window"].AsString(null));
            request.Unit = ParseUnit(args["unit"].AsString(null));
            request.Basis = ParseBasis(args["basis"].AsString(null));
            request.ItemFilter = Trimmed(args["itemFilter"].AsString(null));

            request.Limit = ClampInt(args["limit"].AsInt(DefaultLimit), 1, MaxLimit);
            request.Offset = Math.Max(0, args["offset"].AsInt(0));
            request.Depth = ClampInt(args["depth"].AsInt(DefaultDepth), 1, MaxDepth);
            request.Radius = ClampDouble(args["radius"].AsDouble(DefaultRadius), MinRadius, MaxRadius);

            var frame = args["frame"].AsString(null);
            if (!string.IsNullOrEmpty(frame)) request.Frame = frame.Trim().ToLowerInvariant();

            request.Precision = ClampInt(args["precision"].AsInt(3), 0, 6);
            return request;
        }

        /// <summary>target 是对象时保留原始描述，几何类区域靠它取参数。</summary>
        public JsonValue TargetSpec { get; private set; }

        /// <summary>类型明确、且该方法在该类型上成立；否则抛错。</summary>
        public void RequireSupported()
        {
            if (Type is null) return;
            if (SpatialTypes.Supports(Method, Type)) return;
            var supported = SpatialTypes.MethodsOf(Type);
            var list = supported.Count == 0 ? "无" : string.Join(" / ", supported);
            throw new SpatialException(
                $"类型 {Type} 不支持 {SpatialTypes.NameOf(Method)}；它支持：{list}");
        }

        /// <summary>统计类方法才接受 window / unit / basis。</summary>
        public bool IsStatMethod =>
            Method == SpatialMethod.Input || Method == SpatialMethod.Output
            || Method == SpatialMethod.Production || Method == SpatialMethod.Consumption;

        public static string NormalizeType(string type)
        {
            if (string.IsNullOrEmpty(type)) return type;
            return type.Trim().ToLowerInvariant().Replace('-', '_');
        }

        private static string Trimmed(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            var trimmed = value.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }

        public static StatWindow ParseWindow(string value)
        {
            if (string.IsNullOrEmpty(value)) return StatWindow.Minute10;
            switch (value.Trim().ToLowerInvariant())
            {
                case "1m": case "1min": case "minute": return StatWindow.Minute1;
                case "10m": case "10min": case "ten": return StatWindow.Minute10;
                case "1h": case "hour": return StatWindow.Hour1;
                case "10h": return StatWindow.Hour10;
                case "100h": return StatWindow.Hour100;
                case "all": case "total_all": return StatWindow.All;
                default:
                    throw new SpatialException($"window 只能是 1m / 10m / 1h / 10h / 100h / all，收到：{value}");
            }
        }

        public static StatUnit ParseUnit(string value)
        {
            if (string.IsNullOrEmpty(value)) return StatUnit.PerMinute;
            switch (value.Trim().ToLowerInvariant())
            {
                case "permin": case "per_min": case "perminute": return StatUnit.PerMinute;
                case "perhour": case "per_hour": case "perhourly": return StatUnit.PerHour;
                case "total": return StatUnit.Total;
                default:
                    throw new SpatialException($"unit 只能是 perMin / perHour / total，收到：{value}");
            }
        }

        public static StatBasis ParseBasis(string value)
        {
            if (string.IsNullOrEmpty(value)) return StatBasis.Actual;
            switch (value.Trim().ToLowerInvariant())
            {
                case "actual": return StatBasis.Actual;
                case "installed": return StatBasis.Installed;
                default:
                    throw new SpatialException($"basis 只能是 actual / installed，收到：{value}");
            }
        }

        /// <summary>统计窗口对应的分钟数；All 用 0 表示不折算。</summary>
        public static double WindowMinutes(StatWindow window)
        {
            switch (window)
            {
                case StatWindow.Minute1: return 1;
                case StatWindow.Minute10: return 10;
                case StatWindow.Hour1: return 60;
                case StatWindow.Hour10: return 600;
                case StatWindow.Hour100: return 6000;
                default: return 0;
            }
        }

        /// <summary>把窗口内的累计值折算成请求的单位。</summary>
        public static double ConvertUnit(double amount, StatWindow window, StatUnit unit)
        {
            if (unit == StatUnit.Total) return amount;
            var minutes = WindowMinutes(window);
            if (minutes <= 0) return amount;
            return unit == StatUnit.PerHour ? amount * 60.0 / minutes : amount / minutes;
        }

        private static int ClampInt(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static double ClampDouble(double value, double min, double max)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return min;
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public override string ToString()
        {
            var text = SpatialTypes.NameOf(Method) + ":" + (Type ?? SpatialTypes.Auto);
            if (!string.IsNullOrEmpty(TargetId)) text += "#" + TargetId;
            if (IsStatMethod)
                text += string.Format(CultureInfo.InvariantCulture, "[{0}/{1}/{2}]", Window, Unit, Basis);
            return text;
        }
    }

    /// <summary>空间查询的可预期失败；工具层转 invalid_args，不当作内部错误。</summary>
    public sealed class SpatialException : Exception
    {
        public SpatialException(string message) : base(message) { }
    }
}
