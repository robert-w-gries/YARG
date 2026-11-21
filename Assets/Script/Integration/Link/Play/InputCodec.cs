using System;
using System.Collections.Generic;
using System.IO;

namespace YARG.Integration.Link.Play
{
    /// <summary>One recorded input, as it travels in <see cref="PlayerInputs.Inputs"/>.</summary>
    /// <remarks>The same values as YARG.Core's <c>GameInput</c>, which this layer doesn't use.</remarks>
    public readonly struct LinkInput : IEquatable<LinkInput>
    {
        public readonly double Time;
        public readonly int    Action;
        public readonly int    Value;

        public LinkInput(double time, int action, int value)
        {
            Time = time;
            Action = action;
            Value = value;
        }

        public bool Equals(LinkInput other) => Time.Equals(other.Time) && Action == other.Action && Value == other.Value;
        public override bool Equals(object obj) => obj is LinkInput other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Time, Action, Value);
        public override string ToString() => $"{Time:0.000}: {Action} = {Value}";
    }

    /// <summary>
    /// The binary format of the inputs in an <see cref="InputsMessage"/>: for each input, its time (double),
    /// action (int) and value (int), little-endian.
    /// </summary>
    public static class InputCodec
    {
        /// <summary>Bytes for each input.</summary>
        public const int INPUT_SIZE = 16;

        /// <summary>
        /// Inputs for each player in one message. A backlog (after a hitch, say) goes out over several messages,
        /// so no message gets near the transport's size limit. Friends refuse a message with more.
        /// </summary>
        public const int MAX_INPUTS_PER_MESSAGE = 4096;

        /// <summary>
        /// Encode the inputs from index <paramref name="from"/> in <paramref name="inputs"/>, at most
        /// <see cref="MAX_INPUTS_PER_MESSAGE"/> of them. The rest go in the next message.
        /// </summary>
        /// <param name="count">The number of inputs in the bytes.</param>
        public static byte[] Encode<T>(IReadOnlyList<T> inputs, int from, Func<T, LinkInput> convert, out int count)
        {
            count = Math.Max(0, Math.Min(inputs.Count - from, MAX_INPUTS_PER_MESSAGE));

            var bytes = new byte[count * INPUT_SIZE];
            using var writer = new BinaryWriter(new MemoryStream(bytes));
            for (int i = from; i < from + count; i++)
            {
                var input = convert(inputs[i]);
                writer.Write(input.Time);
                writer.Write(input.Action);
                writer.Write(input.Value);
            }
            return bytes;
        }

        /// <summary>Decode inputs from a friend. Extra bytes after the last whole input are ignored.</summary>
        /// <exception cref="FormatException">Too many inputs, or an input time that is not sane.</exception>
        public static List<LinkInput> Decode(byte[] bytes)
        {
            int count = (bytes?.Length ?? 0) / INPUT_SIZE;
            if (count > MAX_INPUTS_PER_MESSAGE)
            {
                throw new FormatException($"{count} inputs is more than {MAX_INPUTS_PER_MESSAGE}");
            }

            var inputs = new List<LinkInput>(count);
            if (count == 0) return inputs;

            using var reader = new BinaryReader(new MemoryStream(bytes));
            for (int i = 0; i < count; i++)
            {
                double time = reader.ReadDouble();
                int action = reader.ReadInt32();
                int value = reader.ReadInt32();
                PlayLimits.CheckTime(time, "input time");
                inputs.Add(new LinkInput(time, action, value));
            }
            return inputs;
        }
    }
}
