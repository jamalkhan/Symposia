namespace Symposia.BlobStorage.Domain.Erasure;

/// <summary>
/// Systematic Reed–Solomon over GF(2⁸) (#8 FR5): <c>k</c> data shards and <c>m</c> parity shards, any
/// <c>k</c> of the <c>k + m</c> reconstruct the data. The encoding matrix is a Vandermonde matrix normalised
/// so its top <c>k</c> rows are the identity (data shards are the data, unchanged), which keeps every
/// <c>k</c>-row submatrix invertible. Field polynomial x⁸ + x⁴ + x³ + x² + 1 (0x11D), generator 2.
/// </summary>
public sealed class ReedSolomon
{
    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    static ReedSolomon()
    {
        var x = 1;
        for (var i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= 0x11D;
        }

        for (var i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }

    private static byte Mul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

    private static byte Div(byte a, byte b) =>
        b == 0 ? throw new DivideByZeroException() : a == 0 ? (byte)0 : Exp[Log[a] + 255 - Log[b]];

    private static byte Pow(byte a, int n) => n == 0 ? (byte)1 : a == 0 ? (byte)0 : Exp[Log[a] * n % 255];

    private readonly byte[,] _matrix; // (k + m) × k

    public ReedSolomon(int dataShards, int parityShards)
    {
        if (dataShards < 1 || parityShards < 0 || dataShards + parityShards > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(dataShards), "Need 1 ≤ k and k + m ≤ 255.");
        }

        DataShards = dataShards;
        ParityShards = parityShards;
        var n = dataShards + parityShards;
        var vandermonde = new byte[n, dataShards];
        for (var r = 0; r < n; r++)
            for (var c = 0; c < dataShards; c++)
                vandermonde[r, c] = Pow((byte)r, c);

        var top = new byte[dataShards, dataShards];
        for (var r = 0; r < dataShards; r++)
            for (var c = 0; c < dataShards; c++)
                top[r, c] = vandermonde[r, c];

        _matrix = Multiply(vandermonde, Invert(top));
    }

    public int DataShards { get; }
    public int ParityShards { get; }
    public int TotalShards => DataShards + ParityShards;

    /// <summary>Fills <paramref name="shards"/>[k..] with parity computed from <paramref name="shards"/>[..k]; all the same length.</summary>
    public void Encode(byte[][] shards, int length)
    {
        for (var p = 0; p < ParityShards; p++)
        {
            var output = shards[DataShards + p];
            Array.Clear(output, 0, length);
            for (var d = 0; d < DataShards; d++)
            {
                var coefficient = _matrix[DataShards + p, d];
                if (coefficient == 0) continue;
                var input = shards[d];
                for (var i = 0; i < length; i++)
                {
                    output[i] ^= Mul(coefficient, input[i]);
                }
            }
        }
    }

    /// <summary>
    /// Rebuilds every missing shard in place from any <c>k</c> present ones. <paramref name="present"/> marks
    /// which entries of <paramref name="shards"/> hold data; missing entries must be allocated buffers.
    /// </summary>
    public void Reconstruct(byte[][] shards, bool[] present, int length)
    {
        var available = Enumerable.Range(0, TotalShards).Where(i => present[i]).Take(DataShards).ToArray();
        if (available.Length < DataShards)
        {
            throw new InvalidOperationException($"Need {DataShards} shards to reconstruct; only {available.Length} present.");
        }

        var sub = new byte[DataShards, DataShards];
        for (var r = 0; r < DataShards; r++)
            for (var c = 0; c < DataShards; c++)
                sub[r, c] = _matrix[available[r], c];

        var decode = Invert(sub);
        // Data shards first: data = decode × available.
        for (var d = 0; d < DataShards; d++)
        {
            if (present[d]) continue;
            var output = shards[d];
            Array.Clear(output, 0, length);
            for (var j = 0; j < DataShards; j++)
            {
                var coefficient = decode[d, j];
                if (coefficient == 0) continue;
                var input = shards[available[j]];
                for (var i = 0; i < length; i++) output[i] ^= Mul(coefficient, input[i]);
            }
        }

        // Then any missing parity, from the now-complete data.
        for (var p = DataShards; p < TotalShards; p++)
        {
            if (present[p]) continue;
            var output = shards[p];
            Array.Clear(output, 0, length);
            for (var d = 0; d < DataShards; d++)
            {
                var coefficient = _matrix[p, d];
                if (coefficient == 0) continue;
                for (var i = 0; i < length; i++) output[i] ^= Mul(coefficient, shards[d][i]);
            }
        }
    }

