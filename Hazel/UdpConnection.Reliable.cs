﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hazel
{
    partial class UdpConnection
    {//TODO recycle dataevents and things?
        /// <summary>
        ///     The starting timeout, in miliseconds, at which data will be resent.
        /// </summary>
        /// <remarks>
        ///     On each resend this is doubled for that packet.
        /// </remarks>
        public int ResendTimeout { get { return resendTimeout; } set { resendTimeout = value; } }
        private int resendTimeout = 200;        //TODO this based of average ping?

        /// <summary>
        ///     Holds the last ID allocated.
        /// </summary>
        volatile ushort lastIDAllocated;

        /// <summary>
        ///     The number of items to remember we have received before overwriting.
        /// </summary>
        private readonly int receiveCapacity = 4096;

        /// <summary>
        ///     The packets of data that have been transmitted reliably and not acknowledged.
        /// </summary>
        Dictionary<ushort, Packet> reliableDataPacketsSent = new Dictionary<ushort, Packet>();

        /// <summary>
        ///     The last packets that were received.
        /// </summary>
        HashSet<ushort> reliableDataPacketsMissing = new HashSet<ushort>();

        /// <summary>
        ///     The packet id that was received last.
        /// </summary>
        volatile ushort reliableReceiveLast = 0;

        /// <summary>
        ///     Has the connection received anything yet
        /// </summary>
        volatile bool hasReceivedSomething = false;

        /// <summary>
        ///     Class to hold packet data
        /// </summary>
        class Packet
        {
            public byte[] Data;
            public Timer Timer;
            public int LastTimeout;

            public Packet(byte[] data, Action<Packet> resendAction, int timeout)
            {
                Data = data;
                
                Timer = new Timer(
                    (object obj) => resendAction(this),
                    null, 
                    timeout,
                    timeout
                );

                LastTimeout = timeout;
            }
        }

        /// <summary>
        ///     Writes the bytes neccessary for a reliable send and stores the send.
        /// </summary>
        /// <param name="bytes">The byte array to write to.</param>
        void WriteReliableSendHeader(byte[] bytes)
        {
            lock (reliableDataPacketsSent)
            {
                //Find an ID not used yet.
                ushort id;

                do
                    id = ++lastIDAllocated;
                while (reliableDataPacketsSent.ContainsKey(id));

                //Write ID
                bytes[1] = (byte)((id >> 8) & 0xFF);
                bytes[2] = (byte)id;

                //Create packet object
                Packet packet = new Packet(
                    bytes,
                    (Packet p) =>
                    {
                        WriteBytesToConnection(p.Data);

                        //Double packet timeout
                        p.Timer.Change(0, p.LastTimeout *= 2);
                    },
                    resendTimeout
                );

                //Remember packet
                reliableDataPacketsSent.Add(id, packet);
            }
        }

        /// <summary>
        ///     Handles receives from reliable packets.
        /// </summary>
        /// <param name="bytes">The buffer containing the data.</param>
        /// <returns>Whether the bytes were valid or not.</returns>
        bool HandleReliableReceive(byte[] bytes)
        {
            //Get the ID form the packet
            ushort id = (ushort)((bytes[1] << 8) + bytes[2]);

            //Always reply with acknowledgement in order to stop the sender repeatedly sending it
            WriteBytesToConnection(     //TODO group acks together
                new byte[]
                {
                    (byte)SendOptionInternal.Acknowledgement,
                    bytes[1],
                    bytes[2]
                }
            );

            //Handle reliableness!
            lock (reliableDataPacketsMissing)
            {
                //If the ID <= reliableReceiveLast it might be something we're missing
                //HasReceivedSomething handles the edge case of reliableReceiveLast = 0 & ID = 0
                //TODO Looping of IDs
                if (id <= reliableReceiveLast && hasReceivedSomething)
                {
                    //See if we're missing it, else this packet is a duplicate
                    if (reliableDataPacketsMissing.Contains(id))
                        reliableDataPacketsMissing.Remove(id);
                    else
                        return false;
                }
                
                //If ID > reliableReceiveLast then it's something new
                else
                {
                    //Mark items between the most recent receive and the id received as missing
                    for (ushort i = (ushort)(reliableReceiveLast + 1); i < id; i++)
                        reliableDataPacketsMissing.Add(i);

                    //Update the most recently received
                    reliableReceiveLast = id;
                    hasReceivedSomething = true;
                }
            }

            return true;
        }

        /// <summary>
        ///     Handles acknowledgement packets to us.
        /// </summary>
        /// <param name="bytes">The buffer containing the data.</param>
        void HandleAcknowledgement(byte[] bytes)
        {
            //Get ID
            ushort id = (ushort)((bytes[1] << 8) + bytes[2]);

            lock (reliableDataPacketsSent)
            {
                //Dispose of timer and remove from dictionary
                if (reliableDataPacketsSent.ContainsKey(id))
                {
                    reliableDataPacketsSent[id].Timer.Dispose();
                    reliableDataPacketsSent.Remove(id);
                }
            }
        }
    }
}
