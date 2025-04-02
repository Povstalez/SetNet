using System;
using System.IO;

namespace SetNet.Core
{
    public class PacketBuilder
    {
        private readonly MemoryStream _buffer = new MemoryStream();

        public byte[] BuildPacket(ushort type, byte[] data)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                BinaryWriter writer = new BinaryWriter(stream);
                writer.Write(data.Length + 2); // Записуємо довжину даних (4 байти)
                writer.Write(type); // Записуємо тип пакета (2 байти)
                writer.Write(data); // Записуємо саме повідомлення

                return stream.ToArray();
            }
        }

        public static (ushort, byte[]) ParsePacket(byte[] packet)
        {
            using (MemoryStream stream = new MemoryStream(packet))
            {
                BinaryReader reader = new BinaryReader(stream);
                ushort type = reader.ReadUInt16(); // Читаємо тип
                byte[] data = reader.ReadBytes(packet.Length - 2); // Читаємо тіло

                return (type, data);
            }
        }

        public void AppendData(byte[] data)
        {
            _buffer.Write(data, 0, data.Length);
        }

        public bool TryGetCompletePacket(out byte[] packet)
        {
            packet = null;

            // Потрібно як мінімум 4 байти для отримання довжини пакета
            if (_buffer.Length < 4)
                return false;

            _buffer.Position = 0;

            // Зчитуємо довжину пакета
            var length = BitConverter.ToInt32(_buffer.GetBuffer(), 0);

            if (_buffer.Length >= length + 4)
            {
                packet = new byte[length];
                Array.Copy(_buffer.GetBuffer(), 4, packet, 0, length);

                // Забираємо прочитаний пакет із буфера
                var remainingData = new byte[_buffer.Length - (length + 4)];
                Array.Copy(_buffer.GetBuffer(), length + 4, remainingData, 0, remainingData.Length);

                _buffer.SetLength(0);
                _buffer.Write(remainingData, 0, remainingData.Length);

                return true;
            }

            return false;
        }
    }
}