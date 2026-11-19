using System.Buffers.Binary;
using System.Net.Sockets;

namespace FrankEnPoller;

public sealed class ModbusException(byte code)
    : Exception($"Modbus exception {code} ({Describe(code)})")
{
    public byte Code { get; } = code;

    private static string Describe(byte code) => code switch
    {
        1 => "illegal function",
        2 => "illegal data address",
        3 => "illegal data value",
        4 => "device failure",
        6 => "device busy",
        _ => "unknown"
    };
}

/// <summary>
/// Minimal Modbus TCP client: function 3 (read holding registers) only, which is all a
/// read-only poller needs. Calls are serialized; the connection is reused while fresh
/// and reopened after idling, because the aGate drops connections idle for ~10 s.
/// </summary>
public sealed class ModbusTcpClient(string host, int port, byte unitId) : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(8);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private DateTime _lastUse;
    private ushort _transactionId;

    public async Task<ushort[]> ReadHoldingRegistersAsync(int address, int count, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 125);

        await _lock.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(RequestTimeout);
                try
                {
                    var stream = await ConnectAsync(timeout.Token);
                    return await ExchangeAsync(stream, address, count, timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Close();
                    throw new TimeoutException($"No Modbus response from {host}:{port} within {RequestTimeout.TotalSeconds:F0} s.");
                }
                catch (Exception ex) when (attempt == 0 && ex is IOException or SocketException)
                {
                    Close(); // stale connection: reconnect and retry once
                }
                catch (Exception ex) when (ex is IOException or SocketException)
                {
                    Close();
                    throw;
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<NetworkStream> ConnectAsync(CancellationToken ct)
    {
        if (_stream != null && DateTime.UtcNow - _lastUse < IdleLimit)
            return _stream;

        Close();
        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync(host, port, ct);
        _lastUse = DateTime.UtcNow;
        return _stream = _tcp.GetStream();
    }

    private async Task<ushort[]> ExchangeAsync(NetworkStream stream, int address, int count, CancellationToken ct)
    {
        var tx = ++_transactionId;
        var request = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0), tx);
        // bytes 2-3: protocol id 0
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), 6); // bytes that follow
        request[6] = unitId;
        request[7] = 3; // read holding registers
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8), (ushort)address);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(10), (ushort)count);
        await stream.WriteAsync(request, ct);

        var header = new byte[7];
        await stream.ReadExactlyAsync(header, ct);
        if (BinaryPrimitives.ReadUInt16BigEndian(header) != tx)
            throw new IOException("Modbus transaction id mismatch.");
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4)); // includes unit id byte
        if (length < 3 || length > 260)
            throw new IOException($"Bad Modbus frame length {length}.");

        var body = new byte[length - 1];
        await stream.ReadExactlyAsync(body, ct);
        _lastUse = DateTime.UtcNow;

        if (body[0] == 0x83)
            throw new ModbusException(body[1]);
        if (body[0] != 3 || body[1] != count * 2 || body.Length < 2 + count * 2)
            throw new IOException("Unexpected Modbus response.");

        var registers = new ushort[count];
        for (var i = 0; i < count; i++)
            registers[i] = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(2 + i * 2));
        return registers;
    }

    private void Close()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
    }

    public void Dispose() => Close();
}
