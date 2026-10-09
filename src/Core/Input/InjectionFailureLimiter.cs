using System.Collections.Concurrent;

namespace Core.Input;

/// <summary>
/// 注入失败的**时间窗聚合器**（设计文档 §3.1.2）。
///
/// <para>
/// 背景：<c>SendInput</c> 受 UIPI 约束（目标窗口权限更高时会被系统拒绝），
/// 失败时返回 0 或偏少事件数且**不抛异常**。此时若每条失败都写一行日志，
/// 在 1000 次/秒的命令下会瞬间写出海量日志，反过来拖垮 §4 时间轴的
/// 精度保障（NFR-04）。故同一动作的失败日志**按时间窗口聚合**：
/// 每个窗口内最多一条。
/// </para>
/// <para>
/// 设计文档 §3.1.2 的裁决同时要求：失败**不中断任务、继续尝试**。
/// 本类只负责"要不要记这条日志"，不影响注入动作本身是否继续。
/// </para>
/// <para>
/// **按动作分桶**：不同动作（键盘按住 / 鼠标移动 / 滚轮 ……）各自独立计时——
/// "鼠标移动被拒"与"滚轮被拒"是两个不同问题，不该互相压掉对方的日志。
/// </para>
/// </summary>
internal sealed class InjectionFailureLimiter
{
    /// <summary>默认聚合窗口：一分钟一条（设计文档 §3.1.2 举例口径）。</summary>
    internal const int DefaultWindowMs = 60_000;

    private readonly ConcurrentDictionary<string, long> _lastReportedAt = new(StringComparer.Ordinal);
    private readonly Func<long> _now;
    private readonly int _windowMs;

    /// <param name="now">时钟（单调毫秒）；为空时用 <see cref="Environment.TickCount64"/>。</param>
    /// <param name="windowMs">聚合窗口（毫秒）。</param>
    internal InjectionFailureLimiter(Func<long>? now = null, int windowMs = DefaultWindowMs)
    {
        _now = now ?? (static () => Environment.TickCount64);
        _windowMs = windowMs > 0 ? windowMs : DefaultWindowMs;
    }

    /// <summary>
    /// 该动作此刻是否应当记录一条失败日志；
    /// 返回 <c>true</c> 时**同时**刷新该动作的时间戳（一次判定即一次计数）。
    /// </summary>
    internal bool ShouldReport(string action)
    {
        var timestamp = _now();

        if (_lastReportedAt.TryGetValue(action, out var last))
        {
            // 用减法比较而非 TickCount 直接相减的溢出问题：TickCount64 溢出需数亿年，忽略。
            if (timestamp - last < _windowMs)
            {
                return false;
            }
        }

        _lastReportedAt[action] = timestamp;
        return true;
    }
}
