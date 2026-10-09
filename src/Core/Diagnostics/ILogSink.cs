namespace Core.Diagnostics;

/// <summary>
/// 日志级别（设计文档 §7.3）。
///
/// <para>
/// 只用两级不是偷懒：§7.3 把记录内容分成两类——「任务启动/停止」是**正常业务轨迹**，
/// 「错误事件」是**异常轨迹**。用户排查时先看的是"任务到底启没启"，两者混在一列里
/// 会让这条线索被淹没，故级别必须落进日志行本身。
/// </para>
/// </summary>
public enum LogLevel
{
    /// <summary>正常业务轨迹（任务启停、开关命中）。</summary>
    Info,

    /// <summary>异常轨迹（配置读写失败、注入失败、钩子异常等）。</summary>
    Error,
}

/// <summary>
/// 日志接收器（设计文档 §7.3、需求 FR-CFG-05）。
///
/// <para>
/// <b>为什么是接口而不是 <c>Action&lt;string&gt;</c></b>：§7.3 要求区分"任务启动/停止"
/// 与"错误事件"两类记录，而 <c>Action&lt;string&gt;</c> 表达不了级别——落到日志文件里
/// 会分不出轻重。<see cref="Exception"/> 重载则是为了让调用方（各错误源）把异常对象
/// 原样交出来，而不是自己 <c>ex.ToString()</c> 之后拼进消息串——栈的格式留给落地实现决定。
/// </para>
///
/// <para>
/// <b>实现方（<see cref="AppLogger"/>）的硬约束</b>：任何一次调用都不得让异常逸出。
/// 日志器坏了不能把注入热路径或调度器拖垮——这比"少记一条日志"严重得多。
/// 故实现侧吞掉全部 IO 异常，调用侧也不需要 try/catch。
/// </para>
///
/// <para>
/// <b>调用位置约束</b>：实现可能做文件 IO，故**不得在钩子回调路径**上调用（设计 §3.2 / §7.3）。
/// 钩子投递线程与消息泵线程上的调用点须逐一按 §3.2 判定。
/// </para>
/// </summary>
public interface ILogSink
{
    /// <summary>记一条正常业务轨迹（任务启停、开关命中）。</summary>
    /// <param name="message">消息正文（不含时间戳——时间戳由落地实现补）。</param>
    void Info(string message);

    /// <summary>记一条错误事件（配置读写失败、注入失败、钩子异常等）。</summary>
    /// <param name="message">消息正文（不含时间戳）。</param>
    /// <param name="exception">相关异常（可空）。</param>
    void Error(string message, Exception? exception = null);
}
