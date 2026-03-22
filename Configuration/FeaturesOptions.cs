namespace HazelInvoice.Configuration;

/// <summary>
/// Simple feature flags used to enable/disable optional modules without deleting code/data.
/// Keep defaults conservative (false) so new installs don't unexpectedly show unfinished modules.
/// </summary>
public sealed class FeaturesOptions
{
    public bool PartnersEnabled { get; set; } = false;
    public bool AllowDangerousDatabaseReset { get; set; } = false;
    public bool AllowPublicRegistration { get; set; } = false;
}
