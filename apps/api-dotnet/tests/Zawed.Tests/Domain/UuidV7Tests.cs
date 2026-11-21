using Zawed.Domain.Ids;

namespace Zawed.Tests.Domain;

/// <summary>
/// Java <c>UuidV7Tests</c>. The .NET ids come from <c>Guid.CreateVersion7</c>, which takes an instant rather than a
/// millisecond count and a random source, so <c>rejectsATimestampThatDoesNotFit</c> keeps only its lower bound: no
/// <see cref="DateTimeOffset"/> reaches 2^48 ms (year 10889), the upper one cannot be expressed.
/// </summary>
public sealed class UuidV7Tests
{
    private static DateTimeOffset Millis(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    [Fact]
    public void IsAVersion7Rfc9562Uuid()
    {
        var id = UuidV7.Create(Millis(1_794_000_000_000L));
        Assert.Equal(7, id.Version);
        // RFC 9562 variant: the top two bits of byte 8 (in text order) are 10.
        Assert.Equal(0b10, Convert.ToInt32(id.ToString("N")[16..17], 16) >> 2);
    }

    [Fact]
    public void CarriesItsTimestamp()
    {
        const long millis = 1_794_000_123_456L;
        Assert.Equal(millis, UuidV7.TimestampOf(UuidV7.Create(Millis(millis))));
    }

    [Fact]
    public void SortsByCreationTimeAsText()
    {
        var earlier = UuidV7.Create(Millis(1_794_000_000_000L));
        var later = UuidV7.Create(Millis(1_794_000_000_001L));
        // PostgreSQL compares uuid values byte by byte, which is the order of the text form.
        Assert.True(string.CompareOrdinal(earlier.ToString(), later.ToString()) < 0);
    }

    [Fact]
    public void DoesNotRepeatWithinOneMillisecond()
    {
        var ids = new HashSet<Guid>();
        for (var i = 0; i < 10_000; i++)
        {
            ids.Add(UuidV7.Create(Millis(1_794_000_000_000L)));
        }
        Assert.Equal(10_000, ids.Count);
    }

    [Fact]
    public void RejectsATimestampThatDoesNotFit() =>
        Assert.ThrowsAny<ArgumentException>(() => UuidV7.Create(Millis(-1)));

    [Fact]
    public void ReadsATimestampOnlyFromAVersion7Id() =>
        Assert.Throws<ArgumentException>(() => UuidV7.TimestampOf(Guid.NewGuid()));
}
