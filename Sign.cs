using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InfiniteSigns
{
    public class Sign
    {
        public string account;
        public string text;
        public Vector2 loc;
    }

    public enum SignAction : byte
    {
        NONE,
        PROTECT,
        UNPROTECT,
        INFO
    }
}
