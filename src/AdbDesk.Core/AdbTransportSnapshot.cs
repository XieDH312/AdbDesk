using System.Text;

namespace AdbDesk.Core;

// AOSP proto/adb_host.proto. Decode only fields needed for identification, without
// introducing a protobuf dependency; unknown fields remain forward compatible.
internal sealed class AdbTransportSnapshot
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly IReadOnlyList<Entry> _entries;
    internal string Error { get; }

    private AdbTransportSnapshot(IReadOnlyList<Entry> entries, string error = "") => (_entries, Error) = (entries, error);
    internal static AdbTransportSnapshot Unavailable(string error) => new([], error);

    internal DeviceTransport Classify(DeviceInfo device)
    {
        if (device.TransportId is not > 0) return DeviceTransport.Unknown;
        var matches = _entries.Where(entry => entry.Serial == device.Serial && entry.TransportId == device.TransportId).ToArray();
        if (matches.Length != 1 || matches[0].State != device.State) return DeviceTransport.Unknown;
        return matches[0].Transport;
    }

    internal static AdbTransportSnapshot Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > ushort.MaxValue) throw new InvalidDataException("ADB 连接列表超过协议长度限制。");
        var entries = new List<Entry>();
        var reader = new WireReader(bytes);
        while (!reader.End)
        {
            var (field, wire) = reader.Tag();
            if (field == 1)
            {
                if (wire != 2) throw new InvalidDataException("ADB 设备消息类型无效。");
                entries.Add(ParseDevice(reader.Bytes()));
            }
            else reader.Skip(wire);
        }
        return new(entries);
    }

    private static Entry ParseDevice(ReadOnlySpan<byte> bytes)
    {
        var reader = new WireReader(bytes);
        var seen = new HashSet<int>();
        var serial = "";
        var state = "";
        var transport = DeviceTransport.Unknown;
        long id = 0;
        while (!reader.End)
        {
            var (field, wire) = reader.Tag();
            if (field is not (1 or 2 or 7 or 10)) { reader.Skip(wire); continue; }
            if (!seen.Add(field)) throw new InvalidDataException("ADB 设备消息包含重复标识字段。");
            if (field == 1)
            {
                if (wire != 2) throw new InvalidDataException("ADB 序列号字段类型无效。");
                try { serial = Utf8.GetString(reader.Bytes()); }
                catch (DecoderFallbackException ex) { throw new InvalidDataException("ADB 序列号不是有效 UTF-8。", ex); }
                if (serial.Length > 1024 || serial.Any(char.IsControl)) throw new InvalidDataException("ADB 序列号无效。");
                continue;
            }
            if (wire != 0) throw new InvalidDataException("ADB 连接信息字段类型无效。");
            var value = reader.Varint();
            switch (field)
            {
                case 2:
                    state = value switch
                    {
                        1 => "connecting", 2 => "authorizing", 3 => "unauthorized", 4 => "no permissions",
                        5 => "detached", 6 => "offline", 7 => "bootloader", 8 => "device", 9 => "host",
                        10 => "recovery", 11 => "sideload", 12 => "rescue", _ => ""
                    };
                    break;
                case 7: transport = value switch { 1 => DeviceTransport.Usb, 2 => DeviceTransport.Network, _ => DeviceTransport.Unknown }; break;
                case 10:
                    if (value > long.MaxValue) throw new InvalidDataException("ADB 连接编号超出范围。");
                    id = (long)value;
                    break;
            }
        }
        return new(serial, id, state, transport);
    }

    private sealed record Entry(string Serial, long TransportId, string State, DeviceTransport Transport);

    private ref struct WireReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;
        internal bool End => _position == _data.Length;

        internal (int Field, int Wire) Tag()
        {
            var value = Varint();
            if (value >> 3 is 0 or > 536870911) throw new InvalidDataException("ADB protobuf 字段编号无效。");
            return ((int)(value >> 3), (int)(value & 7));
        }

        internal ulong Varint()
        {
            ulong value = 0;
            for (var shift = 0; shift < 70; shift += 7)
            {
                if (_position >= _data.Length) throw new InvalidDataException("ADB protobuf 整数被截断。");
                var next = _data[_position++];
                if (shift == 63 && next > 1) throw new InvalidDataException("ADB protobuf 整数溢出。");
                value |= (ulong)(next & 127) << shift;
                if ((next & 128) == 0) return value;
            }
            throw new InvalidDataException("ADB protobuf 整数无效。");
        }

        internal ReadOnlySpan<byte> Bytes()
        {
            var length = Varint();
            if (length > (ulong)(_data.Length - _position)) throw new InvalidDataException("ADB protobuf 消息被截断。");
            var value = _data.Slice(_position, (int)length);
            _position += (int)length;
            return value;
        }

        internal void Skip(int wire)
        {
            switch (wire)
            {
                case 0: Varint(); return;
                case 2: Bytes(); return;
                case 1: Advance(8); return;
                case 5: Advance(4); return;
                default: throw new InvalidDataException("ADB protobuf 编码类型无效。");
            }
        }

        private void Advance(int count)
        {
            if (count > _data.Length - _position) throw new InvalidDataException("ADB protobuf 字段被截断。");
            _position += count;
        }
    }
}
