#define DISABLE_TIMEOUTS
//#define ASSERT_NET_WARNINGS
#define ASSERT_NET_ERRORS
#define ASSERT_NET_FATALS
#define LOG_EVERYTHING
using System.Data.Common;

using System.Resources;
using System.Xml.Schema;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

using Newtonsoft.Json;
using static System.Math;

using static Networking;
using static Utils;

public static partial class Networking
{
    public abstract partial class Peer
    {
        public class ReceivedMessage
        {
            public IPEndPoint Endpoint;
            public DateTime Time;
            public Message Msg;
            public bool PreValidated = false;
            public bool Delayed = false;
        }
        public class SentMessage
        {
            public long ID => Msg.MessageID;
            public IPEndPoint Endpoint;
            public bool FailedAtLeastOnce;
            public bool RequestReturn;
            public DateTime Time;
            public DateTime LastTime;
            public Message Msg;
            public int Retries = UNIMPORTANT_RETRIES;
            public int Timeout = DEFAULT_TIMEOUT;
            public Action<SentMessage> OnFailure;
            public Action<SentMessage> OnSuccess;
        }
        public class AwaitedMessage
        {
            public long ID;
            public int SenderID;
            public int MType;
            public int Timeout;
            public Action<AwaitedMessage> OnTimeout;
            public Action<AwaitedMessage> OnReceived;
        }
        public class PartialConnection
        {
            public IPEndPoint Endpoint;
            public string PrivateKey;
            public string PublicKey;
        }
        public PartialConnection _partialConnection = null;
        public ushort Port { get; protected set; }
        public long ID { get; protected set; }
        public Dictionary<long, Connection> ConnectionsByID { get; protected set; } = new Dictionary<long, Connection>(256);
        public List<Connection> Connections { get; protected set; } = new List<Connection>(256);
        public IPEndPoint DefaultEndpoint = null;
        protected List<IPAddress> _bannedIPs = new List<IPAddress>();

        UdpClient _sender;
        UdpClient _receiver;
        bool _shutdown = false;


        protected Queue<SentMessage> _toSend = new Queue<SentMessage>(256);
        protected Queue<ReceivedMessage> _toReceive = new Queue<ReceivedMessage>(256);
        protected Dictionary<int, List<AwaitedMessage>> _awaitedMessages = new Dictionary<int, List<AwaitedMessage>>();
        protected List<SentMessage> _sentMessages = new List<SentMessage>();

        protected AutoResetEvent ev = new AutoResetEvent(false);
        protected AutoResetEvent _sendEvent = new AutoResetEvent(false);
        protected AutoResetEvent _recvEvent = new AutoResetEvent(false);
        System.Threading.Thread _receiveThread;
        System.Threading.Thread _sendThread;
        System.Threading.Thread _mainThread;
        protected Dictionary<int, List<Action<Message>>> _handles = new Dictionary<int, List<Action<Message>>>();
        protected Dictionary<long, long> AlternativeAddresses = new Dictionary<long, long>();
        object _receiveProcessMX = new object();
        long _peerIdCounter = 1;
        long GeneratePeerID()
        {
            long id = _peerIdCounter++;
            while (Connections.FindIndex(it => it.ID == id) != -1)
            {
                id++;
            }
            return id;
        }

