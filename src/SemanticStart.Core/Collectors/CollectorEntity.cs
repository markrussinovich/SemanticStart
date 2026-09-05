using System.Security.Cryptography;
using System.Text;
using SemanticStart.Core.Model;

namespace SemanticStart.Core.Collectors;

internal static class CollectorEntity
{
    public static Entity WithContentHash(Entity entity) => entity with { ContentHash = ComputeContentHash(entity) };

    private static string ComputeContentHash(Entity entity)
    {
        var builder = new StringBuilder();
        Append(builder, entity.Id);
        Append(builder, entity.Kind.ToString());
        Append(builder, entity.DisplayName);
        Append(builder, entity.LaunchKind.ToString());
        Append(builder, entity.LaunchTarget);
        Append(builder, entity.LaunchArguments);
        Append(builder, entity.IconSource);
        Append(builder, entity.Publisher);
        Append(builder, entity.Source);

        foreach (var pair in entity.RawMetadata.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            Append(builder, pair.Key);
            Append(builder, pair.Value);
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void Append(StringBuilder builder, string? value)
    {
        builder.Append(value?.Length ?? -1);
        builder.Append(':');
        builder.Append(value);
        builder.Append('|');
    }
}
