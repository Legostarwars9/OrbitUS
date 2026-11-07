using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace Orbit_Us
{
    [DataContract]
    internal sealed class RoomMessage
    {
        [DataMember(Name="op")] public string Op;
        [DataMember(Name="room",EmitDefaultValue=false)] public string Room;
        [DataMember(Name="id",EmitDefaultValue=false)] public string Id;
        [DataMember(Name="auth",EmitDefaultValue=false)] public string Auth;
        [DataMember(Name="cookie")] public string Cookie {get;set;}
        [DataMember(Name="access",EmitDefaultValue=false)] public string Access;
        [DataMember(Name="turn")] public bool Turn;
        [DataMember(Name="lan",EmitDefaultValue=false)] public object[][] Lan;
        [DataMember(Name="relay_port")] public int RelayPort;
    }
    [DataContract]
    internal sealed class RoomChallenge
    {
        [DataMember(Name="op")] public string Op {get;set;}
        [DataMember(Name="cookie")] public string Cookie {get;set;}
    }
    [DataContract]
    internal sealed class RoomReply
    {
        [DataMember(Name="op")] public string Op {get;set;}
        [DataMember(Name="room")] public string Room {get;set;}
        [DataMember(Name="id")] public string Id {get;set;}
        [DataMember(Name="key")] public string Key {get;set;}
        [DataMember(Name="cookie")] public string Cookie {get;set;}
        [DataMember(Name="error")] public string Error {get;set;}
        [DataMember(Name="revision")] public long Revision {get;set;}
        [DataMember(Name="endpoint")] public object[] Endpoint {get;set;}
        [DataMember(Name="lan")] public object[][] Lan {get;set;}
    }
    public sealed class ReplayWindow
    {
        private ulong highest,bits;
        public bool Accept(ulong sequence)
        {
            if(sequence==0)return false;
            if(sequence>highest)
            {
                ulong shift=sequence-highest;bits=shift>=64?1:(bits<<(int)shift)|1;highest=sequence;return true;
            }
            ulong age=highest-sequence;if(age>=64 || (bits&(1UL<<(int)age))!=0)return false;
            bits|=1UL<<(int)age;return true;
        }
    }
    public static class RoomProtocol
    {
        public const int HeaderLength=29,Overhead=61;
        private const int SignalPrefixLength=4,SignalSignatureLength=32;
        public static string RandomHex(int count)
        {
            byte[] bytes=new byte[count];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();
        }
        public static byte[] Hex(string text,int length)
        {
            if(text==null || text.Length!=length*2)throw new InvalidDataException("Invalid room identity/key.");
            byte[] result=new byte[length];
            for(int i=0;i<length;i++)result[i]=Convert.ToByte(text.Substring(i*2,2),16);
            return result;
        }
        public static string NormalizeCode(string code)
        {
            string value=(code??"").Trim().Replace("-","").ToUpperInvariant();
            if(value.Length!=10)throw new InvalidDataException("Room codes contain 10 letters/numbers.");
            foreach(char c in value)if("ABCDEFGHJKLMNPQRSTUVWXYZ23456789".IndexOf(c)<0)throw new InvalidDataException("Invalid room code.");
            return value;
        }
        internal static byte[] Request(RoomMessage message)
        {
            using(var stream=new MemoryStream())
            {
                stream.Write(Encoding.ASCII.GetBytes("OUS1"),0,4);
                new DataContractJsonSerializer(typeof(RoomMessage)).WriteObject(stream,message);
                byte[] data=stream.ToArray();if(data.Length>1400)throw new InvalidDataException("Room request too large.");return data;
            }
        }
        internal static RoomReply ReadReply(byte[] data,byte[] auth,out bool challenge)
        {
            challenge=false;if(data==null || data.Length<5 || data.Length>1400)return null;
            string magic=Encoding.ASCII.GetString(data,0,SignalPrefixLength);int offset;
            if(magic=="OUC1"){challenge=true;offset=SignalPrefixLength;}
            else if(magic=="OUS1" && data.Length>SignalPrefixLength+SignalSignatureLength)
            {
                offset=SignalPrefixLength+SignalSignatureLength;
                using(var hmac=new HMACSHA256(auth))
                {
                    byte[] expected=hmac.ComputeHash(data,offset,data.Length-offset);int diff=0;
                    for(int i=0;i<SignalSignatureLength;i++)diff|=expected[i]^data[SignalPrefixLength+i];if(diff!=0)return null;
                }
            }
            else return null;
            byte[] json=new byte[data.Length-offset];Buffer.BlockCopy(data,offset,json,0,json.Length);
            using(var stream=new MemoryStream(json))
            {
                if(challenge)
                {
                    // OUC1 is the challenge discriminator; some servers omit the redundant op.
                    // Deserialize only challenge fields, never unsigned membership state.
                    var value=(RoomChallenge)new DataContractJsonSerializer(typeof(RoomChallenge)).ReadObject(stream);
                    if(value==null || (value.Op!=null && value.Op!="challenge") || value.Cookie?.Length!=64)return null;
                    try{Hex(value.Cookie,32);}catch(FormatException){return null;}
                    return new RoomReply {Op="challenge",Cookie=value.Cookie};
                }
                return (RoomReply)new DataContractJsonSerializer(typeof(RoomReply)).ReadObject(stream);
            }
        }
        public static byte[] Frame(byte[] id,byte[] key,ulong sequence,byte kind,byte[] payload)
        {
            if(id.Length!=16 || key.Length!=32 || sequence==0 || kind<1 || kind>6 || payload.Length>60000)throw new InvalidDataException("Invalid peer packet.");
            byte[] bytes=new byte[Overhead+payload.Length];Buffer.BlockCopy(Encoding.ASCII.GetBytes("OUP1"),0,bytes,0,4);
            Buffer.BlockCopy(id,0,bytes,4,16);Buffer.BlockCopy(BitConverter.GetBytes(sequence),0,bytes,20,8);bytes[28]=kind;
            Buffer.BlockCopy(payload,0,bytes,HeaderLength,payload.Length);
            using(var hmac=new HMACSHA256(key))Buffer.BlockCopy(hmac.ComputeHash(bytes,0,bytes.Length-32),0,bytes,bytes.Length-32,32);
            return bytes;
        }
        public static bool ReadFrame(byte[] bytes,byte[] key,ReplayWindow window,out byte kind,out byte[] payload)
        {
            kind=0;payload=null;
            if(bytes==null || bytes.Length<Overhead || bytes.Length>60000+Overhead || bytes[28]<1 || bytes[28]>6 || Encoding.ASCII.GetString(bytes,0,4)!="OUP1")return false;
            using(var hmac=new HMACSHA256(key))
            {
                byte[] signature=hmac.ComputeHash(bytes,0,bytes.Length-32);int diff=0;
                for(int i=0;i<32;i++)diff|=signature[i]^bytes[bytes.Length-32+i];if(diff!=0)return false;
            }
            if(!window.Accept(BitConverter.ToUInt64(bytes,20)))return false;
            kind=bytes[28];payload=new byte[bytes.Length-Overhead];Buffer.BlockCopy(bytes,HeaderLength,payload,0,payload.Length);return true;
        }
    }
}
