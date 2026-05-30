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
        private static ConcurrentQueue<Player> queuePractice = new();
        private static ConcurrentQueue<Player> queue1V1 = new();
        private static ConcurrentQueue<Player> queue5V5 = new();
        private static ConcurrentDictionary<int, Room> roomList = [];
        private static Dictionary<GameMode, ConcurrentQueue<Player>> queueDic = [];
        private static Dictionary<GameMode, int> numDic = [];

        static void Main()
        {
            queueDic.Add(GameMode.ModePractice, queuePractice);
            queueDic.Add(GameMode.Mode1v1, queue1V1);
            queueDic.Add(GameMode.Mode5v5, queue5V5);
            numDic.Add(GameMode.ModePractice, 1);
            numDic.Add(GameMode.Mode1v1, 2);
            numDic.Add(GameMode.Mode5v5, 10);

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
                        switch (msg.type)
                        {
                            case HallMessageType.Match:
                                Match? match = JsonConvert.DeserializeObject<Match>(msg.info);
                                if (match != null && match.uid > 0 && players.TryGetValue(match.uid, out Player? player))
                                    queueDic[match.mode].Enqueue(player);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Message process error: {ex.Message}");
                    }
                }
            }
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
                SendMessage(client, new HallMessage(HallMessageType.Connect, JsonConvert.SerializeObject(connect)));
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

                    string allData = buffer.ToString();
                    int closeBrace;
                    while ((closeBrace = allData.IndexOf('}')) >= 0)
                    {
                        string json = allData[..(closeBrace + 1)];
                        allData = allData[(closeBrace + 1)..];
                        buffer.Clear();
                        buffer.Append(allData);

                        HallMessage? msg = JsonConvert.DeserializeObject<HallMessage>(json);
                        if (msg != null && !string.IsNullOrEmpty(msg.info))
                        {
                            messageList.Enqueue(msg);
                        }
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

        public static void SendMessage(Socket socket, HallMessage message)
        {
            try
            {
                byte[] buffer = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message));
                int sent = 0;
                while (sent < buffer.Length)
                    sent += socket.Send(buffer, sent, buffer.Length - sent, SocketFlags.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Send error: {ex.Message}");
            }
        }

        private static void HandleDisconnect(int uid)
        {
            if (players.TryRemove(uid, out Player? player))
            {
                player.socket.Shutdown(SocketShutdown.Both);
                player.socket.Close();

                foreach (var queue in queueDic.Values)
                {
                    var remaining = new ConcurrentQueue<Player>();
                    while (queue.TryDequeue(out Player? p))
                    {
                        if (p!.uid != uid)
                            remaining.Enqueue(p);
                    }
                    while (remaining.TryDequeue(out Player? p))
                        queue.Enqueue(p!);
                }

                Console.WriteLine($"A player was disconnected, uid: {uid}");
            }
        }

        private static void QueueThread()
        {
            while (true)
            {
                bool matched = false;
                foreach (GameMode mode in queueDic.Keys)
                    if (queueDic[mode].Count >= numDic[mode])
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
            Room room = CreateRoom();
            for (int i = 0; i < numDic[mode]; i++)
            {
                if (queueDic[mode].TryDequeue(out Player? player))
                {
                    room.Join(player);
                    var start = new Start(room.port, 0);
                    SendMessage(player.socket, new HallMessage(HallMessageType.Start, JsonConvert.SerializeObject(start)));
                }
            }
            room.StartRoom();
        }

        static Room CreateRoom()
        {
            int rid = Interlocked.Increment(ref roomId);
            Room room = new(port + rid);
            roomList.TryAdd(rid, room);
            return room;
        }
    }

    public enum GameMode
    {
        ModePractice, Mode1v1, Mode5v5
    }

    public enum HallMessageType
    {
        Connect, Match, Start
    }

    /// <summary>
    /// 消息包装类，与客户端保持一致
    /// </summary>
    public class HallMessage
    {
        public HallMessageType type;
        public string info;

        public HallMessage(HallMessageType type, string info)
        {
            this.type = type;
            this.info = info;
        }
    }

    public class Connect
    {
        public int uid;
        public Connect() { }
        public Connect(int uid)
        {
            this.uid = uid;
        }
    }

    public class Match
    {
        public int uid;
        public GameMode mode;
        public Match() { }
        public Match(int uid, GameMode mode)
        {
            this.uid = uid;
            this.mode = mode;
        }
    }

    public class Start
    {
        public int port;
        public int map;
        public Start(int port, int map)
        {
            this.port = port;
            this.map = map;
        }
    }

    public class Player
    {
        public int uid;
        public Socket socket;
        public Player(int uid, Socket socket)
        {
            this.uid = uid;
            this.socket = socket;
        }
    }

}