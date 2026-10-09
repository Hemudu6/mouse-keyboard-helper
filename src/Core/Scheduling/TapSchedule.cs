namespace Core.Scheduling;

/// <summary>
/// 频率时间轴（设计文档 §6.1）。
///
/// 需求 NFR-02（AC-23）：两条以上命令并行、频率落在 1~50 次/秒区间时，
/// 连续 30 次实际点按间隔必须全部落在 ±20ms 内。
/// 达成它的关键是**绝对时间轴**，而不是"每次做完动作后 sleep(1/频率)"：
/// 后者会把动作本身的耗时累加进间隔，误差随时间线性漂移——
/// 一次动作若耗时 3ms，1000ms 的目标间隔会变成 1003ms，30 次就漂到 90ms，直接超出 ±20ms。
///
/// 绝对时间轴的口径：第 n 次的**应发时刻** = t0 + n × (1000 / 频率) 毫秒（n 从 0 起）。
/// 每次算出的都是相对起点的绝对位置，动作耗时与调度抖动**不累积**。
/// 若某一次因为系统卡顿迟发了，下一次仍按原时间轴对表，
/// 表现为"偶尔漏一拍"而非"整体越来越慢"——前者可接受且能自愈，
/// 后者会让用户明确感觉到频率失真。
///
/// 本类**不持有定时器、不睡眠、不碰线程**：只管"下一次该在什么时刻"这一件事。
/// 运行期线程与等待策略属 Engine 层（TaskRuntime）。这样本类可被单测穷举，
/// 且"时间从哪来"与"怎么等"两个问题各自独立。
/// </summary>
public static class TapSchedule
{
    /// <summary>频率下限（次/秒），与 <see cref="Validation"/> 的校验区间一致。</summary>
    public const int MinFrequency = 1;

    /// <summary>频率上限（次/秒，需求 FR-TAP-05 明确拒绝超过 1000 的值）。</summary>
    public const int MaxFrequency = 1000;

    /// <summary>
    /// 高频阈值：≥ 本值的命令走专用线程，低于本值走池化线程（设计文档 §6.1）。
    ///
    /// 为什么是 50：低于 50 次/秒时，一次调度抖动（Windows 默认定时器分辨率约 15.6ms）
    /// 相对于 20ms 以上的间隔占比小，池化线程 + 时间轴自校正足以达标；
    /// 达到 50 次/秒时目标间隔已降到 20ms，与默认抖动同量级，
    /// 必须靠专用线程 + <c>timeBeginPeriod(1)</c> 把分辨率压到 1ms。
    /// 这与需求把 50 次/秒划为 AC-23 与 AC-23b 的分界完全一致。
    /// </summary>
    public const int DedicatedThreadThreshold = 50;

    /// <summary>
    /// 第 <paramref name="index"/> 次执行的应发时刻，单位微秒。
    ///
    /// 用 <see cref="long"/> 整数乘除而非 <c>index / (double)frequency * 1e6</c>：
    /// 需求 FR-TAP-02 的频率取值是整数次/秒，而 1e6/frequency 在 frequency 不整除 1e6 时
    /// 是循环小数（如 300 次/秒 → 3333.33...µs）。用整数微秒运算可保证
    /// "第 n 次"的时刻每次算出来完全一致（纯函数、无浮点误差），
    /// 且长任务的累计误差为零——这正是 AC-23b（> 50 次/秒、按平均值判据）
    /// 能达标的前提：平均值判据对系统性偏差零容忍。
    /// </summary>
    public static long NextDueMicroseconds(long t0Microseconds, long index, int frequency)
    {
        if (index <= 0)
        {
            return t0Microseconds;
        }

        var safeFrequency = ClampFrequency(frequency);
        return t0Microseconds + (index * 1_000_000L) / safeFrequency;
    }

    /// <summary>下一个"还没到"的执行序号：给定当前时刻，返回下一次该发的 index。</summary>
    public static long NextIndexAfter(long t0Microseconds, long nowMicroseconds, int frequency)
    {
        var safeFrequency = ClampFrequency(frequency);
        var elapsed = nowMicroseconds - t0Microseconds;
        if (elapsed < 0)
        {
            return 0;
        }

        // 已到期的次数 = floor(elapsed × 频率 / 1e6)；下一次即 +1。
        // 用整数乘除避免浮点边界（恰好整除时不能多算一次）。
        return (elapsed * safeFrequency) / 1_000_000L + 1;
    }

    /// <summary>目标间隔（微秒）。仅用于等待时长计算与单测断言。</summary>
    public static long IntervalMicroseconds(int frequency)
        => 1_000_000L / ClampFrequency(frequency);

    /// <summary>
    /// 把频率夹到合法区间。**这是运行期的防御**，不是校验：
    /// 配置文件被外部改坏（AC-21）时，运行期拿到 0 或负频率不能让线程空转或除零。
    /// 真正的"拒绝保存"由 <c>TaskValidator</c> 负责（AC-14）。
    /// </summary>
    public static int ClampFrequency(int frequency)
    {
        if (frequency < MinFrequency)
        {
            return MinFrequency;
        }

        return frequency > MaxFrequency ? MaxFrequency : frequency;
    }

    /// <summary>该频率的命令是否需要专用线程。</summary>
    public static bool NeedsDedicatedThread(int frequency)
        => ClampFrequency(frequency) >= DedicatedThreadThreshold;
}
