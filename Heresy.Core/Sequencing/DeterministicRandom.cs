namespace Heresy.Core.Sequencing;

/// <summary>
/// Small deterministic PRNG used by sequencing. This intentionally avoids
/// System.Random so song output does not depend on framework implementation
/// changes. Algorithm: xoshiro256** seeded through SplitMix64.
/// </summary>
public sealed class DeterministicRandom
{
	private ulong _s0;
	private ulong _s1;
	private ulong _s2;
	private ulong _s3;

	public DeterministicRandom(ulong seed)
	{
		ulong state = seed;
		_s0 = SplitMix64(ref state);
		_s1 = SplitMix64(ref state);
		_s2 = SplitMix64(ref state);
		_s3 = SplitMix64(ref state);

		if ((_s0 | _s1 | _s2 | _s3) == 0)
			_s0 = 1;
	}

	public ulong NextUInt64()
	{
		ulong result = RotateLeft(_s1 * 5, 7) * 9;
		ulong t = _s1 << 17;

		_s2 ^= _s0;
		_s3 ^= _s1;
		_s1 ^= _s2;
		_s0 ^= _s3;
		_s2 ^= t;
		_s3 = RotateLeft(_s3, 45);

		return result;
	}

	public double NextDouble()
		=> (NextUInt64() >> 11) * (1.0 / (1UL << 53));

	public DeterministicRandom CreateChild() => new(NextUInt64());

	private static ulong SplitMix64(ref ulong state)
	{
		ulong z = (state += 0x9E3779B97F4A7C15UL);
		z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
		z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
		return z ^ (z >> 31);
	}

	private static ulong RotateLeft(ulong value, int count)
		=> (value << count) | (value >> (64 - count));
}
