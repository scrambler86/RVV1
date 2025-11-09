using System;
using System.Buffers;
using System.IO;
using System.Text;

namespace Game.Common
{
    /// <summary>
    /// Reusable MemoryStream + BinaryWriter backed by ArrayPool to reduce GC.
    /// Place this file under Assets/Scripts/Game.Common/ and ensure Game.Common.asmdef exists.
    /// </summary>
    public sealed class PooledBinaryWriter : IDisposable
    {
        public byte[] Buffer { get; private set; }
        private MemoryStream _ms;
        private BinaryWriter _bw;
        private bool _disposed;

        public PooledBinaryWriter(int initialCapacity = 512)
        {
            int capacity = Math.Max(256, initialCapacity);
            Buffer = ArrayPool<byte>.Shared.Rent(capacity);
            _ms = new MemoryStream(Buffer, 0, Buffer.Length, writable: true, publiclyVisible: true);
            _bw = new BinaryWriter(_ms, Encoding.UTF8, leaveOpen: true);
        }

        public BinaryWriter Writer
        {
            get
            {
                if (_disposed) throw new ObjectDisposedException(nameof(PooledBinaryWriter));
                return _bw;
            }
        }

        public int Length
        {
            get
            {
                if (_disposed) throw new ObjectDisposedException(nameof(PooledBinaryWriter));
                return (int)_ms.Position;
            }
        }

        public ArraySegment<byte> ToSegment()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PooledBinaryWriter));
            return new ArraySegment<byte>(Buffer, 0, (int)_ms.Position);
        }

        public void Reset()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PooledBinaryWriter));
            _ms.Position = 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _bw?.Dispose();
            _ms?.Dispose();
            if (Buffer != null) { ArrayPool<byte>.Shared.Return(Buffer); Buffer = null; }
            _disposed = true;
        }
    }
}
