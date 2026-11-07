using System;
using System.Globalization;

namespace IcarusII.Common.Spatial
{
    /// <summary>双精度三维向量；Shared 不能引用 UnityEngine。</summary>
    public readonly struct Vec3d
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;

        public Vec3d(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly Vec3d Zero = new Vec3d(0, 0, 0);

        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public double SqrLength => X * X + Y * Y + Z * Z;

        public static Vec3d operator +(Vec3d a, Vec3d b) => new Vec3d(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3d operator -(Vec3d a, Vec3d b) => new Vec3d(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3d operator *(Vec3d a, double s) => new Vec3d(a.X * s, a.Y * s, a.Z * s);

        public Vec3d Normalized
        {
            get
            {
                var length = Length;
                return length > 1e-9 ? new Vec3d(X / length, Y / length, Z / length) : new Vec3d(0, 1, 0);
            }
        }

        public static double Distance(Vec3d a, Vec3d b) => (a - b).Length;

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})", X, Y, Z);
    }

    /// <summary>
    /// 经纬度与球面几何。行星局部坐标约定与游戏一致：
    /// y 轴为北极，+x 为经度 0 的切向，-z 为经度 0 点。
    /// </summary>
    public static class GeoMath
    {
        public const double RadToDeg = 180.0 / Math.PI;
        public const double DegToRad = Math.PI / 180.0;

        /// <summary>行星局部方向 → 经纬度（度）。</summary>
        public static (double Latitude, double Longitude) ToLatLon(Vec3d direction)
        {
            var n = direction.Normalized;
            var latitude = Math.Asin(Clamp(n.Y, -1, 1)) * RadToDeg;
            var longitude = Math.Atan2(n.X, -n.Z) * RadToDeg;
            return (latitude, longitude);
        }

        /// <summary>经纬度 + 半径 → 行星局部坐标。</summary>
        public static Vec3d FromLatLon(double latitude, double longitude, double radius)
        {
            var lat = latitude * DegToRad;
            var lon = longitude * DegToRad;
            var x = Math.Cos(lat) * Math.Sin(lon);
            var y = Math.Sin(lat);
            var z = -Math.Cos(lat) * Math.Cos(lon);
            return new Vec3d(x, y, z) * radius;
        }

        /// <summary>球面大圆距离（米）。</summary>
        public static double SurfaceDistance(double lat1, double lon1, double lat2, double lon2, double radius)
        {
            var p1 = lat1 * DegToRad;
            var p2 = lat2 * DegToRad;
            var dp = (lat2 - lat1) * DegToRad;
            var dl = (lon2 - lon1) * DegToRad;
            var a = Math.Sin(dp / 2) * Math.Sin(dp / 2)
                + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
            return 2 * radius * Math.Asin(Math.Sqrt(Clamp(a, 0, 1)));
        }

        /// <summary>方位角（度，正北为 0，顺时针）。</summary>
        public static double Bearing(double lat1, double lon1, double lat2, double lon2)
        {
            var p1 = lat1 * DegToRad;
            var p2 = lat2 * DegToRad;
            var dl = (lon2 - lon1) * DegToRad;
            var y = Math.Sin(dl) * Math.Cos(p2);
            var x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
            var deg = Math.Atan2(y, x) * RadToDeg;
            return deg < 0 ? deg + 360 : deg;
        }

        /// <summary>经度归一化到 [-180, 180)。</summary>
        public static double NormalizeLongitude(double longitude)
        {
            var value = longitude;
            while (value >= 180) value -= 360;
            while (value < -180) value += 360;
            return value;
        }

        /// <summary>跨 180° 经线的区间长度；lonSpan 允许超过 180。</summary>
        public static bool LongitudeInRange(double longitude, double start, double span)
        {
            if (span >= 360) return true;
            var delta = NormalizeLongitude(longitude - start);
            if (delta < 0) delta += 360;
            return delta <= span;
        }

        public static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        /// <summary>地面两点间的三维距离（含高度差）。</summary>
        public static double GroundDistance(double lat1, double lon1, double alt1,
            double lat2, double lon2, double alt2, double radius)
        {
            var surface = SurfaceDistance(lat1, lon1, lat2, lon2, radius);
            var dz = alt2 - alt1;
            return Math.Sqrt(surface * surface + dz * dz);
        }
    }
}
