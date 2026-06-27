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
///
/// [[b1_bytequeue_cursor]] Implementación interna con CURSOR (_start/_length) sobre un único
/// buffer, en vez de "leer a un array temporal + desplazar TODO el resto" en cada campo (versión
/// pre-B1). Cada ReadXxx ahora:
///   - Chequea que haya bytes suficientes ANTES de tocar nada (si falta algo, lanza
///     NotEnoughDataException sin mutar _start/_length — invariante preservado 1:1 respecto a la
///     versión anterior, verificado con tests de fuzz comparando ambas implementaciones).
///   - Lee directo del buffer interno (BitConverter.ToXxx(_data, _start) / Encoding.GetString con
///     offset) sin alocar un array temporal por campo.
///   - Avanza el cursor (_start += N; _length -= N) en vez de mover en memoria los bytes que
///     quedan atrás.
/// La compactación (mover los bytes válidos a partir de 0) sólo ocurre en EnsureSpace, y sólo
/// cuando hace falta lugar para escribir — no en cada lectura. El formato de bytes en el cable
/// (lo que ESCRIBE al array subyacente y lo que DEVUELVEN los Read) es exactamente el mismo que
/// antes: no cambia ni un bit del protocolo, sólo cómo se mueve la memoria internamente.
/// </summary>
public sealed class ByteQueue
{
    private const int DATA_BUFFER = 10240; // igual que VB6 (10 KB)

    // CP1252 implementado a mano en Cp1252.cs (sin NuGet). Ver memoria [[vb6_encoding]].

    private byte[] _data;
    private int _capacity;
    private int _start;   // offset del primer byte válido dentro de _data
    private int _length;  // cantidad de bytes válidos a partir de _start

    public ByteQueue(int capacity = DATA_BUFFER)
    {
        _capacity = capacity < 1 ? DATA_BUFFER : capacity;
        _data = new byte[_capacity];
        _start = 0;
        _length = 0;
    }

    /// <summary>Bytes actualmente almacenados (equivale a la prop Length de VB6).</summary>
    public int Length => _length;

    public int Capacity => _capacity;

    // ----------------------------------------------------------------- núcleo

    private void EnsureSpace(int extra)
    {
        // ¿Ya entra al final del buffer tal cual está? Caso común: no hace falta tocar nada.
        if (_start + _length + extra <= _capacity) return;

        // Compactar primero: mover los bytes válidos al principio libera el espacio ya leído
        // (_start) sin alocar memoria nueva. Es la ÚNICA forma de "shift" que sobrevive de la
        // versión anterior, y sólo se paga cuando una escritura realmente lo necesita — no en
        // cada Read (que es el patrón que se estaba pagando antes, por cada campo).
        if (_start > 0)
        {
            if (_length > 0) Buffer.BlockCopy(_data, _start, _data, 0, _length);
            _start = 0;
        }
        if (_length + extra <= _capacity) return;

        // Ni compactando entra: crecer el buffer (mismo criterio de duplicar que antes).
        int newCap = _capacity;
        while (newCap < _length + extra) newCap *= 2;
        var newData = new byte[newCap];
        if (_length > 0) Buffer.BlockCopy(_data, _start, newData, 0, _length);
        _data = newData;
        _capacity = newCap;
        _start = 0;
    }

    private void WriteData(byte[] buf, int dataLength)
    {
        EnsureSpace(dataLength);
        Buffer.BlockCopy(buf, 0, _data, _start + _length, dataLength);
        _length += dataLength;
    }

    /// <summary>Elimina dataLength bytes del frente (equivale a RemoveData de VB6). Ya no mueve
    /// memoria: solo avanza el cursor. Sigue pública por compatibilidad de API, aunque hoy no la
    /// llama nada fuera de esta clase (los ReadXxx la reemplazaron por el avance de cursor inline).</summary>
    public int RemoveData(int dataLength)
    {
        int removed = Math.Min(dataLength, _length);
        _start += removed;
        _length -= removed;
        return removed;
    }

    // --------------------------------------------------------------- escritura

    public void WriteByte(byte value)
    {
        EnsureSpace(1);
        _data[_start + _length] = value;
        _length++;
    }

    public void WriteInteger(short value)
    {
        EnsureSpace(2);
        BitConverter.TryWriteBytes(_data.AsSpan(_start + _length, 2), value);
        _length += 2;
    }

    public void WriteLong(int value)
    {
        EnsureSpace(4);
        BitConverter.TryWriteBytes(_data.AsSpan(_start + _length, 4), value);
        _length += 4;
    }

    public void WriteSingle(float value)
    {
        EnsureSpace(4);
        BitConverter.TryWriteBytes(_data.AsSpan(_start + _length, 4), value);
        _length += 4;
    }

