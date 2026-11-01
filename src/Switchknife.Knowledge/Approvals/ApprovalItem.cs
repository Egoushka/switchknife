using System.Text.Json.Serialization;

namespace Switchknife.Knowledge.Approvals;

/// <summary>A quote shown on an item, with where it came from.</summary>
public sealed record ApprovalEvidence(string Quote, string? Url = null);

/// <summary>A link the item opens in the browser; it takes no decision.</summary>
public sealed record ApprovalLink(string Label, string Url);

/// <summary>A button on an item.</summary>
/// <param name="Id">What the decision carries as <c>action</c>.</param>
/// <param name="Label">The button text.</param>
/// <param name="Style"><c>primary</c>, <c>danger</c> or null.</param>
/// <param name="Confirm">True when the button needs a second tap.</param>
public sealed record ApprovalAction(string Id, string Label, string? Style = null, bool? Confirm = null);

/// <summary>
/// An item for the approvals shell, in its contract: the id starts with <see cref="Source"/>, every <see cref="Editable"/> path names a string in
/// <see cref="Body"/>, and the shell POSTs the decision to <see cref="Callback"/>.
/// </summary>
/// <param name="Source">Always <c>knowledge</c> here.</param>
/// <param name="Id"><c>knowledge:idea:&lt;idea&gt;</c> or <c>knowledge:result:&lt;experiment&gt;</c>.</param>
/// <param name="Kind"><c>decision</c> or <c>info</c>.</param>
/// <param name="Priority">1 to 5.</param>
/// <param name="Title">The item's heading.</param>
/// <param name="Summary">One line for the queue.</param>
/// <param name="Evidence">Quotes with their links.</param>
/// <param name="Body">The fields shown; all strings.</param>
/// <param name="Editable">The body paths the owner may change, as <c>body.&lt;key&gt;</c>.</param>
/// <param name="Links">Links to open.</param>
/// <param name="Actions">The buttons.</param>
/// <param name="ExpiresAt">When the item stops taking decisions; null leaves it to the shell's default.</param>
/// <param name="Callback">Where the decision goes; given by the host, never built here.</param>
public sealed record ApprovalItem(
    string Source,
    string Id,
    string Kind,
    int Priority,
    string Title,
    string Summary,
    IReadOnlyList<ApprovalEvidence> Evidence,
    IReadOnlyDictionary<string, string> Body,
    IReadOnlyList<string> Editable,
    IReadOnlyList<ApprovalLink> Links,
    IReadOnlyList<ApprovalAction> Actions,
    DateTimeOffset? ExpiresAt,
    string Callback);

/// <summary>A decision as the shell POSTs it. Every field may be missing or wrong until <see cref="KnowledgeApprovals"/> has checked it.</summary>
/// <param name="Decision">The decision's id (a ULID); the shell reuses it on a retry.</param>
/// <param name="Id">The item's id.</param>
/// <param name="Action">The button's id.</param>
/// <param name="Edited">Only the body paths the owner changed.</param>
/// <param name="At">When the decision was signed, as sent; the signature covers this text.</param>
/// <param name="Sig">HMAC-SHA256 hex, see <see cref="ApprovalSignature"/>.</param>
public sealed record ApprovalDecision(
    string? Decision,
    string? Id,
    string? Action,
    Dictionary<string, string>? Edited,
    string? At,
    string? Sig);

/// <summary>What the source answers; the shell shows <see cref="Message"/> on the item.</summary>
public sealed record ApprovalReply(bool Ok, string? Message = null);

/// <summary>Source-generated serialization of items, decisions and replies; no reflection.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ApprovalItem))]
[JsonSerializable(typeof(ApprovalDecision))]
[JsonSerializable(typeof(ApprovalReply))]
public sealed partial class ApprovalJsonContext : JsonSerializerContext;
