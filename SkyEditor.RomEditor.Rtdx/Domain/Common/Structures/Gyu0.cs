﻿using SkyEditor.IO.Binary;
using System;
using System.IO;
using System.Text;

namespace SkyEditor.RomEditor.Domain.Common.Structures
{
    public static class Gyu0
    {
        private enum Opcode
        {
            Copy,
            SplitCopy,
            Fill,
            Skip
        }

        public static IBinaryDataAccessor Decompress(IReadOnlyBinaryDataAccessor data)
        {
            var Magic = data.ReadInt32(0);
            var Length = data.ReadInt32(4);

            IBinaryDataAccessor dest = new BinaryFile(new byte[Length]);

            var dataIndex = 8;
            var destIndex = 0;

            while (true)
            {
                byte cur = data.ReadByte(dataIndex++);

                if (cur < 0x80)
                {
                    var next = data.ReadByte(dataIndex++);

                    // EOF marker
                    if (cur == 0x7F && next == 0xFF)
                        return dest;

                    var offset = 0x400 - ((cur & 3) * 0x100 + next);
                    var bytes = dest.ReadSpan(destIndex - offset, (cur >> 2) + 2);
                    dest.Write(destIndex, bytes);
                    destIndex += bytes.Length;
                    continue;
                }

                var op = (Opcode)((cur >> 5) - 4);
                var arg = cur & 0x1F;

                switch (op)
                {
                    case Opcode.Copy:
                        {
                            var bytes = data.ReadSpan(dataIndex, arg + 1);
                            dataIndex += bytes.Length;
                            dest.Write(destIndex, bytes);
                            destIndex += bytes.Length;
                            break;
                        }

                    case Opcode.SplitCopy:
                        {
                            var sep = data.ReadByte(dataIndex++);
                            var bytes = data.ReadSpan(dataIndex, arg + 2);
                            dataIndex += bytes.Length;
                            foreach (byte x in bytes)
                            {
                                dest.Write(destIndex++, sep);
                                dest.Write(destIndex++, x);
                            }
                            break;
                        }

                    case Opcode.Fill:
                        {
                            var fill = data.ReadByte(dataIndex++);
                            for (int i = 0; i < arg + 2; i++)
                                dest.Write(destIndex++, fill);
                            break;
                        }

                    case Opcode.Skip:
                        {
                            var count = arg < 0x1F ? arg + 1 : 0x20 + data.ReadByte(dataIndex++);
                            destIndex += count;
                            break;
                        }
                }
            }
        }

        public static IBinaryDataAccessor Compress(IReadOnlyBinaryDataAccessor input)
        {
            var output = new MemoryStream((int)input.Length / 2);
            var inputData = input.ReadArray();

            void writeArray(byte[] array) {
                output.Write(array, 0, array.Length);
            };

            writeArray(Encoding.ASCII.GetBytes("GYU0"));
            writeArray(BitConverter.GetBytes(inputData.Length));

            long dataOffset = 0;
            var compressionResult = new CompressionResult();
            while (dataOffset < inputData.LongLength)
            {
                // Try each of the compression algorithms without copying data first.
                // If we get a result, write that to the output right away.
                // Otherwise, try copying the least amount of data followed by one of the algorithms.
                TryCompress(inputData, dataOffset, output, ref compressionResult);
                if (!compressionResult.Valid)
                {
                    var copyOffset = dataOffset;
                    var copyCommandOffset = output.Position;
                    output.Position++;
                    while (!compressionResult.Valid && copyOffset - dataOffset < 31 && copyOffset < inputData.LongLength)
                    {
                        output.WriteByte(inputData[copyOffset]);
                        copyOffset++;
                        TryCompress(inputData, copyOffset, output, ref compressionResult);
                    }
                    var currPos = output.Position;
                    output.Position = copyCommandOffset;
                    output.WriteByte((byte)(0x80 + copyOffset - dataOffset - 1));
                    output.Position = currPos;
                    dataOffset = copyOffset;
                }
                if (compressionResult.Valid)
                {
                    dataOffset += compressionResult.InputByteCount;
                }
            }

            // Write EOF marker
            output.WriteByte(0x7F);
            output.WriteByte(0xFF);
            // Trim any excess bytes that may have been written by the TryCompress* methods
            output.SetLength(output.Position);
            return new BinaryFile(output.ToArray());
        }

        private class CompressionResult
        {
            public long InputByteCount { get; set; }
            public long OutputByteCount { get; set; }
            public float CompressionRatio => Valid ? ((float)InputByteCount / OutputByteCount) : 0;
            public bool Valid => (InputByteCount != 0);
        }

        private static void TryCompress(byte[] data, long offset, MemoryStream output, ref CompressionResult result)
        {
            var outputPos = output.Position;
            result.InputByteCount = 0;
            TryCompressSplitCopy(data, offset, output, ref result);
            output.Position = outputPos;

            TryCompressFill(data, offset, output, ref result);
            output.Position = outputPos;

            TryCompressSkip(data, offset, output, ref result);
            output.Position = outputPos;

            // FIXME: Redesign this algorithm; too slow
            // - Move to the main loop
            // - Use a rolling window instead of recomputing every time
            //TryCompressPrevious(data, offset, output, ref result);
            //output.Position = outputPos;

            if (result.Valid) output.Position += result.OutputByteCount;
        }

