using System;
using System.Globalization;

namespace Orbit_Us
{
    // Smooth the host's half-second text snapshots locally. Keep only the newest
    // target, and reveal at most one text element per rendered update.
    public sealed class StoryTextPresentation
    {
        private readonly bool backwards, allowInward;
        private string target="";
        private bool initialized, active, inward, fromRight;
        private double nextCharacter, interval=.05;
        public string Text {get;private set;}="";

        public StoryTextPresentation(bool backwards=false,bool allowInward=false)
        {this.backwards=backwards;this.allowInward=allowInward;}

        public void Observe(string text,bool visible,double now)
        {
            text=text??"";
            // Joining in the middle of a conversation starts at the host's
            // current position instead of replaying an entire old paragraph.
            if(!initialized || !visible)
            {
                initialized=true;active=visible;target=Text=text;
                inward=fromRight=false;nextCharacter=now;
                return;
            }
            if(active && text==target)return;
            bool pending=Text!=target;
            bool continuation=active && (!inward || Text==target) &&
                (backwards?text.EndsWith(target,StringComparison.Ordinal):text.StartsWith(target,StringComparison.Ordinal));
            bool filling=active && allowInward && FillsSpaces(target,text);
            if(!continuation && !filling)
            {
                Text="";inward=fromRight=false;
            }
            if(filling)
            {
                inward=true;
                Text=Text.PadRight(text.Length);
            }
            else if(continuation)inward=false;
            active=true;target=text;
            if(text.Length==0){Text="";inward=false;return;}
            int remaining=PendingCharacters();
            // Spread each batch over one snapshot interval, catching up gently
            // after packet loss without displaying a whole batch in one frame.
            interval=Math.Max(1.0/120,Math.Min(.1,.5/Math.Max(1,remaining)));
            if(!pending || (!continuation && !filling))nextCharacter=now;
            else nextCharacter=Math.Min(nextCharacter,now+interval);
        }

        public bool Step(double now,bool paused=false)
        {
            if(!active || Text==target)return false;
            if(paused){nextCharacter=now+interval;return false;}
            if(now<nextCharacter)return false;
            if(inward)
            {
                int[] positions=StringInfo.ParseCombiningCharacters(target);
                for(int n=0;n<positions.Length;n++)
                {
                    int i=fromRight?positions.Length-1-n:n;
                    int at=positions[i],end=i+1<positions.Length?positions[i+1]:target.Length;
                    if(string.CompareOrdinal(Text,at,target,at,end-at)==0)continue;
                    Text=Text.Substring(0,at)+target.Substring(at,end-at)+Text.Substring(end);
                    fromRight=!fromRight;break;
                }
            }
            else if(backwards)
            {
                int[] positions=StringInfo.ParseCombiningCharacters(target.Substring(0,target.Length-Text.Length));
                Text=target.Substring(positions[positions.Length-1]);
            }
            else
            {
                int end=Text.Length;
                while(end<target.Length && TagEnd(target,end,out int tag))end=tag;
                if(end<target.Length)
                {
                    // Wait for a complete rich-text tag rather than flashing it.
                    if(target[end]=='<')return false;
                    end+=StringInfo.GetNextTextElement(target,end).Length;
                    while(end<target.Length && TagEnd(target,end,out int trailingTag))end=trailingTag;
                }
                Text=target.Substring(0,end);
            }
            nextCharacter=now+interval;
            return true;
        }

        private int PendingCharacters()
        {
            if(inward)
            {
                int count=0;
                for(int i=0;i<target.Length;i++)if(Text[i]!=target[i])count++;
                return count;
            }
            string remaining=backwards?target.Substring(0,target.Length-Text.Length):target.Substring(Text.Length);
            int characters=0;
            for(int i=0;i<remaining.Length;)
            {
                if(!backwards && TagEnd(remaining,i,out int tag)){i=tag;continue;}
                i+=StringInfo.GetNextTextElement(remaining,i).Length;characters++;
            }
            return characters;
        }

        private static bool TagEnd(string text,int at,out int end)
        {
            end=at;
            if(text[at]!='<')return false;
            int close=text.IndexOf('>',at+1);
            if(close<0)return false;
            end=close+1;return true;
        }

        private static bool FillsSpaces(string previous,string text)
        {
            if(previous.Length==0 || previous.Length!=text.Length)return false;
            // Timeless dialogue fills a fixed-width line from both ends.
            for(int i=0;i<previous.Length;i++)
                if(previous[i]!=text[i] && !char.IsWhiteSpace(previous[i]))return false;
            return true;
        }
    }
}
