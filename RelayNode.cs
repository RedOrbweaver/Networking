using System.Collections.Concurrent;
using System.Xml.Schema;
using System.ComponentModel.DataAnnotations;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using static System.Math;

using static Utils;

public static partial class Networking
{
    public abstract class RelayNode
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RelayMessage
        {
            public bool Encrypted;
            public long ReceiverID;
            public long SenderID;
            public VArray256 Message;
        }
        public class DeserializedRelayMessage
        {
            public RelayMessageType type;
            public long index;
            public object data;
        }
        public class RelayConnection
        {
            public IPEndPoint End;
            public PublicKey ClientPubKey;
            public PublicKey ServerPubKey;
            public PrivateKey ServerPrivKey;
            public long ID;
            public bool IsAdmin = false;
            public long LastReceivedIndex = 0;
            public long LastSentIndex = 0;
            public bool LoggedIn = false;
            public FIFO<(long index, RelayMessage rm)> SavedMessages = new FIFO<(long index, RelayMessage rm)>(N_SAVED_MESSAGES);
        }
        public enum RelayMessageType
        {
            ERR=0,
            CONNECTION_REQUEST,
            CONNECTION_RESPONSE,
            LOGIN_REQUEST,
            LOGIN_RESPONSE,
            RESEND_MESSAGES,
            RELAY,
            NEW_PEER,
            LAST_TYPE,
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct ConnectionRequest
        {
            public PublicKey pubkey;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct ConnectionResponse
        {
            public bool allowed;
            public PublicKey pubkey;
            public long AssignedID;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct LoginRequest
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PASSWORD_LENGTH)]
            public string password;
            public bool admin;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PASSWORD_LENGTH)]
            public string admin_password;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct LoginResponse
        {
            public bool success;
            public bool admin;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct ResendMessagesMessage
        {
            public long First;
            public long Last;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct PackedRelayedMessage
        {
            public VArray128 data;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct NewPeerMessage
        {
            public long ID;
            public bool IsAdmin;
        }
        public void Resend(RelayConnection con, ResendMessagesMessage rm)
        {
            for(long i = rm.First; i <= rm.Last; i++)
            {
                var nmsgi = con.SavedMessages.ToList().FindIndex(it => it.index == i);
                if(nmsgi != -1)
                {
                    var nmsg = con.SavedMessages[nmsgi].rm;
                    Send(con.End, nmsg);
                }
            }
        }
        public virtual void AddConnection(RelayConnection conn)
        {
            this.Connections.Add(conn);
        }
        public DeserializedRelayMessage DeserializeMessage(RelayMessage msg, RelayConnection conn)
        {
            DeserializedRelayMessage des = new DeserializedRelayMessage();
            var dt = msg.Message.Data;
            if(msg.Encrypted && conn != null)
                dt = Decrypt(dt, conn.ServerPrivKey);
            NetAssert(dt.Length >= sizeof(int) + sizeof(long));
            int head = BitConverter.ToInt32(dt.Take(sizeof(int)).ToArray());
            NetAssert(head > (int)RelayMessageType.ERR && head < (int)RelayMessageType.LAST_TYPE);
            RelayMessageType type = (RelayMessageType)head;
            des.type = type;
            des.index = BitConverter.ToInt64(dt.Skip(sizeof(int)).Take(sizeof(long)).ToArray());
            var rest = dt.Skip(sizeof(int)+sizeof(long)).ToArray();
            switch(type)
            {
                case RelayMessageType.CONNECTION_REQUEST:
                    des.data = DeserializeStruct<ConnectionRequest>(rest);
                    break;
                case RelayMessageType.CONNECTION_RESPONSE:
                    des.data = DeserializeStruct<ConnectionResponse>(rest);
                    break;
                case RelayMessageType.LOGIN_REQUEST:
                    des.data = DeserializeStruct<LoginRequest>(rest);
                    break;
                case RelayMessageType.LOGIN_RESPONSE:
                    des.data = DeserializeStruct<LoginResponse>(rest);
                    break;
                case RelayMessageType.RESEND_MESSAGES:
                    des.data = DeserializeStruct<ResendMessagesMessage>(rest);
                    break;
                case RelayMessageType.RELAY:
                    des.data = DeserializeStruct<PackedRelayedMessage>(rest);
                    break;
                case RelayMessageType.NEW_PEER:
                    des.data = DeserializeStruct<NewPeerMessage>(rest);
                    break;
                default:
                    throw new NotImplementedException();
            }
            return des;
        }
        public RelayMessage SerializeMessage<T>(RelayConnection target, long message_index, long sourceID, RelayMessageType type, T obj, bool encrypt) where T : struct
        {
            var rm = new RelayMessage();
            rm.SenderID = sourceID;
            rm.ReceiverID = message_index;
            var sersr = SerializeStruct<T>(obj);
            List<byte> dt = new List<byte>(sizeof(int) + sizeof(long) + sersr.Length);
            dt.AddRange(BitConverter.GetBytes((int)type));
            dt.AddRange(BitConverter.GetBytes(message_index));
            dt.AddRange(sersr);
            var adt = dt.ToArray();
            if(encrypt)
                adt = Encrypt(adt, target.ClientPubKey);
            rm.Encrypted = encrypt;
            rm.Message.Data = adt;
            return rm;
        }
        protected long ID = ID_NULL;
        Socket _socket;
        Thread _receiveThread;
        Thread _sendThread;
        Thread _processThread;
        AutoResetEvent _sendEvent = new AutoResetEvent(false);
        AutoResetEvent _receiveEvent = new AutoResetEvent(false);
        ConcurrentQueue<(IPEndPoint end, byte[] data)> _receiveQueue = new ConcurrentQueue<(IPEndPoint end, byte[] data)>();
        ConcurrentQueue<(IPEndPoint end, byte[] data)> _sendQueue = new ConcurrentQueue<(IPEndPoint end, byte[] data)>();
        bool _ending = false;
        object _mutex = new object();
        IPEndPoint _listenEnd;
        ushort _port;
        public List<RelayConnection> Connections = new List<RelayConnection>();
        public  void Send(IPEndPoint end, RelayMessage message)
        {
            byte[] dt = SerializeStruct(message);
            Console.WriteLine($"Sending {dt.Length} bytes to {end}");
            _sendQueue.Enqueue((end, dt));
            _sendEvent.Set();
        } 
        public void Send<T>(long sender_ID, long indx, RelayConnection conn, RelayMessageType type, T dt) where T : struct
        {
            lock(conn)
            {
                var rm = SerializeMessage(conn, indx, ID, type, dt, true);
                lock(conn.SavedMessages)
                {
                    conn.SavedMessages.Enqueue((indx, rm));
                }
                Send(conn.End, rm);
            }
        }
        public void Send<T>(RelayConnection conn, long indx, RelayMessageType type, T dt) where T : struct
        {
            Send(ID, indx, conn, type, dt);
        }
        static int _nextID = 0;
        static object _idLock = new object();
        int _debugID;

        void ReceiveLoop()
        {
            byte[] buf = new byte[1024];
            while(true)
            {
                lock(_mutex)
                {
                    if(_ending)
                    {
                        Console.WriteLine($"({_debugID})Ending");
                        return;
                    }
                }
                EndPoint endpoint = new IPEndPoint(_listenEnd.Address, _listenEnd.Port);
                int received = _socket.ReceiveFrom(buf, SocketFlags.None, ref endpoint);
                byte[] dt = buf.Take(received).ToArray();
                Console.WriteLine($"({_debugID})Received messsage");
                _receiveQueue.Enqueue(((IPEndPoint)endpoint, dt));
                _receiveEvent.Set();
            }
        }
        void SendLoop()
        {
            while(true)
            {
                bool b = _sendEvent.WaitOne(10);
                lock(_mutex)
                {
                    if(_ending)
                        return;
                }
                if(!b)
                    continue;
                (IPEndPoint end, byte[] data) tosend;
                while(_sendQueue.TryDequeue(out tosend))
                {
                    _socket.SendTo(tosend.data, tosend.end);
                }
            }
        }
        void ProcessReceivedLoop()
        {  
            while(true)
            {
                bool b = _receiveEvent.WaitOne(10);
                lock(_mutex)
                {
                    if(_ending)
                        return;
                }
                if(!b)
                    continue;
                (IPEndPoint end, byte[] data) received;
                while(_receiveQueue.TryDequeue(out received))
                {
                    Console.WriteLine($"({_debugID})Received messsage");
                    RelayMessage? message = null;
                    try
                    {
                        message = DeserializeStruct<RelayMessage>(received.data);
                        Console.WriteLine($"ReceiverID: {message.Value.ReceiverID}");
                        Console.WriteLine($"SenderID: {message.Value.SenderID}\n");
                    }
                    catch(DeserializationFailedException)
                    {
                        Console.WriteLine($"Unable to deserialize message from {received.end}");
                    }
                    if(message != null)
                    {
                        Console.WriteLine($"Received {received.data.Length} bytes from {received.end}");
                        OnReceive((IPEndPoint)received.end, message.Value);
                    }
                }
                
            }
        }
        public abstract void OnReceive(IPEndPoint source, RelayMessage message);
        public void NoteReceiveError(IPEndPoint source)
        {
            Console.WriteLine($"Received a malformed message from {source}");
        }
        public void NoteReceiveError(IPEndPoint source, DeserializedRelayMessage drm)
        {
            Console.WriteLine($"Received a malformed message from {source}. Type {drm.type}, index {drm.index}");
        }
        public void RequestResendMessages(RelayConnection conn, long first, long last)
        {
            Assert(first != MSG_INDEX_IGNORE && first <= last);
            Send<ResendMessagesMessage>(ID, MSG_INDEX_IGNORE, conn, RelayMessageType.RESEND_MESSAGES, new ResendMessagesMessage()
            {
                First = first,
                Last = last
            });
        }
        public void RequestResendMessage(RelayConnection conn, long indx)
        {
            Assert(indx != MSG_INDEX_IGNORE);
            RequestResendMessages(conn, indx, indx);
        }
        
        public RelayNode(IPEndPoint listen_end, ushort port=0)
        {
            lock(_idLock)
            {
                _debugID = _nextID++; 
            }
            _listenEnd = listen_end;
            _port = port;
            _socket = new Socket(listen_end.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            _socket.Bind(new IPEndPoint(IPAddress.Any, port));
            _receiveThread = new Thread(ReceiveLoop);
            _sendThread = new Thread(SendLoop);
            _processThread = new Thread(ProcessReceivedLoop);
            _sendThread.Start();
            _processThread.Start();
            _receiveThread.Start();
        }
        ~RelayNode()
        {
            lock(_mutex)
            {
                _ending = true;
            }
            _receiveThread.Join(100);
            _sendThread.Join(100);
            _processThread.Join(100);
        }
    }
}