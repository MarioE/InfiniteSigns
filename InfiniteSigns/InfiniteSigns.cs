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
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace InfiniteSigns
{
	[ApiVersion(1, 16)]
	public class InfiniteSigns : TerrariaPlugin
	{
		public IDbConnection Database;
		public PlayerInfo[] Infos = new PlayerInfo[256];
        
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
			for (int i = 0; i < 256; i++)
				Infos[i] = new PlayerInfo() { Index = i };
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
								int x = reader.ReadInt16();
								int y = reader.ReadInt16();
								string text = reader.ReadString();
								Task.Factory.StartNew(() => ModSign(x, y, e.Msg.whoAmI, text));
								e.Handled = true;
							}
							break;
						case PacketTypes.SignRead:
							{
								int x = reader.ReadInt16();
								int y = reader.ReadInt16();
								Task.Factory.StartNew(() => GetSign(x, y, e.Msg.whoAmI));
								e.Handled = true;
							}
							break;
						case PacketTypes.Tile:
							{
								byte action = reader.ReadByte();
								int x = reader.ReadInt16();
								int y = reader.ReadInt16();
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
			Commands.ChatCommands.Add(new Command("infsigns.admin.convert", ConvertSigns, "convsigns")
			{
				HelpText = "Converts Terraria signs to InfiniteSigns signs."
			});
			Commands.ChatCommands.Add(new Command("infsigns.admin.prune", Prune, "prunesigns")
			{
				HelpText = "Prunes empty signs."
			});
			Commands.ChatCommands.Add(new Command("infsigns.admin.rconvert", ReverseConvertSigns, "rconvsigns")
			{
				HelpText = "Converts InfiniteSigns signs to Terraria signs."
			});
			Commands.ChatCommands.Add(new Command("infsigns.sign.deselect", Deselect, "scset")
			{
				AllowServer = false,
				HelpText = "Cancels a sign selection."
			});
			Commands.ChatCommands.Add(new Command("infsigns.admin.info", Info, "sinfo")
			{
				AllowServer = false,
				HelpText = "Gets information about a sign when read."
			});
			Commands.ChatCommands.Add(new Command("infsigns.sign.lock", Lock, "slock")
			{
				AllowServer = false,
				DoLog = false,
				HelpText = "Locks a sign with a password when read. Use remove as the password to remove it."
			});
			Commands.ChatCommands.Add(new Command("infsigns.sign.protect", Protect, "sset")
			{
				AllowServer = false,
				HelpText = "Protects an unprotected sign when read."
			});
			Commands.ChatCommands.Add(new Command("infsigns.sign.public", Region, "spset")
			{
				AllowServer = false,
				HelpText = "Toggles a sign's public protection when read."
			});
			Commands.ChatCommands.Add(new Command("infsigns.sign.region", Region, "srset")
			{
				AllowServer = false,
				HelpText = "Toggles a sign's region protection when read."
			});
			Commands.ChatCommands.Add(new Command("infsigns.sign.unlock", Unlock, "sunlock")
			{
				AllowServer = false,
				DoLog = false,
				HelpText = "Unlocks a sign with a password."
			});
			Commands.ChatCommands.Add(new Command("infsigns.sign.unprotect", Unprotect, "sunset")
			{
				AllowServer = false,
				HelpText = "Unprotects a sign when read."
			});

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
				new SqlColumn("ID", MySqlDbType.Int32) { AutoIncrement = true, Primary = true },
				new SqlColumn("X", MySqlDbType.Int32),
				new SqlColumn("Y", MySqlDbType.Int32),
				new SqlColumn("Account", MySqlDbType.Text),
				new SqlColumn("Flags", MySqlDbType.Int32),
				new SqlColumn("Password", MySqlDbType.Text),
				new SqlColumn("Text", MySqlDbType.Text),
				new SqlColumn("WorldID", MySqlDbType.Int32)));
		}
		void OnLeave(LeaveEventArgs e)
		{
			Infos[e.Who] = new PlayerInfo();
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
				TSPlayer.Server.SendSuccessMessage("[InfiniteSigns] Converted {0} sign{1}.", converted, converted == 1 ? "" : "s");
				WorldFile.saveWorld();
			}
		}

		void GetSign(int x, int y, int plr)
		{
			Sign sign = null;
			using (QueryResult reader = Database.QueryReader("SELECT Account, Flags, ID, Text FROM Signs WHERE X = @0 AND Y = @1 AND WorldID = @2",
				x, y, Main.worldID))
			{
				if (reader.Read())
				{
					sign = new Sign
					{
						Account = reader.Get<string>("Account"),
						Flags = (SignFlags)reader.Get<int>("Flags"),
						ID = reader.Get<int>("ID"),
						Text = reader.Get<string>("Text")
					};
				}
			}

			var info = Infos[plr];
			var player = TShock.Players[plr];
			if (sign != null)
			{
				switch (info.Action)
				{
					case SignAction.Info:
						player.SendInfoMessage("X: {0} Y: {1} Account: {2} Region: {3}", x, y, sign.Account ?? "N/A", sign.IsRegion);
						break;
					case SignAction.Protect:
						if (!String.IsNullOrEmpty(sign.Account))
						{
							player.SendErrorMessage("This sign is protected.");
							break;
						}
						Database.Query("UPDATE Signs SET Account = @0 WHERE ID = @1", player.UserAccountName, sign.ID);
						player.SendInfoMessage("This sign is now protected.");
						break;
					case SignAction.SetPassword:
						if (String.IsNullOrEmpty(sign.Account))
						{
							player.SendErrorMessage("This sign is not protected.");
							break;
						}
						if (sign.Account != player.UserAccountName && !player.Group.HasPermission("infsigns.admin.editall"))
						{
							player.SendErrorMessage("This sign is not yours.");
							break;
						}
						if (String.Equals(info.Password, "remove", StringComparison.CurrentCultureIgnoreCase))
						{
							Database.Query("UPDATE Signs SET Password = NULL WHERE ID = @0", sign.ID);
							player.SendSuccessMessage("This sign is no longer password protected.");
						}
						else
						{
							Database.Query("UPDATE Signs SET Password = @0 WHERE ID = @1", TShock.Utils.HashPassword(info.Password), sign.ID);
							player.SendSuccessMessage("This sign is now password protected with password '{0}'.", info.Password);
						}
						break;
					case SignAction.TogglePublic:
						if (String.IsNullOrEmpty(sign.Account))
						{
							player.SendErrorMessage("This sign is not protected.");
							break;
						}
						if (sign.Account != player.UserAccountName && !player.Group.HasPermission("infsigns.admin.editall"))
						{
							player.SendErrorMessage("This sign is not yours.");
							break;
						}
						Database.Query("UPDATE Signs SET Flags = (~(Flags & 1)) & (Flags | 1) WHERE ID = @0", sign.ID);
						player.SendInfoMessage("This sign is no{0} public.", sign.IsRegion ? " longer" : "w");
						break;
					case SignAction.ToggleRegion:
						if (String.IsNullOrEmpty(sign.Account))
						{
							player.SendErrorMessage("This sign is not protected.");
							break;
						}
						if (sign.Account != player.UserAccountName && !player.Group.HasPermission("infsigns.admin.editall"))
						{
							player.SendErrorMessage("This sign is not yours.");
							break;
						}
						Database.Query("UPDATE Signs SET Flags = (~(Flags & 2)) & (Flags | 2) WHERE ID = @0", sign.ID);
						player.SendInfoMessage("This sign is no{0} region shared.", sign.IsRegion ? " longer" : "w");
						break;
					case SignAction.Unprotect:
						if (String.IsNullOrEmpty(sign.Account))
						{
							player.SendErrorMessage("This sign is not protected.");
							break;
						}
						if (sign.Account != player.UserAccountName && !player.Group.HasPermission("infsigns.admin.editall"))
						{
							player.SendErrorMessage("This sign is not yours.");
							break;
						}
						Database.Query("UPDATE Signs SET Account = NULL, Flags = 0, Password = NULL WHERE ID = @0", sign.ID);
						player.SendInfoMessage("This sign is now unprotected.");
						break;
					default:
						sign.Text = sign.Text.Replace("\0", "");
						using (var writer = new BinaryWriter(new MemoryStream()))
						{
							writer.Write((short)0);
							writer.Write((byte)47);
							writer.Write((short)(info.SignIndex ? 1 : 0));
							writer.Write((short)x);
							writer.Write((short)y);
							writer.Write(sign.Text);

							short length = (short)writer.BaseStream.Position;
							writer.BaseStream.Position = 0;
							writer.Write(length);
							player.SendRawData(((MemoryStream)writer.BaseStream).ToArray());
						}
						info.SignIndex = !info.SignIndex;
						info.X = x;
						info.Y = y;
						break;
				}
				info.Action = SignAction.None;
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
				if (TileValid(x - 1, y) && Main.tile[x - 1, y].IsSign())
					positions.Add(Sign.GetSign(x - 1, y));
				if (TileValid(x + 1, y) && Main.tile[x + 1, y].IsSign())
					positions.Add(Sign.GetSign(x + 1, y));
				if (TileValid(x, y - 1) && Main.tile[x, y - 1].IsSign())
					positions.Add(Sign.GetSign(x, y - 1));
				if (TileValid(x, y + 1) && Main.tile[x, y + 1].IsSign())
					positions.Add(Sign.GetSign(x, y + 1));
				foreach (Point p in positions)
				{
					attached[0] = TileValid(p.X, p.Y + 2) && Main.tile[p.X, p.Y + 2].IsSolid()
						&& TileValid(p.X + 1, p.Y + 2) && Main.tile[p.X + 1, p.Y + 2].IsSolid();
					attached[1] = TileValid(p.X, p.Y - 1) && Main.tile[p.X, p.Y - 1].IsSolid()
						&& TileValid(p.X + 1, p.Y - 1) && Main.tile[p.X + 1, p.Y - 1].IsSolid();
					attached[2] = TileValid(p.X - 1, p.Y) && Main.tile[p.X - 1, p.Y].IsSolid()
						&& TileValid(p.X - 1, p.Y + 1) && Main.tile[p.X - 1, p.Y + 1].IsSolid();
					attached[3] = TileValid(p.X + 2, p.Y) && Main.tile[p.X + 2, p.Y].IsSolid()
						&& TileValid(p.X + 2, p.Y + 1) && Main.tile[p.X + 2, p.Y + 1].IsSolid();
					bool prev = Main.tile[x, y].active();
					Main.tile[x, y].active(false);
					attachedNext[0] = TileValid(p.X, p.Y + 2) && Main.tile[p.X, p.Y + 2].IsSolid()
						&& TileValid(p.X + 1, p.Y + 2) && Main.tile[p.X + 1, p.Y + 2].IsSolid();
					attachedNext[1] = TileValid(p.X, p.Y - 1) && Main.tile[p.X, p.Y - 1].IsSolid()
						&& TileValid(p.X + 1, p.Y - 1) && Main.tile[p.X + 1, p.Y - 1].IsSolid();
					attachedNext[2] = TileValid(p.X - 1, p.Y) && Main.tile[p.X - 1, p.Y].IsSolid()
						&& TileValid(p.X - 1, p.Y + 1) && Main.tile[p.X - 1, p.Y + 1].IsSolid();
					attachedNext[3] = TileValid(p.X + 2, p.Y) && Main.tile[p.X + 2, p.Y].IsSolid()
						&& TileValid(p.X + 2, p.Y + 1) && Main.tile[p.X + 2, p.Y + 1].IsSolid();
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
			using (QueryResult reader = Database.QueryReader("SELECT Account, Flags, ID, Password FROM Signs WHERE X = @0 AND Y = @1 AND WorldID = @2",
				x, y, Main.worldID))
			{
				if (reader.Read())
				{
					sign = new Sign
					{
						Account = reader.Get<string>("Account"),
						Flags = (SignFlags)reader.Get<int>("Flags"),
						HashedPassword = reader.Get<string>("Password"),
						ID = reader.Get<int>("ID")
					};
				}
			}

			var info = Infos[plr];
			var player = TShock.Players[plr];
			if (sign != null)
			{
				Console.WriteLine("IsRegion: {0}", sign.IsRegion);
				bool isFree = String.IsNullOrEmpty(sign.Account);
				bool isOwner = sign.Account == player.UserAccountName || player.Group.HasPermission("infsigns.admin.editall");
				bool isRegion = sign.IsRegion && TShock.Regions.CanBuild(x, y, player);
				if (!isFree && !isOwner && !isRegion)
				{
					if (String.IsNullOrEmpty(sign.HashedPassword))
					{
						player.SendErrorMessage("This sign is protected.");
						return;
					}
					else if (TShock.Utils.HashPassword(info.Password) != sign.HashedPassword)
					{
						player.SendErrorMessage("This sign is password protected.");
						return;
					}
					else
					{
						player.SendSuccessMessage("Sign unlocked.");
						info.Password = "";
					}
				}

				Database.Query("UPDATE Signs SET Text = @0 WHERE ID = @1", text, sign.ID);

				byte[] raw = null;
				using (var writer = new BinaryWriter(new MemoryStream()))
				{
					writer.Write((short)0);
					writer.Write((byte)47);
					writer.Write((short)(info.SignIndex ? 1 : 0));
					writer.Write((short)x);
					writer.Write((short)y);
					writer.Write(sign.Text);

					short length = (short)writer.BaseStream.Position;
					writer.BaseStream.Position = 0;
					writer.Write(length);
					raw = ((MemoryStream)writer.BaseStream).ToArray();
				}

				foreach (var info2 in Infos.Where(i => i.X == info.X && i.Y == info.Y && i != info))
				{
					raw[2] = (byte)(info2.SignIndex ? 1 : 0);
					TShock.Players[info2.Index].SendRawData(raw);
				}
			}
		}
		void PlaceSign(int x, int y, int plr)
		{
			TSPlayer player = TShock.Players[plr];
			Database.Query("INSERT INTO Signs (X, Y, Account, Text, WorldID) VALUES (@0, @1, @2, '', @3)",
				x, y, (player.IsLoggedIn && player.Group.HasPermission("infsigns.sign.protect")) ? player.UserAccountName : null, Main.worldID);
			Main.sign[999] = null;
		}
		bool TileValid(int x, int y)
		{
			return x >= 0 && y >= 0 && x < Main.maxTilesX && y < Main.maxTilesY && Main.tile[x, y] != null && Main.tile[x, y].type != 127;
		}
		bool TryKillSign(int x, int y, int plr)
		{
			Sign sign = null;
			using (QueryResult reader = Database.QueryReader("SELECT Account, ID, Text FROM Signs WHERE X = @0 AND Y = @1 AND WorldID = @2",
				x, y, Main.worldID))
			{
				if (reader.Read())
				{
					sign = new Sign
					{
						Account = reader.Get<string>("Account"),
						ID = reader.Get<int>("ID"),
						Text = reader.Get<string>("Text")
					};
				}
			}
			if (sign != null)
			{
				if (sign.Account != TShock.Players[plr].UserAccountName && sign.Account != "" &&
					!TShock.Players[plr].Group.HasPermission("infsigns.admin.editall"))
				{
					return false;
				}
				Database.Query("DELETE FROM Signs WHERE ID = @0", sign.ID);
			}
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
							Database.Query("INSERT INTO Signs (X, Y, Text, WorldID) VALUES (@0, @1, @2, @3)", s.x, s.y, s.text, Main.worldID);
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
			Infos[e.Player.Index].Action = SignAction.None;
			Infos[e.Player.Index].Password = "";
			e.Player.SendInfoMessage("Stopped selecting a sign.");
		}
		void Info(CommandArgs e)
		{
			Infos[e.Player.Index].Action = SignAction.Info;
			e.Player.SendInfoMessage("Read a sign to get its info.");
		}
		void Lock(CommandArgs e)
		{
			if (e.Parameters.Count != 1)
			{
				e.Player.SendErrorMessage("Invalid syntax! Proper syntax: /slock <password>");
				return;
			}

			Infos[e.Player.Index].Action = SignAction.SetPassword;
			Infos[e.Player.Index].Password = e.Parameters[0];
			if (e.Parameters[0].ToLower() == "remove")
				e.Player.SendInfoMessage("Read a sign to disable a password on it.");
			else
				e.Player.SendInfoMessage("Read a sign to set its password to '{0}'.", e.Parameters[0]);
		}
		void Protect(CommandArgs e)
		{
			Infos[e.Player.Index].Action = SignAction.Protect;
			e.Player.SendInfoMessage("Read a sign to protect it.");
		}
		void Prune(CommandArgs e)
		{
			Task.Factory.StartNew(() =>
			{
				int corrupted = 0;
				int empty = 0;
				var pruneID = new List<int>();
				for (int i = 0; i < Main.maxTilesX; i++)
				{
					for (int j = 0; j < Main.maxTilesY; j++)
					{
						if (Main.tile[i, j].type == TileID.Signs)
						{
							int x = i;
							int y = j;
							if (Main.tile[x, y].frameY % 36 != 0)
								y--;
							if (Main.tile[x, y].frameX % 36 != 0)
								x--;

							using (var reader = Database.QueryReader("SELECT ID, Text FROM Signs WHERE X = @0 AND Y = @1 AND WorldID = @2", x, y, Main.worldID))
							{
								if (reader.Read())
								{
									if (String.IsNullOrWhiteSpace(reader.Get<string>("Text")))
									{
										empty++;
										WorldGen.KillTile(x, y);
										TSPlayer.All.SendTileSquare(x, y, 3);
										pruneID.Add(reader.Get<int>("ID"));
									}
								}
								else
								{
									corrupted++;
									WorldGen.KillTile(x, y);
									TSPlayer.All.SendTileSquare(x, y, 3);
								}
							}
						}
					}
				}

				e.Player.SendSuccessMessage("Pruned {0} empty sign{1}.", empty, empty == 1 ? "" : "s");

				using (var reader = Database.QueryReader("SELECT ID, X, Y FROM Signs WHERE WorldID = @0", Main.worldID))
				{
					while (reader.Read())
					{
						int x = reader.Get<int>("X");
						int y = reader.Get<int>("Y");
						if (Main.tile[x, y].type != TileID.Signs)
						{
							corrupted++;
							WorldGen.KillTile(x, y);
							TSPlayer.All.SendTileSquare(x, y, 3);
							pruneID.Add(reader.Get<int>("ID"));
						}
					}
				}

				for (int i = 0; i < pruneID.Count; i++)
					Database.Query("DELETE FROM Signs WHERE ID = @0", pruneID[i]);

				e.Player.SendSuccessMessage("Pruned {0} corrupted sign{1}.", corrupted, corrupted == 1 ? "" : "s");
				if (corrupted + empty > 0)
					WorldFile.saveWorld();
			});
		}
		void Public(CommandArgs e)
		{
			Infos[e.Player.Index].Action = SignAction.TogglePublic;
			e.Player.SendInfoMessage("Read a sign to toggle its public protection.");
		}
		void Region(CommandArgs e)
		{
			Infos[e.Player.Index].Action = SignAction.ToggleRegion;
			e.Player.SendInfoMessage("Read a sign to toggle its region protection.");
		}
		void ReverseConvertSigns(CommandArgs e)
		{
			Task.Factory.StartNew(() =>
			{
				using (var reader = Database.QueryReader("SELECT COUNT(*) AS Count FROM Signs"))
				{
					reader.Read();
					if (reader.Get<int>("Count") > 1000)
					{
						e.Player.SendErrorMessage("The signs cannot be reverse-converted without losing data.");
						return;
					}
				}

				int i = 0;
				using (var reader = Database.QueryReader("SELECT Text, X, Y FROM Signs WHERE WorldID = @0", Main.worldID))
				{
					while (reader.Read())
					{
						var sign = (Main.sign[i++] = new Terraria.Sign());
						sign.text = reader.Get<string>("Text");
						sign.x = reader.Get<int>("X");
						sign.y = reader.Get<int>("Y");
					}
				}
				Database.Query("DELETE FROM Signs WHERE WorldID = @0", Main.worldID);
				e.Player.SendSuccessMessage("Reverse converted {0} signs.", i);
				if (i > 0)
					WorldFile.saveWorld();
			});
		}
		void Unlock(CommandArgs e)
		{
			if (e.Parameters.Count != 1)
			{
				e.Player.SendErrorMessage("Invalid syntax! Proper syntax: /sunlock <password>");
				return;
			}
			Infos[e.Player.Index].Password = e.Parameters[0];
			e.Player.SendInfoMessage("Read and edit a sign to unlock it.");
		}
		void Unprotect(CommandArgs e)
		{
			Infos[e.Player.Index].Action = SignAction.Unprotect;
			e.Player.SendInfoMessage("Read a sign to unprotect it.");
		}
	}
}