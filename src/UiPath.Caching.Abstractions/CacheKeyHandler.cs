using System.Buffers;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace UiPath.Caching;

/// <summary>Builds a cache key from an interpolated string without allocating for a short key; the text sits on the stack, and a long key borrows a pooled array.</summary>
/// <remarks>Formats with <see cref="CultureInfo.CurrentCulture"/>, as string interpolation does, so a key reads the same as the string it replaces. The compiler builds a handler value for one call: do not store it or pass it twice. Pass <c>$"..."</c> where a key is taken.</remarks>
[InterpolatedStringHandler]
public ref struct CacheKeyHandler
{
    private const int StackLength = 256;

    private Buffer _stack;
    private char[]? _rented;
    private int _length;

    /// <summary>Called by the compiler.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public CacheKeyHandler(int literalLength, int formattedCount)
    {
        _stack = default;
        _rented = null;
        _length = 0;
        var estimate = literalLength + (formattedCount * 16);
        if (estimate > StackLength)
        {
            _rented = ArrayPool<char>.Shared.Rent(estimate);
        }
    }

    /// <summary>The text written so far; valid until the forwarding call returns.</summary>
    internal Span<char> Written
    {
        [UnscopedRef]
        get => Buffered[.._length];
    }

    private Span<char> Buffered
    {
        [UnscopedRef]
        get => _rented ?? (Span<char>)_stack;
    }

    private Span<char> Free
    {
        [UnscopedRef]
        get => Buffered[_length..];
    }

    /// <summary>Called by the compiler.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void AppendLiteral(string value) => AppendFormatted(value.AsSpan());

    /// <summary>Called by the compiler.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void AppendFormatted<TValue>(TValue value) => AppendFormatted(value, null);

    /// <summary>Called by the compiler.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [SuppressMessage("Major Code Smell", "S3247:Duplicate casts should not be made", Justification = "A pattern variable boxes a value-type TValue (32 bytes for a Guid); the is-then-cast pair compiles to a constrained call with no allocation.")]
    [MethodImpl(MethodImplOptions.AggressiveOptimization)] // skips tier 0, which boxes a value-type TValue on the type tests
    public void AppendFormatted<TValue>(TValue value, string? format)
    {
        string? text;
        if (value is IFormattable)
        {
            if (value is ISpanFormattable && ((ISpanFormattable)value).TryFormat(Free, out var written, format, CultureInfo.CurrentCulture))
            {
                _length += written;
                return;
            }

            text = ((IFormattable)value).ToString(format, CultureInfo.CurrentCulture);
        }
        else
        {
            text = value?.ToString();
        }

        AppendFormatted(text);
    }

    /// <summary>Called by the compiler.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void AppendFormatted(ReadOnlySpan<char> value)
    {
        if (value.Length > Free.Length)
        {
            Grow(value.Length);
        }

        value.CopyTo(Free);
        _length += value.Length;
    }

    /// <summary>Called by the compiler.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void AppendFormatted(string? value) => AppendFormatted(value.AsSpan());

    /// <summary>Called by the compiler.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void AppendFormatted(string? value, int alignment) => AppendFormatted(value, alignment, null);

    /// <summary>Called by the compiler.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void AppendFormatted(string? value, int alignment, string? format)
    {
        var start = _length;
        AppendFormatted(value.AsSpan());
        Pad(start, alignment);
    }

    /// <summary>Called by the compiler.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void AppendFormatted(ReadOnlySpan<char> value, int alignment) => AppendFormatted(value, alignment, null);

    /// <summary>Called by the compiler.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void AppendFormatted(ReadOnlySpan<char> value, int alignment, string? format)
    {
        var start = _length;
        AppendFormatted(value);
        Pad(start, alignment);
    }

    /// <summary>Called by the compiler.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void AppendFormatted(object? value, int alignment = 0, string? format = null) => AppendFormatted<object?>(value, alignment, format);

    /// <summary>Called by the compiler.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void AppendFormatted<TValue>(TValue value, int alignment) => AppendFormatted(value, alignment, null);

    /// <summary>Called by the compiler.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void AppendFormatted<TValue>(TValue value, int alignment, string? format)
    {
        var start = _length;
        AppendFormatted(value, format);
        Pad(start, alignment);
    }

    /// <summary>The key as a <see cref="CacheKey"/>, for a cache with no span capability. Returns the pooled array.</summary>
    internal CacheKey ToCacheKey()
    {
        var key = new CacheKey(Written);
        Return();
        return key;
    }

    /// <summary>Gives back the pooled array, if one was borrowed.</summary>
    internal void Return()
    {
        var rented = _rented;
        _rented = null;
        if (rented is not null)
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    private void Pad(int start, int alignment)
    {
        var padding = Math.Abs(alignment) - (_length - start);
        if (padding <= 0)
        {
            return;
        }

        if (padding > Free.Length)
        {
            Grow(padding);
        }

        var buffer = Buffered;
        if (alignment > 0)
        {
            buffer[start.._length].CopyTo(buffer[(start + padding)..]);
            buffer.Slice(start, padding).Fill(' ');
        }
        else
        {
            buffer.Slice(_length, padding).Fill(' ');
        }

        _length += padding;
    }

    private void Grow(int additional)
    {
        var bigger = ArrayPool<char>.Shared.Rent(Math.Max(_length + additional, (_rented?.Length ?? StackLength) * 2));
        Written.CopyTo(bigger);
        Return();
        _rented = bigger;
    }

    [InlineArray(StackLength)]
    private struct Buffer
    {
        [SuppressMessage("Major Code Smell", "S1144:Unused private types or members should be removed", Justification = "The element field the runtime requires on an [InlineArray] type.")]
        private char _element0;
    }
}
