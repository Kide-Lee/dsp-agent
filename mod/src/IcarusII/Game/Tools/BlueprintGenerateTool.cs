using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using IcarusII.Common.Json;
using IcarusII.Game;
using UnityEngine;

namespace IcarusII.Game.Tools
{
    /// <summary>
    /// 从模版生成蓝图（P4 v1）。
    ///
    /// 只做「机器阵列」：给定配方与行列/间距，生成一张只含生产设备的合法蓝图，
    /// 传送带/分拣器留给玩家自己接。格式依据游戏 BlueprintData 与社区解析器
    /// （antian369/dsp-calc 的 blueprint/parser.js，木兰宽松许可证 v2）：
    ///
    ///   header "BLUEPRINT:0,layout,5 icons,0,ticks,version,shortDesc,desc&quot;
    ///   + base64(gzip(binary)) + &quot; + md5(header+base64)
    ///
    /// 二进制：version/cursor/dragBox/primaryArea + areas(14B) + buildings(81B)。
    /// 建筑前缀 -101 表示带倾斜字段的新版格式（与 0.10.31 一致）。
    /// </summary>
    internal sealed class BlueprintGenerateTool : IGameToolHandler
    {
        public string Name => "game_generate_blueprint";
        public int TimeoutMs => 20000;

        private const int DefaultAssembler = 2303; // 制造台 Mk.I
        private const int MaxBuildings = 800;

        private sealed class BpBuilding
        {
            public int ProtoId;
            public int RecipeId;
            // 地面坐标 X/Z，高度 Y；与蓝图二进制的 (localOffset_x, localOffset_y, localOffset_z) 对应。
            public float X;
            public float Z;
            public float Alt;
            public float X2;
            public float Z2;
            public float Alt2;
            public float Yaw;
            public float Tilt;
            public float Pitch;
            public float Yaw2;
            public float Tilt2;
            public float Pitch2;
            public int OutputObjIndex = -1;
            public int InputObjIndex = -1;
            public sbyte OutputToSlot;
            public sbyte InputFromSlot;
            public sbyte OutputFromSlot;
            public sbyte InputToSlot;
            public int FilterId;
            public int[] Params;
        }

        public IGameToolJob Start(JsonValue args)
        {
            var planet = GameToolFormat.ResolvePlanet(args);
            if (planet == null) throw new ArgumentException("找不到行星");
            return new OneShotJob(() => Build(planet, args));
        }

