using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Terraria;

namespace InfiniteSigns
{
	public class Sign
	{
		public string account = "";
		public string text = "";

		public static Point GetSign(int X, int Y)
		{
			if (Main.tile[X, Y].frameY != 0)
			{
				Y--;
			}
			if (Main.tile[X, Y].frameX % 36 != 0)
			{
				X--;
			}
			return new Point(X, Y);
		}
		public static bool Nearby(int X, int Y)
		{
			return SignOn(X, Y) || SignOn(X - 1, Y) || SignOn(X + 1, Y) || SignOn(X, Y - 1) || SignOn(X, Y + 1);
		}
		static bool SignOn(int X, int Y)
		{
			return TileSolid(X, Y) && Main.tile[X, Y].IsSign();
		}
		static bool TileSolid(int X, int Y)
		{
			return X >= 0 && Y >= 0 && X < Main.maxTilesX && Y < Main.maxTilesY && Main.tile[X, Y] != null && Main.tile[X, Y].type != 127;
		}
	}

	public enum SignAction : byte
	{
		NONE,
		PROTECT,
		UNPROTECT,
		INFO
	}
}