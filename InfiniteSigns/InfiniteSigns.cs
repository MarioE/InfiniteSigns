using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Hooks;
using Mono.Data.Sqlite;
using MySql.Data.MySqlClient;
using Terraria;
using TShockAPI;
using TShockAPI.DB;

namespace InfiniteSigns
{
	[APIVersion(1, 12)]
	public class InfiniteSigns : TerrariaPlugin
	{
		public SignAction[] Action = new SignAction[256];
		public IDbConnection Database;
		public bool[] SignNum = new bool[256];
		public static event Action<SignEventArgs> SignEdit;
		public static event Action<SignEventArgs> SignKill;
		public static event Action<SignEventArgs> SignRead;

		public override string Author
		{
			get { return "MarioE"; }
		}
		public override string Description
		{
			get { return "Allows for infinite signs, and supports all sign control commands."; }
		}
		public override string Name
		{
			get { return "InfiniteSigns"; }
		}
		public override Version Version
		{
			get { return Assembly.GetExecutingAssembly().GetName().Version; }
		}
		public InfiniteSigns(Main game)
			: base(game)
		{
			Order = -1;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				NetHooks.GetData -= OnGetData;
				GameHooks.Initialize -= OnInitialize;
				ServerHooks.Leave -= OnLeave;

				Database.Dispose();
			}
		}
		public override void Initialize()
		{
			NetHooks.GetData += OnGetData;
			GameHooks.Initialize += OnInitialize;
			ServerHooks.Leave += OnLeave;
		}

		void OnGetData(GetDataEventArgs e)
		{
			if (!e.Handled)
			{
				switch (e.MsgID)
				{
					case PacketTypes.SignNew:
						{
							int X = BitConverter.ToInt32(e.Msg.readBuffer, e.Index + 2);
							int Y = BitConverter.ToInt32(e.Msg.readBuffer, e.Index + 6);
							string text = Encoding.UTF8.GetString(e.Msg.readBuffer, e.Index + 10, e.Length - 11);
							ModSign(X, Y, e.Msg.whoAmI, text);
							e.Handled = true;
						}
						break;
					case PacketTypes.SignRead:
						{
							int X = BitConverter.ToInt32(e.Msg.readBuffer, e.Index);
							int Y = BitConverter.ToInt32(e.Msg.readBuffer, e.Index + 4);
							GetSign(X, Y, e.Msg.whoAmI);
							e.Handled = true;
						}
						break;
					case PacketTypes.Tile:
						{
							int X = BitConverter.ToInt32(e.Msg.readBuffer, e.Index + 1);
							int Y = BitConverter.ToInt32(e.Msg.readBuffer, e.Index + 5);
							if (X < 0 || Y < 0 || X >= Main.maxTilesX || Y >= Main.maxTilesY)
							{
								return;
							}
							if (e.Msg.readBuffer[e.Index] == 0 && e.Msg.readBuffer[e.Index + 9] == 0)
							{
								if (Sign.Nearby(X, Y))
								{
									e.Handled = KillSign(X, Y, e.Msg.whoAmI);
								}
							}
							else if (e.Msg.readBuffer[e.Index] == 1 && (e.Msg.readBuffer[e.Index + 9] == 55 || e.Msg.readBuffer[e.Index + 9] == 85))
							{
								if (TShock.Regions.CanBuild(X, Y, TShock.Players[e.Msg.whoAmI]))
								{
									WorldGen.PlaceSign(X, Y, e.Msg.readBuffer[e.Index + 9]);
									NetMessage.SendData(17, -1, e.Msg.whoAmI, "", 1, X, Y, e.Msg.readBuffer[e.Index + 9]);
									if (Main.tile[X, Y].frameY != 0)
									{
										Y--;
									}
									if (Main.tile[X, Y].frameX % 36 != 0)
									{
										X--;
									}
									PlaceSign(X, Y, e.Msg.whoAmI);
									e.Handled = true;
								}
							}
						}
						break;
				}
			}
		}
		void OnInitialize()
		{
			Commands.ChatCommands.Add(new Command("infsigns.admin.convert", ConvertSigns, "convsigns"));
			Commands.ChatCommands.Add(new Command("infsigns.sign.deselect", Deselect, "scset"));
			Commands.ChatCommands.Add(new Command("infsigns.admin.info", Info, "sinfo"));
			Commands.ChatCommands.Add(new Command("infsigns.sign.protect", Protect, "sset"));
			Commands.ChatCommands.Add(new Command("infsigns.sign.unprotect", Unprotect, "sunset"));

			switch (TShock.Config.StorageType.ToLower())
			{
				case "mysql":
					string[] host = TShock.Config.MySqlHost.Split(':');
					Database = new MySqlConnection()
					{
						ConnectionString = string.Format("Server={0}; Port={1}; Database={2}; Uid={3}; Pwd={4};",
							host[0],
							host.Length == 1 ? "3306" : host[1],
							TShock.Config.MySqlDbName,
							TShock.Config.MySqlUsername,
							TShock.Config.MySqlPassword)
					};
					break;
				case "sqlite":
					string sql = Path.Combine(TShock.SavePath, "signs.sqlite");
					Database = new SqliteConnection(string.Format("uri=file://{0},Version=3", sql));
					break;
			}
			SqlTableCreator sqlcreator = new SqlTableCreator(Database,
				Database.GetSqlType() == SqlType.Sqlite ? (IQueryBuilder)new SqliteQueryCreator() : new MysqlQueryCreator());
			sqlcreator.EnsureExists(new SqlTable("Signs",
				new SqlColumn("X", MySqlDbType.Int32),
				new SqlColumn("Y", MySqlDbType.Int32),
				new SqlColumn("Account", MySqlDbType.Text),
				new SqlColumn("Text", MySqlDbType.Text),
				new SqlColumn("WorldID", MySqlDbType.Int32)));
		}
		void OnLeave(int index)
		{
			Action[index] = SignAction.NONE;
			SignNum[index] = false;
		}

