namespace SoundLibert.Domain.Exceptions;

/// <summary>
/// YouTube answered an account request as if the visitor were signed out
/// (<c>logged_in=0</c>): the stored session silently stopped working. The
/// response carries a "sign in" prompt instead of the library, so it must
/// never be read as "the account is empty" — that wiped the local mirror of
/// likes, playlists, subscriptions and saved albums.
/// </summary>
public sealed class AccountSessionInvalidException : Exception
{
    public AccountSessionInvalidException(string section)
        : base($"YouTube no longer recognises the stored session ({section}).")
    {
        Section = section;
    }

    public string Section { get; }
}
