using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using RimWorld;
using Verse;

namespace RimWorldMCP.MapRendering
{
    public static class SymbolDictionary
    {
        private static Dictionary<string, char> _forward = new();
        private static Dictionary<char, (string DefName, string Label, string Category)> _reverse = new();
        private static string _dictHash = "";

        // ---- 符号池（兜底分配用） ----

        private static readonly List<string> FallbackPool = new()
        {
            "a","b","c","d","e","f","g","h","i","j","k","l","m","n","o","p","q","r","s","t","u","v","w","x","y","z",
            "A","B","C","D","E","F","G","H","I","J","K","L","M","N","O","P","Q","R","S","T","U","V","W","X","Y","Z",
            "0","1","2","3","4","5","6","7","8","9",
            "α","β","γ","δ","ε","ζ","η","θ","ι","κ","λ","μ","ν","ξ","ο","π","ρ","σ","τ","υ","φ","χ","ψ","ω",
            "Α","Β","Γ","Δ","Ε","Ζ","Η","Θ","Ι","Κ","Λ","Μ","Ν","Ξ","Ο","Π","Ρ","Σ","Τ","Υ","Φ","Χ","Ψ","Ω",
            "■","□","▪","▫","▴","▾","▸","◂","▬","▭","▮","▯","▲","△","▼","▽","◀","▶",
            "┄","┅","┆","┇","┈","┉","┊","┋","│","─","━","═","╌","╍","╎","╏",
            "∑","∏","∫","∆","∇","∂","√","∞","≈","≠","≡","≤","≥","⊕","⊗","⊙","∅","∌",
            "×","÷","±","∓","∔","∛","∜","∝","∟","∠","∡","∢","⟂",
            "◎","◉","◐","◑","◒","◓","◔","◕","◖","◗","◘","◙","◚","◛","◜","◝","◞","◟",
            "⁂","⁃","⁜","⁝","⁞","※","‡","†","‣","•","‧","◦","‥","…",
            "★","☆","✦","✧","✩","✪","✫","✬","✭","✮","✯","✰",
            "♠","♣","♥","♦","♤","♧","♡","♢",
            "①","②","③","④","⑤","⑥","⑦","⑧","⑨","⑩","⑪","⑫","⑬","⑭","⑮","⑯","⑰","⑱","⑲","⑳",
            "㉑","㉒","㉓","㉔","㉕","㉖","㉗","㉘","㉙","㉚","㉛","㉜","㉝","㉞","㉟",
        };

        // ---- 公共属性 ----

        public static string DictHash => _dictHash;
        public static int EntryCount => _forward.Count;

        // ---- 初始化 ----

        public static void Initialize()
        {
            try
            {
                Rebuild();
            }
            catch (Exception ex)
            {
                McpLog.Error($"[SymbolDictionary] 初始化失败: {ex.Message}");
            }
        }

        /// <summary>尝试从 mod 目录加载词表文件</summary>
        private static Dictionary<string, char>? LoadCuratedTable()
        {
            try
            {
                // 词表与 DLL 同目录: Mod/1.6/Assemblies/Symbols.json
                var asmDir = Path.GetDirectoryName(typeof(SymbolDictionary).Assembly.Location);
                var path = Path.Combine(asmDir ?? ".", "Symbols.json");

                if (!File.Exists(path))
                {
                    McpLog.Warn($"[SymbolDictionary] 未找到词表: {path}，全部使用兜底分配");
                    return null;
                }

                string json = File.ReadAllText(path, Encoding.UTF8);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("symbols", out var syms))
                {
                    McpLog.Warn("[SymbolDictionary] Symbols.json 缺少 symbols 字段");
                    return null;
                }

                var curated = new Dictionary<string, char>();
                var charToDef = new Dictionary<char, string>();

                foreach (var prop in syms.EnumerateObject())
                {
                    var defName = prop.Name;
                    string? charStr = null;
                    if (prop.Value.ValueKind == JsonValueKind.Object
                        && prop.Value.TryGetProperty("char", out var jCh))
                        charStr = jCh.GetString();

                    if (charStr == null || charStr.Length != 1)
                    {
                        McpLog.Warn($"[SymbolDictionary] 词表跳过无效条目: {defName}");
                        continue;
                    }
                    char ch = charStr[0];

                    // 验证一对一
                    if (charToDef.TryGetValue(ch, out var existing))
                    {
                        McpLog.Error($"[SymbolDictionary] 词表冲突: '{ch}' 同时分配给 {existing} 和 {defName}，保留后者");
                    }
                    charToDef[ch] = defName;
                    curated[defName] = ch;
                }

                McpLog.Info($"[SymbolDictionary] 词表加载: {curated.Count} 条");
                return curated;
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[SymbolDictionary] 词表加载失败: {ex.Message}");
                return null;
            }
        }

