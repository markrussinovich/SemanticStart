namespace SemanticStart.Core.Storage;

public sealed class VectorFile
{
    private readonly string _path;
    private readonly int _dimensions;
    private readonly int _recordBytes;
    private readonly object _gate = new();

    public VectorFile(string path, int dimensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);

        _path = path;
        _dimensions = dimensions;
        _recordBytes = checked(dimensions * sizeof(float));
    }

    public int RowCount
    {
        get
        {
            lock (_gate)
            {
                if (!File.Exists(_path))
                    return 0;

                return checked((int)(new FileInfo(_path).Length / _recordBytes));
            }
        }
    }

    public void Write(int ordinal, float[] vector)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentNullException.ThrowIfNull(vector);
        if (vector.Length != _dimensions)
            throw new ArgumentException($"Expected {_dimensions} dimensions, got {vector.Length}.", nameof(vector));

        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using var stream = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            var offset = checked((long)ordinal * _recordBytes);
            if (stream.Length < offset)
                stream.SetLength(offset);

            stream.Position = offset;
            var bytes = new byte[_recordBytes];
            Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
            stream.Write(bytes, 0, bytes.Length);
        }
    }

    public float[] ReadAll()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
                return [];

            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var completeBytes = stream.Length - (stream.Length % _recordBytes);
            if (completeBytes == 0)
                return [];

            var bytes = new byte[completeBytes];
            var read = 0;
            while (read < bytes.Length)
            {
                var n = stream.Read(bytes, read, bytes.Length - read);
                if (n == 0)
                    break;
                read += n;
            }

            var values = new float[read / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, values, 0, values.Length * sizeof(float));
            return values;
        }
    }

    public void Truncate()
    {
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using var stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read);
            stream.SetLength(0);
        }
    }
}
