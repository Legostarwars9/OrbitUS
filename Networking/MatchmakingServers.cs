using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Orbit_Us
{
    public sealed class MatchmakingServer
    {
        public string Address {get;}
        public int Port {get;}
        private MatchmakingServer(string address,int port){Address=address;Port=port;}
        public override string ToString()=>Address+":"+Port;

        public static List<MatchmakingServer> ParseList(string list)
        {
            var result=new List<MatchmakingServer>();
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(string raw in (list??"").Split(new[]{',',';'},StringSplitOptions.RemoveEmptyEntries))
            {
                string value=raw.Trim(),address=value;int port=3478;
                int colon=value.LastIndexOf(':');
                if(colon>=0)
                {
                    address=value.Substring(0,colon).Trim();
                    if(!int.TryParse(value.Substring(colon+1),out port))throw InvalidEntry();
                }
                address=address.TrimEnd('.');
                UriHostNameType type=Uri.CheckHostName(address);
                if((type!=UriHostNameType.Dns && type!=UriHostNameType.IPv4) || port<1 || port>65535)throw InvalidEntry();
                var server=new MatchmakingServer(address,port);
                if(seen.Add(server.ToString()))result.Add(server);
                if(result.Count>16)throw new ArgumentException("Matchmaking.Servers supports at most 16 servers.");
            }
            if(result.Count==0)throw new ArgumentException("Add at least one hostname:UDPport entry to Matchmaking.Servers in the config.");
            return result;
        }
        private static ArgumentException InvalidEntry()=>new ArgumentException("Invalid Matchmaking.Servers entry. Use comma-separated IPv4 addresses or hostnames with :UDPport, without a URL scheme.");
    }

    public sealed class MatchmakingServerLatency
    {
        public MatchmakingServer Server {get;internal set;}
        public IPEndPoint Endpoint {get;internal set;}
        public double Milliseconds {get;internal set;}
    }

    public static class MatchmakingServers
    {
        public static async Task<List<MatchmakingServerLatency>> RankAsync(IReadOnlyList<MatchmakingServer> servers,int timeoutMs,CancellationToken cancellation)
        {
            if(timeoutMs<1)throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            var results=await Task.WhenAll(servers.Select(s=>ProbeAsync(s,timeoutMs,cancellation))).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            return results.Where(r=>r!=null).OrderBy(r=>r.Milliseconds).ToList();
        }

        private static async Task<MatchmakingServerLatency> ProbeAsync(MatchmakingServer server,int timeoutMs,CancellationToken cancellation)
        {
            IPAddress ip;
            if(!IPAddress.TryParse(server.Address,out ip))
            {
                Task<IPAddress[]> lookup;
                try{lookup=Dns.GetHostAddressesAsync(server.Address);}
                catch(SocketException){return null;}
                catch(ArgumentException){return null;}
                if(await Task.WhenAny(lookup,Task.Delay(timeoutMs,cancellation)).ConfigureAwait(false)!=lookup)
                {
                    _=lookup.ContinueWith(t=>{var ignored=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
                    cancellation.ThrowIfCancellationRequested();return null;
                }
                try{ip=(await lookup.ConfigureAwait(false)).FirstOrDefault(a=>a.AddressFamily==AddressFamily.InterNetwork);}
                catch(SocketException){return null;}
            }
            if(ip==null || ip.AddressFamily!=AddressFamily.InterNetwork)return null;
            var endpoint=new IPEndPoint(ip,server.Port);
            return await Task.Run(()=>Probe(server,endpoint,timeoutMs,cancellation),cancellation).ConfigureAwait(false);
        }

        private static MatchmakingServerLatency Probe(MatchmakingServer server,IPEndPoint endpoint,int timeoutMs,CancellationToken cancellation)
        {
            var samples=new List<double>();
            for(int round=0;round<3;round++)
            {
                cancellation.ThrowIfCancellationRequested();
                using(var socket=new UdpClient(AddressFamily.InterNetwork))
                using(cancellation.Register(()=>socket.Close()))
                {
                    // Standard STUN Binding, supported on the rendezvous UDP port.
                    byte[] request=new byte[20];request[1]=1;
                    request[4]=0x21;request[5]=0x12;request[6]=0xA4;request[7]=0x42;
                    Buffer.BlockCopy(RoomProtocol.Hex(RoomProtocol.RandomHex(12),12),0,request,8,12);
                    try
                    {
                        socket.Connect(endpoint);var timer=Stopwatch.StartNew();socket.Send(request,request.Length);
                        while(timer.ElapsedMilliseconds<timeoutMs)
                        {
                            socket.Client.ReceiveTimeout=Math.Max(1,timeoutMs-(int)timer.ElapsedMilliseconds);
                            IPEndPoint sender=null;byte[] reply=socket.Receive(ref sender);
                            if(!sender.Equals(endpoint) || !ValidStunReply(reply,request))continue;
                            samples.Add(timer.Elapsed.TotalMilliseconds);break;
                        }
                    }
                    catch(SocketException){cancellation.ThrowIfCancellationRequested();}
                    catch(ObjectDisposedException){cancellation.ThrowIfCancellationRequested();}
                }
            }
            if(samples.Count==0)return null;
            samples.Sort();int middle=samples.Count/2;
            double median=(samples[(samples.Count-1)/2]+samples[middle])/2;
            return new MatchmakingServerLatency {Server=server,Endpoint=endpoint,Milliseconds=median};
        }

        private static bool ValidStunReply(byte[] reply,byte[] request)
        {
            if(reply.Length<20 || reply[0]!=1 || reply[1]!=1)return false;
            int length=(reply[2]<<8)|reply[3];
            if(length%4!=0 || reply.Length!=20+length)return false;
            for(int i=4;i<20;i++)if(reply[i]!=request[i])return false;
            return true;
        }

        public static async Task<RoomTransport> ConnectAsync(IReadOnlyList<MatchmakingServer> servers,int probeTimeoutMs,int connectTimeoutMs,
            bool host,string code,string access,int gamePort,string username,CancellationToken cancellation)
        {
            if(connectTimeoutMs<1)throw new ArgumentOutOfRangeException(nameof(connectTimeoutMs));
            if(!host)code=RoomProtocol.NormalizeCode(code);
            var ranked=await RankAsync(servers,probeTimeoutMs,cancellation).ConfigureAwait(false);
            if(ranked.Count==0)throw new InvalidOperationException("No matchmaking servers responded. Check Matchmaking.Servers and UDP connectivity.");
            foreach(var candidate in ranked)
            {
                cancellation.ThrowIfCancellationRequested();
                var room=new RoomTransport(candidate.Endpoint,host,code,access,gamePort) {LocalUsername=username};
                bool selected=false;
                try
                {
                    var timer=Stopwatch.StartNew();
                    while(timer.ElapsedMilliseconds<connectTimeoutMs)
                    {
                        cancellation.ThrowIfCancellationRequested();room.Tick();
                        if(room.Registered)
                        {
                            cancellation.ThrowIfCancellationRequested();selected=true;
                            Console.WriteLine("[Orbit-Us] Matchmaking selected "+candidate.Server+" ("+candidate.Milliseconds.ToString("0.0",System.Globalization.CultureInfo.InvariantCulture)+" ms UDP).");
                            return room;
                        }
                        if(room.EndedReason.Length>0)break;
                        await Task.Delay(25,cancellation).ConfigureAwait(false);
                    }
                }
                finally{if(!selected)room.Dispose();}
            }
            throw new InvalidOperationException(host?
                "Could not create a room on the available matchmaking servers. Check Matchmaking settings and AccessKey.":
                "Room code could not be found on the available matchmaking servers. Check the code, server list and AccessKey.");
        }
    }
}
