using System;
using System.Collections.Generic;
using Verse;

namespace RimWorldMCP.MapRendering
{
    /// <summary>
    /// 公共网格渲染器 — 从 chunk 工具中抽取的纯数据生成逻辑。
    /// 遍历矩形范围，调用 cellCharProvider 生成字符网格和已用符号集合。
    /// 不包含压缩、chunk 逻辑、输出格式化。
    /// </summary>
    public static class GridRenderer
    {
        /// <summary>字符网格渲染结果</summary>
        public struct GridResult
        {
            /// <summary>rows[z][x]，z 为相对偏移（0..height-1），x 为相对偏移（0..width-1）</summary>
            public char[][] Rows;
            /// <summary>网格中出现的所有唯一符号</summary>
            public HashSet<char> UsedSymbols;
            /// <summary>是否所有格子均为迷雾</summary>
            public bool AllFog;
        }

        /// <summary>
        /// 遍历矩形范围生成字符网格。
        /// minX/minZ/maxX/maxZ 为世界坐标（闭区间），cellCharProvider 决定每格的符号。
        /// </summary>
        public static GridResult RenderGrid(
            Map map,
            int minX, int minZ, int maxX, int maxZ,
            Func<IntVec3, Map, (char symbol, string? category)> cellCharProvider)
        {
            int w = maxX - minX + 1;
            int h = maxZ - minZ + 1;

            var usedSymbols = new HashSet<char>();
            bool allFog = true;

            var rows = new char[h][];
            for (int z = 0; z < h; z++)
            {
                rows[z] = new char[w];
                for (int x = 0; x < w; x++)
                {
                    var pos = new IntVec3(minX + x, 0, minZ + z);
                    var (symbol, _) = cellCharProvider(pos, map);
                    rows[z][x] = symbol;
                    usedSymbols.Add(symbol);
                    if (symbol != '█') allFog = false;
                }
            }

            return new GridResult
            {
                Rows = rows,
                UsedSymbols = usedSymbols,
                AllFog = allFog
            };
        }
    }
}
