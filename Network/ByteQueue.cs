namespace ServidorCS.Network;

/// <summary>
/// Port 1:1 de clsByteQueue.cls (VB6, Maraxus).
/// Cola FIFO de bytes usada para serializar/deserializar TODOS los packets
/// entre servidor y cliente. El formato de cable DEBE ser idéntico al VB6
/// para que el cliente Godot siga funcionando:
///   - Integer  = 2 bytes little-endian
///   - Long     = 4 bytes little-endian
///   - Single   = 4 bytes IEEE-754 little-endian
///   - Double   = 8 bytes IEEE-754 little-endian
///   - Boolean  = 1 byte (1 = true, 0 = false)
///   - ASCIIString       = Int16 LE (longitud) + N bytes CP1252
///   - ASCIIStringFixed  = N bytes CP1252 (sin prefijo de longitud)
/// </summary>
public sealed class ByteQueue
{
    private const int DATA_BUFFER = 10240; // igual que VB6 (10 KB)

    // CP1252 implementado a mano en Cp1252.cs (sin NuGet). Ver memoria [[vb6_encoding]].

    private byte[] _data;
    private int _capacity;
    private int _length;

    public ByteQueue(int capacity = DATA_BUFFER)
    {
        _capacity = capacity < 1 ? DATA_BUFFER : capacity;
        _data = new byte[_capacity];
        _length = 0;
    }

    /// <summary>Bytes actualmente almacenados (equivale a la prop Length de VB6).</summary>
    public int Length => _length;

    public int Capacity => _capacity;

    // ----------------------------------------------------------------- núcleo

    private void EnsureSpace(int extra)
    {
        // VB6 lanzaba NOT_ENOUGH_SPACE; acá crecemos automáticamente (más robusto,
        // mismo formato de bytes en la salida).
        int needed = _length + extra;
        if (needed <= _capacity) return;
        int newCap = _capacity;
        while (newCap < needed) newCap *= 2;
        Array.Resize(ref _data, newCap);
        _capacity = newCap;
    }

    private void WriteData(byte[] buf, int dataLength)
    {
        EnsureSpace(dataLength);
        Buffer.BlockCopy(buf, 0, _data, _length, dataLength);
        _length += dataLength;
    }

    private void ReadData(byte[] buf, int dataLength)
    {
        if (dataLength > _length)
            throw new NotEnoughDataException();
        Buffer.BlockCopy(_data, 0, buf, 0, dataLength);
    }

    /// <summary>Elimina dataLength bytes del frente (equivale a RemoveData de VB6).</summary>
    public int RemoveData(int dataLength)
    {
        int removed = Math.Min(dataLength, _length);
        if (removed != _capacity)
            Buffer.BlockCopy(_data, removed, _data, 0, _length - removed);
        _length -= removed;
        return removed;
    }

    // --------------------------------------------------------------- escritura

    public void WriteByte(byte value)
    {
        EnsureSpace(1);
        _data[_length++] = value;
    }

    public void WriteInteger(short value)
        => WriteData(BitConverter.GetBytes(value), 2);

    public void WriteLong(int value)
        => WriteData(BitConverter.GetBytes(value), 4);

    public void WriteSingle(float value)
        => WriteData(BitConverter.GetBytes(value), 4);

    public void WriteDouble(double value)
        => WriteData(BitConverter.GetBytes(value), 8);

    public void WriteBoolean(bool value)
        => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteASCIIStringFixed(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        byte[] buf = Cp1252.GetBytes(value);
        WriteData(buf, buf.Length);
    }

    public void WriteASCIIString(string value)
    {
        value ??= string.Empty;
        byte[] str = Cp1252.GetBytes(value);
        short nLen = (short)str.Length;
        EnsureSpace(2 + str.Length);
        WriteData(BitConverter.GetBytes(nLen), 2);
        if (str.Length > 0) WriteData(str, str.Length);
    }

    public void WriteBlock(byte[] value, int length = -1)
    {
        if (length > value.Length || length < 0) length = value.Length;
        WriteData(value, length);
    }

    // ----------------------------------------------------------------- lectura

    public byte ReadByte()
    {
        var buf = new byte[1];
        ReadData(buf, 1);
        RemoveData(1);
        return buf[0];
    }

    public short ReadInteger()
    {
        var buf = new byte[2];
        ReadData(buf, 2); RemoveData(2);
        return BitConverter.ToInt16(buf, 0);
    }

