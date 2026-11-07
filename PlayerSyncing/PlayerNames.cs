using System;
using System.Text;

namespace Orbit_Us
{
    public static class PlayerNames
    {
        public const int MaxLength=24,MaxBytes=96;
        public static string Normalize(string value)
        {
            var text=new StringBuilder();
            foreach(char c in value??"")
            {
                if(char.IsControl(c) || c=='<' || c=='>' || char.GetUnicodeCategory(c)==System.Globalization.UnicodeCategory.Format)continue;
                if(text.Length==MaxLength)break;
                text.Append(c);
            }
            if(text.Length>0 && char.IsHighSurrogate(text[text.Length-1]))text.Length--;
            return text.ToString().Trim();
        }
        public static byte[] Encode(int id,string name)
        {
            byte[] text=Encoding.UTF8.GetBytes(Normalize(name)),data=new byte[4+text.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(id),0,data,0,4);Buffer.BlockCopy(text,0,data,4,text.Length);return data;
        }
        public static bool TryDecode(byte[] data,out int id,out string name)
        {
            id=0;name="";if(data==null || data.Length<4 || data.Length>4+MaxBytes)return false;
            id=BitConverter.ToInt32(data,0);if(id<=0)return false;
            name=Normalize(Encoding.UTF8.GetString(data,4,data.Length-4));return true;
        }
    }
}
