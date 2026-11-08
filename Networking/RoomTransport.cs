using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Threading.Tasks;

namespace Orbit_Us
{
    public sealed class RoomPeerInfo
    {
        public string Id,State,Name;
        public int PlayerId;
        public bool Queued;
    }
    // The public socket is shared by rendezvous, authenticated probes and game data.
    // Private loopback bridges preserve the existing authoritative UDP relay.
    public sealed class RoomTransport : IDisposable
    {
        private sealed class Peer
        {
            public string Id,Name="";
            public byte[] Key,Nonce;
            public ulong Outgoing;
            public readonly ReplayWindow Incoming=new ReplayWindow();
            public readonly List<IPEndPoint> Candidates=new List<IPEndPoint>();
            public IPEndPoint Endpoint;
            public DateTime LastProbe,NonceAt;
            public bool Approved,Rejected;
            public int PlayerId;
            public UdpClient Bridge;
        }
        private readonly object gate=new object();
        private readonly UdpClient socket,clientBridge;
        private readonly IPEndPoint server;
        private readonly string identity=RoomProtocol.RandomHex(16),auth=RoomProtocol.RandomHex(16),access;
        private readonly byte[] identityBytes,authBytes;
        private readonly bool host;
        private readonly int gamePort;
        private readonly Dictionary<string,Peer> peers=new Dictionary<string,Peer>();
        private readonly object[][] lan;
        private string code,cookie="",status="Contacting matchmaking server...",ended="";
        private long revision;
        private bool disposed,accepted,registrationRetryRequested,registered;
        private DateTime lastRegistration,lastSignal,created=DateTime.UtcNow;
        private IPEndPoint localClient;
        public bool IsHost=>host;
        public string LocalUsername {get;set;}="";
        public string Code {get{lock(gate)return code;}}
        public string Status {get{lock(gate)return status;}}
        public string EndedReason {get{lock(gate)return ended;}}
        public bool Accepted {get{lock(gate)return accepted;}}
        public bool Registered {get{lock(gate)return registered;}}
        public int LocalPort {get;}
        public static Task<RoomTransport> CreateAsync(string address,int signalingPort,bool host,string room,string access,int gamePort)
            =>Task.Run(()=>
            {
                IPAddress ip=Dns.GetHostAddresses(address).FirstOrDefault(a=>a.AddressFamily==AddressFamily.InterNetwork);
                if(ip==null)throw new InvalidOperationException("The matchmaking server needs an IPv4 address.");
                return new RoomTransport(new IPEndPoint(ip,signalingPort),host,room,access,gamePort);
            });
        public RoomTransport(IPEndPoint server,bool host,string room,string access,int gamePort)
        {
            this.server=server;this.host=host;code=host?"":RoomProtocol.NormalizeCode(room);this.access=access;this.gamePort=gamePort;
            identityBytes=RoomProtocol.Hex(identity,16);authBytes=RoomProtocol.Hex(auth,16);
            socket=new UdpClient(new IPEndPoint(IPAddress.Any,0));
            if(!host){clientBridge=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));LocalPort=((IPEndPoint)clientBridge.Client.LocalEndPoint).Port;}
            int publicPort=((IPEndPoint)socket.Client.LocalEndPoint).Port;
            var addresses=new List<object[]>();
            try
            {
                foreach(var ip in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if(ip.AddressFamily!=AddressFamily.InterNetwork)continue;
                    byte[] b=ip.GetAddressBytes();
                    if(b[0]==10 || (b[0]==172 && b[1]>=16 && b[1]<=31) || (b[0]==192 && b[1]==168))
                        addresses.Add(new object[]{ip.ToString(),publicPort});
                    if(addresses.Count==4)break;
                }
            }
            catch(SocketException) { }
            lan=addresses.ToArray();
            _=ReceivePublic();if(clientBridge!=null)_=ReceiveLocal();
        }
        public List<RoomPeerInfo> GetPeers()
        {
            lock(gate)return peers.Values.Select(p=>new RoomPeerInfo {Id=p.Id,PlayerId=p.PlayerId,Name=p.Name.Length>0?p.Name:p.Id.Substring(0,6),
                Queued=p.Endpoint!=null && !p.Approved && !p.Rejected,
                State=p.Rejected?"Declined":p.Endpoint==null?"Connecting":p.PlayerId>0?"Connected":p.Approved?"Joining":"Queued"}).ToList();
        }
        public void Approve(string id)
        {
            lock(gate)if(host && peers.TryGetValue(id,out Peer peer) && !peer.Rejected && peer.Endpoint!=null)
            {peer.Approved=true;SendPeer(peer,3,new byte[0],peer.Endpoint);}
        }
        public void Decline(string id)
        {
            lock(gate)if(host && peers.TryGetValue(id,out Peer peer))
            {peer.Rejected=true;CloseBridge(peer);if(peer.Endpoint!=null)SendPeer(peer,4,new byte[0],peer.Endpoint);}
        }
        public void Tick()
        {
            lock(gate)
            {
                if(disposed || ended.Length>0)return;
                DateTime now=DateTime.UtcNow;
                double sinceRegistration=(now-lastRegistration).TotalSeconds;
                if(sinceRegistration>=2 || (registrationRetryRequested && sinceRegistration>=0.25))
                {
                    registrationRetryRequested=false;lastRegistration=now;Register(host?"host":"join");
                }
                if((now-created).TotalSeconds>12 && lastSignal==default(DateTime))status="No reply from matchmaking server. Check its address and UDP port.";
                else if(lastSignal!=default(DateTime) && (now-lastSignal).TotalSeconds>10)status="Matchmaking connection interrupted; retrying...";
                foreach(var peer in peers.Values)
                {
                    if((now-peer.LastProbe).TotalSeconds<0.5)continue;peer.LastProbe=now;
                    if(peer.Rejected){if(peer.Endpoint!=null)SendPeer(peer,4,new byte[0],peer.Endpoint);continue;}
                    if(peer.Nonce==null || (now-peer.NonceAt).TotalSeconds>5)
                    {peer.Nonce=RoomProtocol.Hex(RoomProtocol.RandomHex(8),8);peer.NonceAt=now;}
                    byte[] name=System.Text.Encoding.UTF8.GetBytes(PlayerNames.Normalize(LocalUsername));
                    byte[] probe=new byte[8+name.Length];Buffer.BlockCopy(peer.Nonce,0,probe,0,8);Buffer.BlockCopy(name,0,probe,8,name.Length);
                    if(peer.Endpoint==null)
                        foreach(var candidate in peer.Candidates)SendPeer(peer,1,probe,candidate);
                    else
                    {
                        SendPeer(peer,1,probe,peer.Endpoint);
                        if(host && peer.Approved)SendPeer(peer,3,new byte[0],peer.Endpoint);
                    }
                    if(peer.Endpoint==null && (now-created).TotalSeconds>15)
                        status="Direct connection is blocked. Try a shared VPN and the Direct IP option.";
                }
            }
        }
        private void Register(string op)
        {
            var data=RoomProtocol.Request(new RoomMessage {Op=op,Room=code,Id=identity,Auth=auth,Cookie=cookie??"",Access=access,
                Turn=false,Lan=lan,RelayPort=0});
            SendPublic(data,server);
        }
        private void SendPublic(byte[] bytes,IPEndPoint endpoint)
        {try{socket.Send(bytes,bytes.Length,endpoint);}catch(SocketException){}catch(ObjectDisposedException){}}
        private void SendPeer(Peer peer,byte kind,byte[] payload,IPEndPoint endpoint)
            =>SendPublic(RoomProtocol.Frame(identityBytes,peer.Key,++peer.Outgoing,kind,payload),endpoint);
        private async Task ReceivePublic()
        {
            while(true)
            {
                UdpReceiveResult received;
                try{received=await socket.ReceiveAsync().ConfigureAwait(false);}
                catch(ObjectDisposedException){return;}catch(SocketException){lock(gate)if(disposed)return;continue;}
                lock(gate)
                {
                    if(disposed)return;
                    try
                    {
                        if(received.RemoteEndPoint.Equals(server))HandleSignal(received.Buffer);
                        else HandlePeer(received.Buffer,received.RemoteEndPoint);
                    }
                    catch(Exception ex) when(ex is System.IO.InvalidDataException || ex is SerializationException || ex is FormatException || ex is OverflowException || ex is ArgumentException || ex is SocketException || ex is ObjectDisposedException)
                    {
                        if(received.RemoteEndPoint.Equals(server))
                        {
                            string error="Could not read matchmaking reply ("+ex.GetType().Name+"). Retrying...";
                            if(status!=error)Console.WriteLine("[Orbit-Us] "+error);
                            status=error;
                        }
                        // Invalid peer datagrams do not change membership.
                    }
                }
            }
        }
        private void HandleSignal(byte[] bytes)
        {
            var message=RoomProtocol.ReadReply(bytes,authBytes,out bool challenge);if(message==null)return;
            if(challenge)
            {
                if(cookie!=message.Cookie)
                {
                    cookie=message.Cookie;registrationRetryRequested=true;
                    Console.WriteLine("[Orbit-Us] Rendezvous cookie received; retrying registration with the cookie.");
                }
                return;
            }
            if(message.Revision>0){if(message.Revision<=revision)return;revision=message.Revision;}
            lastSignal=DateTime.UtcNow;
            if(message.Op=="welcome")
            {
                string assigned=RoomProtocol.NormalizeCode(message.Room);
                if(code.Length>0 && code!=assigned)return;
                code=assigned;registered=true;status=host?"Share this code with your friends.":accepted?"Connected to host.":
                    peers.Values.Any(p=>p.Endpoint!=null)?"Waiting for host approval...":"Finding a path to the host...";
            }
            else if(message.Op=="peer")
            {
                RoomProtocol.Hex(message.Id,16);byte[] key=RoomProtocol.Hex(message.Key,32);
                if(message.Id==identity)return;
                if(!peers.TryGetValue(message.Id,out Peer peer))
                {
                    if(peers.Count>=16 || (!host && peers.Count>0))return;
                    peer=new Peer {Id=message.Id,Key=key};peers.Add(message.Id,peer);
                }
                if(!peer.Key.SequenceEqual(key))return;
                peer.Candidates.Clear();AddCandidate(peer,message.Endpoint,false);
                if(message.Lan!=null)foreach(var candidate in message.Lan.Take(4))AddCandidate(peer,candidate,true);
                if(peer.Endpoint!=null && !peer.Candidates.Contains(peer.Endpoint))peer.Endpoint=null;
            }
            else if(message.Op=="gone" && message.Id!=null && peers.TryGetValue(message.Id,out Peer gone))
            {CloseBridge(gone);peers.Remove(message.Id);}
            else if(message.Op=="error")
            {
                ended=message.Error=="host_left"?"The host closed the room.":"Matchmaking: "+message.Error;
                status=ended;
            }
        }
        private static void AddCandidate(Peer peer,object[] candidate,bool privateOnly)
        {
            if(candidate==null || candidate.Length!=2 || !(candidate[0] is string address) || !IPAddress.TryParse(address,out IPAddress ip) ||
                ip.AddressFamily!=AddressFamily.InterNetwork)return;
            int port=Convert.ToInt32(candidate[1],CultureInfo.InvariantCulture);if(port<1 || port>65535)return;
            byte[] b=ip.GetAddressBytes();bool privateIp=b[0]==10 || (b[0]==172 && b[1]>=16 && b[1]<=31) || (b[0]==192 && b[1]==168);
            if(privateOnly && !privateIp)return;
            if(b[0]==0 || b[0]>=224 || ip.Equals(IPAddress.Broadcast))return;
            var endpoint=new IPEndPoint(ip,port);if(!peer.Candidates.Contains(endpoint))peer.Candidates.Add(endpoint);
        }
        private void HandlePeer(byte[] bytes,IPEndPoint sender)
        {
            if(bytes.Length<RoomProtocol.Overhead)return;
            string id=BitConverter.ToString(bytes,4,16).Replace("-","").ToLowerInvariant();
            if(!peers.TryGetValue(id,out Peer peer) || !RoomProtocol.ReadFrame(bytes,peer.Key,peer.Incoming,out byte kind,out byte[] payload))return;
            if(kind==1 && payload.Length>=8 && payload.Length<=8+PlayerNames.MaxBytes)
            {
                peer.Name=PlayerNames.Normalize(System.Text.Encoding.UTF8.GetString(payload,8,payload.Length-8));
                SendPeer(peer,peer.Rejected?(byte)4:(byte)2,peer.Rejected?new byte[0]:payload.Take(8).ToArray(),sender);return;
            }
            if(kind==2 && peer.Nonce!=null && payload.SequenceEqual(peer.Nonce) && !peer.Rejected)
            {
                peer.Endpoint=sender;
                if(!host)status=accepted?"Connected to host.":"Waiting for host approval...";
                return;
            }
            // A signed rejection may arrive before the return-path probe completes.
            if(kind==4 && !host && payload.Length==0)
            {ended="The host declined your request or kicked you.";status=ended;return;}
            if(peer.Endpoint==null || !peer.Endpoint.Equals(sender))return;
            if(kind==3 && !host && payload.Length==0)
            {accepted=true;peer.Approved=true;status="Connected to host.";return;}
            if(kind==6 && payload.Length==0)
            {
                if(host){CloseBridge(peer);peer.Rejected=true;}
                else{ended="The host closed the room.";status=ended;}
                return;
            }
            if(kind!=5 || peer.Rejected || !peer.Approved || payload.Length<8)return;
            if(host)
            {
                NetworkPacket packet=PacketSerializer.Deserialize(payload);
                if(packet.Type==PacketType.PlayerConnect && (packet.Data.Length!=1 || packet.Data[0]!=0))return;
                if(peer.Bridge==null)
                {
                    if(packet.Type!=PacketType.PlayerConnect)return;
                    peer.Bridge=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));peer.Bridge.Connect(IPAddress.Loopback,gamePort);
                    _=ReceiveHostBridge(peer,peer.Bridge);
                }
                peer.Bridge.Send(payload,payload.Length);
            }
            else if(localClient!=null)clientBridge.Send(payload,payload.Length,localClient);
        }
        private async Task ReceiveLocal()
        {
            while(true)
            {
                UdpReceiveResult received;
                try{received=await clientBridge.ReceiveAsync().ConfigureAwait(false);}catch(ObjectDisposedException){return;}catch(SocketException){return;}
                lock(gate)
                {
                    if(disposed)return;
                    if(!IPAddress.IsLoopback(received.RemoteEndPoint.Address))continue;
                    if(localClient==null)
                    {
                        try{if(PacketSerializer.Deserialize(received.Buffer).Type!=PacketType.PlayerConnect)continue;}
                        catch(System.IO.InvalidDataException){continue;}
                        localClient=received.RemoteEndPoint;
                    }
                    if(!localClient.Equals(received.RemoteEndPoint))continue;
                    foreach(var peer in peers.Values)if(peer.Approved && peer.Endpoint!=null)SendPeer(peer,5,received.Buffer,peer.Endpoint);
                }
            }
        }
        private async Task ReceiveHostBridge(Peer peer,UdpClient bridge)
        {
            while(true)
            {
                UdpReceiveResult received;
                try{received=await bridge.ReceiveAsync().ConfigureAwait(false);}catch(ObjectDisposedException){return;}catch(SocketException){return;}
                lock(gate)
                {
                    if(disposed || !ReferenceEquals(peer.Bridge,bridge))return;
                    if(peer.Endpoint==null)continue;
                    try
                    {
                        var packet=PacketSerializer.Deserialize(received.Buffer);
                        if(packet.Type==PacketType.PlayerAssigned && packet.Data.Length==4)peer.PlayerId=BitConverter.ToInt32(packet.Data,0);
                        SendPeer(peer,5,received.Buffer,peer.Endpoint);
                        if(packet.Type==PacketType.PlayerKicked)
                        {peer.Rejected=true;CloseBridge(peer);SendPeer(peer,4,new byte[0],peer.Endpoint);return;}
                    }
                    catch(System.IO.InvalidDataException) { }
                }
            }
        }
        private static void CloseBridge(Peer peer)
        {
            if(peer.Bridge==null)return;
            byte[] leave=PacketSerializer.Serialize(new NetworkPacket {Type=PacketType.PlayerDisconnected,Data=new byte[0]});
            try{peer.Bridge.Send(leave,leave.Length);}catch(SocketException){}catch(ObjectDisposedException){}
            peer.Bridge.Close();peer.Bridge=null;peer.PlayerId=0;
        }
        public void Dispose()
        {
            lock(gate)
            {
                if(disposed)return;
                Register("leave");
                foreach(var peer in peers.Values)
                {if(peer.Endpoint!=null)SendPeer(peer,6,new byte[0],peer.Endpoint);CloseBridge(peer);}
                disposed=true;socket.Close();clientBridge?.Close();peers.Clear();
            }
        }
    }
}
