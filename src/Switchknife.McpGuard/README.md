# Switchknife.McpGuard

Guards an MCP server on the streamable HTTP transport, or any endpoint that only non-browser clients call, with two checks in this order:

1. Any `Origin` header is refused with 403. A browser sends one on every cross-origin request and on every POST, so a page that reaches the server through DNS rebinding is turned away before anything else runs. MCP clients (agents, gateways, CLIs) send none.
2. The `Authorization` header must be exactly `Bearer {token}`, compared in fixed time; anything else is 401. A null or empty token refuses every request, so a missing setting closes the endpoint instead of opening it.

```csharp
builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools<MyTools>();

var app = builder.Build();
app.MapMcp("/mcp").RequireMcpGuard(app.Configuration["Mcp:Token"]);
```

An endpoint authenticated by other means (an authentication scheme, a signature) keeps the Origin check alone:

```csharp
app.MapMcp("/mcp").RequireAuthorization("read").RejectOrigin();
app.MapGroup("/approvals").RejectOrigin();
```

Both methods are endpoint conventions: they apply to an endpoint, a route group (every endpoint in it, including ones mapped later) or whatever `MapMcp` returns, and return the same builder for chaining. Refused requests get the status code and an empty body.

Limits:

- One static token. Rotate it by restarting with a new value; scopes or per-client tokens belong to an authentication scheme, with `RejectOrigin` beside it.
- The token's length is not hidden: the comparison is fixed-time for equal lengths only.
- No dependency on the MCP SDK; it guards whatever endpoint it is applied to.
