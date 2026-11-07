using System;
using IcarusII.Common.Json;

namespace IcarusII.Common.Spatial
{
    /// <summary>
    /// 区域几何。纯数据，不含游戏对象；mod 层负责把 id 换成运行时状态。
    /// </summary>
    public sealed class RegionShape
    {
        /// <summary>planet / world 两种坐标系；行星表面与球体用 planet。</summary>
        public bool IsWorldFrame;

        /// <summary>行星区域：经纬度矩形。</summary>
        public double LatMin = -90;
        public double LatMax = 90;
        public double LonStart = -180;
        public double LonSpan = 360;

        /// <summary>世界区域：轴对齐长方体。</summary>
        public Vec3d BoxMin;
        public Vec3d BoxMax;

        /// <summary>球体：中心 + 半径。</summary>
        public Vec3d SphereCenter;
        public double SphereRadius;

        public double PlanetRadius;

        public bool ContainsPatch(double latitude, double longitude)
        {
            if (latitude < LatMin || latitude > LatMax) return false;
            return GeoMath.LongitudeInRange(longitude, LonStart, LonSpan);
        }

        public bool ContainsBox(Vec3d point)
        {
            return point.X >= BoxMin.X && point.X <= BoxMax.X
                && point.Y >= BoxMin.Y && point.Y <= BoxMax.Y
                && point.Z >= BoxMin.Z && point.Z <= BoxMax.Z;
        }

        public bool ContainsSphere(Vec3d point)
        {
            return Vec3d.Distance(point, SphereCenter) <= SphereRadius;
        }

        /// <summary>本形状的面积（行星表面用球面度折算成平方米）。</summary>
        public double SurfaceArea()
        {
            if (PlanetRadius <= 0) return 0;
            var latSpan = (LatMax - LatMin) * GeoMath.DegToRad;
            var lonSpan = Math.Min(LonSpan, 360) * GeoMath.DegToRad;
            return PlanetRadius * PlanetRadius * Math.Abs(Math.Sin(LatMax * GeoMath.DegToRad)
                - Math.Sin(LatMin * GeoMath.DegToRad)) * lonSpan;
        }

        public JsonValue ToJson()
        {
            var obj = JsonValue.NewObject();
            obj.Set("frame", IsWorldFrame ? "world" : "planet");
            if (IsWorldFrame)
            {
                obj.Set("min", Vector3Json(BoxMin));
                obj.Set("max", Vector3Json(BoxMax));
            }
            else
            {
                obj.Set("latMin", Round(LatMin));
                obj.Set("latMax", Round(LatMax));
                obj.Set("lonStart", Round(LonStart));
                obj.Set("lonSpan", Round(LonSpan));
            }
            return obj;
        }

        private static JsonValue Vector3Json(Vec3d value)
        {
            var obj = JsonValue.NewObject();
            obj.Set("x", Round(value.X, 1));
            obj.Set("y", Round(value.Y, 1));
            obj.Set("z", Round(value.Z, 1));
            return obj;
        }

        internal static double Round(double value, int digits = 3)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
            return Math.Round(value, digits, MidpointRounding.AwayFromZero);
        }
    }

    /// <summary>定时点在某一 tick 的状态快照。</summary>
    public sealed class MotionState
    {
        public long GameTick;
        /// <summary>世界坐标（宇宙系）。行星表面的目标同时给出行星局部坐标。</summary>
        public Vec3d UPosition;
        public int PlanetId;
        public bool OnPlanet;
        public double Latitude;
        public double Longitude;
        public double Altitude;
        public Vec3d Velocity;
        /// <summary>速度标量，米/秒。</summary>
        public double Speed;

        public JsonValue ToJson(int precision)
        {
            var obj = JsonValue.NewObject();
            obj.Set("gameTick", GameTick);
            if (OnPlanet)
            {
                obj.Set("latitude", RegionShape.Round(Latitude, precision));
                obj.Set("longitude", RegionShape.Round(Longitude, precision));
                obj.Set("altitude", RegionShape.Round(Altitude, 1));
                obj.Set("planetId", PlanetId);
            }
            else
            {
                var position = JsonValue.NewObject();
                position.Set("x", RegionShape.Round(UPosition.X, 1));
                position.Set("y", RegionShape.Round(UPosition.Y, 1));
                position.Set("z", RegionShape.Round(UPosition.Z, 1));
                obj.Set("position", position);
            }
            obj.Set("speed", RegionShape.Round(Speed, 2));
            return obj;
        }
    }
}
