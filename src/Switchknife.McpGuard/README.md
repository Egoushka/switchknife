# Switchknife.McpGuard

Guards an MCP server on the streamable HTTP transport, or any endpoint that only non-browser clients call, with two checks in this order (and a third, `AllowHosts`, below):

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

A page that reaches the server through DNS rebinding can still send a request without `Origin` (a GET, for one), but its `Host` header names the attacker's domain. `AllowHosts` admits only the host names the server is meant to be addressed by and refuses the rest with 403:

```csharp
app.MapMcp("/mcp").RequireMcpGuard(app.Configuration["Mcp:Token"]).AllowHosts("localhost", "127.0.0.1", "[::1]");
```

An entry without a port admits that host on any port; `localhost:5000` admits port 5000 only. Names compare ignoring case; a bare IPv6 address (`::1`) is read as `[::1]`. No entries, or a request without a `Host` header, refuses the request. Behind a reverse proxy, the `Host` checked is the one the server sees, after any forwarded-headers middleware.

All three methods are endpoint conventions: they apply to an endpoint, a route group (every endpoint in it, including ones mapped later) or whatever `MapMcp` returns, and return the same builder for chaining. Refused requests get the status code and an empty body.

Limits:

- One static token. Rotate it by restarting with a new value; scopes or per-client tokens belong to an authentication scheme, with `RejectOrigin` beside it.
- The token's length is not hidden: the comparison is fixed-time for equal lengths only.
- No dependency on the MCP SDK; it guards whatever endpoint it is applied to.
