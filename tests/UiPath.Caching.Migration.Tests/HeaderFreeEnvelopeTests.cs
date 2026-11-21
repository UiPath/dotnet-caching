using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

/// <summary>The router's envelope is marked header-free, keeps a memory path it has, and fails a write that would read back as a header.</summary>
public sealed class HeaderFreeEnvelopeTests
{
    // The library's header rule (ValueHeader): 0x01 to 0x1F except tab, line feed and carriage return.
    private static readonly HashSet<int> HeaderBytes = [.. Enumerable.Range(0x01, 0x1F).Except([0x09, 0x0A, 0x0D])];

    [Fact]
    public void The_header_byte_check_matches_the_library_rule_for_every_first_byte()
    {
        for (var b = 0; b < 256; b++)
        {
            HeaderFreeEnvelope.StartsWithHeaderByte([(byte)b, 0x7B]).Should().Be(HeaderBytes.Contains(b), $"byte 0x{b:X2}");
        }

        HeaderFreeEnvelope.StartsWithHeaderByte([]).Should().BeFalse();
    }

    [Fact]
    public void The_envelope_is_wrapped_once_and_reads_and_writes_through()
    {
        var wrapped = HeaderFreeEnvelope.Wrap(new LegacyEnvelopeProxy());

        wrapped.Should().BeAssignableTo<IHeaderFreeSerializerProxy>();
        wrapped.Should().NotBeAssignableTo<IMemorySerializerProxy>("the envelope has no memory path to keep");
        HeaderFreeEnvelope.Wrap(wrapped).Should().BeSameAs(wrapped);
        var bytes = wrapped.Serialize(new List<long> { 1, 2 });
        bytes.Should().Equal(new LegacyEnvelopeProxy().Serialize(new List<long> { 1, 2 }));
        wrapped.Deserialize<List<long>>(bytes).Should().Equal(1, 2);
        wrapped.Serialize(null).Should().BeNull();
    }

    [Fact]
    public void A_memory_path_is_kept_and_checked()
    {
        var wrapped = HeaderFreeEnvelope.Wrap(new MemoryEnvelope([0x7B, 0x7D]));

        var memory = wrapped.Should().BeAssignableTo<IMemorySerializerProxy>().Subject;
        memory.SerializeToMemory("x").ToArray().Should().Equal(0x7B, 0x7D);
        var header = (IMemorySerializerProxy)HeaderFreeEnvelope.Wrap(new MemoryEnvelope([0x10, 0x7B]));
        header.Invoking(p => p.SerializeToMemory("x")).Should().Throw<InvalidOperationException>().WithMessage("*0x10*");
    }

    [Fact]
    public void A_write_that_would_read_back_as_a_header_fails()
    {
        var wrapped = HeaderFreeEnvelope.Wrap(new MemoryEnvelope([0x1F, 0x00]));

        wrapped.Invoking(p => p.Serialize("x")).Should().Throw<InvalidOperationException>().WithMessage("*0x1F*");
    }

    [Fact]
    public void The_router_stack_registers_the_envelope_as_header_free()
    {
        var stack = new ServiceCollection();

        RouterServiceCollectionExtensions.AddEnvelope(stack, new LegacyEnvelopeProxy());

        using var provider = stack.BuildServiceProvider();
        provider.GetRequiredService<ISerializerProxy<byte[]>>().Should().BeOfType<HeaderFreeEnvelope>();
    }

    private sealed class MemoryEnvelope(byte[] bytes) : IMemorySerializerProxy
    {
        public byte[]? Serialize(object? value) => bytes;

        public ReadOnlyMemory<byte> SerializeToMemory<T>(T? value) => bytes;

        public T? Deserialize<T>(byte[]? value) => default;

        public bool TryDeserialize<T>(string? value, out T? result)
        {
            result = default;
            return false;
        }

        public bool TryDeserialize<T>(object? value, out T? result)
        {
            result = default;
            return false;
        }
    }
}
