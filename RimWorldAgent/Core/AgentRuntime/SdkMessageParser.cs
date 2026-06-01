using System;
using System.Collections.Generic;
using System.Text.Json;
using RimWorldAgent.Core.CcbManager;

namespace RimWorldAgent.Core.AgentRuntime
{
    /// <summary>
    /// SDK 原始 JSON → UiMessage 转换。
    /// 放在 AgentCore 层，BridgeBus 不感知 SDK 消息格式。
    /// </summary>
    public static class SdkMessageParser
    {
        public static List<string> ParseToUiMessages(string rawJson)
        {
            var result = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(rawJson);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) return result;
                var type = typeProp.GetString();

                switch (type)
                {
                    case "event":
                    {
                        // companion bridge 用 type=event 包装
                        if (root.TryGetProperty("event", out var inner) && root.TryGetProperty("payload", out var payload))
                        {
                            var innerType = inner.GetString();
                            CoreLog.Info($"[CCGUI_DEBUG] SdkMessageParser unwrap type=event inner={innerType}");
                            switch (innerType)
                            {
                                case "stream_event": ParseStreamEventPayload(payload, result); break;
                                case "assistant": ParseAssistantPayload(payload, result); break;
                                case "result": ParseResultPayload(payload, result); break;
                                case "aborted": result.Add(UiMessage.Aborted()); break;
                                case "system": ParseSystemPayload(payload, result); break;
                            }
                        }
                        break;
                    }
                    case "assistant":
                        ParseAssistant(doc, result);
                        break;
                    case "stream_event":
                        ParseStreamEvent(doc, result);
                        break;
                    case "result":
                    {
                        var subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() : "";
                        var sr = root.TryGetProperty("stop_reason", out var stop) ? stop.GetString() : null;
                        result.Add(UiMessage.Result(subtype ?? "", sr));
                        break;
                    }
                    case "system":
                    {
                        var sub = root.TryGetProperty("subtype", out var s) ? s.GetString() : "";
                        if (sub == "init")
                        {
                            var model = root.TryGetProperty("model", out var m) ? m.GetString() : null;
                            var sid = root.TryGetProperty("session_id", out var sidE) ? sidE.GetString() : null;
                            result.Add(UiMessage.SystemInit(model, sid));
                        }
                        break;
                    }
                    case "aborted":
                        result.Add(UiMessage.Aborted());
                        break;
                }
            }
            catch (Exception ex) { CoreLog.Warn($"[SdkMessageParser] 解析失败: {ex.Message}"); }
            return result;
        }

        // ===== 原始 SDK 格式（type=assistant/stream_event/...） =====

        private static void ParseAssistant(JsonDocument doc, List<string> outList)
        {
            var root = doc.RootElement;
            ExtractUsage(doc);
            ParseContentBlocks(root, outList);
        }

        private static void ParseStreamEvent(JsonDocument doc, List<string> outList)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("event", out var evt)) return;
            ParseStreamEventBlocks(evt, outList);
        }

        // ===== type=event payload 解包（companion bridge 包装格式） =====

        private static void ParseAssistantPayload(JsonElement payload, List<string> outList)
        {
            ExtractUsageFromRoot(payload);
            ParseContentBlocks(payload, outList);
        }

        private static void ParseStreamEventPayload(JsonElement payload, List<string> outList)
        {
            if (!payload.TryGetProperty("event", out var evt)) return;
            ParseStreamEventBlocks(evt, outList);
        }

        private static void ParseResultPayload(JsonElement payload, List<string> outList)
        {
            var subtype = payload.TryGetProperty("subtype", out var st) ? st.GetString() : "";
            var sr = payload.TryGetProperty("stop_reason", out var stop) ? stop.GetString() : null;
            outList.Add(UiMessage.Result(subtype ?? "", sr));
        }

        private static void ParseSystemPayload(JsonElement payload, List<string> outList)
        {
            var sub = payload.TryGetProperty("subtype", out var s) ? s.GetString() : "";
            if (sub == "init")
            {
                var model = payload.TryGetProperty("model", out var m) ? m.GetString() : null;
                var sid = payload.TryGetProperty("session_id", out var sidE) ? sidE.GetString() : null;
                outList.Add(UiMessage.SystemInit(model, sid));
            }
        }

        // ===== 共享解析逻辑 =====

        private static void ParseContentBlocks(JsonElement root, List<string> outList)
        {
            if (!root.TryGetProperty("message", out var msg)) return;
            if (!msg.TryGetProperty("content", out var content)) return;
            foreach (var block in content.EnumerateArray())
            {
                var bt = block.GetProperty("type").GetString();
                if (bt == "text")
                {
                    var text = block.GetProperty("text").GetString() ?? "";
                    outList.Add(UiMessage.TextBlock(text));
                }
                else if (bt == "tool_use")
                {
                    var id = block.GetProperty("id").GetString() ?? "";
                    var name = block.GetProperty("name").GetString() ?? "";
                    var input = block.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}";
                    outList.Add(UiMessage.ToolCall(id, name, input));
                }
            }
        }

        private static void ParseStreamEventBlocks(JsonElement evt, List<string> outList)
        {
            var et = evt.GetProperty("type").GetString();
            if (et == "content_block_start")
            {
                var cb = evt.GetProperty("content_block");
                var cbt = cb.GetProperty("type").GetString();
                if (cbt == "text")
                    outList.Add(UiMessage.TextDelta(""));
                else if (cbt == "thinking")
                    outList.Add(UiMessage.ThinkingDelta(""));
            }
            else if (et == "content_block_delta")
            {
                var delta = evt.GetProperty("delta");
                var dt = delta.GetProperty("type").GetString();
                if (dt == "text_delta")
                    outList.Add(UiMessage.TextDelta(delta.GetProperty("text").GetString() ?? ""));
                else if (dt == "thinking_delta")
                    outList.Add(UiMessage.ThinkingDelta(delta.GetProperty("thinking").GetString() ?? ""));
            }
        }

        // ===== Token 用量提取 =====

        private static void ExtractUsage(JsonDocument doc)
            => ExtractUsageFromRoot(doc.RootElement);

        private static void ExtractUsageFromRoot(JsonElement root)
        {
            try
            {
                if (!root.TryGetProperty("message", out var msg)) return;
                if (!msg.TryGetProperty("usage", out var usage)) return;
                var inp = TryGetLong(usage, "input_tokens");
                var outp = TryGetLong(usage, "output_tokens");
                var cr = TryGetLong(usage, "cache_read_input_tokens");
                var cc = TryGetLong(usage, "cache_creation_input_tokens");
                if (inp > 0 || outp > 0)
                    TokenUsageTracker.Record(inp, outp, cr, cc, 0);
            }
            catch (Exception ex) { CoreLog.Warn($"[SdkMessageParser] Usage 提取失败: {ex.Message}"); }
        }

        private static long TryGetLong(JsonElement el, string key)
            => el.TryGetProperty(key, out var v) && v.TryGetInt64(out var n) ? n : 0;
    }
}
