using System.Collections.Immutable;
using Axiom.Application.Common;
using Axiom.Domain.Governance;

namespace Axiom.Application.Evaluation;

/// <summary>Size and shape limits for untrusted caller input (design.md §14: content-size and path limits).</summary>
public static class RequestLimits
{
    public const int MaxTaskLength = 16_000;
    public const int MaxPaths = 5_000;
    public const int MaxPathLength = 1_024;
    public const int MaxIdentifierLength = 256;
    public const int MaxDesignLength = 200_000;

    public static string Identifier(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw AxiomException.Invalid($"'{name}' is required.");
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxIdentifierLength || trimmed.Any(char.IsControl))
        {
            throw AxiomException.Invalid($"'{name}' is not a valid identifier.");
        }

        return trimmed;
    }

    public static string? OptionalIdentifier(string? value, string name) => string.IsNullOrWhiteSpace(value) ? null : Identifier(value, name);

    public static string? Text(string? value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= maxLength ? value : throw AxiomException.Invalid($"'{name}' exceeds {maxLength} characters.");
    }

    /// <summary>Normalizes repository-relative paths and rejects absolute paths and traversal.</summary>
    public static ImmutableArray<string>? Paths(IReadOnlyCollection<string>? paths)
    {
        if (paths is null)
        {
            return null;
        }

        if (paths.Count > MaxPaths)
        {
            throw AxiomException.Invalid($"At most {MaxPaths} paths may be supplied.");
        }

        var result = ImmutableArray.CreateBuilder<string>(paths.Count);
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxPathLength || raw.Any(char.IsControl))
            {
                throw AxiomException.Invalid("A supplied path is empty, too long, or contains control characters.");
            }

            var normalized = GlobPattern.NormalizePath(raw);
            var rooted = raw.TrimStart().StartsWith('/') || raw.TrimStart().StartsWith('\\') || (raw.Length > 1 && raw[1] == ':');
            if (normalized.Length == 0 || rooted || normalized.Split('/').Contains(".."))
            {
                throw AxiomException.Invalid($"Path '{raw}' must be relative to the repository root and must not traverse upward.");
            }

            result.Add(normalized);
        }

        return [.. result.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    public static string? CommitSha(string? value, string name, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return required ? throw AxiomException.Invalid($"'{name}' is required.") : null;
        }

        var sha = value.Trim().ToLowerInvariant();
        return sha.Length is >= 7 and <= 64 && sha.All(char.IsAsciiHexDigit)
            ? sha
            : throw AxiomException.Invalid($"'{name}' must be a hexadecimal commit SHA.");
    }
}
