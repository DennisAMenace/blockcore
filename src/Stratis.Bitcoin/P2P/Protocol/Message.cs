﻿using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using NBitcoin.Protocol;
using Stratis.Bitcoin.P2P.Protocol.Payloads;

namespace Stratis.Bitcoin.P2P.Protocol
{
    public class Message : IBitcoinSerializable
    {
        private uint magic;
        public uint Magic { get { return this.magic; } set { this.magic = value; } }

        private byte[] command = new byte[12];
        public string Command
        {
            get
            {
                return Encoders.ASCII.EncodeData(this.command);
            }
            private set
            {
                this.command = Encoders.ASCII.DecodeData(value.Trim().PadRight(12, '\0'));
            }
        }

        internal byte[] buffer;
        private Payload payloadObject;
        public Payload Payload
        {
            get
            {
                return this.payloadObject;
            }
            set
            {
                this.payloadObject = value;
                this.Command = this.payloadObject.Command;
            }
        }

        /// <summary>When parsing, maybe Magic is already parsed.</summary>
        private bool skipMagic;

        public bool IfPayloadIs<TPayload>(Action<TPayload> action) where TPayload : Payload
        {
            var payload = this.Payload as TPayload;
            if (payload != null)
                action(payload);
            return payload != null;
        }

        public void ReadWrite(BitcoinStream stream)
        {
            if ((this.Payload == null) && stream.Serializing)
                throw new InvalidOperationException("Payload not affected");

            if (stream.Serializing || (!stream.Serializing && !this.skipMagic))
                stream.ReadWrite(ref this.magic);

            stream.ReadWrite(ref this.command);
            int length = 0;
            uint checksum = 0;
            bool hasChecksum = false;
            byte[] payloadBytes = stream.Serializing ? GetPayloadBytes(stream.ProtocolVersion, out length) : null;
            length = payloadBytes == null ? 0 : length;
            stream.ReadWrite(ref length);

            if (stream.ProtocolVersion >= ProtocolVersion.MEMPOOL_GD_VERSION)
            {
                if (stream.Serializing)
                    checksum = Hashes.Hash256(payloadBytes, 0, length).GetLow32();

                stream.ReadWrite(ref checksum);
                hasChecksum = true;
            }

            if (stream.Serializing)
            {
                stream.ReadWrite(ref payloadBytes, 0, length);
            }
            else
            {
                // MAX_SIZE 0x02000000 Serialize.h.
                if (length > 0x02000000)
                    throw new FormatException("Message payload too big ( > 0x02000000 bytes)");

                payloadBytes = (this.buffer == null) || (this.buffer.Length < length) ? new byte[length] : this.buffer;
                stream.ReadWrite(ref payloadBytes, 0, length);

                if (hasChecksum)
                {
                    if (!VerifyChecksum(checksum, payloadBytes, length))
                    {
                        if (NodeServerTrace.Trace.Switch.ShouldTrace(TraceEventType.Verbose))
                            NodeServerTrace.Trace.TraceEvent(TraceEventType.Verbose, 0, "Invalid message checksum bytes");
                        throw new FormatException("Message checksum invalid");
                    }
                }

                BitcoinStream payloadStream = new BitcoinStream(payloadBytes);
                payloadStream.CopyParameters(stream);

                Type payloadType = PayloadAttribute.GetCommandType(this.Command);
                bool unknown = payloadType == typeof(UnknowPayload);
                if (unknown)
                    NodeServerTrace.Trace.TraceEvent(TraceEventType.Warning, 0, "Unknown command received : " + this.Command);

                object payload = this.payloadObject;
                payloadStream.ReadWrite(payloadType, ref payload);
                if (unknown)
                    ((UnknowPayload)payload).command = this.Command;

                this.Payload = (Payload)payload;
            }
        }

        // FIXME: protocolVersion is not used. Is this a defect?
        private byte[] GetPayloadBytes(ProtocolVersion protocolVersion, out int length)
        {
            MemoryStream ms = this.buffer == null ? new MemoryStream() : new MemoryStream(this.buffer);
            this.Payload.ReadWrite(new BitcoinStream(ms, true));
            length = (int)ms.Position;
            return this.buffer ?? GetBuffer(ms);
        }

        private static byte[] GetBuffer(MemoryStream ms)
        {
            return ms.ToArray();
        }

        internal static bool VerifyChecksum(uint256 checksum, byte[] payload, int length)
        {
            return checksum == Hashes.Hash256(payload, 0, length).GetLow32();
        }


        public override string ToString()
        {
            return string.Format("{0} : {1}", this.Command, this.Payload);
        }

        public static Message ReadNext(Socket socket, Network network, ProtocolVersion version, CancellationToken cancellationToken)
        {
            PerformanceCounter counter;
            return ReadNext(socket, network, version, cancellationToken, out counter);
        }

        public static Message ReadNext(Socket socket, Network network, ProtocolVersion version, CancellationToken cancellationToken, out PerformanceCounter counter)
        {
            return ReadNext(socket, network, version, cancellationToken, null, out counter);
        }
        public static Message ReadNext(Socket socket, Network network, ProtocolVersion version, CancellationToken cancellationToken, byte[] buffer, out PerformanceCounter counter)
        {
            var stream = new NetworkStream(socket, false);
            return ReadNext(stream, network, version, cancellationToken, buffer, out counter);
        }

        public static Message ReadNext(Stream stream, Network network, ProtocolVersion version, CancellationToken cancellationToken)
        {
            PerformanceCounter counter;
            return ReadNext(stream, network, version, cancellationToken, out counter);
        }

        public static Message ReadNext(Stream stream, Network network, ProtocolVersion version, CancellationToken cancellationToken, out PerformanceCounter counter)
        {
            return ReadNext(stream, network, version, cancellationToken, null, out counter);
        }

        public static Message ReadNext(Stream stream, Network network, ProtocolVersion version, CancellationToken cancellationToken, byte[] buffer, out PerformanceCounter counter)
        {
            BitcoinStream bitStream = new BitcoinStream(stream, false)
            {
                ProtocolVersion = version,
                ReadCancellationToken = cancellationToken
            };

            if (!network.ReadMagic(stream, cancellationToken, true))
                throw new FormatException("Magic incorrect, the message comes from another network");

            Message message = new Message();
            message.buffer = buffer;
            using (message.SkipMagicScope(true))
            {
                message.Magic = network.Magic;
                message.ReadWrite(bitStream);
            }

            counter = bitStream.Counter;
            return message;
        }

        private IDisposable SkipMagicScope(bool value)
        {
            bool old = this.skipMagic;
            return new Scope(() => this.skipMagic = value, () => this.skipMagic = old);
        }
    }
}