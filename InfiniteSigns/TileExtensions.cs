using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Terraria;

namespace InfiniteSigns
{
    public static class TileExtensions
    {
        public static bool IsSign(this Tile t)
        {
            return t.type == 55 || t.type == 85;
        }
        public static bool IsSolid(this Tile t)
        {
            return t.active && Main.tileSolid[t.type];
        }
        public static bool Valid(this TileCollection t, int X, int Y)
        {
            return X >= 0 && Y >= 0 && X < Main.maxTilesX && Y < Main.maxTilesY && Main.tile[X, Y] != null && Main.tile[X, Y].type != 127;
        }
    }
}
