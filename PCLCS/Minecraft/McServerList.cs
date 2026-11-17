using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;

namespace PCLCS;

public static class McServerList {

    public static string ValidateConfiguration(string configuration) {
        try {
            ParseConfiguration(configuration);
            return "";
        } catch (FormatException exception) {
            return exception.Message;
        }
    }

    private static List<KeyValuePair<string, string>> ParseConfiguration(string configuration) {
        var servers = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrWhiteSpace(configuration)) return servers;
        if (configuration.Length > 10000) throw new FormatException("服务器列表不能超过 10000 个字符！");
        if (configuration.Any(char.IsControl)) throw new FormatException("服务器列表不能包含换行或控制字符！");
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in configuration.Split(',')) {
            int separator = entry.IndexOf(':');
            if (separator <= 0) throw new FormatException("服务器列表格式应为 名称:地址,名称:地址！");
            string title = entry.Substring(0, separator).Trim();
            string address = entry.Substring(separator + 1).Trim();
            if (title.Length == 0 || title.Any(char.IsControl) || address.Any(char.IsWhiteSpace) || address.Any(char.IsControl))
                throw new FormatException("服务器名称不能为空，名称和地址不能包含换行，地址不能包含空格！");
            if (title.Length > 200) throw new FormatException("服务器名称不能超过 200 个字符！");
            string host = address;
            string port = "";
            if (address.StartsWith("[", StringComparison.Ordinal)) {
                int closingBracket = address.IndexOf(']');
                if (closingBracket < 0 || !IPAddress.TryParse(address.Substring(1, closingBracket - 1), out var ipv6) ||
                    ipv6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
                    throw new FormatException("IPv6 地址应使用 [IPv6] 或 [IPv6]:端口 格式！");
                host = address.Substring(0, closingBracket + 1);
                if (closingBracket + 1 < address.Length) {
                    if (address[closingBracket + 1] != ':') throw new FormatException("IPv6 地址的端口格式不正确！");
                    port = address.Substring(closingBracket + 2);
                    if (port.Length == 0) throw new FormatException("服务器端口不能为空！");
                }
            } else {
                int portSeparator = address.IndexOf(':');
                if (portSeparator >= 0) {
                    host = address.Substring(0, portSeparator);
                    port = address.Substring(portSeparator + 1);
                    if (port.Length == 0) throw new FormatException("服务器端口不能为空！");
                }
                var hostType = Uri.CheckHostName(host);
                if (hostType != UriHostNameType.Dns && hostType != UriHostNameType.IPv4)
                    throw new FormatException("服务器地址应为域名、IPv4 或带方括号的 IPv6 地址！");
            }
            if (port.Length > 0 && (port.Any(character => character < '0' || character > '9') ||
                !int.TryParse(port, out int portNumber) || portNumber < 1 || portNumber > 65535))
                throw new FormatException("服务器端口必须为 1 至 65535 的整数！");
            if (addresses.Add(NormalizeAddress(address))) servers.Add(new KeyValuePair<string, string>(title, address));
        }
        return servers;
    }

    private static string NormalizeAddress(string address) {
        address = address.Trim();
        return address.EndsWith(":25565", StringComparison.Ordinal) ? address.Substring(0, address.Length - 6) : address;
    }

    public static int Inject(string gameDirectory, string configuration) {
        var servers = ParseConfiguration(configuration);
        if (servers.Count == 0) return 0;
        string path = Path.Combine(gameDirectory, "servers.dat");
        bool exists = File.Exists(path);
        byte[] original = exists ? File.ReadAllBytes(path) : new byte[] { 10, 0, 0, 0 };
        bool compressed = original.Length >= 2 && original[0] == 0x1F && original[1] == 0x8B;
        byte[] data = compressed ? Decompress(original) : original;
        var reader = new NbtReader(data);
        if (reader.ReadByte() != 10) throw new InvalidDataException("servers.dat 的根标签不是复合标签！");
        reader.ReadString();
        int countPosition = -1;
        int listEnd = -1;
        int existingCount = 0;
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        byte tagType;
        while ((tagType = reader.ReadByte()) != 0) {
            string tagName = reader.ReadString();
            if (tagName != "servers") {
                reader.SkipPayload(tagType);
                continue;
            }
            if (tagType != 9 || countPosition >= 0) throw new InvalidDataException("servers.dat 的服务器列表标签不正确！");
            byte elementType = reader.ReadByte();
            countPosition = reader.Position;
            existingCount = reader.ReadCount();
            if (elementType != 10 && !(elementType == 0 && existingCount == 0))
                throw new InvalidDataException("servers.dat 的服务器列表不是复合标签列表！");
            for (int serverIndex = 0; serverIndex < existingCount; serverIndex++) {
                byte fieldType;
                while ((fieldType = reader.ReadByte()) != 0) {
                    string fieldName = reader.ReadString();
                    if (fieldType == 8 && fieldName == "ip") addresses.Add(NormalizeAddress(reader.ReadString()));
                    else reader.SkipPayload(fieldType);
                }
            }
            listEnd = reader.Position;
        }
        if (reader.Position != data.Length) throw new InvalidDataException("servers.dat 包含多余的数据！");
        var additions = servers.Where(server => !addresses.Contains(NormalizeAddress(server.Value))).ToList();
        if (additions.Count == 0) return 0;
        using var output = new MemoryStream();
        if (countPosition >= 0) {
            output.Write(data, 0, countPosition - 1);
            output.WriteByte(10);
            WriteInt(output, checked(existingCount + additions.Count));
            output.Write(data, countPosition + 4, listEnd - countPosition - 4);
        } else {
            output.Write(data, 0, data.Length - 1);
            output.WriteByte(9);
            WriteString(output, "servers");
            output.WriteByte(10);
            WriteInt(output, additions.Count);
            listEnd = data.Length - 1;
        }
        foreach (var server in additions) {
            output.WriteByte(8);
            WriteString(output, "name");
            WriteString(output, server.Key);
            output.WriteByte(8);
            WriteString(output, "ip");
            WriteString(output, server.Value);
            output.WriteByte(0);
        }
        output.Write(data, listEnd, data.Length - listEnd);
        byte[] updated = output.ToArray();
        if (compressed) {
            using var compressedOutput = new MemoryStream();
            using (var gzip = new GZipStream(compressedOutput, CompressionMode.Compress, true)) gzip.Write(updated, 0, updated.Length);
            updated = compressedOutput.ToArray();
        }
        Directory.CreateDirectory(gameDirectory);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                file.Write(updated, 0, updated.Length);
                file.Flush(true);
            }
            if (exists) File.Replace(temporaryPath, path, path + ".pcl.bak");
            else File.Move(temporaryPath, path);
        } finally {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
        return additions.Count;
    }

    private static byte[] Decompress(byte[] data) {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = gzip.Read(buffer, 0, buffer.Length)) > 0) {
            if (output.Length + bytesRead > 64 * 1024 * 1024) throw new InvalidDataException("servers.dat 解压后过大！");
            output.Write(buffer, 0, bytesRead);
        }
        return output.ToArray();
    }

    private static void WriteInt(Stream output, int value) {
        output.WriteByte((byte)(value >> 24));
        output.WriteByte((byte)(value >> 16));
        output.WriteByte((byte)(value >> 8));
        output.WriteByte((byte)value);
    }

    private static void WriteString(Stream output, string value) {
        using var encoded = new MemoryStream();
        foreach (char character in value) {
            if (character >= 1 && character <= 0x7F) encoded.WriteByte((byte)character);
            else if (character <= 0x7FF) {
                encoded.WriteByte((byte)(0xC0 | (character >> 6)));
                encoded.WriteByte((byte)(0x80 | (character & 0x3F)));
            } else {
                encoded.WriteByte((byte)(0xE0 | (character >> 12)));
                encoded.WriteByte((byte)(0x80 | ((character >> 6) & 0x3F)));
                encoded.WriteByte((byte)(0x80 | (character & 0x3F)));
            }
        }
        if (encoded.Length > ushort.MaxValue) throw new FormatException("服务器名称或地址过长！");
        output.WriteByte((byte)(encoded.Length >> 8));
        output.WriteByte((byte)encoded.Length);
        encoded.Position = 0;
        encoded.CopyTo(output);
    }

    private sealed class NbtReader(byte[] data) {
        public int Position { get; private set; }

        public byte ReadByte() {
            if (Position >= data.Length) throw new InvalidDataException("servers.dat 数据不完整！");
            return data[Position++];
        }

        public int ReadCount() {
            int count = (ReadByte() << 24) | (ReadByte() << 16) | (ReadByte() << 8) | ReadByte();
            if (count < 0 || count > data.Length - Position) throw new InvalidDataException("servers.dat 的标签长度不正确！");
            return count;
        }

        public string ReadString() {
            int length = (ReadByte() << 8) | ReadByte();
            int start = Position;
            Skip(length);
            var result = new StringBuilder();
            int offset = start;
            while (offset < Position) {
                byte first = data[offset++];
                if (first <= 0x7F) result.Append((char)first);
                else if ((first & 0xE0) == 0xC0 && offset < Position && (data[offset] & 0xC0) == 0x80)
                    result.Append((char)(((first & 0x1F) << 6) | (data[offset++] & 0x3F)));
                else if ((first & 0xF0) == 0xE0 && offset + 1 < Position &&
                    (data[offset] & 0xC0) == 0x80 && (data[offset + 1] & 0xC0) == 0x80) {
                    result.Append((char)(((first & 0x0F) << 12) | ((data[offset] & 0x3F) << 6) | (data[offset + 1] & 0x3F)));
                    offset += 2;
                } else throw new InvalidDataException("servers.dat 的字符串编码不正确！");
            }
            return result.ToString();
        }

        private void Skip(long length) {
            if (length < 0 || length > data.Length - Position) throw new InvalidDataException("servers.dat 数据不完整！");
            Position += (int)length;
        }

        public void SkipPayload(byte tagType, int depth = 0) {
            if (depth > 128) throw new InvalidDataException("servers.dat 的标签嵌套过深！");
            switch (tagType) {
                case 1: Skip(1); break;
                case 2: Skip(2); break;
                case 3:
                case 5: Skip(4); break;
                case 4:
                case 6: Skip(8); break;
                case 7: Skip(ReadCount()); break;
                case 8: ReadString(); break;
                case 9:
                    byte elementType = ReadByte();
                    int count = ReadCount();
                    if (elementType > 12 || (elementType == 0 && count != 0)) throw new InvalidDataException("servers.dat 的列表类型不正确！");
                    for (int elementIndex = 0; elementIndex < count; elementIndex++) SkipPayload(elementType, depth + 1);
                    break;
                case 10:
                    byte childType;
                    while ((childType = ReadByte()) != 0) {
                        ReadString();
                        SkipPayload(childType, depth + 1);
                    }
                    break;
                case 11: Skip((long)ReadCount() * 4); break;
                case 12: Skip((long)ReadCount() * 8); break;
                default: throw new InvalidDataException("servers.dat 包含未知的标签类型！");
            }
        }
    }
}
