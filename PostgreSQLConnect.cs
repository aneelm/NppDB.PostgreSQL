﻿using System;
using System.Data;
using System.Data.Odbc;
using System.Data.OleDb;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Xml.Serialization;
using NppDB.Comm;

namespace NppDB.PostgreSQL
{
    [XmlRoot]
    [ConnectAttr(Id = "PostgreSQLConnect", Title = "PostgreSQL")]
    public class PostgreSQLConnect : TreeNode, IDBConnect, IRefreshable, IMenuProvider, IIconProvider, INppDBCommandClient
    {
        [XmlElement]
        public string Title { set => Text = value; get => Text; }
        [XmlElement]
        public string ServerAddress { set; get; }
        public string Account { set; get; }
        public string Port { set; get; }
        public string Database { set; get; }
        [XmlIgnore]
        public string Password { set; get; }
        private OdbcConnection _connection;

        public bool IsOpened => _connection != null && _connection.State == ConnectionState.Open;

        internal INppDBCommandHost CommandHost { get; private set; }

        public void SetCommandHost(INppDBCommandHost host)
        {
            CommandHost = host;
        }

        public void Attach()
        {
            Console.WriteLine("start attach");
            var id = CommandHost.Execute(NppDBCommandType.GetAttachedBufferID, null);
            if (id != null)
            {
                CommandHost.Execute(NppDBCommandType.NewFile, null);
            }
            id = CommandHost.Execute(NppDBCommandType.GetActivatedBufferID, null);
            CommandHost.Execute(NppDBCommandType.CreateResultView, new[] { id, this, CreateSQLExecutor() });
            Console.WriteLine("end attach");
        }

        internal ISQLExecutor CreateSQLExecutor()
        {
            return new PostgreSQLExecutor(GetConnection);
        }

        internal OdbcConnection GetConnection()
        {
            OdbcConnectionStringBuilder builder = GetConnectionStringBuilder();
            builder["Pwd"] = Password;
            return new OdbcConnection(builder.ConnectionString);
        }

        public bool CheckLogin()
        {
            var dlg = new frmPostgreSQLConnect();
            if (dlg.ShowDialog() != DialogResult.OK) return false;
            Password = dlg.Password;
            Account = dlg.Username;
            Port = dlg.Port;
            ServerAddress = dlg.Server;
            Database = dlg.Database;

            Console.WriteLine(Password);
            Console.WriteLine(Account);
            Console.WriteLine(Port);
            Console.WriteLine(ServerAddress);
            Console.WriteLine(Database);
            return true;
        }

        public void Connect()
        {
            if (_connection == null) _connection = new OdbcConnection();

            var curConnStrBuilder = GetConnectionStringBuilder();
            if (String.IsNullOrEmpty(_connection.ConnectionString) ||
                _connection.ConnectionString.Remove(_connection.ConnectionString.Length - 1, 1) != curConnStrBuilder.ConnectionString)
            {
                curConnStrBuilder["Pwd"] = Password;
                _connection.ConnectionString = curConnStrBuilder.ConnectionString;
            }
            if (_connection.State == ConnectionState.Open) return;
            try
            {
                _connection.Open();
                Console.WriteLine("connected?");
            }
            catch (Exception ex)
            {
                throw new ApplicationException("connect fail", ex);
            }
        }

        internal OdbcConnectionStringBuilder GetConnectionStringBuilder()
        {
            var builder = new OdbcConnectionStringBuilder
            {
                Driver = "PostgreSQL Unicode(x64)",
            };
            builder["Uid"] = Account;
            builder["Server"] = ServerAddress;
            builder["Port"] = Port;
            builder["Database"] = Database;
            Console.WriteLine(builder.ConnectionString);
            return builder;
        }

        public void ConnectAndAttach()
        {
            if (IsOpened || !CheckLogin()) return;
            try
            {
                Connect();
                Attach();
                Refresh();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message + (ex.InnerException != null ? " : " + ex.InnerException.Message : ""));
            }
        }

        public void Disconnect()
        {
            if (_connection == null || _connection.State == ConnectionState.Closed) return;

            _connection.Close();
        }

        public string GetDefaultTitle()
        {
            return string.IsNullOrEmpty(Database) ? "" : Database;
        }

        public Bitmap GetIcon()
        {
            return Properties.Resources.PostgreSQL;
        }

        public ContextMenuStrip GetMenu()
        {
            var menuList = new ContextMenuStrip { ShowImageMargin = false };
            var connect = this;
            var host = CommandHost;
            if (host != null)
            {
                menuList.Items.Add(new ToolStripButton("Open", null, (s, e) =>
                {
                    host.Execute(NppDBCommandType.NewFile, null);
                    var id = host.Execute(NppDBCommandType.GetActivatedBufferID, null);
                    host.Execute(NppDBCommandType.CreateResultView, new[] { id, connect, CreateSQLExecutor() });
                }));
                if (host.Execute(NppDBCommandType.GetAttachedBufferID, null) == null)
                {
                    menuList.Items.Add(new ToolStripButton("Attach", null, (s, e) =>
                    {
                        var id = host.Execute(NppDBCommandType.GetActivatedBufferID, null);
                        host.Execute(NppDBCommandType.CreateResultView, new[] { id, connect, CreateSQLExecutor() });
                    }));
                }
                else
                {
                    menuList.Items.Add(new ToolStripButton("Detach", null, (s, e) =>
                    {
                        host.Execute(NppDBCommandType.DestroyResultView, null);
                    }));
                }
                menuList.Items.Add(new ToolStripSeparator());
            }
            menuList.Items.Add(new ToolStripButton("Refresh", null, (s, e) => { Refresh(); }));
            return menuList;
        }

        public void Refresh()
        {
            Console.WriteLine("start Refresh");
            using (var conn = GetConnection())
            {
                TreeView.Cursor = Cursors.WaitCursor;
                TreeView.Enabled = false;
                try
                {
                    Console.WriteLine("Refresh conn.open");
                    conn.Open();
                    Console.WriteLine("Refresh conn.opened");
                    Nodes.Clear();
                    //string currentNSQuery = "show search_path;";
                    //string currentNS = "public";
                    //using (OdbcCommand command = new OdbcCommand(currentNSQuery, conn))
                    //{
                    //    using (OdbcDataReader reader = command.ExecuteReader())
                    //    {
                    //        while (reader.Read())
                    //        {
                    //            currentNS = reader["search_path"].ToString();
                    //            Console.WriteLine("Search Path: " + currentNS);
                    //        }
                    //    }
                    //}
                    string query = "SELECT nspname FROM pg_namespace order by nspname;";
                    using (OdbcCommand command = new OdbcCommand(query, conn))
                    {
                        using (OdbcDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string schemaName = reader["nspname"].ToString();
                                //if (currentNS.ToLower().Contains(schemaName.ToLower()))
                                //{
                                //    schemaName += " (default)";
                                //}
                                var db = new PostgreSQLSchema { Text = schemaName, Schema = schemaName };
                                Nodes.Add(db);
                                Console.WriteLine("Schema Name: " + schemaName);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    Nodes.Clear();
                }
                finally
                {
                    TreeView.Enabled = true;
                    TreeView.Cursor = null;
                }
            }
            Console.WriteLine("end Refresh");
        }

        public void Reset()
        {
            Title = ""; ServerAddress = ""; Account = ""; Password = "";
            Disconnect();
            _connection = null;
        }
    }
}