		void GetSign(int X, int Y, int plr)
		{
			Sign sign = null;
			using (QueryResult reader = Database.QueryReader("SELECT Account, Text FROM Signs WHERE X = @0 AND Y = @1 AND WorldID = @2",
				X, Y, Main.worldID))
			{
				if (reader.Read())
				{
					sign = new Sign { account = reader.Get<string>("Account"), text = reader.Get<string>("Text") };
				}
			}
			TSPlayer player = TShock.Players[plr];

			if (sign != null)
			{
				switch (Action[plr])
				{
					case SignAction.INFO:
						player.SendMessage(string.Format("X: {0} Y: {1} Account: {2}", X, Y, sign.account == "" ? "N/A" : sign.account), Color.Yellow);
						break;
					case SignAction.PROTECT:
						if (sign.account != "")
						{
							player.SendMessage("This sign is protected.", Color.Red);
							break;
						}
						Database.Query("UPDATE Signs SET Account = @0 WHERE X = @1 AND Y = @2 AND WorldID = @3",
							player.UserAccountName, X, Y, Main.worldID);
						player.SendMessage("This sign is now protected.");
						break;
					case SignAction.UNPROTECT:
						if (sign.account == "")
						{
							player.SendMessage("This sign is not protected.", Color.Red);
							break;
						}
						if (sign.account != player.UserAccountName && !player.Group.HasPermission("infsigns.admin.editall"))
						{
							player.SendMessage("This sign is not yours.", Color.Red);
							break;
						}
						Database.Query("UPDATE Signs SET Account = '' WHERE X = @0 AND Y = @1 AND WorldID = @2",
							X, Y, Main.worldID);
						player.SendMessage("This sign is now unprotected.");
						break;
					default:
						if (sign.text.Length > 0 && sign.text[sign.text.Length - 1] == '\0')
						{
							sign.text = sign.text.Substring(0, sign.text.Length - 1);
						}
						byte[] utf8 = Encoding.UTF8.GetBytes(sign.text);
						byte[] raw = new byte[15 + utf8.Length];
						Buffer.BlockCopy(BitConverter.GetBytes(utf8.Length + 11), 0, raw, 0, 4);
						if (SignNum[plr])
						{
							raw[5] = 1;
						}
						raw[4] = 47;
						Buffer.BlockCopy(BitConverter.GetBytes(X), 0, raw, 7, 4);
						Buffer.BlockCopy(BitConverter.GetBytes(Y), 0, raw, 11, 4);
						Buffer.BlockCopy(utf8, 0, raw, 15, utf8.Length);
						SignNum[plr] = !SignNum[plr];
						SignEventArgs signargs = new SignEventArgs(X, Y, sign.text, sign.account);
						if (SignRead != null)
							SignRead(signargs);
						if (!signargs.Handled)
							player.SendRawData(raw);
						break;
				}
				Action[plr] = SignAction.NONE;
			}
		}
		bool KillSign(int X, int Y, int plr)
		{
			TSPlayer player = TShock.Players[plr];

			bool[] attached = new bool[4];
			bool[] attachedNext = new bool[4];
			List<Point> positions = new List<Point>();
			if (Main.tile[X, Y].IsSign())
			{
				positions.Add(Sign.GetSign(X, Y));
				if (TryKillSign(positions[0].X, positions[0].Y, plr))
				{
					WorldGen.KillTile(X, Y);
					TSPlayer.All.SendTileSquare(X, Y, 3);
					return true;
				}
				else
				{
					player.SendMessage("This sign is protected.", Color.Red);
					player.SendTileSquare(X, Y, 5);
					return true;
				}
			}
			else
			{
				if (Main.tile.Valid(X - 1, Y) && Main.tile[X - 1, Y].IsSign())
				{
					positions.Add(Sign.GetSign(X - 1, Y));
				}
				if (Main.tile.Valid(X + 1, Y) && Main.tile[X + 1, Y].IsSign())
				{
					positions.Add(Sign.GetSign(X + 1, Y));
				}
				if (Main.tile.Valid(X, Y - 1) && Main.tile[X, Y - 1].IsSign())
				{
					positions.Add(Sign.GetSign(X, Y - 1));
				}
				if (Main.tile.Valid(X, Y + 1) && Main.tile[X, Y + 1].IsSign())
				{
					positions.Add(Sign.GetSign(X, Y + 1));
				}
				bool killTile = true;
				foreach (Point p in positions)
				{
					attached[0] = Main.tile.Valid(p.X, p.Y + 2) && Main.tile[p.X, p.Y + 2].IsSolid()
						&& Main.tile.Valid(p.X + 1, p.Y + 2) && Main.tile[p.X + 1, p.Y + 2].IsSolid();
					attached[1] = Main.tile.Valid(p.X, p.Y - 1) && Main.tile[p.X, p.Y - 1].IsSolid()
						&& Main.tile.Valid(p.X + 1, p.Y - 1) && Main.tile[p.X + 1, p.Y - 1].IsSolid();
					attached[2] = Main.tile.Valid(p.X - 1, p.Y) && Main.tile[p.X - 1, p.Y].IsSolid()
						&& Main.tile.Valid(p.X - 1, p.Y + 1) && Main.tile[p.X - 1, p.Y + 1].IsSolid();
					attached[3] = Main.tile.Valid(p.X + 2, p.Y) && Main.tile[p.X + 2, p.Y].IsSolid()
						&& Main.tile.Valid(p.X + 2, p.Y + 1) && Main.tile[p.X + 2, p.Y + 1].IsSolid();
					bool prev = Main.tile[X, Y].active;
					Main.tile[X, Y].active = false;
					attachedNext[0] = Main.tile.Valid(p.X, p.Y + 2) && Main.tile[p.X, p.Y + 2].IsSolid()
						&& Main.tile.Valid(p.X + 1, p.Y + 2) && Main.tile[p.X + 1, p.Y + 2].IsSolid();
					attachedNext[1] = Main.tile.Valid(p.X, p.Y - 1) && Main.tile[p.X, p.Y - 1].IsSolid()
						&& Main.tile.Valid(p.X + 1, p.Y - 1) && Main.tile[p.X + 1, p.Y - 1].IsSolid();
					attachedNext[2] = Main.tile.Valid(p.X - 1, p.Y) && Main.tile[p.X - 1, p.Y].IsSolid()
						&& Main.tile.Valid(p.X - 1, p.Y + 1) && Main.tile[p.X - 1, p.Y + 1].IsSolid();
					attachedNext[3] = Main.tile.Valid(p.X + 2, p.Y) && Main.tile[p.X + 2, p.Y].IsSolid()
						&& Main.tile.Valid(p.X + 2, p.Y + 1) && Main.tile[p.X + 2, p.Y + 1].IsSolid();
					Main.tile[X, Y].active = prev;
					if (attached.Count(b => b) > 1 || attached.Count(b => b) == attachedNext.Count(b => b))
					{
						continue;
					}
					if (TryKillSign(p.X, p.Y, plr))
					{
						WorldGen.KillTile(p.X, p.Y);
						TSPlayer.All.SendTileSquare(p.X, p.Y, 3);
					}
					else
					{
						player.SendMessage("This sign is protected.", Color.Red);
						player.SendTileSquare(X, Y, 5);
						killTile = false;
					}
				}
				return !killTile;
			}
		}
		void ModSign(int X, int Y, int plr, string text)
		{
			Sign sign = null;
			using (QueryResult reader = Database.QueryReader("SELECT Account FROM Signs WHERE X = @0 AND Y = @1 AND WorldID = @2",
				X, Y, Main.worldID))
			{
				if (reader.Read())
				{
					sign = new Sign { account = reader.Get<string>("Account") };
				}
			}
			TSPlayer player = TShock.Players[plr];

			if (sign != null)
			{
				if (sign.account != player.UserAccountName && sign.account != "" && !player.Group.HasPermission("infsigns.admin.editall"))
				{
					player.SendMessage("This sign is protected.", Color.Red);
				}
				else
				{
					SignEventArgs signargs = new SignEventArgs(X, Y, sign.text, sign.account);
					if (SignEdit != null)
						SignEdit(signargs);
					if (signargs.Handled)
						player.SendMessage("Another plugin is preventing the sign from being edited.", Color.Red);
					else
						Database.Query("UPDATE Signs SET Text = @0 WHERE X = @1 AND Y = @2 AND WorldID = @3", text, X, Y, Main.worldID);
				}
			}
		}
		void PlaceSign(int X, int Y, int plr)
		{
			TSPlayer player = TShock.Players[plr];
			Database.Query("INSERT INTO Signs (X, Y, Account, Text, WorldID) VALUES (@0, @1, @2, '', @3)",
				X, Y, player.IsLoggedIn ? player.UserAccountName : "", Main.worldID);
			Main.sign[999] = null;
		}
		bool TryKillSign(int X, int Y, int plr)
		{
			Sign sign = null;
			using (QueryResult reader = Database.QueryReader("SELECT Account, Text FROM Signs WHERE X = @0 AND Y = @1 AND WorldID = @2",
				X, Y, Main.worldID))
			{
				if (reader.Read())
				{
					sign = new Sign { account = reader.Get<string>("Account"), text = reader.Get<string>("Text") };
				}
			}
			if (sign != null)
			{
				if (sign.account != TShock.Players[plr].UserAccountName && sign.account != "" &&
					!TShock.Players[plr].Group.HasPermission("infsigns.admin.editall"))
				{
					return false;
				}
				else
				{
					if (SignKill != null)
					{
						SignEventArgs signargs = new SignEventArgs(X, Y, sign.text, sign.account);
						SignKill(signargs);
						if (signargs.Handled)
							return false;
					}
				}
			}
			Database.Query("DELETE FROM Signs WHERE X = @0 AND Y = @1 AND WorldID = @2", X, Y, Main.worldID);
			return true;
		}

