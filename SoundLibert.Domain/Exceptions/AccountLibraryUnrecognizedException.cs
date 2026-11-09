namespace SoundLibert.Domain.Exceptions;

/// <summary>
/// An account library answer held no list this app can read, or a later page
/// of the list could not be read. Nothing can be concluded from it, so nothing
/// is reconciled: reading it as "the account is empty" would remove the local
/// mirror of that section. A brand-new account's empty library can look the
/// same, which is why this is logged and not shown to the user as an error.
/// </summary>
public sealed class AccountLibraryUnrecognizedException : Exception
{
    public AccountLibraryUnrecognizedException(string section, string detail)
        : base($"Account library section {section}: {detail}")
    {
        Section = section;
    }

    public string Section { get; }
}
