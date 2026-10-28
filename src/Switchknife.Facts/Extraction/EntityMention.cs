namespace Switchknife.Facts.Extraction;

/// <summary>An entity a validated fact is about or points at.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Name">Its name as written.</param>
/// <param name="KnownId">The entity's id when the extractor named a handle or an exact name of the context; null when the resolver must place it.</param>
public sealed record EntityMention(string Kind, string Name, Guid? KnownId);

/// <summary>
/// Turns a name into an entity. The seam F4 (entity resolution) fills; <c>ExactNameEntityResolver</c> is the default.
/// A wrong placement poisons facts and a duplicate is cheap to fix, so an implementation that cannot decide returns null.
/// </summary>
public interface IEntityResolver
{
    /// <summary>The entity's id (created if the resolver creates), or null when the mention cannot be placed; its fact is then skipped.</summary>
    Task<Guid?> ResolveAsync(EntityMention mention, CancellationToken cancellationToken = default);
}