        private static GameToolJobResult Build(PlanetData planet, JsonValue args)
        {
            var data = JsonValue.NewObject();
            var recipeId = args["recipeId"].AsInt(0);
            var layout = args["layout"].AsString("template");
            var explicitRows = args["buildings"];
            var list = new List<BpBuilding>();

            if (explicitRows != null && explicitRows.IsArray && explicitRows.Count > 0)
            {
                for (int i = 0; i < explicitRows.Count && list.Count < MaxBuildings; i++)
                {
                    var row = explicitRows[i];
                    if (row == null || !row.IsObject) continue;
                    var proto = row["protoId"].AsInt(0);
                    var recipe = row["recipeId"].AsInt(recipeId);
                    if (proto <= 0 || recipe <= 0) continue;

                    // 兼容两种写法：
                    //   {x, z, y}  = Unity 习惯：x 东西、z 南北、y 高度；
                    //   {x, y}     = 蓝图习惯：x 东西、y 南北、高度 0。
                    var groundZ = row["z"] != null && !row["z"].IsNull
                        ? row["z"].AsDouble(0)
                        : row["y"].AsDouble(0);
                    var alt = row["z"] != null && !row["z"].IsNull
                        ? row["y"].AsDouble(0)
                        : row["height"].AsDouble(0);
                    var b = new BpBuilding
                    {
                        ProtoId = proto,
                        RecipeId = recipe,
                        X = (float)row["x"].AsDouble(0),
                        Z = (float)groundZ,
                        Alt = (float)alt,
                        Yaw = (float)row["yaw"].AsDouble(0),
                        Tilt = (float)row["tilt"].AsDouble(0),
                        Pitch = (float)row["pitch"].AsDouble(0),
                        Yaw2 = (float)row["yaw2"].AsDouble(row["yaw"].AsDouble(0)),
                        Tilt2 = (float)row["tilt2"].AsDouble(0),
                        Pitch2 = (float)row["pitch2"].AsDouble(0),
                        FilterId = row["filterId"].AsInt(0),
                        OutputObjIndex = row["outputObjIndex"].AsInt(-1),
                        InputObjIndex = row["inputObjIndex"].AsInt(-1),
                        OutputToSlot = (sbyte)row["outputToSlot"].AsInt(0),
                        InputFromSlot = (sbyte)row["inputFromSlot"].AsInt(0),
                        OutputFromSlot = (sbyte)row["outputFromSlot"].AsInt(0),
                        InputToSlot = (sbyte)row["inputToSlot"].AsInt(0),
                    };
                    b.X2 = (float)row["x2"].AsDouble(b.X);
                    b.Z2 = (float)row["z2"].AsDouble(b.Z);
                    b.Alt2 = (float)row["height2"].AsDouble(b.Alt);
                    list.Add(b);
                }
            }
            else if (layout != "row")
            {
                if (recipeId <= 0)
                {
                    data.Set("saved", false);
                    data.Set("error", "recipe_required");
                    data.Set("display", "需要 recipeId（或显式 buildings 数组）");
                    return new GameToolJobResult { Data = data };
                }
                var recipe = LDB.recipes != null ? LDB.recipes.Select(recipeId) : null;
                if (recipe == null)
                {
                    data.Set("saved", false);
                    data.Set("error", "recipe_not_found");
                    return new GameToolJobResult { Data = data };
                }

                var protoId = args["protoId"].AsInt(0);
                if (protoId <= 0) protoId = PickBuilding(recipe.Type);
                var rows = Math.Max(1, Math.Min(40, args["rows"].AsInt(1)));
                var cols = Math.Max(1, Math.Min(60, args["cols"].AsInt(1)));
                var spacingX = args["spacingX"].AsDouble(0);
                var spacingY = args["spacingY"].AsDouble(0);
                if (spacingX <= 0) spacingX = 4;
                if (spacingY <= 0) spacingY = 4;
                var originX = args["originX"].AsDouble(0);
                var originY = args["originY"].AsDouble(0);
                var yaw = args["yaw"].AsDouble(0);

                for (int r = 0; r < rows; r++)
                {
                    for (int c = 0; c < cols && list.Count < MaxBuildings; c++)
                    {
                        list.Add(new BpBuilding
                        {
                            ProtoId = protoId,
                            RecipeId = recipeId,
                            X = (float)(originX + c * spacingX),
                            Z = (float)(originY + r * spacingY),
                            Yaw = (float)yaw,
                        });
                    }
                }
            }

            if (layout == "row" && (explicitRows == null || !explicitRows.IsArray || explicitRows.Count == 0))
            {
                BuildRow(args, recipeId, list);
            }

            if (list.Count == 0)
            {
                data.Set("saved", false);
                data.Set("error", "no_buildings");
                return new GameToolJobResult { Data = data };
            }

            // 计算范围并做最小合法性检查：不能重叠在同一格。
            float maxX = 0;
            float maxY = 0;
            var occupied = new HashSet<long>();
            foreach (var b in list)
            {
                var gx = Math.Max(b.X, b.X2);
                var gz = Math.Max(b.Z, b.Z2);
                if (gx + 1 > maxX) maxX = gx + 1;
                if (gz + 1 > maxY) maxY = gz + 1;
                var key = ((long)Math.Round(b.X * 10) << 32) ^ (long)Math.Round(b.Z * 10);
                if (!occupied.Add(key))
                {
                    data.Set("saved", false);
                    data.Set("error", "overlap");
                    data.Set("display", "生成失败：有两个建筑落在同一坐标");
                    return new GameToolJobResult { Data = data };
                }
            }
            if (maxX <= 0 || maxY <= 0)
            {
                data.Set("saved", false);
                data.Set("error", "bad_coordinates");
                return new GameToolJobResult { Data = data };
            }

            var title = args["name"].AsString(null);
            if (string.IsNullOrEmpty(title)) title = "IcarusII 产线";
            var iconItemId = args["iconItemId"].AsInt(0);
            if (iconItemId <= 0)
            {
                var rootRecipe = LDB.recipes.Select(recipeId);
                if (rootRecipe != null && rootRecipe.Results != null && rootRecipe.Results.Length > 0)
                    iconItemId = rootRecipe.Results[0];
            }
            var author = args["author"].AsString("");
            var bpVersion = args["version"].AsString("");
            var attributes = args["attributes"].AsString("");
            var description = args["description"].AsString("");
            var text = Encode(title, author, bpVersion, attributes, description, iconItemId,
                (int)Math.Ceiling(maxX), (int)Math.Ceiling(maxY), list);

            var safe = Sanitize(title);
            if (string.IsNullOrEmpty(safe)) safe = "IcarusII-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var dir = GameConfig.blueprintFolder;
            var fileName = safe + ".txt";
            var path = dir + fileName;
            for (int i = 2; i < 50 && File.Exists(path); i++)
            {
                fileName = safe + " (" + i.ToString(CultureInfo.InvariantCulture) + ").txt";
                path = dir + fileName;
            }

            // 用游戏完整的文件加载路径验收（header + 签名 + 内容），
            // CreateNew 只校验内容，会漏掉 MD5/header 问题。
            var check = new BlueprintData();
            var tmp = Path.Combine(Path.GetTempPath(), "icarus-bp-check.txt");
            BlueprintDataIOError checkError;
            try
            {
                File.WriteAllText(tmp, text, Encoding.ASCII);
                checkError = check.LoadBlueprintData(tmp);
            }
            catch (Exception ex)
            {
                checkError = BlueprintDataIOError.DataCorruption;
                data.Set("checkError", ex.Message);
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
            // 除了能加载，还要确认坐标/朝向原样回读，避免二进制字段顺序写错。
            bool coordsOk = checkError == BlueprintDataIOError.OK && check.buildings != null
                && check.buildings.Length == list.Count;
            if (coordsOk)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var src = list[i];
                    var got = check.buildings[i];
                    if (Math.Abs(got.localOffset_x - src.X) > 0.001f
                        || Math.Abs(got.localOffset_y - src.Z) > 0.001f
                        || Math.Abs(got.localOffset_z - src.Alt) > 0.001f
                        || got.recipeId != src.RecipeId)
                    {
                        coordsOk = false;
                        break;
                    }
                }
            }
            if (checkError != BlueprintDataIOError.OK || !check.isValid || check.buildings == null
                || check.buildings.Length != list.Count || !coordsOk)
            {
                data.Set("saved", false);
                data.Set("error", "encode_invalid");
                data.Set("checkError", checkError.ToString());
                data.Set("display", "生成失败：编码结果未通过游戏文件加载校验（" + checkError + "）");
                return new GameToolJobResult { Data = data };
            }

            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(path, text, Encoding.ASCII);
            }
            catch (Exception ex)
            {
                data.Set("saved", false);
                data.Set("error", ex.Message);
                return new GameToolJobResult { Data = data };
            }

