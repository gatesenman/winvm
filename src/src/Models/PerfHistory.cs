using CommunityToolkit.Mvvm.ComponentModel;

namespace WinVM.Models;

/// <summary>Rolling performance samples of one VM, filled from the regular library poll (no extra queries).
/// Each push publishes new snapshot arrays so bound sparklines redraw.</summary>
public sealed partial class PerfHistory : ObservableObject
{
    public const int Capacity = 60;
    private readonly double[] _cpu = new double[Capacity];
    private readonly double[] _mem = new double[Capacity];
    private int _count;
    private int _head; // next write index

    [ObservableProperty] private IReadOnlyList<double> _cpuSeries = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _memorySeries = Array.Empty<double>();
    [ObservableProperty] private double _memoryMax = 1;

    public int Count => _count;
    public double CpuAverage => _count == 0 ? 0 : Snapshot(_cpu).Average();
    public double CpuPeak => _count == 0 ? 0 : Snapshot(_cpu).Max();
    public string CpuStatsText => _count < 2 ? "正在收集数据…" : $"平均 {CpuAverage:0}% · 峰值 {CpuPeak:0}%";
    public string MemoryStatsText => _count < 2 ? "正在收集数据…" : $"峰值 {VmInfo.FormatBytes((long)Snapshot(_mem).Max())}";

    /// <param name="cpuPercent">0..100</param>
    /// <param name="memoryBytes">assigned memory</param>
    /// <param name="memoryCeiling">scale for the memory chart (startup/maximum memory)</param>
    public void Push(double cpuPercent, double memoryBytes, double memoryCeiling)
    {
        _cpu[_head] = Math.Clamp(cpuPercent, 0, 100);
        _mem[_head] = Math.Max(0, memoryBytes);
        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;
        var mem = Snapshot(_mem);
        MemoryMax = Math.Max(1, Math.Max(memoryCeiling, mem.Length > 0 ? mem.Max() : 0));
        CpuSeries = Snapshot(_cpu);
        MemorySeries = mem;
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(CpuStatsText));
        OnPropertyChanged(nameof(MemoryStatsText));
    }

    public void Clear()
    {
        _count = 0; _head = 0;
        CpuSeries = Array.Empty<double>();
        MemorySeries = Array.Empty<double>();
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(CpuStatsText));
        OnPropertyChanged(nameof(MemoryStatsText));
    }

    /// <summary>Samples oldest → newest.</summary>
    private double[] Snapshot(double[] ring)
    {
        var r = new double[_count];
        var start = (_head - _count + Capacity) % Capacity;
        for (var i = 0; i < _count; i++) r[i] = ring[(start + i) % Capacity];
        return r;
    }
}