        private static void TryCompressSplitCopy(byte[] data, long offset, MemoryStream output, ref CompressionResult result)
        {
            try
            {
                var sep = data[offset];
                var count = 1;
                while (data[offset + count * 2] == sep && data[offset + count * 2 + 1] != sep && count < 0x21)
                {
                    count++;
                }

                if (count >= 2)
                {
                    var compressionRatio = count * 2.0f / (2.0f + count);
                    if (compressionRatio > result.CompressionRatio)
                    {
                        result.InputByteCount = count * 2;
                        result.OutputByteCount = 2 + count;
                        output.WriteByte((byte)(0xA0 + count - 2));
                        output.WriteByte(sep);
                        for (int i = 0; i < count; i++)
                        {
                            output.WriteByte(data[offset + i * 2 + 1]);
                        }
                    }
                }
            }
            catch (IndexOutOfRangeException)
            {
                // EOF means failure
            }
        }

        private static void TryCompressFill(byte[] data, long offset, MemoryStream output, ref CompressionResult result)
        {
            try
            {
                var fill = data[offset];
                var count = 1;
                while (data[offset + count] == fill && count < 0x21)
                {
                    count++;
                }

                if (count >= 2)
                {
                    var compressionRatio = count * 0.5f;
                    if (compressionRatio > result.CompressionRatio)
                    {
                        result.InputByteCount = count;
                        result.OutputByteCount = 2;
                        output.WriteByte((byte)(0xC0 + count - 2));
                        output.WriteByte(fill);
                    }
                }
            }
            catch (IndexOutOfRangeException)
            {
                // EOF means failure
            }
        }

        private static void TryCompressSkip(byte[] data, long offset, MemoryStream output, ref CompressionResult result)
        {
            try
            {
                var count = 0;
                while (data[offset + count] == 0 && count < 0x11F)
                {
                    count++;
                }
                if (count > 0)
                {
                    if (count < 0x1F)
                    {
                        var compressionRatio = count;
                        if (compressionRatio > result.CompressionRatio)
                        {
                            result.InputByteCount = count;
                            result.OutputByteCount = 1;
                            output.WriteByte((byte)(0xE0 + count - 1));
                        }
                    }
                    else
                    {
                        var compressionRatio = count * 0.5f;
                        if (compressionRatio > result.CompressionRatio)
                        {
                            result.InputByteCount = count;
                            result.OutputByteCount = 2;
                            output.WriteByte(0xFF);
                            output.WriteByte((byte)(count - 0x20));
                        }
                    }
                }
            }
            catch (IndexOutOfRangeException)
            {
                // EOF means failure
            }
        }

        private static void TryCompressPrevious(byte[] data, long offset, MemoryStream output, ref CompressionResult result)
        {
            // Don't waste time trying to look behind if there's nothing written yet
            if (offset == 0) return;

            try
            {
                // Search output up to 0x400 bytes behind for the longest subsequence of bytes found in data starting at offset.
                // The common substring must be between 2 and 33 bytes long.
                var maxLookbehindDistance = Math.Min(0x400, (int)offset);
                if (maxLookbehindDistance < 2) return;
                var maxLength = Math.Min(33, (int)Math.Min(maxLookbehindDistance, data.Length - offset));

                var lookbehindData = new Span<byte>(data, (int)(offset - maxLookbehindDistance), maxLookbehindDistance);
                var lookaheadData = new Span<byte>(data, (int)offset, maxLength);

                int matchLength = 0;
                int matchPos = -1;

                int[,] longestCommonSuffixes = new int[lookbehindData.Length + 1, lookaheadData.Length + 1];
                for (int i = 0; i <= lookbehindData.Length; i++)
                {
                    for (int j = 0; j <= lookaheadData.Length; j++)
                    {
                        if (i == 0 || j == 0)
                        {
                            longestCommonSuffixes[i, j] = 0;
                        }
                        else if (lookbehindData[i - 1] == lookaheadData[j - 1])
                        {
                            longestCommonSuffixes[i, j] = longestCommonSuffixes[i - 1, j - 1] + 1;
                            if (longestCommonSuffixes[i, j] > matchLength && longestCommonSuffixes[i, j] == j)
                            {
                                matchLength = longestCommonSuffixes[i, j];
                                matchPos = i - matchLength;
                            }
                        }
                        else
                        {
                            longestCommonSuffixes[i, j] = 0;
                        }
                    }
                }

                if (matchLength >= 2)
                {
                    var compressionRatio = matchLength * 0.5f;
                    if (compressionRatio > result.CompressionRatio)
                    {
                        var matchOffset = matchPos - maxLookbehindDistance;

                        result.InputByteCount = matchLength;
                        result.OutputByteCount = 2;
                        output.WriteByte((byte)((byte)((matchLength - 2) << 2) | (byte)((matchOffset >> 8) & 3)));
                        output.WriteByte((byte)(matchOffset & 0xFF));
                    }
                }
            }
            catch (IndexOutOfRangeException)
            {
                // EOF means failure
            }
        }
    }
}