    public int ReadLong()
    {
        var buf = new byte[4];
        ReadData(buf, 4); RemoveData(4);
        return BitConverter.ToInt32(buf, 0);
    }

    public float ReadSingle()
    {
        var buf = new byte[4];
        ReadData(buf, 4); RemoveData(4);
        return BitConverter.ToSingle(buf, 0);
    }

    public double ReadDouble()
    {
        var buf = new byte[8];
        ReadData(buf, 8); RemoveData(8);
        return BitConverter.ToDouble(buf, 0);
    }

    public bool ReadBoolean() => ReadByte() == 1;

    public string ReadASCIIStringFixed(int length)
    {
        if (length <= 0) return string.Empty;
        if (_length < length) throw new NotEnoughDataException();
        var buf = new byte[length];
        ReadData(buf, length); RemoveData(length);
        return Cp1252.GetString(buf);
    }

    public string ReadASCIIString()
    {
        if (_length <= 1) throw new NotEnoughDataException();
        var lenBuf = new byte[2];
        ReadData(lenBuf, 2);
        short length = BitConverter.ToInt16(lenBuf, 0);
        if (_length < (long)length + 2) throw new NotEnoughDataException();
        RemoveData(2);
        if (length <= 0) return string.Empty;
        var buf = new byte[length];
        ReadData(buf, length); RemoveData(length);
        return Cp1252.GetString(buf);
    }

    /// <summary>
    /// Lee un bloque prefijado (Int16 len + len bytes) como bytes CRUDOS, sin decodificar CP1252.
    /// Necesario para datos binarios (ej. password cifrado con shift AO) que se corromperían al
    /// pasar por CP1252 (bytes 0x81/0x8D/0x8F/0x90/0x9D no tienen roundtrip).
    /// </summary>
    public byte[] ReadBlockBytes()
    {
        if (_length <= 1) throw new NotEnoughDataException();
        var lenBuf = new byte[2];
        ReadData(lenBuf, 2);
        short length = BitConverter.ToInt16(lenBuf, 0);
        if (_length < (long)length + 2) throw new NotEnoughDataException();
        RemoveData(2);
        if (length <= 0) return Array.Empty<byte>();
        var buf = new byte[length];
        ReadData(buf, length); RemoveData(length);
        return buf;
    }

    /// <summary>ReadUnicodeString: Int16(len) + len*2 bytes UTF-16LE.</summary>
    public string ReadUnicodeString()
    {
        if (_length <= 1) throw new NotEnoughDataException();
        var lenBuf = new byte[2];
        ReadData(lenBuf, 2);
        short length = BitConverter.ToInt16(lenBuf, 0);
        int bytes = length * 2;
        if (_length < (long)bytes + 2) throw new NotEnoughDataException();
        RemoveData(2);
        if (length <= 0) return string.Empty;
        var buf = new byte[bytes];
        ReadData(buf, bytes); RemoveData(bytes);
        return System.Text.Encoding.Unicode.GetString(buf);
    }

    /// <summary>WriteUnicodeString: Int16(len) + len*2 bytes UTF-16LE.</summary>
    public void WriteUnicodeString(string value)
    {
        value ??= string.Empty;
        var bytes = System.Text.Encoding.Unicode.GetBytes(value);
        WriteData(BitConverter.GetBytes((short)value.Length), 2);
        if (bytes.Length > 0) WriteData(bytes, bytes.Length);
    }

    // --------------------------------------------------------------------- peek

    public byte PeekByte()
    {
        var buf = new byte[1];
        ReadData(buf, 1);
        return buf[0];
    }

    // --------------------------------------------------------------- utilidades

    /// <summary>Vuelca todo el contenido actual a un array nuevo (para enviarlo por socket).</summary>
    public byte[] ToArray()
    {
        var outBuf = new byte[_length];
        Buffer.BlockCopy(_data, 0, outBuf, 0, _length);
        return outBuf;
    }

    /// <summary>Anexa bytes crudos recibidos del socket al final de la cola.</summary>
    public void AppendRaw(byte[] src, int count)
        => WriteData(src, count);

    public void Clear() => _length = 0;
}

/// <summary>Equivale al error NOT_ENOUGH_DATA del VB6: faltan bytes para completar la lectura.</summary>
public sealed class NotEnoughDataException : Exception { }
