using System.Buffers.Binary;
using CcDirector.Core.Machine;
static class States
{
    public static void Run(int burners)
    {
        var stop = false;
        var threads = Enumerable.Range(0, burners).Select(_ => new Thread(() => { while (!Volatile.Read(ref stop)) { } }) { IsBackground = true }).ToList();
        threads.ForEach(t => t.Start());
        Thread.Sleep(500);
        for (var round = 0; round < 3; round++)
        {
            var size = 1 << 20;
            var buf = WindowsResourceNative.ReadProcessInformation(ref size, out _);
            var hist = new SortedDictionary<uint, int>();
            var offset = 0; var total = 0;
            while (true)
            {
                var next = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset));
                var n = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 4));
                for (var t = 0; t < n; t++) { var s = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset + 256 + t * 80 + 68)); hist[s] = hist.GetValueOrDefault(s) + 1; total++; }
                if (next == 0) break; offset += next;
            }
            Console.WriteLine($"burners={burners} threads={total} states: " + string.Join(" ", hist.Select(kv => $"{kv.Key}:{kv.Value}")));
            Thread.Sleep(300);
        }
        Volatile.Write(ref stop, true);
    }
}
