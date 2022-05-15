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
    public class RelayClient : RelayNode
    {
        public enum RCStatus
        {
            DISCONNECTED,
            CONNECTED,
            LOGGED_IN,
        }
        public RCStatus Status  {get; protected set;} = RCStatus.DISCONNECTED;
        RelayConnection _relay;
        AutoResetEvent _ev = new AutoResetEvent(false);
        public bool IsAdmin {get; protected set;} = false;
        public Action<long, PackedRelayedMessage> OnRelayedReceived = (id, drm) => {};
        public override void OnReceive(IPEndPoint source, RelayMessage message)
        {
            var con = Connections.Find(it => it.ID == message.SenderID);
            if(con == null)
            {
                OnReceivedMalformed(source);
                return;
            }
            DeserializedRelayMessage msg = null;
            try
            {
                msg = DeserializeMessage(message, null);
            }
            catch(NetAssertionException)
            {
                OnReceivedMalformed(con.End);
            }
            if(msg.data is ResendMessage rm)
            {
                Resend(con, rm);
            }
            else if(msg.data is ConnectionResponse cr)
            {
                this.ID = cr.AssignedID;
                this._relay.ClientPubKey = cr.pubkey;
                if(cr.allowed)
                {
                    Status = RCStatus.CONNECTED;
                }
                _ev.Set();
            }
            else if(msg.data is LoginResponse lr)
            {
                if(lr.success)
                {
                    Status = RCStatus.LOGGED_IN;
                    IsAdmin = lr.admin;
                }
                _ev.Set();
            }
            else if(msg.data is PackedRelayedMessage prm)
            {
                OnRelayedReceived(message.SenderID, prm);
            }
            else
            {
                OnReceivedMalformed(source, msg.index);
            }
        }
        public void SendRelayed(RelayConnection con, byte[] data)
        {
            Assert(con != null);
            Assert(data != null);
            Send(con, RelayMessageType.RELAY, new PackedRelayedMessage()
            {
                data = new VArray128(){Data = data},
            });
        }
        public void SendRelayed(long conID, byte[] data)
        {
            SendRelayed(Connections.Find(it => it.ID == conID), data);
        }
        public override void OnReceivedMalformed(IPEndPoint source, long index = -1)
        {
            throw new NotImplementedException();
        }
        public async Task<bool> TryConnect()
        {
            Assert(Status == RCStatus.DISCONNECTED);
            return await QueueUserWorkItemAsync<bool>(() => 
            {
                var rm = base.SerializeMessage(_relay, 0, ID_NULL, RelayMessageType.CONNECTION_REQUEST, new ConnectionRequest()
                {
                    pubkey = _relay.ServerPubKey,
                }, false);
                Send(_relay.End, rm);
                if(!_ev.WaitOne(1000))
                {
                    throw new NetConnectionTimeoutException("timed out trying to connect");
                }
                return Status == RCStatus.CONNECTED;
            });
        }
        public async Task<bool> LogIn(string password, bool admin, string admin_password)
        {
            Assert(Status == RCStatus.CONNECTED);
            return await QueueUserWorkItemAsync<bool>(() => 
            {

                Send(_relay, RelayMessageType.LOGIN_REQUEST, new LoginRequest()
                {
                    password = password,
                    admin = admin,
                    admin_password = (admin) ? admin_password : "",
                });
                if(!_ev.WaitOne(1000))
                {
                    Status = RCStatus.DISCONNECTED;
                    throw new NetConnectionTimeoutException("timed out trying to login");
                }
                return Status == RCStatus.LOGGED_IN;
            });
        }
        public async Task<bool> LogInAdmin(string password, string admin_password)
        {
            return await LogIn(password, true, admin_password);
        }
        public async Task<bool> LogIn(string password)
        {
            return await LogIn(password, false, null);
        }
        public RelayClient(IPEndPoint server) : base(server, 0)
        {
            var keys = GenerateKeyPair();
            var rc = new RelayConnection()
            {
                End = server,
                ServerPrivKey = keys.privkey,
                ServerPubKey = keys.pubkey,
                ID = ID_RELAY,
                IsAdmin = false,
                LastReceivedIndex = -1,
                LastSentIndex = 0,
            };
            _relay = rc;
            AddConnection(rc);
        }
    }
}