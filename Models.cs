using System.Collections.Concurrent;
using System.Net.Sockets;

namespace MainServer;

public enum GameMode
{
    ModePractice, Mode1v1, Mode3v3
}

public enum HallMessageType
{
    Connect, Match, Start
}

public enum RoomStatus
{
    Waiting, Playing
}

public class QueueInfo
{
    public ConcurrentQueue<Player> Players { get; } = new();
    public int Required { get; }
    public QueueInfo(int required) { Required = required; }
}

public class HallMessage
{
    public HallMessageType type;
    public string? info;

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
    public Connect(int uid) { this.uid = uid; }
}

public class Match
{
    public int uid;
    public GameMode mode;
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
    public int roomId = -1;

    public Player(int uid, Socket socket)
    {
        this.uid = uid;
        this.socket = socket;
    }
}
