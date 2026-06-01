using System.Text.Json;

namespace RimWorldAgent.Core
{
    /// <summary>UI 显示消息基类 — UIMessageBus 统一输出，所有客户端消费同一协议</summary>
    public abstract class UiMessage
    {
        /// <summary>序列化为 WS 广播 JSON</summary>
        public string ToJson() => JsonSerializer.Serialize(this, GetType());

        // ===== 工厂（便捷） =====
        public static UiTextDelta TextDelta(string text) => new UiTextDelta(text);
        public static UiThinkingDelta ThinkingDelta(string thinking) => new UiThinkingDelta(thinking);
        public static UiTextBlock TextBlock(string text) => new UiTextBlock(text);
        public static UiToolCall ToolCall(string id, string name, string input) => new UiToolCall(id, name, input);
        public static UiToolResult ToolResult(string id, bool isError, double durationMs) => new UiToolResult(id, isError, durationMs);
        public static UiResult Result(string subtype, string? stopReason) => new UiResult(subtype, stopReason ?? "");
        public static UiAborted Aborted() => new UiAborted();
        public static UiSystemInit SystemInit(string? model, string? sessionId) => new UiSystemInit(model, sessionId);
        public static UiError Error(string error) => new UiError(error);
        public static UiUser User(string text) => new UiUser(text);
        public static UiSystem System(string text) => new UiSystem(text);
        public static UiBudgetStatus BudgetStatus(long used, long limit, string action, long cacheRead, long totalInput, long cacheCreate)
            => new UiBudgetStatus(used, limit, action, cacheRead, totalInput, cacheCreate);
    }

    // ===== 具体消息类型（与 WS 协议 JSON 字段对齐） =====

    public class UiTextDelta : UiMessage
    {
        public string type => "text_delta";
        public string text { get; }
        public UiTextDelta(string text) { this.text = text; }
    }

    public class UiThinkingDelta : UiMessage
    {
        public string type => "thinking_delta";
        public string thinking { get; }
        public UiThinkingDelta(string thinking) { this.thinking = thinking; }
    }

    public class UiTextBlock : UiMessage
    {
        public string type => "text_block";
        public string text { get; }
        public UiTextBlock(string text) { this.text = text; }
    }

    public class UiToolCall : UiMessage
    {
        public string type => "tool_call";
        public string id { get; }
        public string name { get; }
        public string input { get; }
        public UiToolCall(string id, string name, string input)
        { this.id = id; this.name = name; this.input = input; }
    }

    public class UiToolResult : UiMessage
    {
        public string type => "tool_result";
        public string id { get; }
        public bool isError { get; }
        public double durationMs { get; }
        public UiToolResult(string id, bool isError, double durationMs)
        { this.id = id; this.isError = isError; this.durationMs = durationMs; }
    }

    public class UiResult : UiMessage
    {
        public string type => "result";
        public string subtype { get; }
        public string stop_reason { get; }
        public UiResult(string subtype, string stopReason)
        { this.subtype = subtype; this.stop_reason = stopReason; }
    }

    public class UiAborted : UiMessage { public string type => "aborted"; }

    public class UiSystemInit : UiMessage
    {
        public string type => "system_init";
        public string? model { get; }
        public string? session_id { get; }
        public UiSystemInit(string? model, string? sessionId)
        { this.model = model; this.session_id = sessionId; }
    }

    public class UiError : UiMessage
    {
        public string type => "error";
        public string error { get; }
        public UiError(string error) { this.error = error; }
    }

    public class UiUser : UiMessage
    {
        public string type => "user";
        public string text { get; }
        public UiUser(string text) { this.text = text; }
    }

    public class UiSystem : UiMessage
    {
        public string type => "system";
        public string text { get; }
        public UiSystem(string text) { this.text = text; }
    }

    public class UiBudgetStatus : UiMessage
    {
        public string type => "budget_status";
        public long used { get; }
        public long limit { get; }
        public string action { get; }
        public long cacheRead { get; }
        public long totalInput { get; }
        public long cacheCreate { get; }
        public UiBudgetStatus(long used, long limit, string action, long cacheRead, long totalInput, long cacheCreate)
        { this.used = used; this.limit = limit; this.action = action; this.cacheRead = cacheRead; this.totalInput = totalInput; this.cacheCreate = cacheCreate; }
    }
}
