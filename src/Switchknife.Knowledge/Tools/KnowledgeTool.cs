using System.Text.Json.Nodes;

namespace Switchknife.Knowledge.Tools;

/// <summary>
/// A tool the host registers with its MCP server. The kit builds the definition and runs the handler; the host does the
/// transport and the guard (<c>RequireMcpGuard</c>).
/// </summary>
/// <param name="Name">The tool name.</param>
/// <param name="Description">What the tool does, for the model.</param>
/// <param name="InputSchema">The JSON schema of the arguments, exported from the source-generated <c>JsonTypeInfo</c>.</param>
/// <param name="Private">True when the tool reads or writes a private namespace: the host must not serve it to a session in the employer workspace.</param>
/// <param name="Handler">Takes the arguments as JSON text and returns the result as JSON text. Bad arguments come back as rejection codes, not exceptions.</param>
public sealed record KnowledgeTool(
    string Name,
    string Description,
    JsonObject InputSchema,
    bool Private,
    Func<string, CancellationToken, Task<string>> Handler);
