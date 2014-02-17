using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Mono.Data.Sqlite;
using MySql.Data.MySqlClient;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace InfiniteSigns
{
	[ApiVersion(1, 15)]
	public class InfiniteSigns : TerrariaPlugin
	{
		public SignAction[] Action = new SignAction[256];
		public IDbConnection Database;
		public bool[] SignNum = new bool[256];
        
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
			Order = 1;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				ServerApi.Hooks.NetGetData.Deregister(this, OnGetData);
				ServerApi.Hooks.GameInitialize.Deregister(this, OnInitialize);
				ServerApi.Hooks.GamePostInitialize.Deregister(this, OnPostInitialize);
				ServerApi.Hooks.ServerLeave.Deregister(this, OnLeave);

				Database.Dispose();
			}
		}
		public override void Initialize()
		{
			ServerApi.Hooks.NetGetData.Register(this, OnGetData);
			ServerApi.Hooks.GameInitialize.Register(this, OnInitialize);
			ServerApi.Hooks.GamePostInitialize.Register(this, OnPostInitialize);
			ServerApi.Hooks.ServerLeave.Register(this, OnLeave);
		}

		void OnGetData(GetDataEventArgs e)
		{
			if (!e.Handled)
			{
				using (var reader = new BinaryReader(new MemoryStream(e.Msg.readBuffer, e.Index, e.Length)))
				{
					switch (e.MsgID)
					{
						case PacketTypes.SignNew:
							{
								reader.ReadInt16();
								int x = reader.ReadInt32();
								int y = reader.ReadInt32();
								string text = Encoding.UTF8.GetString(e.Msg.readBuffer, e.Index + 10, e.Length - 11);
								Task.Factory.StartNew(() => ModSign(x, y, e.Msg.whoAmI, text));
								e.Handled = true;
							}
							break;
						case PacketTypes.SignRead:
							{
								int x = reader.ReadInt32();
								int y = reader.ReadInt32();
								Task.Factory.StartNew(() => GetSign(x, y, e.Msg.whoAmI));
								e.Handled = true;
							}
							break;
						case PacketTypes.Tile:
							{
								byte action = reader.ReadByte();
								int x = reader.ReadInt32();
								int y = reader.ReadInt32();
								ushort type = reader.ReadUInt16();

								if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY)
									return;

								if (action == 0 && type == 0)
								{
									if (Sign.Nearby(x, y))
									{
										Task.Factory.StartNew(() => KillSign(x, y, e.Msg.whoAmI));
										e.Handled = true;
									}
								}
								else if (action == 1 && (type == 55 || type == 85))
								{
									if (TShock.Regions.CanBuild(x, y, TShock.Players[e.Msg.whoAmI]))
									{
										WorldGen.PlaceSign(x, y, type);
										NetMessage.SendData(17, -1, e.Msg.whoAmI, "", 1, x, y, type);
										if (Main.tile[x, y].frameY != 0)
											y--;
										if (Main.tile[x, y].frameX % 36 != 0)
											x--;
										Task.Factory.StartNew(() => PlaceSign(x, y, e.Msg.whoAmI));
										e.Handled = true;
									}
								}
							}
							break;
					}
				}
			}
		}
		void OnInitialize(EventArgs e)
		{
			Commands.ChatCommands.Add(new Command("infsigns.admin.convert", ConvertSigns, "convsigns"));
			Commands.ChatCommands.Add(new Command("infsigns.sign.deselect", Deselect, "scset"));
			Commands.ChatCommands.Add(new Command("infsigns.admin.info", Info, "sinfo"));
			Commands.ChatCommands.Add(new Command("infsigns.sign.protect", Protect, "sset"));
			Commands.ChatCommands.Add(new Command("infsigns.admin.prune", Prune, "prunesigns"));
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
		void OnLeave(LeaveEventArgs e)
		{
			Action[e.Who] = SignAction.NONE;
			SignNum[e.Who] = false;
		}
		void OnPostInitialize(EventArgs e)
		{
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
			if (converted > 0)
			{
				TSPlayer.Server.SendSuccessMessage("Converted {0} sign{1}.", converted, converted == 1 ? "" : "s");
				WorldFile.saveWorld();
			}
		}

		void GetSign(int x, int y, int plr)
		{
			Sign sign = null;
			using (QueryResult reader = Database.QueryReader("SELECT Account, Text FROM Signs WHERE X = @0 AND Y = @1 AND WorldID = @2",
				x, y, Main.worldID))
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
						player.SendInfoMessage("X: {0} Y: {1} Account: {2}", x, y, sign.account == "" ? "N/A" : sign.account);
						break;
					case SignAction.PROTECT:
						if (sign.account != "")
						{
							player.SendErrorMessage("This sign is protected.");
							break;
						}
						Database.Query("UPDATE Signs SET Account = @0 WHERE X = @1 AND Y = @2 AND WorldID = @3",
							player.UserAccountName, x, y, Main.worldID);
						player.SendInfoMessage("This sign is now protected.");
						break;
					case SignAction.UNPROTECT:
						if (sign.account == "")
						{
							player.SendErrorMessage("This sign is not protected.");
							break;
						}
						if (sign.account != player.UserAccountName && !player.Group.HasPermission("infsigns.admin.editall"))
						{
							player.SendErrorMessage("This sign is not yours.");
							break;
						}
						Database.Query("UPDATE Signs SET Account = '' WHERE X = @0 AND Y = @1 AND WorldID = @2",
							x, y, Main.worldID);
						player.SendInfoMessage("This sign is now unprotected.");
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
						Buffer.BlockCopy(BitConverter.GetBytes(x), 0, raw, 7, 4);
						Buffer.BlockCopy(BitConverter.GetBytes(y), 0, raw, 11, 4);
						Buffer.BlockCopy(utf8, 0, raw, 15, utf8.Length);
						player.SendRawData(raw);
						SignNum[plr] = !SignNum[plr];
						break;
				}
				Action[plr] = SignAction.NONE;
			}
		}
		void KillSign(int x, int y, int plr)
		{
			TSPlayer player = TShock.Players[plr];

			bool[] attached = new bool[4];
			bool[] attachedNext = new bool[4];
			var positions = new List<Point>();
			if (Main.tile[x, y].IsSign())
			{
				if (TryKillSign(Sign.GetSign(x, y).X, Sign.GetSign(x, y).Y, plr))
				{
					WorldGen.KillTile(x, y);
					TSPlayer.All.SendTileSquare(x, y, 3);
					return;
				}
				else
				{
					player.SendErrorMessage("This sign is protected.");
					player.SendTileSquare(x, y, 5);
					return;
				}
			}
			else
			{
				if (TileSolid(x - 1, y) && Main.tile[x - 1, y].IsSign())
					positions.Add(Sign.GetSign(x - 1, y));
				if (TileSolid(x + 1, y) && Main.tile[x + 1, y].IsSign())
					positions.Add(Sign.GetSign(x + 1, y));
				if (TileSolid(x, y - 1) && Main.tile[x, y - 1].IsSign())
					positions.Add(Sign.GetSign(x, y - 1));
				if (TileSolid(x, y + 1) && Main.tile[x, y + 1].IsSign())
					positions.Add(Sign.GetSign(x, y + 1));
				foreach (Point p in positions)
				{
					attached[0] = TileSolid(p.X, p.Y + 2) && Main.tile[p.X, p.Y + 2].IsSolid()
						&& TileSolid(p.X + 1, p.Y + 2) && Main.tile[p.X + 1, p.Y + 2].IsSolid();
					attached[1] = TileSolid(p.X, p.Y - 1) && Main.tile[p.X, p.Y - 1].IsSolid()
						&& TileSolid(p.X + 1, p.Y - 1) && Main.tile[p.X + 1, p.Y - 1].IsSolid();
					attached[2] = TileSolid(p.X - 1, p.Y) && Main.tile[p.X - 1, p.Y].IsSolid()
						&& TileSolid(p.X - 1, p.Y + 1) && Main.tile[p.X - 1, p.Y + 1].IsSolid();
					attached[3] = TileSolid(p.X + 2, p.Y) && Main.tile[p.X + 2, p.Y].IsSolid()
						&& TileSolid(p.X + 2, p.Y + 1) && Main.tile[p.X + 2, p.Y + 1].IsSolid();
					bool prev = Main.tile[x, y].active();
					Main.tile[x, y].active(false);
					attachedNext[0] = TileSolid(p.X, p.Y + 2) && Main.tile[p.X, p.Y + 2].IsSolid()
						&& TileSolid(p.X + 1, p.Y + 2) && Main.tile[p.X + 1, p.Y + 2].IsSolid();
					attachedNext[1] = TileSolid(p.X, p.Y - 1) && Main.tile[p.X, p.Y - 1].IsSolid()
						&& TileSolid(p.X + 1, p.Y - 1) && Main.tile[p.X + 1, p.Y - 1].IsSolid();
					attachedNext[2] = TileSolid(p.X - 1, p.Y) && Main.tile[p.X - 1, p.Y].IsSolid()
						&& TileSolid(p.X - 1, p.Y + 1) && Main.tile[p.X - 1, p.Y + 1].IsSolid();
					attachedNext[3] = TileSolid(p.X + 2, p.Y) && Main.tile[p.X + 2, p.Y].IsSolid()
						&& TileSolid(p.X + 2, p.Y + 1) && Main.tile[p.X + 2, p.Y + 1].IsSolid();
					Main.tile[x, y].active(prev);
					if (attached.Count(b => b) > 1 || attached.Count(b => b) == attachedNext.Count(b => b))
						continue;
					if (TryKillSign(p.X, p.Y, plr))
					{
						WorldGen.KillTile(p.X, p.Y);
						TSPlayer.All.SendTileSquare(p.X, p.Y, 3);
					}
					else
					{
						player.SendErrorMessage("This sign is protected.");
						player.SendTileSquare(x, y, 5);
					}
				}
			}
		}
		void ModSign(int x, int y, int plr, string text)
		{
			Sign sign = null;
			using (QueryResult reader = Database.QueryReader("SELECT Account FROM Signs WHERE X = @0 AND Y = @1 AND WorldID = @2",
				x, y, Main.worldID))
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
					player.SendErrorMessage("This sign is protected.");
				}
				else
				{
					Database.Query("UPDATE Signs SET Text = @0 WHERE X = @1 AND Y = @2 AND WorldID = @3", text, x, y, Main.worldID);
				}
			}
		}
		void PlaceSign(int x, int y, int plr)
		{
			TSPlayer player = TShock.Players[plr];
			Database.Query("INSERT INTO Signs (X, Y, Account, Text, WorldID) VALUES (@0, @1, @2, '', @3)",
				x, y, (player.IsLoggedIn && player.Group.HasPermission("infsigns.sign.protect")) ? player.UserAccountName : "", Main.worldID);
			Main.sign[999] = null;
		}
		bool TileSolid(int x, int y)
		{
			return x >= 0 && y >= 0 && x < Main.maxTilesX && y < Main.maxTilesY && Main.tile[x, y] != null && Main.tile[x, y].type != 127;
		}
		bool TryKillSign(int x, int y, int plr)
		{
			Sign sign = null;
			using (QueryResult reader = Database.QueryReader("SELECT Account, Text FROM Signs WHERE X = @0 AND Y = @1 AND WorldID = @2",
				x, y, Main.worldID))
			{
				if (reader.Read())
					sign = new Sign { account = reader.Get<string>("Account"), text = reader.Get<string>("Text") };
			}
			if (sign != null)
			{
				if (sign.account != TShock.Players[plr].UserAccountName && sign.account != "" &&
					!TShock.Players[plr].Group.HasPermission("infsigns.admin.editall"))
				{
					return false;
				}
			}
			Database.Query("DELETE FROM Signs WHERE X = @0 AND Y = @1 AND WorldID = @2", x, y, Main.worldID);
			return true;
		}

		void ConvertSigns(CommandArgs e)
		{
			Task.Factory.StartNew(() =>
				{
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
					e.Player.SendSuccessMessage("Converted {0} sign{1}.", converted, converted == 1 ? "" : "s");
					if (converted > 0)
						WorldFile.saveWorld();
				});
		}
		void Deselect(CommandArgs e)
		{
			Action[e.Player.Index] = SignAction.NONE;
			e.Player.SendInfoMessage("Stopped selecting a sign.");
		}
		void Info(CommandArgs e)
		{
			Action[e.Player.Index] = SignAction.INFO;
			e.Player.SendInfoMessage("Read a sign to get its info.");
		}
		void Protect(CommandArgs e)
		{
			Action[e.Player.Index] = SignAction.PROTECT;
			e.Player.SendInfoMessage("Read a sign to protect it.");
		}
		void Prune(CommandArgs e)
		{
			Task.Factory.StartNew(() =>
			{
				using (var reader = Database.QueryReader("SELECT X, Y FROM Signs WHERE Text = '' AND WorldID = @0", Main.worldID))
				{
					while (reader.Read())
					{
						int x = reader.Get<int>("X");
						int y = reader.Get<int>("Y");
						WorldGen.KillTile(x, y);
						TSPlayer.All.SendTileSquare(x, y, 3);
					}
				}

				int count = Database.Query("DELETE FROM Signs WHERE Text = '' AND WorldID = @0", Main.worldID);
				e.Player.SendSuccessMessage("Pruned {0} sign{1}.", count, count == 1 ? "" : "s");
				if (count > 0)
					WorldFile.saveWorld();
			});
		}
		void Unprotect(CommandArgs e)
		{
			Action[e.Player.Index] = SignAction.UNPROTECT;
			e.Player.SendInfoMessage("Read a sign to unprotect it.");
		}
	}
}