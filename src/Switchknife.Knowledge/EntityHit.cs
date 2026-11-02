namespace Switchknife.Knowledge;

/// <summary>One fact shown under an entity.</summary>
public sealed record EntityFact(Guid Id, string Predicate, string Text, double Score);

/// <summary>An entity the question is about, with the facts that put it here.</summary>
/// <param name="Namespace">The namespace the entity lives in.</param>
/// <param name="EntityId">The entity's id.</param>
/// <param name="Kind">Its kind (<c>tool</c>, <c>practice</c>, …).</param>
/// <param name="Name">Its name.</param>
/// <param name="ExternalRef">The consumer's id of it, when it has one.</param>
/// <param name="Score">The reciprocal-rank score across namespaces; higher is better.</param>
/// <param name="Facts">Its best facts for this question, best first.</param>
public sealed record EntityHit(
    string Namespace, Guid EntityId, string Kind, string Name, string? ExternalRef, double Score, IReadOnlyList<EntityFact> Facts);