        public enum Severity
        {
            INFO,
            WARNING,
            ERROR,
            FAIL,
        }
        object _logMX = new object();
        protected IPEndPoint GetEndpointForID(long ID, bool allowall = true, bool allowfail = false)
        {
            if(ID == ID_ALL)
            {
                Assert(allowall);
                return null;
            }
            if(ID == ID_UNKNOWN)
            {
                if(DefaultEndpoint == null)
                {
                    Assert(allowfail);
                    return null;
                }
                return DefaultEndpoint;
            }
            if(!ConnectionsByID.ContainsKey(ID))
            {
                Assert(allowfail);
                return null;
            }
            return ConnectionsByID[ID].Endpoint;
        }
        protected virtual void OnQueueSend(Message msg, bool requestreturn)
        {
            if (requestreturn && ConnectionsByID.ContainsKey(msg.ReceiverID))
            {
                ConnectionsByID[msg.ReceiverID].NoteMessageSent();
            }
        }
        protected void QueueSendMessage(SentMessage smsg)
        {
            Assert(smsg.Endpoint != null || smsg.Msg.ReceiverID == ID_ALL);
            smsg.Msg.RequestConfirmation = smsg.RequestReturn;
            lock (_toSend)
            {
                _toSend.Enqueue(smsg);
                _sendEvent.Set();
            }
            lock(_sentMessages)
            {
                if(smsg.RequestReturn && !_sentMessages.Contains(smsg))
                {
                    _sentMessages.Add(smsg);
                }
            }
        }
        protected void QueueSendMessage(Message msg, bool requestreturn)
        {
            Assert(ConnectionsByID.ContainsKey(msg.ReceiverID));
            QueueSendMessage(new SentMessage()
            {
                Endpoint = ConnectionsByID[msg.ReceiverID].Endpoint,
                RequestReturn = requestreturn,
                Msg = msg,
                Time = DateTime.Now,
                LastTime = DateTime.Now,
            });
            OnQueueSend(msg, requestreturn);
        }
        protected void QueueSendMessage(IPEndPoint end, Message msg, bool requestreturn)
        {
            QueueSendMessage(new SentMessage()
            {
                Endpoint = end,
                RequestReturn = requestreturn,
                Msg = msg,
                Time = DateTime.Now,
                LastTime = DateTime.Now,
            });
            OnQueueSend(msg, requestreturn);
        }
        protected void QueueSendMessage(IPEndPoint end, Message msg, Action<SentMessage> failhandle,
            Action<SentMessage> successhandle, int responsetimeoutms = DEFAULT_TIMEOUT, int retries = 0)
        {
            Assert(failhandle != null);
            Assert(end != null || msg.ReceiverID == ID_ALL);
            QueueSendMessage(new SentMessage()
            {
                Endpoint = end,
                RequestReturn = true,
                Msg = msg,
                Time = DateTime.Now,
                LastTime = DateTime.Now,
                Retries = retries,
                Timeout = responsetimeoutms,
                OnFailure = failhandle,
                OnSuccess = successhandle,
            });
            OnQueueSend(msg, true);
        }
        protected void QueueSendMessage(Message msg, Action<SentMessage> failhandle,
            Action<SentMessage> successhandle, int responsetimeoutms = DEFAULT_TIMEOUT, int retries = 0)
        {
            QueueSendMessage(GetEndpointForID(msg.ReceiverID), msg, failhandle, successhandle, responsetimeoutms, retries);
            OnQueueSend(msg, true);
        }
        protected void QueueSendMessage(Message msg, Action<SentMessage> failhandle,
            int responsetimeoutms = DEFAULT_TIMEOUT, int retries = 0)
        {
            QueueSendMessage(msg, failhandle, null, responsetimeoutms, retries);
        }

