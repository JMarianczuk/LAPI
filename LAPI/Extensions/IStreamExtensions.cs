﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LAPI.Contracts;
using LAPI.Model;

namespace LAPI.Extensions
{
    public static class IStreamExtensions
    {
        public const int EncryptedPublicKeyInformationLength = 9;

        public const int Int32ByteSize = 4;

        private static CommunicationResult<TResult> From<TResult>(CommunicationResult result) =>
            CommunicationResult<TResult>.From(result);

        public static Task<int> ReadAsync(this Stream stream, byte[] buffer, CancellationToken token)
        {
            return stream.ReadAsync(buffer, 0, buffer.Length, token);
        }

        public static Task WriteAsync(this Stream stream, byte[] buffer, CancellationToken token)
        {
            return stream.WriteAsync(buffer, 0, buffer.Length, token);
        }

        public static async Task<CommunicationResult> WriteSafelyAsync(
            this Stream stream,
            CancellationToken token,
            params StreamWritable[] elements)
        {
            try
            {
                await stream.WriteAsync(StreamWritable.Combine(elements), token);
                return new CommunicationResult
                {
                    Successful = true,
                };
            }
            catch (Exception exc)
            {
                return new CommunicationResult
                {
                    Successful = false,
                    Exception = exc,
                };
            }
        }
        public static async Task<CommunicationResult<byte[]>> ReadSafelyAsync(this Stream stream, int length, CancellationToken token)
        {
            var buffer = new byte[length];
            try
            {
                var bytesReceived = await stream.ReadAsync(buffer, token);
                if (bytesReceived == length)
                {
                    return new CommunicationResult<byte[]>
                    {
                        Successful = true,
                        Result = buffer
                    };
                }
            }
            catch (Exception exc)
            {
                return new CommunicationResult<byte[]>
                {
                    Successful = false,
                    Exception = exc
                };
            }
            return CommunicationResult<byte[]>.Failed;
        }

        public static async Task<CommunicationResult<byte>> ReceiveByteSafelyAsync(this Stream stream, CancellationToken token)
        {
            var result = await stream.ReadSafelyAsync(1, token);
            if (result.Successful)
            {
                return new CommunicationResult<byte>
                {
                    Successful = true,
                    Result = result.Result[0],
                };
            }
            return CommunicationResult<byte>.Failed;
        }

        public static async Task<CommunicationResult<int>> ReceiveInt32SafelyAsync(this Stream stream, CancellationToken token)
        {
            var result = await stream.ReadSafelyAsync(Int32ByteSize, token);
            if (result.Successful)
            {
                return new CommunicationResult<int>
                {
                    Successful = true,
                    Result = BitConverter.ToInt32(result.Result, 0)
                };
            }
            return From<int>(result);
        }

        public static async Task<CommunicationResult<Guid>> ReceiveGuidSafelyAsync(this Stream stream, CancellationToken token)
        {
            var result = await stream.ReadSafelyAsync(new Guid().ToByteArray().Length, token);
            if (result.Successful)
            {
                return new CommunicationResult<Guid>
                {
                    Successful = true,
                    Result = new Guid(result.Result)
                };
            }
            return From<Guid>(result);
        }

        public class StreamWritable
        {
            private readonly byte[] _buffer;
            private StreamWritable(byte[] buffer)
            {
                _buffer = buffer;
            }

            public static implicit operator StreamWritable(byte[] buffer) => new StreamWritable(buffer);

            public static implicit operator StreamWritable(bool logical) => new StreamWritable(BitConverter.GetBytes(logical));
            public static implicit operator StreamWritable(int number) => new StreamWritable(BitConverter.GetBytes(number));
            public static implicit operator StreamWritable(long number) => new StreamWritable(BitConverter.GetBytes(number));
            public static implicit operator StreamWritable(float number) => new StreamWritable(BitConverter.GetBytes(number));
            public static implicit operator StreamWritable(double number) => new StreamWritable(BitConverter.GetBytes(number));

            public static implicit operator StreamWritable(Guid guid) => new StreamWritable(guid.ToByteArray());

            public static byte[] Combine(IReadOnlyCollection<StreamWritable> writables)
            {
                var totalLength = writables.Sum(x => x._buffer.Length);
                var result = new byte[totalLength];
                var currentIndex = 0;
                foreach (var part in writables)
                {
                    Array.Copy(part._buffer, 0, result, currentIndex, part._buffer.Length);
                    currentIndex += part._buffer.Length;
                }
                return result;
            }
        }
    }

}