using System.Buffers.Binary;

namespace PlatzAtlas.Core;

/// <summary>Detached sprite editing. Colors are packed as RRGGBBAA.</summary>
public sealed class PixelDraft
{
    private const int MaxUndo = 96;
    private readonly List<Frame> _undo = [];
    private readonly Stack<Frame> _redo = new();
    private Frame _frame;
    private Frame? _stroke;

    public PixelDraft(int size, PixelAsset? source = null)
    {
        CheckSize(size);
        _frame = new Frame(size, new uint[size * size]);
        if (source is null)
            return;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var sampleX = Math.Min(source.Width - 1, (2 * x + 1) * source.Width / (2 * size));
            var sampleY = Math.Min(source.Height - 1, (2 * y + 1) * source.Height / (2 * size));
            _frame.Pixels[y * size + x] = BinaryPrimitives.ReadUInt32BigEndian(
                source.Rgba.Span.Slice((sampleY * source.Width + sampleX) * 4, 4));
        }
    }

    public int Size => _frame.Size;
    public bool CanUndo => _undo.Count != 0;
    public bool CanRedo => _redo.Count != 0;
    public bool CanUndoWithoutResize => CanUndo && _undo[^1].Size == Size;
    public bool CanRedoWithoutResize => _redo.TryPeek(out var frame) && frame.Size == Size;
    public uint Read(int x, int y) => _frame.Pixels[Index(x, y)];

    public void BeginStroke()
    {
        if (_stroke is not null)
            throw new InvalidOperationException("Finish the active stroke first.");
        _stroke = _frame with { Pixels = _frame.Pixels.ToArray() };
    }

    public void PaintLine(int x, int y, int toX, int toY, uint color)
    {
        if (_stroke is null)
            throw new InvalidOperationException("Begin a stroke before painting.");
        Index(x, y);
        Index(toX, toY);
        var dx = Math.Abs(toX - x);
        var dy = -Math.Abs(toY - y);
        var stepX = x < toX ? 1 : -1;
        var stepY = y < toY ? 1 : -1;
        var error = dx + dy;
        while (true)
        {
            _frame.Pixels[y * Size + x] = color;
            if (x == toX && y == toY)
                return;
            var doubled = 2 * error;
            if (doubled >= dy) { error += dy; x += stepX; }
            if (doubled <= dx) { error += dx; y += stepY; }
        }
    }

    public void EndStroke()
    {
        if (_stroke is not { } before)
            return;
        _stroke = null;
        if (!_frame.Pixels.AsSpan().SequenceEqual(before.Pixels))
            Record(before);
    }

    public void Fill(int x, int y, uint color)
    {
        EndStroke();
        var start = Index(x, y);
        var old = _frame.Pixels[start];
        if (old == color)
            return;
        var prior = _frame with { Pixels = _frame.Pixels.ToArray() };
        var work = new Stack<int>();
        Add(start);
        while (work.TryPop(out var at))
        {
            if (at % Size > 0) Add(at - 1);
            if (at % Size < Size - 1) Add(at + 1);
            if (at >= Size) Add(at - Size);
            if (at < Size * (Size - 1)) Add(at + Size);
        }
        Record(prior);

        void Add(int index)
        {
            if (_frame.Pixels[index] != old)
                return;
            _frame.Pixels[index] = color;
            work.Push(index);
        }
    }

    public void Resize(int size)
    {
        CheckSize(size);
        EndStroke();
        if (size == Size)
            return;
        var prior = _frame;
        _frame = new PixelDraft(size, new PixelAsset(prior.Size, prior.Size, CopyRgba()))._frame;
        Record(prior);
    }

    public bool Undo()
    {
        EndStroke();
        if (!CanUndo)
            return false;
        _redo.Push(_frame);
        _frame = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        return true;
    }

    public bool Redo()
    {
        EndStroke();
        if (!_redo.TryPop(out var next))
            return false;
        _undo.Add(_frame);
        _frame = next;
        return true;
    }

    public byte[] CopyRgba()
    {
        var rgba = new byte[_frame.Pixels.Length * 4];
        for (var index = 0; index < _frame.Pixels.Length; index++)
            BinaryPrimitives.WriteUInt32BigEndian(rgba.AsSpan(index * 4, 4), _frame.Pixels[index]);
        return rgba;
    }

    public void CopyBgraTo(Span<byte> pixels)
    {
        if (pixels.Length != _frame.Pixels.Length * 4)
            throw new ArgumentException("The buffer must match the canvas.", nameof(pixels));
        for (var index = 0; index < _frame.Pixels.Length; index++)
        {
            var color = _frame.Pixels[index];
            BinaryPrimitives.WriteUInt32LittleEndian(pixels.Slice(index * 4, 4),
                (color << 24) | (color >> 8));
        }
    }

    private void Record(Frame frame)
    {
        _undo.Add(frame);
        if (_undo.Count > MaxUndo)
            _undo.RemoveAt(0);
        _redo.Clear();
    }

    private int Index(int x, int y)
    {
        if (x < 0 || x >= Size || y < 0 || y >= Size)
            throw new ArgumentOutOfRangeException(nameof(x), "The pixel lies outside the canvas.");
        return y * Size + x;
    }

    private static void CheckSize(int size)
    {
        if (size is not (16 or 32 or 64 or 128 or 256))
            throw new ArgumentOutOfRangeException(nameof(size), "Choose a canvas of 16, 32, 64, 128 or 256 pixels.");
    }

    private sealed record Frame(int Size, uint[] Pixels);
}