        private static string ResolveCategory(Def def)
        {
            return def switch
            {
                TerrainDef => "地形",
                ThingDef td when td.category == ThingCategory.Building => "建筑",
                ThingDef td when td.category == ThingCategory.Item => "物品",
                ThingDef td when td.category == ThingCategory.Plant => "植物",
                ThingDef td when td.category == ThingCategory.Pawn => "生物",
                _ => "其他"
            };
        }

        private static void Rebuild()
        {
            _forward = new();
            _reverse = new();

            var curated = LoadCuratedTable();
            var usedChars = new HashSet<char>();

            var allDefs = new List<Def>();
            allDefs.AddRange(DefDatabase<ThingDef>.AllDefs);
            allDefs.AddRange(DefDatabase<TerrainDef>.AllDefs);
            var sorted = allDefs.OrderBy(d => d.defName).ToList();

            int fromTable = 0, fromPool = 0;

            // 第一轮：注册词表中已有的 def
            if (curated != null)
            {
                foreach (var def in sorted)
                {
                    if (!curated.TryGetValue(def.defName, out var ch))
                        continue;
                    if (usedChars.Contains(ch))
                    {
                        // 词表已验证一对一，不应该走到这里（除非运行时 def 名重复）
                        continue;
                    }

                    _forward[def.defName] = ch;
                    usedChars.Add(ch);
                    _reverse[ch] = (def.defName, def.label ?? def.defName, ResolveCategory(def));
                    fromTable++;
                }
            }

            // 第二轮：兜底 — 词表中不存在的 def 用动态池分配
            int poolIdx = 0;
            foreach (var def in sorted)
            {
                if (_forward.ContainsKey(def.defName))
                    continue;  // 已在词表中

                // 找下一个未使用的池字符
                char sym;
                if (poolIdx < FallbackPool.Count)
                {
                    sym = FallbackPool[poolIdx][0];
                    while (usedChars.Contains(sym) && poolIdx < FallbackPool.Count - 1)
                    {
                        poolIdx++;
                        sym = FallbackPool[poolIdx][0];
                    }
                    usedChars.Add(sym);
                    poolIdx++;
                }
                else
                {
                    // 池耗尽：用 defName 首字符（非一对一，但极罕见）
                    sym = def.defName.Length > 0 && char.IsLetter(def.defName[0])
                        ? def.defName[0] : '?';
                }

                _forward[def.defName] = sym;
                _reverse[sym] = (def.defName, def.label ?? def.defName, ResolveCategory(def));
                fromPool++;
            }

            // 计算 Hash
            var sortedNames = allDefs.Select(d => d.defName).OrderBy(n => n).ToList();
            _dictHash = string.Join(",", sortedNames).Length.ToString("x8");
            McpLog.Info($"[SymbolDictionary] 重建完成 — 词表: {fromTable}, 兜底: {fromPool}, 总计: {_forward.Count}, Hash: {_dictHash}");
        }

        // ---- 查询方法 ----

        public static char GetChar(string defName)
        {
            if (_forward.TryGetValue(defName, out var c))
                return c;
            return '?';
        }

        public static char GetChar(ThingDef def) => GetChar(def.defName);

        public static char GetChar(TerrainDef def) => GetChar(def.defName);

        public static string Lookup(char c)
        {
            if (_reverse.TryGetValue(c, out var info))
                return $"{info.DefName} ({info.Label}, {info.Category})";
            return $"未知符号: {c}";
        }

        public static Dictionary<char, (string DefName, string Label, string Category)> GetAll() => new(_reverse);

        public static Dictionary<char, (string DefName, string Label, string Category)> GetByChars(IEnumerable<char> chars)
        {
            var result = new Dictionary<char, (string, string, string)>();
            foreach (var c in chars)
            {
                if (_reverse.TryGetValue(c, out var info))
                    result[c] = info;
            }
            return result;
        }

        public static string GetLegendString(HashSet<char> usedSymbols)
        {
            var sb = new StringBuilder();
            foreach (var c in usedSymbols)
            {
                if (_reverse.TryGetValue(c, out var info))
                {
                    if (sb.Length > 0) sb.Append("  ");
                    sb.Append(c);
                    sb.Append('=');
                    sb.Append(info.Label);
                }
            }
            return sb.ToString();
        }

    }
}
