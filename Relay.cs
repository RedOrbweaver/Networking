using System.Net.Mail;
using System.Threading.Tasks.Dataflow;
using System.Xml;
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

using Newtonsoft.Json;
using static System.Math;

using static Utils;

public static partial class Networking
{
    public class Relay : RelayNode
    {
        string _password;
        string _adminPassword;
        long _lastID = 1;
        Dictionary<IPEndPoint, RelayConnection> ConnectionsByEndpoints = new Dictionary<IPEndPoint, RelayConnection>();
        Dictionary<long, RelayConnection> ConnectionsByID = new Dictionary<long, RelayConnection>();
        RelayConnection _admin;
        public bool AcceptNewLogins = true;
        public List<RelayConnection> LoggedIn = new List<RelayConnection>();
        public Func<RelayConnection, bool> CanLogIn = rc => true; 
        public Action<RelayConnection> OnNewLogIn = rc => {};
        void InformLogIn(RelayConnection newcon, RelayConnection con)
        {
            Send<NewPeerMessage>(ID_RELAY, MSG_INDEX_IGNORE, con, RelayMessageType.NEW_PEER, new NewPeerMessage()
            {
                ID=newcon.ID,
                IsAdmin=newcon.IsAdmin,
            });
        }
        void InformLogIn(RelayConnection newcon)
        {
            Assert(newcon.LoggedIn);
            foreach(var it in Connections.FindAll(it => it.LoggedIn))
            {
                InformLogIn(newcon, it);
            }
        }
        public override void AddConnection(RelayConnection conn)
        {
            base.AddConnection(conn);
            ConnectionsByEndpoints.Add(conn.End, conn);
            ConnectionsByID.Add(conn.ID, conn);
        }
        void LogInUser(RelayConnection rc, bool isadmin)
        {
            if(isadmin)
            {
                _admin = rc; 
                rc.IsAdmin = true;
            }
            rc.LoggedIn = true;
            LoggedIn.Add(rc);
            InformLogIn(rc);
            Connections.FindAll(it => it.LoggedIn).ForEach(it => InformLogIn(it, rc));
            OnNewLogIn(rc);
        }
        void ProcessMessage(RelayConnection con, RelayMessage relaymsg)
        {
            DeserializedRelayMessage msg = null;
            try
            {
                msg = DeserializeMessage(relaymsg, null);
            }
            catch(NetAssertionException)
            {
                NoteReceiveError(con.End);
            }
            if(msg.data is ResendMessagesMessage rm)
            {
                Resend(con, rm);
            }
            else if(msg.data is LoginRequest lr)
            {
                if(!AcceptNewLogins || lr.password != _password || 
                    (lr.admin && lr.admin_password != _adminPassword) || 
                    (lr.admin && _admin != null) || 
                    !CanLogIn(con))
                {
                    Send(con, 0, RelayMessageType.LOGIN_RESPONSE, new LoginResponse()
                    {
                        success = false,
                        admin = false,
                    });
                }
                LogInUser(con, lr.admin);
                Send(con, 0, RelayMessageType.LOGIN_RESPONSE, new LoginResponse()
                {
                    success = true,
                    admin = lr.admin,
                });
            }
            else
            {
                NoteReceiveError(con.End, msg);
            }
        }
        public void RelayTo(long target, long source, RelayMessage rm)
        {
            if(!ConnectionsByID.ContainsKey(target))
            {
                NoteReceiveError(ConnectionsByID[rm.SenderID].End);
                return;
            }
            var tarcon =  ConnectionsByID[target];
            var sorcom = ConnectionsByID[rm.SenderID];
            rm.Message.Data = Encrypt(Decrypt(rm.Message.Data, sorcom.ServerPrivKey), tarcon.ClientPubKey);
            rm.SenderID = source;
            Send(tarcon.End, rm);
        }
        public override void OnReceive(IPEndPoint source, RelayMessage message)
        {
            if(ConnectionsByEndpoints.ContainsKey(source))
            {
                ProcessMessage(ConnectionsByEndpoints[source], message);
                return;
            }
            if(message.Encrypted)
            {
                NoteReceiveError(source);
                return;
            }
            if(message.ReceiverID != ID)
            {
                RelayTo(message.ReceiverID, ConnectionsByEndpoints[source].ID, message);
                return; 
            }
            DeserializedRelayMessage msg = null;
            try
            {
                msg = DeserializeMessage(message, null);
            }
            catch(NetAssertionException)
            {
                NoteReceiveError(source);
                return;
            }
            if(msg.data is ConnectionRequest conreq)
            {
                var keys = GenerateKeyPair();
                var rc = new RelayConnection()
                {
                    End = source,
                    ID = _lastID++,
                    IsAdmin = false,
                    LastReceivedIndex = msg.index,
                    LastSentIndex = 0,
                };
                AddConnection(rc);
                Send(rc, 0, RelayMessageType.CONNECTION_RESPONSE, new ConnectionResponse()
                {
                    allowed = true,
                    pubkey = rc.ServerPubKey,
                    AssignedID = rc.ID,
                });
            }
            else 
                NoteReceiveError(source);
        }
        public Relay(ushort port, string password, string admin_password) : base(new IPEndPoint(IPAddress.Any, 0), port)
        {
            this.ID = ID_RELAY;
            this._password = password;
            this._adminPassword = admin_password;
        }
    }
}