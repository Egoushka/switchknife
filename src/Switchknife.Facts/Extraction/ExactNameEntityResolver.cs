namespace Switchknife.Facts.Extraction;

/// <summary>
/// The default <see cref="IEntityResolver"/>: an entity of the same kind and the same name (trimmed, case-insensitive) is the entity;
/// none is created; two or more entities that share the name cannot be told apart by it, so the mention is not placed (null). It
/// proposes nothing fuzzy: a wrong merge poisons facts. Entity resolution proper (aliases, phonetic keys, embeddings) replaces it in F4.
/// </summary>
public sealed class ExactNameEntityResolver(FactsStore store) : IEntityResolver
{
    private readonly FactsStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <inheritdoc />
    public Task<Guid?> ResolveAsync(EntityMention mention, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mention);
        return mention.KnownId is { } known ? Task.FromResult<Guid?>(known) : _store.ResolveByExactNameAsync(mention.Kind, mention.Name, cancellationToken);
    }
}
