using System;
using System.Collections.Generic;

namespace IcarusII.Common.Spatial
{
    /// <summary>空间变量的三种主语。与 docs/dsp-agent.xlsx 的三张表一一对应。</summary>
    public enum SpatialKind
    {
        Region = 0,
        Point = 1,
        Motion = 2,
    }

    /// <summary>空间变量支持的方法，即 xlsx 的行。</summary>
    public enum SpatialMethod
    {
        List = 0,
        Storage = 1,
        Input = 2,
        Output = 3,
        Production = 4,
        Consumption = 5,
        Power = 6,
        Parent = 7,
        Location = 8,
        Navigation = 9,
        Information = 10,
        Note = 11,
    }

    /// <summary>
    /// 支持矩阵的唯一来源。
    /// 改动必须同时改 docs/dsp-agent.xlsx；SpatialSupportTests 会比对两边的清单。
    /// </summary>
    public static class SpatialTypes
    {
        // 区域
        public const string StarSystem = "star_system";
        public const string Star = "star";
        public const string Planet = "planet";
        public const string PowerGrid = "power_grid";
        public const string Factory = "factory";
        public const string DfHive = "df_hive";
        public const string DfBase = "df_base";
        public const string DysonSphere = "dyson_sphere";
        public const string WorldBox = "world_box";
        public const string PlanetPatch = "planet_patch";
        public const string Blueprint = "blueprint";

        // 定点
        public const string Building = "building";
        public const string Vein = "vein";
        public const string Vegetation = "vegetation";
        public const string DfSpaceBuilding = "df_space_building";
        public const string DfGroundBuilding = "df_ground_building";
        public const string WorldPosition = "world_position";
        public const string PlanetPosition = "planet_position";

        // 动点
        public const string Player = "player";
        public const string ConstructionDrone = "construction_drone";
        public const string DeliveryCourier = "delivery_courier";
        public const string StationDrone = "station_drone";
        public const string StationShip = "station_ship";
        public const string DysonRocket = "dyson_rocket";
        public const string CombatDrone = "combat_drone";
        public const string CombatFleet = "combat_fleet";
        public const string DfSpaceUnit = "df_space_unit";
        public const string DfGroundUnit = "df_ground_unit";
        public const string Trash = "trash";

        /// <summary>target 取 auto 时的占位类型，按玩家上下文推断。</summary>
        public const string Auto = "auto";

        public static readonly string[] RegionTypes =
        {
            StarSystem, Star, Planet, PowerGrid, Factory, DfHive, DfBase,
            DysonSphere, WorldBox, PlanetPatch, Blueprint,
        };

        public static readonly string[] PointTypes =
        {
            Building, Vein, Vegetation, DfSpaceBuilding, DfGroundBuilding,
            WorldPosition, PlanetPosition,
        };

        public static readonly string[] MotionTypes =
        {
            Player, ConstructionDrone, DeliveryCourier, StationDrone, StationShip,
            DysonRocket, CombatDrone, CombatFleet, DfSpaceUnit, DfGroundUnit, Trash,
        };

        public static string[] TypesOf(SpatialKind kind)
        {
            switch (kind)
            {
                case SpatialKind.Region: return RegionTypes;
                case SpatialKind.Point: return PointTypes;
                default: return MotionTypes;
            }
        }

        public static SpatialKind? KindOf(string type)
        {
            if (string.IsNullOrEmpty(type)) return null;
            for (int i = 0; i < RegionTypes.Length; i++)
                if (string.Equals(RegionTypes[i], type, StringComparison.Ordinal)) return SpatialKind.Region;
            for (int i = 0; i < PointTypes.Length; i++)
                if (string.Equals(PointTypes[i], type, StringComparison.Ordinal)) return SpatialKind.Point;
            for (int i = 0; i < MotionTypes.Length; i++)
                if (string.Equals(MotionTypes[i], type, StringComparison.Ordinal)) return SpatialKind.Motion;
            return null;
        }

        public static bool IsKnown(string type) => KindOf(type).HasValue;

        // ------------------------------------------------------------ 方法名

        private static readonly string[] MethodNames =
        {
            "list", "storage", "input", "output", "production", "consumption",
            "power", "parent", "location", "navigation", "information", "note",
        };

        public static string NameOf(SpatialMethod method)
        {
            var index = (int)method;
            return index >= 0 && index < MethodNames.Length ? MethodNames[index] : "unknown";
        }

