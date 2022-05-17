using System.Runtime.InteropServices.ComTypes;
using System.Reflection.Metadata.Ecma335;
using System.Collections.Concurrent;
using System.IO;
using System.Globalization;
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
    public abstract class NetworkedStateMachine
    {
        public delegate bool SubscribtionDelegate(Subscribtion sub, ReceivedMessage message);
        [StructLayout(LayoutKind.Sequential)]
        public struct PingRequestNM
        {
            public DateTime LocalTime;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct PingResponseNM
        {
            public DateTime LocalTime;
        }
        public class Subscribtion
        {
            public ulong id;
            public Peer source = null;
            public SubscribtionDelegate del;
            public Subscribtion(ulong id, SubscribtionDelegate del)
            {
                this.id = id;
                this.del = del;
            }
        }
        public class ReceivedMessage
        {
            public Peer Sender;
            public DateTime TimeReceived;
            public object Message;
        }
        public class Peer
        {
            RelayNode.RelayConnection con;
            public long ID => con.ID;
            public DateTime DiscoveryTime;
            public DateTime LastMessageTime;
            public DateTime LastSentToTime;
            public object LastSentTo;
            public object LastMessage;
            public bool IsAdmin => con.IsAdmin;
            public virtual void OnMessageSentTo(object msg)
            {
                LastSentToTime =  DateTime.Now;
                LastSentTo = msg;
            }
            public virtual void OnMessageReceived(object msg)
            {
                LastMessage = msg; 
                LastMessageTime = DateTime.Now;
            }
            public Peer(RelayNode.RelayConnection con)
            {
                Assert(ID != ID_RELAY && ID != ID_NULL);
                this.con = con;
            }
        }
        public List<Peer> Peers {get; protected set;} = new List<Peer>();
        static object _serializationSystemMutex = new object();
        static Dictionary<ulong, Func<byte[], object>> _deserializers = new Dictionary<ulong, Func<byte[], object>>();
        static Dictionary<Type, Func<object, byte[]>> _serializers = new Dictionary<Type, Func<object, byte[]>>();
        static Dictionary<Type, ulong> _typeToID = new Dictionary<Type, ulong>();
        static List<ulong> _typeIDs = new List<ulong>();
        Peer server;
        RelayClient _client;
        Thread _backgroundThread;
        Thread _logicThread;
        object _internalMutex = new object();
        Dictionary<ulong, List<Subscribtion>> _subscribtions = new Dictionary<ulong, List<Subscribtion>>();
        ConcurrentQueue<Subscribtion> _newSubscribtions = new ConcurrentQueue<Subscribtion>();
        Dictionary<ulong, List<(Peer peer, RelayNode.PackedRelayedMessage)>> _unregisteredMessages = new Dictionary<ulong, List<(Peer peer, RelayNode.PackedRelayedMessage)>>();
        Dictionary<ulong, List<ReceivedMessage>> _unsubscribedMessages = new Dictionary<ulong, List<ReceivedMessage>>();
        ulong GenerateTypeID(Type t)
        {
            ulong ret = 666;
            string s = t.FullName;
            for(int i = 0; i < s.Length; i+=sizeof(ulong))
            {
                ulong v = 0;
                for(int ii = 0; i+ii < s.Length && ii < sizeof(ulong); ii++)
                {
                    v += (((ulong)s[i+ii]) << (8*ii));
                }
                ret += new RNG(v).NextULong();
            }
            return ret;
        }
        ulong RegisterType<T>() where T : struct
        {
            ulong id = GenerateTypeID(typeof(T));
            int sz = Marshal.SizeOf<T>();
            Assert(sz <= MESSAGE_MAX_LEN);
            byte[] SS(T v)
            {   
                byte[] ret = BitConverter.GetBytes(id);
                ret = ret.Concat(SerializeStruct(v)).ToArray();
                return ret;
            }
            lock(_serializationSystemMutex)
            {
                Assert(!_typeIDs.Contains(id), "Hash collison");
                _typeIDs.Add(id);
                if(sz == 0)
                    _deserializers.Add(id, dt => new T());
                else 
                    _deserializers.Add(id, dt => DeserializeStruct<T>(dt));
                _serializers.Add(typeof(T), o => SS((T)o));
                _typeToID.Add(typeof(T), id);
            }
            lock(_internalMutex)
            {
                if(_unregisteredMessages.ContainsKey(id))
                {
                    var ums = _unregisteredMessages[id];
                    if(!_unsubscribedMessages.ContainsKey(id))
                        _unsubscribedMessages.Add(id, new List<ReceivedMessage>(ums.Count));
                    foreach(var it in ums)
                    {
                        var rawmsg = DeserializeMessage(it.Item2.data.Data, out id);
                        ReceivedMessage rm = new ReceivedMessage()
                        {
                            Sender = it.peer,
                            TimeReceived = DateTime.Now,
                            Message=rawmsg,
                        };
                        _unsubscribedMessages[id].Add(rm);
                    }
                    _unregisteredMessages.Remove(id);
                }
            }
            return id;
        }
        protected void EnsureRegistered<T>() where T : struct
        {
            lock(_serializationSystemMutex)
            {
                if(!_serializers.ContainsKey(typeof(T)))
                {
                    RegisterType<T>();
                }
            }
        }
        protected byte[] SerializeMessage<T>(T data) where T : struct
        {
            Func<object, byte[]> serializer = null;
            EnsureRegistered<T>();
            lock(_serializationSystemMutex)
            {
                serializer = _serializers[typeof(T)];
            }
            return serializer((object)data);
        }
        protected object DeserializeMessage(byte[] data, out ulong id)
        {
            Assert<NetMessageMalformedException>(data.Length >= sizeof(ulong));
            id = BitConverter.ToUInt64(data.Take(sizeof(ulong)).ToArray());
            Func<byte[], object> deserializer = null;
            lock(_serializationSystemMutex)
            {
                Assert<NetUnknownMessageException>(_typeIDs.Contains(id));
                deserializer = _deserializers[id];
            }
            return deserializer(data.Skip(sizeof(ulong)).ToArray());
        }
        public void UnSubscribe(Subscribtion sub)
        {
            lock(_internalMutex)
            {
                Assert(_subscribtions.ContainsKey(sub.id) && _subscribtions[sub.id].Contains(sub));
                _subscribtions[sub.id].Remove(sub);
            }
        }
        void AddSubscribtion(ulong id, Subscribtion sub)
        {
            lock(_internalMutex)
            {
                if(!_subscribtions.ContainsKey(id))
                    _subscribtions.Add(id, new List<Subscribtion>());
                _subscribtions[id].Add(sub);
                _newSubscribtions.Enqueue(sub);
            }
        }
        public Subscribtion Subscribe(ulong id, SubscribtionDelegate OnMessage, Peer source = null)
        {
            Assert(_typeIDs.Contains(id));
            Subscribtion sub = new Subscribtion(id, OnMessage);
            sub.source = source;
            AddSubscribtion(id, sub);
            return sub;
        }
        public Subscribtion Subscribe<T>(SubscribtionDelegate OnMessage) where T : struct
        {
            EnsureRegistered<T>();
            return Subscribe(_typeToID[typeof(T)], OnMessage);
        }
        public Subscribtion Subscribe<T>(Peer source, SubscribtionDelegate OnMessage) where T : struct
        {
            EnsureRegistered<T>();
            return Subscribe(_typeToID[typeof(T)], OnMessage, source);
        }
        public Subscribtion Subscribe<T>(Func<ReceivedMessage, bool> OnMessage) where T : struct
        {
            return Subscribe<T>((sub, msg) => 
            {
                return OnMessage(msg);
            });
        }
        public Subscribtion Subscribe<T>(Action<ReceivedMessage> OnMessage) where T : struct
        {
            return Subscribe<T>((sub, msg) => 
            {
                OnMessage(msg);
                return true;
            });
        }
        public Subscribtion SubscribeOneShot<T>(Action<ReceivedMessage> OnMessage) where T : struct
        {
            return Subscribe<T>((sub, msg) => 
            {
                OnMessage(msg);
                return false;
            });
        }
        public void Send<T>(Peer target, T data, bool unindexed = false) where T : struct
        {
            target.OnMessageSentTo(data);
            _client.SendRelayed(target.ID, SerializeMessage<T>(data), (unindexed) ? MSG_INDEX_IGNORE : -1);
        }
        public void SendUnindexed<T>(Peer target, T data) where T : struct
        {
            Send(target, data, true);
        }
        public void SendToAll<T>(T data, bool includeadmin, bool unindexed = false) where T : struct
        {
            var sermsg = SerializeMessage<T>(data);
            Peers.ForEach(target => 
            {
                if(includeadmin || !target.IsAdmin)
                {
                    target.OnMessageSentTo(data);
                    _client.SendRelayed(target.ID, sermsg, (unindexed) ? MSG_INDEX_IGNORE : -1);
                }
            });
        }
        public void SendToAllUnindexed<T>(T data, bool includeadmin) where T : struct
        {
            SendToAll<T>(data, includeadmin, true);
        }
        public async Task<T> AwaitMessage<T>(Peer peer = null, int timeout = DEFAULT_TIMEOUT_MS) where T : struct
        {
            SemaphoreSlim ss = new SemaphoreSlim(0, 1);
            T ret = default(T);
            Subscribe<T>(peer, (sub, msg)  => 
            {
                ret = (T)msg.Message;
                ss.Release();
                return false;
            });
            if(!await ss.WaitAsync(timeout))
            {
                throw new NetConnectionTimeoutException($"Timed out waiting for {typeof(T).Name}");
            }
            return ret;
        }
        public async Task<TResponse> SendAndAwaitResponse<TMessage, TResponse>(Peer peer, TMessage msg, int attempts = DEFAULT_RETRIES)
        where TResponse : struct where TMessage : struct
        {
            TResponse? res = null;
            for(int i = 0; i < attempts; i++)
            {
                Send(peer, msg);
                try
                {
                    res = await AwaitMessage<TResponse>(peer, DEFAULT_RETRY_DELAY_MS);
                }
                catch(NetConnectionTimeoutException)
                {
                    continue;
                }
                break;
            }
            if(res == null)
                throw new NetConnectionTimeoutException();
            return res.Value;
        }
        public async Task<double> CheckPingMS(Peer peer)
        {
            var sent = DateTime.Now;
            var res = await SendAndAwaitResponse<PingRequestNM, PingResponseNM>(peer, new PingRequestNM()
            {
                LocalTime=sent,
            });
            return (DateTime.Now-sent).TotalMilliseconds;
        }
        protected virtual void AddNewPeer(Peer peer)
        {
            Peers.Add(peer);
        }

        void BackgroundLoop()
        {
            AutoResetEvent are = new AutoResetEvent(false);
            ConcurrentQueue<(long id, RelayNode.PackedRelayedMessage prm)> queue = 
                new ConcurrentQueue<(long id, RelayNode.PackedRelayedMessage prm)>();
            _client.OnRelayedReceived += (id, it) =>
            {
                queue.Enqueue((id, it));
                are.Set();
            };

            void CheckSubs(ulong id, ReceivedMessage rm, Peer peer)
            {
                lock(_internalMutex)
                {
                    if(!_subscribtions.ContainsKey(id) || _subscribtions[id].Count == 0)
                    {
                        Console.WriteLine($"Received a message with not subscribers {rm.Message.GetType().Name}");
                        if(!_unsubscribedMessages.ContainsKey(id))
                            _unsubscribedMessages.Add(id, new List<ReceivedMessage>());
                        _unsubscribedMessages[id].Add(rm);
                        return;
                    }
                    var sublist = _subscribtions[id];
                    foreach(var sub in _subscribtions[id].ToList())
                    {
                        if(sub.source == peer || sub.source == null)
                        {
                            if(!sub.del(sub, rm))
                            {
                                sublist.Remove(sub);
                            }
                        }
                    }
                    _subscribtions[id] = sublist;
                }
            }
            

            while(true)
            {
                are.WaitOne(1);
                Subscribtion nsub;
                while(_newSubscribtions.TryDequeue(out nsub))
                {   
                    lock(_internalMutex)
                    {
                        if(_unsubscribedMessages.ContainsKey(nsub.id))
                        {
                            var usms = _unsubscribedMessages[nsub.id];
                            _unsubscribedMessages.Remove(nsub.id);
                            usms.ForEach(it => CheckSubs(nsub.id, it, it.Sender));
                        }
                    }
                }
                (long id, RelayNode.PackedRelayedMessage prm) mm;
                while(queue.TryDequeue(out mm))
                {
                    var peer = Peers.Find(it => it.ID == mm.id);
                    if(peer == null)
                    {
                        Console.WriteLine($"Received a message meant for a peer that does not exist {mm.id}");
                        continue;
                    }
                    object rawmsg;
                    ulong id = 0;
                    try
                    {
                        rawmsg = DeserializeMessage(mm.prm.data.Data, out id);
                    }
                    catch(NetUnknownMessageException)
                    {
                        Console.WriteLine($"Received an invalid message from {mm.id}, typeid={id}");
                        if(id != 0)
                        {
                            if(!_unregisteredMessages.ContainsKey(id))
                                _unregisteredMessages.Add(id, new List<(Peer peer, RelayNode.PackedRelayedMessage)>());
                            _unregisteredMessages[id].Add((peer, mm.prm));
                        }
                        continue;
                    }
                    ReceivedMessage rm = new ReceivedMessage()
                    {
                        Sender = peer,
                        TimeReceived = DateTime.Now,
                        Message=rawmsg,
                    };
                    CheckSubs(id, rm, peer);
                }
            }
        }
        Task<bool> _connectionTask;
        protected async Task AwaitConnection()
        {
            var res = await _connectionTask;
            if(!res)
                throw new NetConnectionErrorException("Failed to connect");
        }
        protected abstract void OnConnectionLost(NetConnectionErrorException ex);
        protected abstract void Logic();
        protected void Disconnect()
        {

        }
        void LogicCont()
        {
            try
            {
                Logic();
            }
            catch(NetConnectionErrorException err)
            {
                OnConnectionLost(err);
            }
            Disconnect();
        }
        public NetworkedStateMachine(IPEndPoint server)
        {
            _backgroundThread = new Thread(BackgroundLoop);
            _logicThread = new Thread(LogicCont);
            _logicThread.Start();
            _backgroundThread.Start();
            _client = new RelayClient(server);
            _client.OnNewConnection += con => 
            {
                AddNewPeer(new Peer(con));
            };
            _connectionTask = _client.TryConnect();
        }
    }
}