            data.Set("saved", true);
            data.Set("path", path);
            data.Set("fileName", fileName);
            data.Set("buildings", list.Count);
            data.Set("width", (int)Math.Ceiling(maxX));
            data.Set("height", (int)Math.Ceiling(maxY));
            data.Set("bytes", text.Length);
            data.Set("verified", true);
            data.Set("display", "已生成蓝图 " + fileName + " · " + list.Count + " 台 · "
                + (int)Math.Ceiling(maxX) + "×" + (int)Math.Ceiling(maxY) + " 格");
            data.Set("note", "只含生产设备，不含传送带/分拣器；粘贴后自行接线。");
            return new GameToolJobResult { Data = data };
        }

        /// <summary>
        /// 单行产线：机器一排，上下各一条直带，爪子从带子进/出机器。
        /// 约定来自真实蓝图：爪子接带子时槽位 -1，接机器时用机器槽位；爪子参数 [length]。
        /// </summary>
        private static void BuildRow(JsonValue args, int recipeId, List<BpBuilding> list)
        {
            if (recipeId <= 0) return;
            var recipe = LDB.recipes != null ? LDB.recipes.Select(recipeId) : null;
            if (recipe == null) return;

            int count = Math.Max(1, Math.Min(30, args["count"].AsInt(args["cols"].AsInt(4))));
            float spacing = (float)Math.Max(3.0, args["spacingX"].AsDouble(4));
            int proto = args["protoId"].AsInt(0);
            if (proto <= 0) proto = PickBuilding(recipe.Type);
            int belt = args["beltId"].AsInt(2003);
            int inserter = args["inserterId"].AsInt(2014);
            // 与真实蓝图一致：机器在 2，进料带在 4、出料带在 0；
            // 爪子从带子跨 2 格到机器，parameters[0] = 2。
            float feedZ = 4f;
            float machineZ = 2f;
            float outZ = 0f;

            for (int i = 0; i < count; i++)
                list.Add(new BpBuilding { ProtoId = proto, RecipeId = recipeId, X = i * spacing, Z = machineZ });

            int inputStart = list.Count;
            for (int i = 0; i < count; i++)
                list.Add(new BpBuilding { ProtoId = belt, X = i * spacing, Z = feedZ, Yaw = 90f,
                    InputObjIndex = i == 0 ? -1 : inputStart + i - 1, OutputObjIndex = i + 1 < count ? inputStart + i + 1 : -1,
                    InputFromSlot = 0, OutputToSlot = 1, Params = new int[0] });

            int outputStart = list.Count;
            for (int i = 0; i < count; i++)
                list.Add(new BpBuilding { ProtoId = belt, X = i * spacing, Z = outZ, Yaw = 90f,
                    InputObjIndex = i == 0 ? -1 : outputStart + i - 1, OutputObjIndex = i + 1 < count ? outputStart + i + 1 : -1,
                    InputFromSlot = 0, OutputToSlot = 1, Params = new int[0] });

            for (int i = 0; i < count; i++)
            {
                int machineIndex = i;
                int inBelt = inputStart + i;
                int outBelt = outputStart + i;
                list.Add(new BpBuilding { ProtoId = inserter, X = i * spacing, Z = feedZ,
                    X2 = i * spacing, Z2 = machineZ, Yaw = 180f, Yaw2 = 180f,
                    InputObjIndex = inBelt, InputFromSlot = -1,
                    OutputObjIndex = machineIndex, OutputToSlot = 0,
                    Params = new int[] { 2 } });
                list.Add(new BpBuilding { ProtoId = inserter, X = i * spacing, Z = machineZ,
                    X2 = i * spacing, Z2 = outZ, Yaw = 0f, Yaw2 = 0f,
                    InputObjIndex = machineIndex, InputFromSlot = 0,
                    OutputObjIndex = outBelt, OutputToSlot = -1,
                    Params = new int[] { 2 } });
            }
        }

        /// <summary>测试入口：只跑 row 规划 + 编码，不需要行星/工厂。</summary>
        internal static string SelfTestRow(int recipeId, int count)
        {
            var args = JsonValue.NewObject();
            args.Set("count", count);
            var list = new List<BpBuilding>();
            BuildRow(args, recipeId, list);
            float maxX = 0, maxZ = 0;
            foreach (var b in list)
            {
                maxX = Math.Max(maxX, Math.Max(b.X, b.X2) + 1);
                maxZ = Math.Max(maxZ, Math.Max(b.Z, b.Z2) + 1);
            }
            var recipe = LDB.recipes.Select(recipeId);
            int icon = recipe != null && recipe.Results != null && recipe.Results.Length > 0 ? recipe.Results[0] : 0;
            return Encode("selftest", "", "", "", "", icon,
                (int)Math.Ceiling(maxX), (int)Math.Ceiling(maxZ), list);
        }

        /// <summary>按配方类型挑同类型里速度最低（基础档）的设备；找不到就用制造台 Mk.I。</summary>
        private static int PickBuilding(ERecipeType type)
        {
            var items = LDB.items != null ? LDB.items.dataArray : null;
            if (items != null)
            {
                int best = 0;
                int bestSpeed = int.MaxValue;
                foreach (var item in items)
                {
                    if (item == null || item.prefabDesc == null) continue;
                    if (!item.prefabDesc.isAssembler) continue;
                    if (item.prefabDesc.assemblerRecipeType != type) continue;
                    if (item.prefabDesc.assemblerSpeed < bestSpeed)
                    {
                        bestSpeed = item.prefabDesc.assemblerSpeed;
                        best = item.ID;
                    }
                }
                if (best > 0) return best;
            }
            return DefaultAssembler;
        }

        private static string Encode(string title, string author, string customVersion,
            string attributes, string description, int iconItemId, int width, int height, List<BpBuilding> buildings)
        {
            var version = GameConfig.gameVersion.ToFullString();
            var ticks = DateTime.UtcNow.Ticks;
            // 与 BlueprintData.headerStr 完全一致：
            // BLUEPRINT:1,layout,icon0..4,0,ticks,gameVersion,shortDesc,author,customVersion,externalFields,desc"
            var header = "BLUEPRINT:1,10," + iconItemId.ToString(CultureInfo.InvariantCulture)
                + ",0,0,0,0,0," + ticks.ToString(CultureInfo.InvariantCulture)
                + "," + version + "," + Esc(title) + "," + Esc(author) + "," + Esc(customVersion)
                + "," + Esc(attributes) + "," + Esc(description) + "\"";

            byte[] binary;
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
                {
                    w.Write(2);              // version
                    w.Write(6);              // cursor x
                    w.Write(2);              // cursor y
                    w.Write(0);              // cursor target area
                    w.Write(width);          // drag box x
                    w.Write(height);         // drag box y
                    w.Write(0);              // primary area
                    w.Write((byte)1);        // area count
                    w.Write((sbyte)0);       // index
                    w.Write((sbyte)-1);      // parent
                    w.Write((short)0);       // tropic anchor
                    w.Write((short)200);     // area segments
                    w.Write((short)0);       // anchor local x
                    w.Write((short)0);       // anchor local y
                    w.Write((short)width);
                    w.Write((short)height);
                    w.Write(buildings.Count);

                    for (int i = 0; i < buildings.Count; i++)
                    {
                        var b = buildings[i];
                        var item = LDB.items.Select(b.ProtoId);
                        var modelIndex = item != null && item.prefabDesc != null ? item.prefabDesc.modelIndex : 0;

                        w.Write(-101);       // 新版建筑前缀（带倾斜字段）
                        w.Write(i);          // index
                        w.Write((short)b.ProtoId);
                        w.Write((short)modelIndex);
                        w.Write((sbyte)0);   // area index
                        w.Write(b.X);        // 东西
                        w.Write(b.Z);        // 南北
                        w.Write(b.Alt);      // 高度
                        w.Write(b.Yaw);
                        if (b.ProtoId > 2000 && b.ProtoId < 2010)
                        {
                            w.Write(b.Tilt);
                        }
                        else if (b.ProtoId > 2010 && b.ProtoId < 2020)
                        {
                            w.Write(b.Tilt);
                            w.Write(b.Pitch);
                            w.Write(b.X2);
                            w.Write(b.Z2);
                            w.Write(b.Alt2);
                            w.Write(b.Yaw2);
                            w.Write(b.Tilt2);
                            w.Write(b.Pitch2);
                        }
                        w.Write(b.OutputObjIndex);
                        w.Write(b.InputObjIndex);
                        w.Write(b.OutputToSlot);
                        w.Write(b.InputFromSlot);
                        w.Write(b.OutputFromSlot);
                        w.Write(b.InputToSlot);
                        w.Write((sbyte)0);   // outputOffset
                        w.Write((sbyte)0);   // inputOffset
                        w.Write((short)b.RecipeId);
                        w.Write((short)b.FilterId);
                        var prms = b.Params ?? new int[0];
                        w.Write((short)prms.Length);
                        for (int pi = 0; pi < prms.Length; pi++) w.Write(prms[pi]);
                    }

                    // version 2 必须在建筑之后写 Patch 和 reformData 标志；
                    // 少了这两段，游戏 Import 会在 EOF 抛异常，蓝图被判无效。
                    w.Write(1);              // Patch = CURRENT_PATCH
                    w.Write((byte)0);        // 无地基 reformData
                }
                binary = ms.ToArray();
            }

            byte[] gz;
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, System.IO.Compression.CompressionLevel.Optimal, true))
                {
                    gzip.Write(binary, 0, binary.Length);
                }
                gz = output.ToArray();
            }

            var b64 = Convert.ToBase64String(gz);
            var head = header;
            // 必须用游戏自己的 MD5F：它不是标准 MD5（同一段文本结果不同），
            // 自己算的签名会被 CheckSignature 判为 MD5CannotMatch。
            return head + b64 + "\"" + MD5F.Compute(head + b64);
        }

        private static string Esc(string raw)
        {
            return Uri.EscapeDataString(raw ?? "");
        }

        private static string Sanitize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var chars = new char[raw.Length];
            int n = 0;
            foreach (var ch in raw)
            {
                if (char.IsControl(ch)) continue;
                switch (ch)
                {
                    case '<': case '>': case ':': case '"':
                    case '/': case '\\': case '|': case '?': case '*':
                        chars[n++] = ' ';
                        break;
                    default:
                        chars[n++] = ch;
                        break;
                }
            }
            var text = new string(chars, 0, n).Trim().Trim('.');
            if (text.Length > 80) text = text.Substring(0, 80).Trim();
            return text;
        }
    }
}
