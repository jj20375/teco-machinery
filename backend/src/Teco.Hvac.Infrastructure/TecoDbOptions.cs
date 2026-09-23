namespace Teco.Hvac.Infrastructure;

public sealed class TecoDbOptions
{
    public const string SectionName = "TecoDb";
    public required string ConnectionString { get; init; }
}
