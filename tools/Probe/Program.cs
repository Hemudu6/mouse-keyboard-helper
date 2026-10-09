using System.Windows.Input;
using Core.Input;
using Core.Keys;
using Core.Model;
using Host.Engine;

// 探针 v8：泄漏归因。
// 手段：用 IInputSink 的 KeyDown 记录**调用它的线程 Id**——孤儿线程的 Id 会一直出现，
// 而正常线程随任务停止而消失。这样能直接看到「有几次不同的线程在注入」。

var sink = new LoneSink();
var input = new InputSimulator(sink);
var scheduler = new Scheduler(input);

TaskDefinition MakeTask(string name, Key rotate, int freq, Key inject)
{
    var t = new TaskDefinition { Name = name, Enabled = true, ToggleKey = Core.Keys.KeyBinding.FromKey(rotate) };
    t.Commands.Add(new KeyboardKeyCommand { VirtualKey = inject, Frequency = freq, TapMode = TapMode.Tap });
    return t;
}

var taskFast = MakeTask("快任务", Key.F6, 50, Key.A);
var taskSlow = MakeTask("慢任务", Key.F7, 12, Key.B);

for (var round = 1; round <= 10; round++)
{
    for (var i = 0; i < 20; i++) scheduler.Toggle(i % 2 == 0 ? taskFast : taskSlow);
    for (var i = 0; i < 3; i++) scheduler.Toggle(taskFast);
    scheduler.StopAll();
    Thread.Sleep(80);
    sink.Clear();
    Thread.Sleep(150);

    {
        var barrier = new Barrier(8);
        var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < 200; i++) { try { scheduler.Toggle(taskFast); } catch { } }
        })).ToArray();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();
    }

    scheduler.StopAll();
    Thread.Sleep(80);
    sink.Clear();
    Thread.Sleep(300);

    Console.WriteLine($"轮{round}: StopAll后 注入={sink.CountA} 涉及线程数={sink.ThreadCount} 线程名单=[{sink.ThreadList}]");
    scheduler.Shutdown();
    Thread.Sleep(100);
    sink.Clear();
    Thread.Sleep(200);
    if (sink.CountA > 0)
        Console.WriteLine($"   └ Shutdown 后仍注入 {sink.CountA}，线程=[{sink.ThreadList}]");
}

Console.WriteLine("完成。");

internal sealed class LoneSink : IInputSink
{
    private readonly object _gate = new();
    private readonly HashSet<int> _threads = new();
    private int _a, _total;
    public int LastInjectionError => 0;
    public int CountA { get { lock (_gate) { return _a; } } }
    public int ThreadCount { get { lock (_gate) { return _threads.Count; } } }
    public string ThreadList
    {
        get
        {
            lock (_gate)
            {
                var ids = _threads.ToList();
                ids.Sort();
                return string.Join(",", ids);
            }
        }
    }
    public void Clear() { lock (_gate) { _a = 0; _total = 0; _threads.Clear(); } }
    public int KeyDown(Key k)
    {
        lock (_gate)
        {
            _total++; _threads.Add(Environment.CurrentManagedThreadId);
            if (k == Key.A) _a++;
        }
        return 1;
    }
    public int KeyUp(Key k) { lock (_gate) { _total++; _threads.Add(Environment.CurrentManagedThreadId); } return 1; }
    public int MouseDown(MouseKey b) { lock (_gate) { _total++; } return 1; }
    public int MouseUp(MouseKey b) { lock (_gate) { _total++; } return 1; }
    public int MoveAbsolute(int x, int y) => 1;
    public int Scroll(bool v, int n) => 1;
}