    private static byte[,] Multiply(byte[,] a, byte[,] b)
    {
        int rows = a.GetLength(0), inner = a.GetLength(1), cols = b.GetLength(1);
        var result = new byte[rows, cols];
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
            {
                byte sum = 0;
                for (var i = 0; i < inner; i++) sum ^= Mul(a[r, i], b[i, c]);
                result[r, c] = sum;
            }

        return result;
    }

    /// <summary>Gauss–Jordan inversion over GF(2⁸).</summary>
    private static byte[,] Invert(byte[,] matrix)
    {
        var n = matrix.GetLength(0);
        var work = new byte[n, 2 * n];
        for (var r = 0; r < n; r++)
        {
            for (var c = 0; c < n; c++) work[r, c] = matrix[r, c];
            work[r, n + r] = 1;
        }

        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            while (pivot < n && work[pivot, col] == 0) pivot++;
            if (pivot == n) throw new InvalidOperationException("Singular matrix.");
            if (pivot != col)
                for (var c = 0; c < 2 * n; c++) (work[col, c], work[pivot, c]) = (work[pivot, c], work[col, c]);

            var scale = work[col, col];
            for (var c = 0; c < 2 * n; c++) work[col, c] = Div(work[col, c], scale);
            for (var r = 0; r < n; r++)
            {
                if (r == col || work[r, col] == 0) continue;
                var factor = work[r, col];
                for (var c = 0; c < 2 * n; c++) work[r, c] ^= Mul(factor, work[col, c]);
            }
        }

        var inverse = new byte[n, n];
        for (var r = 0; r < n; r++)
            for (var c = 0; c < n; c++)
                inverse[r, c] = work[r, n + c];
        return inverse;
    }
}

/// <summary>
/// A named erasure scheme, <c>rs:k+m</c> (#8 FR5.1), and the stripe layout objects are cut into: each
/// stripe is <c>k</c> units of <see cref="UnitSize"/> bytes plus <c>m</c> parity units, and shard <c>i</c> holds
/// unit <c>i</c> of every stripe, back to back. The last stripe is zero-padded; the object size trims it.
/// </summary>
public sealed record ErasureScheme(int DataShards, int ParityShards)
{
    public const int UnitSize = 64 * 1024;

    public int TotalShards => DataShards + ParityShards;

    public int StripeDataBytes => DataShards * UnitSize;

    /// <summary>Shards that must durably confirm before a write succeeds: k plus half the parity, rounded up.</summary>
    public int WriteQuorum => DataShards + (ParityShards + 1) / 2;

    public long Stripes(long objectSize) => Math.Max(1, (objectSize + StripeDataBytes - 1) / StripeDataBytes);

    public override string ToString() => $"rs:{DataShards}+{ParityShards}";

    public static bool TryParse(string? value, out ErasureScheme scheme)
    {
        scheme = null!;
        if (value is null || !value.StartsWith("rs:", StringComparison.Ordinal)) return false;
        var parts = value[3..].Split('+');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var k) || !int.TryParse(parts[1], out var m)
            || k < 1 || m < 1 || k + m > 32)
        {
            return false;
        }

        scheme = new ErasureScheme(k, m);
        return true;
    }
}
