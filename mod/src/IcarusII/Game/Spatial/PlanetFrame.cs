using System;
using System.Globalization;
using IcarusII.Common.Json;
using IcarusII.Common.Spatial;
using IcarusII.Game.Tools;
using UnityEngine;

namespace IcarusII.Game.Spatial
{
    /// <summary>
    /// 运行时坐标换算：行星局部 ↔ 世界（宇宙系）。
    /// 行星在自转/公转，所以任何跨 tick 的比较都必须先冻结到同一个 tick。
    /// </summary>
    internal static class PlanetFrame
    {
        /// <summary>行星局部方向 → 世界方向；用当前 tick 的姿态。</summary>
        public static VectorLF3 LocalToWorldDirection(PlanetData planet, Vector3 local)
        {
            if (planet == null) return VectorLF3.zero;
            var rotation = planet.runtimeRotation;
            return rotation * local.normalized;
        }

        /// <summary>行星局部坐标 → 世界坐标（含行星公转位置）。</summary>
        public static VectorLF3 LocalToWorld(PlanetData planet, Vector3 local)
        {
            if (planet == null) return VectorLF3.zero;
            var rotated = planet.runtimeRotation * local;
            return planet.uPosition + new VectorLF3(rotated.x, rotated.y, rotated.z);
        }

        /// <summary>世界坐标 → 该行星的局部坐标。</summary>
        public static Vector3 WorldToLocal(PlanetData planet, VectorLF3 world)
        {
            if (planet == null) return Vector3.zero;
            return Quaternion.Inverse(planet.runtimeRotation) * (world - planet.uPosition);
        }

        /// <summary>把世界坐标投到某个行星的经纬度；不在该行星上时返回 null。</summary>
        public static (double Latitude, double Longitude, double Altitude)? TryLatLon(
            PlanetData planet, VectorLF3 world)
        {
            if (planet == null) return null;
            var local = WorldToLocal(planet, world);
            if (local.sqrMagnitude < 1e-6f) return null;
            var (lat, lon) = GeoMath.ToLatLon(new Vec3d(local.x, local.y, local.z));
            var altitude = local.magnitude - planet.realRadius;
            return (lat, lon, altitude);
        }

        /// <summary>玩家是否站在这颗行星上。</summary>
        public static bool PlayerOnPlanet(PlanetData planet)
        {
            var player = GameMain.mainPlayer;
            if (player == null || planet == null) return false;
            return player.planetData != null && player.planetData.id == planet.id;
        }

        /// <summary>玩家的世界坐标；永远有效（在大气层内时等于行星表面坐标）。</summary>
        public static Vec3d PlayerUPosition()
        {
            var player = GameMain.mainPlayer;
            if (player == null) return Vec3d.Zero;
            var p = player.uPosition;
            return new Vec3d(p.x, p.y, p.z);
        }

        public static Vec3d ToVec3d(VectorLF3 value) => new Vec3d(value.x, value.y, value.z);
        public static Vec3d ToVec3d(Vector3 value) => new Vec3d(value.x, value.y, value.z);
        public static VectorLF3 ToVectorLF3(Vec3d value) => new VectorLF3(value.X, value.Y, value.Z);
        public static Vector3 ToVector3(Vec3d value) => new Vector3((float)value.X, (float)value.Y, (float)value.Z);

        public static string PlanetName(PlanetData planet) =>
            planet != null ? (planet.displayName ?? planet.name ?? string.Empty) : string.Empty;

        public static string StarName(StarData star) =>
            star != null ? (star.displayName ?? star.name ?? string.Empty) : string.Empty;

        /// <summary>用现有工具的 Location 结构输出行星坐标，保持与旧工具一致。</summary>
        public static JsonValue LocationJson(PlanetData planet, Vector3 local, PlanetData owner)
        {
            return GameToolFormat.Location(owner ?? planet, local);
        }

        public static string Format(double value, int digits) =>
            Math.Round(value, digits, MidpointRounding.AwayFromZero)
                .ToString("0." + new string('#', digits), CultureInfo.InvariantCulture);
    }
}
