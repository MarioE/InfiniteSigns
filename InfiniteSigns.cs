using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Community.CsharpSqlite.SQLiteClient;
using Hooks;
using MySql.Data.MySqlClient;
using Terraria;
using TShockAPI;
using TShockAPI.DB;

namespace InfiniteSigns
{
    [APIVersion(1, 12)]
    public class InfiniteSigns : TerrariaPlugin
    {
        public static SignAction[] Action = new SignAction[256];
        public override string Author
        {
            get { return "MarioE"; }
        }
        public static IDbConnection Database;
        public override string Description
        {
            get { return "Allows for infinite signs, and supports all sign control commands."; }
        }
        public override string Name
        {
            get { return "InfiniteSigns"; }
        }
        public static bool[] SignNum = new bool[256];
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
                            string text = Encoding.UTF8.GetString(e.Msg.readBuffer, e.Index + 10, e.Length - 10);
                            ThreadPool.QueueUserWorkItem(ModSignCallback,
                                new SignArgs { plr = TShock.Players[e.Msg.whoAmI], loc = new Point(X, Y), text = text });
                            e.Handled = true;
                        }
                        break;
                    case PacketTypes.SignRead:
                        {
                            int X = BitConverter.ToInt32(e.Msg.readBuffer, e.Index);
                            int Y = BitConverter.ToInt32(e.Msg.readBuffer, e.Index + 4);
                            ThreadPool.QueueUserWorkItem(GetSignCallback,
                                new SignArgs { plr = TShock.Players[e.Msg.whoAmI], loc = new Point(X, Y) });
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
                                    ThreadPool.QueueUserWorkItem(KillSignCallback,
                                        new SignArgs { plr = TShock.Players[e.Msg.whoAmI], loc = new Point(X, Y) });
                                    e.Handled = true;
                                }
                            }
                            else if (e.Msg.readBuffer[e.Index] == 1 && (e.Msg.readBuffer[e.Index + 9] == 55 || e.Msg.readBuffer[e.Index + 9] == 85))
                            {
                                if (TShock.Regions.CanBuild(X, Y, TShock.Players[e.Msg.whoAmI]))
                                {
                                    WorldGen.PlaceSign(X, Y, e.Msg.readBuffer[e.Index + 9]);
                                    if (Main.tile[X, Y].frameY != 0)
                                    {
                                        Y--;
                                    }
                                    if (Main.tile[X, Y].frameX % 36 != 0)
                                    {
                                        X--;
                                    }
                                    ThreadPool.QueueUserWorkItem(PlaceSignCallback,
                                        new SignArgs { plr = TShock.Players[e.Msg.whoAmI], loc = new Point(X, Y) });
                                    TSPlayer.All.SendTileSquare(X, Y, 3);
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
            Commands.ChatCommands.Add(new Command("maintenance", ConvertSigns, "convsigns"));
            Commands.ChatCommands.Add(new Command("protectsign", Deselect, "scset"));
            Commands.ChatCommands.Add(new Command("showsigninfo", Info, "sinfo"));
            Commands.ChatCommands.Add(new Command("protectsign", Protect, "sset"));
            Commands.ChatCommands.Add(new Command("protectsign", Unprotect, "sunset"));

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

        void ConvertCallback(object t)
        {
            Database.QueryReader("DELETE FROM Signs WHERE WorldID = @0", Main.worldID);
            int converted = 0;
            foreach (Terraria.Sign s in Main.sign)
            {
                if (s != null)
                {
                    Database.Query("INSERT INTO Signs (X, Y, Account, Text, WorldID) VALUES (@0, @1, '', @2, @3)",
                        s.x, s.y, s.text, Main.worldID);
                    converted++;
                }
            }
            ((SignArgs)t).plr.SendMessage("Converted " + converted + " signs.");
        }
        void GetSignCallback(object t)
        {
            SignArgs s = (SignArgs)t;

            using (QueryResult query = Database.QueryReader("SELECT Account, Text FROM Signs WHERE X = @0 AND Y = @1 AND WorldID = @2",
                s.loc.X, s.loc.Y, Main.worldID))
            {
                while (query.Read())
                {
                    Sign sign = new Sign
                    {
                        account = query.Get<string>("Account"),
                        text = query.Get<string>("Text")
                    };
                    switch (Action[s.plr.Index])
                    {
                        case SignAction.INFO:
                            s.plr.SendMessage(string.Format("X: {0} Y: {1} Account: {2}",
                                s.loc.X, s.loc.Y, sign.account == "" ? "N/A" : sign.account), Color.Yellow);
                            break;
                        case SignAction.PROTECT:
                            if (sign.account != "")
                            {
                                s.plr.SendMessage("This sign is already protected.", Color.Red);
                                break;
                            }
                            Database.Query("UPDATE Signs SET Account = @0 WHERE X = @1 AND Y = @2 AND WorldID = @3",
                                s.plr.UserAccountName, s.loc.X, s.loc.Y, Main.worldID);
                            s.plr.SendMessage("This sign is now protected.");
                            break;
                        case SignAction.UNPROTECT:
                            if (sign.account == "")
                            {
                                s.plr.SendMessage("This sign is not protected.", Color.Red);
                                break;
                            }
                            if (sign.account != s.plr.UserAccountName &&
                                !s.plr.Group.HasPermission("removesignprotection"))
                            {
                                s.plr.SendMessage("This sign is not yours.", Color.Red);
                                break;
                            }
                            Database.Query("UPDATE Signs SET Account = '' WHERE X = @0 AND Y = @1 AND WorldID = @2",
                                s.loc.X, s.loc.Y, Main.worldID);
                            s.plr.SendMessage("This sign is now unprotected.");
                            break;
                        default:
                            string text = query.Get<string>("Text");
                            if (text.Length != 0)
                            {
                                text = text.Substring(0, text.Length - 1);
                            }
                            byte[] utf8 = Encoding.UTF8.GetBytes(text);
                            byte[] raw = new byte[15 + utf8.Length];
                            Buffer.BlockCopy(BitConverter.GetBytes(utf8.Length + 11), 0, raw, 0, 4);
                            if (SignNum[s.plr.Index])
                            {
                                raw[5] = 1;
                            }
                            raw[4] = 47;
                            Buffer.BlockCopy(BitConverter.GetBytes(s.loc.X), 0, raw, 7, 4);
                            Buffer.BlockCopy(BitConverter.GetBytes(s.loc.Y), 0, raw, 11, 4);
                            Buffer.BlockCopy(utf8, 0, raw, 15, utf8.Length);
                            s.plr.SendRawData(raw);
                            SignNum[s.plr.Index] = !SignNum[s.plr.Index];
                            break;
                    }
                    Action[s.plr.Index] = SignAction.NONE;
                }
            }
        }
        void KillSignCallback(object t)
        {
            SignArgs s = (SignArgs)t;
            bool[] attached = new bool[4];
            bool[] attachedNext = new bool[4];
            List<Point> positions = new List<Point>();
            if (Main.tile[s.loc.X, s.loc.Y].IsSign())
            {
                positions.Add(Sign.GetSign(s.loc.X, s.loc.Y));
                if (TryKillSign(positions[0].X, positions[0].Y, s.plr))
                {
                    WorldGen.KillTile(s.loc.X, s.loc.Y);
                    TSPlayer.All.SendTileSquare(s.loc.X, s.loc.Y, 3);
                }
                else
                {
                    s.plr.SendMessage("This sign is protected.", Color.Red);
                    s.plr.SendTileSquare(s.loc.X, s.loc.Y, 5);
                }
            }
            else
            {
                if (Main.tile.Valid(s.loc.X - 1, s.loc.Y) && Main.tile[s.loc.X - 1, s.loc.Y].IsSign())
                {
                    positions.Add(Sign.GetSign(s.loc.X - 1, s.loc.Y));
                }
                if (Main.tile.Valid(s.loc.X + 1, s.loc.Y) && Main.tile[s.loc.X + 1, s.loc.Y].IsSign())
                {
                    positions.Add(Sign.GetSign(s.loc.X + 1, s.loc.Y));
                }
                if (Main.tile.Valid(s.loc.X, s.loc.Y - 1) && Main.tile[s.loc.X, s.loc.Y - 1].IsSign())
                {
                    positions.Add(Sign.GetSign(s.loc.X, s.loc.Y - 1));
                }
                if (Main.tile.Valid(s.loc.X, s.loc.Y + 1) && Main.tile[s.loc.X, s.loc.Y + 1].IsSign())
                {
                    positions.Add(Sign.GetSign(s.loc.X , s.loc.Y + 1));
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
                    bool prev = Main.tile[s.loc.X, s.loc.Y].active;
                    Main.tile[s.loc.X, s.loc.Y].active = false;
                    attachedNext[0] = Main.tile.Valid(p.X, p.Y + 2) && Main.tile[p.X, p.Y + 2].IsSolid()
                        && Main.tile.Valid(p.X + 1, p.Y + 2) && Main.tile[p.X + 1, p.Y + 2].IsSolid();
                    attachedNext[1] = Main.tile.Valid(p.X, p.Y - 1) && Main.tile[p.X, p.Y - 1].IsSolid()
                        && Main.tile.Valid(p.X + 1, p.Y - 1) && Main.tile[p.X + 1, p.Y - 1].IsSolid();
                    attachedNext[2] = Main.tile.Valid(p.X - 1, p.Y) && Main.tile[p.X - 1, p.Y].IsSolid()
                        && Main.tile.Valid(p.X - 1, p.Y + 1) && Main.tile[p.X - 1, p.Y + 1].IsSolid();
                    attachedNext[3] = Main.tile.Valid(p.X + 2, p.Y) && Main.tile[p.X + 2, p.Y].IsSolid()
                        && Main.tile.Valid(p.X + 2, p.Y + 1) && Main.tile[p.X + 2, p.Y + 1].IsSolid();
                    Main.tile[s.loc.X, s.loc.Y].active = prev;
                    if (attached.Count(b => b) > 1 || attached.Count(b => b) == attachedNext.Count(b => b))
                    {
                        continue;
                    }
                    if (TryKillSign(p.X, p.Y, s.plr))
                    {
                        WorldGen.KillTile(p.X, p.Y);
                        TSPlayer.All.SendTileSquare(p.X, p.Y, 3);
                    }
                    else
                    {
                        s.plr.SendMessage("This sign is protected.", Color.Red);
                        s.plr.SendTileSquare(s.loc.X, s.loc.Y, 5);
                        killTile = false;
                    }
                }
                if (killTile && Main.tile[s.loc.X, s.loc.Y] != null)
                {
                    WorldGen.KillTile(s.loc.X, s.loc.Y);
                    TSPlayer.All.SendTileSquare(s.loc.X, s.loc.Y, 1);
                }
            }
        }
        void ModSignCallback(object t)
        {
            SignArgs s = (SignArgs)t;
            using (QueryResult query = Database.QueryReader("SELECT Account FROM Signs WHERE X = @0 AND Y = @1 AND WorldID = @2",
                s.loc.X, s.loc.Y, Main.worldID))
            {
                while (query.Read())
                {
                    string account = query.Get<string>("Account");
                    if (account != s.plr.UserAccountName && account != "" && !s.plr.Group.HasPermission("editallsigns"))
                    {
                        s.plr.SendMessage("This sign is protected.", Color.Red);
                        return;
                    }
                    Database.Query("UPDATE Signs SET Text = @0 WHERE X = @1 AND Y = @2 AND WorldID = @3",
                        s.text, s.loc.X, s.loc.Y, Main.worldID);
                }
            }
        }
        void PlaceSignCallback(object t)
        {
            SignArgs s = (SignArgs)t;
            Database.Query("INSERT INTO Signs (X, Y, Account, Text, WorldID) VALUES (@0, @1, @2, '', @3)",
                s.loc.X, s.loc.Y, s.plr.IsLoggedIn ? s.plr.UserAccountName : "", Main.worldID);
            Main.sign[999] = null;
        }
        bool TryKillSign(int X, int Y, TSPlayer plr)
        {
            using (QueryResult query = Database.QueryReader("SELECT Account FROM Signs WHERE X = @0 AND Y = @1 AND WorldID = @2",
                X, Y, Main.worldID))
            {
                while (query.Read())
                {
                    string account = query.Get<string>("Account");
                    if (account != plr.UserAccountName && account != "")
                    {
                        return false;
                    }
                    Database.Query("DELETE FROM Signs WHERE X = @0 AND Y = @1 AND WorldID = @2", X, Y, Main.worldID);
                    return true;
                }
                return true;
            }
        }

        void ConvertSigns(CommandArgs e)
        {
            e.Player.SendMessage("Converting all signs into the new storage format; this may take a while.");
            ThreadPool.QueueUserWorkItem(ConvertCallback, new SignArgs { plr = e.Player });
        }
        void Deselect(CommandArgs e)
        {
            Action[e.Player.Index] = SignAction.NONE;
            e.Player.SendMessage("Stopped selecting a sign.");
        }
        void Info(CommandArgs e)
        {
            Action[e.Player.Index] = SignAction.INFO;
            e.Player.SendMessage("Read a sign to get its info.");
        }
        void Protect(CommandArgs e)
        {
            Action[e.Player.Index] = SignAction.PROTECT;
            e.Player.SendMessage("Read a sign to protect it.");
        }
        void Unprotect(CommandArgs e)
        {
            Action[e.Player.Index] = SignAction.UNPROTECT;
            e.Player.SendMessage("Read a sign to unprotect it.");
        }

        private class SignArgs
        {
            public Point loc;
            public TSPlayer plr;
            public string text;
        }
    }
}