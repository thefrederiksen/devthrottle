// Phase 6, ruling K1: run the PRODUCT's cue search (CcDirector.Core.Audio.ReadyCue.Find) over every clip of the
// local transcription-lab corpus and write, per clip, what it found. No audio and no transcript is written -
// only file names, scores and positions.
//
// Usage: dotnet run -- <corpus clips folder> <output json>
//
// Head search: exactly the recorder's call. The recorder searches from the clip start to 1.5 s past the capture
// position at first audio; the corpus does not record that position, so the first 50 ms buffer
// (MicAudioCapture's buffer size) is assumed: search end = 1550 ms.
// Middle control: the same call over a 1.5 s slice from the middle of the clip, where the research's cue_scan.py
// looked (start = max(1.5 s, half the clip - 0.75 s)). No cue can be there.
using System.Text.Json;
using CcDirector.Core.Audio;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: CueDetectorCorpus <clips folder> <output json>");
    return 2;
}

const int firstBufferMs = 50;
var rows = new List<object>();
foreach (var path in Directory.GetFiles(args[0], "*.wav").OrderBy(p => p, StringComparer.Ordinal))
{
    var (rate, pcm) = ReadPcm16Mono(path);
    long bytesPerSecond = rate * 2L;
    double Ms(long bytes) => Math.Round(bytes * 1000.0 / bytesPerSecond, 1);

    long headEnd = bytesPerSecond * firstBufferMs / 1000 + bytesPerSecond * ReadyCue.SearchAfterFirstAudioMs / 1000;
    var head = ReadyCue.Find(pcm, rate, headEnd);

    long windowBytes = bytesPerSecond * 3 / 2;
    long mid0 = Math.Max(windowBytes, pcm.Length / 2 - bytesPerSecond * 3 / 4);
    mid0 -= mid0 % 2;
    var midSlice = pcm.AsSpan((int)Math.Min(mid0, pcm.Length), (int)Math.Clamp(pcm.Length - mid0, 0, windowBytes));
    var mid = ReadyCue.Find(midSlice, rate, midSlice.Length);

    rows.Add(new
    {
        clip = Path.GetFileName(path),
        sample_rate = rate,
        clip_ms = Ms(pcm.Length),
        head_score = Math.Round(head.BestScore, 4),
        head_found = head.Found,
        head_match_ms = head.MatchSample < 0 ? (double?)null : Ms(head.MatchSample * 2L),
        blank_start_ms = head.Found ? Ms(head.BlankStartByte) : (double?)null,
        blank_end_ms = head.Found ? Ms(head.BlankEndByte) : (double?)null,
        mid_start_ms = Ms(mid0),
        mid_score = Math.Round(mid.BestScore, 4),
        mid_found = mid.Found,
    });
    Console.WriteLine($"{Path.GetFileName(path),-45} head={head.BestScore:F2} found={head.Found,-5} "
        + $"at={(head.MatchSample < 0 ? "-" : Ms(head.MatchSample * 2L).ToString("F1"))} mid={mid.BestScore:F2}");
}

File.WriteAllText(args[1], JsonSerializer.Serialize(new
{
    detector = "CcDirector.Core.Audio.ReadyCue.Find",
    found_score = ReadyCue.FoundScore,
    search_end_ms = firstBufferMs + ReadyCue.SearchAfterFirstAudioMs,
    lead_in_ms = ReadyCue.LeadInMs,
    ring_down_ms = ReadyCue.RingDownMs,
    clips = rows,
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"wrote {rows.Count} clips to {args[1]}");
return 0;

static (int Rate, byte[] Pcm) ReadPcm16Mono(string path)
{
    using var reader = new BinaryReader(File.OpenRead(path));
    if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException($"{path}: not RIFF");
    reader.ReadInt32();
    if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException($"{path}: not WAVE");
    int rate = 0;
    while (reader.BaseStream.Position < reader.BaseStream.Length)
    {
        var id = new string(reader.ReadChars(4));
        int size = reader.ReadInt32();
        if (id == "fmt ")
        {
            var format = reader.ReadInt16();
            var channels = reader.ReadInt16();
            rate = reader.ReadInt32();
            reader.ReadInt32();
            reader.ReadInt16();
            var bits = reader.ReadInt16();
            if (format != 1 || channels != 1 || bits != 16)
                throw new InvalidDataException($"{path}: expected 16-bit mono PCM, got format={format} channels={channels} bits={bits}");
            reader.BaseStream.Seek(size - 16 + (size & 1), SeekOrigin.Current);
        }
        else if (id == "data")
        {
            if (rate == 0) throw new InvalidDataException($"{path}: data before fmt");
            return (rate, reader.ReadBytes(size - (size & 1)));
        }
        else
        {
            reader.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
        }
    }
    throw new InvalidDataException($"{path}: no data chunk");
}