        protected byte[] DecryptData(IPEndPoint ip, byte[] dt)
        {
            return dt.ToArray();
        }
        protected byte[] EncryptData(IPEndPoint ip, long id, byte[] dt)
        {
            return dt.ToArray();
        }
        void QueueReceivedMessage(IPEndPoint endpoint, Message msg)
        {
            lock (_toReceive)
            {
                _toReceive.Enqueue(new ReceivedMessage()
                {
                    Msg = msg,
                    Endpoint = endpoint,
                    Time = DateTime.Now,
                });
                _recvEvent.Set();
            }
        }
        void ReceiveLoop()
        {
            while (!_shutdown)
            {
                IPEndPoint ip = new IPEndPoint(IPAddress.Any, Port);
                byte[] dt = _receiver.Receive(ref ip);
                lock (_receiveProcessMX)
                {
                    dt = DecryptData(ip, dt);
                    Message msg;
                    try
                    {
                        msg = Message.Deserialize(dt);
                    }
                    catch (NetAssertionException ex)
                    {
                        LogEvent(ex.Message, Severity.ERROR);
                        continue;
                    }
                    lock (_toReceive)
                    {
                        QueueReceivedMessage(ip, msg);
                    }
                }
            }
        }
        void SendLoop()
        {
            void SendMessage(IPEndPoint end, long ID, byte[] dt, bool encrypted)
            {
                Assert(IsPeerID(ID));
                if (encrypted)
                {
                    dt = EncryptData(end, ID, dt);
                }
                _sender.Send(dt, dt.Length, end);
            }
            while (!_shutdown)
            {
                SentMessage smsg;
                int count = 0;
                lock (_toSend)
                {
                    count = _toSend.Count;
                }
                if (count == 0)
                {
                    _sendEvent.WaitOne();
                    continue;
                }
                lock (_toSend)
                {
                    smsg = _toSend.Dequeue();
                }
                var msg = smsg.Msg;
                msg.RequestConfirmation = smsg.RequestReturn;
                byte[] dt = Message.Serialize(msg);
                if (msg.ReceiverID == ID_ALL)
                {
                    Connections.ForEach(con => SendMessage(con.Endpoint, con.ID, dt, msg.Encrypted));
                }
                else
                {
                    IPEndPoint endpoint = smsg.Endpoint;
                    NetAssert(endpoint != null, "Attempting to send a message to null endpoint");
                    SendMessage(endpoint, msg.ReceiverID, dt, msg.Encrypted);
                }
            }
        }
        void UpdateConnections()
        {
            foreach (var it in Connections)
            {
                var now = DateTime.Now;
                if ((now - it.LastSent).TotalMilliseconds > AUTO_ECHO_AFTER)
                {
                    Echo(it, (SentMessage sm) =>
                    {
                        DisconnectConnection(it, DisconnectReason.TIMEOUT, true);
                    }, AUTO_ECHO_TIMEOUT, IMPORTANT_RETRIES);
                }
                if ((now - it.LastUpdate).TotalMilliseconds > FORCED_DISCONNECT_TIMEOUT)
                {
                    DisconnectConnection(it, DisconnectReason.TIMEOUT, true);
                    continue;
                }
                var toexec = new List<(ReceivedMessage rmsg, DateTime rtime)>();
                foreach (var dm in it.DelayedMessages)
                {
                    if (DateTime.Now >= dm.rtime)
                    {
                        toexec.Add(dm);
                    }
                }
                if (toexec.Count > 0)
                {
                    toexec.Sort((dm0, dm1) => dm0.rtime.CompareTo(dm1.rtime));
                    foreach (var dm in toexec)
                    {
                        it.DelayedMessages.Remove(dm);
                        lock (_toReceive)
                        {
                            _toReceive.Enqueue(dm.rmsg);
                        }
                    }
                    lock (_toReceive)
                    {
                        var l = _toReceive.ToList();
                        l.Sort((m0, m1) => m0.Msg.MessageID.CompareTo(m1.Msg.MessageID));
                        _toReceive = new Queue<ReceivedMessage>(l);
                    }
                }
            }
            CheckSentMessages();
        }
        protected void Echo(Connection con, Action<SentMessage> failhandle = null, int timeout = DEFAULT_TIMEOUT, int retries = 0)
        {
            var msg = Message.BasicMessage(con.GenerateMessageID(), ID, con.ID, (int)MessageType.MSG_ECHO, true, false);
            QueueSendMessage(msg,
            (sm) =>
            {
                LogEvent($"Failed to echo at {sm.Msg.ReceiverID}", Severity.WARNING);
                if (failhandle != null)
                    failhandle(sm);
            },
            (sm) =>
            {
                #if LOG_ECHOES
                LogEvent($"Echo from {sm.Msg.ReceiverID} tm: {(DateTime.Now - sm.Time).Milliseconds}ms", Severity.INFO);
                #endif
            }, timeout, retries);
        }
        protected void PotentialConnectionFailureOperation(Connection con, System.Action operation)
        {
            try
            {
                operation();
            }
            catch (NetConnectionTimeoutException)
            {
                DisconnectConnection(con, DisconnectReason.TIMEOUT, true);
            }
            catch (NetConnectionInsaneException)
            {
                DisconnectConnection(con, DisconnectReason.INSANITY, true);
            }
            catch (NetConnectionDDOSException)
            {
                bool b = false;
                #if DEBUG
                b = true;
                #endif
                DisconnectConnection(con, DisconnectReason.DDOS, b);
            }
            catch (NetConnectionBrokenException)
            {
                DisconnectConnection(con, DisconnectReason.INSANITY, true);
            }
        }
        bool MaybeDelay(ReceivedMessage rmsg, Connection con)
        {
            if (con.LastID > 1 && con.LastID + 1 < rmsg.Msg.MessageID)
            {
                int oi = con.DelayedMessages.FindIndex(it => it.rmsg.Msg.MessageID == rmsg.Msg.MessageID);
                if (oi != -1)
                {
                    con.DelayedMessages.RemoveAt(oi);
                    return false;
                }
                var df = DateTime.Now - rmsg.Msg.TimeSent;
                if (df.TotalMilliseconds > MAX_EXTRA_DELAY || df.TotalMilliseconds < 0)
                    return false;
                int delay = MAX_EXTRA_DELAY - (int)df.TotalMilliseconds;
                var dt = DateTime.Now + TimeSpan.FromMilliseconds(delay);
                foreach (var it in con.DelayedMessages)
                {
                    if (it.rmsg.Msg.MessageID > rmsg.Msg.MessageID && dt.Ticks > it.rtime.Ticks)
                    {
                        dt = it.rtime - TimeSpan.FromMilliseconds(1);
                    }
                    else if (it.rmsg.Msg.MessageID < rmsg.Msg.MessageID && dt.Ticks < it.rtime.Ticks)
                    {
                        dt = it.rtime + TimeSpan.FromMilliseconds(1);
                    }
                }
                con.DelayedMessages.Add((rmsg, dt));
                rmsg.PreValidated = true;
                rmsg.Delayed = true;
                LogEvent($"Delayed a message by {((dt - DateTime.Now).TotalMilliseconds)}ms. ID: {rmsg.Msg.MessageID} FROM:{rmsg.Msg.SenderID}",
                    Severity.INFO);
                PotentialConnectionFailureOperation(con, () => con.NoteDeayed());
                return true;
            }
            return false;
        }
        void MaybeConfirm(ReceivedMessage rmsg, Connection con, MessageReaction reaction)
        {
            var msg = rmsg.Msg;
            if (!msg.RequestConfirmation)
                return;
            if(con == null && msg.SenderID != ID_UNKNOWN)
            {
                LogEvent("Attempting to solicit a confirmation from a connection that does not exist, but has an ID other than ID_UNKNOWN", Severity.WARNING);
                return;
            }
            if (msg.MType == (int)MessageType.MSG_CONFIRM)
            {
                LogEvent("Potential loop detected and avoided (RequestConfirnmation) on MSG_CONFIRM). " +
                    MessageInfo(msg), Severity.WARNING);
                return;
            }
            long id = MSG_ID_NO_CONNECTION;
            if (con != null)
                id = con.GenerateMessageID();
            var nmsg = Message.BasicMessage(id, ID, rmsg.Msg.SenderID, (int)MessageType.MSG_CONFIRM, con != null, false);
            nmsg.Data = SerializeStruct(new MConfirm()
            {
                MessageID = rmsg.Msg.MessageID,
                TimeReceived = rmsg.Time,
                reaction = reaction,
            });
            var sm = new SentMessage()
            {
                RequestReturn = false,
                Msg = nmsg,
                Time = DateTime.Now,
                LastTime = DateTime.Now,
                Endpoint = (con == null) ? rmsg.Endpoint : con.Endpoint,
            };
            QueueSendMessage(sm);
        }
        void HandleReceived(ReceivedMessage rmsg)
        {
            var msg = rmsg.Msg;
            var con = ConnectionsByID.GetValueOrDefault(msg.SenderID, null);
            MessageReaction react = MessageReaction.ACCEPTED;
            if (!rmsg.PreValidated)
            {
                if (con != null && rmsg.Msg.MessageID <= con.LastID && con.TotalReceivedFrom > 0 && rmsg.Msg.MessageID > 0)
                {
                    LogEvent($"Discarded a message from the past. ID: {rmsg.Msg.MessageID}, FROM: {rmsg.Msg.SenderID}",
                        Severity.WARNING);
                    react = MessageReaction.DISCARDED;
                }
                else
                    react = CheckValid(rmsg.Endpoint, msg);
            }
            if (react == MessageReaction.ACCEPTED)
            {
                if (con != null)
                {
                    if (MaybeDelay(rmsg, con))
                    {
                        con.Touch();
                        return;
                    }
                    var before = con.DelayedMessages.FindAll(it => it.rmsg.Msg.MessageID < msg.MessageID);
                    before.Sort((m0, m1) => m0.rmsg.Msg.MessageID.CompareTo(m1.rmsg.Msg.MessageID));
                    foreach (var it in before)
                    {
                        con.DelayedMessages.Remove(it);
                    }
                    foreach (var it in before)
                    {
                        HandleReceived(it.rmsg);
                    }
                }
                react = CheckAccepted(rmsg.Endpoint, msg, con);
            }
            if (react == MessageReaction.ACCEPTED)
            {
                react = ProcessMessage(rmsg.Endpoint, msg, con);
                if (react == MessageReaction.REJECTED || react == MessageReaction.ACCEPTED)
                {
                    if (_handles.ContainsKey(msg.MType))
                    {
                        _handles[msg.MType].ForEach(it => it(msg));
                        react = MessageReaction.ACCEPTED;
                    }
                }
                con = ConnectionsByID.GetValueOrDefault(msg.SenderID, null);
            }
            if (con != null)
            {
                PotentialConnectionFailureOperation(con, () => con.NoteMessage(rmsg, react, rmsg.Time - msg.TimeSent, rmsg.Delayed));
            }
            if (con == null || !con.Disconnected)
            {
                MaybeConfirm(rmsg, con, react);
            }
            #if LOG_EVERYTHING
            if(react != MessageReaction.ACCEPTED)
            {
                LogEvent($"Message {react}. " + MessageInfo(rmsg.Msg), Severity.WARNING);
            }
            #endif
        }
        protected void DisconnectAll(bool notify)
        {
            foreach(var it in Connections.ToList())
            {
                DisconnectConnection(it, DisconnectReason.END, false);
            }
        }
        void MainLoop()
        {
            while (!_shutdown)
            {
                UpdateConnections();
                ReceivedMessage rmsg;
                int count = 0;
                lock (_toReceive)
                {
                    count = _toReceive.Count;
                }
                if (count == 0)
                {
                    _recvEvent.WaitOne(1);
                    continue;
                }
                lock (_toReceive)
                {
                    rmsg = _toReceive.Dequeue();
                }
                HandleReceived(rmsg);
            }
        }
        protected void LogEvent(string msg, Severity s)
        {
            lock (_logMX)
            {
                OnLog(msg, s);
            }
            #if ASSERT_NET_WARNINGS
            Assert(!(s == Severity.WARNING));
            #endif
            #if ASSERT_NET_ERRORS
            Assert(!(s == Severity.ERROR));
            #endif
            #if ASSERT_NET_FATALS
            Assert(!(s == Severity.FAIL));
            #endif
        }
        public void AddHandle(int id, Action<Message> f)
        {
            if (_handles.ContainsKey(id))
                _handles[id].Add(f);
            else
                _handles.Add(id, new List<Action<Message>> { f });
        }
        public void AddHandles(int minid, int maxid, Action<Message> f)
        {
            RepeatN(maxid - minid, i => AddHandle(i + minid, f));
        }
        protected MessageReaction CheckValid(IPEndPoint source, Message msg)
        {
            if (msg.MType == (int)MessageType.ERR)
            {
                LogEvent($"NULL message received from {msg.SenderID}", Severity.ERROR);
                return MessageReaction.ERROROUS;
            }
            if (msg.Encrypted == false && msg.MType != (int)MessageType.MSG_HAND_SHAKE && msg.MType != (int)MessageType.MSG_CONFIRM)
            {
                LogEvent($"Unencrypted message of type {msg.MType} received from {msg.SenderID}", Severity.ERROR);
                return MessageReaction.ERROROUS;
            }
            return MessageReaction.ACCEPTED;
        }
        protected virtual MessageReaction CheckAccepted(IPEndPoint source, Message msg, Connection con)
        {
            if (!MatchIDs(ID, msg.ReceiverID))
                return MessageReaction.REJECTED;
            return MessageReaction.ACCEPTED;
        }
        protected virtual MessageReaction ProcessMessage(IPEndPoint source, Message msg, Connection con)
        {
            try
            {
                switch ((MessageType)msg.MType)
                {
                    case MessageType.MSG_CONFIRM:
                        {
                            var m = DeserializeStruct<MConfirm>(msg.Data);
                            SentMessage smsg = null;
                            lock (_sentMessages)
                            {
                                int indx = _sentMessages.FindIndex(it => it.ID == m.MessageID 
                                    && (it.Msg.ReceiverID == msg.SenderID || it.Msg.ReceiverID == ID_UNKNOWN));
                                if (indx != -1)
                                {
                                    smsg = _sentMessages[indx];
                                }
                                else
                                {
                                    LogEvent("Received a confirnmation to a message that did not request one "
                                    + MessageInfo(msg), Severity.WARNING);
                                    return MessageReaction.DISCARDED;
                                }
                            }
                            if (smsg != null)
                            {
                                if(m.reaction != MessageReaction.ACCEPTED)
                                {
                                    TryAgainOrFail(smsg);
                                    if (con != null)
                                    {
                                        con.NoteMessageLost();
                                    }
                                }
                                else 
                                {
                                    if (smsg.OnSuccess != null)
                                        smsg.OnSuccess(smsg);
                                    lock (_sentMessages)
                                    {
                                        _sentMessages.Remove(smsg);
                                    }
                                    if (con != null)
                                    {
                                        con.NoteMessageConfirmed();
                                    }
                                }
                            }
                            else
                            {
                                LogEvent($"Received a confirnmation for a forgotten message: {m.MessageID} from {msg.SenderID}",
                                    Severity.WARNING);
                                return MessageReaction.DISCARDED;
                            }
                            break;
                        }
                    case MessageType.MSG_HAND_SHAKE:
                        {
                            var m = DeserializeStruct<MHandshake>(msg.Data);
                            if (msg.IsResponse)
                            {
                                if (Connections.Count > 0 || _partialConnection == null)
                                {
                                    LogEvent($"Received a response to a non-existent handshake from {source}", Severity.ERROR);
                                    return MessageReaction.ERROROUS;
                                }
                                if (!CompareEndpoints(_partialConnection.Endpoint, source))
                                {
                                    LogEvent($"Received a handshake response from the wrong endpoint ({source} instead of {_partialConnection.Endpoint})",
                                         Severity.ERROR);
                                    return MessageReaction.ERROROUS;
                                }
                                this.ID = m.ReceiverID;
                                FinalizeConnection(source, m.SenderID, m.SenderPublicKey, _partialConnection.PrivateKey);
                                _partialConnection = null;
                                msg.SenderID = m.SenderID;
                                msg.ReceiverID = m.ReceiverID;
                            }
                            else
                            {
                                long id = GeneratePeerID();
                                (string PublicKey, string PrivateKey) = GenerateKeyPair();
                                var ncon = FinalizeConnection(source, id, m.SenderPublicKey, PrivateKey);
                                Message rmsg = Message.BasicMessage(ncon.GenerateMessageID(), ID, id,
                                    (int)MessageType.MSG_HAND_SHAKE, false, true);
                                var rm = new MHandshake()
                                {
                                    Accepted = true,
                                    SenderID = ID,
                                    ReceiverID = id,
                                    SenderPublicKey = PublicKey,
                                };
                                rmsg.Data = SerializeStruct(rm);
                                QueueSendMessage(rmsg, (SentMessage s) =>
                                {
                                    DisconnectConnection(ncon, DisconnectReason.TIMEOUT, true);
                                }, DEFAULT_TIMEOUT, IMPORTANT_RETRIES);
                            }
                            break;
                        }
                    case MessageType.MSG_ECHO:
                        {
                            break;
                        }
                    case MessageType.MSG_DISCONNECT:
                        {
                            RemoveConnection(con);
                            break;
                        }
                    default:
                        return MessageReaction.REJECTED;
                }
            }
            catch (DeserializationFailedException)
            {
                return MessageReaction.ERROROUS;
            }
            return MessageReaction.ACCEPTED;
        }
        protected virtual void AddConnection(Connection con)
        {
            lock (Connections)
            lock (ConnectionsByID)
            {
                Assert(!ConnectionsByID.ContainsKey(con.ID), "Attempting to add a connection that already exists.");
                ConnectionsByID.Add(con.ID, con);
                Connections.Add(con);
            }
        }
        protected virtual Connection FinalizeConnection(IPEndPoint endpoint, long ID, string publickey, string privatekey)
        {
            Connection con = new Connection(ID, endpoint.Address, (ushort)endpoint.Port, publickey, privatekey);
            AddConnection(con);
            return con;
        }
        protected void RemoveConnection(Connection con)
        {
            LogEvent($"Removing connection ID: {con.ID}", Severity.INFO);
            con.Disconnected = true;
            lock (_receiveProcessMX)
            {
                if (ConnectionsByID.ContainsKey(con.ID))
                    ConnectionsByID.Remove(con.ID);
                if (Connections.Contains(con))
                    Connections.Remove(con);
                lock (_toReceive)
                {
                    var l = _toReceive.ToList();
                    foreach (var it in l.ToList())
                    {
                        if (it.Msg.SenderID == con.ID)
                            l.Remove(it);
                    }

                    if (l.Count != _toReceive.Count)
                    {
                        _toReceive.Clear();
                        foreach (var it in l)
                        {
                            _toReceive.Enqueue(it);
                        }
                    }
                }
            }
        }
        void TryAgainOrFail(SentMessage sm, bool noretries= false)
        {
            if(sm.Retries == 0 || noretries)
            {
                if(sm.OnFailure != null)
                    sm.OnFailure(sm);
                lock(_sentMessages)
                {
                    _sentMessages.Remove(sm);
                }
            }
            else 
            {
                sm.Retries--;
                sm.FailedAtLeastOnce = true;
                sm.LastTime = DateTime.Now;
                QueueSendMessage(sm);
            }
        }
        void CheckSentMessages()
        {
            lock (_sentMessages)
            {
                var nsm = _sentMessages.ToList();
                foreach (var smsg in _sentMessages)
                {
                    if((DateTime.Now-smsg.LastTime).TotalMilliseconds >= smsg.Timeout)
                    {
                        TryAgainOrFail(smsg, (smsg.Msg.ReceiverID != ID_UNKNOWN && !Connections.Any(it => MatchIDs(it.ID, smsg.Msg.ReceiverID))));
                    }
                }
                _sentMessages = nsm;
            }
        }
        protected virtual void DisconnectConnection(Connection con, DisconnectReason reason, bool notify)
        {
            if (con.Disconnected)
                return;
            if(notify)
            {
                var msg = Message.BasicMessage(con.GenerateMessageID(), ID, con.ID, (int)MessageType.MSG_DISCONNECT, true, false);
                msg.Data = SerializeStruct(new MDisconnect() { Reason = reason });
                QueueSendMessage(con.Endpoint, msg, false);
            }
            RemoveConnection(con);
            CheckSentMessages();
        }
        protected Message BasicMessage(Connection target, int MType, bool response = false, byte[] dt = null)
        {
            var m = Message.BasicMessage(target.GenerateMessageID(), ID, target.ID, MType, true, response);
            if (dt != null)
                m.Data = dt;
            return m;
        }
        protected Message BasicMessage<T>(Connection target, int MType, bool response, T msg) where T : struct
        {
            return BasicMessage(target, MType, response, SerializeStruct<T>(msg));
        }
        protected virtual bool MatchIDs(long MyID, long TargetID)
        {
            if(MyID == ID_NULL)
                return true;
            if (TargetID == ID_UNKNOWN)
                return true;
            if (TargetID == ID_ALL && MyID != ID_NULL)
                return true;
            if (TargetID == ID_NULL)
                return false;
            return MyID == TargetID
                || (AlternativeAddresses.ContainsKey(TargetID) && MatchIDs(MyID, AlternativeAddresses[TargetID]))
                || (AlternativeAddresses.ContainsKey(MyID) && MatchIDs(AlternativeAddresses[MyID], TargetID));
        }
        protected void FailureShutdown()
        {
            _shutdown = true;
        }
        protected abstract void OnFailure(NetException ex);
        protected abstract void OnLog(string msg, Severity s);
        public bool Running { get; protected set; } = false;
        public virtual void Start()
        {
            Assert(!Running, "Already running");
            _receiveThread.Start();
            _sendThread.Start();
            _mainThread.Start();
            Running = true;
        }
        public void Stop()
        {
            Assert(Running, "Already stopped");
            _shutdown = true;
            _receiveThread.Join();
            _sendThread.Join();
            _mainThread.Join();
            Running = false;
        }
        public void ConnectToServer(IPEndPoint endpoint)
        {
            lock (_sentMessages)
            {
                Assert(Connections.Count == 0);
            }
            lock (_sentMessages)
            {
                Assert(_sentMessages.Count == 0);
            }
            if(DefaultEndpoint != null)
            {
                lock (DefaultEndpoint)
                {
                    DefaultEndpoint = endpoint;
                }
            }
            else 
                DefaultEndpoint = endpoint;
            var msg = Message.BasicMessage(MSG_ID_NO_CONNECTION, ID_UNKNOWN, ID_UNKNOWN, (int)MessageType.MSG_HAND_SHAKE, false, false);
            var pair = GenerateKeyPair();
            _partialConnection = new PartialConnection();
            _partialConnection.Endpoint = endpoint;
            _partialConnection.PrivateKey = pair.PrivateKey;
            msg.Data = SerializeStruct(new MHandshake()
            {
                SenderID = ID_UNKNOWN,
                ReceiverID = ID_UNKNOWN,
                SenderPublicKey = pair.PublicKey,
            });

            QueueSendMessage(msg, (sm) =>
            {
                _partialConnection = null;
                lock (DefaultEndpoint)
                {
                    DefaultEndpoint = null;
                }
                throw new NetFailedToConnectToServerException();
            });
        }
        public void ConnectToServer()
        {
            Assert(_partialConnection == null);
            lock (DefaultEndpoint)
            {
                Assert(DefaultEndpoint != null);
                ConnectToServer(DefaultEndpoint);
            }
        }
        public Peer(ushort port, bool start = false)
        {
            this.Port = port;
            this.ID = ID_NULL;
            _sender = new UdpClient();
            _sender.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _sender.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            _receiver = new UdpClient();
            _receiver.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _receiver.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            _receiveThread = new System.Threading.Thread(ReceiveLoop);
            _sendThread = new System.Threading.Thread(SendLoop);
            _mainThread = new System.Threading.Thread(MainLoop);

            _receiveThread.Name = "Receive_" + this.GetType().Name;
            _sendThread.Name = "Send_" + this.GetType().Name;
            _mainThread.Name = "Main_" + this.GetType().Name;


            if (start)
                Start();
        }
    }
}
