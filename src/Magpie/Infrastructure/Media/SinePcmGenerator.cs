namespace Magpie.Infrastructure.Media;

/// <summary>Generates canonical PCM (s16le / 48 kHz / stereo) sine tones for local FIFO proof.</summary>
public sealed class SinePcmGenerator
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;

    private readonly MagpieOptions _options;

    public SinePcmGenerator(Microsoft.Extensions.Options.IOptions<MagpieOptions> options)
    {
        _options = options.Value;
    }

    public Stream CreateStream(CancellationToken cancellationToken = default)
    {
        var frequency = _options.SineFrequencyHz <= 0 ? 440 : _options.SineFrequencyHz;
        var duration = _options.SineDurationSeconds <= 0 ? 30 : _options.SineDurationSeconds;
        var totalSamples = (int)(SampleRate * duration);
        var memory = new MemoryStream(totalSamples * Channels * sizeof(short));
        var phase = 0.0;
        var phaseIncrement = 2.0 * Math.PI * frequency / SampleRate;

        for (var i = 0; i < totalSamples; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sample = (short)(Math.Sin(phase) * short.MaxValue * 0.2);
            phase += phaseIncrement;
            if (phase > 2.0 * Math.PI)
            {
                phase -= 2.0 * Math.PI;
            }

            memory.WriteByte((byte)(sample & 0xFF));
            memory.WriteByte((byte)((sample >> 8) & 0xFF));
            memory.WriteByte((byte)(sample & 0xFF));
            memory.WriteByte((byte)((sample >> 8) & 0xFF));
        }

        memory.Position = 0;
        return memory;
    }
}
