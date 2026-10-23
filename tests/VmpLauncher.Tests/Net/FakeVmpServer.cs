using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using Lidgren.Network;

namespace VmpLauncher.Tests.Net;

/// <summary>
/// In-process stand-in for VMP's LidgrenServer: app id "VMP", denies a wrong hail with "Invalid password",
/// approves the configured password, and answers a handshake request the way HandshakeSystem does for a build
/// mismatch (HandshakeReply message, then disconnect with the SessionAdmission reason).
/// </summary>
internal sealed class FakeVmpServer : IDisposable
{
    public const string DefaultBuild = "0.2.0-train.20261021.1";

    private readonly NetServer _server;
    private readonly Thread _loop;
    private readonly string? _password;
    private readonly string _build;
    private readonly bool _sendReplyMessage;
    private volatile bool _stop;

    private readonly Func<(string PlayerName, string UniqueIdentifier, string ForkId, string Build), (int Code, string Reason)>? _reject;

    /// <param name="password">Null or empty = any hail accepted (server without password).</param>
    /// <param name="sendReplyMessage">False = only disconnect with the reason (exercises the disconnect-reason parser).</param>
    /// <param name="maximumConnections">0 = a full server: Lidgren answers every connect with "Server full" before any password check.</param>
    /// <param name="reject">Overrides the handshake rejection (HandshakeReply code + reason); default is the exact-build mismatch.</param>
    public FakeVmpServer(string? password, string build = DefaultBuild, bool sendReplyMessage = true, int maximumConnections = 32,
        Func<(string PlayerName, string UniqueIdentifier, string ForkId, string Build), (int Code, string Reason)>? reject = null)
    {
        _password = password;
        _build = build;
        _sendReplyMessage = sendReplyMessage;
        _reject = reject;
        var config = new NetPeerConfiguration("VMP")
        {
            LocalAddress = IPAddress.Loopback,
            Port = 0,
            MaximumTransmissionUnit = 1200,
            AutoExpandMTU = false,
            PingInterval = 1,
            ConnectionTimeout = 5,
            MaximumConnections = maximumConnections,
        };
        config.EnableMessageType(NetIncomingMessageType.ConnectionApproval);
        _server = new NetServer(config);
        _server.Start();
        _loop = new Thread(Loop) { IsBackground = true, Name = "FakeVmpServer" };
        _loop.Start();
    }

    public int Port => _server.Port;

    public ConcurrentQueue<string> Hails { get; } = new();
    public ConcurrentQueue<(string PlayerName, string UniqueIdentifier, string ForkId, string Build)> Handshakes { get; } = new();
    public int ApprovedCount;

    private void Loop()
    {
        while (!_stop)
        {
            var msg = _server.WaitMessage(20);
            if (msg is null)
            {
                continue;
            }
            try
            {
                switch (msg.MessageType)
                {
                    case NetIncomingMessageType.ConnectionApproval:
                        string hail;
                        try { hail = msg.ReadString(); } catch (NetException) { hail = string.Empty; }
                        Hails.Enqueue(hail);
                        if (string.IsNullOrEmpty(_password) || string.Equals(hail, _password, StringComparison.Ordinal))
                        {
                            Interlocked.Increment(ref ApprovedCount);
                            msg.SenderConnection.Approve();
                        }
                        else
                        {
                            msg.SenderConnection.Deny("Invalid password");
                        }
                        break;
                    case NetIncomingMessageType.Data:
                        HandleData(msg);
                        break;
                }
            }
            finally
            {
                _server.Recycle(msg);
            }
        }
    }

    private void HandleData(NetIncomingMessage msg)
    {
        // Same read order as LmpCommon FactoryBase.Deserialize + MessageData.Deserialize + HandshakeRequestMsgData.
        var type = msg.ReadUInt16();
        var sub = msg.ReadUInt16();
        msg.SkipPadBits();
        if (type != 0 || sub != 0)
        {
            return;
        }
        msg.ReadInt64();
        msg.ReadUInt16();
        msg.ReadUInt16();
        msg.ReadUInt16();
        msg.SkipPadBits();
        var hs = (msg.ReadString(), msg.ReadString(), msg.ReadString(), msg.ReadString());
        Handshakes.Enqueue(hs);

        var (code, reason) = _reject?.Invoke(hs) ?? (5, $"Exact build mismatch: server requires '{_build}', client sent '{hs.Item4}'.");
        if (_sendReplyMessage)
        {
            var reply = _server.CreateMessage();
            reply.Write((ushort)0);      // ServerMessageType.Handshake
            reply.Write((ushort)1);      // HandshakeMessageType.Reply
            reply.WritePadBits();
            reply.Write(DateTime.UtcNow.Ticks);
            reply.Write((ushort)0);
            reply.Write((ushort)2);
            reply.Write((ushort)0);
            reply.WritePadBits();
            reply.Write(code);           // HandshakeReply: 2 ServerFull, 4 ProtocolForkMismatch, 5 ProtocolBuildMismatch
            reply.Write(reason);
            reply.Write(false);
            reply.WritePadBits();
            reply.Write(0L);
            reply.Write(string.Empty);
            _server.SendMessage(reply, msg.SenderConnection, NetDeliveryMethod.ReliableOrdered, 1);
            _server.FlushSendQueue();
        }
        msg.SenderConnection.Disconnect(reason);
    }

    public void Dispose()
    {
        _stop = true;
        _loop.Join(1000);
        _server.Shutdown("bye");
    }

    public static string Fixture(string name, [CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "Fixtures", name);
}
