using System.Net;
using System.Net.Sockets;

namespace Switchknife.SafeFetch.Tests;

public sealed class IpGuardTests
{
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("172.15.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2a00:1450:4001:81b::200e")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::808:808")]
    [InlineData("2002:808:808::1")]
    public void Public_addresses_are_allowed(string ip) => Assert.True(IpGuard.IsPublic(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("100.64.0.0")]
    [InlineData("100.64.1.7")]          // a CGNAT address, as tailnets use
    [InlineData("100.127.255.255")]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("169.254.169.254")]     // cloud metadata
    [InlineData("169.254.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("192.0.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.255")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.9")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.250")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.1.2.3")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::10.0.0.1")]
    [InlineData("64:ff9b::a00:1")]      // NAT64 of 10.0.0.1
    [InlineData("2002:a00:1::1")]       // 6to4 of 10.0.0.1
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")]   // Teredo
    [InlineData("2001:db8::1")]
    [InlineData("fe80::1")]
    [InlineData("febf::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456:789a::1")]
    [InlineData("fec0::1")]
    [InlineData("ff02::1")]
    [InlineData("100::1")]
    public void Internal_and_special_addresses_are_refused(string ip) => Assert.False(IpGuard.IsPublic(IPAddress.Parse(ip)));
}

public sealed class GuardedConnectorTests
{
    private static (GuardedConnector Connector, List<(IPAddress Address, int Port)> Connects, int[] Resolves) Build(params string[] answers)
    {
        var connects = new List<(IPAddress, int)>();
        var resolves = new[] { 0 };
        var connector = new GuardedConnector(
            (_, _) =>
            {
                resolves[0]++;
                return Task.FromResult(answers.Select(IPAddress.Parse).ToArray());
            },
            (address, port, _) =>
            {
                connects.Add((address, port));
                return Task.FromResult<Stream>(new MemoryStream());
            });
        return (connector, connects, resolves);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("100.64.1.7")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    public async Task A_name_that_resolves_to_an_internal_address_is_refused_without_connecting(string answer)
    {
        var (connector, connects, _) = Build(answer);

        await Assert.ThrowsAsync<UnsafeAddressException>(async () => await connector.ConnectAsync("example.test", 443, default));

        Assert.Empty(connects);
    }

    [Fact]
    public async Task One_internal_answer_among_public_ones_refuses_the_whole_fetch()
    {
        var (connector, connects, _) = Build("8.8.8.8", "10.0.0.1");

        await Assert.ThrowsAsync<UnsafeAddressException>(async () => await connector.ConnectAsync("rebind.test", 443, default));

        Assert.Empty(connects);
    }

    [Fact]
    public async Task The_validated_address_is_the_one_connected_to_and_the_name_is_resolved_once()
    {
        var (connector, connects, resolves) = Build("8.8.4.4");

        await connector.ConnectAsync("example.test", 443, default);

        Assert.Equal(1, resolves[0]);
        Assert.Equal([(IPAddress.Parse("8.8.4.4"), 443)], connects);
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    public async Task Literal_internal_hosts_are_refused_without_a_lookup(string host)
    {
        var (connector, connects, resolves) = Build("8.8.8.8");

        await Assert.ThrowsAsync<UnsafeAddressException>(async () => await connector.ConnectAsync(host.Trim('[', ']'), 80, default));

        Assert.Equal(0, resolves[0]);
        Assert.Empty(connects);
    }

    [Theory]
    [InlineData(22)]
    [InlineData(8080)]
    [InlineData(5432)]
    public async Task Only_ports_80_and_443_are_allowed(int port)
    {
        var (connector, _, _) = Build("8.8.8.8");

        await Assert.ThrowsAsync<UnsafeAddressException>(async () => await connector.ConnectAsync("example.test", port, default));
    }

    [Fact]
    public async Task A_second_address_is_tried_when_the_first_does_not_connect()
    {
        var tried = new List<IPAddress>();
        var connector = new GuardedConnector(
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("8.8.4.4") }),
            (address, _, _) =>
            {
                tried.Add(address);
                return tried.Count == 1 ? throw new SocketException((int)SocketError.ConnectionRefused) : Task.FromResult<Stream>(new MemoryStream());
            });

        await connector.ConnectAsync("example.test", 443, default);

        Assert.Equal(2, tried.Count);
    }
}

public sealed class TrustedHostConnectorTests
{
    private static (GuardedConnector Connector, List<(IPAddress Address, int Port)> Connects) Build(string answer)
    {
        var connects = new List<(IPAddress, int)>();
        var connector = new GuardedConnector(
            (_, _) => Task.FromResult(new[] { IPAddress.Parse(answer) }),
            (address, port, _) =>
            {
                connects.Add((address, port));
                return Task.FromResult<Stream>(new MemoryStream());
            });
        return (connector, connects);
    }

    [Fact]
    public async Task A_trusted_host_may_resolve_to_a_private_address_on_any_port()
    {
        var (connector, connects) = Build("10.0.0.5");

        await connector.Trusting(["feeds.internal"]).ConnectAsync("Feeds.internal", 1200, default);

        Assert.Equal([(IPAddress.Parse("10.0.0.5"), 1200)], connects);
    }

    [Fact]
    public async Task Other_hosts_stay_refused_by_a_trusting_connector()
    {
        var (connector, connects) = Build("10.0.0.5");
        var trusting = connector.Trusting(["feeds.internal"]);

        await Assert.ThrowsAsync<UnsafeAddressException>(async () => await trusting.ConnectAsync("other.example", 443, default));
        await Assert.ThrowsAsync<UnsafeAddressException>(async () => await trusting.ConnectAsync("other.example", 1200, default));

        Assert.Empty(connects);
    }

    [Fact]
    public async Task The_default_connector_trusts_nobody()
    {
        var (connector, connects) = Build("10.0.0.5");

        await Assert.ThrowsAsync<UnsafeAddressException>(async () => await connector.ConnectAsync("feeds.internal", 443, default));

        Assert.Empty(connects);
    }
}
