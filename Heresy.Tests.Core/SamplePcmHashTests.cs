using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

using Heresy.Core.Samples;

using NUnit.Framework;

namespace Heresy.Tests.Core;

[TestFixture]
public sealed class SamplePcmHashTests
{
    [Test]
    public void ConstructorOwnsSampleBitsAndComputesOneImmutableCanonicalHash()
    {
        float[] values = [0f, BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)), 1f,
            BitConverter.Int32BitsToSingle(unchecked((int)0x7fc00001))];
        SamplePcmData pcm = new(48000, 2, values);
        byte[] canonical = new byte[values.Length * sizeof(float)];
        for (int index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteInt32LittleEndian(
                canonical.AsSpan(index * sizeof(float), sizeof(float)),
                BitConverter.SingleToInt32Bits(values[index]));
        string expected = Convert.ToHexString(SHA256.HashData(canonical));

        Assert.That(pcm.Sha256, Is.EqualTo(expected));
        Assert.That(typeof(SamplePcmData).GetProperty(nameof(SamplePcmData.Sha256))!
            .SetMethod, Is.Null, "the content hash cannot be supplied or overwritten");
        values[0] = 99f;
        values[1] = 7f;
        Assert.That(pcm.Sha256, Is.EqualTo(expected));
        Assert.That(BitConverter.SingleToInt32Bits(pcm.GetSample(0, 1)),
            Is.EqualTo(BitConverter.SingleToInt32Bits(BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)))),
            "the PCM buffer is a defensive copy, not the caller's array");
        Assert.That(pcm.HasSameAudio(new SamplePcmData(48000, 2, [
            0f, BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)), 1f, BitConverter.Int32BitsToSingle(unchecked((int)0x7fc00001))
        ])), Is.True);
    }

    [Test]
    public void EqualityDistinguishesSignedZeroAndNanPayloadsDespiteFloatingEquality()
    {
        SamplePcmData positive = new(44100, 1, [0f]);
        SamplePcmData negative = new(44100, 1, [BitConverter.Int32BitsToSingle(unchecked((int)0x80000000))]);
        Assert.That(positive.Sha256, Is.Not.EqualTo(negative.Sha256));
        Assert.That(positive.HasSameAudio(negative), Is.False);
        Assert.That(positive.HasSameAudio(new SamplePcmData(48000, 1, [0f])),
            Is.False);
        SamplePcmData nanOne = new(44100, 1, [
            BitConverter.Int32BitsToSingle(unchecked((int)0x7fc00001))
        ]);
        SamplePcmData nanTwo = new(44100, 1, [
            BitConverter.Int32BitsToSingle(unchecked((int)0x7fc00002))
        ]);
        Assert.That(nanOne.Sha256, Is.Not.EqualTo(nanTwo.Sha256));
        Assert.That(nanOne.HasSameAudio(nanTwo), Is.False);
    }

    [Test]
    public void ReusingTheSamePcmInstanceDoesNotReconstructOrReplaceItsHash()
    {
        SamplePcmData original = new(22050, 1, [.125f, -.25f]);
        string hash = original.Sha256;
        Assert.That(original.HasSameAudio(original), Is.True);
        SamplePcmData modified = new(22050, 1, [.125f, -.20f]);
        Assert.That(modified.Sha256, Is.Not.EqualTo(hash));
        Assert.That(original.Sha256, Is.EqualTo(hash));
        Assert.That(modified.HasSameAudio(original), Is.False);
    }
}
