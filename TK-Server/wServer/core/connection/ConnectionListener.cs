﻿using common.database;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using wServer.core;
using wServer.networking.packets;
using wServer.networking.packets.outgoing;
using wServer.utils;

namespace wServer.networking.connection
{
    #region Tokens

    public sealed class SendToken
    {
        public readonly int BufferOffset;

        public int BytesAvailable;
        public int BytesSent;
        public byte[] Data;
        public ConcurrentQueue<OutgoingMessage> Pending;
        
        public SendToken(int offset)
        {
            BufferOffset = offset;
            Data = new byte[0x100000];
            Pending = new ConcurrentQueue<OutgoingMessage>();
        }

        public void Reset()
        {
            BytesAvailable = 0;
            BytesSent = 0;
            Pending = new ConcurrentQueue<OutgoingMessage>();
        }
    }

    public sealed class ReceiveToken
    {
        public const int PrefixLength = 5;

        public readonly int BufferOffset;

        public int BytesRead;
        public byte[] PacketBytes;
        public int PacketLength;

        public ReceiveToken(int offset)
        {
            BufferOffset = offset;
            PacketBytes = new byte[ConnectionListener.BufferSize];
            PacketLength = PrefixLength;
        }

        public byte[] GetPacketBody()
        {
            if (BytesRead < PrefixLength)
                throw new Exception("Packet prefix not read yet.");

            var packetBody = new byte[PacketLength - PrefixLength];
            Array.Copy(PacketBytes, PrefixLength, packetBody, 0, packetBody.Length);
            return packetBody;
        }

        public MessageId GetPacketId()
        {
            if (BytesRead < PrefixLength)
                throw new Exception("Packet id not read yet.");

            return (MessageId)PacketBytes[4];
        }

        public void Reset()
        {
            PacketLength = PrefixLength;
            BytesRead = 0;
        }
    }

    public enum SendState
    {
        Awaiting,
        Ready,
        Sending
    }

    #endregion Tokens

    public sealed class ConnectionListener
    {
        public const int BufferSize = 16384;

        private const int Backlog = 1024;
        private const int MaxSimultaneousAcceptOps = 20;
        private const int OpsToPreAllocate = 3;

        private GameServer GameServer;
        private BufferManager BuffManager;
        private ClientPool ClientPool;
        private SocketAsyncEventArgsPool EventArgsPoolAccept;
        private Semaphore MaxConnectionsEnforcer;

        public ConnectionListener(GameServer gameServer)
        {
            GameServer = gameServer;

            Port = GameServer.Configuration.serverInfo.port;
            MaxConnections = GameServer.Configuration.serverSettings.maxConnections;

            BuffManager = new BufferManager((MaxConnections + 1) * BufferSize * OpsToPreAllocate, BufferSize);
            EventArgsPoolAccept = new SocketAsyncEventArgsPool(MaxSimultaneousAcceptOps);
            ClientPool = new ClientPool(MaxConnections + 1);
            MaxConnectionsEnforcer = new Semaphore(MaxConnections, MaxConnections);
        }

        private Socket ListenSocket { get; set; }
        private int MaxConnections { get; }
        private int Port { get; }

        public void Initialize()
        {
            BuffManager.InitBuffer();

            for (var i = 0; i < MaxSimultaneousAcceptOps; i++)
                EventArgsPoolAccept.Push(CreateNewAcceptEventArgs());

            for (var i = 0; i < MaxConnections + 1; i++)
            {
                var send = CreateNewSendEventArgs();
                var receive = CreateNewReceiveEventArgs();
                ClientPool.Push(new Client(this, GameServer, send, receive));
            }

            var localEndPoint = new IPEndPoint(IPAddress.Any, Port);

            ListenSocket = new Socket(localEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            ListenSocket.Bind(localEndPoint);
            ListenSocket.Listen(Backlog);
        }

        public void Start() => StartAccept();

        private void AcceptEventArg_Completed(object sender, SocketAsyncEventArgs e) => ProcessAccept(e);

        private SocketAsyncEventArgs CreateNewAcceptEventArgs()
        {
            var acceptEventArg = new SocketAsyncEventArgs();
            acceptEventArg.Completed += AcceptEventArg_Completed;
            return acceptEventArg;
        }

        private SocketAsyncEventArgs CreateNewReceiveEventArgs()
        {
            var eventArgs = new SocketAsyncEventArgs();
            BuffManager.SetBuffer(eventArgs);
            eventArgs.UserToken = new ReceiveToken(eventArgs.Offset);
            return eventArgs;
        }

        private SocketAsyncEventArgs CreateNewSendEventArgs()
        {
            var eventArgs = new SocketAsyncEventArgs();
            BuffManager.SetBuffer(eventArgs);
            eventArgs.UserToken = new SendToken(eventArgs.Offset);
            return eventArgs;
        }

        private void HandleBadAccept(SocketAsyncEventArgs acceptEventArgs)
        {
            acceptEventArgs.AcceptSocket.Close();
            EventArgsPoolAccept.Push(acceptEventArgs);
        }

        public void Disable()
        {
            Console.WriteLine("[ConnectionListener] Disabled");
            try
            {
                ListenSocket.Shutdown(SocketShutdown.Both);
            }
            catch (Exception e)
            {
                if (!(e is SocketException se) || se.SocketErrorCode != SocketError.NotConnected)
                    StaticLogger.Instance.Error(e);
            }
            ListenSocket.Close();
        }

        private void ProcessAccept(SocketAsyncEventArgs acceptEventArgs)
        {
            if (acceptEventArgs.SocketError != SocketError.Success)
            {
                StartAccept();
                HandleBadAccept(acceptEventArgs);
                return;
            }

            acceptEventArgs.AcceptSocket.NoDelay = true;
            ClientPool.Pop().SetSocket(acceptEventArgs.AcceptSocket);

            acceptEventArgs.AcceptSocket = null;
            EventArgsPoolAccept.Push(acceptEventArgs);

            StartAccept();
        }

        private void StartAccept()
        {
            SocketAsyncEventArgs acceptEventArg;

            if (EventArgsPoolAccept.Count > 1)
                try { acceptEventArg = EventArgsPoolAccept.Pop(); }
                catch { acceptEventArg = CreateNewAcceptEventArgs(); }
            else
                acceptEventArg = CreateNewAcceptEventArgs();

            MaxConnectionsEnforcer.WaitOne();

            try
            {
                var willRaiseEvent = ListenSocket.AcceptAsync(acceptEventArg);

                if (!willRaiseEvent)
                    ProcessAccept(acceptEventArg);
            }
            catch { }
        }

        #region Disconnect - Shutdown

        public void Disconnect(Client client)
        {
            try
            {
                if (!client.Socket.Connected)
                    return;

                client.Socket.Shutdown(SocketShutdown.Both);
            }
            catch { }

            client.Socket.Close();
            client.Reset();

            ClientPool.Push(client);

            try { MaxConnectionsEnforcer.Release(); }
            catch (SemaphoreFullException e) { StaticLogger.Instance.Error(e); }
        }

        public void Shutdown()
        {
            foreach (var client in GameServer.ConnectionManager.Clients)
                client.Key?.Disconnect("Shutdown Server");

            while (EventArgsPoolAccept.Count > 0)
            {
                var eventArgs = EventArgsPoolAccept.Pop();
                eventArgs.Dispose();
            }

            while (ClientPool.Count > 0)
            {
                var client = ClientPool.Pop();
                client.Disconnect("Shutdown Server");
            }
        }

        #endregion Disconnect - Shutdown
    }
}
