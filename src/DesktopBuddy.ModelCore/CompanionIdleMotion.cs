using System.Numerics;

namespace DesktopBuddy.ModelCore;

/// <summary>Small, deterministic idle micro-motions layered over the model's authored idle clip.</summary>
public readonly record struct CompanionIdlePose(Vector3 Body, Vector3 Head, float Breathing);

public static class CompanionIdleMotion
{
    public static CompanionIdlePose Sample(double seconds)
    {
        var time = double.IsFinite(seconds) ? Math.Clamp(seconds, 0, 1e12) : 0;
        var phase = Math.Tau * (time / 3.7 + Noise(time, 23.0, 0x19A4u) * .025);
        var breathing = (float)(Math.Sin(phase) + .10 * Math.Sin(phase * 2 - .8));

        // Smooth value noise gives the pet long, asymmetric pauses instead of a mechanical loop.
        var attention = SampleAttention(time);
        var body = new Vector3(
            Noise(time, 5.2, 0x31u) * .036f + Noise(time, 14.7, 0x42u) * .010f,
            breathing * .0075f + Noise(time, 8.6, 0x53u) * .008f,
            Noise(time, 9.8, 0x64u) * .012f) + attention.Body;
        var head = new Vector3(
            Noise(time, 6.4, 0x75u) * .040f + Noise(time, 17.2, 0x86u) * .014f,
            breathing * .0045f + Noise(time, 8.1, 0x97u) * .008f,
            Noise(time, 6.8, 0xA8u) * .012f) + attention.Head;
        return new(body, head, breathing);
    }

    private static (Vector3 Body, Vector3 Head) SampleAttention(double time)
    {
        const double period = 18.0;
        var cycle = (long)Math.Floor(time / period);
        var body = Vector3.Zero;
        var head = Vector3.Zero;
        for (var index = cycle - 1; index <= cycle; index++)
        {
            var direction = Value(index, 0xB9u) >= 0 ? 1f : -1f;
            var start = index * period + 4.0 + (Value(index, 0xCAu) + 1.0) * 1.8;
            var duration = 2.7 + (Value(index, 0xDBu) + 1.0) * .3;
            var progress = (float)((time - start) / duration);
            var envelope = Smooth(progress / .30f) * (1 - Smooth((progress - .70f) / .30f));
            body += new Vector3(-direction * .035f * envelope, .012f * envelope, direction * .030f * envelope);
            head += new Vector3(direction * .105f * envelope, -.018f * envelope, -direction * .025f * envelope);
        }
        return (body, head);
    }

    private static float Smooth(float value)
    {
        var t = Math.Clamp(value, 0, 1);
        return t * t * t * (t * (t * 6 - 15) + 10);
    }

    private static float Noise(double time, double interval, uint seed)
    {
        var position = time / interval;
        var cell = (long)Math.Floor(position);
        var fraction = (float)(position - cell);
        fraction *= fraction * (3 - 2 * fraction);
        var a = Value(cell, seed);
        var b = Value(cell + 1, seed);
        return a + (b - a) * fraction;
    }

    private static float Value(long cell, uint seed)
    {
        var value = unchecked((ulong)cell + seed + 0x9E3779B97F4A7C15UL);
        value ^= value >> 30;
        value = unchecked(value * 0xBF58476D1CE4E5B9UL);
        value ^= value >> 27;
        value = unchecked(value * 0x94D049BB133111EBUL);
        value ^= value >> 31;
        return (float)((value >> 40) * (2.0 / 16777215.0) - 1.0);
    }
}
