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
        public static Vector2[] SignPosition = new Vector2[256];
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
                            string text = Encoding.UTF8.GetString(e.Msg.readBuffer, e.Index + 10, e.Length - 10);
                            ThreadPool.QueueUserWorkItem(ModSignCallback,
                                new SignArgs { plr = TShock.Players[e.Msg.whoAmI], text = text });
                            e.Handled = true;
                        }
                        break;
                    case PacketTypes.SignRead:
                        {
                            int X = BitConverter.ToInt32(e.Msg.readBuffer, e.Index);
                            int Y = BitConverter.ToInt32(e.Msg.readBuffer, e.Index + 4);
                            SignPosition[e.Msg.whoAmI] = new Vector2(X, Y);
                            ThreadPool.QueueUserWorkItem(GetSignCallback,
                                new SignArgs { plr = TShock.Players[e.Msg.whoAmI], loc = new Vector2(X, Y) });
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
                                if (CheckSign(X, Y, ref e))
                                {
                                    return;
                                }
                                CheckSign(X - 1, Y, ref e);
                                CheckSign(X + 1, Y, ref e);
                                CheckSign(X, Y - 1, ref e);
                                CheckSign(X, Y + 1, ref e);
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
                                        new SignArgs { plr = TShock.Players[e.Msg.whoAmI], loc = new Vector2(X, Y) });
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
            Commands.ChatCommands.Add(new Command("protectsign", Deselect, "sdeselect"));
            Commands.ChatCommands.Add(new Command("showsigninfo", Info, "sinfo"));
            Commands.ChatCommands.Add(new Command("protectsign", Protect, "sprotect"));
            Commands.ChatCommands.Add(new Command("protectsign", Unprotect, "sunprotect"));

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
            SignPosition[index] = Vector2.Zero;
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
            for (int i = 0; i < 1000; i++)
            {
                Main.sign[i] = null;
            }
            ((SignArgs)t).plr.SendMessage("Converted " + converted + " signs.");
        }
        void GetSignCallback(object t)
        {
            SignArgs s = (SignArgs)t;

            using (QueryResult query = Database.QueryReader("SELECT Account, Text FROM Signs WHERE X = @0 AND Y = @1 AND WorldID = @2",
                (int)s.loc.X, (int)s.loc.Y, Main.worldID))
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
                                (int)s.loc.X, (int)s.loc.Y, sign.account == "" ? "N/A" : sign.account), Color.Yellow);
                            break;
                        case SignAction.PROTECT:
                            if (sign.account != "")
                            {
                                s.plr.SendMessage("This sign is already protected.", Color.Red);
                                break;
                            }
                            Database.Query("UPDATE Signs SET Account = @0 WHERE X = @1 AND Y = @2 AND WorldID = @3",
                                s.plr.UserAccountName, (int)s.loc.X, (int)s.loc.Y, Main.worldID);
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
                                s.plr.SendMessage("This sign is not yours.");
                                break;
                            }
                            Database.Query("UPDATE Signs SET Account = '' WHERE X = @0 AND Y = @1 AND WorldID = @2",
                                (int)s.loc.X, (int)s.loc.Y, Main.worldID);
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
                            raw[4] = 47;
                            Buffer.BlockCopy(BitConverter.GetBytes((int)s.loc.X), 0, raw, 7, 4);
                            Buffer.BlockCopy(BitConverter.GetBytes((int)s.loc.Y), 0, raw, 11, 4);
                            Buffer.BlockCopy(utf8, 0, raw, 15, utf8.Length);
                            s.plr.SendRawData(raw);
                            SignPosition[s.plr.Index] = s.loc;
                            break;
                    }
                    Action[s.plr.Index] = SignAction.NONE;
                }
            }
        }
        void KillSignCallback(object t)
        {
            SignArgs s = (SignArgs)t;
            using (QueryResult query = Database.QueryReader("SELECT Account FROM Signs WHERE X = @0 AND Y = @1 AND WorldID = @2",
                (int)s.loc.X, (int)s.loc.Y, Main.worldID))
            {
                while (query.Read())
                {
                    string account = query.Get<string>("Account");
                    if (account != s.plr.UserAccountName && account != "")
                    {
                        s.plr.SendMessage("This sign is protected.", Color.Red);
                        s.plr.SendTileSquare((int)s.loc.X, (int)s.loc.Y, 5);
                        return;
                    }
                    Database.Query("DELETE FROM Signs WHERE X = @0 AND Y = @1 AND WorldID = @2", (int)s.loc.X, (int)s.loc.Y, Main.worldID);
                    WorldGen.KillTile((int)s.loc.X, (int)s.loc.Y);
                    TSPlayer.All.SendTileSquare((int)s.loc.X, (int)s.loc.Y, 3);
                    return;
                }
            }
        }
        void ModSignCallback(object t)
        {
            SignArgs s = (SignArgs)t;
            using (QueryResult query = Database.QueryReader("SELECT Account FROM Signs WHERE X = @0 AND Y = @1 AND WorldID = @2",
                (int)SignPosition[s.plr.Index].X, (int)SignPosition[s.plr.Index].Y, Main.worldID))
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
                        s.text, (int)SignPosition[s.plr.Index].X, (int)SignPosition[s.plr.Index].Y, Main.worldID);
                    return;
                }
            }
        }
        void PlaceSignCallback(object t)
        {
            SignArgs s = (SignArgs)t;
            Database.Query("INSERT INTO Signs (X, Y, Account, Text, WorldID) VALUES (@0, @1, @2, '', @3)",
                (int)s.loc.X, (int)s.loc.Y, s.plr.IsLoggedIn ? s.plr.UserAccountName : "", Main.worldID);
            Main.sign[999] = null;
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

        bool CheckSign(int X, int Y, ref GetDataEventArgs e)
        {
            if (X >= 0 && Y >= 0 && X < Main.maxTilesX && Y < Main.maxTilesY && (Main.tile[X, Y].type == 55 || Main.tile[X, Y].type == 85))
            {
                if (Main.tile[X, Y].frameY != 0)
                {
                    Y--;
                }
                if (Main.tile[X, Y].frameX % 36 != 0)
                {
                    X--;
                }
                ThreadPool.QueueUserWorkItem(KillSignCallback,
                    new SignArgs { plr = TShock.Players[e.Msg.whoAmI], loc = new Vector2(X, Y) });
                e.Handled = true;
                return true;
            }
            return false;
        }

        private class SignArgs
        {
            public Vector2 loc;
            public TSPlayer plr;
            public string text;
        }
    }
}