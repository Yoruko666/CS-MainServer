using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;

namespace MainServer
{
    class Program
    {
        private static readonly string ip = "0.0.0.0";
        private static readonly int port = 25000;
        private static readonly int capacity = 1000;

        private static ConcurrentQueue<HallMessage> messageList = new();
        private static ConcurrentDictionary<int, Player> players = new();

        private static int roomId = 1;
        private static int nextUid = 1000;
        private static ConcurrentDictionary<int, Room> roomList = [];
        private static Dictionary<GameMode, QueueInfo> queues = new()
        {
            [GameMode.ModePractice] = new(1),
            [GameMode.Mode1v1] = new(2),
            [GameMode.Mode3v3] = new(6),
        };
        private static Dictionary<HallMessageType, Action<HallMessage>> messageHandlers = [];

        static void Main()
        {
            RegisterHandlers();

            Thread startServer = new(StartServer);
            startServer.Start();

            Thread queueThread = new(QueueThread);
            queueThread.Start();

            while (true)
            {
                if (messageList.TryDequeue(out HallMessage? msg))
                {
                    try
                    {
                        if (messageHandlers.TryGetValue(msg.type, out var handler))
                            handler(msg);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Message process error: {ex.Message}");
                    }
                }
            }
        }

        static void RegisterHandlers()
        {
            messageHandlers[HallMessageType.Match] = msg =>
            {
                Match? match = JsonConvert.DeserializeObject<Match>(msg.info!);
                if (match != null && match.uid > 0 && players.TryGetValue(match.uid, out Player? player))
                {
                    Console.WriteLine($"Player {match.uid} match {match.mode}");
                    queues[match.mode].Players.Enqueue(player);
                }
            };
        }

        static void StartServer()
        {
            IPEndPoint pos = new(IPAddress.Parse(ip), port);
            Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(pos);
            socket.Listen(capacity);
            Console.WriteLine("Main server is working");
            while (true)
            {
                Socket client = socket.Accept();

                int uid = Interlocked.Increment(ref nextUid);

                Console.WriteLine($"A player was connected, uid: {uid}");
                players[uid] = new Player(uid, client);
                Thread listen = new(Receive);
                listen.Start(uid);

                Connect connect = new(uid);
                Send(client, new HallMessage(HallMessageType.Connect, JsonConvert.SerializeObject(connect)));
            }
        }

        static void Receive(object? obj)
        {
            int uid = (int)obj!;
            Socket socket = players[uid].socket;
            byte[] data = new byte[1024];
            StringBuilder buffer = new();
            while (true)
            {
                try
                {
                    int len = socket.Receive(data);
                    if (len <= 0)
                    {
                        HandleDisconnect(uid);
                        return;
                    }
                    buffer.Append(Encoding.UTF8.GetString(data, 0, len));

                    foreach (string json in ExtractJsonObjects(buffer))
                    {
                        HallMessage? msg = JsonConvert.DeserializeObject<HallMessage>(json);
                        if (msg != null && !string.IsNullOrEmpty(msg.info))
                            messageList.Enqueue(msg);
                    }
                }
                catch (JsonException)
                {
                    continue;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Receive error from {uid}: {ex.Message}");
                    HandleDisconnect(uid);
                    return;
                }
            }
        }

        /// <summary>
        /// 从缓冲区中提取完整 JSON 对象（按 {} 深度匹配，正确处理字符串内的 {}）
        /// </summary>
        static List<string> ExtractJsonObjects(StringBuilder buffer)
        {
            string data = buffer.ToString();
            int pos = 0;
            var results = new List<string>();

            while (pos < data.Length)
            {
                while (pos < data.Length && char.IsWhiteSpace(data[pos])) pos++;
                if (pos >= data.Length) break;
                if (data[pos] != '{') { pos++; continue; }

                int depth = 1;
                bool inString = false;
                bool escaped = false;
                int start = pos;
                pos++;

                while (pos < data.Length && depth > 0)
                {
                    char c = data[pos];
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = !inString;
                    else if (!inString)
                    {
                        if (c == '{') depth++;
                        else if (c == '}') depth--;
                    }
                    pos++;
                }

                if (depth == 0)
                {
                    results.Add(data[start..pos]);
                    buffer.Remove(0, pos);
                    data = buffer.ToString();
                    pos = 0;
                }
                else
                {
                    break;
                }
            }

            return results;
        }

        public static bool Send(Socket socket, HallMessage message)
        {
            try
            {
                byte[] buffer = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message));
                int sent = 0;
                while (sent < buffer.Length)
                    sent += socket.Send(buffer, sent, buffer.Length - sent, SocketFlags.None);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Send error: {ex.Message}");
                return false;
            }
        }

        private static void HandleDisconnect(int uid)
        {
            if (players.TryRemove(uid, out Player? player))
            {
                player.socket.Shutdown(SocketShutdown.Both);
                player.socket.Close();

                // 从匹配队列中移除
                foreach (var qi in queues.Values)
                {
                    var q = qi.Players;
                    var remaining = new ConcurrentQueue<Player>();
                    while (q.TryDequeue(out Player? p))
                    {
                        if (p!.uid != uid)
                            remaining.Enqueue(p);
                    }
                    while (remaining.TryDequeue(out Player? p))
                        q.Enqueue(p!);
                }

                // 如果在房间内，空出位置
                if (player.roomId > 0 && roomList.TryGetValue(player.roomId, out Room? room))
                {
                    room.Remove(player);
                    Console.WriteLine($"Player {uid} removed from room {player.roomId}, remaining: {room.playerList.Count}");
                }

                Console.WriteLine($"A player was disconnected, uid: {uid}");
            }
        }

        private static void QueueThread()
        {
            while (true)
            {
                bool matched = false;
                foreach (GameMode mode in queues.Keys)
                    if (queues[mode].Players.Count >= queues[mode].Required)
                    {
                        StartRoom(mode);
                        matched = true;
                    }
                if (!matched)
                    Thread.Sleep(10);
            }
        }

        private static void StartRoom(GameMode mode)
        {
            var (room, rid) = CreateRoom();
            for (int i = 0; i < queues[mode].Required; i++)
            {
                if (queues[mode].Players.TryDequeue(out Player? player))
                {
                    var start = new Start(room.port, 0);
                    if (Send(player.socket, new HallMessage(HallMessageType.Start, JsonConvert.SerializeObject(start))))
                    {
                        player!.roomId = rid;
                        room.Join(player);
                    }
                    else
                    {
                        HandleDisconnect(player!.uid);
                    }
                }
            }
            if (room.playerList.Count > 0)
                room.StartRoom();
        }

        static (Room, int) CreateRoom()
        {
            int rid = Interlocked.Increment(ref roomId);
            Room room = new(port + rid, rid);
            roomList.TryAdd(rid, room);
            return (room, rid);
        }
    }
}