    public void WriteDouble(double value)
    {
        EnsureSpace(8);
        BitConverter.TryWriteBytes(_data.AsSpan(_start + _length, 8), value);
        _length += 8;
    }

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
        WriteInteger(nLen);
        if (str.Length > 0) WriteData(str, str.Length);
    }

    public void WriteBlock(byte[] value, int length = -1)
    {
        if (length > value.Length || length < 0) length = value.Length;
        WriteData(value, length);
    }

    // ----------------------------------------------------------------- lectura
    //
    // Cada Read valida PRIMERO que haya bytes suficientes (si no, NotEnoughDataException sin
    // mutar _start/_length — mismo comportamiento que la versión pre-B1) y recién después lee
    // directo del buffer y avanza el cursor. Cero allocations, cero BlockCopy por campo.

    public byte ReadByte()
    {
        if (_length < 1) throw new NotEnoughDataException();
        byte v = _data[_start];
        _start += 1; _length -= 1;
        return v;
    }

    public short ReadInteger()
    {
        if (_length < 2) throw new NotEnoughDataException();
        short v = BitConverter.ToInt16(_data, _start);
        _start += 2; _length -= 2;
        return v;
    }

    public int ReadLong()
    {
        if (_length < 4) throw new NotEnoughDataException();
        int v = BitConverter.ToInt32(_data, _start);
        _start += 4; _length -= 4;
        return v;
    }

    public float ReadSingle()
    {
        if (_length < 4) throw new NotEnoughDataException();
        float v = BitConverter.ToSingle(_data, _start);
        _start += 4; _length -= 4;
        return v;
    }

    public double ReadDouble()
    {
        if (_length < 8) throw new NotEnoughDataException();
        double v = BitConverter.ToDouble(_data, _start);
        _start += 8; _length -= 8;
        return v;
    }

    public bool ReadBoolean() => ReadByte() == 1;

    public string ReadASCIIStringFixed(int length)
    {
        if (length <= 0) return string.Empty;
        if (_length < length) throw new NotEnoughDataException();
        string s = Cp1252.GetString(_data, _start, length);
        _start += length; _length -= length;
        return s;
    }

    public string ReadASCIIString()
    {
        if (_length <= 1) throw new NotEnoughDataException();
        short length = BitConverter.ToInt16(_data, _start); // peek del prefijo: no avanza todavía
        if (_length < (long)length + 2) throw new NotEnoughDataException();
        _start += 2; _length -= 2; // recién ahora se consume el prefijo (equivale al RemoveData(2) de antes)
        if (length <= 0) return string.Empty;
        string s = Cp1252.GetString(_data, _start, length);
        _start += length; _length -= length;
        return s;
    }

    /// <summary>
    /// Lee un bloque prefijado (Int16 len + len bytes) como bytes CRUDOS, sin decodificar CP1252.
    /// Necesario para datos binarios (ej. password cifrado con shift AO) que se corromperían al
    /// pasar por CP1252 (bytes 0x81/0x8D/0x8F/0x90/0x9D no tienen roundtrip).
    /// </summary>
    public byte[] ReadBlockBytes()
    {
        if (_length <= 1) throw new NotEnoughDataException();
        short length = BitConverter.ToInt16(_data, _start);
        if (_length < (long)length + 2) throw new NotEnoughDataException();
        _start += 2; _length -= 2;
        if (length <= 0) return Array.Empty<byte>();
        var buf = new byte[length]; // alloc real: es el valor de retorno, no un temporal descartable
        Buffer.BlockCopy(_data, _start, buf, 0, length);
        _start += length; _length -= length;
        return buf;
    }

    /// <summary>ReadUnicodeString: Int16(len) + len*2 bytes UTF-16LE.</summary>
    public string ReadUnicodeString()
    {
        if (_length <= 1) throw new NotEnoughDataException();
        short length = BitConverter.ToInt16(_data, _start);
        int bytes = length * 2;
        if (_length < (long)bytes + 2) throw new NotEnoughDataException();
        _start += 2; _length -= 2;
        if (length <= 0) return string.Empty;
        string s = System.Text.Encoding.Unicode.GetString(_data, _start, bytes);
        _start += bytes; _length -= bytes;
        return s;
    }

    /// <summary>WriteUnicodeString: Int16(len) + len*2 bytes UTF-16LE.</summary>
    public void WriteUnicodeString(string value)
    {
        value ??= string.Empty;
        var bytes = System.Text.Encoding.Unicode.GetBytes(value);
        EnsureSpace(2 + bytes.Length);
        WriteInteger((short)value.Length);
        if (bytes.Length > 0) WriteData(bytes, bytes.Length);
    }

    // --------------------------------------------------------------------- peek

    public byte PeekByte()
    {
        if (_length < 1) throw new NotEnoughDataException();
        return _data[_start];
    }

    // --------------------------------------------------------------- utilidades

    /// <summary>Vuelca todo el contenido actual a un array nuevo (para enviarlo por socket).</summary>
    public byte[] ToArray()
    {
        var outBuf = new byte[_length];
        if (_length > 0) Buffer.BlockCopy(_data, _start, outBuf, 0, _length);
        return outBuf;
    }

    /// <summary>Anexa bytes crudos recibidos del socket al final de la cola.</summary>
    public void AppendRaw(byte[] src, int count)
        => WriteData(src, count);

    public void Clear() { _start = 0; _length = 0; }
}

/// <summary>Equivale al error NOT_ENOUGH_DATA del VB6: faltan bytes para completar la lectura.</summary>
public sealed class NotEnoughDataException : Exception { }
