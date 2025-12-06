using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;

namespace LicenseServer
{
    class Program
    {
        static string SettingsPath = "server.config";
        static IPAddress ListenAddress = IPAddress.Any;
        static int ListenPort = 9000;
        static int MaxLicensedClients = 5;
        static int ClientTimeoutSeconds = 60 * 5;
        static int CodeConfirmSeconds = 30;
        static string MySqlConnectionString = "server=127.0.0.1;port=3307;user=root;password=;database=license_manager;";

        class ClientInfo
        {
            public TcpClient TcpClient;
            public string UniqueCode;
            public DateTime ConnectedAt;
            public IPEndPoint EndPoint;
            public CancellationTokenSource Cancellation;
        }

        static ConcurrentDictionary<string, ClientInfo> ConnectedClients = new ConcurrentDictionary<string, ClientInfo>();

        static void WriteInfo(string s)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(s);
            Console.ForegroundColor = old;
        }
        static void WriteError(string s)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(s);
            Console.ForegroundColor = old;
        }
        static void WriteNormal(string s)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(s);
            Console.ForegroundColor = old;
        }

        static async Task Main(string[] args)
        {
            ReadInitialSettings();

            var listener = new TcpListener(ListenAddress, ListenPort);
            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                WriteError($"Failed to start listener: {ex.Message}");
                return;
            }

            WriteInfo($"[Server] Listening on {ListenAddress}:{ListenPort}. Max clients = {MaxLicensedClients}. Timeout = {ClientTimeoutSeconds}s");

            _ = Task.Run(() => AcceptLoop(listener));
            _ = Task.Run(() => TimeoutMonitorLoop());

            AdminCommandLoop();

            await Task.CompletedTask;
        }

        static void ReadInitialSettings()
        {
            if (System.IO.File.Exists(SettingsPath))
            {
                var lines = System.IO.File.ReadAllLines(SettingsPath);
                if (lines.Length >= 5)
                {
                    ListenAddress = string.IsNullOrWhiteSpace(lines[0]) ? IPAddress.Any : IPAddress.Parse(lines[0]);
                    ListenPort = int.TryParse(lines[1], out var p) ? p : 9000;
                    MaxLicensedClients = int.TryParse(lines[2], out var m) ? m : 5;
                    ClientTimeoutSeconds = int.TryParse(lines[3], out var t) ? t : 300;
                    MySqlConnectionString = string.IsNullOrWhiteSpace(lines[4]) ? MySqlConnectionString : lines[4];
                }
                WriteInfo("Settings loaded from file.");
                return;
            }

            WriteNormal("=== License Server initial setup ===");
            Console.Write("Server IP to listen (enter for ANY): ");
            var ip = Console.ReadLine();
            ListenAddress = string.IsNullOrWhiteSpace(ip) ? IPAddress.Any : IPAddress.Parse(ip.Trim());

            Console.Write("Server port (default 9000): ");
            var portLine = Console.ReadLine();
            ListenPort = int.TryParse(portLine, out var p2) ? p2 : 9000;

            Console.Write("Max licensed clients (default 5): ");
            var maxLine = Console.ReadLine();
            MaxLicensedClients = int.TryParse(maxLine, out var m2) ? m2 : 5;

            Console.Write("Client disconnect timeout seconds (default 300): ");
            var tline = Console.ReadLine();
            ClientTimeoutSeconds = int.TryParse(tline, out var t2) ? t2 : 300;

            Console.Write("MySQL connection string (enter to use default): ");
            var cs = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(cs)) MySqlConnectionString = cs.Trim();

            System.IO.File.WriteAllLines(SettingsPath, new[]
            {
                ListenAddress.ToString(),
                ListenPort.ToString(),
                MaxLicensedClients.ToString(),
                ClientTimeoutSeconds.ToString(),
                MySqlConnectionString
            });

            WriteInfo("Settings saved to file.");
        }

        static async Task AcceptLoop(TcpListener listener)
        {
            while (true)
            {
                TcpClient tcp = null;
                try
                {
                    tcp = await listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleTcpClient(tcp));
                }
                catch (Exception ex)
                {
                    WriteError("[AcceptLoop] Error accepting client: " + ex.Message);
                    try { tcp?.Close(); } catch { }
                }
            }
        }

        static void AdminCommandLoop()
        {
            WriteInfo("- - - -- - - - -- - - - -- - - - - - - -- -");
            WriteNormal("/help                 - show this help");
            WriteNormal("/config               - change config");
            WriteNormal("/status               - status");
            WriteNormal("/disconnect <id>      - ds");
            WriteNormal("/blacklist add <ip>   - ban");
            WriteNormal("/blacklist remove <ip>- unban");
            while (true)
            {
                Console.Write("$ ");
                var line = Console.ReadLine();
                if (line == null) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                var cmd = parts[0].ToLowerInvariant();

                if (cmd == "/help")
                {
                    WriteInfo("- - - -- - - - -- - - - -- - - - - - - -- -");
                    WriteNormal("/help                 - show this help");
                    WriteNormal("/config               - change config");
                    WriteNormal("/status               - status");
                    WriteNormal("/disconnect <id>      - ds");
                    WriteNormal("/blacklist add <ip>   - ban");
                    WriteNormal("/blacklist remove <ip>- unban");
                }
                else if (cmd == "/config")
                {
                    WriteNormal($"Current: MaxClients={MaxLicensedClients}, TimeoutSeconds={ClientTimeoutSeconds}");
                    Console.Write("New Max licensed clients (enter to keep): ");
                    var maxLine = Console.ReadLine();
                    if (int.TryParse(maxLine, out var m)) MaxLicensedClients = m;

                    Console.Write("New client timeout seconds (enter to keep): ");
                    var tline = Console.ReadLine();
                    if (int.TryParse(tline, out var t)) ClientTimeoutSeconds = t;


                    System.IO.File.WriteAllLines(SettingsPath, new[]
                    {
                        ListenAddress.ToString(),
                        ListenPort.ToString(),
                        MaxLicensedClients.ToString(),
                        ClientTimeoutSeconds.ToString(),
                        MySqlConnectionString
                    });

                    WriteInfo("Config updated and saved to file.");
                }
                else if (cmd == "/status")
                {
                    WriteInfo($"Connected clients: {ConnectedClients.Count} / {MaxLicensedClients}");
                    PrintClients();
                }
                else if (cmd == "/disconnect")
                {
                    if (parts.Length < 2)
                    {
                        WriteError("Usage: /disconnect <session_id>");
                        continue;
                    }
                    var code = parts[1];
                    if (ConnectedClients.TryRemove(code, out var info))
                    {
                        try { info.TcpClient.Close(); } catch { }
                        info.Cancellation?.Cancel();
                        WriteInfo($"[Admin] Disconnected {code}");
                    }
                    else
                    {
                        WriteError("[Admin] Not found");
                    }
                }
                else if (cmd == "/blacklist" || cmd == "blacklist")
                {
                    if (parts.Length < 3)
                    {
                        WriteError("Usage: /blacklist add <ip> | /blacklist remove <ip>");
                        continue;
                    }
                    var sub = parts[1].ToLowerInvariant();
                    var ip = parts[2];
                    var code = "localhost";
                    if (sub == "add")
                    {
                        AddToBlacklist(ip).Wait();
                    }
                    else if (sub == "remove")
                    {
                        RemoveFromBlacklist(ip).Wait();
                    }
                    else
                    {
                        WriteError("Unknown subcommand for blacklist");
                    }
                }
                else if (cmd == "/quit" || cmd == "quit")
                {
                    WriteInfo("Shutting down...");
                    Environment.Exit(0);
                }
                else
                {
                    WriteError("Unknown command. Type /help for list.");
                }
            }
        }

        static void PrintClients()
        {
            if (ConnectedClients.IsEmpty)
            {
                WriteNormal("No connected clients.");
                return;
            }

            foreach (var kv in ConnectedClients)
            {
                var code = kv.Key;
                var info = kv.Value;
                var dur = DateTime.Now - info.ConnectedAt;
                WriteNormal($"Code={code} IP={info.EndPoint.Address}:{info.EndPoint.Port} ConnectedAt={info.ConnectedAt:yyyy-MM-dd HH:mm:ss} Duration={dur.TotalSeconds:F0}s");
            }
        }

        static async Task<bool> IsBlacklisted(string ip)
        {
            try
            {
                using var conn = new MySqlConnection(MySqlConnectionString);
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(1) FROM blacklist WHERE ip=@ip";
                cmd.Parameters.AddWithValue("@ip", ip);
                var res = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                return res > 0;
            }
            catch (Exception ex)
            {
                WriteError("[DB] Blacklist check error: " + ex.Message);
                return false;
            }
        }

        static async Task AddToBlacklist(string ip)
        {
            try
            {
                using var conn = new MySqlConnection(MySqlConnectionString);
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT IGNORE INTO blacklist (ip, reason) VALUES (@ip,@r)";
                cmd.Parameters.AddWithValue("@ip", ip);
                cmd.Parameters.AddWithValue("@r", "added by admin");
                await cmd.ExecuteNonQueryAsync();
                WriteInfo($"[Admin] Added {ip} to blacklist");
            }
            catch (Exception ex) { WriteError("[DB] Add blacklist err: " + ex.Message); }
        }

        static async Task RemoveFromBlacklist(string ip)
        {
            try
            {
                using var conn = new MySqlConnection(MySqlConnectionString);
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM blacklist WHERE ip=@ip";
                cmd.Parameters.AddWithValue("@ip", ip);
                await cmd.ExecuteNonQueryAsync();
                WriteInfo($"[Admin] Removed {ip} from blacklist");
            }
            catch (Exception ex) { WriteError("[DB] Remove blacklist err: " + ex.Message); }
        }

        static async Task<bool> ValidateUser(string login, string password)
        {
            try
            {
                using var conn = new MySqlConnection(MySqlConnectionString);
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(1) FROM users WHERE login=@login AND password=@password";
                cmd.Parameters.AddWithValue("@login", login);
                cmd.Parameters.AddWithValue("@password", password);
                var res = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                return res > 0;
            }
            catch (Exception ex)
            {
                WriteError("[DB] Auth error: " + ex.Message);
                return false;
            }
        }

        static async Task HandleTcpClient(TcpClient tcp)
        {
            var remote = tcp.Client.RemoteEndPoint as IPEndPoint;
            var ip = remote?.Address.ToString() ?? "unknown";
            WriteNormal($"[Conn] Incoming connection from {remote}");

            if (await IsBlacklisted(ip))
            {
                await SendAndClose(tcp, "/blacklisted");
                WriteError($"[Conn] Rejected {ip} (blacklist)");
                return;
            }

            NetworkStream ns = tcp.GetStream();
            var buffer = new byte[4096];
            int read = 0;
            try
            {
                read = await ns.ReadAsync(buffer, 0, buffer.Length);
            }
            catch (Exception ex)
            {
                WriteError("[Conn] Read error: " + ex.Message);
                tcp.Close();
                return;
            }

            if (read <= 0)
            {
                tcp.Close();
                return;
            }

            var msg = Encoding.UTF8.GetString(buffer, 0, read).Trim();
            WriteNormal($"[Recv] From {ip}: {msg}");

            var parts = msg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || parts[0] != "/connect")
            {
                await SendAndClose(tcp, "/protocol_error");
                return;
            }

            var login = parts[1];
            var password = parts[2];

            var authOk = await ValidateUser(login, password);
            if (!authOk)
            {
                await SendAndClose(tcp, "/auth_fail");
                WriteError($"[Auth] Login failed for {ip} login={login}");
                return;
            }

            if (ConnectedClients.Count >= MaxLicensedClients)
            {
                await SendAndClose(tcp, "/no_slots");
                WriteError("[Auth] No slots available, rejected " + ip);
                return;
            }

            var unique = Guid.NewGuid().ToString();
            var info = new ClientInfo
            {
                TcpClient = tcp,
                UniqueCode = unique,
                ConnectedAt = DateTime.Now,
                EndPoint = remote,
                Cancellation = new CancellationTokenSource()
            };

            if (!ConnectedClients.TryAdd(unique, info))
            {
                await SendAndClose(tcp, "/server_error");
                return;
            }

            try
            {
                var okMsg = "/auth_ok " + unique;
                var data = Encoding.UTF8.GetBytes(okMsg);
                await ns.WriteAsync(data, 0, data.Length);
            }
            catch (Exception ex)
            {
                WriteError("[Send] Failed to send auth_ok: " + ex.Message);
                ConnectedClients.TryRemove(unique, out _);
                tcp.Close();
                return;
            }

            WriteInfo($"[Auth] {login} @ {ip} authenticated. Code={unique}");
            await ClientReceiveLoop(unique, info);
        }

        static async Task ClientReceiveLoop(string unique, ClientInfo info)
        {
            var tcp = info.TcpClient;
            var ns = tcp.GetStream();
            var buffer = new byte[4096];
            try
            {
                while (tcp.Connected && !info.Cancellation.IsCancellationRequested)
                {
                    if (!ns.DataAvailable)
                    {
                        await Task.Delay(1000);
                        continue;
                    }

                    int read = 0;
                    try
                    {
                        read = await ns.ReadAsync(buffer, 0, buffer.Length, info.Cancellation.Token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        WriteError($"[Recv] read error from {unique}: " + ex.Message);
                        break;
                    }

                    if (read <= 0) break;
                    var msg = Encoding.UTF8.GetString(buffer, 0, read).Trim();
                    if (msg.StartsWith("/ping"))
                    {
                        var resp = "/pong";
                        var bytes = Encoding.UTF8.GetBytes(resp);
                        await ns.WriteAsync(bytes, 0, bytes.Length);
                    }
                    else if (msg.StartsWith("/status"))
                    {
                        var dur = DateTime.Now - info.ConnectedAt;
                        var resp = $"/status {info.ConnectedAt:yyyy-MM-dd_HH:mm:ss} {(int)dur.TotalSeconds} {info.EndPoint.Address} {info.EndPoint.Port} {info.UniqueCode}";
                        var bytes = Encoding.UTF8.GetBytes(resp);
                        await ns.WriteAsync(bytes, 0, bytes.Length);
                    }
                    else if (msg.StartsWith("/disconnect"))
                    {
                        break;
                    }
                }
            }
            finally
            {
                ConnectedClients.TryRemove(unique, out _);
                try { tcp.Close(); } catch { }
                WriteNormal($"[Conn] Client {unique} disconnected/cleanup");
            }
        }

        static async Task SendAndClose(TcpClient tcp, string message)
        {
            try
            {
                var ns = tcp.GetStream();
                var data = Encoding.UTF8.GetBytes(message);
                await ns.WriteAsync(data, 0, data.Length);
            }
            catch { }
            try { tcp.Close(); } catch { }
        }

        static async Task TimeoutMonitorLoop()
        {
            while (true)
            {
                var now = DateTime.Now;
                var toDisconnect = new List<string>();
                foreach (var kv in ConnectedClients)
                {
                    var code = kv.Key;
                    var info = kv.Value;
                    var dur = now - info.ConnectedAt;
                    if (dur.TotalSeconds > ClientTimeoutSeconds)
                    {
                        toDisconnect.Add(code);
                    }
                }

                foreach (var code in toDisconnect)
                {
                    if (ConnectedClients.TryRemove(code, out var info))
                    {
                        try
                        {
                            var ns = info.TcpClient.GetStream();
                            var msg = "/timeout";
                            var data = Encoding.UTF8.GetBytes(msg);
                            await ns.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
                        }
                        catch { }
                        try { info.TcpClient.Close(); } catch { }
                        info.Cancellation?.Cancel();
                        WriteError($"[Timeout] Disconnected {code} due to timeout");
                    }
                }

                await Task.Delay(5000);
            }
        }
    }
}