        public static SpatialMethod? MethodOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < MethodNames.Length; i++)
                if (string.Equals(MethodNames[i], name, StringComparison.Ordinal))
                    return (SpatialMethod)i;
            return null;
        }

        // ------------------------------------------------------------ 支持矩阵

        /// <summary>
        /// (方法, 类型) 是否在 xlsx 里填了内容。留空即不支持。
        /// 这是工具拒绝调用的唯一依据，避免"AI 猜某个组合能用"。
        /// </summary>
        public static bool Supports(SpatialMethod method, string type)
        {
            var kind = KindOf(type);
            if (!kind.HasValue) return false;
            return Supports(method, kind.Value, type);
        }

        public static bool Supports(SpatialMethod method, SpatialKind kind, string type)
        {
            switch (kind)
            {
                case SpatialKind.Region: return SupportsRegion(method, type);
                case SpatialKind.Point: return SupportsPoint(method, type);
                default: return SupportsMotion(method, type);
            }
        }

        private static bool SupportsRegion(SpatialMethod method, string type)
        {
            switch (method)
            {
                // 区域页 11 列全填
                case SpatialMethod.List:
                case SpatialMethod.Output:
                case SpatialMethod.Production:
                case SpatialMethod.Consumption:
                case SpatialMethod.Power:
                case SpatialMethod.Parent:
                    return IsRegion(type);
                // 蓝图不是运行对象：没有位置、不能导航；storage 是其建筑清单
                case SpatialMethod.Storage:
                case SpatialMethod.Location:
                case SpatialMethod.Navigation:
                    return IsRegion(type) && type != Blueprint;
                // 恒星没有物流进出、也没有戴森球的流入；戴森球只从太阳帆/火箭获得
                case SpatialMethod.Input:
                    return IsRegion(type) && type != Star && type != DysonSphere;
                case SpatialMethod.Information:
                    return type == Star || type == Planet || type == DfHive
                        || type == DysonSphere || type == Blueprint;
                case SpatialMethod.Note:
                    return type == StarSystem || type == Star || type == Planet
                        || type == PowerGrid || type == Factory
                        || type == WorldBox || type == PlanetPatch || type == Blueprint;
                default:
                    return false;
            }
        }

        private static bool SupportsPoint(SpatialMethod method, string type)
        {
            var isContainer = type == Building || type == DfSpaceBuilding || type == DfGroundBuilding;
            switch (method)
            {
                case SpatialMethod.List:
                case SpatialMethod.Input:
                case SpatialMethod.Production:
                case SpatialMethod.Consumption:
                case SpatialMethod.Power:
                    return isContainer;
                case SpatialMethod.Storage:
                    return isContainer || type == Vein || type == Vegetation;
                case SpatialMethod.Output:
                    return isContainer || type == Vein;
                case SpatialMethod.Parent:
                case SpatialMethod.Location:
                case SpatialMethod.Navigation:
                    return IsPoint(type);
                // 只有建筑可能是全息信标；其余定点在表里是「（不适用）」
                case SpatialMethod.Note:
                    return type == Building;
                default:
                    return false;
            }
        }

        private static bool SupportsMotion(SpatialMethod method, string type)
        {
            switch (method)
            {
                // 只有玩家"拥有"其他动点，其它动点的 list 没有意义
                case SpatialMethod.List:
                    return type == Player;
                case SpatialMethod.Storage:
                    return type == Player || type == ConstructionDrone || type == DeliveryCourier
                        || type == StationDrone || type == StationShip || type == Trash;
                case SpatialMethod.Parent:
                    return IsMotion(type) && type != Player;
                case SpatialMethod.Location:
                case SpatialMethod.Navigation:
                case SpatialMethod.Information:
                    return IsMotion(type);
                default:
                    return false;
            }
        }

        public static bool IsRegion(string type) => Array.IndexOf(RegionTypes, type) >= 0;
        public static bool IsPoint(string type) => Array.IndexOf(PointTypes, type) >= 0;
        public static bool IsMotion(string type) => Array.IndexOf(MotionTypes, type) >= 0;

        /// <summary>该类型支持的全部方法，用于错误提示"它支持什么"。</summary>
        public static List<string> MethodsOf(string type)
        {
            var result = new List<string>();
            var kind = KindOf(type);
            if (!kind.HasValue) return result;
            for (int i = 0; i < MethodNames.Length; i++)
            {
                var method = (SpatialMethod)i;
                if (Supports(method, kind.Value, type)) result.Add(MethodNames[i]);
            }
            return result;
        }
    }
}
