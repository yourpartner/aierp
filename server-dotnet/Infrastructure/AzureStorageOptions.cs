namespace Server.Infrastructure;

public sealed class AzureStorageOptions
{
    public string? ConnectionString { get; set; }
    public string? ContainerName { get; set; }
    public int SasExpiryMinutes { get; set; } = 120;

    internal bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ConnectionString) &&
        !string.IsNullOrWhiteSpace(ContainerName);
}

