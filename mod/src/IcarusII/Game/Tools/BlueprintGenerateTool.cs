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
            public float X;
            public float Y;
            public float Yaw;
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
                    list.Add(new BpBuilding
                    {
                        ProtoId = proto,
                        RecipeId = recipe,
                        X = (float)row["x"].AsDouble(0),
                        Y = (float)row["y"].AsDouble(0),
                        Yaw = (float)row["yaw"].AsDouble(0),
                    });
                }
            }
            else
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
                            Y = (float)(originY + r * spacingY),
                            Yaw = (float)yaw,
                        });
                    }
                }
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
                if (b.X < 0 || b.Y < 0) continue;
                if (b.X + 1 > maxX) maxX = b.X + 1;
                if (b.Y + 1 > maxY) maxY = b.Y + 1;
                var key = ((long)Math.Round(b.X * 10) << 32) ^ (long)Math.Round(b.Y * 10);
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
            var text = Encode(title, (int)Math.Ceiling(maxX), (int)Math.Ceiling(maxY), list);

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
            if (checkError != BlueprintDataIOError.OK || !check.isValid || check.buildings == null
                || check.buildings.Length != list.Count)
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

        private static string Encode(string title, int width, int height, List<BpBuilding> buildings)
        {
            var version = GameConfig.gameVersion.ToFullString();
            var ticks = DateTime.UtcNow.Ticks;
            // 与 BlueprintData.headerStr 完全一致：
            // BLUEPRINT:1,layout,icon0..4,0,ticks,gameVersion,shortDesc,author,customVersion,externalFields,desc"
            var header = "BLUEPRINT:1,10,0,0,0,0,0,0," + ticks.ToString(CultureInfo.InvariantCulture)
                + "," + version + "," + Uri.EscapeDataString(title) + ",,,,";

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
                        w.Write(b.X);
                        w.Write(0f);
                        w.Write(b.Y);
                        w.Write(b.Yaw);
                        w.Write(0);          // output obj
                        w.Write(0);          // input obj
                        w.Write((sbyte)0);   // outputToSlot
                        w.Write((sbyte)0);   // inputFromSlot
                        w.Write((sbyte)0);   // outputFromSlot
                        w.Write((sbyte)0);   // inputToSlot
                        w.Write((sbyte)0);   // outputOffset
                        w.Write((sbyte)0);   // inputOffset
                        w.Write((short)b.RecipeId);
                        w.Write((short)0);   // filterId
                        w.Write((short)0);   // parameters length
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
            var head = header + "\"";
            // 必须用游戏自己的 MD5F：它不是标准 MD5（同一段文本结果不同），
            // 自己算的签名会被 CheckSignature 判为 MD5CannotMatch。
            return head + b64 + "\"" + MD5F.Compute(head + b64);
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
