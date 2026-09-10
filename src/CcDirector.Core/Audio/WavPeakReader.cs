using System.Text;

namespace CcDirector.Core.Audio;

/// <summary>
/// Reads how LOUD a PCM WAV is between two moments, so a claim about what was said at a given
/// time can be checked against the sound that was actually there.
///
/// Written for the transcript evidence gate (pipeline v2.0): a speech model must produce a
/// caption for every part of a clip, so on a silent stretch it writes the likeliest caption from
/// its training data rather than nothing. The only way to catch that is to measure the audio in
/// the span the model claims those words occupy.
///
/// PEAK, not average. A short word inside a longer quiet span barely moves the average but shows
/// clearly in the peak, so an average would hide exactly the quiet real speech that must survive.
///
/// dBFS, relative to full scale, so the numbers are comparable across sample formats. Callers
/// compare spans WITHIN one clip: absolute levels depend on the microphone, its gain and how
/// close the speaker sat, and a fixed threshold that fits one machine deletes speech on another.
///
/// Linear PCM only (8, 16, 24 and 32-bit integer). Anything else - a compressed WAV, a WebM/Opus
/// upload, a truncated header - returns null so the caller can fail open rather than guess.
/// </summary>
public sealed class WavPeakReader
{
    private readonly byte[] _wav;
    private readonly int _dataOffset;
    private readonly int _dataLength;
    private readonly int _bytesPerSample;
    private readonly int _channels;

    /// <summary>Samples per second.</summary>
    public int SampleRate { get; }

    /// <summary>The clip's length in seconds, from the sample data actually present.</summary>
    public double DurationSeconds =>
        _dataLength / (double)(SampleRate * _channels * _bytesPerSample);

    private WavPeakReader(byte[] wav, int dataOffset, int dataLength, int sampleRate, int channels, int bitsPerSample)
    {
        _wav = wav;
        _dataOffset = dataOffset;
        _dataLength = dataLength;
        _channels = channels;
        _bytesPerSample = bitsPerSample / 8;
        SampleRate = sampleRate;
    }

    /// <summary>
    /// Parse a linear PCM WAV, or return null for anything else. Scans the chunk list, so the
    /// format and data chunks may appear in any order with other chunks between them.
    /// </summary>
    public static WavPeakReader? TryRead(byte[]? wav)
    {
        if (wav is null || wav.Length < 12) return null;
        if (!(wav[0] == 'R' && wav[1] == 'I' && wav[2] == 'F' && wav[3] == 'F')) return null;
        if (!(wav[8] == 'W' && wav[9] == 'A' && wav[10] == 'V' && wav[11] == 'E')) return null;

        int sampleRate = 0, channels = 0, bits = 0, dataOffset = 0, dataLength = 0;
        bool haveFmt = false, haveData = false;
        int p = 12;
        while (p + 8 <= wav.Length)
        {
            string id = Encoding.ASCII.GetString(wav, p, 4);
            int size = BitConverter.ToInt32(wav, p + 4);
            int body = p + 8;

            // A declared size past the end of the buffer (a streaming size of 0, or a stale
            // length) means trusting the bytes actually present rather than reading out of range.
            if (size < 0 || body + size > wav.Length) size = wav.Length - body;
            if (size < 0) break;

            if (id == "fmt ")
            {
                if (size < 16) return null;
                short audioFormat = BitConverter.ToInt16(wav, body);
                if (audioFormat != 1) return null;         // linear PCM only
                channels = BitConverter.ToInt16(wav, body + 2);
                sampleRate = BitConverter.ToInt32(wav, body + 4);
                bits = BitConverter.ToInt16(wav, body + 14);
                haveFmt = true;
            }
            else if (id == "data")
            {
                dataOffset = body;
                dataLength = size;
                haveData = true;
            }

            int advance = size + (size & 1);              // chunks are word-aligned
            p = body + advance;
        }

        if (!haveFmt || !haveData || dataLength <= 0) return null;
        if (channels <= 0 || sampleRate <= 0) return null;
        if (bits != 8 && bits != 16 && bits != 24 && bits != 32) return null;
        return new WavPeakReader(wav, dataOffset, dataLength, sampleRate, channels, bits);
    }

    /// <summary>
    /// The loudest moment between two times, in dBFS. Silence returns -120. A span outside the
    /// audio, or one that contains no whole sample, also returns -120: there is no sound there,
    /// which is the truthful answer to "how loud was it".
    /// </summary>
    public double PeakDbBetween(double startSeconds, double endSeconds)
    {
        int frameBytes = _channels * _bytesPerSample;
        int totalFrames = _dataLength / frameBytes;
        if (totalFrames <= 0) return -120.0;

        int first = (int)Math.Floor(Math.Max(0, startSeconds) * SampleRate);
        int last = (int)Math.Ceiling(Math.Max(0, endSeconds) * SampleRate);
        first = Math.Clamp(first, 0, totalFrames);
        last = Math.Clamp(last, 0, totalFrames);
        if (last <= first) return -120.0;

        double peak = 0.0;
        for (int f = first; f < last; f++)
        {
            int baseIndex = _dataOffset + f * frameBytes;
            for (int c = 0; c < _channels; c++)
            {
                double v = Math.Abs(SampleAt(baseIndex + c * _bytesPerSample));
                if (v > peak) peak = v;
            }
        }

        if (peak <= 0.0) return -120.0;
        return Math.Max(-120.0, 20.0 * Math.Log10(peak));
    }

    /// <summary>One sample, normalised to -1..1 whatever the bit depth. 8-bit PCM is unsigned
    /// with a midpoint of 128; every wider format is signed little-endian.</summary>
    private double SampleAt(int offset)
    {
        switch (_bytesPerSample)
        {
            case 1:
                return (_wav[offset] - 128) / 128.0;
            case 2:
                return BitConverter.ToInt16(_wav, offset) / 32768.0;
            case 3:
                int v24 = _wav[offset] | (_wav[offset + 1] << 8) | (_wav[offset + 2] << 16);
                if ((v24 & 0x800000) != 0) v24 |= unchecked((int)0xFF000000);   // sign-extend
                return v24 / 8388608.0;
            case 4:
                return BitConverter.ToInt32(_wav, offset) / 2147483648.0;
            default:
                return 0.0;
        }
    }
}
