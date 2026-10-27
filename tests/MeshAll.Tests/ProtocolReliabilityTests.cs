using System.Text;
using MeshAll.Server.Services;
using Xunit;

namespace MeshAll.Tests;

public sealed class ProtocolReliabilityTests
{
    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
    }

    [Theory]
    [InlineData("échec")]
    [InlineData("客户端")]
    [InlineData("A💻é")]
    public async Task GuacamoleHandshakeUsesUnicodeCharactersRatherThanUtf8Bytes(string value)
    {
        using MemoryStream output = new();
        await ProtocolWorker.WriteAsync(output, ["ready", value], CancellationToken.None);
        byte[] tail = Encoding.ASCII.GetBytes("4.sync,1.1;");
        using FragmentedStream input = new([.. output.ToArray(), .. tail]);
        Assert.Equal(new[] { "ready", value }, await ProtocolWorker.ReadAsync(input, CancellationToken.None));
        byte[] remaining = new byte[tail.Length];
        await input.ReadExactlyAsync(remaining);
        Assert.Equal(tail, remaining); // Do not consume the next graphical instruction.
    }

    [Theory]
    [InlineData(".;")]
    [InlineData("1.\u00e9,")]
    public async Task MalformedOrTruncatedHandshakeFailsWithAControlledIoError(string wire)
    {
        using FragmentedStream input = new(Encoding.UTF8.GetBytes(wire));
        await Assert.ThrowsAnyAsync<IOException>(() => ProtocolWorker.ReadAsync(input, CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"type\":\"resize\"}")]
    [InlineData("{\"type\":\"resize\",\"columns\":null,\"rows\":24}")]
    [InlineData("{\"type\":\"resize\",\"columns\":\"wide\",\"rows\":24}")]
    [InlineData("{\"type\":\"resize\",\"columns\":80,\"rows\":1.5}")]
    public void MalformedReservedResizeControlsCannotThrowUnexpectedExceptions(string json) =>
        Assert.Throws<IOException>(() => NetworkGateway.TryReadTerminalResize(Encoding.UTF8.GetBytes(json), out _, out _));

    [Theory]
    [InlineData("{\"type\":12}")]
    [InlineData("{\"type\":\"ordinary-input\"}")]
    [InlineData("{not-json")]
    public void NonControlTerminalInputRemainsUnchanged(string input) =>
        Assert.False(NetworkGateway.TryReadTerminalResize(Encoding.UTF8.GetBytes(input), out _, out _));

    [Fact]
    public void TerminalResizeDimensionsAreBoundedBeforeTheyReachTheAgent()
    {
        Assert.True(NetworkGateway.TryReadTerminalResize(Encoding.UTF8.GetBytes("{\"type\":\"resize\",\"columns\":2147483647,\"rows\":-100}"), out int columns, out int rows));
        Assert.Equal(300, columns);
        Assert.Equal(5, rows);
    }

    [Theory]
    [InlineData(new byte[] { (byte)'1', (byte)'.', 0xC0, 0xAF, (byte)';' })]
    [InlineData(new byte[] { (byte)'1', (byte)'.', 0xED, 0xA0, 0x80, (byte)';' })]
    public async Task InvalidUtf8CannotBeAcceptedAsAProtocolCharacter(byte[] bytes)
    {
        using FragmentedStream input = new(bytes);
        await Assert.ThrowsAnyAsync<IOException>(() => ProtocolWorker.ReadAsync(input, CancellationToken.None));
    }
}
