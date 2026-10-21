namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// The arithmetic of limits, written so that a length nobody can trust (a <c>Content-Length</c> near <c>long.MaxValue</c>, a usage file with absurd numbers) can never wrap into a small
/// number that lets a download through: sums saturate, differences stop at zero, and "does it fit" is asked as <c>planned &lt;= limit - held</c>, never as <c>held + planned &lt;= limit</c>.
/// </summary>
internal static class BlindMath
{
    /// <summary>The sum of two non-negative counts, or <see cref="long.MaxValue"/> when it would not fit.</summary>
    public static long AddSat(long a, long b) => b > 0 && a > long.MaxValue - b ? long.MaxValue : a + b;

    /// <summary><paramref name="a"/> minus <paramref name="b"/>, never below zero.</summary>
    public static long SubFloor(long a, long b) => b >= a ? 0 : a - b;

    /// <summary>The product of two non-negative counts, or <see cref="long.MaxValue"/> when it would not fit.</summary>
    public static long MulSat(long a, long b) => a <= 0 || b <= 0 ? 0 : a > long.MaxValue / b ? long.MaxValue : a * b;

    /// <summary>True when <paramref name="planned"/> more bytes fit under <paramref name="limit"/> with <paramref name="held"/> already there. Never overflows; a negative count never fits.</summary>
    public static bool Fits(long limit, long held, long planned) => planned >= 0 && held >= 0 && limit >= 0 && held <= limit && planned <= limit - held;

    /// <summary>What <paramref name="held"/> plus <paramref name="planned"/> comes to for a message (saturated, so that "needed" is never a wrapped, small number).</summary>
    public static long Needed(long held, long planned) => AddSat(Math.Max(0, held), Math.Max(0, planned));
}
