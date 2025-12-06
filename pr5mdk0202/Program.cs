using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Client
{
    class Program
    {
        static IPAddress ServerIpAddress;
        static int ServerPort;
        static string ClientToken;
        static DateTime ClientDateConnection;
        static Socket connectedSocket;

        static void Main(string[] args)
        {
            OnSettings();
            while (true)
            {
                SetCommand();
            }
        }

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

        public static void OnSettings()
        {
            string Path = Directory.GetCurrentDirectory() + "/.config";
            string IpAddress = "";

            if (File.Exists(Path))
            {
                using (var streamReader = new StreamReader(Path))
                {
                    IpAddress = streamReader.ReadLine();
                    ServerIpAddress = IPAddress.Parse(IpAddress);
                    ServerPort = int.Parse(streamReader.ReadLine());
                }

                WriteNormal("Server address: ");
                WriteInfo(IpAddress);

                WriteNormal("Server port: ");
                WriteInfo(ServerPort.ToString());
            }
            else
            {
                WriteNormal("Please provide the IP address of the license server: ");
                Console.ForegroundColor = ConsoleColor.Green;
                IpAddress = Console.ReadLine();
                Console.ForegroundColor = ConsoleColor.White;
                ServerIpAddress = IPAddress.Parse(IpAddress);

                Console.Write("Please specify the license server port: ");
                Console.ForegroundColor = ConsoleColor.Green;
                ServerPort = int.Parse(Console.ReadLine());
                Console.ForegroundColor = ConsoleColor.White;

                using (var streamWriter = new StreamWriter(Path))
                {
                    streamWriter.WriteLine(IpAddress);
                    streamWriter.WriteLine(ServerPort.ToString());
                }
            }

            WriteNormal("To change settings, write the command: ");
            WriteInfo("/config");
        }

        public static void SetCommand()
        {
            Console.ForegroundColor = ConsoleColor.Red;
            string Command = Console.ReadLine();
            Console.ForegroundColor = ConsoleColor.White;

            if (string.IsNullOrWhiteSpace(Command)) return;

            if (Command == "/config")
            {
                File.Delete(Directory.GetCurrentDirectory() + "/.config");
                OnSettings();
            }
            else if (Command == "/connect") ConnectServer();
            else if (Command == "/status") GetStatus();
            else if (Command == "/disconnect") DisconnectLocal();
            else if (Command == "/help") Help();
            else WriteError("Unknown command. Use /help");
        }

        public static void Help()
        {
            WriteNormal("Commands to the server:");
            WriteInfo("/config"); WriteNormal(" - set initial settings");
            WriteInfo("/connect"); WriteNormal(" - connect to the server (and keep connection)");
            WriteInfo("/status"); WriteNormal(" - request status from server (must be connected)");
            WriteInfo("/disconnect"); WriteNormal(" - close current connection");
        }

        public static void GetStatus()
        {
            if (connectedSocket == null || !connectedSocket.Connected)
            {
                WriteError("Not connected. Use /connect first.");
                return;
            }

            try
            {
                var msg = "/status";
                connectedSocket.Send(Encoding.UTF8.GetBytes(msg));

                // читаем ответ
                var buffer = new byte[4096];
                int size = connectedSocket.Receive(buffer);
                if (size <= 0)
                {
                    WriteError("Connection closed by server.");
                    CleanupConnection();
                    return;
                }

                var response = Encoding.UTF8.GetString(buffer, 0, size).Trim();
                if (response.StartsWith("/status"))
                {
                    var parts = response.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 6)
                    {
                        var connectedAt = parts[1].Replace('_', ' ');
                        var duration = parts[2];
                        var ip = parts[3];
                        var port = parts[4];
                        var code = parts[5];

                        WriteInfo("Status received:");
                        WriteNormal($"Connected at: {connectedAt}");
                        WriteNormal($"Duration (s): {duration}");
                        WriteNormal($"Server sees client IP: {ip}");
                        WriteNormal($"Server sees client port: {port}");
                        WriteNormal($"Session code: {code}");
                    }
                    else
                    {
                        WriteNormal("Status: " + response);
                    }
                }
                else if (response == "/timeout")
                {
                    WriteError("Server disconnected you due to timeout.");
                    CleanupConnection();
                }
                else if (response == "/blacklisted")
                {
                    WriteError("You are blacklisted.");
                    CleanupConnection();
                }
                else
                {
                    WriteNormal("Server response: " + response);
                }
            }
            catch (SocketException ex)
            {
                WriteError("Socket error while requesting status: " + ex.Message);
                CleanupConnection();
            }
            catch (Exception ex)
            {
                WriteError("Error while requesting status: " + ex.Message);
                CleanupConnection();
            }
        }

        public static void ConnectServer()
        {
            if (connectedSocket != null && connectedSocket.Connected)
            {
                WriteInfo("Already connected.");
                return;
            }

            WriteNormal("Login: ");
            Console.ForegroundColor = ConsoleColor.Green;
            string login = Console.ReadLine();
            Console.ForegroundColor = ConsoleColor.White;

            WriteNormal("Password: ");
            Console.ForegroundColor = ConsoleColor.Green;
            string password = Console.ReadLine();
            Console.ForegroundColor = ConsoleColor.White;

            IPEndPoint endPoint = new IPEndPoint(ServerIpAddress, ServerPort);
            Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            try
            {
                socket.Connect(endPoint);
            }
            catch (Exception ex)
            {
                WriteError("Connect error: " + ex.Message);
                return;
            }

            string msg = $"/connect {login} {password}";
            try
            {
                socket.Send(Encoding.UTF8.GetBytes(msg));
            }
            catch (Exception ex)
            {
                WriteError("Send error: " + ex.Message);
                socket.Close();
                return;
            }

            try
            {
                byte[] buffer = new byte[4096];
                int size = socket.Receive(buffer);
                if (size <= 0)
                {
                    WriteError("No response from server.");
                    socket.Close();
                    return;
                }

                string response = Encoding.UTF8.GetString(buffer, 0, size).Trim();

                if (response.StartsWith("/auth_ok"))
                {
                    var parts = response.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        ClientToken = parts[1];
                        ClientDateConnection = DateTime.Now;
                        connectedSocket = socket;
                        WriteInfo($"Authenticated. Session code: {ClientToken}");
                        WriteInfo($"Connected at: {ClientDateConnection:yyyy-MM-dd HH:mm:ss}");
                    }
                    else
                    {
                        WriteError("Malformed /auth_ok response.");
                        socket.Close();
                    }
                }
                else if (response == "/auth_fail")
                {
                    WriteError("Неверный логин или пароль.");
                    socket.Close();
                }
                else if (response == "/no_slots")
                {
                    WriteError("No available license slots on server.");
                    socket.Close();
                }
                else if (response == "/blacklisted")
                {
                    WriteError("Your IP is blacklisted on server.");
                    socket.Close();
                }
                else
                {
                    WriteNormal("Server response: " + response);
                    socket.Close();
                }
            }
            catch (SocketException ex)
            {
                WriteError("Socket error while receiving auth response: " + ex.Message);
                try { socket.Close(); } catch { }
            }
            catch (Exception ex)
            {
                WriteError("Error while receiving auth response: " + ex.Message);
                try { socket.Close(); } catch { }
            }
        }

        static void DisconnectLocal()
        {
            if (connectedSocket == null) { WriteError("No active connection."); return; }
            try
            {
                try
                {
                    connectedSocket.Send(Encoding.UTF8.GetBytes("/disconnect"));
                }
                catch { }

                connectedSocket.Close();
            }
            catch (Exception ex)
            {
                WriteError("Error while disconnecting: " + ex.Message);
            }
            finally
            {
                CleanupConnection();
                WriteInfo("Disconnected.");
            }
        }

        static void CleanupConnection()
        {
            try { connectedSocket?.Close(); } catch { } //erqew
            connectedSocket = null;
            ClientToken = null;
            ClientDateConnection = default;
        }
    }
}
