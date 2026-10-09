using Core.Scheduling;

namespace Host.Tests;

/// <summary>
/// 频率时间轴的单测（设计文档 §6.1）。
///
/// 覆盖的需求判据：
///   - AC-23b：> 50 次/秒区间的实际平均频率 ≥ 配置值的 90%——平均值判据对**系统性偏差**零容忍，
///     故这里必须验证长时间轴上不漂移，而不只是前几次对。
///   - AC-23：1~50 次/秒区间 ±20ms——验证相邻间隔恒定为目标间隔。
///
/// 断言口径：全部用整数微秒（<see cref="TapSchedule.NextDueMicroseconds"/>），
/// 不用浮点比较——"约等于"的浮点断言会在临界值上随机红绿。
/// </summary>
public class TapScheduleTests
{
    private const long OneSecondUs = 1_000_000L;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(300)]
    [InlineData(1000)]
    public void 第零次应发时刻等于起点(int frequency)
    {
        // t0 + 0×(1/频率) 必须是 t0 本身：这是"绝对时间轴"的基准点。
        // 若这里带上任何偏移，整个时间轴会整体平移，AC-22 的首条命令延迟会被平白吃掉一截。
        var t0 = 123_456_789L;
        Assert.Equal(t0, TapSchedule.NextDueMicroseconds(t0, 0, frequency));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(300)]
    [InlineData(1000)]
    public void 相邻两次的间隔等于目标间隔(int frequency)
    {
        // 需求 FR-TAP-02：频率即"每秒执行几次"。n+1 次与 n 次的时刻差必须恰好是 1e6/频率 微秒。
        var t0 = 1_000_000L;
        var expectedInterval = OneSecondUs / frequency;

        for (var index = 0L; index < 200; index++)
        {
            var current = TapSchedule.NextDueMicroseconds(t0, index, frequency);
            var next = TapSchedule.NextDueMicroseconds(t0, index + 1, frequency);
            var actualInterval = next - current;

            // 整除时严格相等；不整除时（如 300 次/秒 → 3333.33us）允许 1us 的取整差，
            // 但不得更大——更大的差意味着用了浮点近似而非整数时间轴。
            Assert.True(
                Math.Abs(actualInterval - expectedInterval) <= 1,
                $"频率 {frequency} 次/秒：第 {index} 次间隔 {actualInterval}us，期望 {expectedInterval}us");
        }
    }

    [Theory]
    [InlineData(1, 200)]
    [InlineData(3, 250)]
    [InlineData(7, 300)]
    [InlineData(50, 400)]
    [InlineData(200, 500)]
    [InlineData(300, 600)]
    [InlineData(997, 700)]
    public void 长时间轴上不漂移(int frequency, int samples)
    {
        // 这是 AC-23b 的核心：平均值判据不容忍系统性漂移。
        // 若实现用“做完动作再 sleep(间隔)”的累加口径，误差会随 n 线性增长；
        // 绝对时间轴下，第 n 次的时刻与“n × 目标间隔”之差必须恒定——
        // 它是**纯取整误差**（每次至多 1us 的整数除法余量），与 n 无关。
        //
        // 因此这里的判据是“误差上界 = 1us”，而不是任何随 n 增长的式子：
        //   期望间隔是精确值 n×1e6/frequency，实现取的是 floor(n×1e6/frequency)，
        //   两者之差为小数部分，恒在 [0, 1) 微秒内。
        // 任何随 n 增长的容差都是漂移的伪装——那正是本条要挡住的。
        const long t0 = 0L;
        const long roundingToleranceUs = 1L;

        for (var index = 0L; index < samples; index++)
        {
            var due = TapSchedule.NextDueMicroseconds(t0, index, frequency);

            // 该次相对起点的理想时刻（整数除法的精确值，不丢余数）。
            var ideal = (index * OneSecondUs) / frequency;

            Assert.True(
                Math.Abs(due - ideal) <= roundingToleranceUs,
                $"频率 {frequency}：第 {index} 次时刻 {due}us 偏离精确时刻 {ideal}us 超过 {roundingToleranceUs}us（疑似漂移）");
        }

        // 漂移的另一种可见形式：总时长。第 samples 次的时刻必须精确等于
        // samples × 1e6 / frequency 的整数除法结果（误差 < 1us，不随 samples 增长）。
        var lastDue = TapSchedule.NextDueMicroseconds(t0, samples, frequency);
        var exactSpan = (samples * OneSecondUs) / frequency;
        Assert.Equal(exactSpan, lastDue);

        // 且**不得越过精确值**——整数除法向下取整，越过即意味着向上取整（提前发送）。
        Assert.True(
            lastDue <= exactSpan,
            $"频率 {frequency}：第 {samples} 次时刻 {lastDue}us 超过精确值 {exactSpan}us，整体变快");
    }

    [Fact]
    public void 三百次每秒在十秒后的累计误差不超过一个微秒()
    {
        // 300 次/秒 → 每次 3333.33...us，是最容易暴露浮点漂移的频率之一。
        // 十秒共 3000 次，理想总时长 10_000_000us。整数口径下必须精确命中。
        const int frequency = 300;
        const long t0 = 0L;
        const int count = 3000;

        var due = TapSchedule.NextDueMicroseconds(t0, count, frequency);

        Assert.Equal(10_000_000L, due);
    }

    [Theory]
    [InlineData(50, true)]
    [InlineData(49, false)]
    [InlineData(51, true)]
    [InlineData(1000, true)]
    public void 专用线程阈值判定(int frequency, bool expectedDedicated)
    {
        // 设计文档 §6.1：≥ 50 次/秒走专用线程 + timeBeginPeriod(1)，低于则池化。
        // 边界取 50 本身算专用——恰好 20ms 间隔已与系统默认抖动同量级。
        Assert.Equal(expectedDedicated, TapSchedule.NeedsDedicatedThread(frequency));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(999_999, 1)]
    [InlineData(1_000_000, 2)]
    [InlineData(1_000_001, 2)]
    [InlineData(2_000_000, 3)]
    public void 下一个应发序号按绝对时间轴推进(long elapsedUs, long expectedIndex)
    {
        // NextIndexAfter 是运行期“睡醒后该补发第几次”的判据。
        // 起点时刻 0、频率 1（间隔 1 秒）：index 0 在 t0 已发，
        // 故“下一个该发”的序号 = floor(elapsed / 1e6) + 1。
        var index = TapSchedule.NextIndexAfter(0, elapsedUs, 1);
        Assert.Equal(expectedIndex, index);
    }

    [Fact]
    public void 下一个应发序号在整秒边界不多算一次()
    {
        // 频率 10 → 间隔 100ms。index 0 在 t0 已发，之后每 100ms 依次发 1、2、3……
        // 边界处的算术必须精确：100ms 整除时下一次是 2，多算会重复执行、少算会漏拍。
        Assert.Equal(1, TapSchedule.NextIndexAfter(0, 0, 10));           // 刚起步 → 下一次是 1（0 已在 t0 发过）
        Assert.Equal(1, TapSchedule.NextIndexAfter(0, 99_999, 10));      // 差 1us 才到第 1 个间隔 → 仍是 1
        Assert.Equal(2, TapSchedule.NextIndexAfter(0, 100_000, 10));     // 恰好到第 1 个间隔 → 下一次是 2
        Assert.Equal(2, TapSchedule.NextIndexAfter(0, 100_001, 10));     // 刚过一点点 → 下一次仍是 2（尚未到第 2 个间隔）
        Assert.Equal(3, TapSchedule.NextIndexAfter(0, 200_000, 10));     // 恰好到第 2 个间隔 → 下一次是 3

        // 从“整秒边界”继续：起点已偏移 1s 时，序号语义不因绝对时刻而变。
        Assert.Equal(2, TapSchedule.NextIndexAfter(OneSecondUs, OneSecondUs + 100_000, 10));
    }

    [Fact]
    public void 应发序号在负时间内不倒退()
    {
        // 系统时钟回拨或起点比"现在"晚（理论上不该发生，但时间源不可全信）时，
        // 返回值必须是 0 而不是负数——负数会让运行期索引倒退、重复执行旧拍。
        Assert.Equal(0, TapSchedule.NextIndexAfter(1_000_000, 500_000, 10));
    }

    [Theory]
    [InlineData(1, 1_000_000)]
    [InlineData(10, 100_000)]
    [InlineData(1000, 1000)]
    public void 间隔微秒与频率互为倒数(int frequency, long expectedIntervalUs)
    {
        Assert.Equal(expectedIntervalUs, TapSchedule.IntervalMicroseconds(frequency));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-100, 1)]
    [InlineData(1, 1)]
    [InlineData(1000, 1000)]
    [InlineData(99999, 1000)]
    public void 频率夹取是运行期防御而非校验(int frequency, int expected)
    {
        // 配置文件被外部改坏（AC-21）时，运行期不能因 0 或负数频率而除零/空转。
        // 注意：真正的"拒绝保存"由 TaskValidator 负责（AC-14），本方法只管"别炸"。
        Assert.Equal(expected, TapSchedule.ClampFrequency(frequency));
    }
}
