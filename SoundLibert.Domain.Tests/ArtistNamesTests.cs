using SoundLibert.Domain.Models;

namespace SoundLibert.Domain.Tests;

public class ArtistNamesTests
{
    [Theory]
    [InlineData("Океан Ельзи", "Okean Elzy")]
    [InlineData("Ирина Билык", "Iryna Bilyk")]
    [InlineData("Ірина Білик", "Iryna Bilyk")]
    [InlineData("Скрябін", "Skryabin")]
    [InlineData("Скрябин", "Skriabin")]
    [InlineData("Святослав Вакарчук", "Sviatoslav Vakarchuk")]
    [InlineData("Imagine Dragons", "IMAGINE DRAGONS")]
    [InlineData("Океан Ельзи", "Океан Ельзи")]
    public void One_artist_in_two_spellings(string one, string other) =>
        Assert.True(ArtistNames.SoundTheSame(one, other));

    [Theory]
    [InlineData("Океан Ельзи", "Ocean Drive")]
    [InlineData("Скрябін", "Skrillex")]
    [InlineData("Coldplay", "Cold War Kids")]
    [InlineData("Lama", "Lana")]
    [InlineData("MONATIK", "Monetochka")]
    public void Two_artists_stay_two(string one, string other) =>
        Assert.False(ArtistNames.SoundTheSame(one, other));

    [Theory]
    [InlineData(null, "Okean Elzy")]
    [InlineData("", "")]
    [InlineData("!!!", "???")]
    [InlineData("ТНМ", "АБВ")]
    public void Nothing_to_compare_is_no_match(string? one, string? other) =>
        Assert.False(ArtistNames.SoundTheSame(one, other));
}