		void ConvertSigns(CommandArgs e)
		{
			Database.Query("DELETE FROM Signs WHERE WorldID = @0", Main.worldID);
			int converted = 0;
			foreach (Terraria.Sign s in Main.sign)
			{
				if (s != null)
				{
					Database.Query("INSERT INTO Signs (X, Y, Text, WorldID) VALUES (@0, @1, @2, @3)",
						s.x, s.y, s.text, Main.worldID);
					converted++;
				}
			}
			e.Player.SendMessage("Converted " + converted + " signs.", Color.Green);
		}
		void Deselect(CommandArgs e)
		{
			Action[e.Player.Index] = SignAction.NONE;
			e.Player.SendMessage("Stopped selecting a sign.", Color.Yellow);
		}
		void Info(CommandArgs e)
		{
			Action[e.Player.Index] = SignAction.INFO;
			e.Player.SendMessage("Read a sign to get its info.", Color.Green);
		}
		void Protect(CommandArgs e)
		{
			Action[e.Player.Index] = SignAction.PROTECT;
			e.Player.SendMessage("Read a sign to protect it.", Color.Green);
		}
		void Unprotect(CommandArgs e)
		{
			Action[e.Player.Index] = SignAction.UNPROTECT;
			e.Player.SendMessage("Read a sign to unprotect it.", Color.Green);
		}
	}
	public class SignEventArgs : HandledEventArgs
	{
		public SignEventArgs(int x, int y, string text, string account)
		{
			this.X = x;
			this.Y = y;
			this.text = text;
			this.Account = account;
		}
		public int X;
		public int Y;
		public string text;
		public string Account;
	}
}